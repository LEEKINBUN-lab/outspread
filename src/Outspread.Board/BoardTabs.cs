using System.IO;
using Microsoft.UI;
using Microsoft.UI.Input;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Automation.Provider;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Outspread.Core;
using Windows.Foundation;
using Windows.Graphics;

namespace Outspread.Board;

/// <summary>
/// 一块板一个标签：顶上那条标签栏就是标题栏，点一枚标签＝把这扇窗口挪去处理另一件事。
/// </summary>
/// <remarks>
/// <para>
/// 窗口里"属于这块板"的东西一共六样：文档、相机、外部改动监视、取证管道、同步、撤销历史。
/// 切板必须先把它交回上一块板（一次强制存盘），再一块块指给下一块。<c>LoadBoard</c> 本来就是
/// 按"这一块板"建的（watcher / shot / sync 各自先停旧的再建新的），所以这里只补两件事：
/// 切之前存盘，切之后把只属于旧板子的运行时状态清空。
/// </para>
/// <para>
/// 清空那一格不是打扫，是防串：<c>_pendingSnapshot</c> 里躺着的是上一块板的整份 JSON，留着它，
/// 下一发撤销会把另一块板的内容写进这一块。<c>_editor</c> 与 <c>_lastWrittenHash</c> 同理。
/// </para>
/// <para>
/// 历史与筛选切走即弃（2026-10-21 定）：pan/zoom 进 sidecar，那份文件本来就一块板一份；撤销栈
/// 不进——要进就得扩 sidecar 格式，而安卓端和 agent 都读这份文件。
/// </para>
/// <para>
/// 最后一块板不给关。× 仍然画在那儿（皮是平台的，不为一枚按钮去重模板），按下去只说一句为什么。
/// </para>
/// <para>
/// 2026-10-22：他点名的"难区分/加号奇怪/新建浮窗不好看"把 10-21 的"皮全交给平台"覆盖掉了。
/// 骨架仍是原生 <see cref="TabView"/>（键盘、溢出、中键关、标题栏拖拽都还是平台的），但活动
/// 标签填成板底色、加号紧跟最后一枚、右键菜单与行内改名都在这一层补。数值全读 Core 的
/// <see cref="TabStrip"/>；这一层只负责把它们穿到 TabView 的资源与每个 TabViewItem 上。
/// </para>
/// </remarks>
public sealed partial class MainWindow
{
    /// <summary>开着的板子，顺序＝标签的顺序。当前那块一定在里面。</summary>
    private readonly List<string> _openBoards = [];

    /// <summary>程序自己动 TabItems 时平台也会发 SelectionChanged，那一条不能读成"用户点了另一枚"。</summary>
    private bool _tabsBusy;

    /// <summary>正在改名的那一枚。没有面板可判"键盘归谁"，所以拖动它时整条标签条按这一格让键盘。</summary>
    private string? _renamingPath;

    private TextBox? _renameBox;

    /// <summary>提交/取消已经在走时，LostFocus 不许再进一次——它会对着已经被换掉的头再提交一遍。</summary>
    private bool _renameBusy;

    /// <summary>取证档 <c>--panel rename</c> 只换出输入框给人看；这一格不提交、不改文件名。</summary>
    private bool _renamePreview;

    /// <summary>量加号位置时自己也会改 Margin、触发一次布局；这个门闩防止 LayoutUpdated 自激。</summary>
    private bool _positioningAdd;

    /// <summary>标题栏里哪些矩形算客户区（标签与加号）。不标的话双击会被当成"最大化"、右键会弹系统窗口菜单。</summary>
    private InputNonClientPointerSource? _tabPassthrough;

    private string? _passthroughSignature;

    /// <summary>平台 Caption 区上一次写进去的物理尺寸；窗口没变就不重设，免得每次布局都动输入区。</summary>
    private int _captionWidthPx;
    private int _captionHeightPx;

    /// <summary>
    /// 建窗时搭一次：标签条自己的皮只在 Theme 变时重穿；右键菜单的条目每次打开时现算，
    /// 因为"盘上还有哪些板"是盘上的事实，不该在菜单对象里冻住。
    /// </summary>
    private void BuildBoardTabs()
    {
        StyleTabStripChrome();

        TitleBarHost.ContextFlyout = BuildStripFlyout();
        AddBoardButton.ContextFlyout = BuildStripFlyout();
        AddBoardButton.Click += (_, _) => CreateNewBoard();

        // 加号的位置跟着最后一枚标签走：标题、语言、DPI 一变就要重量。LayoutUpdated 会在
        // 排版之后发，量到的 item.ActualWidth 才是真的；PositionAddBoardButton 自己带防自激门闩。
        BoardTabs.LayoutUpdated += (_, _) => PositionAddBoardButton();
        TitleBarHost.SizeChanged += (_, _) => PositionAddBoardButton();
    }

    /// <summary>
    /// 把 <see cref="TabStrip"/> 的那组数穿到 TabView 和宿主的皮上。颜色走主题字典，指标走
    /// 基础资源；两个都放在 TabView 自己的资源里，不污染根上那两枚"直角"资源。
    /// </summary>
    private void StyleTabStripChrome()
    {
        var dark = NoteStyling.IsDark;

        TitleBarHost.Background = new SolidColorBrush(NoteStyling.ToColor(TabStrip.StripBackground(dark)));
        TabStripLine.Fill = new SolidColorBrush(NoteStyling.WithAlpha(TabStrip.Line(dark), TabStrip.LineAlpha(dark)));
        BoardTabs.Background = new SolidColorBrush(Colors.Transparent);

        var resources = new ResourceDictionary
        {
            ["ControlCornerRadius"] = new CornerRadius(TabStrip.CloseRadius),
            ["TabViewItemMinHeight"] = TabStrip.TabHeight,
            ["TabViewItemMinWidth"] = TabStrip.TabMinWidth,
            ["TabViewItemMaxWidth"] = TabStrip.TabMaxWidth,
            ["TabViewItemHeaderFontSize"] = TabStrip.FontPx,
            ["TabViewItemHeaderPadding"] = new Thickness(TabStrip.PadLeft, 0, TabStrip.PadRight, 0),

            // 没关闭键的那一档把 × 的整格（24 命中 + 2 边距 + 4 内缩）留空：非活动标签的 ×
            // 是 Collapsed 的，若两档内缩不一样，鼠标移上去整枚标签会横向长一截。
            ["TabViewItemHeaderPaddingWithCloseButton"] = new Thickness(TabStrip.PadLeft, 0, TabStrip.PadRight, 0),
            ["TabViewItemHeaderPaddingWithoutCloseButton"] = new Thickness(TabStrip.PadLeft, 0, TabStrip.CloseReservePx, 0),
            ["TabViewItemHeaderCloseButtonWidth"] = TabStrip.CloseHitPx,
            ["TabViewItemHeaderCloseButtonHeight"] = TabStrip.CloseHitPx,
            ["TabViewItemHeaderCloseFontSize"] = TabStrip.CloseGlyphPx,
            ["TabViewItemHeaderCloseMargin"] = new Thickness(TabStrip.CloseMarginPx, 0, 0, 0),
            ["TabViewItemSeparatorMargin"] = new Thickness(0, TabStrip.SeparatorInset, 0, TabStrip.SeparatorInset),
            ["TabViewItemBorderThickness"] = new Thickness(0),
            ["TabViewSelectedItemBorderThickness"] = new Thickness(1, 1, 1, 0),
            ["TabViewSelectedItemHeaderMargin"] = new Thickness(-1, 0, -1, 0),
        };

        resources.ThemeDictionaries["Light"] = TabPalette(dark: false);
        resources.ThemeDictionaries["Dark"] = TabPalette(dark: true);

        // 高对比度不另发明一套：用深档至少保证"看得见"；这一层以后要按系统色重做。
        resources.ThemeDictionaries["HighContrast"] = TabPalette(dark: true);

        BoardTabs.Resources = resources;

        // 平台给内部 TabListView 的默认项目过渡是 AddDeleteThemeTransition：新标签会先透明/空一拍
        // 再把整枚淡进来。他 10-22 点加号看到的"先出一个空白标签"就是这一段（实测点后约
        // 170ms 加号已让位、标签仍整枚不可见，400ms 后才带名字出现）。标签是导航件，名字
        // 必须和它同时到场；行内那点动效交给 TabContainer 的 BrushTransition 与选中填充。
        SuppressTabEntranceTransitions();

        // 标签高 34 + 上沿空气 4 = 条高 38；左边留 8，第一枚标签不贴窗框。
        // TabView 的 Padding 只落在自己的 TabListView 上。
        BoardTabs.Padding = new Thickness(8, TabStrip.StripHeight - TabStrip.TabHeight, 0, 0);

        StyleAddBoardButton(dark);
        PositionAddBoardButton();
    }

    /// <summary>加号是宿主里一枚真 Button（不是 TabView 模板里那枚）：位置由我们量、皮由令牌给。</summary>
    private void StyleAddBoardButton(bool dark)
    {
        var transparent = new SolidColorBrush(Colors.Transparent);
        var hover = Alpha(TabStrip.Hover(dark), TabStrip.HoverAlpha(dark));
        var pressed = Alpha(TabStrip.Line(dark), TabStrip.HoverAlpha(dark) * 1.8);
        var muted = new SolidColorBrush(NoteStyling.ToColor(TabStrip.InactiveInk(dark)));
        var ink = new SolidColorBrush(NoteStyling.ToColor(TabStrip.ActiveInk(dark)));

        AddBoardButton.Width = TabStrip.AddPx;
        AddBoardButton.Height = TabStrip.AddPx;
        AddBoardButton.CornerRadius = new CornerRadius(TabStrip.AddRadius);
        AddBoardButton.FontFamily = NoteStyling.NoteFont;
        AddBoardButton.FontSize = TabStrip.AddGlyphPx;
        AddBoardButton.Padding = new Thickness(0);
        AddBoardButton.BorderThickness = new Thickness(0);
        AddBoardButton.Background = transparent;
        AddBoardButton.Foreground = muted;
        AddBoardButton.HorizontalContentAlignment = HorizontalAlignment.Center;
        AddBoardButton.VerticalContentAlignment = VerticalAlignment.Center;

        // 默认 Button 模板的几枚状态刷子：不覆盖就是微软的灰底和边框压在我们的条上。
        AddBoardButton.Resources["ButtonBackground"] = transparent;
        AddBoardButton.Resources["ButtonBackgroundPointerOver"] = hover;
        AddBoardButton.Resources["ButtonBackgroundPressed"] = pressed;
        AddBoardButton.Resources["ButtonBackgroundDisabled"] = transparent;
        AddBoardButton.Resources["ButtonBorderBrush"] = transparent;
        AddBoardButton.Resources["ButtonBorderBrushPointerOver"] = transparent;
        AddBoardButton.Resources["ButtonBorderBrushPressed"] = transparent;
        AddBoardButton.Resources["ButtonBorderBrushDisabled"] = transparent;
        AddBoardButton.Resources["ButtonForeground"] = muted;
        AddBoardButton.Resources["ButtonForegroundPointerOver"] = ink;
        AddBoardButton.Resources["ButtonForegroundPressed"] = ink;
        AddBoardButton.Resources["ButtonForegroundDisabled"] = new SolidColorBrush(NoteStyling.ToColor(Sheet.Quiet(dark)));
    }

    /// <summary>
    /// 量最后一枚标签的右缘，把加号放在它后面 4px。标签少时加号跟着最后一枚走；标签多到
    /// 贴住右侧系统按钮时，加号留在最后一个能放下的位置，不钻到最小化底下。
    /// </summary>
    private void PositionAddBoardButton()
    {
        if (_positioningAdd || TitleBarHost.ActualWidth <= 0)
        {
            return;
        }

        EnsureTabTransitions();
        SuppressTabEntranceTransitions();
        UpdateTabPassthroughRegions();
        UpdateResizeBorderRegions();

        var last = BoardTabs.TabItems.OfType<TabViewItem>().LastOrDefault();
        var left = 8d;

        if (last is not null && last.ActualWidth > 0.5)
        {
            // 量 TabContainer 而不是整枚 TabViewItem：item 的右缘还含关闭键那一列透明内缩，
            // 拿它算会得到"加号离看得见的标签 15px"——原型要的是 4。
            var anchor = FindDescendant<Grid>(last, "TabContainer") ?? (FrameworkElement)last;
            var box = anchor.TransformToVisual(TitleBarHost).TransformBounds(
                new Rect(0, 0, anchor.ActualWidth, anchor.ActualHeight));

            left = box.Right + 4;
        }

        var available = TitleBarHost.ActualWidth - TitleBarHost.Padding.Right;
        left = Math.Clamp(left, 8, Math.Max(8, available - TabStrip.AddPx - 4));

        var top = (TabStrip.StripHeight - TabStrip.AddPx) / 2;
        var current = AddBoardButton.Margin;

        if (Math.Abs(current.Left - left) < 0.5 && Math.Abs(current.Top - top) < 0.5)
        {
            return;
        }

        _positioningAdd = true;

        try
        {
            AddBoardButton.Margin = new Thickness(left, top, 0, 0);
        }
        finally
        {
            _positioningAdd = false;
        }
    }

    /// <summary>浅深两档的刷子。所有值来自 <see cref="TabStrip"/>，这里只做 Rgb → Brush。</summary>
    private static ResourceDictionary TabPalette(bool dark)
    {
        var transparent = new SolidColorBrush(Colors.Transparent);
        var ink = new SolidColorBrush(NoteStyling.ToColor(TabStrip.ActiveInk(dark)));
        var muted = new SolidColorBrush(NoteStyling.ToColor(TabStrip.InactiveInk(dark)));
        var quiet = new SolidColorBrush(NoteStyling.ToColor(Sheet.Quiet(dark)));
        var hover = Alpha(TabStrip.Hover(dark), TabStrip.HoverAlpha(dark));
        var pressed = Alpha(TabStrip.Line(dark), TabStrip.HoverAlpha(dark) * 1.8);
        var closeHover = Alpha(TabStrip.Line(dark), TabStrip.CloseHoverAlpha(dark));
        var separator = Alpha(TabStrip.Line(dark), TabStrip.SeparatorAlpha(dark));
        var edge = Alpha(TabStrip.Line(dark), TabStrip.EdgeAlpha(dark));
        var active = new SolidColorBrush(NoteStyling.ToColor(TabStrip.ActiveFill(dark)));

        var dictionary = new ResourceDictionary
        {
            ["TabViewBackground"] = transparent,
            ["TabViewItemHeaderBackground"] = transparent,
            ["TabViewItemHeaderBackgroundSelected"] = active,
            ["TabViewItemHeaderDragBackground"] = transparent,
            ["TabViewItemHeaderBackgroundPointerOver"] = hover,
            ["TabViewItemHeaderBackgroundPressed"] = pressed,
            ["TabViewItemHeaderBackgroundDisabled"] = transparent,
            ["TabViewItemHeaderForeground"] = muted,
            ["TabViewItemHeaderForegroundPressed"] = muted,
            ["TabViewItemHeaderForegroundSelected"] = ink,
            ["TabViewItemHeaderForegroundPointerOver"] = ink,
            ["TabViewItemHeaderForegroundDisabled"] = quiet,
            ["TabViewItemIconForeground"] = muted,
            ["TabViewItemIconForegroundPressed"] = muted,
            ["TabViewItemIconForegroundSelected"] = ink,
            ["TabViewItemIconForegroundPointerOver"] = ink,
            ["TabViewItemIconForegroundDisabled"] = quiet,
            ["TabViewButtonBackground"] = transparent,
            ["TabViewButtonBackgroundPressed"] = pressed,
            ["TabViewButtonBackgroundPointerOver"] = hover,
            ["TabViewButtonBackgroundDisabled"] = transparent,
            ["TabViewButtonForeground"] = muted,
            ["TabViewButtonForegroundPressed"] = ink,
            ["TabViewButtonForegroundPointerOver"] = ink,
            ["TabViewButtonForegroundDisabled"] = quiet,
            ["TabViewButtonBorderBrush"] = transparent,
            ["TabViewButtonBorderBrushPressed"] = transparent,
            ["TabViewButtonBorderBrushPointerOver"] = transparent,
            ["TabViewButtonBorderBrushDisabled"] = transparent,
            ["TabViewButtonBackgroundActiveTab"] = transparent,
            ["TabViewButtonForegroundActiveTab"] = ink,
            ["TabViewBorderBrush"] = transparent,
            ["TabViewItemBorderBrush"] = transparent,
            ["TabViewSelectedItemBorderBrush"] = edge,
            ["TabViewItemSeparator"] = separator,
            ["TabViewItemHeaderCloseButtonBackground"] = transparent,
            ["TabViewItemHeaderCloseButtonBackgroundPressed"] = pressed,
            ["TabViewItemHeaderCloseButtonBackgroundPointerOver"] = closeHover,
            ["TabViewItemHeaderPointerOverCloseButtonBackground"] = transparent,
            ["TabViewItemHeaderPressedCloseButtonBackground"] = transparent,
            ["TabViewItemHeaderSelectedCloseButtonBackground"] = transparent,
            ["TabViewItemHeaderDisabledCloseButtonBackground"] = transparent,
            ["TabViewItemHeaderCloseButtonForeground"] = muted,
            ["TabViewItemHeaderCloseButtonForegroundPressed"] = ink,
            ["TabViewItemHeaderCloseButtonForegroundPointerOver"] = ink,
            ["TabViewItemHeaderPressedCloseButtonForeground"] = ink,
            ["TabViewItemHeaderPointerOverCloseButtonForeground"] = ink,
            ["TabViewItemHeaderSelectedCloseButtonForeground"] = ink,
            ["TabViewItemHeaderDisabledCloseButtonForeground"] = quiet,
            ["TabViewItemHeaderCloseButtonBorderBrush"] = transparent,
            ["TabViewItemHeaderCloseButtonBorderBrushPointerOver"] = transparent,
            ["TabViewItemHeaderCloseButtonBorderBrushPressed"] = transparent,
            ["TabViewItemHeaderCloseButtonBorderBrushSelected"] = transparent,
            ["TabViewItemHeaderCloseButtonBorderBrushDisabled"] = transparent,
            ["TabViewScrollButtonBackground"] = transparent,
            ["TabViewScrollButtonBackgroundPointerOver"] = hover,
            ["TabViewScrollButtonBackgroundPressed"] = pressed,
            ["TabViewScrollButtonBackgroundDisabled"] = transparent,
            ["TabViewScrollButtonForeground"] = muted,
            ["TabViewScrollButtonForegroundPointerOver"] = ink,
            ["TabViewScrollButtonForegroundPressed"] = ink,
            ["TabViewScrollButtonForegroundDisabled"] = quiet,
            ["TabViewScrollButtonBorderBrush"] = transparent,
            ["TabViewScrollButtonBorderBrushPointerOver"] = transparent,
            ["TabViewScrollButtonBorderBrushPressed"] = transparent,
            ["TabViewScrollButtonBorderBrushDisabled"] = transparent,
        };

        return dictionary;
    }

    private static SolidColorBrush Alpha(Rgb rgb, double alpha)
        => new(NoteStyling.WithAlpha(rgb, alpha));

    private static string BoardName(string boardPath) => Path.GetFileNameWithoutExtension(boardPath);

    /// <summary>
    /// 标签条就是标题栏，所以它必须给系统那三枚按钮（最小化／最大化／关闭）让出宽度，并把让了多少
    /// 写进日志——最后一枚标签藏到"关闭"底下，是一张为了检查它而拍的图也看不出来的缺陷。
    /// 标签条自己的盒子由 <c>shot --mode chrome</c> 报，不在这里报：这一刻是 Loaded，还没排版。
    /// </summary>
    private void ReserveCaptionButtons()
    {
        // 系统框整条去掉后没有系统按钮可让；右侧让出的宽度就是我们自己三枚按钮的 138。
        var width = TabStrip.CaptionWidth;

        TitleBarHost.Padding = new Thickness(0, 0, width, 0);

        // 底发丝要一直走到窗口右缘（自绘按钮下面也一样）；宿主的内缩只该管标签与加号。
        TabStripLine.Margin = new Thickness(0, 0, -width, 0);
        CaptionButtons.Margin = new Thickness(0, 0, -width, 0);

        Diagnostics.Write(
            $"标题栏: 自绘按钮 3×{TabStrip.CaptionButtonWidth:0.#}={width:0.#} 布局像素，右侧让出 {width:0.#}");
    }

    private static bool SameBoard(string left, string right)
        => string.Equals(left, right, StringComparison.OrdinalIgnoreCase);

    private int CurrentBoardIndex()
        => _boardPath is null
            ? -1
            : _openBoards.FindIndex(open => SameBoard(open, _boardPath));

    // ---------------------------------------------------------------------
    // 标签条
    // ---------------------------------------------------------------------

    /// <summary>
    /// 让标签条和 <see cref="_openBoards"/> 一模一样。2026-10-22 改成增量：只增删/挪动真变了
    /// 的那几枚，而不是整条清空重加——整条重加会让平台把每一枚都走一遍进入动画，关一枚时
    /// 屏上所有标签一起闪，那正是他说的"不如浏览器流畅"。
    /// </summary>
    private void RebuildTabs()
    {
        _tabsBusy = true;

        try
        {
            var desired = _openBoards.ToList();

            for (var index = BoardTabs.TabItems.Count - 1; index >= 0; index--)
            {
                if (BoardTabs.TabItems[index] is TabViewItem item
                    && item.Tag is string tag
                    && desired.Exists(open => SameBoard(open, tag)))
                {
                    continue;
                }

                BoardTabs.TabItems.RemoveAt(index);
            }

            for (var index = 0; index < desired.Count; index++)
            {
                var path = desired[index];
                var at = TabIndexOf(path);

                if (at < 0)
                {
                    BoardTabs.TabItems.Insert(index, NewTabItem(path));
                }
                else if (at != index)
                {
                    var item = BoardTabs.TabItems[at];

                    BoardTabs.TabItems.RemoveAt(at);
                    BoardTabs.TabItems.Insert(index, item);
                }
            }

            foreach (var item in BoardTabs.TabItems.OfType<TabViewItem>())
            {
                if (item.Tag is not string path)
                {
                    continue;
                }

                if (_renamingPath is not null && SameBoard(_renamingPath, path) && item.Header is TextBox)
                {
                    item.MaxWidth = TabStrip.RenameTabMaxWidth;

                    continue;
                }

                item.MaxWidth = TabStrip.TabMaxWidth;
                UpdateHeader(item, BoardName(path));
            }

            if (_renamingPath is not null && !_openBoards.Exists(open => SameBoard(open, _renamingPath)))
            {
                _renamingPath = null;
                _renameBox = null;
            }

            var selected = CurrentBoardIndex();
            BoardTabs.SelectedIndex = selected < 0
                ? 0
                : Math.Min(selected, Math.Max(0, BoardTabs.TabItems.Count - 1));

            ApplyCloseButtonOverlayMode();

            // 任务栏与 Alt+Tab 上认得出是哪件事；界面上不重复说一遍。
            Title = _boardPath is null ? "铺陈" : $"铺陈 · {BoardName(_boardPath)}";
        }
        finally
        {
            _tabsBusy = false;
        }
    }

    /// <summary>
    /// 平台的 TabView 模板在模板里改背景，不会替我们做 120ms 的淡入；给每枚落地后的
    /// TabContainer 挂一枚 BrushTransition，悬停那一下才有浏览器那种过渡。选中态那枚
    /// 填充是 SelectedBackgroundPath 自己画的，不在这个过渡范围内。
    /// </summary>
    private void EnsureTabTransitions()
    {
        foreach (var item in BoardTabs.TabItems.OfType<TabViewItem>())
        {
            var container = FindDescendant<Grid>(item, "TabContainer");

            if (container is null)
            {
                continue;
            }

            if (container.BackgroundTransition is not null)
            {
                continue;
            }

            container.BackgroundTransition = new BrushTransition
            {
                Duration = TimeSpan.FromMilliseconds(TabStrip.HoverMs),
            };
        }
    }

    /// <summary>
    /// 平台把新标签的入场交给内部 TabListView 的 AddDeleteThemeTransition：整枚先不可见再淡入，
    /// 名字跟着晚一拍（他 10-22 拍的"空白标签"）。TabView 不公开这层过渡，只能等它的
    /// ListView 树里落地后清空；清空不影响 TabContainer 的悬停 BrushTransition。
    /// </summary>
    private void SuppressTabEntranceTransitions()
    {
        var list = FindDescendant<ListView>(BoardTabs, "TabListView");

        if (list is null || list.ItemContainerTransitions is not { Count: > 0 })
        {
            return;
        }

        list.ItemContainerTransitions = new TransitionCollection();
    }

    /// <summary>
    /// 原生 TabView 只在 OnPointerOver 属性**发生变化**时把它推给已有的 TabViewItem；
    /// 我们后加的项会退回 TabViewItem 自己的默认 Auto（× 常驻）。所以每次重建后人为
    /// 拨一次 Auto → OnPointerOver，让平台重新给所有项套上"只在活动/悬停时出现"。
    /// </summary>
    private void ApplyCloseButtonOverlayMode()
    {
        var wanted = TabViewCloseButtonOverlayMode.OnPointerOver;

        if (BoardTabs.CloseButtonOverlayMode == wanted)
        {
            BoardTabs.CloseButtonOverlayMode = TabViewCloseButtonOverlayMode.Auto;
        }

        BoardTabs.CloseButtonOverlayMode = wanted;
    }

    private int TabIndexOf(string path)
    {
        for (var index = 0; index < BoardTabs.TabItems.Count; index++)
        {
            if (BoardTabs.TabItems[index] is TabViewItem item
                && item.Tag is string tag
                && SameBoard(tag, path))
            {
                return index;
            }
        }

        return -1;
    }

    private TabViewItem? TabItemFor(string path)
        => BoardTabs.TabItems.OfType<TabViewItem>()
            .FirstOrDefault(item => item.Tag is string tag && SameBoard(tag, path));

    private TabViewItem NewTabItem(string path)
    {
        var item = new TabViewItem
        {
            Tag = path,
            IsClosable = true,
            CornerRadius = new CornerRadius(TabStrip.TopRadius, TabStrip.TopRadius, 0, 0),
            MinWidth = TabStrip.TabMinWidth,
            MaxWidth = TabStrip.TabMaxWidth,
            VerticalContentAlignment = VerticalAlignment.Center,

            // 平台默认会把行内容也走一遍 ContentThemeTransition；新标签会先出一个空壳、
            // 名字再淡进来——他说的"先弹出一个空白名称的标签页"。关掉内容过渡，
            // 标签本身的增删动画仍由 ItemContainerTransitions 负责。
            ContentTransitions = new TransitionCollection(),
        };

        SetHeader(item, BoardName(path));

        // 悬停时标题自己提一档墨：原生模板的 Foreground 落在 ContentPresenter 上，我们给的是
        // TextBlock（为了省略号），所以这一档由这一层补，和活动/非活动同一把尺。
        item.PointerEntered += (_, _) => PaintHeader(item, hovered: true);
        item.PointerExited += (_, _) => PaintHeader(item, hovered: false);
        item.ContextFlyout = BuildTabFlyout(item);

        // 双击标签＝就地把名字改成；标题栏的非客户区已经被 Passthrough 让开了，
        // 否则这一下会被系统读成"双击标题栏＝最大化"（他 10-22 报的那一格）。
        item.DoubleTapped += (_, e) =>
        {
            if (item.Tag is not string current
                || TabItemFor(current) is not { } owner
                || !ReferenceEquals(owner, item)
                || IsInsideButton(e.OriginalSource as DependencyObject))
            {
                return;
            }

            e.Handled = true;
            BeginRenameTab(current);
        };

        return item;
    }

    /// <summary>
    /// 把标签与加号那几块标成 Passthrough。标题栏其余部分仍是 Caption（拖窗口、空白双击最大化），
    /// 所以这里只把"真正可点的内容"让回客户区；系统那三枚按钮不在这些矩形里。
    /// </summary>
    private void UpdateTabPassthroughRegions()
    {
        if (AppWindow is null || Root.XamlRoot is null || !AppWindow.IsVisible)
        {
            return;
        }

        var scale = Root.XamlRoot.RasterizationScale;
        var rects = new List<RectInt32>();

        foreach (var item in BoardTabs.TabItems.OfType<TabViewItem>())
        {
            Add(item);
        }

        Add(AddBoardButton);
        Add(MinimizeButton);
        Add(MaximizeButton);
        Add(CloseButton);

        if (rects.Count == 0)
        {
            return;
        }

        // 自绘外壳的 WndProc 用这两份写死数据做命中：哪些矩形交客户区、标题条有多高。
        _passthroughRects = rects.ToArray();
        _titleBarHeightPx = TitleBarHost.ActualHeight * scale;

        _tabPassthrough ??= InputNonClientPointerSource.GetForWindowId(AppWindow.Id);

        // 不靠 SetTitleBar 时，平台的非客户输入层不会自动把这条当标题栏：显式标一块 Caption，
        // 空白处才拖得动窗口、双击才最大化。标签/加号/三枚按钮仍是 Passthrough，优先交客户区。
        var captionWidth = (int)Math.Round(Root.ActualWidth * scale);
        var captionHeight = (int)Math.Round(_titleBarHeightPx);

        if (captionWidth > 0 && captionHeight > 0
            && (captionWidth != _captionWidthPx || captionHeight != _captionHeightPx))
        {
            _tabPassthrough.ClearRegionRects(NonClientRegionKind.Caption);
            _tabPassthrough.SetRegionRects(
                NonClientRegionKind.Caption,
                [new RectInt32(0, 0, captionWidth, captionHeight)]);
            _captionWidthPx = captionWidth;
            _captionHeightPx = captionHeight;
        }

        var signature = string.Join(";", rects.Select(rect => $"{rect.X},{rect.Y},{rect.Width},{rect.Height}"));

        if (string.Equals(signature, _passthroughSignature, StringComparison.Ordinal))
        {
            return;
        }

        _tabPassthrough.ClearRegionRects(NonClientRegionKind.Passthrough);
        _tabPassthrough.SetRegionRects(NonClientRegionKind.Passthrough, rects.ToArray());
        _passthroughSignature = signature;

        Diagnostics.Write(
            $"标题栏: 交互区 {rects.Count} 块（标签 {BoardTabs.TabItems.Count} 枚 + 加号 + 3 枚标题按钮），" +
            $"第一块 {rects[0].X},{rects[0].Y} {rects[0].Width}×{rects[0].Height}");

        void Add(FrameworkElement element)
        {
            if (element.ActualWidth <= 0.5 || element.ActualHeight <= 0.5)
            {
                return;
            }

            var bounds = element.TransformToVisual(null).TransformBounds(
                new Rect(0, 0, element.ActualWidth, element.ActualHeight));

            rects.Add(new RectInt32(
                (int)Math.Round(bounds.X * scale),
                (int)Math.Round(bounds.Y * scale),
                (int)Math.Round(bounds.Width * scale),
                (int)Math.Round(bounds.Height * scale)));
        }
    }

    private static bool IsInsideButton(DependencyObject? source)
    {
        while (source is not null)
        {
            if (source is Button)
            {
                return true;
            }

            source = VisualTreeHelper.GetParent(source);
        }

        return false;
    }

    private void SetHeader(TabViewItem item, string name)
    {
        var header = new TextBlock
        {
            Text = name,
            FontFamily = NoteStyling.NoteFont,
            FontSize = TabStrip.FontPx,
            TextTrimming = TextTrimming.CharacterEllipsis,
            TextWrapping = TextWrapping.NoWrap,
            VerticalAlignment = VerticalAlignment.Center,
        };

        item.Header = header;
        PaintHeader(item, hovered: false);
    }

    /// <summary>标题没变就只重刷墨色，不把 TextBlock 换掉——整条重建时换头会把鼠标正停着
    /// 那一枚的悬停墨打回静音，下一次 PointerEntered 又不一定会来。</summary>
    private void UpdateHeader(TabViewItem item, string name)
    {
        if (item.Header is TextBlock header && string.Equals(header.Text, name, StringComparison.Ordinal))
        {
            PaintHeader(item, hovered: false);

            return;
        }

        SetHeader(item, name);
    }

    /// <summary>活动那一枚用正文墨、其余退回静音墨；悬停的静音标签临时提成正文墨。</summary>
    private void PaintHeader(TabViewItem item, bool hovered)
    {
        if (item.Header is not TextBlock header || item.Tag is not string path)
        {
            return;
        }

        var active = SameBoard(path, _boardPath ?? string.Empty);
        var dark = NoteStyling.IsDark;

        header.Foreground = new SolidColorBrush(
            NoteStyling.ToColor(active || hovered ? TabStrip.ActiveInk(dark) : TabStrip.InactiveInk(dark)));
        header.FontWeight = active ? Microsoft.UI.Text.FontWeights.Medium : Microsoft.UI.Text.FontWeights.Normal;
    }

    private void RepaintAllHeaders()
    {
        foreach (var item in BoardTabs.TabItems.OfType<TabViewItem>())
        {
            PaintHeader(item, hovered: false);
        }
    }

    private void OnBoardTabSelected(object sender, SelectionChangedEventArgs e)
    {
        if (_tabsBusy || BoardTabs.SelectedIndex < 0 || BoardTabs.SelectedIndex >= _openBoards.Count)
        {
            return;
        }

        SwitchToBoard(_openBoards[BoardTabs.SelectedIndex]);
    }

    private void OnBoardTabClosed(TabView sender, TabViewTabCloseRequestedEventArgs args)
    {
        var path = (args.Item as TabViewItem)?.Tag as string ?? args.Item as string;

        if (path is null)
        {
            return;
        }

        CloseTab(path);
    }

    // ---------------------------------------------------------------------
    // 右键菜单
    // ---------------------------------------------------------------------

    private MenuFlyout BuildTabFlyout(TabViewItem item)
    {
        var menu = new MenuFlyout();

        menu.Opening += (_, _) => FillTabMenu(menu, item);

        return menu;
    }

    private void FillTabMenu(MenuFlyout menu, TabViewItem item)
    {
        menu.Items.Clear();

        if (item.Tag is not string path)
        {
            return;
        }

        var current = SameBoard(path, _boardPath ?? string.Empty);

        var rename = new MenuFlyoutItem { Text = "重命名" };
        rename.Click += (_, _) => BeginRenameTab(path);
        menu.Items.Add(rename);

        var close = new MenuFlyoutItem { Text = "关闭" };

        if (current)
        {
            close.KeyboardAcceleratorTextOverride = "Ctrl+W";
        }

        close.Click += (_, _) => CloseTab(path);
        menu.Items.Add(close);

        if (_openBoards.Count > 1)
        {
            var others = new MenuFlyoutItem { Text = "关闭其他" };
            others.Click += (_, _) => CloseOtherBoards(path);
            menu.Items.Add(others);
        }
    }

    private MenuFlyout BuildStripFlyout()
    {
        var menu = new MenuFlyout();

        menu.Opening += (_, _) => FillStripMenu(menu);

        return menu;
    }

    private void FillStripMenu(MenuFlyout menu)
    {
        menu.Items.Clear();

        var create = new MenuFlyoutItem { Text = "新板块", KeyboardAcceleratorTextOverride = "Ctrl+T" };
        create.Click += (_, _) => CreateNewBoard();
        menu.Items.Add(create);
    }

    // ---------------------------------------------------------------------
    // 切板
    // ---------------------------------------------------------------------

    /// <summary>
    /// 把这扇窗口挪去处理另一块板。开不成时标签条会退回真的那块板——屏上不许留一枚
    /// 指向不存在之物的标签。
    /// </summary>
    private void SwitchToBoard(string boardPath)
    {
        if (_boardPath is null || SameBoard(boardPath, _boardPath))
        {
            return;
        }

        CancelRename();

        FlushCurrentBoard();

        LoadBoard(boardPath);

        if (!SameBoard(boardPath, _boardPath ?? string.Empty))
        {
            RebuildTabs();
        }
        else
        {
            RepaintAllHeaders();
        }
    }

    /// <summary>切走之前把这一块板交回去：定时器里那半秒的改动不能跟着人走。</summary>
    private void FlushCurrentBoard()
    {
        _saveTimer?.Stop();

        if (_boardPath is null || _document is null)
        {
            return;
        }

        try
        {
            _view.Prune(_document.Nodes.Select(node => node.Id));
            _view.Save(CanvasStore.SidecarPath(_boardPath));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Say($"切走之前那一份相机没存上：{ex.Message}");
        }

        SaveNow();
    }

    /// <summary>
    /// 只属于这一块板的运行时状态。留着它们，下一块板就会拿到上一块的撤销栈、选中那几格，
    /// 或者一个还对着旧 id 打字的编辑器。
    /// </summary>
    private void ResetPerBoardRuntime()
    {
        if (_editor is not null)
        {
            CloseEditor(commit: true);
        }

        _history.Clear();
        _pendingSnapshot = null;
        _pendingLabel = null;
        _lastWrittenHash = null;
        _reloadPending = false;

        ClearSelection();

        CloseFilterPanel();
        CloseSearchPanel();
        CloseRestorePanel();
    }

    private void CloseCurrentBoard()
    {
        if (_boardPath is null)
        {
            return;
        }

        if (_openBoards.Count <= 1)
        {
            Say("最后一块板不给关：先按 Ctrl+T 起一块，再关它——关掉一块＝从本机删掉这块板");

            return;
        }

        CancelRename();

        var closing = CurrentBoardIndex();
        var closingPath = _openBoards[closing];
        var closedName = BoardName(closingPath);
        var syncWasOn = _sync?.Enabled == true;
        var neighbour = _openBoards[closing > 0 ? closing - 1 : 1];

        SwitchToBoard(neighbour);

        if (!SameBoard(_boardPath ?? string.Empty, neighbour))
        {
            return;
        }

        var error = DeleteBoard(closingPath);

        if (error is not null)
        {
            Say($"没删掉 {closedName}：{error}；板子还开着");

            return;
        }

        _openBoards.RemoveAt(closing);
        RebuildTabs();
        Say(
            $"删掉了 {closedName}（本机文件与历史已删{(syncWasOn ? "；远端仓库那份不在这一下里" : "")}），" +
            $"回到 {BoardName(neighbour)}");
    }

    private void CloseTab(string path)
    {
        if (_boardPath is null)
        {
            return;
        }

        if (_openBoards.Count <= 1)
        {
            Say("最后一块板不给关：先按 Ctrl+T 起一块，再关它——关掉一块＝从本机删掉这块板");

            return;
        }

        if (SameBoard(path, _boardPath))
        {
            CloseCurrentBoard();

            return;
        }

        CancelRename();

        var closedName = BoardName(path);
        var syncWasOn = _sync?.Enabled == true;
        var error = DeleteBoard(path);

        if (error is not null)
        {
            Say($"没删掉 {closedName}：{error}");

            return;
        }

        _openBoards.RemoveAll(open => SameBoard(open, path));
        RebuildTabs();
        Say($"删掉了 {closedName}（本机文件与历史已删{(syncWasOn ? "；远端仓库那份不在这一下里" : "")}）");
    }

    private void CloseOtherBoards(string path)
    {
        if (_openBoards.Count <= 1)
        {
            Say("只开着这一块板，没有别的可关");

            return;
        }

        if (!SameBoard(path, _boardPath ?? string.Empty))
        {
            SwitchToBoard(path);
        }

        if (!SameBoard(path, _boardPath ?? string.Empty))
        {
            return;
        }

        CancelRename();

        var closedPaths = _openBoards.Where(open => !SameBoard(open, path)).ToList();
        var syncWasOn = _sync?.Enabled == true;
        var deleted = new List<string>();
        var failures = new List<string>();

        foreach (var closing in closedPaths)
        {
            var name = BoardName(closing);
            var error = DeleteBoard(closing);

            if (error is null)
            {
                deleted.Add(closing);
            }
            else
            {
                failures.Add($"{name}：{error}");
            }
        }

        _openBoards.RemoveAll(open => deleted.Exists(gone => SameBoard(gone, open)));
        RebuildTabs();

        var suffix = syncWasOn ? "；远端仓库那几份不在这一下里" : string.Empty;

        Say(failures.Count == 0
            ? $"删掉了 {deleted.Count} 块板（本机文件与历史已删{suffix}），留着 {BoardName(path)}"
            : $"删掉了 {deleted.Count} 块板，{failures.Count} 块没删掉：{string.Join("；", failures)}；留着 {BoardName(path)}");
    }

    /// <summary>
    /// 关掉一块板＝从本机删掉它：板子文件、相机 sidecar、这块板自己的历史快照一起走。
    /// 同步账本保留（它是同名板下一次同步时把删除推过去的祖先）；远端仓库那份没有墓碑，不在这一下里删。
    /// </summary>
    private string? DeleteBoard(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }

            var sidecar = CanvasStore.SidecarPath(path);

            if (File.Exists(sidecar))
            {
                File.Delete(sidecar);
            }

            var history = Snapshots.DirectoryFor(path);
            var boardName = Path.GetFileNameWithoutExtension(path);

            if (Directory.Exists(history))
            {
                foreach (var snapshot in Directory.EnumerateFiles(history, "*.canvas"))
                {
                    var stem = Path.GetFileNameWithoutExtension(snapshot);
                    var at = stem.LastIndexOf('@');

                    if (at > 0 && string.Equals(stem[..at], boardName, StringComparison.OrdinalIgnoreCase))
                    {
                        File.Delete(snapshot);
                    }
                }
            }

            Diagnostics.Write($"board deleted on close: {path}");

            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Diagnostics.Write($"board delete failed: {path}: {ex}");

            return ex.Message;
        }
    }

    private void StepBoard(int delta)
    {
        if (_openBoards.Count < 2)
        {
            Say("只开着这一块板：Ctrl+T 再起一块");

            return;
        }

        var next = (CurrentBoardIndex() + delta + _openBoards.Count) % _openBoards.Count;

        SwitchToBoard(_openBoards[next]);
    }

    // ---------------------------------------------------------------------
    // 新板块 / 打开已有
    // ---------------------------------------------------------------------

    /// <summary>
    /// 点一下 + 就建一块，不再问名字：默认「未命名板」，重名自动加序号；名字随时能右键改。
    /// 这是 2026-10-22 他点名的那条路径（"可以点击一下直接新建，然后右键重命名"）。
    /// </summary>
    private void CreateNewBoard()
    {
        if (_boardPath is null)
        {
            return;
        }

        CancelRename();

        var clock = System.Diagnostics.Stopwatch.StartNew();
        var result = BoardLocator.NewUntitledBoard(
            BoardLocator.BoardDirectory(_boardPath),
            _openBoards);

        if (!result.Ok)
        {
            Say(result.Problem!);

            return;
        }

        var path = result.BoardPath!;
        var resolveMs = clock.Elapsed.TotalMilliseconds;

        try
        {
            CanvasStore.Write(path, CanvasDocument.CreateNew().ToJson());
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Say($"这块板没建出来：{ex.Message}");

            return;
        }

        var writeMs = clock.Elapsed.TotalMilliseconds;

        // 先把带名字的标签挂上去，再去读盘切过去：读盘那几十毫秒里标签已经有名字，
        // 不会先出现一个空白标签再补字（他 10-22 点加号看到的那一帧）。
        if (!_openBoards.Exists(open => SameBoard(open, path)))
        {
            _openBoards.Add(path);
            RebuildTabs();
        }

        var tabMs = clock.Elapsed.TotalMilliseconds;

        SwitchToBoard(path);

        Diagnostics.Write(
            $"新建 {Path.GetFileName(path)}: 起名 {resolveMs:0.#}ms + 落盘 {writeMs - resolveMs:0.#}ms" +
            $" + 标签 {tabMs - writeMs:0.#}ms + 切板 {clock.Elapsed.TotalMilliseconds - tabMs:0.#}ms" +
            $"（起点到切完 {clock.Elapsed.TotalMilliseconds:0.#}ms）");
    }

    // ---------------------------------------------------------------------
    // 行内改名
    // ---------------------------------------------------------------------

    /// <summary>
    /// 右键"重命名"：把那一枚标签的头换成一行 TextBox。不另开浮窗——这块屏上已经有一枚
    /// 正在说"那块板叫什么"的标签，名字就该改在原地。Enter 提交、Esc 取消；点到别处按
    /// LostFocus 提交，失败则退回原名并说明为什么。
    /// </summary>
    private void BeginRenameTab(string path)
    {
        if (_boardPath is null)
        {
            return;
        }

        CancelRename();

        var item = TabItemFor(path);

        if (item is null)
        {
            return;
        }

        var box = new TextBox
        {
            Text = BoardName(path),
            FontFamily = NoteStyling.NoteFont,
            FontSize = TabStrip.FontPx,
            FontWeight = Microsoft.UI.Text.FontWeights.Medium,
            MinWidth = TabStrip.RenameBoxMinWidth,
            MaxWidth = TabStrip.RenameBoxMaxWidth,
            Height = 26,
            Padding = new Thickness(0),
            BorderThickness = new Thickness(0),
            Background = new SolidColorBrush(Colors.Transparent),
            Foreground = new SolidColorBrush(NoteStyling.ToColor(TabStrip.ActiveInk(NoteStyling.IsDark))),
            VerticalContentAlignment = VerticalAlignment.Center,
            IsSpellCheckEnabled = false,
        };

        // TextBox 默认模板在聚焦时画一条强调色下划线和一圈边框；标签上只该有字和光标。
        // 这几枚主题资源是这一条路的全部开关（没有为它重写一整份 TextBox 模板）。
        box.Resources["TextControlBackground"] = new SolidColorBrush(Colors.Transparent);
        box.Resources["TextControlBackgroundFocused"] = new SolidColorBrush(Colors.Transparent);
        box.Resources["TextControlBackgroundPointerOver"] = new SolidColorBrush(Colors.Transparent);
        box.Resources["TextControlBorderBrush"] = new SolidColorBrush(Colors.Transparent);
        box.Resources["TextControlBorderBrushFocused"] = new SolidColorBrush(Colors.Transparent);
        box.Resources["TextControlBorderBrushPointerOver"] = new SolidColorBrush(Colors.Transparent);
        box.Resources["TextControlBorderThemeThickness"] = new Thickness(0);
        box.Resources["TextControlBorderThemeThicknessFocused"] = new Thickness(0);
        box.Resources["TextControlForeground"] = new SolidColorBrush(NoteStyling.ToColor(TabStrip.ActiveInk(NoteStyling.IsDark)));

        _renamingPath = path;
        _renameBox = box;

        item.MaxWidth = TabStrip.RenameTabMaxWidth;
        item.IsClosable = false;
        item.Header = box;

        box.KeyDown += OnRenameKeyDown;
        box.LostFocus += OnRenameLostFocus;

        DispatcherQueue.TryEnqueue(() =>
        {
            if (ReferenceEquals(_renameBox, box))
            {
                box.Focus(FocusState.Programmatic);
                box.SelectAll();
            }
        });
    }

    private void OnRenameKeyDown(object sender, KeyRoutedEventArgs e)
    {
        switch (e.Key)
        {
            case Windows.System.VirtualKey.Enter:
                e.Handled = true;
                TryCommitRename(fromBlur: false);
                break;

            case Windows.System.VirtualKey.Escape:
                e.Handled = true;
                CancelRename();
                break;
        }
    }

    private void OnRenameLostFocus(object sender, RoutedEventArgs e)
    {
        if (_renameBusy || _renamePreview || _renamingPath is null)
        {
            return;
        }

        // 焦点交给菜单/别的标签时也提交；失败就在回执里说为什么，并把原名放回去。
        TryCommitRename(fromBlur: true);
    }

    private void TryCommitRename(bool fromBlur)
    {
        if (_renameBusy || _renamePreview || _renamingPath is null || _renameBox is null)
        {
            return;
        }

        var oldPath = _renamingPath;
        var box = _renameBox;
        var oldName = BoardName(oldPath);
        var result = BoardLocator.Rename(oldPath, box.Text);

        if (!result.Ok)
        {
            Say(result.Problem!);

            if (fromBlur)
            {
                CancelRename();

                return;
            }

            box.Focus(FocusState.Programmatic);
            box.SelectAll();

            return;
        }

        var newPath = result.BoardPath!;

        if (string.Equals(oldPath, newPath, StringComparison.Ordinal))
        {
            CancelRename();

            return;
        }

        _renameBusy = true;

        var current = SameBoard(oldPath, _boardPath ?? string.Empty);
        var syncWasOn = _sync?.Enabled == true;
        var item = TabItemFor(oldPath);

        try
        {
            if (current)
            {
                FlushCurrentBoard();
                StopBoardServicesForRename();
            }

            MoveBoardFiles(oldPath, newPath);

            // 一旦改了名，这块板就是用户的命名板了；关标签时不再清内容。
            var index = _openBoards.FindIndex(open => SameBoard(open, oldPath));

            if (index >= 0)
            {
                _openBoards[index] = newPath;
            }

            if (current)
            {
                _boardPath = newPath;
            }

            if (item is not null)
            {
                item.Tag = newPath;
                item.MaxWidth = TabStrip.TabMaxWidth;
                item.IsClosable = true;
            }

            _renamingPath = null;
            _renameBox = null;

            RebuildTabs();

            if (current)
            {
                StartBoardServicesAfterRename();
            }

            Say(syncWasOn
                ? $"改名为 {BoardName(newPath)}｜同步会把新名字当另一块板，旧名那份留在远端"
                : $"改名为 {BoardName(newPath)}");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Say($"改名没成：{ex.Message}");

            _renamingPath = null;
            _renameBox = null;

            if (item is not null)
            {
                item.Tag = oldPath;
                item.MaxWidth = TabStrip.TabMaxWidth;
                item.IsClosable = true;
                SetHeader(item, oldName);
            }

            if (current)
            {
                StartBoardServicesAfterRename();
            }

            if (!File.Exists(_boardPath ?? string.Empty))
            {
                Diagnostics.Write($"rename left the current board path missing: {_boardPath}");
            }
        }
        finally
        {
            _renameBusy = false;
        }
    }

    private void CancelRename()
    {
        if (_renamingPath is null)
        {
            return;
        }

        var item = TabItemFor(_renamingPath);
        var name = _renamingPath;

        _renamingPath = null;
        _renameBox = null;

        if (item is not null)
        {
            item.MaxWidth = TabStrip.TabMaxWidth;
            item.IsClosable = true;
            SetHeader(item, BoardName(name));
        }
    }

    /// <summary>
    /// 文件与它的相机 sidecar 一起走。只改大小写的改名在 Windows 上不能一步 File.Move
    /// （目标按不区分大小写算已经存在），要走一个临时名。
    /// </summary>
    private void MoveBoardFiles(string oldPath, string newPath)
    {
        if (string.Equals(oldPath, newPath, StringComparison.Ordinal))
        {
            return;
        }

        if (string.Equals(oldPath, newPath, StringComparison.OrdinalIgnoreCase))
        {
            var temporary = newPath + ".renaming-" + Guid.NewGuid().ToString("N")[..6];

            File.Move(oldPath, temporary);
            File.Move(temporary, newPath);
        }
        else
        {
            File.Move(oldPath, newPath);
        }

        var oldSidecar = CanvasStore.SidecarPath(oldPath);
        var newSidecar = CanvasStore.SidecarPath(newPath);

        if (!File.Exists(oldSidecar))
        {
            return;
        }

        try
        {
            if (string.Equals(oldSidecar, newSidecar, StringComparison.OrdinalIgnoreCase)
                && !string.Equals(oldSidecar, newSidecar, StringComparison.Ordinal))
            {
                var temporary = newSidecar + ".renaming-" + Guid.NewGuid().ToString("N")[..6];

                File.Move(oldSidecar, temporary);
                File.Move(temporary, newSidecar);
            }
            else
            {
                File.Move(oldSidecar, newSidecar);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 相机不是板子本体：它搬不过去只丢这一块板的视野，不能让一个已经成功的改名回滚。
            Diagnostics.Write($"rename moved the board but not its camera sidecar: {ex.Message}");
        }
    }

    /// <summary>改名要换 Watcher／shot 管道／同步目录名：先把旧的收干净，再按新路径起。</summary>
    private void StopBoardServicesForRename()
    {
        _watcher?.Dispose();
        _watcher = null;
        _shotServer?.Dispose();
        _shotServer = null;
        _sync?.Stop();
        _sync = null;
    }

    private void StartBoardServicesAfterRename()
    {
        if (_boardPath is null)
        {
            return;
        }

        StartWatching();
        StartShotServer();

        _sync = new SyncHost(_boardPath, Say, DispatcherQueue);
        _sync.Start();
    }

    // ---------------------------------------------------------------------
    // 自检
    // ---------------------------------------------------------------------

    /// <summary>
    /// 自检那一发。它盯的是这一层最贵的一种坏：切走时那一步没落盘，或者上一块板的撤销栈跟着人
    /// 走到了下一块——两种都是丢改动，而不是难看。
    /// </summary>
    /// <remarks>
    /// 新板子走的是界面自己那条路（<see cref="BoardLocator.NewBoard"/> 起名 → 写盘 → 切过去），
    /// 不是直接塞一个路径进 <c>SwitchToBoard</c>：绕开起名那一发，这一组断言就测不到用户真会走的那条路。
    /// 收尾把那块自检板删掉：闸门下次跑要能重新起出同名的一块，而不是被"已经在了"拒掉。
    /// </remarks>
    private void SelfTestBoardSwitching(List<string> problems)
    {
        if (_boardPath is null)
        {
            problems.Add("自检换板子：窗口没有当前板子，这一发不适用");

            return;
        }

        var firstPath = _boardPath;
        var firstName = Path.GetFileNameWithoutExtension(firstPath);
        var nodesOnFirst = _document.Nodes.Count;
        var secondPath = Path.Combine(BoardLocator.BoardDirectory(firstPath), "selftest-second" + CanvasStore.Extension);

        // 上一发没收尾留下的那块先清掉：不清的话 NewBoard 会按"已经在了"拒掉，
        // 那一发红的就不是被测的东西，是装置自己。
        foreach (var stale in new[] { secondPath, CanvasStore.SidecarPath(secondPath) })
        {
            if (File.Exists(stale))
            {
                File.Delete(stale);
            }
        }

        var created = BoardLocator.NewBoard(BoardLocator.BoardDirectory(firstPath), "selftest-second");

        if (!created.Ok)
        {
            problems.Add($"自检那块新板子没起出来：{created.Problem}");

            return;
        }

        secondPath = created.BoardPath!;

        CanvasStore.Write(secondPath, CanvasDocument.CreateNew().ToJson());

        var probe = _document.Nodes.FirstOrDefault(node => node.Kind == CanvasNodeKind.Text);
        var probeStartX = probe?.X ?? 0;

        if (probe is not null)
        {
            probe.X = probeStartX + 7;

            // 故意只排进定时器，不手动存：切板那一下必须替它把盘落掉。
            SaveSoon();
            _saveTimer?.Stop();
        }

        SwitchToBoard(secondPath);

        if (!SameBoard(_boardPath ?? string.Empty, secondPath))
        {
            problems.Add($"切板没切过去：_boardPath 读到 {_boardPath}");
        }

        if (BoardTabs.TabItems.Count != 2 || _openBoards.Count != 2)
        {
            problems.Add($"两块板开着时标签条读到 {BoardTabs.TabItems.Count} 枚标签、列表里 {_openBoards.Count} 块");
        }

        if (BoardTabs.SelectedIndex != 1)
        {
            problems.Add($"切过去以后选中的不是第二枚标签，读到第 {BoardTabs.SelectedIndex} 枚");
        }

        if (!Title.Contains("selftest-second", StringComparison.Ordinal))
        {
            problems.Add($"任务栏那行没跟着换：Title『{Title}』");
        }

        if (_history.UndoCount != 0)
        {
            problems.Add($"切过来还带着 {_history.UndoCount} 步上一块板的历史（下一发撤销会把另一块板写进来）");
        }

        if (probe is not null)
        {
            var onDisk = CanvasDocument.Parse(CanvasStore.ReadAllText(firstPath), firstPath);
            var landed = onDisk.Nodes.FirstOrDefault(node => node.Id == probe.Id);

            if (landed is null || Math.Abs(landed.X - (probeStartX + 7)) > 0.001)
            {
                problems.Add(
                    $"切走之前那一步没落盘：{firstName} 上 {probe.Id} 读到 {landed?.X.ToString() ?? "没了这一格"}，应在 {probeStartX + 7}");
            }
        }

        // 两枚标签都在场时量那一排的几何：活动填充、关闭键和加号的位置都是他 10-22 点名的。
        SelfTestTabStrip(problems);

        SwitchToBoard(firstPath);

        if (BoardTabs.TabItems.Count != 2 || !SameBoard(_boardPath ?? string.Empty, firstPath))
        {
            problems.Add($"切回第一块没切回来：标签 {BoardTabs.TabItems.Count} 枚，_boardPath {_boardPath}");
        }

        // 关掉非当前那块：文件与 sidecar 必须一起走，当前这块一个字不动。
        CloseTab(secondPath);

        if (BoardTabs.TabItems.Count != 1 || _openBoards.Count != 1
            || !SameBoard(_boardPath ?? string.Empty, firstPath))
        {
            problems.Add($"关掉第二块以后应只剩第一块：标签 {BoardTabs.TabItems.Count} 枚、列表 {_openBoards.Count} 块，_boardPath {_boardPath}");
        }

        if (File.Exists(secondPath) || File.Exists(CanvasStore.SidecarPath(secondPath)))
        {
            problems.Add("关掉第二块没把文件/sidecar 删掉");
        }

        if (!File.Exists(firstPath) || _document.Nodes.Count != nodesOnFirst)
        {
            problems.Add($"关第二块把第一块动了：第一块 {File.Exists(firstPath)}、当前 { _document.Nodes.Count } 格，应 {nodesOnFirst}");
        }
        else
        {
            var stillOnFirst = CanvasDocument.Parse(CanvasStore.ReadAllText(firstPath), firstPath);

            if (stillOnFirst.Nodes.Count != nodesOnFirst)
            {
                problems.Add($"关掉第二块以后第一块板上剩 {stillOnFirst.Nodes.Count} 格，应还是 {nodesOnFirst} 格");
            }
        }

        if (!_sayRow.Text.Contains("删掉了", StringComparison.Ordinal))
        {
            problems.Add($"关板子没说删：回执『{_sayRow.Text}』");
        }

        CloseCurrentBoard();

        if (BoardTabs.TabItems.Count != 1 || _openBoards.Count != 1 || !File.Exists(firstPath))
        {
            problems.Add($"最后一块板被关掉了/文件被删了：标签 {BoardTabs.TabItems.Count} 枚、文件 {File.Exists(firstPath)}");
        }

        if (!_sayRow.Text.Contains("不给关", StringComparison.Ordinal))
        {
            problems.Add($"拒掉那一发没说为什么：回执『{_sayRow.Text}』");
        }

        // 收尾回到出发那块板：闸门最后那行读数要报的是真板子，不是一块刚被删掉的文件。
        if (!SameBoard(_boardPath ?? string.Empty, firstPath))
        {
            SwitchToBoard(firstPath);
        }

        _openBoards.RemoveAll(open => SameBoard(open, secondPath));
        RebuildTabs();

        Diagnostics.Write(
            $"selftest 换板子: 两块板切来切去都落盘（{firstName} 上那一步 X+7 已在文件里）、历史不跨板、关第二块删文件不碰第一块、" +
            $"最后一块拒关且明说；标签条 {BoardTabs.TabItems.Count} 枚、选中第 {BoardTabs.SelectedIndex} 枚、" +
            $"条 {BoardTabs.ActualWidth:0.##}×{BoardTabs.ActualHeight:0.##}、右内缩 {TitleBarHost.Padding.Right:0.##}");

        foreach (var leftover in new[] { secondPath, CanvasStore.SidecarPath(secondPath) })
        {
            if (File.Exists(leftover))
            {
                File.Delete(leftover);
            }
        }
    }

    /// <summary>
    /// 两枚标签都在场时量这一排：活动填充＝板底、关闭键只在活动那一枚出现、加号紧跟最后一枚。
    /// 这三条正是他 10-22 点名的"难区分/加号奇怪"；只对常量还不够，必须量它们彼此的位置。
    /// </summary>
    private void SelfTestTabStrip(List<string> problems)
    {
        if (BoardTabs.TabItems.Count < 2)
        {
            problems.Add("自检标签条：没有两枚标签，量不了活动/非活动的差别");

            return;
        }

        BoardTabs.UpdateLayout();
        PositionAddBoardButton();

        var passthrough = _tabPassthrough?.GetRegionRects(NonClientRegionKind.Passthrough) ?? [];
        var caption = _tabPassthrough?.GetRegionRects(NonClientRegionKind.Caption) ?? [];
        var expectedRegions = BoardTabs.TabItems.Count + 4;

        if (passthrough.Length < expectedRegions)
        {
            problems.Add(
                $"标题栏交互区只标了 {passthrough.Length} 块，应至少 {expectedRegions} 块（{BoardTabs.TabItems.Count} 枚标签 + 加号 + 3 枚标题按钮）：输入会被非客户区吞掉");
        }

        if (caption.Length == 0)
        {
            problems.Add("标题条没有 Caption 区：空白处拖不动窗口、双击也不会最大化");
        }

        if (AppWindow.Presenter is not OverlappedPresenter presenter
            || presenter.HasBorder
            || presenter.HasTitleBar
            || !presenter.IsResizable)
        {
            problems.Add(
                $"窗口外壳读到 {AppWindow.Presenter.GetType().Name} (border={Overlapped?.HasBorder}, title={Overlapped?.HasTitleBar}, resizable={Overlapped?.IsResizable})，应是无框、可缩放");
        }

        var outerSize = AppWindow.Size;
        var clientSize = AppWindow.ClientSize;

        if (clientSize.Width != outerSize.Width || clientSize.Height != outerSize.Height)
        {
            problems.Add(
                $"客户区 {clientSize.Width}×{clientSize.Height} ≠ 外框 {outerSize.Width}×{outerSize.Height}：启动时那圈非客户带会把桌面透成白条");
        }

        foreach (var (button, name) in new[]
                 {
                     (MinimizeButton, "最小化"), (MaximizeButton, "最大化"), (CloseButton, "关闭"),
                 })
        {
            if (!button.IsLoaded || button.ActualWidth <= 0 || button.ActualHeight <= 0)
            {
                problems.Add($"标题按钮『{name}』没长出来：loaded={button.IsLoaded} {button.ActualWidth:0.#}×{button.ActualHeight:0.#}");
            }
        }

        var selectedIndex = BoardTabs.SelectedIndex;
        var selected = BoardTabs.TabItems[selectedIndex] as TabViewItem;
        var inactive = BoardTabs.TabItems
            .OfType<TabViewItem>()
            .FirstOrDefault(item => !ReferenceEquals(item, selected));
        var add = AddBoardButton;

        if (selected is null || inactive is null)
        {
            problems.Add("自检标签条：拿不到活动/非活动的那两枚 TabViewItem");

            return;
        }

        var last = BoardTabs.TabItems.OfType<TabViewItem>().Last();
        var lastAnchor = FindDescendant<Grid>(last, "TabContainer") ?? (FrameworkElement)last;
        var lastBox = lastAnchor.TransformToVisual(BoardTabs).TransformBounds(
            new Rect(0, 0, lastAnchor.ActualWidth, lastAnchor.ActualHeight));
        var addBox = add.TransformToVisual(BoardTabs).TransformBounds(
            new Rect(0, 0, add.ActualWidth, add.ActualHeight));
        var gap = addBox.Left - lastBox.Right;

        if (gap < -0.5 || gap > 16)
        {
            problems.Add(
                $"加号没紧跟最后一枚标签：最后一枚右缘 {lastBox.Right:0.##}，加号左缘 {addBox.Left:0.##}，差 {gap:0.##}");
        }

        var expectedFill = NoteStyling.ToColor(TabStrip.ActiveFill(NoteStyling.IsDark));
        var selectedPath = FindDescendant<Microsoft.UI.Xaml.Shapes.Path>(selected, "SelectedBackgroundPath");
        var selectedFill = (selectedPath?.Fill as SolidColorBrush)?.Color;

        if (selectedFill is null || selectedFill.Value != expectedFill)
        {
            problems.Add(
                $"活动标签没有填成板底：读到 {(selectedFill is null ? "没有 SelectedBackgroundPath" : $"{selectedFill.Value.R}、{selectedFill.Value.G}、{selectedFill.Value.B}")}，" +
                $"应在 {expectedFill.R}、{expectedFill.G}、{expectedFill.B}");
        }

        if (selected.CornerRadius.TopLeft != TabStrip.TopRadius || selected.CornerRadius.BottomLeft != 0)
        {
            problems.Add($"活动标签圆角读到 {selected.CornerRadius}，应只圆上沿 {TabStrip.TopRadius}");
        }

        if (BoardTabs.CloseButtonOverlayMode != TabViewCloseButtonOverlayMode.OnPointerOver)
        {
            problems.Add($"关闭键的显示方式读到 {BoardTabs.CloseButtonOverlayMode}，应在悬停/活动时才出现");
        }

        var selectedClose = FindDescendant<Button>(selected, "CloseButton");
        var inactiveClose = FindDescendant<Button>(inactive, "CloseButton");
        var selectedContainer = FindDescendant<Grid>(selected, "TabContainer");
        var transition = selectedContainer?.BackgroundTransition;

        if (selectedClose is null || selectedClose.Visibility != Visibility.Visible)
        {
            problems.Add("活动标签上的关闭键没出现");
        }

        if (transition is null)
        {
            problems.Add("活动标签的悬停过渡没挂上：TabContainer.BackgroundTransition 是 null");
        }

        var tabList = FindDescendant<ListView>(BoardTabs, "TabListView");

        if (tabList is null || tabList.ItemContainerTransitions is not { Count: 0 })
        {
            problems.Add(
                $"标签条的入场过渡还在 {tabList?.ItemContainerTransitions?.Count.ToString() ?? "（找不到 TabListView）"} 枚：新标签会先空一拍再带名字出现");
        }

        SelfTestTabMenus(problems);

        // 非活动那一枚的 × 归平台按 PointerOver 管；自检这一拍没法知道真鼠标在哪一格上，
        // 所以这里只记读数、不断言它必须 Collapsed。真手验证走 chrome 截图与悬停。
        Diagnostics.Write(
            $"selftest 标签条: 两枚标签、活动填充 {selectedFill?.R}、{selectedFill?.G}、{selectedFill?.B}（板底 {expectedFill.R}、{expectedFill.G}、{expectedFill.B}）、" +
            $"活动关闭键 {selectedClose?.Visibility}、非活动关闭键 {inactiveClose?.Visibility}（真鼠标若正停在它上面就是 Visible）、" +
            $"悬停过渡 {(transition is null ? "没挂" : $"{transition.Duration.TotalMilliseconds:0}ms")}、" +
            $"交互区 {passthrough.Length} 块（应 {expectedRegions}）、Caption {caption.Length} 块、" +
            $"最后一枚右缘 {lastBox.Right:0.##}、加号左缘 {addBox.Left:0.##}、差 {gap:0.##}、" +
            $"条 {BoardTabs.ActualWidth:0.##}×{BoardTabs.ActualHeight:0.##}、宿主 {TitleBarHost.ActualWidth:0.##}×{TitleBarHost.ActualHeight:0.##}");
    }

    /// <summary>
    /// 右键菜单的条目在打开时现算；这一发不弹菜单（那是 popup 宿主，自检拍不到），
    /// 只把同一份填充函数跑一遍，钉住"重命名/关闭/关闭其他/打开盘上的板子"真的在。
    /// </summary>
    private void SelfTestTabMenus(List<string> problems)
    {
        var item = BoardTabs.TabItems.OfType<TabViewItem>().LastOrDefault();

        if (item is null)
        {
            problems.Add("自检标签菜单：没有标签可挂菜单");

            return;
        }

        var tabMenu = new MenuFlyout();
        FillTabMenu(tabMenu, item);

        var tabLabels = tabMenu.Items.OfType<MenuFlyoutItem>().Select(row => row.Text).ToList();
        var hasOpenSubmenu = tabMenu.Items.OfType<MenuFlyoutSubItem>()
            .Any(sub => sub.Text == "打开盘上的板子");

        if (!tabLabels.Contains("重命名") || !tabLabels.Contains("关闭"))
        {
            problems.Add($"标签右键菜单缺行：读到 {string.Join(" / ", tabLabels)}");
        }

        if (_openBoards.Count > 1 && !tabLabels.Contains("关闭其他"))
        {
            problems.Add("两块板开着时标签右键菜单没有『关闭其他』");
        }

        if (hasOpenSubmenu)
        {
            problems.Add("标签右键菜单还在提供『打开盘上的板子』：他 10-22 点名撤掉");
        }

        var stripMenu = new MenuFlyout();
        FillStripMenu(stripMenu);

        var stripLabels = stripMenu.Items.OfType<MenuFlyoutItem>().Select(row => row.Text).ToList();
        var stripHasOpen = stripMenu.Items.OfType<MenuFlyoutSubItem>()
            .Any(sub => sub.Text == "打开盘上的板子");

        if (!stripLabels.Contains("新板块") || stripHasOpen)
        {
            problems.Add($"加号右键菜单应只有『新板块』：读到 {string.Join(" / ", stripLabels)}");
        }

        Diagnostics.Write(
            $"selftest 标签菜单: tab [{string.Join(" / ", tabLabels)}]、strip [{string.Join(" / ", stripLabels)}]" +
            $"、打开盘上的板子 {(hasOpenSubmenu || stripHasOpen ? "仍在" : "已撤")}");
    }

    /// <summary>
    /// 直建与改名走界面自己那两条路：<see cref="CreateNewBoard"/> 落一块文件、<see cref="BeginRenameTab"/>
    /// 换出行内输入框、<see cref="TryCommitRename"/> 把文件与路径换过去，历史与文档不重载。
    /// 跑在 %TEMP% 的自检副本上；收尾把造出来的板子删掉，下一发能重新起同名。
    /// </summary>
    private void SelfTestCreateAndRename(List<string> problems)
    {
        if (_boardPath is null)
        {
            problems.Add("自检直建与改名：窗口没有当前板子，这一发不适用");

            return;
        }

        var firstPath = _boardPath;
        var folder = BoardLocator.BoardDirectory(firstPath);
        var targetName = "selftest-rename";
        var targetPath = Path.Combine(folder, targetName + CanvasStore.Extension);

        foreach (var stale in Directory.EnumerateFiles(folder, "未命名板*.canvas")
                     .Concat(Directory.EnumerateFiles(folder, targetName + "*.canvas")))
        {
            File.Delete(stale);
        }

        foreach (var stale in Directory.EnumerateFiles(folder, "*.canvas.view.json")
                     .Where(path => Path.GetFileName(path).StartsWith("未命名板", StringComparison.Ordinal)
                                    || Path.GetFileName(path).StartsWith(targetName, StringComparison.Ordinal)))
        {
            File.Delete(stale);
        }

        if (File.Exists(targetPath))
        {
            File.Delete(targetPath);
        }

        // 走真按钮的自动化 Invoke，而不是直接调 CreateNewBoard：这一发才覆盖"点加号"
        // 那根线（Click 接没接上、按钮在不在可点状态）。
        var addPeer = new ButtonAutomationPeer(AddBoardButton);
        ((IInvokeProvider)addPeer).Invoke();

        var createdPath = _boardPath!;

        // 这两个对象是"改名不重载"的证词：它们必须在直建**之后**取，否则量到的是换板本身。
        var documentBefore = _document;
        var historyBefore = _history;
        var viewBefore = _view;

        if (!File.Exists(createdPath))
        {
            problems.Add($"直建那一发没落盘：{createdPath} 不在");

            return;
        }

        if (!BoardName(createdPath).StartsWith("未命名板", StringComparison.Ordinal))
        {
            problems.Add($"直建那一发名字不是未命名板：{BoardName(createdPath)}");
        }

        if (_openBoards.Count != 2 || BoardTabs.TabItems.Count != 2)
        {
            problems.Add($"直建以后标签条读到 {BoardTabs.TabItems.Count} 枚、列表 {_openBoards.Count} 块，应各 2");
        }

        BeginRenameTab(createdPath);

        if (_renameBox is null)
        {
            problems.Add("行内改名没有换出输入框");

            return;
        }

        _renameBox.Text = targetName;
        TryCommitRename(fromBlur: false);

        if (!File.Exists(targetPath) || File.Exists(createdPath))
        {
            problems.Add($"改名没有把文件搬过去：新 {File.Exists(targetPath)}、旧 {File.Exists(createdPath)}");
        }

        var oldSidecar = CanvasStore.SidecarPath(createdPath);
        var newSidecar = CanvasStore.SidecarPath(targetPath);

        if (!File.Exists(newSidecar) || File.Exists(oldSidecar))
        {
            problems.Add($"改名没把相机 sidecar 一起搬：新 {File.Exists(newSidecar)}、旧 {File.Exists(oldSidecar)}");
        }

        if (!SameBoard(_boardPath ?? string.Empty, targetPath))
        {
            problems.Add($"改名以后当前路径还是 {_boardPath}");
        }

        if (!_openBoards.Exists(open => SameBoard(open, targetPath))
            || _openBoards.Exists(open => SameBoard(open, createdPath)))
        {
            problems.Add("改名以后开着的那份清单没换过去");
        }

        if (!Title.Contains(targetName, StringComparison.Ordinal))
        {
            problems.Add($"改名以后任务栏那行没换：Title『{Title}』");
        }

        if (!ReferenceEquals(documentBefore, _document) || !ReferenceEquals(historyBefore, _history)
            || !ReferenceEquals(viewBefore, _view))
        {
            problems.Add("改名把文档/历史/相机重载了：应该只换路径，不该丢掉这一刻的状态");
        }

        // 重名要被拒，并且还留在输入框里——拒完就退出去等于让人再开一遍。
        BeginRenameTab(targetPath);

        if (_renameBox is null)
        {
            problems.Add("重名那一发没有换出输入框");
        }
        else
        {
            _renameBox.Text = BoardName(firstPath);
            TryCommitRename(fromBlur: false);

            if (_renameBox is null || !SameBoard(_boardPath ?? string.Empty, targetPath))
            {
                problems.Add("重名那一发没有被拦住，或者把当前路径改走了");
            }

            if (!_sayRow.Text.Contains("已经在了", StringComparison.Ordinal))
            {
                problems.Add($"重名那一发没说为什么：回执『{_sayRow.Text}』");
            }

            CancelRename();
        }

        Diagnostics.Write(
            $"selftest 直建与改名: 起出 {BoardName(createdPath)}、改名为 {BoardName(targetPath)}，" +
            $"旧文件 {(File.Exists(createdPath) ? "还在" : "已走")}、tab {BoardTabs.TabItems.Count} 枚、" +
            $"文档/历史/相机同一对象、重名回执『{_sayRow.Text}』");

        // 收尾：回到出发那块板，把造出来的那枚从开着清单里拿掉，再把文件删掉。
        SwitchToBoard(firstPath);

        _openBoards.RemoveAll(open => SameBoard(open, targetPath));
        RebuildTabs();

        foreach (var leftover in new[] { targetPath, CanvasStore.SidecarPath(targetPath), createdPath,
                     CanvasStore.SidecarPath(createdPath) })
        {
            if (File.Exists(leftover))
            {
                File.Delete(leftover);
            }
        }
    }

    /// <summary>
    /// 关掉一块板＝从本机删掉它。这一发走真按钮与真 CloseCurrentBoard：建出未命名板1、放一格落盘、
    /// 关掉，文件和 sidecar 都必须不在；再点加号会重新建出未命名板1（名字已腾出来），内容为空。
    /// 另外盯一眼新标签的内容过渡：它必须为空，否则名字会先空一拍再淡进来。
    /// 跑在 %TEMP% 的自检副本上；删掉的也只是临时板。
    /// </summary>
    private void SelfTestBoardDeletion(List<string> problems)
    {
        if (_boardPath is null)
        {
            problems.Add("自检删板：窗口没有当前板子，这一发不适用");

            return;
        }

        var firstPath = _boardPath;
        var folder = BoardLocator.BoardDirectory(firstPath);
        var expectedName = "未命名板1";
        var expectedPath = Path.Combine(folder, expectedName + CanvasStore.Extension);

        foreach (var stale in Directory.EnumerateFiles(folder, "未命名板*.canvas")
                     .Concat(Directory.EnumerateFiles(folder, "*.canvas.view.json")
                         .Where(path => Path.GetFileName(path).StartsWith("未命名板", StringComparison.Ordinal))))
        {
            File.Delete(stale);
        }

        var addPeer = new ButtonAutomationPeer(AddBoardButton);
        ((IInvokeProvider)addPeer).Invoke();

        if (!SameBoard(_boardPath ?? string.Empty, expectedPath))
        {
            problems.Add($"未命名板第一发没落到 {expectedName}：_boardPath 读到 {_boardPath}");
        }

        if (BoardTabs.TabItems.Count != 2 || _openBoards.Count != 2)
        {
            problems.Add($"建出未命名板以后标签 {BoardTabs.TabItems.Count} 枚、列表 {_openBoards.Count} 块，应各 2");
        }

        var item = TabItemFor(expectedPath);

        if (item is null)
        {
            problems.Add("未命名板那一枚标签没挂上");

            return;
        }

        if (item.Header is not TextBlock header)
        {
            problems.Add("新标签的头不是我们的 TextBlock");
        }
        else if (header.Text != expectedName)
        {
            problems.Add($"新标签在建出来那一刻名字不是 {expectedName}：读到『{header.Text}』");
        }

        if (item.ContentTransitions is not { Count: 0 })
        {
            problems.Add("新标签的内容过渡没关：名字会先空一拍再淡进来");
        }

        // 放一格子并把盘落掉：关掉那一发必须把整块板子删掉，而不只是把它从标签条上拿走。
        _document.Add(CanvasNodeKind.Text, 40, 40, 240, 120);
        SaveNow();

        var beforeClose = CanvasDocument.Parse(CanvasStore.ReadAllText(expectedPath), expectedPath);

        if (beforeClose.Nodes.Count != 1)
        {
            problems.Add($"未命名板上那格子没落盘：文件里 {beforeClose.Nodes.Count} 格，应 1");
        }

        CloseCurrentBoard();

        if (BoardTabs.TabItems.Count != 1 || _openBoards.Count != 1
            || !SameBoard(_boardPath ?? string.Empty, firstPath))
        {
            problems.Add(
                $"关掉未命名板以后没回到出发那块：标签 {BoardTabs.TabItems.Count} 枚、列表 {_openBoards.Count} 块、_boardPath {_boardPath}");
        }

        if (File.Exists(expectedPath))
        {
            var afterClose = CanvasDocument.Parse(CanvasStore.ReadAllText(expectedPath), expectedPath);

            problems.Add($"关掉板子没删文件：文件还在，{afterClose.Nodes.Count} 格");
        }

        if (File.Exists(CanvasStore.SidecarPath(expectedPath)))
        {
            problems.Add("关掉板子没删相机 sidecar");
        }

        if (!_sayRow.Text.Contains("删掉了", StringComparison.Ordinal))
        {
            problems.Add($"关掉板子没删掉：回执『{_sayRow.Text}』");
        }

        ((IInvokeProvider)addPeer).Invoke();

        if (!SameBoard(_boardPath ?? string.Empty, expectedPath) || BoardName(expectedPath) != expectedName)
        {
            problems.Add($"再点加号没回到 {expectedName}：读到 {_boardPath}");
        }

        if (!File.Exists(expectedPath) || _document.Nodes.Count != 0)
        {
            problems.Add($"重新建出的 {expectedName} 不对：文件 {File.Exists(expectedPath)}、{_document.Nodes.Count} 格");
        }

        CloseCurrentBoard();

        if (File.Exists(expectedPath))
        {
            problems.Add("第二次关掉板子没删文件");
        }

        Diagnostics.Write(
            $"selftest 删板: 建出 {expectedName}、放一格并落盘、关掉后文件与 sidecar 已删；" +
            $"再点加号重新建出 {BoardName(expectedPath)}（0 格）；标签 {BoardTabs.TabItems.Count} 枚");

        foreach (var leftover in new[] { expectedPath, CanvasStore.SidecarPath(expectedPath) })
        {
            if (File.Exists(leftover))
            {
                File.Delete(leftover);
            }
        }

        if (!SameBoard(_boardPath ?? string.Empty, firstPath))
        {
            SwitchToBoard(firstPath);
        }
    }

    private static T? FindDescendant<T>(DependencyObject root, string name)
        where T : FrameworkElement
    {
        if (root is T match && string.Equals(match.Name, name, StringComparison.Ordinal))
        {
            return match;
        }

        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
        {
            var found = FindDescendant<T>(VisualTreeHelper.GetChild(root, index), name);

            if (found is not null)
            {
                return found;
            }
        }

        return null;
    }
}
