using System.Runtime.InteropServices;
using Microsoft.UI;
using Microsoft.UI.Input;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Outspread.Core;
using Windows.Graphics;
using Windows.UI;
using WinRT.Interop;

namespace Outspread.Board;

/// <summary>
/// 窗口外壳：不再用系统那一圈 1px 暗框和系统标题按钮，改成无框窗口 + 自绘三枚 caption 按钮。
/// </summary>
/// <remarks>
/// <para>
/// 2026-10-22 他对着四边框截图说"四边都有黑线很奇怪……最上方的黑线外面还有一条白色范围，更丑"。
/// 这台 Win10 19045 上两条 DWM 路径都试过：<c>DWMWA_BORDER_COLOR</c> 返回
/// <c>0x80070057</c>（不支持），<c>DwmExtendFrameIntoClientArea(-1)</c> 返回成功但边框/白带
/// 一个像素没动。所以真正的路只有把系统框整条去掉：<see cref="OverlappedPresenter"/> 的
/// <c>SetBorderAndTitleBar(false,false)</c>，加上三枚自己的按钮。
/// </para>
/// <para>
/// 拖窗口/双击标题条仍然由 <c>SetTitleBar(TitleBarHost)</c> 的 Caption 区承担；标签、加号、
/// 三枚按钮通过 <c>InputNonClientPointerSource.Passthrough</c> 让回客户区（见 BoardTabs 的
/// <c>UpdateTabPassthroughRegions</c>）。实测无框后标题条拖动与右下角缩放都还在。
/// </para>
/// </remarks>
public sealed partial class MainWindow
{
    private const string MinimizeGlyph = "\uE921";
    private const string MaximizeGlyph = "\uE922";
    private const string RestoreGlyph = "\uE923";
    private const string CloseGlyph = "\uE8BB";

    private OverlappedPresenter? Overlapped => AppWindow.Presenter as OverlappedPresenter;

    private const uint WmNcCalcSize = 0x0083;
    private const uint WmNcHitTest = 0x0084;
    private const int GwlWndProc = -4;
    private const int HtClient = 1;
    private const int HtCaption = 2;
    private const int HtLeft = 10;
    private const int HtRight = 11;
    private const int HtTop = 12;
    private const int HtTopLeft = 13;
    private const int HtTopRight = 14;
    private const int HtBottom = 15;
    private const int HtBottomLeft = 16;
    private const int HtBottomRight = 17;

    private delegate IntPtr WindowProcDelegate(IntPtr hWnd, uint message, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW", SetLastError = true)]
    private static extern IntPtr SetWindowLongPtr(IntPtr hWnd, int index, IntPtr value);

    [DllImport("user32.dll", EntryPoint = "CallWindowProcW")]
    private static extern IntPtr CallWindowProc(IntPtr previous, IntPtr hWnd, uint message, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr hWnd, out NativeRect rect);

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool SetWindowPos(
        IntPtr hWnd, IntPtr after, int x, int y, int width, int height, uint flags);

    private WindowProcDelegate? _wndProc;
    private IntPtr _previousWndProc;
    private RectInt32[] _passthroughRects = [];
    private double _titleBarHeightPx;
    private int _resizeBorderPx = 4;

    /// <summary>
    /// 建窗时一次：无框、自绘按钮、拖拽/双击由 WndProc 的 HTCAPTION 与 Caption 区承担。
    /// </summary>
    /// <remarks>
    /// 这里不再调 <c>ExtendsContentIntoTitleBar</c>/<c>SetTitleBar</c>：WinUI 会在客户区顶端
    /// 留 1px 标题栏条，正常态是一条白线；最大化时它消失，来回最大化又让边框/阴影状态变脸。
    /// 那一 px 就是 2026-10-22 他连报两次的“白条”。现在标题条只由我们自己的 WndProc 认：
    /// 空白处返回 HTCAPTION，标签/加号/三枚按钮是 Passthrough；平台侧再由
    /// <c>NonClientRegionKind.Caption</c> 补一块整条区域，拖动与双击最大化才在。
    /// </remarks>
    private void BuildWindowChrome()
    {
        if (Overlapped is { } presenter)
        {
            presenter.SetBorderAndTitleBar(false, false);
            presenter.IsResizable = true;
        }

        var hwnd = WindowNative.GetWindowHandle(this);
        _wndProc = WndProc;
        _previousWndProc = SetWindowLongPtr(hwnd, GwlWndProc, Marshal.GetFunctionPointerForDelegate(_wndProc));
        _resizeBorderPx = Math.Max(3, (int)Math.Round(4 * GetDpiForWindow(hwnd) / 96.0));

        CaptionButtons.Width = TabStrip.CaptionWidth;
        CaptionButtons.Margin = new Thickness(0, 0, -TabStrip.CaptionWidth, 0);

        MinimizeButton.Click += (_, _) => Overlapped?.Minimize();
        MaximizeButton.Click += (_, _) => ToggleMaximize();
        CloseButton.Click += (_, _) => Close();

        TitleBarHost.DoubleTapped += OnTitleBarDoubleTapped;
        AppWindow.Changed += (_, _) =>
        {
            UpdateMaximizeGlyph();
            UpdateWindowShadow();

            if (!_fittingMax && Overlapped is { State: OverlappedPresenterState.Maximized })
            {
                DispatcherQueue.TryEnqueue(FitMaximizedToWorkArea);
            }
        };

        StyleCaptionButtons();
        UpdateMaximizeGlyph();

        // 初始那一次 WM_NCCALCSIZE 发生在我们的 WndProc 装上去之前：窗口按系统框算成
        // 1320×840 外框、1304×824 客户区，顶上 8px 露出板底/桌面——用户正常打开时看到的白条。
        // 启动后主动触发一次重算，让客户区从第一帧起就等于外框。
        Root.Loaded += (_, _) =>
        {
            ForceClientFill();
            DispatcherQueue.TryEnqueue(ForceClientFill);
            UpdateWindowShadow();
            DispatcherQueue.TryEnqueue(UpdateWindowShadow);
        };
        Activated += (_, args) =>
        {
            if (args.WindowActivationState != WindowActivationState.Deactivated)
            {
                ForceClientFill();
                UpdateWindowShadow();
            }
        };
        Closed += (_, _) => _windowShadow?.Dispose();
    }

    private WindowShadow? _windowShadow;

    /// <summary>
    /// 自绘投影：系统窗口阴影在这台机器上是关的，所以贴一层分层窗口在主窗口后面。
    /// 最大化/最小化藏起来；位置与大小跟着 AppWindow 走。
    /// </summary>
    private void UpdateWindowShadow()
    {
        if (AppWindow is null)
        {
            return;
        }

        var hwnd = WindowNative.GetWindowHandle(this);

        if (hwnd == IntPtr.Zero)
        {
            return;
        }

        _windowShadow ??= new WindowShadow(hwnd);

        var visible = AppWindow.IsVisible
                      && Overlapped is { State: OverlappedPresenterState.Restored };
        var position = AppWindow.Position;
        var size = AppWindow.Size;

        _windowShadow.Update(position.X, position.Y, size.Width, size.Height, visible);
    }

    /// <summary>
    /// 客户区若还比外框小（启动时系统按旧框算出来的 8px 非客户带），用一次
    /// <c>SWP_FRAMECHANGED</c> 逼系统重新走 <c>WM_NCCALCSIZE</c>。不改位置/大小、不抢焦点。
    /// </summary>
    private void ForceClientFill()
    {
        if (AppWindow is null)
        {
            return;
        }

        var outer = AppWindow.Size;
        var client = AppWindow.ClientSize;

        if (outer.Width == client.Width && outer.Height == client.Height)
        {
            return;
        }

        var hwnd = WindowNative.GetWindowHandle(this);

        if (hwnd == IntPtr.Zero)
        {
            return;
        }

        SetWindowPos(
            hwnd, IntPtr.Zero, 0, 0, 0, 0,
            0x0020 /* FRAMECHANGED */ | 0x0002 /* NOMOVE */ | 0x0001 /* NOSIZE */
            | 0x0004 /* NOZORDER */ | 0x0010 /* NOACTIVATE */);

        Diagnostics.Write(
            $"client fill: outer {outer.Width}×{outer.Height}, client was {client.Width}×{client.Height}");
    }

    /// <summary>
    /// 客户区裁满外框；四边 4 DIP 交缩放；标签/加号/标题按钮交客户区；标题条其余地方
    /// 仍算 Caption，让系统的拖拽、双击最大化和贴边吸附继续工作。
    /// </summary>
    private IntPtr WndProc(IntPtr hWnd, uint message, IntPtr wParam, IntPtr lParam)
    {
        if (message == WmNcCalcSize && wParam != IntPtr.Zero)
        {
            // 客户区 = 整个窗口。系统那 8px 非客户 resize 带连同它后面透出来的东西一起消失。
            return IntPtr.Zero;
        }

        if (message != WmNcHitTest || _previousWndProc == IntPtr.Zero)
        {
            return CallWindowProc(_previousWndProc, hWnd, message, wParam, lParam);
        }

        var raw = lParam.ToInt64();
        var x = unchecked((short)(raw & 0xFFFF));
        var y = unchecked((short)((raw >> 16) & 0xFFFF));

        GetWindowRect(hWnd, out var rect);

        var border = _resizeBorderPx;
        var left = x >= rect.Left && x < rect.Left + border;
        var right = x <= rect.Right && x > rect.Right - border;
        var top = y >= rect.Top && y < rect.Top + border;
        var bottom = y <= rect.Bottom && y > rect.Bottom - border;

        if (top && left) return HtTopLeft;
        if (top && right) return HtTopRight;
        if (bottom && left) return HtBottomLeft;
        if (bottom && right) return HtBottomRight;
        if (left) return HtLeft;
        if (right) return HtRight;
        if (top) return HtTop;
        if (bottom) return HtBottom;

        var clientX = x - rect.Left;
        var clientY = y - rect.Top;

        foreach (var pass in _passthroughRects)
        {
            if (clientX >= pass.X && clientX < pass.X + pass.Width
                && clientY >= pass.Y && clientY < pass.Y + pass.Height)
            {
                return HtClient;
            }
        }

        if (clientY >= 0 && clientY < _titleBarHeightPx)
        {
            return HtCaption;
        }

        return HtClient;
    }

    /// <summary>
    /// 客户区裁满后系统没有边可抓；把四条 4 DIP 的 band 交给 InputNonClientPointerSource 的
    /// Border 区，平台的 resize/cursor 才能在这些看不见的边上工作。尺寸随窗口变化重设。
    /// </summary>
    private void UpdateResizeBorderRegions()
    {
        if (AppWindow is null || !AppWindow.IsVisible)
        {
            return;
        }

        _tabPassthrough ??= InputNonClientPointerSource.GetForWindowId(AppWindow.Id);

        var size = AppWindow.Size;
        var border = Math.Max(3, _resizeBorderPx);

        _tabPassthrough.SetRegionRects(
            NonClientRegionKind.LeftBorder,
            [new RectInt32(0, 0, border, size.Height)]);
        _tabPassthrough.SetRegionRects(
            NonClientRegionKind.RightBorder,
            [new RectInt32(size.Width - border, 0, border, size.Height)]);
        _tabPassthrough.SetRegionRects(
            NonClientRegionKind.TopBorder,
            [new RectInt32(0, 0, size.Width, border)]);
        _tabPassthrough.SetRegionRects(
            NonClientRegionKind.BottomBorder,
            [new RectInt32(0, size.Height - border, size.Width, border)]);
    }

    private bool _fittingMax;

    /// <summary>
    /// 无框窗口的 Maximize 仍会在四边留 8px 的非客户 resize 带，系统把它摆到屏幕 -8,-8；
     /// 这台机器的左侧有一条 68px 的工作区（任务栏/停靠条），于是内容会压住它。把外框按
     /// 当前 client 与 outer 的差补回去，让 client 正好等于 DisplayArea.WorkArea；实测
     /// SetWindowPos 在最大化状态下能改位置而不丢最大化态。
     /// </summary>
    private void FitMaximizedToWorkArea()
    {
        if (_fittingMax || Overlapped is not { State: OverlappedPresenterState.Maximized })
        {
            return;
        }

        var area = DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Primary);

        if (area is null)
        {
            return;
        }

        var work = area.WorkArea;
        var outer = AppWindow.Size;
        var client = AppWindow.ClientSize;
        var insetX = Math.Max(0, (outer.Width - client.Width) / 2);
        var insetY = Math.Max(0, (outer.Height - client.Height) / 2);
        var target = new RectInt32(
            work.X - insetX,
            work.Y - insetY,
            work.Width + (insetX * 2),
            work.Height + (insetY * 2));

        if (AppWindow.Position.X == target.X
            && AppWindow.Position.Y == target.Y
            && AppWindow.Size.Width == target.Width
            && AppWindow.Size.Height == target.Height)
        {
            return;
        }

        _fittingMax = true;

        try
        {
            AppWindow.MoveAndResize(target);
        }
        finally
        {
            _fittingMax = false;
        }
    }

    /// <summary>三枚按钮的皮。尺寸沿用系统 caption 的 46 宽、标题条 38 高；关闭悬停用系统那支红。</summary>
    private void StyleCaptionButtons()
    {
        var dark = NoteStyling.IsDark;
        var transparent = new SolidColorBrush(Colors.Transparent);
        var ink = new SolidColorBrush(NoteStyling.ToColor(Sheet.Ink(dark)));
        var white = new SolidColorBrush(Colors.White);
        var hover = new SolidColorBrush(NoteStyling.WithAlpha(TabStrip.Hover(dark), TabStrip.HoverAlpha(dark)));
        var pressed = new SolidColorBrush(NoteStyling.WithAlpha(TabStrip.Line(dark), TabStrip.HoverAlpha(dark) * 1.8));
        var closeHover = new SolidColorBrush(NoteStyling.ToColor(TabStrip.CaptionCloseRed));

        StyleCaptionButton(MinimizeButton, MinimizeGlyph, "最小化", ink, transparent, hover, pressed, ink, ink);
        StyleCaptionButton(MaximizeButton, MaximizeGlyph, "最大化", ink, transparent, hover, pressed, ink, ink);
        StyleCaptionButton(CloseButton, CloseGlyph, "关闭", ink, transparent, closeHover, closeHover, white, white);

        UpdateMaximizeGlyph();
    }

    private static void StyleCaptionButton(
        Button button,
        string glyph,
        string name,
        Brush ink,
        Brush transparent,
        Brush hoverBackground,
        Brush pressedBackground,
        Brush hoverForeground,
        Brush pressedForeground)
    {
        button.Width = TabStrip.CaptionButtonWidth;
        button.Height = TabStrip.StripHeight;
        button.Content = glyph;
        button.FontFamily = new FontFamily("Segoe MDL2 Assets");
        button.FontSize = TabStrip.CaptionGlyphPx;
        button.Padding = new Thickness(0);
        button.BorderThickness = new Thickness(0);
        button.CornerRadius = new CornerRadius(0);
        button.Background = transparent;
        button.Foreground = ink;
        button.HorizontalContentAlignment = HorizontalAlignment.Center;
        button.VerticalContentAlignment = VerticalAlignment.Center;

        button.Resources["ButtonBackground"] = transparent;
        button.Resources["ButtonBackgroundPointerOver"] = hoverBackground;
        button.Resources["ButtonBackgroundPressed"] = pressedBackground;
        button.Resources["ButtonBackgroundDisabled"] = transparent;
        button.Resources["ButtonBorderBrush"] = transparent;
        button.Resources["ButtonBorderBrushPointerOver"] = transparent;
        button.Resources["ButtonBorderBrushPressed"] = transparent;
        button.Resources["ButtonBorderBrushDisabled"] = transparent;
        button.Resources["ButtonForeground"] = ink;
        button.Resources["ButtonForegroundPointerOver"] = hoverForeground;
        button.Resources["ButtonForegroundPressed"] = pressedForeground;
        button.Resources["ButtonForegroundDisabled"] = ink;

        AutomationProperties.SetName(button, name);
        ToolTipService.SetToolTip(button, name);
    }

    private void ToggleMaximize()
    {
        if (Overlapped is not { } presenter)
        {
            return;
        }

        if (presenter.State == OverlappedPresenterState.Maximized)
        {
            presenter.Restore();
        }
        else
        {
            presenter.Maximize();
            DispatcherQueue.TryEnqueue(FitMaximizedToWorkArea);
        }

        UpdateMaximizeGlyph();
    }

    private void UpdateMaximizeGlyph()
    {
        if (Overlapped is not { } presenter)
        {
            return;
        }

        var maximized = presenter.State == OverlappedPresenterState.Maximized;
        var glyph = maximized ? RestoreGlyph : MaximizeGlyph;
        var name = maximized ? "还原" : "最大化";

        if (MaximizeButton.Content is not string current || !string.Equals(current, glyph, StringComparison.Ordinal))
        {
            MaximizeButton.Content = glyph;
        }

        AutomationProperties.SetName(MaximizeButton, name);
        ToolTipService.SetToolTip(MaximizeButton, name);
    }

    /// <summary>双击标题条空白＝最大化／还原；标签双击归改名、按钮双击归按钮自己。</summary>
    private void OnTitleBarDoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        if (IsInsideButton(e.OriginalSource as DependencyObject) || IsInsideTabItem(e.OriginalSource as DependencyObject))
        {
            return;
        }

        e.Handled = true;
        ToggleMaximize();
    }

    private static bool IsInsideTabItem(DependencyObject? source)
    {
        while (source is not null)
        {
            if (source is TabViewItem)
            {
                return true;
            }

            source = VisualTreeHelper.GetParent(source);
        }

        return false;
    }
}
