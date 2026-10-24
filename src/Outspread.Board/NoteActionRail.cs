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
            Spacing = 10,
            Opacity = 0,
        };

        _railGroup = RoundButton("NoteRailGroup", GroupIcon(), "分组（点＝菜单，按住拖＝拖入/拖出）");
        _railColor = RoundButton("NoteRailColor", ColorWheelIcon(), "换色");
        _railDelete = RoundButton("NoteRailDelete", TrashIcon(), "删掉这格");

        host.Children.Add(_railGroup);
        host.Children.Add(_railColor);
        host.Children.Add(_railDelete);

        WireRailButtons();

        var shell = new Border
        {
            Name = "NoteRail",
            Child = host,
            Visibility = Visibility.Collapsed,
            IsHitTestVisible = true,
        };

        HandleLayer.Children.Add(shell);

        return shell;
    }

    /// <summary>操作条圆钮的直径。色板落位和命中区都按它算，所以只在这里写一次。</summary>
    private const double RailButtonPx = 38;

    private static readonly Color RailDangerInk = Color.FromArgb(255, 0xD3, 0x2F, 0x2F);

    /// <summary>
    /// 圆钮那一圈发丝的粗细。1.25 而不是 1：1px 的正圆在 125% 缩放下会一段深一段浅，看上去就是
    /// 锯齿；多四分之一像素让覆盖更连续，观感仍是发丝。颜色走
    /// <see cref="SelectionChrome.HandleEdgeAlpha"/>，和选中手柄同一档。
    /// </summary>
    private const double RailHairlinePx = 1.25;

    /// <summary>
    /// 一枚圆钮：外壳仍是 <see cref="Border"/>（自检按名字找它、拖动也靠它的指针事件），
    /// 但圆和描边交给 <see cref="Ellipse"/> 画——10-22 那版是 Border 的 CornerRadius，弧线在
    /// 帧边界上被切成一段段，他看到的锯齿就是它。椭圆的几何描边由平台自己抗锯齿，38px 下干净。
    /// 悬停只做一件事：面加深一点、线实一点，说明"这一枚能按"。
    /// </summary>
    private Border RoundButton(string name, UIElement icon, string help)
    {
        var face = new Ellipse
        {
            Width = RailButtonPx,
            Height = RailButtonPx,
            StrokeThickness = RailHairlinePx,
            IsHitTestVisible = false,
        };

        var stack = new Grid
        {
            Width = RailButtonPx,
            Height = RailButtonPx,
        };

        stack.Children.Add(face);
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

        PaintRailButton(button, hover: false);

        return button;
    }

    private static Ellipse? FaceOf(Border button)
        => button.Child is Grid grid ? grid.Children.OfType<Ellipse>().FirstOrDefault() : null;

    /// <summary>圆钮唯一的状态画法：纸面／悬停面、发丝／悬停线、组钮激活时的蓝环、删除钮的红。</summary>
    private void PaintRailButton(Border button, bool hover)
    {
        if (FaceOf(button) is not { } face)
        {
            return;
        }

        var dark = NoteStyling.IsDark;
        var paper = NoteStyling.ToColor(SelectionChrome.HandleFace(dark));
        var ink = NoteStyling.ToColor(Sheet.Ink(dark));

        var danger = button.Name == "NoteRailDelete";
        var active = _railGroupActive && ReferenceEquals(button, _railGroup);

        face.Fill = new SolidColorBrush(
            hover ? Mixture(paper, danger ? RailDangerInk : ink, danger ? 0.08 : dark ? 0.10 : 0.05) : paper);

        face.Stroke = new SolidColorBrush(
            active ? NoteStyling.ToColor(SelectionChrome.Ring(dark))
            : danger ? AtAlpha(RailDangerInk, hover ? 0.92 : 0.55)
            : AtAlpha(NoteStyling.ToColor(dark ? new Rgb(255, 255, 255) : new Rgb(0, 0, 0)),
                SelectionChrome.HandleEdgeAlpha(dark) + (hover ? 0.10 : 0)));
    }

    private static Color AtAlpha(Color color, double alpha)
        => Color.FromArgb((byte)System.Math.Round(Math.Clamp(alpha, 0, 1) * 255), color.R, color.G, color.B);

    /// <summary>组钮的激活环（拖动中／菜单开着）。原来改的是 Border.BorderBrush，现在那条边不存在了。</summary>
    private void SetRailGroupActive(bool active)
    {
        _railGroupActive = active;

        if (_railGroup is { } group)
        {
            PaintRailButton(group, hover: ReferenceEquals(_railHover, group));
        }
    }

    private static Color Mixture(Color from, Color to, double amount)
    {
        var t = Math.Clamp(amount, 0, 1);

        static byte Blend(byte a, byte b, double share) => (byte)Math.Round(a + ((b - a) * share));

        return Color.FromArgb(from.A, Blend(from.R, to.R, t), Blend(from.G, to.G, t), Blend(from.B, to.B, t));
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

    // WinUI 的几何没有 Geometry.Parse／RectangleGeometry.RadiusX 那套（那是 WPF 的），
    // 三枚图标就用 Shape／字形直接拼：虚线框＋实心块＝组、圆滴＝颜色、垃圾桶＝删。

    private static UIElement GroupIcon()
    {
        var ink = new SolidColorBrush(NoteStyling.InkFor(NoteStyling.ToColor(SelectionChrome.HandleFace(NoteStyling.IsDark))));

        var grid = new Grid { Width = 20, Height = 20 };

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

    // 换色钮：一枚自然晕染的色轮（色相绕圈、越靠圆心越淡到白）。WinUI 这版没有 ConicGradientBrush，
    // 就用连续插值的扇形拼出色相环，再压一层"白心→透明"的径向渐变当饱和度衰减——
    // 72 片在 22px 下接缝看不出来，读起来就是一张调色的色盘；最外一圈发丝把这圈色收干净。
    private static UIElement ColorWheelIcon()
    {
        var grid = new Grid { Width = 22, Height = 22 };

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

    private static UIElement TrashIcon()
    {
        // 删除＝红色，和那颗红描边的按钮一起把"危险"说清楚。字形用系统自带的垃圾桶
        // （Segoe MDL2 Assets E74D）——之前那版是两块矩形拼的盖和身，1.6px 的直角在
        // 38px 圆里读起来像故障，而这类通用图标本来就该交给系统字体画。
        return new FontIcon
        {
            Glyph = "\uE74D",
            FontFamily = new FontFamily("Segoe MDL2 Assets"),
            FontSize = 18,
            Foreground = new SolidColorBrush(RailDangerInk),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            IsHitTestVisible = false,
        };
    }

    private void AnimateRailIn()
    {
        var host = (StackPanel)_rail!.Child;
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
            ToggleColorPopover();
            e.Handled = true;
        };

        _railDelete!.PointerPressed += (_, e) =>
        {
            DeleteSelectedWithFade();
            e.Handled = true;
        };

        foreach (var button in new[] { _railGroup!, _railColor!, _railDelete! })
        {
            var target = button;

            target.PointerEntered += (_, _) =>
            {
                _railHover = target;
                PaintRailButton(target, hover: true);
            };

            target.PointerExited += (_, _) =>
            {
                if (ReferenceEquals(_railHover, target))
                {
                    _railHover = null;
                }

                PaintRailButton(target, hover: false);
            };
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
        const double railWidth = 38;
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
