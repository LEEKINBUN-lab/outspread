using System.Runtime.InteropServices;

namespace Outspread.Board;

/// <summary>
/// 无框窗口的系统投影在这台机器上被关了（SPI_GETDROPSHADOW=False），所以自己画一层：
/// 一个 click-through 的分层窗口贴在主窗口后面，只在四周留一圈很轻的软影。
/// </summary>
/// <remarks>
/// 它不参与输入、不进 Alt+Tab、不激活；位置与大小跟着主窗口走，最大化/最小化时藏起来。
/// 影子只在窗口外的 margin 里画，窗口本身盖住中心，所以不会把板面压暗。
/// </remarks>
internal sealed class WindowShadow : IDisposable
{
    private const string ClassName = "OutspreadShadowWindow";

    private const uint WsPopup = 0x80000000;
    private const uint WsExLayered = 0x00080000;
    private const uint WsExTransparent = 0x00000020;
    private const uint WsExNoActivate = 0x08000000;
    private const uint WsExToolWindow = 0x00000080;

    private const uint SwpNoSize = 0x0001;
    private const uint SwpNoMove = 0x0002;
    private const uint SwpNoActivate = 0x0010;
    private const uint SwpShowWindow = 0x0040;

    private const int SwHide = 0;
    private const int SwShowNoActivate = 4;

    private const uint WmNcHitTest = 0x0084;
    private const uint WmEraseBackground = 0x0014;
    private const uint WmMouseActivate = 0x0021;
    private const int HtTransparent = -1;
    private const int MaNoActivate = 3;

    private const uint UlwAlpha = 0x00000002;
    private const byte AcSrcOver = 0;
    private const byte AcSrcAlpha = 1;
    private const uint DibRgbColors = 0;
    private const uint BiRgb = 0;

    private delegate IntPtr WndProcDelegate(IntPtr hWnd, uint message, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Point
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Size
    {
        public int CX;
        public int CY;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BlendFunction
    {
        public byte BlendOp;
        public byte BlendFlags;
        public byte SourceConstantAlpha;
        public byte AlphaFormat;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BitmapInfoHeader
    {
        public uint Size;
        public int Width;
        public int Height;
        public ushort Planes;
        public ushort BitCount;
        public uint Compression;
        public uint SizeImage;
        public int XPelsPerMeter;
        public int YPelsPerMeter;
        public uint ClrUsed;
        public uint ClrImportant;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BitmapInfo
    {
        public BitmapInfoHeader Header;
        public uint Colors;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WindowClass
    {
        public uint Size;
        public uint Style;
        public WndProcDelegate WindowProc;
        public int ClsExtra;
        public int WndExtra;
        public IntPtr Instance;
        public IntPtr Icon;
        public IntPtr Cursor;
        public IntPtr Background;
        public string? MenuName;
        public string ClassName;
        public IntPtr SmallIcon;
    }

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern ushort RegisterClassEx(ref WindowClass windowClass);

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr CreateWindowEx(
        uint exStyle,
        string className,
        string windowName,
        uint style,
        int x,
        int y,
        int width,
        int height,
        IntPtr parent,
        IntPtr menu,
        IntPtr instance,
        IntPtr param);

    [DllImport("user32.dll")]
    private static extern IntPtr DefWindowProc(IntPtr hWnd, uint message, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool DestroyWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hWnd, int command);

    [DllImport("user32.dll")]
    private static extern bool SetWindowPos(
        IntPtr hWnd, IntPtr after, int x, int y, int width, int height, uint flags);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
    private static extern IntPtr SetWindowLongPtr(IntPtr hWnd, int index, IntPtr value);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr GetModuleHandle(string? moduleName);

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern IntPtr GetDC(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern int ReleaseDC(IntPtr hWnd, IntPtr hdc);

    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateCompatibleDC(IntPtr hdc);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteDC(IntPtr hdc);

    [DllImport("gdi32.dll")]
    private static extern IntPtr SelectObject(IntPtr hdc, IntPtr obj);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteObject(IntPtr obj);

    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateDIBSection(
        IntPtr hdc, ref BitmapInfo info, uint usage, out IntPtr bits, IntPtr section, uint offset);

    [DllImport("user32.dll")]
    private static extern bool UpdateLayeredWindow(
        IntPtr hWnd,
        IntPtr dstDc,
        ref Point dst,
        ref Size size,
        IntPtr srcDc,
        ref Point src,
        uint colorKey,
        ref BlendFunction blend,
        uint flags);

    private static WndProcDelegate? _windowProc;
    private static bool _classRegistered;

    private readonly IntPtr _owner;
    private IntPtr _hwnd;
    private int _appWidth;
    private int _appHeight;
    private int _margin;
    private bool _compact;
    private bool _visible;

    /// <summary>整扇窗口那档的留边（3σ 之外，位图边界不会出现被裁断的台阶）。</summary>
    internal const double WideMargin = 46;

    /// <summary>一张纸那档的留边：够放下紧影就行，再宽就成了一圈框。</summary>
    internal const double CompactMargin = 22;

    /// <summary>一层影的形状：σ（设备 px）、峰值透明度、往下落的偏移。</summary>
    internal readonly record struct ShadowLayer(double Sigma, double Alpha, double Drop);

    /// <summary>
    /// 影的配方。渲染与自检读同一份——"纸边那圈会不会读成描边"是可算的，不该只靠眼睛。
    /// </summary>
    /// <remarks>
    /// 三层：接触影把纸按在桌面上，环境影给一圈很淡的雾，主投影才负责"浮起来"。
    /// 原来那枚 σ=2.5 的紧影在距边 1px 处仍有约 12% 黑，而它只从右、下两处漏出来
    /// （左上被主体盖住）——他截图里那条黑细线就是它。这里没有任何 0 模糊的分量：
    /// 每层都是"半个峰值 × 高斯尾巴"，最外圈随距离衰减到 0，留边也放大到 3σ 之外。
    ///
    /// 小窗那档整体收一档并压暗：σ14 的雾铺在一张纸上就是一圈 70px 的灰环，
    /// 加上纸边 16% 的接触浓度，读起来不是"浮起来"而是"套了个框"（他 11-18 的第三张截图）。
    /// </remarks>
    internal static ShadowLayer[] LayersFor(double scale, bool compact) => compact
        ?
        [
            new(1.2 * scale, 0.04, 0.5 * scale),
            new(3.5 * scale, 0.07, 1.5 * scale),
            new(8.0 * scale, 0.12, 3.0 * scale),
        ]
        :
        [
            new(1.5 * scale, 0.05, 1.0 * scale),
            new(8.0 * scale, 0.09, 2.0 * scale),
            new(14.0 * scale, 0.18, 5.0 * scale),
        ];

    /// <summary>剪影往里收多少：影的最黑处落在纸底下，贴着纸边读到的才是"淡淡的托举"。</summary>
    internal static double TuckPx(double scale, bool compact)
        => compact ? 2.5 * scale : Math.Max(1, 1.1 * scale);

    /// <summary>某一层在距剪影 <paramref name="distance"/> 处贡献的透明度。</summary>
    private static double AlphaOf(ShadowLayer layer, double distance)
    {
        var q = distance / layer.Sigma;

        return layer.Alpha * 0.5 * Math.Exp(-(q * q));
    }

    /// <summary>
    /// 距剪影 <paramref name="distance"/>（设备 px）处的累计透明度。自检拿它当尺：
    /// 纸边那一档要够淡（不然读成描边），留边外沿要归零（不然位图边界上会留一条台阶）。
    /// </summary>
    internal static double AlphaAt(double distance, double scale, bool compact)
    {
        var alpha = 0d;

        foreach (var layer in LayersFor(scale, compact))
        {
            if (distance > 0)
            {
                alpha += AlphaOf(layer, distance);
            }
        }

        return alpha;
    }



    public WindowShadow(IntPtr owner)
    {
        _owner = owner;
    }

    /// <summary>
    /// 把影子窗口贴到主窗口后面。只有 <paramref name="visible"/> 且尺寸有效时才显示；
    /// 最大化/最小化时由调用方传 false。
    /// </summary>
    /// <param name="compact">
    /// 小窗用（浮牌那一档）：它只有一张便签那么大，按整扇窗口那份"留 46px、最外层 σ14"的影去铺，
    /// 影会比纸还宽，读起来像套了一圈框（他 11-18 的第三张截图）。小窗要的是紧贴纸边的一点点悬浮感。
    /// </param>
    public void Update(int x, int y, int width, int height, bool visible, bool compact = false)
    {
        if (!visible || width <= 0 || height <= 0 || _owner == IntPtr.Zero)
        {
            Hide();

            return;
        }

        EnsureWindow();

        if (_hwnd == IntPtr.Zero)
        {
            return;
        }

        var scale = GetDpiForWindow(_owner) / 96.0;

        if (scale <= 0)
        {
            scale = 1;
        }

        var margin = Math.Max(12, (int)Math.Round((compact ? CompactMargin : WideMargin) * scale));

        if (width != _appWidth || height != _appHeight || margin != _margin || compact != _compact)
        {
            _appWidth = width;
            _appHeight = height;
            _margin = margin;
            _compact = compact;
            Render(x, y, width, height, margin, scale);
        }
        else
        {
            UpdateLayeredPosition(x, y);
        }

        // 放在 owner 后面（不是上面）；同时确保显示、不激活、不改大小。
        SetWindowPos(
            _hwnd, _owner, 0, 0, 0, 0,
            SwpNoMove | SwpNoSize | SwpNoActivate | SwpShowWindow);

        _visible = true;
    }

    public void Hide()
    {
        if (_hwnd != IntPtr.Zero && _visible)
        {
            ShowWindow(_hwnd, SwHide);
            _visible = false;
        }
    }

    public void Dispose()
    {
        if (_hwnd != IntPtr.Zero)
        {
            DestroyWindow(_hwnd);
            _hwnd = IntPtr.Zero;
        }

        _visible = false;
    }

    private void EnsureWindow()
    {
        if (_hwnd != IntPtr.Zero)
        {
            return;
        }

        if (!_classRegistered)
        {
            _windowProc = ShadowWndProc;

            var windowClass = new WindowClass
            {
                Size = (uint)Marshal.SizeOf<WindowClass>(),
                WindowProc = _windowProc,
                Instance = GetModuleHandle(null),
                ClassName = ClassName,
            };

            RegisterClassEx(ref windowClass);
            _classRegistered = true;
        }

        _hwnd = CreateWindowEx(
            WsExLayered | WsExTransparent | WsExNoActivate | WsExToolWindow,
            ClassName,
            string.Empty,
            WsPopup,
            0, 0, 0, 0,
            IntPtr.Zero,
            IntPtr.Zero,
            GetModuleHandle(null),
            IntPtr.Zero);

        if (_hwnd != IntPtr.Zero)
        {
            // Owned tool window: MainWindowHandle/任务栏/Alt+Tab 仍认主窗口，不认这一层影。
            SetWindowLongPtr(_hwnd, -8 /* GWLP_HWNDPARENT */, _owner);
        }
    }

    private static IntPtr ShadowWndProc(IntPtr hWnd, uint message, IntPtr wParam, IntPtr lParam)
    {
        return message switch
        {
            WmNcHitTest => new IntPtr(HtTransparent),
            WmMouseActivate => new IntPtr(MaNoActivate),
            WmEraseBackground => new IntPtr(1),
            _ => DefWindowProc(hWnd, message, wParam, lParam),
        };
    }

    private void Render(int x, int y, int appWidth, int appHeight, int margin, double scale)
    {
        var width = appWidth + (margin * 2);
        var height = appHeight + (margin * 2);

        // 只算窗口外那一圈。中心被主窗口整个盖住，byte[] 默认 0 就够。
        var pixels = new byte[width * height * 4];

        // 圆角跟随卡片（原型 9px），影往里收一点点——边缘那一条因此不是最高点。
        // 小窗那档的纸是直角的（板上的格子就是 CornerRadius(0)），影跟着直角才不错位。
        var radius = _compact ? Math.Max(1, 3 * scale) : Math.Max(2, (9 * scale) - 1);

        var tuck = TuckPx(scale, _compact);
        var layers = LayersFor(scale, _compact);

        var left = margin + tuck;
        var right = margin + appWidth - tuck;
        var top = margin + tuck;
        var bottom = margin + appHeight - tuck;

        for (var py = 0; py < height; py++)
        {
            var insideRow = py >= margin && py < margin + appHeight;
            var rowStart = insideRow ? margin : 0;
            var rowEnd = insideRow ? margin + appWidth : width;

            for (var px = 0; px < width; px++)
            {
                // 被主窗口盖住的那一段一个字节都不写：它本来就不会显示。
                if (px >= rowStart && px < rowEnd)
                {
                    continue;
                }

                Write(px, py);
            }
        }

        void Write(int px, int py)
        {
            var alpha = 0d;

            foreach (var layer in layers)
            {
                var distance = RoundedDistance(
                    px, py,
                    left, top + layer.Drop,
                    right, bottom + layer.Drop,
                    radius);

                if (distance <= 0)
                {
                    continue;
                }

                alpha += AlphaOf(layer, distance);
            }

            var value = (int)Math.Round(alpha * 255);

            if (value <= 0)
            {
                return;
            }

            if (value > 255)
            {
                value = 255;
            }

            pixels[((py * width) + px) * 4 + 3] = (byte)value;
        }

        var screenDc = GetDC(IntPtr.Zero);
        var memoryDc = CreateCompatibleDC(screenDc);
        var info = new BitmapInfo
        {
            Header = new BitmapInfoHeader
            {
                Size = (uint)Marshal.SizeOf<BitmapInfoHeader>(),
                Width = width,
                Height = -height,
                Planes = 1,
                BitCount = 32,
                Compression = BiRgb,
            },
        };

        var dib = CreateDIBSection(screenDc, ref info, DibRgbColors, out var bits, IntPtr.Zero, 0);

        if (dib == IntPtr.Zero)
        {
            DeleteDC(memoryDc);
            ReleaseDC(IntPtr.Zero, screenDc);

            return;
        }

        var previous = SelectObject(memoryDc, dib);

        try
        {
            Marshal.Copy(pixels, 0, bits, pixels.Length);

            var destination = new Point { X = x - margin, Y = y - margin };
            var size = new Size { CX = width, CY = height };
            var source = new Point();
            var blend = new BlendFunction
            {
                BlendOp = AcSrcOver,
                SourceConstantAlpha = 255,
                AlphaFormat = AcSrcAlpha,
            };

            UpdateLayeredWindow(
                _hwnd, screenDc, ref destination, ref size, memoryDc, ref source, 0, ref blend, UlwAlpha);
        }
        finally
        {
            SelectObject(memoryDc, previous);
            DeleteObject(dib);
            DeleteDC(memoryDc);
            ReleaseDC(IntPtr.Zero, screenDc);
        }
    }

    /// <summary>
    /// 点到圆角矩形边界的有符号距离：内部为负、边界为 0、外部为正。像素按中心取（+0.5），
    /// 否则整条影会偏半个像素、在 1x 屏上看起来像边缘没对齐。
    /// </summary>
    /// <remarks>
    /// 圆角矩形的标准距离场：先在两个轴上各自减掉「半长 - 半径」，得到点相对内缩矩形的位移；
    /// 落在内缩矩形内的部分取 0 并在轴向上取负（负值代表"在最外圈以内多远"），于是四个圆角
    /// 只剩角上那一段是斜边，正圆心的距离减去半径即为真实距离。不这么写而直接对整条边用
    /// 轴向距离的话，角上会留下一个方角——那正是要消掉的那种硬边。
    /// </remarks>
    private static double RoundedDistance(
        int px, int py, double left, double top, double right, double bottom, double radius)
    {
        var cx = (left + right) * 0.5;
        var cy = (top + bottom) * 0.5;
        var halfWidth = ((right - left) * 0.5) - radius;
        var halfHeight = ((bottom - top) * 0.5) - radius;

        if (halfWidth < 0)
        {
            halfWidth = 0;
        }

        if (halfHeight < 0)
        {
            halfHeight = 0;
        }

        var dx = Math.Abs(px + 0.5 - cx) - halfWidth;
        var dy = Math.Abs(py + 0.5 - cy) - halfHeight;

        var ax = Math.Max(dx, 0);
        var ay = Math.Max(dy, 0);

        return Math.Sqrt((ax * ax) + (ay * ay)) + Math.Min(Math.Max(dx, dy), 0) - radius;
    }

    private void UpdateLayeredPosition(int x, int y)
    {
        if (_hwnd == IntPtr.Zero)
        {
            return;
        }

        SetWindowPos(
            _hwnd, _owner, x - _margin, y - _margin, 0, 0,
            SwpNoSize | SwpNoActivate | SwpShowWindow);
    }
}
