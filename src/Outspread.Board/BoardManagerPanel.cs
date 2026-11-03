using System.IO;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Outspread.Core;

namespace Outspread.Board;

/// <summary>
/// 标签栏左端那枚图标钮打开的「画板」表：盘上有哪些板、各多大、多少格、什么时候动过、
/// 现在开着没有，每行三枚动作（打开／在文件夹中打开／删除）。
/// </summary>
/// <remarks>
/// <para>
/// 面板**接在标签栏下面**：它挂在板面那一行的顶上、与标签栏同宽，不另开窗口——这是 10-26 他拍下的
/// 形状（第一版是浮窗，被"突兀地多一个窗口"打回）。所以边框只有下沿一条，没有圆角、没有阴影。
/// </para>
/// <para>
/// 这里也是**唯一**能真正删掉一块板的地方：标签上的 × 只关不删（见 <c>BoardTabs.CloseTab</c>）。
/// 删除要过 <see cref="ConfirmBoardDeleteAsync"/>，确认句点名从本机拿走的四样东西。
/// </para>
/// </remarks>
public sealed partial class MainWindow
{
    /// <summary>表格内容最多这么宽，再宽就居中留边——不把最后几列挤在窗口右边。</summary>
    private const double ManagerBodyMaxWidth = 1200;

    private const double ManagerActionsWidth = 102;

    /// <summary>每一行左右各留这么多：内容贴着面板边读起来像没装完（他 10-26 的第二条）。
    /// 行间那条分隔线也缩进同样的宽度，两者才在同一条竖线上。</summary>
    private const double ManagerSidePadding = 32;

    /// <summary>列与列之间的固定空隙；列宽本身是星号，窗口变窄时一起收。</summary>
    private const double ManagerColumnGap = 20;

    /// <summary>名字前面那格状态牌的固定宽度：开着的行有牌、没开的行留空，
    /// 名字才不会一行一个起点（表头「名称」也跟着这个宽度缩进）。</summary>
    private const double ManagerStatusSlot = 64;

    private bool _boardManagerOpen;
    private Border? _managerPanel;
    private Border? _managerShadow;
    private Grid? _managerRows;
    private Grid? _managerBody;
    private Button? _managerEntry;
    private TranslateTransform? _managerSlide;

    /// <summary>双击第一下的时间。标题栏现在把这一枚让回客户区，双击仍会落成两次 Click；
    /// 记下这一格，第二下不再开合一次——他点快的时候要的是"开"或"关"，不是开关各一次。</summary>
    private long _lastManagerClickTicks;

    /// <summary>一行要显示的东西，先在内存里算好再画——画的那一步不再碰磁盘。</summary>
    private sealed record BoardLine(string Path, string Name, DateTime Created, DateTime Changed, int Notes, long Bytes, bool Open);

    private void ToggleBoardManager() => SetBoardManager(!_boardManagerOpen);

    private void SetBoardManager(bool open)
    {
        EnsureBoardManager();

        _boardManagerOpen = open;

        if (_managerPanel is not null)
        {
            // 这一张是贴在板面上的数据表，不是浮在便签上的面板：不跟着浮层那档半透明走，
            // 否则下面的字会透出来（他 10-26 的截图里，行与行之间全是影子）。
            _managerPanel.Background = ChromeStyling.PanelSolid;
            AnimateBoardManager(open);
        }

        if (_managerShadow is not null && _managerPanel is not null)
        {
            _managerShadow.Visibility = open ? Visibility.Visible : Visibility.Collapsed;

            if (open)
            {
                _managerPanel.UpdateLayout();
                PlaceManagerShadow();
            }
        }

        if (_managerEntry is not null)
        {
            // 展开态只压一层很淡的墨，不描边：标签栏里别处都没有框，凭什么是它。
            _managerEntry.Background = open
                ? new SolidColorBrush(NoteStyling.WithAlpha(new Rgb(27, 29, 31), 0.08))
                : new SolidColorBrush(Microsoft.UI.Colors.Transparent);
        }

        if (open)
        {
            FillBoardManager();

            // 行是刚塞进去的：面板在为空时量过一次，这里必须先让它的量失效，否则高度停在旧值。
            _managerPanel?.InvalidateMeasure();

            // 开的那一拍就把版排掉：不排的话第一次打开（和取证那一发）会先抓到一块 0 高的空壳。
            BoardArea.UpdateLayout();

            // 宽度也要按此刻的窗口重算：面板第一次出现时 SizeChanged 可能还没发。
            SizeBoardManagerBody();
            _managerPanel?.UpdateLayout();
        }

        Diagnostics.Write($"board manager: open={open} vis={_managerPanel?.Visibility} parent={(_managerPanel?.Parent is null ? "null" : (_managerPanel.Parent as FrameworkElement)?.Name)} rows={_managerRows?.Children.Count} size={_managerPanel?.ActualWidth:0}x{_managerPanel?.ActualHeight:0}");
    }

    /// <summary>开合那一拍：往下滑 10px ＋ 淡进来，关的时候反向。静止不挂常驻描边那一条不动。</summary>
    private void AnimateBoardManager(bool open)
    {
        if (_managerPanel is null || _managerSlide is null)
        {
            return;
        }

        // 自检不量动效，量的是结构：逐帧收起来的收口会把"关掉"那一刻的 Visible 拖后一帧。
        if (App.SelfTest || _capturingShot)
        {
            _managerPanel.Visibility = open ? Visibility.Visible : Visibility.Collapsed;

            return;
        }

        var milliseconds = open ? Motion.BaseMs : Motion.HoverMs;
        var duration = TimeSpan.FromMilliseconds(milliseconds);

        _managerPanel.Visibility = Visibility.Visible;

        var slide = new DoubleAnimation
        {
            To = open ? 0 : -10,
            Duration = duration,
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
        };

        var fade = new DoubleAnimation
        {
            To = open ? 1 : 0,
            Duration = duration,
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
        };

        var storyboard = new Storyboard { FillBehavior = FillBehavior.HoldEnd };
        Storyboard.SetTarget(slide, _managerSlide);
        Storyboard.SetTargetProperty(slide, "Y");
        Storyboard.SetTarget(fade, _managerPanel);
        Storyboard.SetTargetProperty(fade, "Opacity");
        storyboard.Children.Add(slide);
        storyboard.Children.Add(fade);

        if (!open)
        {
            storyboard.Completed += (_, _) =>
            {
                if (!_boardManagerOpen && _managerPanel is not null)
                {
                    _managerPanel.Visibility = Visibility.Collapsed;
                }
            };
        }

        storyboard.Begin();
    }

    private void EnsureBoardManager()
    {
        if (_managerPanel is not null)
        {
            return;
        }

        ChromeStyling.Ensure(NoteStyling.IsDark);

        _managerRows = new Grid();
        // 没有标题行、没有说明行：他要的是"信息平铺"，表头下面的每一行就是答案。
        // 也不在表头上方留空气：那条 4px 白缝会被读成"奇怪的白色区域"（他 10-26 的截图）。
        //
        // 用 Grid 而不是 StackPanel：竖向 StackPanel 只按孩子的 DesiredWidth 摆，
        // 表头和行都不会横向铺开——外层给到 1200 也没用，看起来仍是中间窄窄一条。
        var body = new Grid();
        body.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        body.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        // 宽度由面板给（见 SizeBoardManagerBody）：窗口宽就铺到 1200 再居中，窗口窄就跟着收。
        _managerBody = body;

        var headerRow = ManagerRowGrid(isHeader: true);
        Grid.SetRow(headerRow, 0);
        body.Children.Add(headerRow);

        Grid.SetRow(_managerRows, 1);
        body.Children.Add(_managerRows);

        _managerPanel = new Border
        {
            VerticalAlignment = VerticalAlignment.Top,
            Padding = new Thickness(0),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            Background = ChromeStyling.PanelSolid,
            BorderThickness = new Thickness(0, 0, 0, 1),
            BorderBrush = ChromeStyling.PanelLine,
            CornerRadius = new CornerRadius(0, 0, 8, 8),
            Visibility = Visibility.Collapsed,
            Child = body,
        };

        _managerSlide = new TranslateTransform { Y = -10 };
        _managerPanel.RenderTransform = _managerSlide;

        // 面板本身铺满整行，内容是里面那层 body：宽度按窗口重算，宽窗最多 1200 并居中。
        // 高度一变（删板、重填行）影就跟着下沿走——只在打开那一拍摆一次的话，删掉几块板之后
        // 影会留在原来那摞行的下沿（他 11-03 报的"阴影留在原地"）。
        _managerPanel.SizeChanged += (_, _) =>
        {
            SizeBoardManagerBody();
            PlaceManagerShadow();
        };

        // 挂回 Root 那一行：BoardArea 里层层都是带变换的板面件，垫在里面量出来只有一个矮壳。
        // 取证走 `--mode chrome`（拍 Root），照样拍得到。
        Grid.SetRow(_managerPanel, 1);
        Canvas.SetZIndex(_managerPanel, 40);
        Root.Children.Add(_managerPanel);

        // 面板底下一条很软的影：贴在板面上的数据表也要有"抬起来"的层次，
        // 但不能变成第二条边——它不是线，是从上往下淡掉的影。
        var shadow = new LinearGradientBrush
        {
            StartPoint = new Windows.Foundation.Point(0, 0),
            EndPoint = new Windows.Foundation.Point(0, 1),
        };
        shadow.GradientStops.Add(new GradientStop
        {
            Color = Windows.UI.Color.FromArgb(28, 0, 0, 0),
            Offset = 0,
        });
        shadow.GradientStops.Add(new GradientStop
        {
            Color = Windows.UI.Color.FromArgb(0, 0, 0, 0),
            Offset = 1,
        });

        _managerShadow = new Border
        {
            Height = 10,
            VerticalAlignment = VerticalAlignment.Top,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            Background = shadow,
            IsHitTestVisible = false,
            Visibility = Visibility.Collapsed,
        };

        Grid.SetRow(_managerShadow, 1);
        Canvas.SetZIndex(_managerShadow, 39);
        Root.Children.Add(_managerShadow);
    }

    /// <summary>表头与每一行共用这一套列宽，所以上下对得齐；行的外壳也在这里，改一处两处一起动。</summary>
    private Border ManagerRowGrid(bool isHeader, bool isLast = false)
    {
        var grid = new Grid { ColumnSpacing = ManagerColumnGap };

        // 五列数据全部是星号列 + 最小宽度：窗口变窄时由它们一起缩，动作列永远留在可视区。
        // 旧版把 260/150/150/72/88 写死，窄窗时右端直接越出可视区（他 10-26 的截图）。
        // 日期列的最低宽度也放低：窄窗时宁可省略成「2026-10-…」，也不能把右侧三枚动作挤出屏幕。
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1.15, GridUnitType.Star), MinWidth = 100 });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1.0, GridUnitType.Star), MinWidth = 60 });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1.0, GridUnitType.Star), MinWidth = 60 });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(0.5, GridUnitType.Star), MinWidth = 36 });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(0.6, GridUnitType.Star), MinWidth = 40 });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(ManagerActionsWidth) });

        var tint = NoteStyling.IsDark ? new Rgb(255, 255, 255) : new Rgb(27, 29, 31);

        if (isHeader)
        {
            var muted = NoteStyling.ToColor(Badge.Muted(NoteStyling.IsDark));
            var labels = new[] { "名称", "创建时间", "修改时间", "便签", "大小" };

            for (var column = 0; column < labels.Length; column++)
            {
                // 五列表头压在同一中线上：名称也居中（他 10-26 的第二条——只有它靠左，读起来像漏对齐）。
                var cell = ManagerText(labels[column], muted, column, center: true);
                cell.Opacity = 1;
                cell.FontSize = 12;
                cell.FontWeight = Microsoft.UI.Text.FontWeights.Medium;

                if (column == 0)
                {
                    // 名字前面那格状态牌是常驻的：表头跟着让出那一段，才和名字共用同一条中线。
                    cell.Margin = new Thickness(ManagerStatusSlot, 0, 0, 0);
                }

                grid.Children.Add(cell);
            }
        }

        var shell = new Border
        {
            Height = isHeader ? 32 : 40,
            // 左右留白：内容不许贴着面板边（他 10-26 的第二条）。行的分隔线也缩进这么多，见下。
            Padding = new Thickness(ManagerSidePadding, 0, ManagerSidePadding, 0),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            // 表头下沿是一条整宽的实线；行与行之间是缩进一个留白的细线（见下）。
            BorderThickness = new Thickness(0, 0, 0, isHeader ? 1 : 0),
            BorderBrush = ChromeStyling.PanelLine,
            CornerRadius = isLast ? new CornerRadius(0, 0, 8, 8) : new CornerRadius(0),
            Background = isHeader
                ? new SolidColorBrush(NoteStyling.WithAlpha(tint, 0.065))
                : new SolidColorBrush(Microsoft.UI.Colors.Transparent),
            Child = grid,
        };

        if (!isHeader)
        {
            if (!isLast)
            {
                // 线跟着左右留白一起缩进，与内容左缘齐；画到面板边上会像表格外的框，而不是行间分隔。
                var rule = new Border
                {
                    Height = 1,
                    VerticalAlignment = VerticalAlignment.Bottom,
                    HorizontalAlignment = HorizontalAlignment.Stretch,
                    Background = new SolidColorBrush(NoteStyling.WithAlpha(tint, 0.11)),
                };
                Grid.SetColumnSpan(rule, 6);
                grid.Children.Add(rule);
            }

            var hover = new SolidColorBrush(NoteStyling.WithAlpha(tint, 0.04));
            var clear = new SolidColorBrush(Microsoft.UI.Colors.Transparent);

            shell.PointerEntered += (_, _) => shell.Background = hover;
            shell.PointerExited += (_, _) => shell.Background = clear;
        }

        return shell;
    }

    /// <summary>表头和行共用的那一套列宽随窗口铺开：宽窗最多 1200 并居中，窄窗跟着收。</summary>
    private void SizeBoardManagerBody()
    {
        if (_managerBody is null || _managerPanel is null)
        {
            return;
        }

        var available = _managerPanel.ActualWidth;

        if (available <= 0)
        {
            return;
        }

        var width = Math.Min(available, ManagerBodyMaxWidth);

        if (Math.Abs(_managerBody.Width - width) > 0.01)
        {
            _managerBody.Width = width;
        }
    }

    /// <summary>底下那条软影贴在面板下沿：面板矮了影就跟着上移（删板之后行少了，影不许留在原地）。</summary>
    private void PlaceManagerShadow()
    {
        if (_managerShadow is null || _managerPanel is null)
        {
            return;
        }

        _managerShadow.Margin = new Thickness(0, _managerPanel.ActualHeight, 0, 0);
    }

    /// <summary>把盘上现有的板读成一行行；读不懂的那块照样列出来，只是格数写"读不出"。</summary>
    private void FillBoardManager()
    {
        if (_managerRows is null || _boardPath is null)
        {
            return;
        }

        var folder = Path.GetDirectoryName(_boardPath);

        if (string.IsNullOrEmpty(folder))
        {
            return;
        }

        var lines = new List<BoardLine>();

        foreach (var path in BoardLocator.BoardsIn(folder))
        {
            // 「未归档」是「所有便签」的容器，不是一块画板：列进来就会被人去删它（他 10-26 的第二条）。
            if (IsUnfiledBoard(path))
            {
                continue;
            }

            var info = new FileInfo(path);

            if (!info.Exists)
            {
                continue;
            }

            lines.Add(new BoardLine(
                path,
                BoardName(path),
                info.CreationTime,
                info.LastWriteTime,
                NoteCountOf(path),
                info.Length,
                _openBoards.Exists(open => SameBoard(open, path))));
        }

        lines.Sort((left, right) => right.Changed.CompareTo(left.Changed));

        _managerRows.Children.Clear();
        _managerRows.RowDefinitions.Clear();

        for (var index = 0; index < lines.Count; index++)
        {
            _managerRows.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            var row = ManagerRow(lines[index], isLast: index == lines.Count - 1);
            Grid.SetRow(row, index);
            _managerRows.Children.Add(row);
        }

    }

    /// <summary>
    /// 画板表的影跟着面板下沿走（他 11-03 报的"删掉几块板以后阴影留在原地"）。
    /// 不碰磁盘：往行容器里加一截假行把面板撑高，影必须跟着下移；收回后跟回来。
    /// </summary>
    private void SelfTestManagerShadow(List<string> problems)
    {
        var wasOpen = _boardManagerOpen;
        Border? fake = null;

        try
        {
            SetBoardManager(true);

            if (_managerPanel is null || _managerShadow is null || _managerRows is null)
            {
                problems.Add("自检画板表: 面板/影子/行容器没建出来");

                return;
            }

            Root.UpdateLayout();
            _managerPanel.UpdateLayout();

            var panelHeight = _managerPanel.ActualHeight;

            if (panelHeight <= 0)
            {
                problems.Add($"自检画板表: 面板没展开（高 {panelHeight:0.#}）——前置没立起来");

                return;
            }

            if (Math.Abs(_managerShadow.Margin.Top - panelHeight) > 0.6)
            {
                problems.Add($"自检画板表: 一打开影就没贴到下沿（影 {_managerShadow.Margin.Top:0.#}、板 {panelHeight:0.#}）");
            }

            // 一截假行把面板撑高 40：影必须跟着走（删板走的是同一条 SizeChanged）。
            _managerRows.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            fake = new Border { Height = 40 };
            Grid.SetRow(fake, _managerRows.RowDefinitions.Count - 1);
            _managerRows.Children.Add(fake);
            _managerPanel.InvalidateMeasure();
            Root.UpdateLayout();

            var grown = _managerPanel.ActualHeight;

            if (Math.Abs(_managerShadow.Margin.Top - grown) > 0.6)
            {
                problems.Add($"自检画板表: 面板长高后影没跟上（影 {_managerShadow.Margin.Top:0.#}、板 {grown:0.#}）");
            }
            else if (Math.Abs(grown - panelHeight - 40) > 0.6)
            {
                problems.Add($"自检画板表: 假行没把面板撑高（{panelHeight:0.#}→{grown:0.#}）");
            }
            else
            {
                // 收回假行＝删板后的形状：影跟着收回去。
                _managerRows.Children.Remove(fake);
                _managerRows.RowDefinitions.RemoveAt(_managerRows.RowDefinitions.Count - 1);
                fake = null;
                _managerPanel.InvalidateMeasure();
                Root.UpdateLayout();

                if (Math.Abs(_managerShadow.Margin.Top - _managerPanel.ActualHeight) > 0.6)
                {
                    problems.Add($"自检画板表: 收回行以后影没跟回来（影 {_managerShadow.Margin.Top:0.#}、板 {_managerPanel.ActualHeight:0.#}）");
                }
                else
                {
                    Diagnostics.Write($"selftest 画板表: 影随面板下沿（{panelHeight:0.#}→{grown:0.#}→{_managerPanel.ActualHeight:0.#}）");
                }
            }
        }
        finally
        {
            if (fake is not null && _managerRows is not null && _managerRows.Children.Contains(fake))
            {
                _managerRows.Children.Remove(fake);

                if (_managerRows.RowDefinitions.Count > 0)
                {
                    _managerRows.RowDefinitions.RemoveAt(_managerRows.RowDefinitions.Count - 1);
                }
            }

            SetBoardManager(wasOpen);
        }
    }

    private Border ManagerRow(BoardLine line, bool isLast)
    {
        var shell = ManagerRowGrid(isHeader: false, isLast: isLast);
        var grid = (Grid)shell.Child;
        var ink = NoteStyling.InkFor(NoteStyling.SurfaceFor(null));
        var muted = NoteStyling.ToColor(Badge.Muted(NoteStyling.IsDark));

        grid.Children.Add(ManagerNameCell(line, ink));

        // 两列日期用同一把尺；日期和两个数字都居中，和表头压在同一条中线上。
        // 值用正文墨色、不用次要灰：13px 的灰字在 40px 行里看起来就是"单薄"。
        grid.Children.Add(ManagerText(line.Created.ToString("yyyy-MM-dd HH:mm"), ink, 1, center: true));
        grid.Children.Add(ManagerText(line.Changed.ToString("yyyy-MM-dd HH:mm"), ink, 2, center: true));
        grid.Children.Add(ManagerText(line.Notes < 0 ? "读不出" : line.Notes.ToString(), ink, 3, center: true));
        grid.Children.Add(ManagerText(line.Notes < 0 ? "—" : AttachmentStore.FormatSize(line.Bytes), ink, 4, center: true));

        var actions = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 6,
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Center,
        };

        actions.Children.Add(ManagerIcon(FluentIcons.Open, "打开", muted, () => OpenBoardFromManager(line.Path)));
        actions.Children.Add(ManagerIcon(FluentIcons.FolderOpen, "在文件夹中打开", muted, () => RevealBoardFile(line.Path)));
        actions.Children.Add(ManagerIcon(FluentIcons.Delete, "删除", muted, () => DeleteBoardFromManager(line.Path)));

        Grid.SetColumn(actions, 5);
        grid.Children.Add(actions);

        return shell;
    }

    /// <summary>名字这一格：一枚状态牌 ＋ 板名。开着的牌写「已开启」，没开的不挂空牌。</summary>
    private static Grid ManagerNameCell(BoardLine line, Windows.UI.Color ink)
    {
        var cell = new Grid { VerticalAlignment = VerticalAlignment.Center };
        cell.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(ManagerStatusSlot) });
        cell.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        if (line.Open)
        {
            var pill = new Border
            {
                Background = EntityCardPalette.Tint(ChromeStyling.Ring.Color, 0x1C),
                CornerRadius = new CornerRadius(10),
                Height = 20,
                Padding = new Thickness(8, 0, 8, 0),
                Margin = new Thickness(0, 0, 8, 0),
                VerticalAlignment = VerticalAlignment.Center,
                Child = new TextBlock
                {
                    Text = "已开启",
                    FontSize = 11,
                    VerticalAlignment = VerticalAlignment.Center,
                    Foreground = new SolidColorBrush(ChromeStyling.Ring.Color),
                },
            };

            cell.Children.Add(pill);
        }

        var name = new TextBlock
        {
            Text = line.Name,
            FontSize = 14,
            FontWeight = Microsoft.UI.Text.FontWeights.Medium,
            TextTrimming = TextTrimming.CharacterEllipsis,
            // 状态牌占掉左边那一格之后，名字在自己的列里居中：表头「名称」压在同一条中线上。
            TextAlignment = TextAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = new SolidColorBrush(ink),
        };

        // 名字列是固定宽：长名字会被省略号截断，完整名字留在提示里。
        ToolTipService.SetToolTip(name, line.Name);

        Grid.SetColumn(name, 1);
        cell.Children.Add(name);

        return cell;
    }

    private static TextBlock ManagerText(string text, Windows.UI.Color color, int column, bool center = false)
    {
        var block = new TextBlock
        {
            Text = text,
            FontSize = 14,
            TextTrimming = TextTrimming.CharacterEllipsis,
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = new SolidColorBrush(color),
            HorizontalAlignment = center ? HorizontalAlignment.Center : HorizontalAlignment.Left,
        };

        Grid.SetColumn(block, column);

        return block;
    }

    private static Button ManagerIcon(string icon, string name, Windows.UI.Color color, Action onClick)
    {
        var button = new Button
        {
            Width = 30,
            Height = 30,
            Padding = new Thickness(0),
            Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent),
            BorderThickness = new Thickness(0),
            Content = FluentIcons.Glyph(icon, 16, color),
        };

        AutomationProperties.SetName(button, name);
        ToolTipService.SetToolTip(button, name);
        button.Click += (_, _) => onClick();

        return button;
    }

    private static int NoteCountOf(string path)
    {
        try
        {
            return CanvasDocument.Parse(CanvasStore.ReadAllText(path), path).Nodes.Count(node => node.Kind != CanvasNodeKind.Group);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or CanvasDocumentException or InvalidOperationException)
        {
            Diagnostics.Write($"board manager: 读不出 {path}：{ex.Message}");
            return -1;
        }
    }

    private void OpenBoardFromManager(string path)
    {
        if (_openBoards.Exists(open => SameBoard(open, path)))
        {
            SwitchToBoard(path);
            Say($"已切换到「{BoardName(path)}」");
        }
        else
        {
            _openBoards.Add(path);
            SwitchToBoard(path);
            RebuildTabs();
            Say($"已打开「{BoardName(path)}」");
        }

        SetBoardManager(false);
    }

    private void RevealBoardFile(string path)
    {
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("explorer.exe", $"/select,\"{path}\"") { UseShellExecute = true });
            Say($"已在资源管理器中定位 {Path.GetFileName(path)}");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
        {
            Say($"打不开文件夹：{ex.Message}");
        }
    }

    /// <summary>删一块板：唯一一条会动盘的路。最后一块不许删——删完整扇窗就没板可画了。</summary>
    private async void DeleteBoardFromManager(string path)
    {
        var name = BoardName(path);

        if (RealBoardCount <= 1)
        {
            Say($"无法删除最后一块画板「{name}」：请先新建一块");

            return;
        }

        if (!await ConfirmBoardDeleteAsync([path]))
        {
            Say($"已取消删除，{name} 仍保留");

            return;
        }

        if (SameBoard(path, _boardPath ?? string.Empty))
        {
            SwitchToBoard(NeighbourBoard(CurrentBoardIndex()));
        }

        var error = DeleteBoard(path);

        if (error is not null)
        {
            Say($"未能删除「{name}」：{error}");
        }
        else
        {
            _openBoards.RemoveAll(open => SameBoard(open, path));
            RebuildTabs();
            Say($"已删除「{name}」· 本机文件、相机记录与历史快照已移除；远端仓库中的副本未改动");
        }

        FillBoardManager();
    }

    private void OnRootPressedCloseManager(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
    {
        if (!_boardManagerOpen)
        {
            return;
        }

        var source = e.OriginalSource as DependencyObject;

        if (IsInsideManagerPart(source, _managerPanel) || IsInsideManagerPart(source, _managerEntry))
        {
            return;
        }

        SetBoardManager(false);
    }

    private static bool IsInsideManagerPart(DependencyObject? source, DependencyObject? owner)
    {
        if (owner is null)
        {
            return false;
        }

        for (var node = source; node is not null; node = VisualTreeHelper.GetParent(node))
        {
            if (ReferenceEquals(node, owner))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>标题栏里的入口：双击的第一下开合，第二下只更新时间、不再合回去。</summary>
    private void ToggleBoardManagerFromTitleBar()
    {
        var now = Environment.TickCount64;

        if (now - _lastManagerClickTicks < DoubleClickWindowMs)
        {
            _lastManagerClickTicks = now;

            return;
        }

        _lastManagerClickTicks = now;
        ToggleBoardManager();
    }

    /// <summary>标签栏最左端那枚图标钮（在所有便签的左侧）：点一下开合这张表。</summary>
    private void BuildBoardManagerEntry()
    {
        if (_managerEntry is not null)
        {
            return;
        }

        var muted = NoteStyling.ToColor(Badge.Muted(NoteStyling.IsDark));
        _managerEntry = new Button
        {
            Width = 30,
            Height = 30,
            MinWidth = 0,
            Padding = new Thickness(0),
            Margin = new Thickness(4, 4, 0, 0),
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Top,
            Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent),
            BorderThickness = new Thickness(0),
            CornerRadius = new CornerRadius(6),
            Content = FluentIcons.Glyph(FluentIcons.List, 16, muted),
        };

        AutomationProperties.SetName(_managerEntry, "画板");
        ToolTipService.SetToolTip(_managerEntry, "画板");
        _managerEntry.Click += (_, _) => ToggleBoardManagerFromTitleBar();

        // 开着的下拉表点别处就收起：从 Root 收这一发（handledEventsToo），
        // 面板里和入口上的按下不算"别处"。
        Root.AddHandler(
            UIElement.PointerPressedEvent,
            new Microsoft.UI.Xaml.Input.PointerEventHandler(OnRootPressedCloseManager),
            handledEventsToo: true);

        // 这一枚现在也在标题栏的 Passthrough 区里，系统不会再把双击读成"最大化"；
        // 但 XAML 仍会送两次 Click，第二下在这里吃掉，开合只走一次。
        _managerEntry.DoubleTapped += (_, e) => e.Handled = true;

        // 标签项整体右移这 30px，让第一枚标签落在图标钮右边。
        BoardTabs.Margin = new Thickness(34, 0, 0, 0);
        TitleBarHost.Children.Add(_managerEntry);
    }
}
