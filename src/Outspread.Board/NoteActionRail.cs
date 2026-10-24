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
    private Border? _railGroup;
    private Border? _railColor;
    private Border? _railDelete;
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

        var screenLeft = _view.ToScreenX(box.X);
        var flip = screenLeft < 60;
        var x = flip ? _view.ToScreenX(box.Right) + 12 : screenLeft - 50;
        var y = Math.Clamp(_view.ToScreenY(box.Y + box.Height / 2) - 64, 8, Math.Max(8, BoardArea.ActualHeight - 140));

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

        if (!_railShown)
        {
            _railShown = true;
            _rail.Visibility = Visibility.Visible;
            AnimateRailIn();
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
            Spacing = 2,
            Opacity = 0,
        };

        _railGroup = RoundButton("NoteRailGroup", GroupIcon(RailInk()), "分组（点＝菜单，按住拖＝拖入/拖出）");
        _railColor = RoundButton("NoteRailColor", PaletteIcon(RailInk()), "换色");

        // 删的红现在长在图形本身，不再长在圈上——静止那一档根本没有圈。
        _railDelete = RoundButton("NoteRailDelete", TrashIcon(new SolidColorBrush(RailDangerInk)), "删掉这格");

        host.Children.Add(_railGroup);
        host.Children.Add(_railColor);
        host.Children.Add(_railDelete);

        WireRailButtons();

        var backing = new Border
        {
            CornerRadius = new CornerRadius(RailCornerPx),
            BorderThickness = new Thickness(RailHairlinePx),
            Padding = new Thickness(RailPadPx),
            Child = host,
        };

        var shell = new Border
        {
            Name = "NoteRail",
            Child = backing,
            Visibility = Visibility.Collapsed,
            IsHitTestVisible = true,
        };

        HandleLayer.Children.Add(shell);

        // BuildRail 只由 `_rail ??= BuildRail()` 调进来，这里先挂上是为了让 PaintRailSurface 认得它。
        _rail = shell;
        PaintRailSurface();

        return shell;
    }

    /// <summary>图标那三枚的墨色：跟着纸面走，深浅两档各一个值。</summary>
    private static SolidColorBrush RailInk()
        => new(NoteStyling.ToColor(Sheet.Ink(NoteStyling.IsDark)));

    /// <summary>纸上那一档底色：图标里"挖洞"与挡住后面那枚卡片都靠它。</summary>
    private static SolidColorBrush FaceBrush()
        => new(NoteStyling.ToColor(SelectionChrome.HandleFace(NoteStyling.IsDark)));

    /// <summary>圆钮本体直径——令牌里那一个数，色板落位与命中区都按它算。</summary>
    private const double RailButtonPx = ControlKit.ButtonPx;

    /// <summary>整条浮层底的内衬与圆角：三枚钮坐在同一张底上，不再各套一个常驻圆圈。</summary>
    private const double RailPadPx = 3;

    private const double RailCornerPx = 8;

    /// <summary>浮层底那一圈发丝：1 逻辑像素。钮自己不再有发丝——静止时三家都不画。</summary>
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

        stack.Children.Add(new Ellipse { Width = RailButtonPx, Height = RailButtonPx, Opacity = 0, IsHitTestVisible = false });
        stack.Children.Add(new Ellipse { Width = RailButtonPx, Height = RailButtonPx, Opacity = 0, IsHitTestVisible = false });
        stack.Children.Add(new Ellipse
        {
            Width = RailButtonPx - 2 * RailHairlinePx,
            Height = RailButtonPx - 2 * RailHairlinePx,
            StrokeThickness = 2,
            Opacity = 0,
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
    /// 一枚钮的三层状态：悬停层、按压层、组钮激活环。按加入顺序取——顺序变了自检会红，
    /// 因为时长与哪一层该出现在哪一档，全靠这个顺序对上。
    /// </summary>
    private static (Ellipse Hover, Ellipse Press, Ellipse Ring)? LayersOf(Border button)
    {
        if (button.Child is not Grid grid)
        {
            return null;
        }

        var layers = grid.Children.OfType<Ellipse>().ToList();

        return layers.Count < 3
            ? null
            : (layers[0], layers[1], layers[2]);
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

        layers.Hover.Fill = new SolidColorBrush(AtAlpha(ink, ControlKit.HoverAlpha));
        layers.Press.Fill = new SolidColorBrush(AtAlpha(ink, ControlKit.PressAlpha));
        layers.Ring.Stroke = new SolidColorBrush(AtAlpha(NoteStyling.ToColor(SelectionChrome.Ring(dark)), 0.9));
    }

    /// <summary>
    /// 整条浮层底：一张纸、一圈 1px 发丝。钮自己静止时不带圈——Chrome／Fluent／M3 三家的
    /// 浮动条都是"一条底＋只在指针上来时出现的圆面"，原来那三圈常驻发丝是这里唯一多出来的东西。
    /// </summary>
    private void PaintRailSurface()
    {
        if (_rail?.Child is not Border backing)
        {
            return;
        }

        var dark = NoteStyling.IsDark;

        _railSurfaceDark = dark;
        backing.Background = new SolidColorBrush(NoteStyling.ToColor(SelectionChrome.HandleFace(dark)));
        backing.BorderBrush = new SolidColorBrush(AtAlpha(dark ? Colors.White : Colors.Black, SelectionChrome.HandleEdgeAlpha(dark)));

        foreach (var button in new[] { _railGroup, _railColor, _railDelete })
        {
            if (button is { } hit)
            {
                PaintRailButton(hit);
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

    // 三枚图标按最终尺寸直接落笔：20 画布、2 像素笔画、坐标全在整数格上、开口笔画两端补圆点当线帽。
    // 为什么不再走"24 网格 + Viewbox 缩到 16"那一发：它把 1.5 的笔画稀释成落屏 1.0，又把每个整数
    // 坐标乘上 2/3 变成 4.667 这种小数——一条线被摊到两行像素上，就是他 10-24 说的"晕染"。
    // 三个参照系走的都是另一条路：笔画按档重画，不靠缩放（实测 Fluent 图标 16px 档 1.0／24px 档 1.5，
    // IBM 20px 档 1.25，Chrome 把 ~1.9dp 的重量直接烘进填充形状）。
    // 圆头线帽在 C# 里确实拿不到（那个枚举的命名空间两个全名都报 CS0234，XAML 侧才认），
    // 所以开口笔画的两端各补一枚直径＝笔画的实心圆点——视觉等价，而且自检量得到。
    // 其余六个量（钮径、光学盒、状态层、时长）都在 ControlKit 里，一处写死。

    private const double IconCanvasPx = ControlKit.GlyphCanvasPx;

    private double RasterizationScale => BoardArea.XamlRoot?.RasterizationScale ?? 1;

    /// <summary>落整设备像素的那条笔画：96 DPI 下就是 2，125% 下自动升到 3 个物理像素。</summary>
    private double IconStrokePx => ControlKit.CrispStroke(RasterizationScale, ControlKit.GlyphStrokePx);

    /// <summary>组＝两枚卡片前后叠压，前面那枚用面色挡住后面那枚的边。</summary>
    private UIElement GroupIcon(SolidColorBrush ink)
    {
        var canvas = IconCanvas();

        IconRect(canvas, ink, 7, 3, 10, 10, 2);
        IconRect(canvas, ink, 3, 7, 10, 10, 2, FaceBrush());

        return canvas;
    }

    /// <summary>
    /// 换色＝一枚带指孔的调色盘：圈＋右下角挖一个面色孔＋板上三滴。
    /// 原来那枚 72 片色轮读起来最像"调色"，但它和另两枚不是一档笔画——他这轮要的是"一套"。
    /// 要退回去：<c>git show 92c3ecd~1</c> 里那份 ColorWheelIcon 整段贴回来（连带 HsvToColor）。
    /// </summary>
    private UIElement PaletteIcon(SolidColorBrush ink)
    {
        var canvas = IconCanvas();
        var w = IconStrokePx;

        IconRing(canvas, ink, 10, 10, 7);
        IconDot(canvas, FaceBrush(), 15, 15, w);
        IconDot(canvas, ink, 6.5, 8, 1.5);
        IconDot(canvas, ink, 10, 5.5, 1.5);
        IconDot(canvas, ink, 13.5, 8, 1.5);

        return canvas;
    }

    private UIElement TrashIcon(SolidColorBrush ink)
    {
        var canvas = IconCanvas();

        IconStroke(canvas, ink, (3, 5), (17, 5));
        IconStroke(canvas, ink, (8, 5), (8, 3), (12, 3), (12, 5));
        IconStroke(canvas, ink, (5, 8), (5, 17), (15, 17), (15, 8));
        IconStroke(canvas, ink, (8, 11), (8, 15));
        IconStroke(canvas, ink, (12, 11), (12, 15));

        return canvas;
    }

    /// <summary>一枚图标的画布：20×20，最终尺寸，外面不再套任何缩放。</summary>
    private static Canvas IconCanvas() => new() { Width = IconCanvasPx, Height = IconCanvasPx, IsHitTestVisible = false };

    /// <summary>
    /// 一条开口笔画：本体＋两端各一枚圆点。圆点就是圆头线帽的替身——
    /// 这个投影的 C# 拿不到 StrokeStartLineCap，而稿子那三枚的观感一半在圆头上。
    /// </summary>
    private void IconStroke(Canvas canvas, SolidColorBrush ink, params (double X, double Y)[] points)
    {
        var w = IconStrokePx;

        if (points.Length == 2)
        {
            canvas.Children.Add(new Line
            {
                X1 = points[0].X,
                Y1 = points[0].Y,
                X2 = points[1].X,
                Y2 = points[1].Y,
                Stroke = ink,
                StrokeThickness = w,
            });
        }
        else
        {
            var polyline = new Polyline { Stroke = ink, StrokeThickness = w };

            foreach (var (x, y) in points)
            {
                polyline.Points.Add(new Point(x, y));
            }

            canvas.Children.Add(polyline);
        }

        IconDot(canvas, ink, points[0].X, points[0].Y, w / 2);
        IconDot(canvas, ink, points[^1].X, points[^1].Y, w / 2);
    }

    private void IconRect(Canvas canvas, SolidColorBrush ink, double x, double y, double width, double height, double radius, SolidColorBrush? fill = null)
    {
        var rect = new Rectangle
        {
            Width = width,
            Height = height,
            RadiusX = radius,
            RadiusY = radius,
            Stroke = ink,
            StrokeThickness = IconStrokePx,
        };

        if (fill is { } face)
        {
            rect.Fill = face;
        }

        Canvas.SetLeft(rect, x);
        Canvas.SetTop(rect, y);
        canvas.Children.Add(rect);
    }

    /// <summary>一圈闭合的描边（调色盘的外廓）：闭合曲线没有端点，所以不需要线帽。</summary>
    private void IconRing(Canvas canvas, SolidColorBrush ink, double cx, double cy, double radius)
    {
        var ring = new Ellipse
        {
            Width = radius * 2,
            Height = radius * 2,
            Stroke = ink,
            StrokeThickness = IconStrokePx,
        };

        Canvas.SetLeft(ring, cx - radius);
        Canvas.SetTop(ring, cy - radius);
        canvas.Children.Add(ring);
    }

    private static void IconDot(Canvas canvas, SolidColorBrush ink, double cx, double cy, double radius)
    {
        var dot = new Ellipse
        {
            Width = radius * 2,
            Height = radius * 2,
            Fill = ink,
        };

        Canvas.SetLeft(dot, cx - radius);
        Canvas.SetTop(dot, cy - radius);
        canvas.Children.Add(dot);
    }

    /// <summary>
    /// 三枚图标的画法凭据：同一张最终尺寸的画布、同一条落整设备像素的笔画、坐标全在半格网格上、
    /// 链路上不许再有 Viewbox，且每一条开口笔画两端都有圆点。
    /// </summary>
    /// <remarks>
    /// 这四条各锁一次他 10-24 报过的缺陷：不一致（"三枚各画各的"）、太细（稀释成 1.0）、
    /// 晕染（缩放松把坐标变成小数）、平头（"和稿不一样"）。判据全部读实物视觉树，不读注释。
    /// </remarks>
    private void SelfTestRailIconSet(List<string> problems)
    {
        if (_railGroup is null || _railColor is null || _railDelete is null)
        {
            problems.Add("自检图标集: 操作条没建出来（三枚钮读不到）");

            return;
        }

        var scale = RasterizationScale;
        var expected = IconStrokePx;

        if (!ControlKit.IsCrispStroke(scale, expected))
        {
            problems.Add($"自检图标集: 令牌给的笔画 {expected:0.###} 在 {scale:0.##}× 缩放下落不到整设备像素——这就是晕染");
        }

        var strokes = new List<double>();
        var buttons = new[] { (_railGroup, "组"), (_railColor, "色"), (_railDelete, "删") };

        foreach (var (button, name) in buttons)
        {
            // 圆钮：Border → Grid（三层状态 ＋ 一枚画布）。画布必须直接坐在 Grid 里，中间不许有缩放。
            var grid = button.Child as Grid;
            var canvas = grid?.Children.OfType<Canvas>().FirstOrDefault();

            if (grid is null || canvas is null)
            {
                problems.Add(
                    $"自检图标集: {name} 那枚不是「Grid 直接套画布」这一形" +
                    $"（读到 {(grid?.Children.Count.ToString() ?? "不是 Grid")} 件，画布 {(canvas is null ? "没找到" : "在")}）");

                continue;
            }

            if (grid.Children.OfType<Viewbox>().Any())
            {
                problems.Add($"自检图标集: {name} 那枚又被 Viewbox 套了一层——缩放会把笔画稀释、把坐标变成小数");
            }

            if (canvas.Width != IconCanvasPx)
            {
                problems.Add($"自检图标集: {name} 那枚画布是 {canvas.Width:0.#}，应是最终尺寸 {IconCanvasPx:0.#}");
            }

            var shapes = canvas.Children.OfType<Shape>().ToList();
            var dots = shapes.Where(shape => shape.Stroke is null && shape.Fill is not null).OfType<Ellipse>().ToList();
            var open = shapes.OfType<Line>().Select(line => new[] { new Point(line.X1, line.Y1), new Point(line.X2, line.Y2) })
                .Concat(shapes.OfType<Polyline>().Select(poly => poly.Points.ToArray()))
                .ToList();

            if (shapes.Count == 0)
            {
                problems.Add($"自检图标集: {name} 那枚画布上一个形状都没有");
            }

            // 开口笔画（有端点的那种）才需要圆点收尾；两枚卡片与那圈调色盘是闭合的，本来就没有端点。
            if (open.Count > 0 && dots.Count == 0)
            {
                problems.Add($"自检图标集: {name} 那枚有 {open.Count} 条开口笔画却没有一枚圆点——端点是平的，稿子上是圆头");
            }

            strokes.AddRange(shapes.Where(shape => shape.Stroke is not null).Select(shape => shape.StrokeThickness));

            foreach (var point in open.SelectMany(shape => shape).Concat(
                         shapes.OfType<Rectangle>().SelectMany(rect => new[]
                         {
                             new Point(Canvas.GetLeft(rect), Canvas.GetTop(rect)),
                             new Point(Canvas.GetLeft(rect) + rect.Width, Canvas.GetTop(rect) + rect.Height),
                         })))
            {
                if (!ControlKit.OnPixelGrid(point.X) || !ControlKit.OnPixelGrid(point.Y))
                {
                    problems.Add($"自检图标集: {name} 那枚有坐标落在半格网格外（{point.X:0.###},{point.Y:0.###}）——边缘会摊到两行像素上");

                    break;
                }
            }

            var misses = open.SelectMany(shape => new[] { shape.First(), shape.Last() })
                .Count(end => dots.All(dot => System.Math.Abs(Canvas.GetLeft(dot) + dot.Width / 2 - end.X) > 0.75
                    || System.Math.Abs(Canvas.GetTop(dot) + dot.Height / 2 - end.Y) > 0.75));

            if (misses > 0)
            {
                problems.Add($"自检图标集: {name} 那枚有 {misses} 个端点没有圆点收尾——端点是平的，稿子上是圆头");
            }
        }

        if (strokes.Count == 0 || strokes.Any(thickness => Math.Abs(thickness - expected) > 0.001))
        {
            problems.Add(
                $"自检图标集: 描边粗细不是一套——读到 {string.Join("、", strokes.Distinct().Select(value => value.ToString("0.##")))}，" +
                $"应全是 {expected:0.###}");
        }

        Diagnostics.Write(
            $"selftest 图标集: 三枚都是 {IconCanvasPx:0} 画布直接落笔（无 Viewbox）、笔画 {strokes.Count} 条全 " +
            $"{expected:0.###} 逻辑像素＝{expected * scale:0.##} 物理像素、端点带圆头");
    }

    private void AnimateRailIn()
    {
        var host = (StackPanel)((Border)_rail!.Child).Child;
        host.Opacity = 1;
        _rail.Opacity = 1;

        var buttons = new[] { _railGroup!, _railColor!, _railDelete! };

        // 取证要的是落定那一帧：入场是 120ms 错峰，照片在 0ms 拍下去只会得到三枚透明圆。
        if (_capturingShot)
        {
            foreach (var button in buttons)
            {
                button.RenderTransform = new TranslateTransform { X = 0 };
                button.Opacity = 1;
            }

            return;
        }

        for (var i = 0; i < buttons.Length; i++)
        {
            var button = buttons[i];
            var slide = new TranslateTransform { X = 8 };

            button.RenderTransform = slide;
            button.Opacity = 0;

            var fade = FadeTo(button, 1, Motion.HoverMs);
            var move = MoveX(slide, 8, 0, Motion.HoverMs);

            fade.BeginTime = TimeSpan.FromMilliseconds(i * 35);
            move.BeginTime = TimeSpan.FromMilliseconds(i * 35);

            var board = new Microsoft.UI.Xaml.Media.Animation.Storyboard();
            board.Children.Add(fade);
            board.Children.Add(move);
            board.Begin();
        }
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
            else
            {
                ShowGroupMenu();
            }

            e.Handled = true;
        };

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

        foreach (var button in new[] { _railGroup!, _railColor!, _railDelete! })
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

        if (_railGroup is not null)
        {
            SetRailGroupActive(false);
        }

        if (NodeOf(state.NodeId) is not { } node)
        {
            return;
        }

        var now = Groups().GroupOf(RectOf(node))?.Id;

        if (joined || (now is not null && now != state.StartGroupId))
        {
            Say("移入了这一组：框长过去罩住它，格子没动");
        }
        else if (state.StartGroupId is not null && now is null)
        {
            Say("移出了那一组：框已缩回");
        }
        else
        {
            Say("松在了原来的位置——分组没动");
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

        // 创建分组：把当前选中的（一枚或多枚）圈成一个<b>新</b>组——分组是显式动作，
        // 单枚也能成组（10-22 他要的）。旧那条"移入分组＝塞进最近的一个已有框"已删；
        // 要并进某个具体的框，走组钮按住拖那一条。
        var create = new MenuFlyoutItem
        {
            Text = SelectedNotes().Count == 1 ? "创建分组（这一格）" : $"创建分组（这 {SelectedNotes().Count} 格）",
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
        // 整条的外沿宽：一枚钮 ＋ 两侧内衬 ＋ 两侧发丝。色板贴的是这条，不是单枚钮。
        const double railWidth = RailButtonPx + 2 * (RailPadPx + RailHairlinePx);
        var height = ColourSwatches.Length * swatch + (ColourSwatches.Length - 1) * 8;

        // 贴在操作条那一列的旁边、离便签远的那一侧；贴不下就翻到另一侧。
        var left = _railFlipped ? _railLeft + railWidth + gap : _railLeft - swatch - gap;

        if (left < 4)
        {
            left = _railLeft + railWidth + gap;
        }
        else if (left + swatch > BoardArea.ActualWidth - 4)
        {
            left = _railLeft - swatch - gap;
        }

        // 色板列的中心对齐换色钮那颗（它是三枚里第 2 枚：38＋10 间距 → 中心在列顶 +67）。
        var top = Math.Clamp(
            _railTop + 67 - height / 2,
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
            Say(captured is null ? "颜色摘了" : "换了个颜色");
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
