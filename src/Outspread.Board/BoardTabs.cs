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
        SettingsButton.Click += (_, _) => OpenSettingsMenu();

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
            // 原生关闭键整格归零：× 改由表头里的覆盖层画（不参与布局，悬停只切透明度）。
            // 这样"有/没有 ×"不再改变标签宽度，也就没有悬停时宽度抖动带来的闪烁。
            ["TabViewItemHeaderCloseButtonWidth"] = 0,
            ["TabViewItemHeaderCloseButtonHeight"] = 0,
            ["TabViewItemHeaderCloseFontSize"] = TabStrip.CloseGlyphPx,
            ["TabViewItemHeaderCloseMargin"] = new Thickness(0),
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

        // 点到别处＝这一格改名到此为止。**不能只等 LostFocus**：这一版按一块不持焦点的区域（板面、
        // 条上的空白、别的标签）不会把焦点从 TextBox 拿走，所以"点了别处还在改名"是必然不是偶发
        // ——板子那侧的编辑器早就为同一条写过注释（MainWindow.xaml.cs:700-706）。
        // handledEventsToo：标签/按钮自己会把这一发吃掉，那条路径也要能收。
        Root.AddHandler(
            UIElement.PointerPressedEvent,
            new Microsoft.UI.Xaml.Input.PointerEventHandler(EndRenameOnPress),
            handledEventsToo: true);

        StyleAddBoardButton(dark);
        StyleSettingsButton(dark);
        PositionAddBoardButton();
    }

    /// <summary>
    /// 标题条右端的设置入口。形状与加号同一套（30 高、圆角、透明底、悬停才有底色），
    /// 但它是"更多/设置"那一格：竖排三点、靠窗口按钮那边，和浏览器一致。
    /// </summary>
    private void StyleSettingsButton(bool dark)
    {
        var transparent = new SolidColorBrush(Colors.Transparent);
        var hover = Alpha(TabStrip.Hover(dark), TabStrip.HoverAlpha(dark));
        var pressed = Alpha(TabStrip.Line(dark), TabStrip.HoverAlpha(dark) * 1.8);
        var muted = new SolidColorBrush(NoteStyling.ToColor(TabStrip.InactiveInk(dark)));
        var ink = new SolidColorBrush(NoteStyling.ToColor(TabStrip.ActiveInk(dark)));

        SettingsButton.Width = TabStrip.SettingsPx;
        SettingsButton.Height = TabStrip.SettingsPx;
        SettingsButton.CornerRadius = new CornerRadius(TabStrip.SettingsRadius);
        SettingsButton.Padding = new Thickness(0);
        SettingsButton.BorderThickness = new Thickness(0);
        SettingsButton.Background = transparent;
        SettingsButton.Foreground = muted;

        // 竖排三点：画三枚 2.5px 的圆点，不用 "⋮" 字形——W 字体里那个字形的点是方的，
        // 2px 的方块在 30px 钮里就是"廉价"的来源。圆点跟横排/竖排的间距都由这里定。
        var dots = new StackPanel
        {
            Spacing = TabStrip.SettingsDotGapPx,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            IsHitTestVisible = false,
        };

        for (var index = 0; index < 3; index++)
        {
            dots.Children.Add(new Microsoft.UI.Xaml.Shapes.Ellipse
            {
                Width = TabStrip.SettingsDotPx,
                Height = TabStrip.SettingsDotPx,
                Fill = muted,
                IsHitTestVisible = false,
            });
        }

        SettingsButton.Content = dots;

        SettingsButton.Resources["ButtonBackground"] = transparent;
        SettingsButton.Resources["ButtonBackgroundPointerOver"] = hover;
        SettingsButton.Resources["ButtonBackgroundPressed"] = pressed;
        SettingsButton.Resources["ButtonBackgroundDisabled"] = transparent;
        SettingsButton.Resources["ButtonBorderBrush"] = transparent;
        SettingsButton.Resources["ButtonBorderBrushPointerOver"] = transparent;
        SettingsButton.Resources["ButtonBorderBrushPressed"] = transparent;
        SettingsButton.Resources["ButtonBorderBrushDisabled"] = transparent;
        SettingsButton.Resources["ButtonForeground"] = muted;
        SettingsButton.Resources["ButtonForegroundPointerOver"] = ink;
        SettingsButton.Resources["ButtonForegroundPressed"] = ink;
        SettingsButton.Resources["ButtonForegroundDisabled"] = new SolidColorBrush(NoteStyling.ToColor(Sheet.Quiet(dark)));
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
        AddBoardButton.Padding = new Thickness(0);
        AddBoardButton.BorderThickness = new Thickness(0);
        AddBoardButton.Background = transparent;
        AddBoardButton.Foreground = muted;
        AddBoardButton.HorizontalContentAlignment = HorizontalAlignment.Center;
        AddBoardButton.VerticalContentAlignment = VerticalAlignment.Center;

        // 2026-10-23：全角 "＋" 换成画出来的十字。字体字符的笔画粗细和光学中心都不归我们管，
        // 在 30px 的圆里就会"看着有点歪、又有点贵"；两根 1.5px 的条由我们自己定。
        AddBoardButton.Content = PlusIcon();

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

        // 右端那枚设置入口也占地方：加号最多停到它左边 8px，别在标签多的时候钻到它底下。
        var available = TitleBarHost.ActualWidth - TitleBarHost.Padding.Right
            - TabStrip.SettingsInsetPx - TabStrip.SettingsPx - 8;
        left = Math.Clamp(left, 8, Math.Max(8, available - TabStrip.AddPx - 4));

        var top = (TabStrip.StripHeight - TabStrip.AddPx) / 2 + TabStrip.AddOpticalDropPx;
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

    /// <summary>加号：两根 1.2px 的细条，12×12。画出来而不是写 "＋"——后者是字体排出来的，
    /// 笔画和光学中心都不受控；画画的那一枚在 30px 圆里才稳。</summary>
    private static PathIcon PlusIcon()
    {
        var plus = new GeometryGroup();
        plus.Children.Add(new RectangleGeometry { Rect = new Rect(0, 5.4, 12, 1.2) });
        plus.Children.Add(new RectangleGeometry { Rect = new Rect(5.4, 0, 1.2, 12) });

        return new PathIcon
        {
            Data = plus,
            Width = TabStrip.AddGlyphPx,
            Height = TabStrip.AddGlyphPx,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };
    }

    private static string BoardName(string boardPath)
        => IsAllNotes(boardPath) ? "所有便签" : Path.GetFileNameWithoutExtension(boardPath);

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
                    // 改名途中这一枚的宽度由 BeginRenameTab 钉住（见 FreezeRenameSlot）：这里一次都不许碰，
                    // 否则每一趟 Rebuild 都会把 300 压回 220，字又跳一次。
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
            IsClosable = !IsAllNotes(path), // 「所有便签」固定、关不掉
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
        Add(SettingsButton);
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
            $"标题栏: 交互区 {rects.Count} 块（标签 {BoardTabs.TabItems.Count} 枚 + 加号 + 设置 + 3 枚标题按钮），" +
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
        var text = new TextBlock
        {
            Text = name,
            FontFamily = NoteStyling.NoteFont,
            FontSize = TabStrip.FontPx,
            TextTrimming = TextTrimming.CharacterEllipsis,
            TextWrapping = TextWrapping.NoWrap,
            VerticalAlignment = VerticalAlignment.Center,

            // 可关的标签始终给 × 留出这一格：它不再"悬停时长出来"，标签宽度所以在悬停前后
            // 一个像素都不变；标题也停在 × 左侧，不会被那一枚盖掉最后一个字。
            Margin = item.IsClosable ? new Thickness(0, 0, TabStrip.CloseHitPx, 0) : new Thickness(0),
        };

        // × 与标题同格：覆盖层压在字上面，不占一格宽度。标签宽只由标题决定，
        // 悬停时只换它的 Opacity/命中，所以宽度不会跳，也不会闪。
        var header = new Grid { VerticalAlignment = VerticalAlignment.Center };
        header.Children.Add(text);
        header.Children.Add(BuildHeaderClose(item, name));

        item.Header = header;
        PaintHeader(item, hovered: false);
    }

    /// <summary>
    /// 表头里的 ×。原生 TabView 关闭键把宽度算进标签布局，悬停一显一隐就改宽度，
    /// 指针被挤出去再回来——他看到的"边缘闪烁"就是这么来的。这枚覆盖层始终在表头里，
    /// 只切 Opacity 与 IsHitTestVisible；可以盖住标题的尾巴，但标签宽度一个像素不动。
    /// </summary>
    private Button BuildHeaderClose(TabViewItem item, string name)
    {
        var dark = NoteStyling.IsDark;
        var transparent = new SolidColorBrush(Colors.Transparent);
        var hover = Alpha(TabStrip.Line(dark), TabStrip.CloseHoverAlpha(dark));
        var pressed = Alpha(TabStrip.Line(dark), TabStrip.CloseHoverAlpha(dark) * 1.8);
        var muted = new SolidColorBrush(NoteStyling.ToColor(TabStrip.InactiveInk(dark)));
        var ink = new SolidColorBrush(NoteStyling.ToColor(TabStrip.ActiveInk(dark)));

        var close = new Button
        {
            Name = "TabCloseButton",
            Width = TabStrip.CloseHitPx,
            Height = TabStrip.CloseHitPx,
            MinWidth = 0,
            MinHeight = 0,
            Padding = new Thickness(0),
            BorderThickness = new Thickness(0),
            Background = transparent,
            Foreground = muted,
            CornerRadius = new CornerRadius(TabStrip.CloseRadius),
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Center,
            IsTabStop = false,
            Opacity = 0,
            IsHitTestVisible = false,
            Tag = item,
            Content = new FontIcon
            {
                Glyph = "",
                FontFamily = new FontFamily("Segoe MDL2 Assets"),
                FontSize = TabStrip.CloseGlyphPx,
            },
        };

        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(close, $"关闭 {name}");
        ToolTipService.SetToolTip(close, $"关闭 {name}");

        close.Resources["ButtonBackground"] = transparent;
        close.Resources["ButtonBackgroundPointerOver"] = hover;
        close.Resources["ButtonBackgroundPressed"] = pressed;
        close.Resources["ButtonBackgroundDisabled"] = transparent;
        close.Resources["ButtonBorderBrush"] = transparent;
        close.Resources["ButtonBorderBrushPointerOver"] = transparent;
        close.Resources["ButtonBorderBrushPressed"] = transparent;
        close.Resources["ButtonBorderBrushDisabled"] = transparent;
        close.Resources["ButtonForeground"] = muted;
        close.Resources["ButtonForegroundPointerOver"] = ink;
        close.Resources["ButtonForegroundPressed"] = ink;
        close.Resources["ButtonForegroundDisabled"] = new SolidColorBrush(NoteStyling.ToColor(Sheet.Quiet(dark)));

        close.Click += (_, _) =>
        {
            if (item.IsClosable && item.Tag is string path)
            {
                CloseTab(path);
            }
        };

        return close;
    }

    /// <summary>标题没变就只重刷墨色，不把表头换掉——整条重建时换头会把鼠标正停着
    /// 那一枚的悬停墨打回静音，下一次 PointerEntered 又不一定会来。</summary>
    private void UpdateHeader(TabViewItem item, string name)
    {
        if (item.Header is Grid header
            && header.Children.OfType<TextBlock>().FirstOrDefault() is { } text
            && string.Equals(text.Text, name, StringComparison.Ordinal))
        {
            PaintHeader(item, hovered: false);

            return;
        }

        SetHeader(item, name);
    }

    /// <summary>活动那一枚用正文墨、其余退回静音墨；悬停的静音标签临时提成正文墨。
    /// × 只在"可关 且（活动或悬停）"时出现，位置与宽度始终占着，不参与布局。</summary>
    private void PaintHeader(TabViewItem item, bool hovered)
    {
        if (item.Header is not Grid header || item.Tag is not string path)
        {
            return;
        }

        var active = SameBoard(path, _boardPath ?? string.Empty);
        var dark = NoteStyling.IsDark;

        if (header.Children.OfType<TextBlock>().FirstOrDefault() is { } text)
        {
            text.Foreground = new SolidColorBrush(
                NoteStyling.ToColor(active || hovered ? TabStrip.ActiveInk(dark) : TabStrip.InactiveInk(dark)));
            text.FontWeight = active ? Microsoft.UI.Text.FontWeights.Medium : Microsoft.UI.Text.FontWeights.Normal;
        }

        if (header.Children.OfType<Button>().FirstOrDefault() is { } close)
        {
            // × 也在这条皮上：换主题时它的本地刷子要跟着换，否则深色下还是浅色那只。
            var transparent = new SolidColorBrush(Colors.Transparent);
            close.Resources["ButtonBackground"] = transparent;
            close.Resources["ButtonBackgroundPointerOver"] = Alpha(TabStrip.Line(dark), TabStrip.CloseHoverAlpha(dark));
            close.Resources["ButtonBackgroundPressed"] = Alpha(TabStrip.Line(dark), TabStrip.CloseHoverAlpha(dark) * 1.8);
            close.Resources["ButtonForeground"] = new SolidColorBrush(NoteStyling.ToColor(TabStrip.InactiveInk(dark)));
            close.Resources["ButtonForegroundPointerOver"] = new SolidColorBrush(NoteStyling.ToColor(TabStrip.ActiveInk(dark)));
            close.Resources["ButtonForegroundPressed"] = new SolidColorBrush(NoteStyling.ToColor(TabStrip.ActiveInk(dark)));

            var show = item.IsClosable && (active || hovered);
            close.Opacity = show ? 1 : 0;
            close.IsHitTestVisible = show;
        }
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
        CommitWheelPreview();
        _saveTimer?.Stop();

        if (_boardPath is null || _document is null)
        {
            return;
        }

        try
        {
            _view.Prune(_document.Nodes.Select(node => node.Id));

            // 哨兵没有自己的文件，别拿它拼一个 sidecar 路径。
            if (!IsAllNotes(_boardPath))
            {
                _view.Save(CanvasStore.SidecarPath(_boardPath));
            }
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

        if (IsAllNotes(_boardPath))
        {
            Say("『所有便签』是固定的总览页，关不掉");
            return;
        }

        if (RealBoardCount <= 1)
        {
            Say("最后一块板不给关：先按 Ctrl+T 起一块，再关它——关掉一块＝从本机删掉这块板");

            return;
        }

        CancelRename();

        var closing = CurrentBoardIndex();
        var closingPath = _openBoards[closing];
        var closedName = BoardName(closingPath);
        var syncWasOn = _sync?.Enabled == true;
        var neighbour = NeighbourBoard(closing);

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
        if (IsAllNotes(path))
        {
            Say("『所有便签』是固定的总览页，关不掉");
            return;
        }

        if (_boardPath is null)
        {
            return;
        }

        if (RealBoardCount <= 1)
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
        if (RealBoardCount <= 1)
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

        var closedPaths = _openBoards.Where(open => !SameBoard(open, path) && !IsAllNotes(open)).ToList();
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
        if (IsAllNotes(path))
        {
            Say("『所有便签』是固定的总览页，没有名字可改");
            return;
        }

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

            // 框自己不带字面：宽度全由那一枚标签钉住（见 FreezeRenameSlot）。留 180/240 在框上，
            // 打字时每敲一下标签就跟着内容长一次——他 10-23 报的"文字位置会浮动"。
            //
            // 高度两头都不许有：写死 26 会把字顶高，留着主题那枚最小高也一样高——真机量过两遍，
            // 标题的墨迹中心在第 20.8 行、改名框在 16.2 行，差 4.6px（他 10-23 第二条"双击编辑时
            // 还是会往上动一下"）。模板把字长在内容宿主顶上，`VerticalContentAlignment=Center`
            // 管不到它；只有让框收到"一行字自己那么高"再整枚居中，字才回到标题原来那条线上。
            MinWidth = 0,
            MinHeight = 0,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Center,
            Padding = new Thickness(0),
            BorderThickness = new Thickness(0),
            Background = new SolidColorBrush(Colors.Transparent),
            Foreground = new SolidColorBrush(NoteStyling.ToColor(TabStrip.ActiveInk(NoteStyling.IsDark))),
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

        FreezeRenameSlot(item, BoardName(path));
        item.IsClosable = false;
        item.Header = box;

        box.KeyDown += OnRenameKeyDown;
        box.LostFocus += OnRenameLostFocus;

        // TextBox 默认模板在"有字且聚焦"时会在右边亮一枚圆形清除按钮（× 那枚）。它在模板的视觉状态里，
        // 不在 Border* 上，所以那几枚主题资源管不着。真机截图里它就长在标签的改名框里，既吃宽度又像
        // 一个能一键清空名字的按钮——这里按模板件的名字把它收掉，不重写整份模板。
        // 收两次：状态机在"有字/没字"翻转时会把 Visibility 设回去，而打字正是它翻转的那一拍。
        box.Loaded += (_, _) => HideRenameClearButton(box);
        box.TextChanged += (_, _) => HideRenameClearButton(box);

        FocusRenameBox(box);
    }

    /// <summary>把改名框模板里那枚清除按钮收掉（模板件要到第一次排版之后才在树里）。</summary>
    private static void HideRenameClearButton(DependencyObject root)
    {
        if (root is FrameworkElement { Name: "DeleteButton" } clear)
        {
            clear.Visibility = Visibility.Collapsed;

            return;
        }

        var children = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetChildrenCount(root);

        for (var index = 0; index < children; index++)
        {
            HideRenameClearButton(Microsoft.UI.Xaml.Media.VisualTreeHelper.GetChild(root, index));
        }
    }

    /// <summary>
    /// 开始改名时把这一枚标签的宽度**量一次、钉住**（`Min/Max/Width` 给同一个数），之后打字不再改。
    /// 旧写法是"临时放宽到 300"＋输入框自带 180/240 的字面：标签先跳一次宽，然后每敲一下按内容再长
    /// 一次——那就是他看到的"文字位置会浮动"。钉住以后长名字在框里自己滚，Chrome 也是这一条。
    /// <para>
    /// 钉在哪个数：至少给 <see cref="TabStrip.RenameTabMinWidth"/> 的字面（编辑要有地方看，
    /// 5 个字的框改不了 40 个字的名字），名字更长就一次放宽到放得下（上限 300）。这一跳只发生在
    /// **进改名那一拍**，而且是向右长——标签的左沿不动，标题的字不动，所以它不是"浮动"；
    /// 每敲一下再长一次才是。
    /// </para>
    /// </summary>
    private void FreezeRenameSlot(TabViewItem item, string name)
    {
        var wanted = Math.Ceiling(MeasureTabName(name)) + (TabStrip.PadLeft * 2) + TabStrip.RenameSlackPx;
        var from = item.ActualWidth > 0 ? item.ActualWidth : TabStrip.RenameTabMinWidth;

        var slot = Math.Clamp(Math.Max(wanted, from), TabStrip.RenameTabMinWidth, TabStrip.RenameTabMaxWidth);

        item.MinWidth = slot;
        item.MaxWidth = slot;
        item.Width = slot;
    }

    private static void ReleaseRenameSlot(TabViewItem item)
    {
        item.MinWidth = TabStrip.TabMinWidth;
        item.MaxWidth = TabStrip.TabMaxWidth;
        item.Width = double.NaN;
    }

    private TextBlock? _renameProbe;

    /// <summary>名字在这一套标签字（<see cref="TabStrip.FontPx"/>＋便签同族）下有多宽。</summary>
    private double MeasureTabName(string name)
    {
        _renameProbe ??= new TextBlock
        {
            FontFamily = NoteStyling.NoteFont,
            FontSize = TabStrip.FontPx,
            FontWeight = Microsoft.UI.Text.FontWeights.Medium,
            TextWrapping = TextWrapping.NoWrap,
        };

        _renameProbe.Text = name;
        _renameProbe.Measure(new Windows.Foundation.Size(double.PositiveInfinity, double.PositiveInfinity));

        return _renameProbe.DesiredSize.Width;
    }

    /// <summary>
    /// 把**键盘**焦点交给那一行字。只调 <c>Focus(FocusState.Programmatic)</c> 换不来光标：双击那一下
    /// 的焦点还挂在 <c>TabViewItem</c>（它是 Selector）上，程序化"占个位"不产生光标，他 10-23 报的
    /// "没有光标"就是这一条。<c>FocusManager.TryFocusAsync(box, FocusState.Keyboard)</c> 走框架自己那趟
    /// 焦点移动，光标会亮；它失败（那一刻窗口没激活之类）再退回 Programmatic——至少字还打得进去。
    /// 全选放在焦点之后。
    /// </summary>
    private void FocusRenameBox(TextBox box)
    {
        _ = DispatcherQueue.TryEnqueue(async () =>
        {
            if (!ReferenceEquals(_renameBox, box))
            {
                return;
            }

            var moved = false;

            try
            {
                // 这一版的形状：`FocusManager.TryFocusAsync(element, FocusState)` 回 `FocusMovementResult`，
                // 判"移没移成"看它的 `Succeeded`（`UIElement.TryFocusAsync` 与 `WasFocusMoved` 都不在这份投影里）。
                var movedResult = await Microsoft.UI.Xaml.Input.FocusManager.TryFocusAsync(box, FocusState.Keyboard);

                moved = movedResult?.Succeeded == true;
            }
            catch (Exception ex) when (ex is InvalidOperationException or NotSupportedException)
            {
                moved = false;
            }

            if (!ReferenceEquals(_renameBox, box))
            {
                return;
            }

            if (!moved)
            {
                box.Focus(FocusState.Programmatic);
            }

            box.SelectAll();
        });
    }

    /// <summary>
    /// 窗口里任何一处按下，只要落点不在这一行字里，改名就到此为止（按提交处理，失败在回执里说为什么）。
    /// 触发改名那一下不会被误收：<c>DoubleTapped</c> 在第二发 <c>PointerPressed</c> **之后**才到，
    /// 那一刻 <c>_renamingPath</c> 还是空的。
    /// </summary>
    private void EndRenameOnPress(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
    {
        if (!EndRenameOnPress(e.OriginalSource as DependencyObject))
        {
            return;
        }

        TryCommitRename(fromBlur: true);
    }

    /// <summary>
    /// 这一发按下该不该结束改名？落在框里（含框自己的模板零件）＝不该；落在**同一枚标签**的
    /// 上下那条空气里＝也不该——框现在只有一行字那么高，标签比它高，点在自己那枚标签上不该被
    /// 当成"点了别处"。别的任何地方＝收。
    /// </summary>
    private bool EndRenameOnPress(DependencyObject? source)
    {
        if (_renamingPath is null || _renameBusy || _renamePreview || _renameBox is null)
        {
            return false;
        }

        var own = TabItemFor(_renamingPath);

        while (source is not null)
        {
            if (ReferenceEquals(source, _renameBox) || (own is not null && ReferenceEquals(source, own)))
            {
                return false;
            }

            source = VisualTreeHelper.GetParent(source);
        }

        return true;
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

            FocusRenameBox(box);

            return;
        }

        var newPath = result.BoardPath!;

        if (string.Equals(oldPath, newPath, StringComparison.Ordinal))
        {
            CancelRename();

            return;
        }

        _renameBusy = true;

        var item = TabItemFor(oldPath);

        _renamingPath = null;
        _renameBox = null;

        try
        {
            var problem = ApplyBoardRename(oldPath, newPath);

            if (problem is not null)
            {
                Say($"改名没成：{problem}");

                if (item is not null)
                {
                    item.Tag = oldPath;
                    ReleaseRenameSlot(item);
                    item.IsClosable = true;
                    SetHeader(item, oldName);
                }

                if (!File.Exists(_boardPath ?? string.Empty))
                {
                    Diagnostics.Write($"rename left the current board path missing: {_boardPath}");
                }
            }
        }
        finally
        {
            _renameBusy = false;
        }
    }

    /// <summary>
    /// 本机这一侧的改名：文件与相机 sidecar 一起搬、账本跟着搬（祖先与待发标记跟着走）、标签换名、
    /// 当前这块的话务按新路径重启。
    /// <para>
    /// **两条路共用这一份**：改名框提交（人打的字）与同步跟上的改名（同伴发的标记）。一个名字两处
    /// 真相，正是会漏改的地方——少搬 sidecar 会丢相机，少搬账本会把改名读成"首轮没祖先"并丢内容。
    /// </para>
    /// </summary>
    /// <returns>null＝搬好了；否则是那句"为什么没搬成"，怎么收场归调用方。</returns>
    /// <param name="publishMarker">
    /// 只有**改名的那一台**欠仓里一条标记（第 5 条的 ③④）。跟着别人的标记搬过来那一台传 false：
    /// 仓里那条就是它搬的理由，再补一条同名的进去只会让 `by` 不再等于"谁改的名"。
    /// </param>
    private string? ApplyBoardRename(string oldPath, string newPath, bool publishMarker = true)
    {
        var current = SameBoard(oldPath, _boardPath ?? string.Empty);
        var syncWasOn = _sync?.Enabled == true;
        var item = TabItemFor(oldPath);

        if (current)
        {
            FlushCurrentBoard();
            StopBoardServicesForRename();
        }

        try
        {
            MoveBoardFiles(oldPath, newPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            if (current)
            {
                StartBoardServicesAfterRename();
            }

            return ex.Message;
        }

        // 账本跟着搬（契约「改名怎么跨端跟上」第 4 条）：祖先、`publishedSha`、`rev` 带过去，
        // 两个令牌清掉（它们属于旧路径），并留下 `pendingRename` 让下一轮去补那条标记。
        var ledgerProblem = SyncLayout.RenameLedger(oldPath, newPath, publishMarker: publishMarker);

        if (ledgerProblem is not null)
        {
            Diagnostics.Write($"改名的账本没搬过去：{ledgerProblem}");
        }

        // 一旦改了名，这块板就是用户的命名板了；关标签时不再清内容。
        var index = _openBoards.FindIndex(open => SameBoard(open, oldPath));

        if (index >= 0)
        {
            _openBoards[index] = newPath;
        }

        if (current)
        {
            _boardPath = newPath;

            // 启动记忆存的是路径：改名以后旧路径就没了，不跟着改，下次开屏会落回首次那一条。
            RememberLastBoard(newPath);
        }

        if (item is not null)
        {
            item.Tag = newPath;
            ReleaseRenameSlot(item);
            item.IsClosable = true;
        }

        RebuildTabs();

        if (current)
        {
            StartBoardServicesAfterRename();
        }

        Say(syncWasOn
            ? ledgerProblem is null
                ? $"改名为 {BoardName(newPath)}｜下一轮同步把改名发到远端，仓里旧名那两份跟着删（同伴那一台会自己搬过来）"
                : $"改名为 {BoardName(newPath)}｜远端还没跟上：{ledgerProblem}"
            : $"改名为 {BoardName(newPath)}");

        // 账本没搬成不是"改名失败"：文件已经搬了、本机改名已经成了，只是同步这一轮跟不上——
        // 那句话已经写进日志与回执，这里不把它升级成回滚（回滚会把一块已经改好的板子搬回旧名）。
        return null;
    }

    /// <summary>
    /// 同步发现这块板在别处被改了名（契约第 4 条"跟的人先搬自己的盘，再走正常一轮"）。
    /// 两种情况**不搬**，因为都不可逆：本机已经有一块同名板（覆盖它等于删掉一块板），
    /// 或者你正在改名框里打字（那一发的手归你）。
    /// </summary>
    private void FollowRemoteRename(string newStem)
    {
        var oldPath = _boardPath;

        if (oldPath is null)
        {
            return;
        }

        if (_renameBusy || _renamingPath is not null)
        {
            Say($"远端把这块板改名为 {newStem}｜你正在改名，先不动，下一轮再跟");

            return;
        }

        var folder = Path.GetDirectoryName(oldPath) ?? ".";
        var newPath = Path.Combine(folder, newStem + CanvasStore.Extension);

        if (File.Exists(newPath) || SameBoard(newPath, oldPath))
        {
            Say($"远端把这块板改名为 {newStem}，但本机已经有同名的一块：没动，两块都还在，你自己挑");

            return;
        }

        var problem = ApplyBoardRename(oldPath, newPath, publishMarker: false);

        Say(problem is null
            ? $"这块板在别处被改名为 {newStem}，本机跟着搬好了"
            : $"远端把这块板改名为 {newStem}，本机没跟上：{problem}");

        // 搬完了按新名字立刻再来一轮：这一发要把本地那份带上新名字发上去，不该等下一次轮询。
        _sync?.NotifyChanged();
    }

    /// <summary>
    /// 同步跟上一个改名的那一发（契约「改名怎么跨端跟上」第 4 条）。这里不碰网络：引擎那半边由核心
    /// 测试 <c>A_board_the_far_side_renamed_leaves_this_disk_alone</c> 钉；这一发钉的是**动手搬的这半边**，
    /// 因为它搬的是你的文件。三种情况都要有读数：正常搬（含 sidecar 与标签）、本机已有同名板不搬、
    /// 你正在改名框里打字不搬。跑在 %TEMP% 的自检副本上。
    /// </summary>
    private void SelfTestRemoteRenameFollow(List<string> problems)
    {
        if (_boardPath is null)
        {
            problems.Add("自检跟随改名：窗口没有当前板子，这一发不适用");

            return;
        }

        var home = _boardPath;
        var before = problems.Count;
        var folder = Path.GetDirectoryName(home)!;
        var oldPath = Path.Combine(folder, "selftest-rename-from.canvas");
        var newPath = Path.Combine(folder, "selftest-rename-to.canvas");

        void Clean(params string[] paths)
        {
            foreach (var path in paths)
            {
                File.Delete(path);
                File.Delete(CanvasStore.SidecarPath(path));
            }
        }

        Clean(oldPath, newPath);

        try
        {
            CanvasStore.Write(oldPath, CanvasDocument.CreateNew().ToJson());
            SwitchToBoard(oldPath);

            _document.Add(CanvasNodeKind.Text, 40, 40, 240, 120, id: "urename1");
            SaveNow();
            File.WriteAllText(CanvasStore.SidecarPath(oldPath), "{ \"zoom\": 1.5 }");

            FollowRemoteRename("selftest-rename-to");

            if (File.Exists(oldPath))
            {
                problems.Add("跟随改名：旧名那份还在，等于搬成了两份");
            }

            if (!File.Exists(newPath))
            {
                problems.Add("跟随改名：新名那份没出现");
            }
            else
            {
                if (CanvasDocument.Parse(CanvasStore.ReadAllText(newPath), newPath).Nodes.Count != 1)
                {
                    problems.Add("跟随改名：那一格没跟着搬过去");
                }

                if (!File.Exists(CanvasStore.SidecarPath(newPath)))
                {
                    problems.Add("跟随改名：相机 sidecar 没搬，视野会回到默认");
                }

                if (!SameBoard(_boardPath ?? string.Empty, newPath))
                {
                    problems.Add($"跟随改名：当前路径还是 {_boardPath}");
                }

                if (TabItemFor(newPath) is null || !Title.Contains("selftest-rename-to", StringComparison.Ordinal))
                {
                    problems.Add("跟随改名：标签或任务栏那行没换名");
                }
            }

            // 本机已经有一块同名板 ⇒ 一个字都不搬（覆盖它是不可逆的）。
            var clashFrom = Path.Combine(folder, "selftest-clash-a.canvas");
            var clashTo = Path.Combine(folder, "selftest-clash-b.canvas");

            Clean(clashFrom, clashTo);

            CanvasStore.Write(clashFrom, CanvasDocument.CreateNew().ToJson());
            SwitchToBoard(clashFrom);
            _document.Add(CanvasNodeKind.Text, 10, 10, 100, 100, id: "uclash1");
            SaveNow();

            CanvasStore.Write(clashTo, CanvasDocument.CreateNew().ToJson());
            SwitchToBoard(clashTo);
            _document.Add(CanvasNodeKind.Text, 20, 20, 100, 100, id: "uclash2");
            SaveNow();

            var clashBefore = CanvasStore.ReadAllText(clashTo);

            SwitchToBoard(clashFrom);
            FollowRemoteRename("selftest-clash-b");

            if (!SameBoard(_boardPath ?? string.Empty, clashFrom) || File.Exists(clashFrom) is false
                || CanvasStore.ReadAllText(clashTo) != clashBefore)
            {
                problems.Add("跟随改名撞上了本机同名板：不该搬，那一发搬了或改动了东西");
            }

            // 正在改名 ⇒ 不搬（那一发的手归你）。
            BeginRenameTab(clashFrom);

            FollowRemoteRename("selftest-clash-c");

            if (_renamingPath is null || !SameBoard(_boardPath ?? string.Empty, clashFrom))
            {
                problems.Add("正在改名时被跟随改名打断了：那一发该等");
            }

            CancelRename();

            SwitchToBoard(clashTo);
            CloseCurrentBoard();
            SwitchToBoard(clashFrom);
            CloseCurrentBoard();

            // 搬过去那一块也得关掉再走：留着它，后面几条自检会看到一枚指向已删文件的标签
            // （"所有便签往返"就是这么炸的）。
            SwitchToBoard(newPath);
            CloseCurrentBoard();

            SwitchToBoard(home);

            Diagnostics.Write(problems.Count == before
                ? "selftest 跟随改名: 搬好了（含 sidecar 与标签）；本机同名板不搬；正在改名不搬"
                : $"selftest 跟随改名: 这一发有 {problems.Count - before} 条没过（见上面的 FAIL 行）");
        }
        finally
        {
            Clean(oldPath, newPath, Path.Combine(folder, "selftest-clash-a.canvas"), Path.Combine(folder, "selftest-clash-b.canvas"));
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
            ReleaseRenameSlot(item);
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

        _sync = new SyncHost(_boardPath, Say, DispatcherQueue, FollowRemoteRename);
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

        if (BoardTabs.TabItems.Count != 3 || _openBoards.Count != 3)
        {
            problems.Add($"两块板开着时标签条读到 {BoardTabs.TabItems.Count} 枚标签、列表里 {_openBoards.Count} 块（应为 2 块板 + 固定『所有便签』=3）");
        }

        if (BoardTabs.SelectedIndex != 2)
        {
            problems.Add($"切过去以后选中的不是第二块板那枚标签（哨兵占 0，第二块在 2），读到第 {BoardTabs.SelectedIndex} 枚");
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

        if (BoardTabs.TabItems.Count != 3 || !SameBoard(_boardPath ?? string.Empty, firstPath))
        {
            problems.Add($"切回第一块没切回来：标签 {BoardTabs.TabItems.Count} 枚，_boardPath {_boardPath}");
        }

        // 关掉非当前那块：文件与 sidecar 必须一起走，当前这块一个字不动。
        CloseTab(secondPath);

        if (BoardTabs.TabItems.Count != 2 || _openBoards.Count != 2
            || !SameBoard(_boardPath ?? string.Empty, firstPath))
        {
            problems.Add($"关掉第二块以后应只剩『所有便签』＋第一块：标签 {BoardTabs.TabItems.Count} 枚、列表 {_openBoards.Count} 块，_boardPath {_boardPath}");
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

        if (BoardTabs.TabItems.Count != 2 || _openBoards.Count != 2 || !File.Exists(firstPath))
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
        var expectedRegions = BoardTabs.TabItems.Count + 5;

        if (passthrough.Length < expectedRegions)
        {
            problems.Add(
                $"标题栏交互区只标了 {passthrough.Length} 块，应至少 {expectedRegions} 块（{BoardTabs.TabItems.Count} 枚标签 + 加号 + 设置 + 3 枚标题按钮）：输入会被非客户区吞掉");
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

        if (add.Content is not PathIcon plus || plus.Width != TabStrip.AddGlyphPx || plus.Height != TabStrip.AddGlyphPx)
        {
            problems.Add("加号不是那枚 12×12 画出来的十字（字体字符会让笔画与站位失控）");
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

        var nativeClose = FindDescendant<Button>(selected, "CloseButton");
        var selectedClose = FindDescendant<Button>(selected, "TabCloseButton");
        var inactiveClose = FindDescendant<Button>(inactive, "TabCloseButton");
        var selectedContainer = FindDescendant<Grid>(selected, "TabContainer");
        var transition = selectedContainer?.BackgroundTransition;

        // 原生关闭键整格归零；× 是表头里的覆盖层，不占布局宽度。
        if (nativeClose is { ActualWidth: > 0.5 })
        {
            problems.Add($"原生关闭键还占着 {nativeClose.ActualWidth:0.#}px 宽：× 又会撑标签");
        }

        if (selectedClose is null)
        {
            problems.Add("活动标签的表头里没有关闭覆盖层（TabCloseButton）");
        }
        else
        {
            if (selected.IsClosable && selectedClose.Opacity < 0.99)
            {
                problems.Add($"可关的活动标签上 × 没显出来：Opacity {selectedClose.Opacity:0.##}");
            }

            if (selectedClose.CornerRadius.TopLeft != TabStrip.CloseRadius
                || selectedClose.Width != TabStrip.CloseHitPx)
            {
                problems.Add($"关闭覆盖层读到 {selectedClose.Width:0.#}×{selectedClose.Height:0.#}、圆角 {selectedClose.CornerRadius.TopLeft}，应 {TabStrip.CloseHitPx}×{TabStrip.CloseHitPx} 正圆");
            }

            if (selectedClose.Content is not FontIcon)
            {
                problems.Add("关闭覆盖层的内容不是那枚 × 字形");
            }
        }

        // 宽度的回归锁：同一枚标签，× 显示/隐藏只许改 Opacity，ActualWidth 必须一个像素不动。
        if (inactiveClose is not null)
        {
            var hiddenWidth = inactive.ActualWidth;
            inactiveClose.Opacity = 1;
            inactiveClose.IsHitTestVisible = true;
            BoardTabs.UpdateLayout();
            var shownWidth = inactive.ActualWidth;

            PaintHeader(inactive, hovered: false);
            BoardTabs.UpdateLayout();
            var backWidth = inactive.ActualWidth;

            if (Math.Abs(shownWidth - hiddenWidth) > 0.01 || Math.Abs(backWidth - hiddenWidth) > 0.01)
            {
                problems.Add(
                    $"× 显示/隐藏改了标签宽度：{hiddenWidth:0.##} → {shownWidth:0.##} → {backWidth:0.##}（悬停闪烁就是这么来的）");
            }
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

        Diagnostics.Write(
            $"selftest 标签条: 两枚标签、活动填充 {selectedFill?.R}、{selectedFill?.G}、{selectedFill?.B}（板底 {expectedFill.R}、{expectedFill.G}、{expectedFill.B}）、" +
            $"关闭键覆盖层 活动 Opacity {selectedClose?.Opacity:0.##} / 非活动 {inactiveClose?.Opacity:0.##}、宽度不动 {inactive.ActualWidth:0.##}、" +
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

        // 设置菜单：配对/恢复/筛选 三行 + 一条分隔 + 三枚图标。右键菜单里已经没有它们了，
        // 所以这条如果断了，那三件就真的没有入口——不是"少一个捷径"。
        var settings = BuildSettingsMenu();
        var settingsLabels = settings.Items.OfType<MenuFlyoutItem>().Select(row => row.Text).ToList();
        var settingsSeparators = settings.Items.OfType<MenuFlyoutSeparator>().Count();

        if (!settingsLabels.Contains("配对手机…") || !settingsLabels.Contains("恢复…") || !settingsLabels.Contains("筛选：日期与标签…"))
        {
            problems.Add($"设置菜单缺行：读到 {string.Join(" / ", settingsLabels)}");
        }

        if (settingsSeparators != 1)
        {
            problems.Add($"设置菜单的分隔读到 {settingsSeparators} 条，应 1 条（配对与板面两件之间）");
        }

        if (settings.Items.OfType<MenuFlyoutItem>().Any(row => row.Icon is null))
        {
            problems.Add("设置菜单有行没图标：浏览器图标式是他 10-23 选的");
        }

        Diagnostics.Write(
            $"selftest 标签菜单: tab [{string.Join(" / ", tabLabels)}]、strip [{string.Join(" / ", stripLabels)}]、" +
            $"settings [{string.Join(" / ", settingsLabels)}]（图标 {settings.Items.OfType<MenuFlyoutItem>().Count(row => row.Icon is not null)}、分隔 {settingsSeparators}）" +
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

        if (_openBoards.Count != 3 || BoardTabs.TabItems.Count != 3)
        {
            problems.Add($"直建以后标签条读到 {BoardTabs.TabItems.Count} 枚、列表 {_openBoards.Count} 块，应为『所有便签』＋2 块=3");
        }

        BeginRenameTab(createdPath);

        if (_renameBox is null)
        {
            problems.Add("行内改名没有换出输入框");

            return;
        }

        // 改名途中这一枚的宽度必须钉死（他 10-23 报"文字位置会浮动"：标签先跳一次宽、打字时每敲
        // 一下再按内容长一次）。三把尺同一个数才算钉住；输入框自己不许再带字面。
        var renaming = TabItemFor(createdPath);

        if (renaming is null)
        {
            problems.Add("改名途中那一枚标签找不回来了");
        }
        else
        {
            if (double.IsNaN(renaming.Width) || renaming.Width != renaming.MinWidth || renaming.Width != renaming.MaxWidth)
            {
                problems.Add($"改名途中宽度没钉住：Width={renaming.Width:0.#} Min={renaming.MinWidth:0.#} Max={renaming.MaxWidth:0.#}");
            }

            if (_renameBox.MinWidth > 0d)
            {
                problems.Add($"输入框自己还带字面 MinWidth={_renameBox.MinWidth:0.#}：它会按内容把标签撑大");
            }

            // 他 10-23 第二条"双击编辑时还是会往上动一下"：真机量过，写死 Height=26 时框里的字
            // 比它替掉的标题高 4.6px（模板把字长在 ScrollViewer 顶上，VerticalContentAlignment 管不到）。
            if (!double.IsNaN(_renameBox.Height) || _renameBox.MinHeight > 0d || _renameBox.VerticalAlignment != VerticalAlignment.Center)
            {
                problems.Add($"改名框的高度不是「一行字自己那么高、整枚居中」：Height={_renameBox.Height:0.#} MinHeight={_renameBox.MinHeight:0.#} 对齐={_renameBox.VerticalAlignment}");
            }

            if (EndRenameOnPress(renaming))
            {
                problems.Add("按在自己那一枚标签的空气上也算「点了别处」：字只有一行高，标签比它高");
            }
        }

        // "点到别处退出改名"的规则。接线那一发要他的眼睛（L4），这里锁的是判据本身：
        // 落点在框外＝收，落在框里（含框自己的模板零件）＝不收——字还没打完不许提交。
        if (!EndRenameOnPress(BoardArea))
        {
            problems.Add("按在板子上这一发不结束改名：『点了别处还在编辑』那条没修掉");
        }

        if (EndRenameOnPress(_renameBox))
        {
            problems.Add("按在改名框里也会结束改名：打字途中会被自己提交掉");
        }

        // 这一行是"这三条判据真的跑了"的凭据，顺便把钉住的数报出来（绿灯本身不说明它量过）。
        Diagnostics.Write(
            $"selftest 改名: 钉住 {renaming?.Width ?? double.NaN:0.#}px（名字 {MeasureTabName(BoardName(createdPath)):0.#}px）、" +
            $"框自己 MinWidth={_renameBox.MinWidth:0.#}、按板子收＝{EndRenameOnPress(BoardArea)}、按框里不收＝{!EndRenameOnPress(_renameBox)}");

        _renameBox.Text = targetName;
        TryCommitRename(fromBlur: false);

        var renamed = TabItemFor(targetPath);

        if (renamed is not null && !double.IsNaN(renamed.Width))
        {
            problems.Add($"改名结束以后这一枚还钉在 {renamed.Width:0.#}px：ReleaseRenameSlot 没跑");
        }

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

        if (BoardTabs.TabItems.Count != 3 || _openBoards.Count != 3)
        {
            problems.Add($"建出未命名板以后标签 {BoardTabs.TabItems.Count} 枚、列表 {_openBoards.Count} 块，应为『所有便签』＋2 块=3");
        }

        var item = TabItemFor(expectedPath);

        if (item is null)
        {
            problems.Add("未命名板那一枚标签没挂上");

            return;
        }

        if (item.Header is not Grid header || header.Children.OfType<TextBlock>().FirstOrDefault() is not { } headerText)
        {
            problems.Add("新标签的头不是我们的标题格（TextBlock + 关闭覆盖层）");
        }
        else if (headerText.Text != expectedName)
        {
            problems.Add($"新标签在建出来那一刻名字不是 {expectedName}：读到『{headerText.Text}』");
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

        if (BoardTabs.TabItems.Count != 2 || _openBoards.Count != 2
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
