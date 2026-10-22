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
    private bool _visible;

    public WindowShadow(IntPtr owner)
    {
        _owner = owner;
    }

    /// <summary>
    /// 把影子窗口贴到主窗口后面。只有 <paramref name="visible"/> 且尺寸有效时才显示；
    /// 最大化/最小化时由调用方传 false。
    /// </summary>
    public void Update(int x, int y, int width, int height, bool visible)
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

        var margin = Math.Max(10, (int)Math.Round(18 * scale));

        if (width != _appWidth || height != _appHeight || margin != _margin)
        {
            _appWidth = width;
            _appHeight = height;
            _margin = margin;
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
        var sigmaSoft = 10 * scale;
        var sigmaTight = 2.5 * scale;
        var offsetY = (int)Math.Round(2 * scale);

        // 影子相对主窗口下移 2px：上沿更轻，下沿稍实。
        var left = margin;
        var top = margin + offsetY;
        var right = margin + appWidth;
        var bottom = margin + appHeight + offsetY;

        for (var py = 0; py < height; py++)
        {
            var middleRow = py >= margin && py < margin + appHeight;

            if (middleRow)
            {
                for (var px = 0; px < margin; px++)
                {
                    Write(px, py);
                }

                for (var px = margin + appWidth; px < width; px++)
                {
                    Write(px, py);
                }
            }
            else
            {
                for (var px = 0; px < width; px++)
                {
                    Write(px, py);
                }
            }
        }

        void Write(int px, int py)
        {
            var dx = Math.Max(0, Math.Max(left - px, px - (right - 1)));
            var dy = Math.Max(0, Math.Max(top - py, py - (bottom - 1)));
            var distance = Math.Sqrt((dx * dx) + (dy * dy));

            if (distance <= 0)
            {
                return;
            }

            var soft = distance / sigmaSoft;
            var tight = distance / sigmaTight;
            var alpha = (18 * Math.Exp(-(soft * soft))) + (16 * Math.Exp(-(tight * tight)));
            var value = (int)Math.Round(alpha);

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
