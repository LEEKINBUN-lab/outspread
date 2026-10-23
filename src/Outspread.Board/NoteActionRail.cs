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

        // 删除那枚整颗染红（图标 + 描边），一眼看出是危险动作。
        _railDelete.BorderBrush = new SolidColorBrush(Color.FromArgb(255, 0xD3, 0x2F, 0x2F));

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

    private static Border RoundButton(string name, UIElement icon, string help)
    {
        var paper = NoteStyling.ToColor(SelectionChrome.HandleFace(NoteStyling.IsDark));

        var button = new Border
        {
            Name = name,
            Width = 38,
            Height = 38,
            CornerRadius = new CornerRadius(19),
            Background = new SolidColorBrush(paper),
            BorderBrush = new SolidColorBrush(NoteStyling.FallbackLine()),
            BorderThickness = new Thickness(1),
            Child = icon,
            Tag = help,
            RenderTransform = new TranslateTransform { X = 8 },
            Opacity = 0,
        };

        return button;
    }

    // WinUI 的几何没有 Geometry.Parse／RectangleGeometry.RadiusX 那套（那是 WPF 的），
    // 三枚图标就用 Shape 直接拼：虚线框＋实心块＝组、圆滴＝颜色、盖＋身＝删。

    private static UIElement GroupIcon()
    {
        var ink = new SolidColorBrush(NoteStyling.InkFor(NoteStyling.ToColor(SelectionChrome.HandleFace(NoteStyling.IsDark))));

        var grid = new Grid { Width = 20, Height = 20 };

        grid.Children.Add(new Rectangle
        {
            Width = 15,
            Height = 15,
            Stroke = ink,
            StrokeThickness = 1.6,
            StrokeDashArray = [3.2, 2.4],
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        });

        grid.Children.Add(new Rectangle
        {
            Width = 7,
            Height = 7,
            Fill = ink,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        });

        return grid;
    }

    // 换色钮：一枚自然晕染的色轮（色相绕圈、越靠圆心越淡到白）。WinUI 这版没有 ConicGradientBrush，
    // 就用 60 片连续插值的扇形拼出色相环，再压一层"白心→透明"的径向渐变当饱和度衰减——
    // 60 片在 22px 下接缝看不出来，读起来就是一张调色的色盘。
    private static UIElement ColorWheelIcon()
    {
        var grid = new Grid { Width = 22, Height = 22 };

        const double cx = 11, cy = 11, r = 10;
        const int slices = 60;
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
        // 删除＝红色，和那颗红描边的按钮一起把"危险"说清楚。
        var ink = new SolidColorBrush(Color.FromArgb(255, 0xD3, 0x2F, 0x2F));

        var grid = new Grid { Width = 20, Height = 20 };

        grid.Children.Add(new Rectangle
        {
            Width = 6,
            Height = 2,
            Fill = ink,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(0, 3, 0, 0),
        });

        grid.Children.Add(new Rectangle
        {
            Width = 13,
            Height = 2,
            Fill = ink,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(0, 5.6, 0, 0),
        });

        grid.Children.Add(new Rectangle
        {
            Width = 10,
            Height = 9,
            Stroke = ink,
            StrokeThickness = 1.6,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Bottom,
            Margin = new Thickness(0, 0, 0, 2.5),
        });

        return grid;
    }

    private void AnimateRailIn()
    {
        var host = (StackPanel)_rail!.Child;
        host.Opacity = 1;
        _rail.Opacity = 1;

        var buttons = new[] { _railGroup!, _railColor!, _railDelete! };

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
                StartGroupId = Grouping.GroupOf(RectOf(first), GroupRects())?.Id,
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
    }

    private void BeginGroupDrag(RailDrag state, Point screen)
    {
        if (NodeOf(state.NodeId) is not { } node)
        {
            return;
        }

        BeginGesture(state.StartPositions.Count > 1 ? $"把 {state.StartPositions.Count} 格拖进／拖出组" : "把这格拖进／拖出组");

        _nodeGesture = new NodeGesture(
            _view.ToWorldX(screen.X),
            _view.ToWorldY(screen.Y),
            new Dictionary<string, (double X, double Y)>(state.StartPositions, StringComparer.Ordinal),
            node.Id,
            GroupMode: true,
            StartGroupId: state.StartGroupId,
            StartMembers: null);

        _railGroup!.BorderBrush = ChromeStyling.Ring;

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

        if (_railGroup is { } group)
        {
            group.BorderBrush = new SolidColorBrush(NoteStyling.FallbackLine());
        }

        if (NodeOf(state.NodeId) is not { } node)
        {
            return;
        }

        var now = Grouping.GroupOf(RectOf(node), GroupRects())?.Id;

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

            var members = new List<Grouping.Rect>();

            foreach (var note in BoardNotes())
            {
                if (!draggedSet.Contains(note.Id) && frame.Holds(RectOf(note)))
                {
                    members.Add(RectOf(note));
                }
            }

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
        var inGroup = SelectedNotes().Where(n => Grouping.GroupOf(RectOf(n), GroupRects()) is not null).ToList();
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

                if (_railGroup is { } group)
                {
                    group.BorderBrush = new SolidColorBrush(NoteStyling.FallbackLine());
                }
            }
        };

        if (_railGroup is { } button)
        {
            button.BorderBrush = ChromeStyling.Ring;
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

        Canvas.SetLeft(_colorPopover, left);
        Canvas.SetTop(_colorPopover, top);
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

        board.Completed += (_, _) => RemoveSelection();
        board.Begin();
    }

    // ---------------------------------------------------------------------
    // 普通拖动的"不脱组"：成员被甩出框时，框跟着长，而不是把它踢出去
    // ---------------------------------------------------------------------

    /// <summary>
    /// 普通拖动松手的那一拍：如果这格原来在某个框里、中心现在出去了——那是"挪位置"，
    /// 不是"拆组"。把框按开手那一刻的成员集合重新 hug 住（它们还都在，框跟着长）。
    /// 只有组钮拖（GroupMode）才允许真的把成员甩出框外。
    /// </summary>
    private void KeepDraggedMemberInFrame(NodeGesture gesture)
    {
        // 并集下不写任何组框（见 RefitFrames 说明）：sticky 会长/缩组框，跨文件会污染源文件。
        if (IsAllNotes(_boardPath))
        {
            return;
        }

        if (gesture.GroupMode || gesture.StartGroupId is not { } gid || gesture.StartMembers is not { } ids)
        {
            return;
        }

        if (NodeOf(gesture.PrimaryId) is not { } moved || NodeOf(gid) is not { } frameNode)
        {
            return;
        }

        if (RectOf(frameNode).Holds(RectOf(moved)))
        {
            return; // 中心还在框里：本来就不用粘
        }

        var rects = new List<Grouping.Rect>();

        foreach (var id in ids)
        {
            if (NodeOf(id) is { } member)
            {
                rects.Add(RectOf(member));
            }
        }

        if (rects.Count == 0)
        {
            return;
        }

        var hug = Grouping.Enclose(gid, rects);

        frameNode.X = hug.X;
        frameNode.Y = hug.Y;
        frameNode.Width = hug.Width;
        frameNode.Height = hug.Height;

        Diagnostics.Write($"sticky frame: {gid} 跟着 {moved.Id} 长到 {hug.Width:0.#}×{hug.Height:0.#}（普通拖动不脱组）");
    }
}
