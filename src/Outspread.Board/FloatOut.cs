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
/// 浮牌窗（他 11-04 拍 A 档、11-07 拍 B 档编辑）：borderless 置顶窗，里面装 MainWindow 铸的
/// 一枚<b>真 NodeVisual</b>——纸面、字排、标签牌与画板上那格同一条渲染管线。窗自己不再
/// 画第二层皮：底色＝纸面色只为窗角不露黑，呼吸边为 0，窗缘那条发丝线就是格子自己的——
/// 看见的应当是一格便签浮在顶层，不是套娃。
/// 拖动＝<b>按住并移动</b>：按下捕获指针、之后每一拍 GetCursorPos − 起点算增量挪窗。
/// 之前两版都走 WM_NCLBUTTONDOWN 的模态拖动循环，这投影里循环会吞掉松开——轻点一下
/// 窗口就黏着鼠标跟到天荒地老（他连报两次）。手动挪窗没有循环：松手即停，跟手 1:1。
/// 双击＝就地编辑（B 档）：编辑框与画板同一套外皮，提交走同一把撤销账。
/// </summary>
internal sealed class FloatingNoteWindow : Window
{
    private const int GWL_EXSTYLE = -20;
    private const long WS_EX_TOOLWINDOW = 0x0000_0080;

    /// <summary>按住挪过这条死区才算"要拖"：轻点、双击都不惊动。</summary>
    private const double DragDeadZonePx = 3;

    private readonly IntPtr _hwnd;
    private readonly Grid _root;
    private TextBox? _editorBox;
    private bool _returnFired;
    private bool _dragging;
    private bool _dragEngaged;
    private Windows.Foundation.Point _dragCursor0;
    private PointInt32 _dragWindow0;

    /// <summary>浮牌以任何途径收掉（回板钮、Esc、Close）都发一次；MainWindow 据此复原格子。只发一次。</summary>
    public event Action? ReturnRequested;

    /// <summary>双击正文＝想就地编辑（B 档）。MainWindow 接住后调 <see cref="ShowEditor"/>。</summary>
    public event Action? EditRequested;

    /// <summary>编辑框提交（Ctrl＋Enter／失焦）。带的是正文原文，标记那几行由 MainWindow 保管接回。</summary>
    public event Action<string>? EditCommitted;

    /// <summary>正在编辑：拖动与 Esc-回板都要让位。</summary>
    public bool Editing => _editorBox is not null;

    /// <param name="note">MainWindow 铸好的格子宿主（Canvas 里装着 Update 过的 NodeVisual）。</param>
    /// <param name="paper">纸面色＝便签自己的表面色，窗角与格子圆角之间才不露第二层皮。</param>
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

        _root = new Grid { Background = new SolidColorBrush(paper) };

        // Esc：编辑中＝收编辑框；否则＝回板。（快捷键挂在根元素，Window 没有 KeyboardAccelerators。）
        var esc = new KeyboardAccelerator { Key = Windows.System.VirtualKey.Escape };
        esc.Invoked += (_, _) =>
        {
            if (_editorBox is not null)
            {
                CloseEditor();
            }
            else
            {
                Close();
            }
        };
        _root.KeyboardAccelerators.Add(esc);

        // 拖动手感（他 11-07 连报两版的"点一下就黏着鼠标"）：彻底不走 WM_NCLBUTTONDOWN 的
        // 模态循环。按下记鼠标与窗的基线并捕获指针，之后每一拍按 GetCursorPos 的增量挪窗；
        // 指针被捕获，鼠标跑出窗外事件也不断，松手即停，跟手 1:1。
        _root.AddHandler(
            UIElement.PointerPressedEvent,
            new PointerEventHandler((_, e) =>
            {
                if (Editing || _dragging || IsInside(e.OriginalSource as DependencyObject, static el => el is Button or TextBox))
                {
                    return;
                }

                if (!e.GetCurrentPoint(_root).Properties.IsLeftButtonPressed)
                {
                    return;
                }

                _dragCursor0 = CursorPos();
                _dragWindow0 = AppWindow.Position;
                _dragging = true;
                _root.CapturePointer(e.Pointer);
                e.Handled = true;
            }),
            handledEventsToo: true);

        _root.PointerMoved += (_, e) =>
        {
            if (!_dragging)
            {
                return;
            }

            var at = CursorPos();

            if (!_dragEngaged)
            {
                if (Math.Abs(at.X - _dragCursor0.X) + Math.Abs(at.Y - _dragCursor0.Y) <= DragDeadZonePx)
                {
                    return;
                }

                _dragEngaged = true;
            }

            AppWindow.Move(new PointInt32
            {
                X = _dragWindow0.X + (int)Math.Round(at.X - _dragCursor0.X),
                Y = _dragWindow0.Y + (int)Math.Round(at.Y - _dragCursor0.Y),
            });
        };

        _root.PointerReleased += (_, e) =>
        {
            _dragging = false;
            _dragEngaged = false;
            _root.ReleasePointerCapture(e.Pointer);
        };

        _root.PointerCanceled += (_, e) =>
        {
            _dragging = false;
            _dragEngaged = false;
            _root.ReleasePointerCapture(e.Pointer);
        };

        // 双击正文＝编辑。回板钮自己吃事件，不会漏到这里。
        _root.DoubleTapped += (_, e) =>
        {
            if (!Editing && !IsInside(e.OriginalSource as DependencyObject, static el => el is Button))
            {
                EditRequested?.Invoke();
                e.Handled = true;
            }
        };

        _root.Children.Add(note);
        _root.Children.Add(BuildBackButton());

        Content = _root;

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

    private Button BuildBackButton()
    {
        var ink = NoteStyling.InkFor(((SolidColorBrush)_root.Background).Color);

        static Color Fade(Color c, double a) => Color.FromArgb((byte)Math.Round(Math.Clamp(a, 0, 1) * 255), c.R, c.G, c.B);

        var back = new Button
        {
            Name = "FloaterBackButton",
            Width = 24,
            Height = 24,
            MinWidth = 0,
            MinHeight = 0,
            Padding = new Thickness(0),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(12),
            Background = new SolidColorBrush(Fade(ink, 0.08)),
            BorderBrush = new SolidColorBrush(Fade(ink, 0.25)),
            Foreground = new SolidColorBrush(Fade(ink, 0.70)),
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(0, 5, 5, 0),
            Content = new TextBlock
            {
                Text = "回",
                FontFamily = NoteStyling.NoteFont,
                FontSize = 11,
            },
            TabNavigation = KeyboardNavigationMode.Once,
        };
        ToolTipService.SetToolTip(back, "回板（Esc）");
        back.Click += (_, _) =>
        {
            // 编辑没提交就点回板：先落账再走，别把刚打的字丢了。
            CommitEditor();
            Close();
        };

        return back;
    }

    /// <summary>就地编辑（B 档）：编辑框盖在格子正文的位上，外皮与画板编辑器同一套。
    /// 由 MainWindow 在 <see cref="EditRequested"/> 里调，色号字号都按那一格算好。</summary>
    public void ShowEditor(string body, double fontPx, Color ink, Color surface, Color line, Thickness padding, double widthDip, double heightDip)
    {
        CloseEditor();

        _editorBox = new TextBox
        {
            AcceptsReturn = true,
            TextWrapping = TextWrapping.Wrap,
            FontFamily = NoteStyling.NoteFont,
            FontSize = fontPx,
            Foreground = new SolidColorBrush(ink),
            Background = new SolidColorBrush(surface),
            BorderBrush = new SolidColorBrush(line),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(0),
            Padding = padding,
            IsSpellCheckEnabled = false,
            UseLayoutRounding = false,
            Text = body,
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(0),
            Width = widthDip,
            Height = heightDip,
        };

        _editorBox.KeyDown += (_, e) =>
        {
            if (e.Key is Windows.System.VirtualKey.Enter
                && Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(Windows.System.VirtualKey.Control).HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down))
            {
                e.Handled = true;
                CommitEditor();
            }
            else if (e.Key is Windows.System.VirtualKey.Escape)
            {
                e.Handled = true;
                CloseEditor();
            }
        };

        _editorBox.LostFocus += (_, _) => CommitEditor();

        _root.Children.Add(_editorBox);
        _editorBox.Focus(FocusState.Programmatic);
        _editorBox.Select(_editorBox.Text.Length, 0);
    }

    /// <summary>收编辑框。有改动经 <see cref="EditCommitted"/> 交 MainWindow 落账（撤销与保存走画板同一把）。</summary>
    public void CommitEditor()
    {
        if (_editorBox is not { } box)
        {
            return;
        }

        _editorBox = null;
        var text = box.Text;
        _root.Children.Remove(box);
        EditCommitted?.Invoke(text);
    }

    private void CloseEditor()
    {
        if (_editorBox is not { } box)
        {
            return;
        }

        _editorBox = null;
        _root.Children.Remove(box);
    }

    private static bool IsInside(DependencyObject? source, Func<DependencyObject, bool> match)
    {
        while (source is not null)
        {
            if (match(source))
            {
                return true;
            }

            source = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetParent(source);
        }

        return false;
    }

    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out NativePoint point);

    private static Windows.Foundation.Point CursorPos()
    {
        GetCursorPos(out var p);

        return new Windows.Foundation.Point(p.X, p.Y);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint
    {
        public int X;
        public int Y;
    }

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    private static extern IntPtr GetWindowLongPtr(IntPtr hWnd, int index);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
    private static extern IntPtr SetWindowLongPtr(IntPtr hWnd, int index, IntPtr value);
}

/// <summary>
/// 浮牌的登记、铸格与收放（MainWindow 那一半）。一个格子同时只有一枚浮牌；删除、切板、关主窗都收。
/// B 档编辑的落账在这边：<see cref="CommitFloatNoteText"/> 与画板编辑器走同一条
/// BeginGesture → 写回 → FitNoteHeight → EndGesture → Rebuild → SaveSoon 链，撤销与自动保存天然跟随。
/// </summary>
public sealed partial class MainWindow
{
    /// <summary>浮出中原件降透明，读作"这格正被映在顶层"。</summary>
    private const double FloaterSourceOpacity = 0.45;

    /// <summary>格子 → 浮牌；Opacity 记的是浮出前的原值，回板时照原样还。</summary>
    private readonly Dictionary<string, (FloatingNoteWindow Window, double PreviousOpacity)> _floaters = new(StringComparer.Ordinal);

    /// <summary>浮牌纸面外的呼吸边（DIP）：0——窗就是纸面，窗缘那条发丝线＝格子自己的边，
    /// 不给他再数出一层框（他 11-07 连报两版的"两层边框"）。</summary>
    private const double FloaterPadPx = 0;

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

        var host = new Canvas
        {
            Width = Math.Max(2, node.Width) * zoom + FloaterPadPx * 2,
            Height = Math.Max(2, node.Height) * zoom + FloaterPadPx * 2,
        };
        host.Children.Add(visual);
        visual.Update(node, _view, scale);

        // Update 自己会把格子钉在它画板上的屏幕位；浮窗的画布就是这一格的盒子，钉在呼吸边内侧。
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

        var width = (int)Math.Clamp(host.Width * scale, 160, 1100);
        var height = (int)Math.Clamp(host.Height * scale, 120, 1500);

        // 格子在画板上的屏幕位（物理 px）→ 浮窗就开在那儿。
        var point = new NativePoint
        {
            X = (int)Math.Round(_view.ToScreenX(node.X) * scale),
            Y = (int)Math.Round(_view.ToScreenY(node.Y) * scale),
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

        window.EditRequested += () => OpenFloatEditor(id);
        window.EditCommitted += body =>
        {
            if (CommitFloatNoteText(id, body))
            {
                RefitFloater(id, scale);
            }
        };

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

    /// <summary>双击浮牌＝就地编辑（B 档）：正文种进编辑框（标记那几行留在账上，提交时原样接回）。</summary>
    private void OpenFloatEditor(string id)
    {
        if (!_floaters.TryGetValue(id, out var entry)
            || NodeOf(id) is not { } node
            || !EntityCards.CanEditBody(node))
        {
            return;
        }

        var zoom = _view.Zoom;
        var surface = NoteStyling.SurfaceFor(node.Color);
        var seeded = Tags.Read(node.Text);

        entry.Window.ShowEditor(
            seeded.Body,
            _view.FontSizeFor(id) * zoom,
            NoteStyling.InkFor(node.Color, surface),
            surface,
            NoteStyling.LineFor(node.Color, surface),
            new Thickness(NoteTypography.For(_view.FontSizeFor(id)).Padding * zoom),
            Math.Max(2, node.Width) * zoom + FloaterPadPx,
            Math.Max(2, node.Height) * zoom + FloaterPadPx);
    }

    /// <summary>浮牌编辑的落账：与画板编辑器同一条链（标记接回 → BeginGesture → 写回 →
    /// FitNoteHeight → EndGesture → Rebuild → SaveSoon），撤销与自动保存天然跟随。有真改动才返回 true。</summary>
    private bool CommitFloatNoteText(string id, string body)
    {
        if (NodeOf(id) is not { } node || !EntityCards.CanEditBody(node))
        {
            return false;
        }

        // 编辑框里看到的只是正文；标记那几行由账面原样接回（与画板编辑器同规矩）。
        var props = Tags.Read(node.Text).Props;
        var text = NoteProperties.WithProps(props, JsonText.NormalizeTextNewlines(body));

        if (string.Equals(text, node.Text, StringComparison.Ordinal))
        {
            return false;
        }

        BeginGesture("改了一格文字（浮牌）");
        node.Text = text;
        FitNoteHeight(node);
        EndGesture();
        Rebuild();
        SaveSoon();
        DrainPendingReload();

        // Rebuild 重铸了画板那格，"浮出中"的降透明标记跟着丢了——补回去，浮牌没收前画板上仍是那格半透明的。
        if (_realized.TryGetValue(id, out var redimmed))
        {
            redimmed.Opacity = FloaterSourceOpacity;
        }

        return true;
    }

    /// <summary>落账后格子可能长高/变矮：画布、浮窗与格子的布局盒一起跟上，弹窗位置不跑出屏幕。</summary>
    private void RefitFloater(string id, double scale)
    {
        if (!_floaters.TryGetValue(id, out var entry)
            || NodeOf(id) is not { } node
            || entry.Window.Content is not Grid root
            || root.Children.FirstOrDefault() is not Canvas host)
        {
            return;
        }

        var zoom = _view.Zoom;
        host.Width = Math.Max(2, node.Width) * zoom + FloaterPadPx * 2;
        host.Height = Math.Max(2, node.Height) * zoom + FloaterPadPx * 2;

        if (_realized.TryGetValue(id, out var visual))
        {
            visual.Update(node, _view, scale);
            Canvas.SetLeft(visual, FloaterPadPx);
            Canvas.SetTop(visual, FloaterPadPx);
        }

        var width = (int)Math.Clamp(host.Width * scale, 160, 1100);
        var height = (int)Math.Clamp(host.Height * scale, 120, 1500);

        var point = new NativePoint
        {
            X = (int)Math.Round(_view.ToScreenX(node.X) * scale),
            Y = (int)Math.Round(_view.ToScreenY(node.Y) * scale),
        };
        _ = ClientToScreen(WindowNative.GetWindowHandle(this), ref point);
        var (x, y) = ClampedToWorkArea(point.X - (int)Math.Round(FloaterPadPx * scale), point.Y - (int)Math.Round(FloaterPadPx * scale), width, height);

        entry.Window.AppWindow.Resize(new SizeInt32 { Width = width, Height = height });
        entry.Window.AppWindow.Move(new PointInt32 { X = x, Y = y });
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

    /// <summary>切板、关主窗用的总收：浮牌是某块板的快照，板走了它没有单独存在的理由（不跨板跟人）。</summary>
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

    /// <summary>浮牌的自检：选中一格→浮出→登记在案、原件降透明、浮窗里同款格子量得出尺寸→
    /// 已浮出的不重复开→回板→透明度复原、字典清空。锁的是弹出／回板这条核心链路。
    /// 拖动手感、置顶层级、就地编辑这些只有真机看得见，不在这条尺的管里。</summary>
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

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint
    {
        public int X;
        public int Y;
    }

    [DllImport("user32.dll")]
    private static extern bool ClientToScreen(IntPtr hWnd, ref NativePoint point);
}
