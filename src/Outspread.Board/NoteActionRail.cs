using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Outspread.Core;
using Windows.Foundation;
using Windows.UI;

namespace Outspread.Board;

/// <summary>
/// 便签的操作条（10-22 他点了形的 v3 原型 1:1）：选中恰好一枚时，它左侧浮出三枚圆钮——
/// 组 / 换色 / 删。分组这件事从"拖便签"里摘出来，只归组钮：
/// <list type="bullet">
/// <item>轻点组钮＝菜单（创建分组 / 移出分组，不可用的灰着）。点便签不再被塞进某个已有组——
/// 那正是 10-22「莫名其妙分到已有分组」的错；要并进一个具体的框，走组钮<b>按住拖</b>那一条。</item>
/// <item>轻点换色钮＝在操作条左侧弹一列圆角色板（6 预设＋无色，直接显示颜色），点一格套色并收起。
/// 换色不再走循环，也不在右键菜单里——颜色只长在这块板子唯一该长它的地方。</item>
/// <item>按住组钮拖＝便签跟手，但框在拖动期间纹丝不动（它们是文档里的旧矩形，只有松手的
/// EndGesture 才重算）；激活阈值是一角碰上就算（矩形相交），不是中心进框——碰进别的框那框亮蓝、
/// 整格离开原框那框亮红。松手时<b>格子留在原地不动</b>，只把目标框长过去罩住整格（中心随之入新框，
/// 成员按中心那条规矩下一拍仍自洽），光标带一枚淡环。这样拖入既不压住原有成员、也不挪你的格子。</item>
/// <item>单独拖便签＝只挪位置：成员不许被普通拖动甩出组——框会跟着长（sticky），
/// 这是他躲误操作的那条"更合理"。</item>
/// </list>
/// 时长全取 <see cref="Motion"/>：进 120 错峰、退 167、框 250 由排版那侧走。
/// </summary>
public sealed partial class MainWindow
{
    private Border? _rail;
    private StackPanel? _railHost;
    private readonly List<Ellipse> _railHalos = [];
    private Border? _railGroup;
    private Border? _railColor;
    private Border? _railArrange;
    private Border? _railTag;
    private Border? _railDelete;
    private Grid? _railStack;
    private Canvas? _railHaloCanvas;
    private Ellipse? _tether;
    private RailDrag? _railDrag;
    private bool _railShown;
    private bool _railGroupActive;

    /// <summary>取证拍图期间为 true：入场动效改成立刻落定，照片才拍得到那一排圆钮。</summary>
    private bool _capturingShot;

    /// <summary>指针停在哪一枚上。这个投影里 <c>UIElement</c> 没有 IsPointerOver，自己记一格。</summary>


    private Border? _railHover;
    private Border? _colorPopover;
    private bool _colorOpen;
    private double _railLeft;
    private double _railTop;
    private bool _railFlipped;

    private sealed class RailDrag
    {
        public required string NodeId { get; init; }
        public Point StartPointer { get; init; }
        public double StartX { get; init; }
        public double StartY { get; init; }
        public bool Started;
        public string? StartGroupId;

        // 多选时这一发要拖走整组选中：记下每一枚的起手坐标（单选时它就一枚）。
        public Dictionary<string, (double X, double Y)> StartPositions { get; init; } = new(StringComparer.Ordinal);

        /// <summary>开手那一拍每个组的成员名单：松手时按它改写，才知道谁进了谁出了组。</summary>
        public Dictionary<string, List<string>> StartRosters { get; init; } = new(StringComparer.Ordinal);
    }

    /// <summary>ProjectSelection 每一拍叫它：该不该有操作条、贴在哪。手势进行中不重画。</summary>
    private void EnsureRail()
    {
        // 选中至少一枚就浮操作条——多枚更需要那枚"创建分组"（10-22 他：选中多个也该出现按钮）。
        // 位置按整个选中的包围盒贴，不再只认那一枚。
        if (_resizeGesture is not null || SelectionBox() is not { } box)
        {
            HideRail();

            return;
        }

        _rail ??= BuildRail();

        // 排列钮和批量标签钮只在选中多枚时插进来；先同步，外壳高度与落位都跟着这一发的钮数走。
        var many = SelectedNotes().Count >= 2;
        SyncRailArrange(many);
        SyncRailTag(many);

        if (RailButtons().Length == 0)
        {
            HideRail();

            return;
        }

        var cellCount = RailButtons().Length;
        var shellHeight = RailShellHeight(cellCount);
        var screenLeft = _view.ToScreenX(box.X);
        var flip = screenLeft < 60;
        // 外壳比圆盘宽 2×晕影：落位把这层内缩抵消掉，圆盘仍贴在原来那条 12 像素的缝上。
        var x = (flip ? _view.ToScreenX(box.Right) + 12 : screenLeft - 50) - ControlKit.VignetteSpreadPx;

        // 夹回视口里：选区比视口还宽（放大到只剩两格横排、或全选）时，左缘和右缘**都**在屏幕外，
        // 上面那一发翻到哪一侧都是贴到看不见的地方——选中状态还在，那列钮却再也够不着
        // （他 10-26 的 @n8 同款：点完排序，选中框在、左侧没有按钮）。
        x = Math.Clamp(x, 8, Math.Max(8, BoardArea.ActualWidth - RailShellWidthPx - 8));
        // 圆盘列的中心对准选区的中心（不是固定往下 64）：四枚时原来会整体偏低 27px。
        var y = Math.Clamp(
            _view.ToScreenY(box.Y + (box.Height / 2)) - RailTopInsetPx - (RailColumnHeight(cellCount) / 2),
            8,
            Math.Max(8, BoardArea.ActualHeight - shellHeight - 8));

        // 落位对齐到整枚设备像素：38px 的圆在 .37 这种偏移上描边会一段实一段虚，看起来就是锯齿。
        x = SnapToPixel(x);
        y = SnapToPixel(y);

        // 记住操作条这一列落在哪：色板弹层要贴在同一坐标空间里（HandleLayer 带平移/缩放变换，
        // 用 TransformToVisual 量会偏出一大截——10-22 那张截图色板跑到最左边就是这么来的）。
        _railLeft = x;
        _railTop = y;
        _railFlipped = flip;

        if (_railSurfaceDark != NoteStyling.IsDark)
        {
            PaintRailSurface();
        }

        Canvas.SetLeft(_rail, x);
        Canvas.SetTop(_rail, y);

        if (_capturingShot)
        {
            foreach (var entry in RailButtons())
            {
                var onBoard = entry.TransformToVisual(BoardArea).TransformPoint(new Point(0, 0));

                Diagnostics.Write(
                    $"操作条探针: {entry.Name} 落位 ({onBoard.X:0.#},{onBoard.Y:0.#}) 尺寸 {entry.ActualWidth:0.#}×{entry.ActualHeight:0.#}"
                    + $" 透明 {entry.Opacity:0.##} 可见 {entry.Visibility}");
            }
        }

        if (!_railShown)
        {
            _railShown = true;
            _rail.Visibility = Visibility.Visible;
            AnimateRailIn();
        }
    }

    /// <summary>自检那把尺：操作条此刻亮不亮、有几枚钮。选中框与这一列必须同现同隐（@n8）。</summary>
    internal bool RailIsShown => _railShown;

    internal int RailButtonCount => RailButtons().Length;

    /// <summary>自检那把尺：操作条亮着的时候，整列必须落在可视区里。选区比视口宽（放大到只剩
    /// 两格横排、或全选）时，老算法会把它贴到屏幕外——选中状态还在，按钮却够不着（@n8 同款）。</summary>
    internal bool RailInsideViewport
    {
        get
        {
            if (!_railShown || _rail is null)
            {
                return false;
            }

            var left = Canvas.GetLeft(_rail);
            var top = Canvas.GetTop(_rail);

            return !double.IsNaN(left)
                && !double.IsNaN(top)
                && left >= 0
                && top >= 0
                && (left + RailShellWidthPx) <= (BoardArea.ActualWidth + 0.5)
                && (top + _rail.ActualHeight) <= (BoardArea.ActualHeight + 0.5);
        }
    }

    private void HideRail()
    {
        if (_rail is null || !_railShown)
        {
            return;
        }

        _railShown = false;
        _railHover = null;
        CloseGroupMenu();
        CloseArrangeMenu();
        CloseTagMenu();
        CloseColorPopover();

        var fade = FadeTo(_rail, 0, Motion.BaseMs);
        var board = new Microsoft.UI.Xaml.Media.Animation.Storyboard();
        board.Children.Add(fade);
        board.Completed += (_, _) =>
        {
            if (!_railShown)
            {
                _rail!.Visibility = Visibility.Collapsed;
            }
        };
        board.Begin();
    }

    private Border BuildRail()
    {
        var host = new StackPanel
        {
            Spacing = RailGapPx,
            Width = RailButtonPx,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(0, RailTopInsetPx, 0, 0),
            Opacity = 0,
        };

        _railHost = host;

        _railGroup = RoundButton("NoteRailGroup", GroupIcon(), "分组（点＝菜单，按住拖＝拖入/拖出）");
        _railColor = RoundButton("NoteRailColor", ColorWheelIcon(), "换色");
        _railArrange = RoundButton("NoteRailArrange", ArrangeIcon(), "排列（按颜色或标签；排选中的，没选中＝整板）");
        _railTag = RoundButton("NoteRailTag", TagIcon(), "批量加标签（选中的便签；可新增标签）");

        _railGroup.Visibility = _view.IsRailButtonHidden("group") ? Visibility.Collapsed : Visibility.Visible;
        _railColor.Visibility = _view.IsRailButtonHidden("color") ? Visibility.Collapsed : Visibility.Visible;

        // 删的红现在长在图形本身，不再长在圈上——静止那一档根本没有圈。
        _railDelete = RoundButton("NoteRailDelete", TrashIcon(), "删掉这格");
        _railDelete.Visibility = _view.IsRailButtonHidden("delete") ? Visibility.Collapsed : Visibility.Visible;

        host.Children.Add(_railGroup);
        host.Children.Add(_railColor);
        host.Children.Add(_railDelete);

        WireRailButtons();

        // 晕影单独一层垫在钮下面，而不是塞进每一枚的格子里：塞进格子就得把格子撑到 58，
        // 钮与钮的净空会从 10 变成 30（布局被影改了）。这一层正好是三枚的外接盒，
        // 也不存在"58 的影被 38 的格子裁掉"——10-24 第一发右下角那块直边灰楔就是这么来的。
        var halos = new Canvas
        {
            Width = RailShellWidthPx,
            Height = RailShellHeight(3),
            IsHitTestVisible = false,
        };

        _railHaloCanvas = halos;
        _railHalos.Clear();

        // 四圈影先建好，用哪几圈由钮数决定（排列钮是后插进来的，不能等到那时再补影）。
        for (var i = 0; i < RailMaxCells; i++)
        {
            var halo = Vignette();
            Canvas.SetLeft(halo, 0);
            Canvas.SetTop(halo, i * RailPitchPx);
            halo.Visibility = i < 3 ? Visibility.Visible : Visibility.Collapsed;
            _railHalos.Add(halo);
            halos.Children.Add(halo);
        }

        var stack = new Grid
        {
            Width = RailShellWidthPx,
            Height = RailShellHeight(3),
        };

        _railStack = stack;
        stack.Children.Add(halos);
        stack.Children.Add(host);

        var shell = new Border
        {
            Name = "NoteRail",
            Width = RailShellWidthPx,
            Height = RailShellHeight(3),
            Child = stack,
            Visibility = Visibility.Collapsed,
            IsHitTestVisible = true,
        };

        HandleLayer.Children.Add(shell);

        // BuildRail 只由 `_rail ??= BuildRail()` 调进来，这里先挂上是为了让 PaintRailSurface 认得它。
        _rail = shell;
        PaintRailSurface();

        return shell;
    }

    /// <summary>圆钮本体直径——令牌里那一个数，色板落位与命中区都按它算。</summary>
    private const double RailButtonPx = ControlKit.ButtonPx;

    /// <summary>钮与钮之间的净空。加晕影之前就是 10——影不许参与布局。</summary>
    private const double RailGapPx = 10;

    private const double RailPitchPx = RailButtonPx + RailGapPx;

    /// <summary>最多五枚：组、色、排列、批量标签、删。排列/标签只在选中 ≥2 时插进来。</summary>
    private const int RailMaxCells = 5;

    /// <summary>外壳：晕影那一圈在左右各扩 10；高度按此刻的钮数算（上下各扩 10）。</summary>
    private const double RailShellWidthPx = RailButtonPx + (2 * ControlKit.VignetteSpreadPx);

    private static double RailColumnHeight(int cells)
        => (cells * RailButtonPx) + (Math.Max(0, cells - 1) * RailGapPx);

    private static double RailShellHeight(int cells)
        => RailColumnHeight(cells) + (2 * ControlKit.VignetteSpreadPx);

    /// <summary>圆盘列在外壳里的顶边：上面留 10 给影，下压 2 由外壳底边多出的那截补。</summary>
    private const double RailTopInsetPx = ControlKit.VignetteSpreadPx - ControlKit.VignetteDropPx;

    /// <summary>纸面那一圈发丝：1 逻辑像素（上一版是 1.25，小数宽度本身就是一条灰边）。</summary>
    private const double RailHairlinePx = 1;

    private static readonly Color RailDangerInk = Color.FromArgb(255, 0xD3, 0x2F, 0x2F);

    /// <summary>上一次画浮层底时的深浅档；只有换主题才重画，不必每帧造刷子。</summary>
    private bool? _railSurfaceDark;

    /// <summary>
    /// 一枚圆钮：外壳仍是 <see cref="Border"/>（自检按名字找它、拖动也靠它的指针事件），
    /// 里面叠三层——悬停层、按压层、组钮激活环——静止时三层全透明，圆面只在指针上来时才出现。
    /// 这是 Chrome／Fluent 2／M3 三家的共同画法：状态是<b>叠在底上的半透明墨层</b>，
    /// 不是"换一次背景色"；层有 100ms 进、250ms 出，所以点得下去的那一下看得见反馈。
    /// </summary>
    private Border RoundButton(string name, UIElement icon, string help)
    {
        var stack = new Grid
        {
            Width = RailButtonPx,
            Height = RailButtonPx,
        };

        stack.Children.Add(Disc(opacity: 1));
        stack.Children.Add(Disc());
        stack.Children.Add(Disc());
        stack.Children.Add(new Ellipse
        {
            Width = RailButtonPx - 2 * RailHairlinePx,
            Height = RailButtonPx - 2 * RailHairlinePx,
            StrokeThickness = 2,
            Opacity = 0,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            IsHitTestVisible = false,
        });
        stack.Children.Add(icon);

        var button = new Border
        {
            Name = name,
            Width = RailButtonPx,
            Height = RailButtonPx,
            Background = new SolidColorBrush(Colors.Transparent),
            Child = stack,
            Tag = help,
            IsHitTestVisible = true,
            RenderTransform = new TranslateTransform { X = 8 },
            Opacity = 0,
        };

        // 三枚钮只有图标没有字：读屏与悬停都需要这行说明（它原来只躺在 Tag 里，没人读）。
        ToolTipService.SetToolTip(button, help);
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(button, help);

        PaintRailButton(button);

        return button;
    }

    /// <summary>
    /// 一枚钮的四层：纸面、悬停层、按压层、组钮激活环。按加入顺序取——顺序变了自检会红，
    /// 因为时长与哪一层该出现在哪一档，全靠这个顺序对上。晕影不在这四层里（见 BuildRail 那段）。
    /// </summary>
    private static (Ellipse Face, Ellipse Hover, Ellipse Press, Ellipse Ring)? LayersOf(Border button)
    {
        if (button.Child is not Grid grid)
        {
            return null;
        }

        var layers = grid.Children.OfType<Ellipse>().ToList();

        return layers.Count < 4
            ? null
            : (layers[0], layers[1], layers[2], layers[3]);
    }

    /// <summary>一枚圆盘：直径就是令牌那一格；默认全透明，只有纸面那一枚起来。</summary>
    private static Ellipse Disc(double opacity = 0)
        => new()
        {
            Width = RailButtonPx,
            Height = RailButtonPx,
            Opacity = opacity,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            IsHitTestVisible = false,
        };

    /// <summary>
    /// 一圈径向渐变的晕影：盘以内全透明（不糊纸面），盘沿那一圈最实，往外散到零。
    /// 出处见 <see cref="ControlKit.VignetteAlpha"/>——Chrome 那圈"几乎看不见的影"在容器上，
    /// 而这几枚浮在板面上就是容器。自己画渐变而不挂投影：那要一张遮罩，这版 WinUI 没有现成的。
    /// 下压不写在这里，落在 Canvas 的落位上——有 RenderTransform 就会往外顶，反而多一层裁剪的口子。
    /// </summary>
    private static Ellipse Vignette()
    {
        var size = ControlKit.ButtonPx + (2 * ControlKit.VignetteSpreadPx);
        var edge = (ControlKit.ButtonPx / 2) / size;

        var halo = new RadialGradientBrush
        {
            GradientOrigin = new Point(0.5, 0.5),
            Center = new Point(0.5, 0.5),
            RadiusX = 0.5,
            RadiusY = 0.5,
        };

        // 透明端必须写死成"黑且 alpha 0"：Colors.Transparent 是白透明（#00FFFFFF），
        // 这个投影的渐变按非预乘插值，中途会掺进灰色——深底上那圈影反而比板面亮。
        var clear = Color.FromArgb(0, 0, 0, 0);

        halo.GradientStops.Add(new GradientStop { Color = clear, Offset = 0 });
        halo.GradientStops.Add(new GradientStop { Color = clear, Offset = Math.Max(0, edge - 0.12) });
        halo.GradientStops.Add(new GradientStop { Color = AtAlpha(Colors.Black, ControlKit.VignetteAlpha), Offset = edge });
        halo.GradientStops.Add(new GradientStop { Color = clear, Offset = 1 });

        return new Ellipse
        {
            Width = size,
            Height = size,
            Opacity = 1,
            IsHitTestVisible = false,
            Fill = halo,
        };
    }

    /// <summary>一层的颜色：静止时它是透明的，所以这里只定"上来了有多深"。</summary>
    private void PaintRailButton(Border button)
    {
        if (LayersOf(button) is not { } layers)
        {
            return;
        }

        var dark = NoteStyling.IsDark;
        var ink = NoteStyling.ToColor(Sheet.Ink(dark));
        var paper = NoteStyling.ToColor(SelectionChrome.HandleFace(dark));

        layers.Face.Fill = new SolidColorBrush(paper);
        layers.Face.Stroke = new SolidColorBrush(AtAlpha(dark ? Colors.White : Colors.Black, ControlKit.RingAlpha));
        layers.Face.StrokeThickness = RailHairlinePx;
        layers.Hover.Fill = new SolidColorBrush(AtAlpha(ink, ControlKit.HoverAlpha));
        layers.Press.Fill = new SolidColorBrush(AtAlpha(ink, ControlKit.PressAlpha));
        layers.Ring.Stroke = new SolidColorBrush(AtAlpha(NoteStyling.ToColor(SelectionChrome.Ring(dark)), 0.9));
    }

    /// <summary>换主题时把三枚的纸面、发丝、状态层与晕影颜色一起跟上。</summary>
    private void PaintRailSurface()
    {
        if (_rail is null)
        {
            return;
        }

        _railSurfaceDark = NoteStyling.IsDark;

        foreach (var button in RailButtons())
        {
            PaintRailButton(button);
        }

        PaintRailHalos();
    }

    /// <summary>晕影是黑的一层，深底要更实——只在换主题时重画，不必每帧造刷子。</summary>
    private void PaintRailHalos()
    {
        var dark = NoteStyling.IsDark;

        foreach (var halo in _railHalos)
        {
            if (halo.Fill is RadialGradientBrush brush && brush.GradientStops.Count == 4)
            {
                brush.GradientStops[2].Color = AtAlpha(Colors.Black, dark ? ControlKit.VignetteAlphaDark : ControlKit.VignetteAlpha);
            }
        }
    }

    private static Color AtAlpha(Color color, double alpha)
        => Color.FromArgb((byte)System.Math.Round(Math.Clamp(alpha, 0, 1) * 255), color.R, color.G, color.B);

    /// <summary>把一层淡到某个不透明度。层的进出就是这枚钮的全部反馈语言。</summary>
    private static void LayerTo(Ellipse layer, double to, double ms)
    {
        var board = new Microsoft.UI.Xaml.Media.Animation.Storyboard();
        board.Children.Add(FadeTo(layer, to, ms));
        board.Begin();
    }

    private void RailHoverIn(Border button)
    {
        _railHover = button;

        if (LayersOf(button) is { } layers)
        {
            LayerTo(layers.Hover, 1, ControlKit.HoverInMs);
        }
    }

    private void RailHoverOut(Border button)
    {
        if (ReferenceEquals(_railHover, button))
        {
            _railHover = null;
        }

        if (LayersOf(button) is { } layers)
        {
            LayerTo(layers.Hover, 0, ControlKit.HoverOutMs);
            LayerTo(layers.Press, 0, ControlKit.HoverOutMs);
        }
    }

    /// <summary>手指压住的那一档：12% 的墨层 90ms 上来，抬起 250ms 退。</summary>
    private void RailPress(Border button, bool down)
    {
        if (LayersOf(button) is { } layers)
        {
            LayerTo(layers.Press, down ? 1 : 0, down ? ControlKit.PressMs : ControlKit.HoverOutMs);
        }
    }

    /// <summary>
    /// 操作条上有几枚钮，以那条 StackPanel 的孩子们为准——不另写一份名单。
    /// 10-24 他报"按钮直接消失了"：第四枚建出来了，但入场动画与重画各自拿一份写死的三枚名单，
    /// 于是它既没淡进画面（停在 Opacity 0）、也没刷过纸面色（连底圈都没有），看着就是凭空少一枚。
    /// </summary>
    private Border[] RailButtons()
        => _railHost?.Children.OfType<Border>().Where(button => button.Visibility == Visibility.Visible).ToArray() ?? [];

    /// <summary>设置里改了"左侧按钮"之后，把三枚静态钮的可见性同步过来并重新落位。</summary>
    private void ApplyRailVisibility()
    {
        _railGroup?.Visibility = _view.IsRailButtonHidden("group") ? Visibility.Collapsed : Visibility.Visible;
        _railColor?.Visibility = _view.IsRailButtonHidden("color") ? Visibility.Collapsed : Visibility.Visible;
        _railDelete?.Visibility = _view.IsRailButtonHidden("delete") ? Visibility.Collapsed : Visibility.Visible;

        if (_rail is not null)
        {
            RelayoutRail();
            EnsureRail();
        }
    }

    /// <summary>组钮的激活环（拖动中／菜单开着）。</summary>
    private void SetRailGroupActive(bool active)
    {
        _railGroupActive = active;

        if (_railGroup is not null && LayersOf(_railGroup) is { } layers)
        {
            LayerTo(layers.Ring, active ? 1 : 0, active ? ControlKit.PressMs : ControlKit.HoverOutMs);
        }
    }

    /// <summary>把布局坐标对齐到整枚设备像素（125%/150% 下也按物理像素算）。</summary>
    private double SnapToPixel(double value)
    {
        var scale = BoardArea.XamlRoot?.RasterizationScale ?? 1;

        if (scale <= 0)
        {
            scale = 1;
        }

        return Math.Round(value * scale) / scale;
    }

    // 三枚图标是"自己画线稿"那一发被否掉之后取回来的原样：虚线框＋实心块＝组、
    // 72 片色轮＝换色、系统字形 E74D＝删。留这一段记号，别再顺手重画一遍：
    // 10-24 我为此试过三版线稿（24 网格 1.5 描边、20 画布 2 像素带圆头、圆头补点），
    // 他的判语是"现在的图标还不如我们修改图标集之前的版本"。原因不在尺寸或粗细——
    // 色轮那一枚是**带颜色**的，任何线稿调色盘都没有颜色可看；垃圾桶那类通用符号
    // 本就该交给系统字体，它的字形是按字号调过 hinting 的。
    // 控件层（纸面圆盘、悬停/按压状态层与时长）保留，那部分他没说退。

    /// <summary>批量标签＝官方 Fluent 的 Tag 形状；不自绘。</summary>
    private static UIElement TagIcon()
    {
        var ink = NoteStyling.InkFor(NoteStyling.ToColor(SelectionChrome.HandleFace(NoteStyling.IsDark)));

        return FluentIcons.Glyph(FluentIcons.Tag, 18, ink)
            ?? new TextBlock
            {
                Text = "#",
                FontSize = 18,
                Foreground = new SolidColorBrush(ink),
            };
    }

    /// <summary>排列＝2×3 的方块矩阵：画的就是排完的样子（10-25 他选的 A 档）。</summary>
    private static UIElement ArrangeIcon()
    {
        var ink = new SolidColorBrush(NoteStyling.InkFor(NoteStyling.ToColor(SelectionChrome.HandleFace(NoteStyling.IsDark))));

        var canvas = new Canvas { Width = 20, Height = 20, IsHitTestVisible = false };

        foreach (var (x, y) in new[] { (3.0, 4.0), (8.0, 4.0), (13.0, 4.0), (3.0, 10.0), (8.0, 10.0), (13.0, 10.0) })
        {
            var cell = new Microsoft.UI.Xaml.Shapes.Rectangle
            {
                Width = 4,
                Height = 4,
                RadiusX = 1,
                RadiusY = 1,
                Fill = ink,
            };

            Canvas.SetLeft(cell, x);
            Canvas.SetTop(cell, y);
            canvas.Children.Add(cell);
        }

        return canvas;
    }

    private static UIElement GroupIcon()
    {
        var ink = new SolidColorBrush(NoteStyling.InkFor(NoteStyling.ToColor(SelectionChrome.HandleFace(NoteStyling.IsDark))));

        var grid = new Grid { Width = 20, Height = 20, IsHitTestVisible = false };

        grid.Children.Add(new Rectangle
        {
            Width = 15,
            Height = 15,
            RadiusX = 2.5,
            RadiusY = 2.5,
            Stroke = ink,
            StrokeThickness = 1.5,
            StrokeDashArray = [3, 2.2],
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        });

        grid.Children.Add(new Rectangle
        {
            Width = 7,
            Height = 7,
            RadiusX = 1.5,
            RadiusY = 1.5,
            Fill = ink,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        });

        return grid;
    }

    /// <summary>
    /// 换色钮：一枚自然晕染的色轮（色相绕圈、越靠圆心越淡到白）。WinUI 这版没有 ConicGradientBrush，
    /// 就用连续插值的扇形拼出色相环，再压一层"白心→透明"的径向渐变当饱和度衰减——
    /// 72 片在 22px 下接缝看不出来，读起来就是一张调色的色盘；最外一圈发丝把这圈色收干净。
    /// </summary>
    private static UIElement ColorWheelIcon()
    {
        var grid = new Grid { Width = 22, Height = 22, IsHitTestVisible = false };

        const double cx = 11, cy = 11, r = 10;
        const int slices = 72;
        var step = System.Math.PI * 2 / slices;

        for (var i = 0; i < slices; i++)
        {
            var a0 = i * step;
            var a1 = (i + 1) * step;
            var color = HsvToColor(360.0 * i / slices, 1, 1);

            var figure = new PathFigure
            {
                StartPoint = new Point(cx + (r * System.Math.Cos(a0)), cy + (r * System.Math.Sin(a0))),
                IsClosed = true,
            };

            figure.Segments.Add(new ArcSegment
            {
                Point = new Point(cx + (r * System.Math.Cos(a1)), cy + (r * System.Math.Sin(a1))),
                Size = new Size(r, r),
                SweepDirection = SweepDirection.Clockwise,
                IsLargeArc = false,
            });

            figure.Segments.Add(new LineSegment { Point = new Point(cx, cy) });

            var geometry = new PathGeometry();
            geometry.Figures.Add(figure);

            grid.Children.Add(new Microsoft.UI.Xaml.Shapes.Path { Data = geometry, Fill = new SolidColorBrush(color) });
        }

        grid.Children.Add(new Ellipse
        {
            Width = r * 2,
            Height = r * 2,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            IsHitTestVisible = false,
            Stroke = new SolidColorBrush(Color.FromArgb(30, 0, 0, 0)),
            StrokeThickness = 1,
        });

        grid.Children.Add(new Ellipse
        {
            Width = r * 2,
            Height = r * 2,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            IsHitTestVisible = false,
            Fill = new RadialGradientBrush
            {
                GradientOrigin = new Point(0.5, 0.5),
                Center = new Point(0.5, 0.5),
                RadiusX = 0.5,
                RadiusY = 0.5,
                GradientStops =
                {
                    new GradientStop { Color = Color.FromArgb(255, 255, 255, 255), Offset = 0 },
                    new GradientStop { Color = Color.FromArgb(0, 255, 255, 255), Offset = 0.72 },
                },
            },
        });

        return grid;
    }

    private static Color HsvToColor(double h, double s, double v)
    {
        var c = v * s;
        var x = c * (1 - System.Math.Abs(((h / 60) % 2) - 1));
        var m = v - c;

        var (r1, g1, b1) = h switch
        {
            < 60 => (c, x, 0.0),
            < 120 => (x, c, 0.0),
            < 180 => (0.0, c, x),
            < 240 => (0.0, x, c),
            < 300 => (x, 0.0, c),
            _ => (c, 0.0, x),
        };

        return Color.FromArgb(
            255,
            (byte)System.Math.Round((r1 + m) * 255),
            (byte)System.Math.Round((g1 + m) * 255),
            (byte)System.Math.Round((b1 + m) * 255));
    }

    /// <summary>
    /// 删＝比稿页 design/icons.html 上那枚垃圾桶，原样搬：盖、提手、身、两道竖，1.5 描边，
    /// <b>圆头线帽与圆角连接</b>。上一版"和稿不一样"就差在这两端是平的——不是形状选错了。
    /// 圆头只有 XAML 侧拿得到：这个投影的 C# 里那个枚举的命名空间看不见（两种全名都 CS0234），
    /// 而 XamlReader 按元数据解析，认。所以几何写成一段 path 串，代码只负责换墨色。
    /// </summary>
    private static UIElement TrashIcon()
    {
        var canvas = (Canvas)Microsoft.UI.Xaml.Markup.XamlReader.Load(
            "<Canvas xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation' Width='22' Height='22' IsHitTestVisible='False'>"
            + "<Path StrokeThickness='1.5' StrokeStartLineCap='Round' StrokeEndLineCap='Round' StrokeLineJoin='Round'"
            + " Data='M3,6.5 L19,6.5 M8,6.5 L8,4 L14,4 L14,6.5 M5,6.5 L6,19 L16,19 L17,6.5 M9,10.5 L9,15.5 M13,10.5 L13,15.5'/>"
            + "</Canvas>");

        foreach (var path in canvas.Children.OfType<Microsoft.UI.Xaml.Shapes.Path>())
        {
            path.Stroke = new SolidColorBrush(RailDangerInk);
        }

        return canvas;
    }

    /// <summary>
    /// 操作条那三枚的画法凭据：每枚都是"纸面＋悬停层＋按压层＋激活环"四层叠出来的，
    /// 静止时后三层必须全透明，两层的墨占比要等于令牌；删那枚必须是**圆头线帽**的 Path。
    /// </summary>
    /// <remarks>
    /// 10-24 这一发锁的是**控件层与端点**，不锁图形形状：图形他自己判过"自绘三版都不如原样"，
    /// 形状还会再改，锁形状等于锁一个他手里正在定的东西。但"实物和稿子不一样"这一格要钉住——
    /// 上一版差就差在稿子是圆头、实物是平头（1.5 描边两端各少 0.75 像素），所以这里读回
    /// StrokeEndLineCap 的实际值。三层顺序错了、静止时漏出一层、或谁把状态层改回"瞬间换背景色"，
    /// 这一发都会红。
    /// </remarks>
    private void SelfTestRailStateLayers(List<string> problems)
    {
        var probe = BoardNotes().Take(2).ToList();

        if (probe.Count < 2)
        {
            problems.Add("自检状态层: 板上不足两格，这一发不适用");

            return;
        }

        // 动态钮的出现条件：选中一格＝三枚；再加一格＝五枚（排列＋批量标签）。
        SelectOnly(probe[0].Id);
        ProjectSelection();

        var single = RailButtons();

        if (single.Length != 3 || single.Any(button => button.Name is "NoteRailArrange" or "NoteRailTag"))
        {
            problems.Add($"自检状态层: 选中 1 格时操作条应 3 枚、且没有排列/标签钮，读到 {single.Length} 枚");
        }

        _selection.Add(probe[1].Id);
        ProjectSelection();

        var buttons = RailButtons();

        if (buttons.Length != 5
            || buttons.All(button => button.Name != "NoteRailArrange")
            || buttons.All(button => button.Name != "NoteRailTag"))
        {
            problems.Add($"自检状态层: 选中 2 格时操作条应 5 枚、且含排列与标签钮，读到 {buttons.Length} 枚");
        }

        // 圆盘列的中心要落在选区中心上（四枚时原来会低 27px）。
        _rail?.UpdateLayout();

        if (SelectionBox() is { } chosen && buttons.Length > 0)
        {
            var top = double.MaxValue;
            var bottom = double.MinValue;

            foreach (var button in buttons)
            {
                var box = button.TransformToVisual(BoardArea)
                    .TransformBounds(new Rect(0, 0, button.ActualWidth, button.ActualHeight));
                top = Math.Min(top, box.Top);
                bottom = Math.Max(bottom, box.Bottom);
            }

            var wanted = _view.ToScreenY(chosen.Y + (chosen.Height / 2));
            var got = (top + bottom) / 2;

            // 五枚钮是一列很高的条：选区贴顶/贴底时它只能停进边界，中心天然对不上。
            // 只有没被边界夹住时，"中心＝选区中心"才是这条尺该要求的事。
            var railTop = _rail is null ? double.NaN : Canvas.GetTop(_rail);
            var railBottom = BoardArea.ActualHeight - RailShellHeight(buttons.Length) - 8;
            var clamped = Math.Abs(railTop - 8) < 0.5 || Math.Abs(railTop - railBottom) < 0.5;

            if (!clamped && Math.Abs(got - wanted) > 1.5)
            {
                problems.Add($"自检状态层: {buttons.Length} 枚钮的竖向中心 {got:0.#} 没对准选区中心 {wanted:0.#}（差 {got - wanted:0.#}）");
            }
        }

        // 每一枚都必须恒为可见：入场不再逐枚淡入（见 AnimateRailIn 那段说明），
        // 所以这里读到 0 就是有人把逐枚透明度加回来了——那正是"屏幕上凭空少一枚"的形状。
        foreach (var entry in buttons.Where(button => button.Opacity < 0.99))
        {
            problems.Add($"自检状态层: {entry.Name} 的透明度是 {entry.Opacity:0.##}——逐枚淡入会漏名单，屏幕上就是少一枚");
        }

        static string NameOf(Border button) => button.Name switch
        {
            "NoteRailGroup" => "组",
            "NoteRailColor" => "色",
            "NoteRailArrange" => "排列",
            "NoteRailTag" => "标签",
            _ => "删",
        };

        foreach (var button in buttons)
        {
            var name = NameOf(button);

            if (LayersOf(button) is not { } layers)
            {
                problems.Add($"自检状态层: {name} 那枚不是「纸面＋悬停＋按压＋环」四层这一形");

                continue;
            }

            if (layers.Face.Opacity < 0.99 || layers.Face.StrokeThickness <= 0)
            {
                problems.Add($"自检状态层: {name} 那枚的纸面不透明或没有发丝（面 {layers.Face.Opacity:0.##}、线 {layers.Face.StrokeThickness:0.##}）");
            }

            if (_railGroupActive)
            {
                continue;
            }

            if (layers.Hover.Opacity > 0.01 || layers.Press.Opacity > 0.01 || layers.Ring.Opacity > 0.01)
            {
                problems.Add(
                    $"自检状态层: {name} 那枚静止时还有层没退干净（悬 {layers.Hover.Opacity:0.##}、按 {layers.Press.Opacity:0.##}、环 {layers.Ring.Opacity:0.##}）");
            }

            ExpectAlpha(layers, name, "悬停", layers.Hover, ControlKit.HoverAlpha, problems);
            ExpectAlpha(layers, name, "按压", layers.Press, ControlKit.PressAlpha, problems);
        }

        SelfTestRailHalos(problems);

        var trash = (_railDelete?.Child as Grid)?.Children.OfType<Canvas>()
            .SelectMany(canvas => canvas.Children.OfType<Microsoft.UI.Xaml.Shapes.Path>()).ToList() ?? [];

        if (trash.Count == 0)
        {
            problems.Add("自检状态层: 删那枚里读不到 Path——垃圾桶不再是稿子上那一段几何");
        }
        else if (trash.Any(path => System.Convert.ToString(path.StrokeEndLineCap) != "Round" || System.Convert.ToString(path.StrokeLineJoin) != "Round"))
        {
            problems.Add("自检状态层: 删那枚的端点或拐角不是圆头（稿子上是 round，平头就是「和稿不一样」那一格）");
        }
        else if (trash.Any(path => Math.Abs(path.StrokeThickness - 1.5) > 0.001))
        {
            problems.Add($"自检状态层: 删那枚的描边是 {trash[0].StrokeThickness:0.###}，稿子上是 1.5");
        }

        Diagnostics.Write(
            $"selftest 状态层: 选中 1 格＝3 枚、选中 2 格＝5 枚（排列＋标签）；每枚都是纸面＋悬停 {ControlKit.HoverAlpha:0.###}＋按压 {ControlKit.PressAlpha:0.###}＋环，静止时后三层全透明；" +
            $"进 {ControlKit.HoverInMs:0}ms／退 {ControlKit.HoverOutMs:0}ms／按 {ControlKit.PressMs:0}ms；删那枚 Path 端点圆头、描边 1.5；晕影 {ControlKit.VignetteAlpha:0.##}／深底 {ControlKit.VignetteAlphaDark:0.##}，四圈都装在外壳里");

        ClearSelection();
        ProjectSelection();
    }

    private static void ExpectAlpha(
        (Ellipse Face, Ellipse Hover, Ellipse Press, Ellipse Ring) layers,
        string name,
        string which,
        Ellipse layer,
        double expected,
        List<string> problems)
    {
        if (layer.Fill is not SolidColorBrush brush)
        {
            problems.Add($"自检状态层: {name} 那枚的{which}层没有刷子");

            return;
        }

        var read = brush.Color.A / 255d;

        if (System.Math.Abs(read - expected) > 0.02)
        {
            problems.Add($"自检状态层: {name} 那枚的{which}层墨占比是 {read:0.###}，令牌给的是 {expected:0.###}");
        }
    }

    /// <summary>
    /// 晕影的凭据：每枚钮一圈（最多四圈），整圈都装在外壳里，影心比盘心低一个下压量。
    /// 10-24 第一发把 58 的影塞进 38 的格，排版把盘外那圈裁掉了——屏幕上就是右下角一块直边灰楔。
    /// 这里不比对像素，读的是变换后的实际边界：影的外接盒越出外壳，哪怕 1 像素，就是那一格回来了。
    /// </summary>
    private void SelfTestRailHalos(List<string> problems)
    {
        if (_rail is null || _railHalos.Count != RailMaxCells)
        {
            problems.Add($"自检状态层: 晕影读到 {_railHalos.Count} 圈，最多 {RailMaxCells} 枚钮要一圈一枚");

            return;
        }

        // 刚插/拔了一枚钮和改了外壳高度：先排一次版，下面的变换与 Actual* 才是这一帧的。
        var rail = _rail;
        rail.UpdateLayout();

        var cells = RailButtons();
        var visibleHalos = _railHalos.Count(halo => halo.Visibility == Visibility.Visible);

        if (visibleHalos != cells.Length)
        {
            problems.Add($"自检状态层: 亮着的晕影 {visibleHalos} 圈，钮 {cells.Length} 枚——多一圈少一圈都会露边");
        }

        // 排列/标签钮只在选中 ≥2 时出现；这条与钮数、影数一起钉。
        var wantArrange = SelectedNotes().Count >= 2;

        if ((cells.Any(button => button.Name == "NoteRailArrange")) != wantArrange)
        {
            problems.Add($"自检状态层: 选中 {SelectedNotes().Count} 格时，排列钮{(wantArrange ? "应该出现却没有" : "不该出现却在")}");
        }

        if ((cells.Any(button => button.Name == "NoteRailTag")) != wantArrange)
        {
            problems.Add($"自检状态层: 选中 {SelectedNotes().Count} 格时，标签钮{(wantArrange ? "应该出现却没有" : "不该出现却在")}");
        }

        if (cells.Length != (wantArrange ? 5 : 3))
        {
            problems.Add($"自检状态层: 操作条上有 {cells.Length} 枚钮（选中 {SelectedNotes().Count} 格）");
        }

        // 透明端必须是「黑且 alpha 0」。Colors.Transparent 是 #00FFFFFF（白透明），这个投影的
        // 渐变按非预乘插值，中途会掺灰——深底上实测 16→24，影反而比板面亮（10-24 抓到的）。
        foreach (var halo in _railHalos)
        {
            if (halo.Fill is not RadialGradientBrush brush || brush.GradientStops.Count != 4)
            {
                problems.Add("自检状态层: 晕影不是四停点的径向渐变");

                continue;
            }

            foreach (var stop in new[] { 0, 1, 3 })
            {
                var color = brush.GradientStops[stop].Color;

                if (color.A != 0 || color.R != 0 || color.G != 0 || color.B != 0)
                {
                    problems.Add(
                        $"自检状态层: 晕影第 {stop} 个停点是 #{color.A:x2}{color.R:x2}{color.G:x2}{color.B:x2}——" +
                        "透明端要用黑透明，白透明会在深底上发亮");
                }
            }

            var peak = brush.GradientStops[2].Color;

            if (peak.A == 0 || peak.R != 0 || peak.G != 0 || peak.B != 0)
            {
                problems.Add($"自检状态层: 晕影的峰值不是黑（#{peak.A:x2}{peak.R:x2}{peak.G:x2}{peak.B:x2}）");
            }
        }

        for (var index = 0; index < _railHalos.Count; index++)
        {
            var halo = _railHalos[index];

            if (halo.Visibility != Visibility.Visible)
            {
                continue;
            }

            var bounds = halo.TransformToVisual(rail).TransformBounds(new Rect(0, 0, halo.ActualWidth, halo.ActualHeight));

            if (bounds.X < -0.001 || bounds.Y < -0.001
                || bounds.Right > rail.ActualWidth + 0.001 || bounds.Bottom > rail.ActualHeight + 0.001)
            {
                problems.Add(
                    $"自检状态层: 第 {index + 1} 圈晕影 ({bounds.X:0.#},{bounds.Y:0.#})–({bounds.Right:0.#},{bounds.Bottom:0.#}) " +
                    $"越出外壳 {rail.ActualWidth:0.#}×{rail.ActualHeight:0.#}——盘外那圈会被裁成直边");
            }

            if (index >= cells.Length || LayersOf(cells[index])?.Face is not { } disc)
            {
                continue;
            }

            var face = disc.TransformToVisual(_rail).TransformBounds(new Rect(0, 0, disc.ActualWidth, disc.ActualHeight));

            // 入场那一手每枚钮还在横移（8→0）；影不跟着滑，量的是落定之后的对位。
            var slide = (cells[index].RenderTransform as TranslateTransform)?.X ?? 0;
            var dx = System.Math.Abs((bounds.X + bounds.Width / 2) + slide - (face.X + face.Width / 2));
            var dy = (bounds.Y + bounds.Height / 2) - (face.Y + face.Height / 2);

            if (dx > 0.5 || System.Math.Abs(dy - ControlKit.VignetteDropPx) > 0.5)
            {
                problems.Add(
                    $"自检状态层: 第 {index + 1} 圈晕影没对准它的钮（横偏 {dx:0.##}、下压 {dy:0.##}，令牌给 {ControlKit.VignetteDropPx:0.##}）");
            }
        }
    }


    private void AnimateRailIn()
    {
        var host = _railHost!;
        var buttons = RailButtons();

        // "看得见"不再挂在逐枚的淡入名单上：整条一起淡，逐枚只错峰平移。
        // 原来是每枚各自 Opacity 0→1，名单漏了谁，屏幕上就凭空少一枚——10-24 他连报两次
        // "按钮直接消失了"，第一次我还拿自检读数当"修好了"。漏名单现在最多是少一段错峰。
        foreach (var button in buttons)
        {
            button.Opacity = 1;
            button.RenderTransform = new TranslateTransform { X = _capturingShot ? 0 : 8 };
        }

        _rail!.Opacity = 1;

        if (_capturingShot)
        {
            host.Opacity = 1;

            return;
        }

        host.Opacity = 0;

        var appear = new Microsoft.UI.Xaml.Media.Animation.Storyboard();
        appear.Children.Add(FadeTo(host, 1, Motion.HoverMs));

        for (var i = 0; i < buttons.Length; i++)
        {
            var move = MoveX((TranslateTransform)buttons[i].RenderTransform, 8, 0, Motion.HoverMs);

            move.BeginTime = TimeSpan.FromMilliseconds(i * 35);
            appear.Children.Add(move);
        }

        appear.Begin();
    }

    private static Microsoft.UI.Xaml.Media.Animation.DoubleAnimation FadeTo(UIElement target, double to, double ms)
    {
        var animation = new Microsoft.UI.Xaml.Media.Animation.DoubleAnimation
        {
            To = to,
            Duration = TimeSpan.FromMilliseconds(ms),
            EasingFunction = new Microsoft.UI.Xaml.Media.Animation.CubicEase { EasingMode = Microsoft.UI.Xaml.Media.Animation.EasingMode.EaseOut },
        };

        Microsoft.UI.Xaml.Media.Animation.Storyboard.SetTarget(animation, target);
        Microsoft.UI.Xaml.Media.Animation.Storyboard.SetTargetProperty(animation, "Opacity");

        return animation;
    }

    private static Microsoft.UI.Xaml.Media.Animation.DoubleAnimation MoveX(TranslateTransform transform, double from, double to, double ms)
    {
        var animation = new Microsoft.UI.Xaml.Media.Animation.DoubleAnimation
        {
            From = from,
            To = to,
            Duration = TimeSpan.FromMilliseconds(ms),
            EasingFunction = new Microsoft.UI.Xaml.Media.Animation.CubicEase { EasingMode = Microsoft.UI.Xaml.Media.Animation.EasingMode.EaseOut },
        };

        Microsoft.UI.Xaml.Media.Animation.Storyboard.SetTarget(animation, transform);
        Microsoft.UI.Xaml.Media.Animation.Storyboard.SetTargetProperty(animation, "X");

        return animation;
    }

    // ---------------------------------------------------------------------
    // 组钮：轻点＝菜单，按住拖＝分组模式
    // ---------------------------------------------------------------------

    /// <summary>
    /// 排列钮是第四枚、只在选中多枚时出现：插在"换色"和"删"之间，外壳/影层的高度跟着重算。
    /// 没有动画——一条已经在屏幕上的操作条里多一枚，突然出现比淡入更不容易被错过。
    /// </summary>
    private void SyncRailTag(bool show)
    {
        if (_railHost is null || _railTag is null || _rail is null)
        {
            return;
        }

        var present = _railHost.Children.Contains(_railTag);

        if (present == show)
        {
            return;
        }

        if (show)
        {
            _railTag.Opacity = 1;
            _railTag.RenderTransform = new TranslateTransform { X = 0 };

            // 排在排列钮后面、删除钮前面；排列钮是先插的，所以标签固定落在第 3 格。
            _railHost.Children.Insert(Math.Min(3, _railHost.Children.Count), _railTag);
        }
        else
        {
            _railHost.Children.Remove(_railTag);
        }

        RelayoutRail();
    }

    private void SyncRailArrange(bool show)
    {
        if (_railHost is null || _railArrange is null || _rail is null)
        {
            return;
        }

        var present = _railHost.Children.Contains(_railArrange);

        if (present == show)
        {
            return;
        }

        if (show)
        {
            _railArrange.Opacity = 1;
            _railArrange.RenderTransform = new TranslateTransform { X = 0 };
            _railHost.Children.Insert(2, _railArrange);
        }
        else
        {
            _railHost.Children.Remove(_railArrange);
        }

        RelayoutRail();
    }

    /// <summary>钮数变了：外壳、网格、影层的盒子一起改；多出来的影藏起来。</summary>
    private void RelayoutRail()
    {
        var cells = RailButtons().Length;
        var height = RailShellHeight(cells);

        if (_rail is not null)
        {
            _rail.Height = height;
        }

        if (_railStack is not null)
        {
            _railStack.Height = height;
        }

        if (_railHaloCanvas is not null)
        {
            _railHaloCanvas.Height = height;
        }

        for (var index = 0; index < _railHalos.Count; index++)
        {
            _railHalos[index].Visibility = index < cells ? Visibility.Visible : Visibility.Collapsed;
        }
    }

    private void WireRailButtons()
    {
        var group = _railGroup!;

        group.PointerPressed += (_, e) =>
        {
            RailPress(group, down: true);

            // 记下整组选中的起手坐标（单选就一枚，多选全带上）——组钮那一条现在也能拖多枚。
            var positions = new Dictionary<string, (double X, double Y)>(StringComparer.Ordinal);

            foreach (var selectedId in _selection)
            {
                if (NodeOf(selectedId) is { } selected && selected.Kind != CanvasNodeKind.Group)
                {
                    positions[selected.Id] = (selected.X, selected.Y);
                }
            }

            if (positions.Count == 0 || NodeOf(positions.Keys.First()) is not { } first)
            {
                return;
            }

            _railDrag = new RailDrag
            {
                NodeId = first.Id,
                StartPointer = e.GetCurrentPoint(BoardArea).Position,
                StartX = first.X,
                StartY = first.Y,
                StartPositions = positions,
                StartGroupId = Groups().GroupOf(RectOf(first))?.Id,
                StartRosters = RosterSnapshot(),
            };

            group.CapturePointer(e.Pointer);
            CloseGroupMenu();
            e.Handled = true;
        };

        group.PointerMoved += (_, e) =>
        {
            if (_railDrag is not { } state)
            {
                return;
            }

            var screen = e.GetCurrentPoint(BoardArea).Position;

            if (!state.Started)
            {
                if (Math.Abs(screen.X - state.StartPointer.X) + Math.Abs(screen.Y - state.StartPointer.Y) <= 6)
                {
                    return;
                }

                state.Started = true;
                BeginGroupDrag(state, screen);
            }

            MoveGroupDrag(state, screen);
            e.Handled = true;
        };

        group.PointerReleased += (_, e) =>
        {
            RailPress(group, down: false);

            if (_railDrag is not { } state)
            {
                return;
            }

            group.ReleasePointerCapture(e.Pointer);
            _railDrag = null;

            if (state.Started)
            {
                EndGroupDrag(state);
            }
            else if (SelectedNotes().Count >= 2)
            {
                // 多选时单击＝成组/并入/解散，和 Ctrl＋G 同一条；长按拖走的是上面那条 Started。
                GroupOrDissolve();
            }
            else
            {
                ShowGroupMenu();
            }

            e.Handled = true;
        };

        // 拖入只有"正常松手"一条出口会清警示态，是他 10-31 报的蓝框卡死的根子：指针一旦被
        // 打断（系统取消触摸、窗口失活收回捕获），Released 不来，最后压着的那枚框就永远亮着。
        // 取消与捕获丢失都走同一条收尾：手势拆掉、全部框的蓝/红回到虚线。
        group.PointerCanceled += (_, e) =>
        {
            RailPress(group, down: false);
            AbortGroupDrag();
            e.Handled = true;
        };

        group.PointerCaptureLost += (_, _) => AbortGroupDrag();

        _railColor!.PointerPressed += (_, e) =>
        {
            RailPress(_railColor, down: true);
            ToggleColorPopover();
            e.Handled = true;
        };

        _railColor.PointerReleased += (_, e) =>
        {
            RailPress(_railColor, down: false);
        };

        _railArrange!.PointerPressed += (_, e) =>
        {
            RailPress(_railArrange, down: true);
            ShowArrangeMenu();
            e.Handled = true;
        };

        _railArrange.PointerReleased += (_, e) =>
        {
            RailPress(_railArrange, down: false);
        };

        _railTag!.PointerPressed += (_, e) =>
        {
            RailPress(_railTag, down: true);
            ShowTagMenu();
            e.Handled = true;
        };

        _railTag.PointerReleased += (_, e) =>
        {
            RailPress(_railTag, down: false);
        };

        _railDelete!.PointerPressed += (_, e) =>
        {
            RailPress(_railDelete, down: true);
            DeleteSelectedWithFade();
            e.Handled = true;
        };

        _railDelete.PointerReleased += (_, e) =>
        {
            RailPress(_railDelete, down: false);
        };

        foreach (var button in new[] { _railGroup!, _railColor!, _railArrange!, _railTag!, _railDelete! })
        {
            var target = button;

            target.PointerEntered += (_, _) => RailHoverIn(target);

            target.PointerExited += (_, _) => RailHoverOut(target);
        }
    }

    private void BeginGroupDrag(RailDrag state, Point screen)
    {
        if (NodeOf(state.NodeId) is not { } node)
        {
            return;
        }

        BeginGesture(state.StartPositions.Count > 1 ? $"把 {state.StartPositions.Count} 格拖进／拖出组" : "把这格拖进／拖出组");

        _nodeDragStarted = false;

        _nodeGesture = new NodeGesture(
            _view.ToWorldX(screen.X),
            _view.ToWorldY(screen.Y),
            new Dictionary<string, (double X, double Y)>(state.StartPositions, StringComparer.Ordinal),
            node.Id,
            GroupMode: true,
            StartGroupId: state.StartGroupId,
            StartMembers: null);

        SetRailGroupActive(true);

        _tether ??= new Ellipse
        {
            Width = 14,
            Height = 14,
            StrokeThickness = 1.5,
            IsHitTestVisible = false,
            Name = "NoteTether",
        };

        if (_tether.Parent is null)
        {
            HandleLayer.Children.Add(_tether);
        }

        _tether.Visibility = Visibility.Visible;
    }

    private void MoveGroupDrag(RailDrag state, Point screen)
    {
        if (_nodeGesture is null)
        {
            return;
        }

        ApplyNodeDrag(screen);
        UpdateGroupAlerts(screen);
    }

    private void UpdateGroupAlerts(Point screen)
    {
        if (_nodeGesture is not { GroupMode: true } gesture)
        {
            return;
        }

        var dragged = gesture.StartGeometry.Keys
            .Select(id => NodeOf(id))
            .Where(n => n is not null)
            .Select(n => RectOf(n!))
            .ToList();

        if (dragged.Count == 0)
        {
            return;
        }

        // 多枚一起拖时，用它们的外接矩形去碰框（一角碰上就算），单选时它就等于那一枚。
        var rect = new Grouping.Rect(
            dragged[0].Id,
            dragged.Min(r => r.X),
            dragged.Min(r => r.Y),
            dragged.Max(r => r.Right) - dragged.Min(r => r.X),
            dragged.Max(r => r.Bottom) - dragged.Min(r => r.Y));

        string? alertId = null;
        string? kind = null;

        // 激活阈值是"一角碰上"（矩形相交），不是"中心进框"——后者要把格子拖进去半截才触发，
        // 松手时已经压住框里原有的格子，得再手动挪开（他 10-22 要改的就是这一格）。
        foreach (var frame in GroupRects())
        {
            if (frame.Id == gesture.StartGroupId)
            {
                continue;
            }

            if (frame.Intersects(rect))
            {
                alertId = frame.Id;
                kind = "in";
                break;
            }
        }

        if (kind is null && gesture.StartGroupId is { } startGroupId)
        {
            foreach (var frame in GroupRects())
            {
                if (frame.Id == startGroupId && !frame.Intersects(rect))
                {
                    alertId = startGroupId;
                    kind = "out";
                }
            }
        }

        foreach (var frame in GroupRects())
        {
            if (_realized.TryGetValue(frame.Id, out var visual))
            {
                visual.SetGroupAlert(frame.Id == alertId ? kind : null);
            }
        }

        if (_tether is not null)
        {
            var ring = kind == "out"
                ? Color.FromArgb(140, 224, 49, 49)
                : Color.FromArgb(140, 49, 130, 237);
            var wash = kind == "out"
                ? Color.FromArgb(16, 224, 49, 49)
                : Color.FromArgb(20, 49, 130, 237);

            _tether.Stroke = new SolidColorBrush(ring);
            _tether.Fill = new SolidColorBrush(wash);
            Canvas.SetLeft(_tether, screen.X - 7);
            Canvas.SetTop(_tether, screen.Y - 7);
        }
    }

    /// <summary>把组警示态全部收掉（拖进＝蓝、拖出＝红、光标环、组钮激活环）：
    /// 正常松手与被打断的出口共用这一把尺，谁也不许留下亮着的框。</summary>
    private void ClearGroupAlerts()
    {
        foreach (var frame in GroupRects())
        {
            if (_realized.TryGetValue(frame.Id, out var visual))
            {
                visual.SetGroupAlert(null);
            }
        }

        if (_tether is not null)
        {
            _tether.Visibility = Visibility.Collapsed;
        }

        SetRailGroupActive(false);
    }

    /// <summary>拖入被打断（指针取消／捕获丢失）：手势当场拆掉，警示态一发清干净。
    /// 没有这条出口，最后压着的那枚框会一直亮到他重启窗口（他 10-31 报的蓝框卡死）。</summary>
    private void AbortGroupDrag()
    {
        if (_railDrag is null && _nodeGesture is not { GroupMode: true })
        {
            return;
        }

        _railDrag = null;
        _nodeGesture = null;
        DrawGuides([]);
        EndGesture();
        ProjectSelection();
        ClearGroupAlerts();

        Diagnostics.Write("group drag: 指针被打断（取消或捕获丢失），手势拆除、警示态全清");
    }

    private void EndGroupDrag(RailDrag state)
    {
        // 松手那一拍重新问一次"现在跟哪个框碰上了"——不依赖拖动中途缓存的末态：末态只在指针
        // 还在动时刷新，拖到位后停一下再松手会漏掉，那一趟角就白碰了（他报的"只有一角移入没
        // 生效，还是只有几何中心移入才入组"就是这个）。判定抽在 JoinByGrowingFrame。
        var joined = JoinByGrowingFrame(state.StartPositions.Keys.ToList(), state.StartGroupId);

        // 入组／出组写的是名单（普通拖动不走这一句：那是挪位置，不是改关系）。
        WriteRostersAfterGroupDrag(state);

        _nodeGesture = null;
        DrawGuides([]);
        EndGesture();
        SaveSoon();
        ArrangeAll();
        ProjectSelection();
        ClearGroupAlerts();

        if (NodeOf(state.NodeId) is not { } node)
        {
            return;
        }

        var now = Groups().GroupOf(RectOf(node))?.Id;

        static string GroupName(string? id, Func<string, CanvasNode?> find)
        {
            var label = id is null ? null : find(id)?.Label;

            return string.IsNullOrWhiteSpace(label) ? "未命名组" : label;
        }

        if (joined || (now is not null && now != state.StartGroupId))
        {
            Say($"已加入组“{GroupName(now, NodeOf)}”");
        }
        else if (state.StartGroupId is not null && now is null)
        {
            Say($"已从组“{GroupName(state.StartGroupId, NodeOf)}”移出");
        }
        else
        {
            Say("分组未变更");
        }

        Diagnostics.Write($"group drag: {state.NodeId} {state.StartGroupId ?? "(散)"} → {now ?? "(散)"}，入组 {joined}");
    }

    /// <summary>
    /// 松手那一拍的判定：这格现在和哪个框碰上了（一角即可，跳过它原本就在的那个框）？
    /// 碰上就把那个框<b>长过去</b>罩住整格——格子留在松手处不动，只动虚线框（他 10-22 的更正：
    /// "松手之后便签的位置会变，有问题，应该只是组的虚线边框范围变化"）。框长到覆盖整格后，
    /// 这格的中心自然落进新框里，"成员按中心"那条规矩在下一拍重算时仍然自洽。返回是否入了某框。
    /// </summary>
    private bool JoinByGrowingFrame(IReadOnlyList<string> draggedIds, string? startGroupId)
    {
        // 并集下不准"长框罩住"：那会跨文件改某块板的组框。分组只在各子页里做。
        if (IsAllNotes(_boardPath))
        {
            return false;
        }

        var dragged = draggedIds
            .Select(id => NodeOf(id))
            .Where(n => n is not null)
            .Select(n => RectOf(n!))
            .ToList();

        if (dragged.Count == 0)
        {
            return false;
        }

        var draggedSet = new HashSet<string>(draggedIds, StringComparer.Ordinal);
        var left = dragged.Min(r => r.X);
        var top = dragged.Min(r => r.Y);
        var union = new Grouping.Rect(dragged[0].Id, left, top, dragged.Max(r => r.Right) - left, dragged.Max(r => r.Bottom) - top);

        foreach (var frame in GroupRects())
        {
            if (frame.Id == startGroupId)
            {
                continue;
            }

            if (!frame.Intersects(union))
            {
                continue;
            }

            // 目标框现在那些成员按名单取（没有名单的框才退回几何）。
            var members = Groups().Members(frame.Id).Where(member => !draggedSet.Contains(member.Id)).ToList();

            members.AddRange(dragged);

            var grown = Grouping.Enclose(frame.Id, members);

            if (NodeOf(frame.Id) is { } frameNode)
            {
                frameNode.X = grown.X;
                frameNode.Y = grown.Y;
                frameNode.Width = grown.Width;
                frameNode.Height = grown.Height;
            }

            Diagnostics.Write($"join grow: {dragged.Count} 格一角碰上 {frame.Id} → 框长到 {grown.Width:0.#}×{grown.Height:0.#}（格子不动）");

            return true;
        }

        return false;
    }

    // ---------------------------------------------------------------------
    // 组钮菜单：创建分组 / 移出分组
    // ---------------------------------------------------------------------

    private MenuFlyout? _groupMenu;

    private void ShowGroupMenu()
    {
        if (_selection.Count == 0)
        {
            return;
        }

        var menu = new MenuFlyout();

        // 成组：全散＝圈成一个新组；混选＝把没成组的并进已有的那个组（10-24 与 Ctrl＋G 同一条语义）。
        // 单枚也能成组（10-22 他要的）。要并进某个具体的框，还可以走组钮按住拖那一条。
        var create = new MenuFlyoutItem
        {
            Text = SelectedNotes().Count == 1 ? "成组（这一格）" : $"成组（这 {SelectedNotes().Count} 格）",
            Icon = DotIcon(new SolidColorBrush(Color.FromArgb(200, 150, 158, 166))),
        };
        create.IsEnabled = SelectedNotes().Count >= 1;
        create.Click += (_, _) => GroupSelection();
        menu.Items.Add(create);

        var leave = new MenuFlyoutItem { Text = "移出分组", Icon = DotIcon(ChromeStyling.Snap) };
        var inGroup = SelectedNotes().Where(n => Groups().GroupOf(RectOf(n)) is not null).ToList();
        leave.IsEnabled = inGroup.Count >= 1;
        leave.Click += (_, _) =>
        {
            foreach (var n in inGroup)
            {
                DetachFromGroup(n.Id);
            }
        };
        menu.Items.Add(leave);

        _groupMenu = menu;
        menu.Closed += (_, _) =>
        {
            if (ReferenceEquals(_groupMenu, menu))
            {
                _groupMenu = null;

                if (_railGroup is not null)
                {
                    SetRailGroupActive(false);
                }
            }
        };

        if (_railGroup is not null)
        {
            SetRailGroupActive(true);
        }

        menu.ShowAt(_railGroup!, new FlyoutShowOptions { Placement = FlyoutPlacementMode.RightEdgeAlignedTop });
    }

    private void CloseGroupMenu()
    {
        _groupMenu?.Hide();
        _groupMenu = null;
    }

    // ---------------------------------------------------------------------
    // 排列钮菜单：按颜色 / 按标签（10-25 他："得能选怎么排"）
    // ---------------------------------------------------------------------

    private MenuFlyout? _arrangeMenu;

    /// <summary>两行：颜色还是标签。拆出来，自检能直接读（不用真弹一次）。</summary>
    private MenuFlyout BuildArrangeMenu()
    {
        var menu = new MenuFlyout();

        var byColor = new MenuFlyoutItem { Text = "按颜色排列" };
        byColor.Click += (_, _) => ArrangeByKey("按颜色排列", node => node.Color ?? "无颜色");
        menu.Items.Add(byColor);

        var byTag = new MenuFlyoutItem { Text = "按标签排列" };
        byTag.Click += (_, _) => ArrangeByKey("按标签排列", node => Tags.Of(node.Text).FirstOrDefault() ?? "#无标签", "#无标签");
        menu.Items.Add(byTag);

        var bySequence = new MenuFlyoutItem { Text = "按序号排列" };
        bySequence.Click += (_, _) => ArrangeBySequence("按序号排列");
        menu.Items.Add(bySequence);

        return menu;
    }

    private void ShowArrangeMenu()
    {
        if (_railArrange is null)
        {
            return;
        }

        var menu = BuildArrangeMenu();

        _arrangeMenu = menu;
        menu.Closed += (_, _) =>
        {
            if (ReferenceEquals(_arrangeMenu, menu))
            {
                _arrangeMenu = null;
            }
        };

        menu.ShowAt(_railArrange, new FlyoutShowOptions { Placement = FlyoutPlacementMode.RightEdgeAlignedTop });
    }

    private void CloseArrangeMenu()
    {
        _arrangeMenu?.Hide();
        _arrangeMenu = null;
    }

    // ---------------------------------------------------------------------
    // 批量加标签：多选时多一枚钮，菜单里列出板上已有的标签，也可以新增
    // ---------------------------------------------------------------------

    private MenuFlyout? _tagMenu;

    private void ShowTagMenu()
    {
        if (_railTag is null || _selection.Count < 2)
        {
            return;
        }

        var menu = new MenuFlyout();
        var tags = BoardNotes()
            .SelectMany(node => Tags.Of(node.Text))
            .Distinct(StringComparer.Ordinal)
            .OrderBy(tag => tag, StringComparer.Ordinal)
            .ToList();

        foreach (var tag in tags)
        {
            var item = new MenuFlyoutItem { Text = tag };
            item.Click += (_, _) => ApplyTagToSelected(tag);
            menu.Items.Add(item);
        }

        if (tags.Count > 0)
        {
            menu.Items.Add(new MenuFlyoutSeparator());
        }

        var add = new MenuFlyoutItem { Text = "新增标签…" };
        add.Click += (_, _) => _ = PromptNewTagAsync();
        menu.Items.Add(add);

        _tagMenu = menu;
        menu.Closed += (_, _) =>
        {
            if (ReferenceEquals(_tagMenu, menu))
            {
                _tagMenu = null;
            }
        };

        menu.ShowAt(_railTag, new FlyoutShowOptions { Placement = FlyoutPlacementMode.RightEdgeAlignedTop });
    }

    private void CloseTagMenu()
    {
        _tagMenu?.Hide();
        _tagMenu = null;
    }

    private async Task PromptNewTagAsync()
    {
        var box = new TextBox
        {
            PlaceholderText = "标签名（不用带 # 或 @t）",
            FontSize = 14,
        };

        var dialog = new ContentDialog
        {
            Title = "新增标签",
            Content = box,
            PrimaryButtonText = "加到选中的便签",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = Root.XamlRoot,
        };

        if (await dialog.ShowAsync() != ContentDialogResult.Primary)
        {
            return;
        }

        var name = box.Text.Trim();

        if (name.Length == 0)
        {
            Say("标签名不能为空");

            return;
        }

        if (name.Any(char.IsWhiteSpace))
        {
            Say("标签名里不能有空格；一个标签是 @t 后面的一整段");

            return;
        }

        ApplyTagToSelected(name);
    }

    /// <summary>批量标签的写分支：两格都真的多出牌子、正文没动；撤销以后逐字回到原样。</summary>
    private void SelfTestBatchTag(List<string> problems)
    {
        var notes = BoardNotes()
            .Where(node => node.Kind == CanvasNodeKind.Text && !EntityCards.IsTable(node))
            .Take(2)
            .ToList();

        if (notes.Count < 2)
        {
            problems.Add("自检批量标签: 板上不足两格文字便签");

            return;
        }

        var before = notes.ToDictionary(node => node.Id, node => node.Text ?? string.Empty, StringComparer.Ordinal);

        _selection.Clear();

        foreach (var node in notes)
        {
            _selection.Add(node.Id);
        }

        ProjectSelection();
        ApplyTagToSelected("批量自检");

        foreach (var id in before.Keys)
        {
            if (NodeOf(id) is not { } after)
            {
                problems.Add($"自检批量标签: {id} 加标签后不见了");

                continue;
            }

            var marks = Tags.Read(after.Text);

            if (!marks.Tags.Contains("#批量自检", StringComparer.Ordinal))
            {
                problems.Add($"自检批量标签: {id} 没读到 #批量自检");
            }

            if (marks.Body != Tags.Read(before[id]).Body)
            {
                problems.Add($"自检批量标签: {id} 的正文被改了");
            }

            if (string.Equals(after.Text, before[id], StringComparison.Ordinal))
            {
                problems.Add($"自检批量标签: {id} 的文本根本没变");
            }

            if (!_realized.TryGetValue(id, out var card) || !card.PaintsReadout)
            {
                problems.Add($"自检批量标签: {id} 加了标签但卡片上没画出牌子");
            }
        }

        Undo();

        foreach (var (id, text) in before)
        {
            if (NodeOf(id) is { } after && !string.Equals(after.Text, text, StringComparison.Ordinal))
            {
                problems.Add($"自检批量标签: {id} 撤销以后没回到原样");
            }
        }

        ClearSelection();
        ProjectSelection();
    }

    private void ApplyTagToSelected(string tag)
    {
        var notes = SelectedNotes()
            .Where(node => node.Kind == CanvasNodeKind.Text && !EntityCards.IsTable(node))
            .ToList();

        if (notes.Count == 0)
        {
            Say("先选中便签，再加标签");

            return;
        }

        BeginGesture("批量加标签");

        foreach (var node in notes)
        {
            var heightBefore = node.Height;

            node.Text = Tags.AddTag(node.Text, tag);

            // 新牌子也要有地方站：自动高度的便签按"正文＋牌子带"重新量一次高。
            // 只许长、不许缩——这一步只加牌子，正文没变；量矮了会把已有的牌子挤出卡外
            // （自检里空正文那格就是从 80 被量成 44，牌子当场不画了）。
            FitNoteHeight(node);

            if (node.Height < heightBefore)
            {
                node.Height = heightBefore;
            }
        }

        EndGesture();

        Rebuild();
        ClearSelection();
        ProjectSelection();

        // 标签是正文改动：立刻落盘，别等 0.5 秒定时器——关窗/切板时那一发可能还没到。
        SaveNow();

        var shown = tag.Trim();

        if (shown.StartsWith('#'))
        {
            shown = shown[1..];
        }
        else if (shown.StartsWith("@t", StringComparison.OrdinalIgnoreCase))
        {
            shown = shown[2..];
        }

        Say($"已给 {notes.Count} 张便签加上 {shown}");
    }

    private static IconElement DotIcon(Brush brush)
        => new PathIcon { Data = new EllipseGeometry { Center = new Point(6, 6), RadiusX = 4.5, RadiusY = 4.5 }, Foreground = brush, Width = 12, Height = 12 };

    // ---------------------------------------------------------------------
    // 换色：操作条左侧弹一列圆角色板（直接显示颜色，不是色名）
    // ---------------------------------------------------------------------

    // 预设号 1–6 加"无色"，就是色板从上往下那一列；每颗涂的是 NoteStyling 现量出的那格底色。
    private static readonly string?[] ColourSwatches = [null, "1", "2", "3", "4", "5", "6"];

    private void ToggleColorPopover()
    {
        if (_colorOpen)
        {
            CloseColorPopover();
        }
        else
        {
            OpenColorPopover();
        }
    }

    private void OpenColorPopover()
    {
        if (_railColor is null)
        {
            return;
        }

        _colorPopover ??= new Border { Name = "NoteColorPopover", IsHitTestVisible = true };

        var column = new StackPanel { Spacing = 8 };

        foreach (var preset in ColourSwatches)
        {
            column.Children.Add(SwatchButton(preset));
        }

        _colorPopover.Child = column;

        if (_colorPopover.Parent is null)
        {
            HandleLayer.Children.Add(_colorPopover);
        }

        PlaceColorPopover();

        // 上一次淡出可能停在 0：先抬回满不透明，再让每一颗自己错峰滑进来。
        _colorPopover.Opacity = 1;
        _colorPopover.Visibility = Visibility.Visible;
        _colorOpen = true;

        AnimateColorPopoverIn();
    }

    private void PlaceColorPopover()
    {
        if (_colorPopover is null)
        {
            return;
        }

        const double swatch = 30;
        const double gap = 12;
        var height = ColourSwatches.Length * swatch + (ColourSwatches.Length - 1) * 8;

        // 贴在那列圆盘的旁边、离便签远的那一侧；贴不下就翻到另一侧。
        // 外壳比圆盘多出左右各一圈晕影，这里要按盘沿算，不能按外壳边算。
        var discLeft = _railFlipped ? _railLeft + RailShellWidthPx - ControlKit.VignetteSpreadPx : _railLeft + ControlKit.VignetteSpreadPx;
        var left = _railFlipped ? discLeft + gap : discLeft - swatch - gap;

        if (left < 4)
        {
            left = discLeft + RailButtonPx + gap;
        }
        else if (left + swatch > BoardArea.ActualWidth - 4)
        {
            left = discLeft - swatch - gap;
        }

        // 色板列的中心对齐换色钮那颗（第 2 枚：外壳顶 8＋半枚 19＋一格 48 = 列顶 +75）。
        var top = Math.Clamp(
            _railTop + RailTopInsetPx + (RailButtonPx / 2) + RailPitchPx - height / 2,
            8,
            Math.Max(8, BoardArea.ActualHeight - height - 8));

        Canvas.SetLeft(_colorPopover, SnapToPixel(left));
        Canvas.SetTop(_colorPopover, SnapToPixel(top));
    }

    private Border SwatchButton(string? preset)
    {
        var surface = NoteStyling.SurfaceFor(preset);
        var line = NoteStyling.LineFor(preset, surface);

        var circle = new Border
        {
            Name = preset is null ? "NoteSwatchNone" : $"NoteSwatch{preset}",
            Width = swatchSize,
            Height = swatchSize,
            CornerRadius = new CornerRadius(swatchSize / 2),
            Background = new SolidColorBrush(surface),
            BorderBrush = new SolidColorBrush(line),
            BorderThickness = new Thickness(1.4),
            RenderTransform = new TranslateTransform { X = 8 },
            Opacity = 0,
        };

        if (preset is null)
        {
            // 无色那枚：一圈虚线，读作"空的／摘掉颜色"——跟组框的虚线同一语言，
            // 比一道黑杠干净（10-22 他嫌那道黑线怪）。
            circle.Child = new Ellipse
            {
                Width = 20,
                Height = 20,
                Stroke = new SolidColorBrush(NoteStyling.FallbackLine()),
                StrokeThickness = 1.4,
                StrokeDashArray = [2.2, 1.8],
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            };
        }

        var captured = preset;

        circle.PointerPressed += (_, e) =>
        {
            ApplyColor(captured);
            CloseColorPopover();
            Say(captured is null ? "已移除颜色" : "已更改颜色");
            e.Handled = true;
        };

        return circle;
    }

    private const double swatchSize = 30;

    private void AnimateColorPopoverIn()
    {
        if (_colorPopover?.Child is not StackPanel column)
        {
            return;
        }

        for (var i = 0; i < column.Children.Count; i++)
        {
            var item = column.Children[i];
            var slide = new TranslateTransform { X = 8 };

            item.RenderTransform = slide;
            item.Opacity = 0;

            var fade = FadeTo(item, 1, Motion.HoverMs);
            var move = MoveX(slide, 8, 0, Motion.HoverMs);

            fade.BeginTime = TimeSpan.FromMilliseconds(i * 22);
            move.BeginTime = TimeSpan.FromMilliseconds(i * 22);

            var board = new Microsoft.UI.Xaml.Media.Animation.Storyboard();
            board.Children.Add(fade);
            board.Children.Add(move);
            board.Begin();
        }
    }

    private void CloseColorPopover()
    {
        if (_colorPopover is null || !_colorOpen)
        {
            return;
        }

        _colorOpen = false;

        var fade = FadeTo(_colorPopover, 0, Motion.BaseMs);
        var board = new Microsoft.UI.Xaml.Media.Animation.Storyboard();
        board.Children.Add(fade);
        board.Completed += (_, _) =>
        {
            if (!_colorOpen)
            {
                _colorPopover!.Visibility = Visibility.Collapsed;
                _colorPopover.Opacity = 1;
            }
        };
        board.Begin();
    }

    // ---------------------------------------------------------------------
    // 删除（操作条那枚钮的落点）
    // ---------------------------------------------------------------------

    private void DeleteSelectedWithFade()
    {
        if (_selection.Count == 0)
        {
            return;
        }

        var visuals = _realized
            .Where(pair => _selection.Contains(pair.Key))
            .Select(pair => pair.Value)
            .ToList();

        if (visuals.Count == 0)
        {
            RemoveSelection();

            return;
        }

        var board = new Microsoft.UI.Xaml.Media.Animation.Storyboard();

        foreach (var visual in visuals)
        {
            board.Children.Add(FadeTo(visual, 0, Motion.BaseMs));
        }

        // 选中框、手柄与操作条要跟着那一格一起淡：只淡卡片、把那几层留到最后一拍忽然消失，
        // 读起来就是"东西没了但套子还在"（10-24 他点的第四格）。
        foreach (var chrome in SelectionChromeVisuals())
        {
            board.Children.Add(FadeTo(chrome, 0, Motion.BaseMs));
        }

        board.Completed += (_, _) => RemoveSelection();
        board.Begin();
    }

    /// <summary>跟着"选中那一格"走的那几层皮：描边、内对比线、八枚手柄、操作条。</summary>
    private IEnumerable<UIElement> SelectionChromeVisuals()
    {
        yield return SelectionOutline;
        yield return SelectionInner;

        foreach (var handle in _handles)
        {
            yield return handle;
        }

        if (_rail is { Visibility: Visibility.Visible })
        {
            yield return _rail;
        }
    }

    /// <summary>开手那一拍的成员名单：每个框一份（没有名单的框按几何抄一份，等它真的被改动时落成名单）。</summary>
    private Dictionary<string, List<string>> RosterSnapshot()
    {
        var map = Groups();
        var snapshot = new Dictionary<string, List<string>>(StringComparer.Ordinal);

        foreach (var frame in GroupRects())
        {
            snapshot[frame.Id] = [.. map.MemberIds(frame.Id)];
        }

        return snapshot;
    }

    /// <summary>
    /// 组钮那一发的落点写成名单：与别的框一角碰上＝搬进那个框；拖到没有框的地方＝从原框搬出；
    /// 还碰着原框＝留下。
    /// </summary>
    /// <remarks>
    /// 只有这一路改名单。普通拖动一格什么都不写（10-24 他定的规矩："普通拖动不能影响分组，
    /// 只有按住分组图标才能发生入组和出组"）——那一档里被拖远的成员仍在自己的组里，框按名单
    /// hug 过去罩住它；路上别人不会被吞，因为收人不再看几何。
    /// 原来那一档叫 <c>KeepDraggedMemberInFrame</c>：它靠"把框拉长去接那一格"做到不脱组，
    /// 代价就是框长到吞掉路上别人、虚线边拉到屏幕外看着像组消失了。两格 bug 同一个因。
    /// </remarks>
    /// <summary>
    /// 组钮拖那一拍的警示态：路上最多亮一个框，松手后所有框都回到虚线。11-02 他报的是
    /// "拖过两个组时两个框都触发入组，只放进其中一个，另一个框一直亮着"。
    /// </summary>
    /// <remarks>
    /// 这一发自己造两组一格，走真的 Begin/Move/End 那条路（不是直接调 SetGroupAlert），
    /// 判据只读画面：框上的 Alert 状态与那枚矩形的虚线。造的形状放在当前相机中心，免得
    /// 被剔除出画面（那会让断言在没画出来的东西上"通过"）。
    /// </remarks>
    private void SelfTestGroupDragAlerts(List<string> problems)
    {
        var originX = Math.Round(_view.ToWorldX(420));
        var originY = Math.Round(_view.ToWorldY(320));

        var first = _document.Add(CanvasNodeKind.Text, originX, originY, 200, 100);
        var second = _document.Add(CanvasNodeKind.Text, originX + 300, originY, 200, 100);
        var loose = _document.Add(CanvasNodeKind.Text, originX, originY + 300, 200, 100);

        var frameA = _document.Add(CanvasNodeKind.Group, originX - 10, originY - 10, 220, 120);
        frameA.Label = "自检组拖·甲";
        frameA.SetGroupMembers([first.Id]);

        var frameB = _document.Add(CanvasNodeKind.Group, originX + 290, originY - 10, 220, 120);
        frameB.Label = "自检组拖·乙";
        frameB.SetGroupMembers([second.Id]);

        Rebuild();

        var state = new RailDrag
        {
            NodeId = loose.Id,
            StartPointer = new Point(_view.ToScreenX(loose.X + (loose.Width / 2)), _view.ToScreenY(loose.Y + (loose.Height / 2))),
            StartX = loose.X,
            StartY = loose.Y,
            StartPositions = new Dictionary<string, (double X, double Y)>(StringComparer.Ordinal) { [loose.Id] = (loose.X, loose.Y) },
            StartGroupId = null,
            StartRosters = RosterSnapshot(),
        };

        BeginGroupDrag(state, state.StartPointer);
        state.Started = true;

        // 落点在两框之间那道缝上：横跨 200 宽的格子同时与甲、乙相交。
        var landing = new Point(
            _view.ToScreenX(originX + 250),
            _view.ToScreenY(originY + 50));

        MoveGroupDrag(state, landing);

        var alertA = FrameAlert(frameA.Id);
        var alertB = FrameAlert(frameB.Id);

        Diagnostics.Write($"selftest 组拖警示: 横跨两框时 甲={alertA ?? "虚线"} 乙={alertB ?? "虚线"}");

        if (alertA is "?" || alertB is "?")
        {
            problems.Add("selftest 组拖警示: 自检造的框没有画面（被剔除或没造出来），这一发不算过");
        }
        else if (alertA is not null && alertB is not null)
        {
            problems.Add($"selftest 组拖警示: 横跨两框时两个框同时亮（甲={alertA}、乙={alertB}）——同时只能有一个入组目标");
        }

        // 他 11-02 那一次的实际顺序：拖动被打断（捕获丢失）先清一次，随后 Released 仍然把这一发
        // 结算掉。两条出口叠在一起不许留下任何一格亮着的框。
        AbortGroupDrag();

        var abortA = FrameAlert(frameA.Id);
        var abortB = FrameAlert(frameB.Id);

        if (abortA is not null || abortB is not null)
        {
            problems.Add($"selftest 组拖警示: 拖动被打断以后还有框亮着（甲={abortA ?? "无"}、乙={abortB ?? "无"}）");
        }

        EndGroupDrag(state);

        var (alertAfterA, dashedA) = FrameLine(frameA.Id);
        var (alertAfterB, dashedB) = FrameLine(frameB.Id);
        var (haloA, fadeToA) = FrameHalo(frameA.Id);
        var (haloB, fadeToB) = FrameHalo(frameB.Id);

        if (alertAfterA is not null || alertAfterB is not null)
        {
            problems.Add(
                $"selftest 组拖警示: 松手后还有框亮着（甲={alertAfterA ?? "无"}、乙={alertAfterB ?? "无"}）" +
                "——就是「另一个分组的入组状态边框一直存在」");
        }
        else if (!dashedA || !dashedB)
        {
            problems.Add("selftest 组拖警示: 松手后框线没回到虚线（状态清了、线还亮着）");
        }
        else if (fadeToA is 1 || fadeToB is 1)
        {
            // 状态与虚线都对、环却还在往 1 淡：这就是 11-02 那格——清警示态时没停掉刚起步的
            // 淡入，HoldEnd 把它自己拉到 1 并停住，画面一直亮着。
            problems.Add(
                $"selftest 组拖警示: 松手后警示环还在往 1 淡（甲 基值 {haloA:0.##}/目标 {fadeToA}、" +
                $"乙 基值 {haloB:0.##}/目标 {fadeToB}）——那支淡入没被停掉");
        }
        else
        {
            Diagnostics.Write("selftest 组拖警示: 拖过两框只亮一个、松手后状态/虚线/警示环三样都收回");
        }

        foreach (var temp in new[] { frameA, frameB, loose, second, first })
        {
            _document.Remove(temp.Id);
        }

        Rebuild();
    }

    /// <summary>画面上那枚框的警示态；没画出来的读 "?"，别把"没画"当成"已清"。</summary>
    private string? FrameAlert(string id)
        => _realized.TryGetValue(id, out var visual) ? visual.GroupChrome().Alert : "?";

    /// <summary>画面上那枚框的警示态与虚线（同上，没画出来读 "?"）。</summary>
    private (string? Alert, bool Dashed) FrameLine(string id)
        => _realized.TryGetValue(id, out var visual)
            ? (visual.GroupChrome().Alert, visual.GroupFrameLine().Dashed)
            : ("?", false);

    /// <summary>画面上那枚框的警示环：基值与此刻还在往哪儿淡（1＝还在亮、0/null＝已收回）。</summary>
    private (double Halo, double? FadeTo) FrameHalo(string id)
        => _realized.TryGetValue(id, out var visual) ? visual.GroupHaloState() : (0, 1);

    private void WriteRostersAfterGroupDrag(RailDrag state)
    {
        var frames = GroupRects();
        var dragged = state.StartPositions.Keys.ToList();
        var landedIn = new Dictionary<string, string?>(StringComparer.Ordinal);

        foreach (var id in dragged)
        {
            if (NodeOf(id) is not { } node)
            {
                continue;
            }

            var rect = RectOf(node);
            var own = state.StartRosters.FirstOrDefault(pair => pair.Value.Contains(id)).Key;
            string? landed = null;

            foreach (var frame in frames)
            {
                if (frame.Id == own)
                {
                    continue;
                }

                if (frame.Intersects(rect))
                {
                    landed = frame.Id;

                    break;
                }
            }

            if (landed is null && own is { } ownId)
            {
                var self = frames.FirstOrDefault(frame => frame.Id == ownId);

                if (self.Width > 0 && self.Intersects(rect))
                {
                    landed = ownId;   // 还碰着原来的框＝留在原组
                }
            }

            landedIn[id] = landed;
        }

        foreach (var frameNode in _document.Nodes.Where(node => node.Kind == CanvasNodeKind.Group))
        {
            var before = state.StartRosters.TryGetValue(frameNode.Id, out var old) ? old : [];
            var after = new List<string>();

            foreach (var id in before)
            {
                if (!landedIn.TryGetValue(id, out var landed) || landed == frameNode.Id)
                {
                    after.Add(id);
                }
            }

            foreach (var (id, landed) in landedIn)
            {
                if (landed == frameNode.Id && !after.Contains(id))
                {
                    after.Add(id);
                }
            }

            // 没变就别写：没有名单的框（别的工具写的、10-24 之前存的）不该被这一发凭空长出一张表。
            if (!after.SequenceEqual(before))
            {
                frameNode.SetGroupMembers(after);
            }
        }
    }
}
