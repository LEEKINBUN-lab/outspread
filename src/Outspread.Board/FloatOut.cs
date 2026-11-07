using System.Runtime.InteropServices;
using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Outspread.Core;
using Windows.Foundation;
using Windows.Graphics;
using Windows.UI;
using WinRT.Interop;

namespace Outspread.Board;

/// <summary>
/// 只读浮牌（他 11-04 拍的 A 档，11-07 按他的反馈改成同款渲染）：borderless 置顶窗，
/// 里面装的是 MainWindow 铸的一枚<b>真 NodeVisual</b>——纸面、字排、标签牌与画板上那格
/// 完全同一条渲染管线（Update 一发带上 zoom 与光栅档），不再是自造的简化文本版。
/// A 档是<b>快照不是搬运</b>——原件留在画板上（降透明作"浮出中"标记），浮牌不跟画板实时走；
/// 「回板」＝关浮牌、把格子透明度复原。拖动走 WM_NCLBUTTONDOWN + HTCAPTION 交给系统挪窗，
/// P/Invoke 的路子和 <see cref="WindowShadow"/> 是同一套。任务栏不占位：WS_EX_TOOLWINDOW。
/// </summary>
internal sealed class FloatingNoteWindow : Window
{
    private const int GWL_EXSTYLE = -20;
    private const long WS_EX_TOOLWINDOW = 0x0000_0080;
    private const int WM_NCLBUTTONDOWN = 0x00A1;
    private const int HTCAPTION = 0x0002;

    private readonly IntPtr _hwnd;
    private bool _returnFired;

    /// <summary>浮牌以任何途径收掉（回板钮、Esc、Close）都发一次；MainWindow 据此复原格子。只发一次。</summary>
    public event Action? ReturnRequested;

    /// <param name="note">MainWindow 铸好的格子宿主（Canvas 里装着 Update 过的 NodeVisual）。</param>
    /// <param name="paper">纸面色＝便签自己的表面色，窗缘露出的呼吸边与纸面同色才不突兀。</param>
    public FloatingNoteWindow(UIElement note, Color paper, PointInt32 screenPx, SizeInt32 sizePx)
    {
        _hwnd = WindowNative.GetWindowHandle(this);

        if (AppWindow.Presenter is OverlappedPresenter presenter)
        {
            // 去框的方法长在 Presenter 上不在 AppWindow 上（WindowChrome 踩过的路）。
            presenter.SetBorderAndTitleBar(false, false);
            presenter.IsAlwaysOnTop = true;
            presenter.IsResizable = false;
            presenter.IsMaximizable = false;
            presenter.IsMinimizable = false;
        }

        AppWindow.Move(screenPx);
        AppWindow.Resize(sizePx);

        // 工具窗样式：不占任务栏、不在 Alt-Tab 里占一格（路子同 WindowShadow 的 SetWindowLongPtr）。
        var style = GetWindowLongPtr(_hwnd, GWL_EXSTYLE);
        SetWindowLongPtr(_hwnd, GWL_EXSTYLE, new IntPtr(style.ToInt64() | WS_EX_TOOLWINDOW));

        Content = BuildSurface(note, paper);

        Closed += (_, _) =>
        {
            if (_returnFired)
            {
                return;
            }

            _returnFired = true;
            ReturnRequested?.Invoke();
        };
    }

    /// <summary>纸面铺满、格子居中。回板缩成右上角一枚小圆钮（纸面墨色，20% 底），
    /// 不再占一条标题——内容本身就该是全部。Win10 没有系统圆角，方角＋1px 线读边界。</summary>
    private UIElement BuildSurface(UIElement note, Color paper)
    {
        var ink = NoteStyling.InkFor(paper);

        // NoteStyling 公开的 WithAlpha 吃的是 Rgb＋0..1 浮点；这里全在 Windows.UI.Color 域里走，自己折一层省心。
        static Color Fade(Color c, double a) => Color.FromArgb((byte)Math.Round(Math.Clamp(a, 0, 1) * 255), c.R, c.G, c.B);

        var root = new Grid { Background = new SolidColorBrush(paper) };

        // Esc ＝ 回板。浮牌没有标题栏，键盘得有一条出口（快捷键挂在根元素，Window 没有 KeyboardAccelerators）。
        var esc = new KeyboardAccelerator { Key = Windows.System.VirtualKey.Escape };
        esc.Invoked += (_, _) => Close();
        root.KeyboardAccelerators.Add(esc);

        // 按住任意处＝系统拖窗。handledEventsToo：格子内部吃掉按压的件（牌子、文本）也要能起拖；
        // 回板钮那一路在 handler 里按 OriginalSource 排除。
        root.AddHandler(
            UIElement.PointerPressedEvent,
            new PointerEventHandler((_, e) =>
            {
                if (!e.GetCurrentPoint(root).Properties.IsLeftButtonPressed || IsInsideButton(e.OriginalSource as DependencyObject))
                {
                    return;
                }

                ReleaseCapture();
                _ = SendMessage(_hwnd, WM_NCLBUTTONDOWN, (IntPtr)HTCAPTION, IntPtr.Zero);
                e.Handled = true;
            }),
            handledEventsToo: true);

        root.Children.Add(note);

        var back = new Button
        {
            Name = "FloaterBackButton",
            Width = 26,
            Height = 26,
            MinWidth = 0,
            MinHeight = 0,
            Padding = new Thickness(0),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(13),
            Background = new SolidColorBrush(Fade(ink, 0.10)),
            BorderBrush = new SolidColorBrush(Fade(ink, 0.30)),
            Foreground = new SolidColorBrush(Fade(ink, 0.75)),
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(0, 6, 6, 0),
            Content = new TextBlock
            {
                Text = "回",
                FontFamily = NoteStyling.NoteFont,
                FontSize = 12,
            },
            TabNavigation = KeyboardNavigationMode.Once,
        };
        ToolTipService.SetToolTip(back, "回板（Esc）");
        back.Click += (_, _) => Close();
        root.Children.Add(back);

        var outline = new Border
        {
            BorderBrush = new SolidColorBrush(Fade(ink, 0.20)),
            BorderThickness = new Thickness(1),
            Child = root,
        };

        return outline;
    }

    private static bool IsInsideButton(DependencyObject? source)
    {
        while (source is not null)
        {
            if (source is Button)
            {
                return true;
            }

            source = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetParent(source);
        }

        return false;
    }

    [DllImport("user32.dll")]
    private static extern bool ReleaseCapture();

    [DllImport("user32.dll")]
    private static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    private static extern IntPtr GetWindowLongPtr(IntPtr hWnd, int index);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
    private static extern IntPtr SetWindowLongPtr(IntPtr hWnd, int index, IntPtr value);
}

/// <summary>
/// 浮牌的登记与收放（MainWindow 那一半）。一个格子同时只有一枚浮牌；删除、切板、关主窗都收。
/// </summary>
public sealed partial class MainWindow
{
    /// <summary>浮出中原件降透明，读作"这格正被映在顶层"。</summary>
    private const double FloaterSourceOpacity = 0.45;

    /// <summary>格子 → 浮牌；Opacity 记的是浮出前的原值，回板时照原样还。</summary>
    private readonly Dictionary<string, (FloatingNoteWindow Window, double PreviousOpacity)> _floaters = new(StringComparer.Ordinal);

    /// <summary>浮牌纸面外留的呼吸边（DIP）：格子的合成阴影/发丝不贴着窗框裁。</summary>
    private const double FloaterPadPx = 10;

    /// <summary>rail「浮」钮：把选中的文字便签映到屏幕顶层。File/Link 没有正文，跳过；已浮出的不重复开。
    /// 源格未实格化（视口外被折叠）也照浮——弹出位从世界坐标算，不依赖源格的布局盒。</summary>
    private void PopOutSelectedNotes()
    {
        foreach (var id in _selection.ToArray())
        {
            if (NodeOf(id) is not { } node || node.Kind != CanvasNodeKind.Text)
            {
                continue;
            }

            if (_floaters.ContainsKey(id))
            {
                continue;
            }

            _realized.TryGetValue(id, out var source);
            PopOutNote(id, node, source);
        }
    }

    private void PopOutNote(string id, CanvasNode node, NodeVisual? source)
    {
        var scale = BoardArea.XamlRoot?.RasterizationScale ?? 1;
        var zoom = _view.Zoom;

        // 铸一枚只读的同款格子：渲染管线与画板同一条（Update 带上 zoom 与光栅档），
        // 不挂任何事件钩子——A 档要的就是"和画板上显示的一样"，11-07 他点的。
        var visual = new NodeVisual(node, (relative) => AssetForNode(node, relative), DescribeEntity(node));
        visual.GateMark = source?.GateMark;

        var screenX = _view.ToScreenX(node.X);
        var screenY = _view.ToScreenY(node.Y);

        var host = new Canvas
        {
            Width = Math.Max(2, node.Width) * zoom + FloaterPadPx * 2,
            Height = Math.Max(2, node.Height) * zoom + FloaterPadPx * 2,
        };
        host.Children.Add(visual);
        visual.Update(node, _view, scale);

        // Update 自己会把格子钉在它画板上的屏幕位；这里改写成相对浮窗的位（左上留呼吸边）。
        Place(visual);

        // Update 首发时格子还没进任何窗的树（XamlRoot 为 null），内容账的印章里记着"没根"，
        // 后面的内容重画整段早退——浮牌就是一张空纸（11-07 实拍）。这个无框工具窗里 Loaded
        // 不触发（实测 loaded=False 而.xr 已就绪），所以拿计时器补发：连补三拍，纸面、字排、
        // 牌子才会真的铸出来；补发时印章对上的拍子会被内容账自己早退，不花工。
        var repaint = new Microsoft.UI.Xaml.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(60) };
        var ticks = 0;
        repaint.Tick += (_, _) =>
        {
            visual.Update(node, _view, scale);
            Place(visual);

            if (++ticks >= 3)
            {
                repaint.Stop();
            }
        };
        repaint.Start();

        var width = (int)Math.Clamp(host.Width * scale, 200, 1100);
        var height = (int)Math.Clamp(host.Height * scale, 150, 1500);

        // 格子在客户区的 DIP 位 → 物理 px → 屏幕坐标。客户区原点用 ClientToScreen 拿，
        // 不去猜无框窗的非客户区还剩多少——量出来是多少就是多少。
        var point = new NativePoint
        {
            X = (int)Math.Round(screenX * scale),
            Y = (int)Math.Round(screenY * scale),
        };
        _ = ClientToScreen(WindowNative.GetWindowHandle(this), ref point);
        var (x, y) = ClampedToWorkArea(point.X - (int)Math.Round(FloaterPadPx * scale), point.Y - (int)Math.Round(FloaterPadPx * scale), width, height);

        var previous = source?.Opacity ?? 1;
        source?.Opacity = FloaterSourceOpacity;

        var window = new FloatingNoteWindow(
            host,
            NoteStyling.SurfaceFor(node.Color),
            new PointInt32 { X = x, Y = y },
            new SizeInt32 { Width = width, Height = height });

        _floaters[id] = (window, previous);
        window.ReturnRequested += () => ReturnNote(id);
        window.Activate();

        void Place(NodeVisual visual)
        {
            // 浮窗的画布就是这一格的盒子：格子钉在呼吸边内側，不牵扯画板的屏幕位。
            Canvas.SetLeft(visual, FloaterPadPx);
            Canvas.SetTop(visual, FloaterPadPx);
        }
    }

    /// <summary>收一枚浮牌并复原格子。浮牌的 Closed 也会走到这（同一把字典守着，重复进不来）。</summary>
    private void ReturnNote(string id)
    {
        if (!_floaters.Remove(id, out var entry))
        {
            return;
        }

        if (_realized.TryGetValue(id, out var visual))
        {
            visual.Opacity = entry.PreviousOpacity;
        }

        entry.Window.Close();
    }

    private void CloseFloaters(IEnumerable<string> ids)
    {
        foreach (var id in ids.ToArray())
        {
            ReturnNote(id);
        }
    }

    /// <summary>切板、关主窗用的总收：浮牌是某块板的快照，板走了它没有单独存在的理由（A 档不跨板跟人）。</summary>
    private void CloseAllFloaters()
    {
        CloseFloaters(_floaters.Keys.ToArray());
    }

    /// <summary>把弹出位夹进最近那块屏的工作区，别让浮牌跑到看不见的地方。</summary>
    private (int X, int Y) ClampedToWorkArea(int x, int y, int width, int height)
    {
        var area = DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Nearest).WorkArea;

        return (
            Math.Clamp(x, area.X, Math.Max(area.X, area.X + area.Width - width)),
            Math.Clamp(y, area.Y, Math.Max(area.Y, area.Y + area.Height - height)));
    }

    /// <summary>浮牌的自检：选中一格→浮出→登记在案、原件降透明→已浮出的不重复开→回板→透明度复原、字典清空。
    /// 锁的是弹出／回板这条核心链路。浮牌的窗会真的开一瞬间（自检本来就开着真窗）；
    /// 拖动手感、置顶层级、Esc 这些只有真机看得见，不在这条尺的管里。</summary>
    private void SelfTestFloatOut(List<string> problems)
    {
        var probe = _realized.Keys
            .Select(NodeOf)
            .FirstOrDefault(node => node is { Kind: CanvasNodeKind.Text });

        if (probe is null || !_realized.TryGetValue(probe.Id, out var visual))
        {
            problems.Add("浮出自检: 夹具里没找到文字格，这条尺空跑");

            return;
        }

        var before = _floaters.Count;
        var originalOpacity = visual.Opacity;

        SelectOnly(probe.Id);
        ProjectSelection();
        PopOutSelectedNotes();

        if (!_floaters.ContainsKey(probe.Id) || _floaters.Count != before + 1)
        {
            problems.Add($"浮出自检: 点浮钮后没登记上（字典 {_floaters.Count}，应 {before + 1}）");
        }

        if (Math.Abs(visual.Opacity - FloaterSourceOpacity) > 0.01)
        {
            problems.Add($"浮出自检: 浮出中透明度读到 {visual.Opacity:0.##}，应 {FloaterSourceOpacity}");
        }

        // 同款渲染的凭据：浮窗里那枚真 NodeVisual 要量得出尺寸（Update 布了局、内容真铸出来了）。
        // 量不出就是浮窗铸格这条链断了——屏幕上会是一张空纸。
        if (_floaters.TryGetValue(probe.Id, out var entry))
        {
            entry.Window.Content.UpdateLayout();

            var floated = FindNodeVisualIn(entry.Window.Content);

            if (floated is null || floated.ActualWidth < 8 || floated.ActualHeight < 8)
            {
                problems.Add($"浮出自检: 浮窗里没铸出同款格子（{(floated is null ? "树上没有 NodeVisual" : $"量到 {floated.ActualWidth:0.#}×{floated.ActualHeight:0.#}")}）");
            }
        }

        // 同一格再点一发不许开第二枚。
        PopOutSelectedNotes();

        if (_floaters.Count != before + 1)
        {
            problems.Add($"浮出自检: 已浮出的格子又开了一枚（字典 {_floaters.Count}）");
        }

        ReturnNote(probe.Id);

        if (_floaters.Count != before)
        {
            problems.Add($"浮出自检: 回板后字典没清干净（剩 {_floaters.Count}）");
        }

        if (Math.Abs(visual.Opacity - originalOpacity) > 0.01)
        {
            problems.Add($"浮出自检: 回板后透明度读到 {visual.Opacity:0.##}，原值 {originalOpacity:0.##}");
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint
    {
        public int X;
        public int Y;
    }

    /// <summary>浮窗内容树里找那枚铸出来的格子（FindDescendant 的 name 是必填参，这里按类型走）。</summary>
    private static NodeVisual? FindNodeVisualIn(DependencyObject root)
    {
        if (root is NodeVisual hit)
        {
            return hit;
        }

        var count = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetChildrenCount(root);

        for (var index = 0; index < count; index++)
        {
            if (FindNodeVisualIn(Microsoft.UI.Xaml.Media.VisualTreeHelper.GetChild(root, index)) is { } found)
            {
                return found;
            }
        }

        return null;
    }

    [DllImport("user32.dll")]
    private static extern bool ClientToScreen(IntPtr hWnd, ref NativePoint point);
}
