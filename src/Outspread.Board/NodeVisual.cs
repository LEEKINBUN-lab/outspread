using System.IO;
using System.Text.Json.Nodes;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Outspread.Core;
using Windows.UI;

namespace Outspread.Board;

/// <summary>
/// One node, placed in screen coordinates, carrying the paper shadow instead of a border.
/// </summary>
/// <remarks>
/// <para>
/// 缩放＝同一张卡做变换（2026-10-27 他点的工单，替代 10-24 之前"每档重排"的旧账）。
/// 卡片的一切几何——字号、行高、内距、行数、牌子、渐隐——都在<b>世界单位</b>下排一次；
/// 缩放只改内容层那枚 <see cref="_paintZoom"/>。旧的"每拍按 view.Zoom 重算"路线删掉了：
/// 它让测量缓存的键里带着缩放，换一档就把视口内每张卡重量一遍（300 格板上一次滚轮 115–165ms），
/// 而且行高逐项乘缩放后的取整误差随档位变，同一格在 160% 掉一行、在 100% 又长回来——
/// 他两张截图里"第 3 行时有时无"就是这个。世界排版＋一层变换让行数天然与缩放无关。
/// 变换走 <see cref="FrameworkElement.RenderTransform"/>（在 <c>_paint</c> 那层，不是布局变换），
/// 所以它不吃 <c>UseLayoutRounding</c> 的账；命中区跟着变换走（名牌那枚负移就是同一条机制）。
/// </para>
///
/// The visible card is an inner <see cref="Border"/> rather than this control's own
/// Background, because <see cref="ContentControl"/>'s default template places its content
/// without painting Background or BorderBrush. Notes rendered as loose uncoloured text until
/// that was fixed — see the first screenshot of this board.
///
/// <para>Layer order inside this control, bottom first: the shadow bands, the card, the press
/// along the top edge.</para>
///
/// <para>The card has no border at this edge treatment (轮廓＝纸影): the three shadow layers are
/// what separates it from the board. The clip therefore moved onto the card itself, because a
/// clip on this control would cut off the band that hangs below the card — and a clipped-away
/// shadow is invisible in a screenshot but obvious on the desk.</para>
/// </remarks>
public sealed class NodeVisual : ContentControl
{
    private static readonly string[] ImageExtensions =
    [".png", ".jpg", ".jpeg", ".gif", ".webp", ".bmp", ".ico", ".tiff"];

    private static readonly int[] DecodeTiers = [256, 512, 1024, 2048];

    private readonly Canvas _bands = new() { IsHitTestVisible = false };

    private readonly Border _surface = new()
    {
        CornerRadius = new CornerRadius(0),
        BorderThickness = new Thickness(0),
    };

    private readonly Microsoft.UI.Xaml.Shapes.Rectangle _press = new() { IsHitTestVisible = false };

    /// <summary>
    /// The 2px edge bar an <c>@d…</c> date earns. 世界单位，随卡缩放（变换路线）：色条和纸面是
    /// 同一件东西的一部分，缩到总览时整张卡一起变小——"同一张卡每档一致"优先于"每档都 2 屏幕像素"，
    /// 后者会让色条相对卡身的粗细随档位变。
    /// </summary>
    private readonly Microsoft.UI.Xaml.Shapes.Rectangle _dueBar = new()
    {
        HorizontalAlignment = HorizontalAlignment.Left,
        VerticalAlignment = VerticalAlignment.Top,
        IsHitTestVisible = false,
    };

    /// <summary>自检读回：色条这一帧的高度与它的竖向对齐方式（对齐不是 Top 就会被 Grid 居中，上下各缺一角）。</summary>
    internal double DueBarHeight => _dueBar.Height;

    internal VerticalAlignment DueBarAlignment => _dueBar.VerticalAlignment;

    /// <summary>名牌在某个坐标空间里的矩形（没显示就是空的）。双击组名走的是这一块。</summary>
    internal Windows.Foundation.Rect NamePlateBoundsIn(UIElement relativeTo)
    {
        if (_namePlate.Visibility != Visibility.Visible || _namePlate.ActualWidth <= 0 || _namePlate.ActualHeight <= 0)
        {
            return default;
        }

        return _namePlate.TransformToVisual(relativeTo)
            .TransformBounds(new Windows.Foundation.Rect(0, 0, _namePlate.ActualWidth, _namePlate.ActualHeight));
    }

    /// <summary>
    /// A group is a container, not a card: it gets a dashed frame and its label, and no fill and
    /// no paper shadow. Painting it as a card put a 620×300 opaque square with a shadow under the
    /// whole first batch of notes — visible in the first screenshot of this port.
    /// </summary>
    private readonly Microsoft.UI.Xaml.Shapes.Rectangle _frame = new()
    {
        StrokeDashArray = [4, 3],
        StrokeThickness = 1,
        Fill = new SolidColorBrush(Colors.Transparent),
        IsHitTestVisible = false,
    };

    private readonly TextBlock _label;

    /// <summary>
    /// 组框的"将移入/将移出"警示环：组钮拖便签的那一拍，目标框亮蓝、被离开的框亮红
    /// （实线＋一圈柔光，150ms 淡入淡出）。反馈画在框上而不是便签上——分组这件事是框的。
    /// </summary>
    private readonly Microsoft.UI.Xaml.Shapes.Rectangle _groupHalo = new()
    {
        StrokeThickness = 7,
        IsHitTestVisible = false,
        Opacity = 0,
        Margin = new Thickness(-5),
        RadiusX = 4,
        RadiusY = 4,
    };

    private string? _groupAlert;

    /// <summary>正在播的那支警示环淡入淡出，以及它的落点（null＝没有动画在跑）。</summary>
    private Microsoft.UI.Xaml.Media.Animation.Storyboard? _haloFade;
    private double? _haloFadeTo;

    /// <summary>
    /// 组的名牌：画在框顶边<b>上方</b>，压着边线一角。画在框内就会和成员便签的字重合
    /// （他 10-22 报的第二条），而"点名字＝选中组里所有便签"需要一个货真价实的可点区。
    /// </summary>
    private readonly Border _namePlate = new()
    {
        HorizontalAlignment = HorizontalAlignment.Left,
        VerticalAlignment = VerticalAlignment.Top,
        // 不再用圆角矩形把名字包起来（10-22 他不要那层壳）：透明底只保留可点区，
        // 点名字＝选中组里所有便签这条不能丢，所以 Background 留 Transparent 而不是 null。
        CornerRadius = new CornerRadius(0),
        BorderThickness = new Thickness(0),
        Background = new SolidColorBrush(Colors.Transparent),
        Padding = new Thickness(2, 0, 2, 0),
    };

    private readonly TranslateTransform _nameLift = new();

    /// <summary>
    /// 组的"整组门"：一条盖住虚线的环带（框外 7 屏幕像素、框内 7），点它＝选中组里所有便签。
    /// 框内其余地方<b>不接鼠标</b>——单击必须落在里面的那枚便签上，这是这版语义的全部要点。
    /// </summary>
    private readonly Microsoft.UI.Xaml.Shapes.Path _grab = new()
    {
        Fill = new SolidColorBrush(Colors.Transparent),
        Visibility = Visibility.Collapsed,
    };

    private string _grabKey = string.Empty;

    /// <summary>内容超出时，卡片上/下边缘各压一道渐隐遮罩（B 方案：无字，只把最后一行淡进卡底）。</summary>
    private readonly Microsoft.UI.Xaml.Shapes.Rectangle _fadeAbove = OverflowFade(VerticalAlignment.Top, "OverflowFadeAbove");
    private readonly Microsoft.UI.Xaml.Shapes.Rectangle _fadeBelow = OverflowFade(VerticalAlignment.Bottom, "OverflowFadeBelow");

    /// <summary>
    /// The same body, painted as blocks, when it carries markdown: one element per line so a heading
    /// can be taller without the rest of the card inheriting its leading. Only one of
    /// <see cref="_label"/> and this is visible at a time — a note with no markup in it paints exactly
    /// the way it did before this existed.
    /// </summary>
    private readonly StackPanel _md = new() { Visibility = Visibility.Collapsed, Spacing = 0 };

    /// <summary>The card this block was built for. Rebuilding it every frame of a pan would re-take
    /// the layout a card has not changed.</summary>
    private string _mdKey = string.Empty;

    /// <summary>Markdown measure cache: the same lines at the same width/zoom keep their heights.</summary>
    private string _mdMeasureKey = string.Empty;
    private double _mdTotalHeight;
    private List<double> _mdHeights = [];

    /// <summary>两条裁线按对账换（每发 new 一枚几何体＝每发脏一次合成，白花钱）。</summary>
    private string _labelClipKey = string.Empty;
    private string _mdClipKey = string.Empty;

    /// <summary>Screen y of the last fully visible text line's bottom — where the bottom fade starts.
    /// The old fixed-height band fell inside a large paragraph gap and looked like no fade at all.</summary>
    private double _belowFadeTop;
    private double _belowFadeLeading;

    /* 整行窗口：格子被压得比内容矮时（只有人手动拖小、或整块板子切到"固定"才会这样），
       后面的行不许变成"看不见也够不着"。以前的做法是截到看得下的行数加一枚省略号——
       它诚实地说了"后面还有"，可没有任何办法把还有的那部分调出来。
       这里不加滚动宿主：那条闸（`CountBoardScrollers`）是定的——板子上不许出现滚动条，
       而一枚"看不见条"的宿主同样让滚轮在那一格上改姓。所以窗口按**行文字**（编辑器里
       回车切开的那种）上下走，画出来的顶和底永远是整行，"不许半行被横切"这条也就没破。 */
    private CanvasNode? _windowNode;

    private BoardViewState? _windowView;

    private double _windowScale;

    private string _windowKey = string.Empty;

    private int _firstLine;

    private int _visibleLines = 1;

    /// <summary>上一次的溢出布局键：只有它变了才主动重排，平移时不会每帧触发。</summary>
    private string _overflowKey = string.Empty;

    /// <summary>自检读的第三个数：实际允许画几行（有溢出时会比可见行多一行半行）。</summary>
    internal int RenderedMaxLines => _label.MaxLines;

    internal string? EntityTitle => _entity?.Title;

    internal string? EntityBadge => _entity?.Badge;

    internal string? EntityTip => _entity?.Tip;

    /// <summary>悬停那一排的两档透明度与牌子释义：静止时动作必须是藏着的。</summary>
    internal double EntityActionsOpacity => _entityHeader?.ActionsOpacity ?? -1;

    internal double EntityMetaOpacity => _entityHeader?.MetaOpacity ?? -1;

    internal string EntityBadgeTip => _entityHeader?.BadgeTip ?? string.Empty;

    /// <summary>自检直接按悬停那一拍：卡头高度在淡入前后必须一个像素都不动。</summary>
    internal void RevealEntityHeader(bool on) => _entityHeader?.Reveal(on);

    internal int EntityThumbCount => _entity?.Thumbnails.Count ?? 0;

    internal double EntityHeaderHeight => _headerHost.Height;

    internal bool IsTableCard => _entity?.Table is not null;

    internal int TablePlacements => _entity?.Table?.Placements().Count ?? 0;

    /// <summary>双击落在表格的哪一格上，连同那一格画出来的方框（板子握着指针捕获，卡片自己的手势收不到，
    /// 所以只能由窗口来问；问的就是画出来的那一份，看见的和改掉的不会差开）。</summary>
    internal (int Row, int Column, Windows.Foundation.Rect Bounds)? TableCellAt(Windows.Foundation.Point at, UIElement relativeTo) => _table.CellAt(at, relativeTo);

    /// <summary>自检读的那句占位提示（备注为空时才亮）。</summary>
    internal bool PaintsNoteHint => _noteHint.Visibility == Visibility.Visible;

    /// <summary>自检没亮时读这一句：占位提示的四个条件里，是哪一条把它按下去的。</summary>
    internal string NoteHintDebug
        => $"visible={_noteHint.Visibility}|ShowText={ShowText}|entity={_entity is not null}"
        + $"|body={_label.Text.Length}|md={_md.Visibility == Visibility.Visible}";

    internal string NoteBody => _entity?.Table is null ? string.Empty : _label.Text;

    internal string GroupName => Kind == CanvasNodeKind.Group ? _label.Text : string.Empty;

    internal double GroupNameMaxWidth => Kind == CanvasNodeKind.Group ? _label.MaxWidth : 0;


    private int _totalLines = 1;

    /* "还有没有藏起来的行"必须按**视觉行**算：一段长句子在窄格子里会绕成三四行，
       按回车切开的那种行数会算成"装得下"，于是滚轮交给了板子——整页被缩放，
       而他想看的是这格剩下的字（他 10-12 报的第二条）。 */
    private bool _hasMore;

    private TextBlock? _naturalProbe;

    private string _naturalKey = string.Empty;

    private int _naturalVisual = 1;

    /// <summary>
    /// The card's own marks, bottom-left: the date it carries and its tags, one chip each. Hovering a
    /// chip brings out the × and clicking it takes that span out of the text — which is the whole
    /// delete path, because the tokens themselves are hidden from the editor.
    /// </summary>
    /// <summary>
    /// 闸门插件（helm-gate）给这一格的状态牌，由窗口按 <c>&lt;板&gt;.gate.json</c> 投影设置；
    /// null＝这一格没被记账（缺文件的板完全没有这东西，跟没装插件一样）。只读，进牌子排
    /// 但不进编辑/删除。账本归插件，画板一个字节都不写它。
    /// </summary>
    internal NoteMarks.Mark? GateMark { get; set; }

    private readonly StackPanel _chips = new()
    {
        Orientation = Orientation.Vertical,
        Spacing = 4,
        Visibility = Visibility.Collapsed,
        VerticalAlignment = VerticalAlignment.Bottom,
    };

    /// <summary>牌子带现在会换行（他 11-03：标签多了被遮挡）：这是当前排出来的行数，以及它占的世界高度。</summary>
    private int _chipRowCount;

    private double _chipBandWorld;

    private double _chipWrapWidth;

    /// <summary>Set by the window: (node id, the exact span in the text) → remove it, undoably.</summary>
    internal Action<string, string>? RemoveMark;

    /// <summary>点牌身＝聚焦这一类标记（投影镜头，他 11-03 拍的 A）；带按下处的窗口坐标与这一发的时间戳。</summary>
    internal Action<string, NoteMarks.Mark, Windows.Foundation.Point, ulong>? ChipPressed;

    /// <summary>双击牌身＝编辑便签。</summary>
    internal Action<string, NoteMarks.Mark>? ChipDoublePressed;

    /// <summary>镜头里没被聚的那几格淡到多少（他 11-03 改口：背景的字要几乎看不出来）。</summary>
    internal const double LensDimmedOpacity = 0.05;

    /// <summary>Set by the window: the card header's open button asks the host where this entity lives.</summary>
    internal Action<string>? OpenEntity;

    /// <summary>Set by the window: the folder button asks the host to reveal the original in Explorer.</summary>
    internal Action<string>? RevealEntity;

    /// <summary>那格还没有备注时，在可编辑的位置亮一句淡色提示；一开始打字就没了。</summary>
    private readonly TextBlock _noteHint = new()
    {
        Text = "双击键入备注",
        TextWrapping = TextWrapping.NoWrap,
        TextTrimming = TextTrimming.CharacterEllipsis,
        MaxLines = 1,
        VerticalAlignment = VerticalAlignment.Top,
        HorizontalAlignment = HorizontalAlignment.Left,
        IsHitTestVisible = false,
        Visibility = Visibility.Collapsed,
    };

    /// <summary>The chips are rebuilt only when their content changed: re-making them every frame of a
    /// pan would drop the hover state the × depends on.</summary>
    private string _chipKey = string.Empty;

    /// <summary>Body on top, marks under it, in one row each — so the two cannot overlap and the band
    /// reserved for the marks is the band they actually take.</summary>
    private readonly Grid _textHost = new();

    private readonly Grid _content = new();


    private readonly Grid _headerHost = new() { Visibility = Visibility.Collapsed };

    private readonly TableCardView _table = new() { Visibility = Visibility.Collapsed };

    private readonly EntityHeader? _entityHeader;

    private readonly EntityCardInfo? _entity;

    private readonly Image _image;

    private string? _shadowKey;

    private Color _paperKey;
    private LinearGradientBrush? _paper;

    /// <summary>
    /// The card's fill with its own light baked in: white at the top edge, the chosen colour
    /// untouched at 42%, black pressed along the bottom. WinUI has no "gradient over a colour",
    /// so the three stops are pre-composited — which also keeps the text contrast testable in
    /// Core against the colour the card is actually painted with at the line's height.
    /// </summary>
    /// <remarks>
    /// The stops come from <see cref="NoteSurface.PaperAt"/>, the one place that says what colour
    /// the card is at a given height. The overflow fade reads it from the same place: when it was
    /// painted with the flat fill colour instead, the strip it covers sat a few levels off the
    /// paper around it and read as a second sheet laid on the first (2026-10-26, "放大看颜色会分层").
    /// </remarks>
    private LinearGradientBrush PaperFor(Color surface)
    {
        if (_paper is not null && _paperKey.R == surface.R && _paperKey.G == surface.G && _paperKey.B == surface.B)
        {
            return _paper;
        }

        _paperKey = surface;

        var back = new Rgb(surface.R, surface.G, surface.B);

        _paper = new LinearGradientBrush { StartPoint = new Windows.Foundation.Point(0, 0), EndPoint = new Windows.Foundation.Point(0, 1) };
        _paper.GradientStops.Add(new GradientStop { Color = NoteStyling.ToColor(NoteSurface.PaperAt(back, 0, NoteStyling.IsDark)), Offset = 0 });
        _paper.GradientStops.Add(new GradientStop { Color = surface, Offset = NoteSurface.PaperMiddleOffset });
        _paper.GradientStops.Add(new GradientStop { Color = NoteStyling.ToColor(NoteSurface.PaperAt(back, 1, NoteStyling.IsDark)), Offset = 1 });

        return _paper;
    }

    private int _decodeTier;

    private string? _loadedPath;

    /// <summary>那格"图没了/读不出"的字。不复用 <see cref="_label"/>：它挂在 <c>_textHost</c> 里，
    /// 而 WinUI 不许把还有父亲的元素再挂一次——10-23 拿一块丢了图的板子开屏，就是这一句把窗口
    /// 直接打死（<c>Border.set_Child</c> 抛 ArgumentException）。</summary>
    private TextBlock? _missing;

    internal NodeVisual(CanvasNode node, Func<string, string?> resolveAsset, EntityCardInfo? entity = null)
    {
        NodeId = node.Id;
        Kind = node.Kind;
        _entity = entity;

        // 表格卡不吃卡头：它要的是一张表，不是一份关于这张表的说明。
        _entityHeader = entity is null || entity.Table is not null
            ? null
            : new EntityHeader(entity, () => OpenEntity?.Invoke(NodeId), () => RevealEntity?.Invoke(NodeId));

        UseLayoutRounding = false;
        IsHitTestVisible = true;

        // 折叠与缩放分家：根上这一枚只管平移（屏幕像素），缩放住在内容层 _paintZoom（变换路线）。
        // 两层各走各的，撤销一次筛选就是把平移归零，不会碰缩放。
        RenderTransform = _fold;

        HorizontalContentAlignment = HorizontalAlignment.Stretch;
        VerticalContentAlignment = VerticalAlignment.Stretch;

        _label = new TextBlock
        {
            TextWrapping = TextWrapping.Wrap,
            FontFamily = NoteStyling.NoteFont,
            VerticalAlignment = VerticalAlignment.Top,
        };

        _textHost.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        _textHost.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        Grid.SetRow(_md, 0);
        Grid.SetRow(_chips, 1);
        _textHost.Children.Add(_chips);
        if (Kind == CanvasNodeKind.Group)
        {
            // 组的整片区域不吃命中：单击要落在里面的那枚便签上。名字戴上顶边上方的名牌，
            // 环带接"点边＝选整组"——这两样才是组的可点部。
            _surface.IsHitTestVisible = false;
            _label.TextWrapping = TextWrapping.NoWrap;
            _label.TextTrimming = TextTrimming.CharacterEllipsis;
            _namePlate.Child = _label;
            _namePlate.RenderTransform = _nameLift;
            _namePlate.Visibility = Visibility.Collapsed;
        }
        else
        {
            Grid.SetRow(_label, 0);
            Grid.SetRow(_noteHint, 0);
            _textHost.Children.Add(_label);
            _textHost.Children.Add(_noteHint);
        }

        _image = new Image
        {
            Stretch = Stretch.Uniform,
            IsHitTestVisible = false,
        };

        if (_entityHeader is not null)
        {
            _headerHost.Children.Add(_entityHeader);
            _headerHost.Visibility = Visibility.Visible;
        }

        // 卡头（实体卡）在上、表格（表格卡）在中、正文在下——三者都是"这一格上面的东西"，
        // 所以放在同一枚 Grid 的三行里，而不是再套一层宿主。
        _content.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        _content.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        _content.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        Grid.SetRow(_headerHost, 0);
        Grid.SetRow(_table, 1);
        Grid.SetRow(_textHost, 2);
        _content.Children.Add(_textHost);

        ShowText = Kind != CanvasNodeKind.File || !LooksLikeImage(node.FilePath);
        if (entity?.Table is not null && !_content.Children.Contains(_table))
        {
            _content.Children.Add(_table);
        }

        _table.Visibility = entity?.Table is null ? Visibility.Collapsed : Visibility.Visible;

        _surface.Child = ShowText ? _content : _image;

        // 缩放走这一层变换：卡内一切几何按世界单位排版，_paintZoom 把整层投到屏幕。
        // 布局盒（根控件的 Width/Height）也是世界尺寸，Canvas 落位仍是屏幕坐标——
        // 命中与绘制都跟着变换走（名牌的负移抬升就是同一条机制），所以"看到的"和"点到的"不散架。
        _paint.RenderTransform = _paintZoom;
        _paint.Children.Add(_bands);
        _paint.Children.Add(_surface);
        _paint.Children.Add(_press);

        // `_frame` / `_grab` / `_namePlate` 不再在这里挂：普通卡上它们是**空壳**（名牌的 Child
        // 恒为 null、另两枚恒 Collapsed），挂在树里只让每一帧多走、多合成几个元素。
        // 它们改由 AttachChrome 在第一次真要用时按 ChromeOrderOf 的层序插进去（只有组卡会用）；
        // 层序不变，所以画面不动一个像素。10-28 阶段 1（见 plans/inland-jungle-gull.md）。
        // Once, here. It used to hang off Update behind a `!_dueBar.IsLoaded` guard, which is not a
        // membership test: a card updated twice before the tree has loaded — which is what a rebuild
        // inside one synchronous pass does — took IsLoaded as false and added a rectangle that was
        // already a child, and XAML answers that with 0x800F1000. Visibility is what Update changes.
        Content = _paint;

        // 第二行（元信息＋动作）由悬停决定亮不亮。选中不跟着亮：选中那一拍右侧会浮出操作条，
        // 两样叠在同一列上；键盘与触屏走的是右键菜单里那两条"打开原件 / 在文件夹中打开"。
        PointerEntered += (_, _) => _entityHeader?.Reveal(true);
        PointerExited += (_, _) => _entityHeader?.Reveal(false);

        // 滚轮先问这一格要不要窗口：藏着的行归它，走到头了再让板子接手去缩放。
        PointerWheelChanged += (_, args) =>
        {
            var page = Math.Max(1, _visibleLines);
            var step = args.KeyModifiers.HasFlag(Windows.System.VirtualKeyModifiers.Shift) ? page : 1;

            // 这一枚事件带的是指针包，滚轮的格数在它的属性里。
            var wheel = args.GetCurrentPoint(null).Properties.MouseWheelDelta;

            if (ScrollByLines(wheel > 0 ? -step : step))
            {
                args.Handled = true;
            }
        };

        ResolveAsset = resolveAsset;

        if (!ShowText)
        {
            LoadImage(node);
        }
    }


    /// <summary>这份文字在这宽度上要占几行视觉行；量不了（还没进过树）返回 0，调用方按源行数兜底。</summary>
    private int NaturalVisualLines(string text, double width, double size, double leading)
    {
        if (XamlRoot is null)
        {
            return 0;
        }

        var key = $"{text.Length}|{text.GetHashCode()}|{width:0.#}|{size:0.###}|{leading:0.###}";

        if (key == _naturalKey)
        {
            return _naturalVisual;
        }

        _naturalKey = key;

        _naturalProbe ??= new TextBlock
        {
            TextWrapping = TextWrapping.Wrap,
            IsHitTestVisible = false,
            MaxLines = int.MaxValue,
            TextTrimming = TextTrimming.None,
            FontFamily = NoteStyling.NoteFont,
            XamlRoot = XamlRoot,
        };

        _naturalProbe.Text = text;
        _naturalProbe.FontSize = size;
        _naturalProbe.LineHeight = leading;
        _naturalProbe.Measure(new Windows.Foundation.Size(width, double.PositiveInfinity));

        // 四舍五入到整行，不是向上取整：行盒本身会多出一两个像素（这台的 LineHeight 是
        // 整像素 half-up），向上取整会把"刚好 12 行"读成 13 行，于是每一格都自称还有藏行，
        // 滚轮在放得下的格子上也不还给板子了。
        _naturalVisual = Math.Max(1, (int)Math.Floor(
            (_naturalProbe.DesiredSize.Height + (leading * 0.5)) / Math.Max(1, leading)));

        return _naturalVisual;
    }

    /// <summary>这格现在是不是还有没露出来的行。</summary>
    private bool HiddenBelow(string text, double width, double size, double leading)
        => NaturalVisualLines(text, width, size, leading) > _visibleLines;

    /// <summary>滚轮在那一格上＝窗口上下走。回到顶或底就交回板子去缩放（返回 false）。</summary>
    /// <param name="lines">正数往下、负数往上；一格＝一行文字。</param>
    internal bool ScrollByLines(int lines)
    {
        var max = _hasMore ? Math.Max(0, _totalLines - 1) : 0;

        if (_windowNode is null || _windowView is null || max <= 0)
        {
            return false;
        }

        var next = Math.Clamp(_firstLine + lines, 0, max);

        if (next == _firstLine)
        {
            return false;
        }

        _firstLine = next;

        Update(_windowNode, _windowView, _windowScale);

        return true;
    }

    /// <summary>这一格走的是块（markdown）表示还是单行表示——由内容定，与"这一拍画没画字"无关。</summary>
    private bool _paintsMarkdown;

    /// <summary>自检那一发读的三件事：这格一共几行、视窗装得下几行、现在从第几行开始。</summary>
    internal (int Total, int Visible, int First, bool HasMore, bool Blocks) WindowState()
        => (_totalLines, _visibleLines, _firstLine, _hasMore, _paintsMarkdown);

    /// <summary>
    /// 自检那一发读的组的门形：框内还吃不吃鼠标、环带与名牌开没开、名牌抬到了顶边上方几像素。
    /// </summary>
    internal (bool SurfaceHittable, Visibility Grab, Visibility Plate, double PlateLift, string? Alert) GroupChrome()
        => (_surface.IsHitTestVisible, _grab.Visibility, _namePlate.Visibility, _nameLift.Y, _groupAlert);

    /// <summary>自检读的框线：警示态把它改成实线＋加粗，收手以后必须回到虚线与 1px。</summary>
    internal (bool Dashed, double Thickness) GroupFrameLine()
        => (_frame.StrokeDashArray is { Count: > 0 }, _frame.StrokeThickness);

    /// <summary>
    /// 自检读的警示环：基值、以及"此刻还在往哪儿淡"（null＝没有动画在跑）。
    /// 11-02 那个"另一个框一直亮"的根子就在这第二个数：清警示态时若只写基值、不停掉正在
    /// 播的淡入，HoldEnd 会把它自己拉到 1 并停在那儿——状态已是 null，画面却是亮的。
    /// </summary>
    internal (double Halo, double? FadeTo) GroupHaloState() => (_groupHalo.Opacity, _haloFadeTo);

    /// <summary>组钮拖便签那一拍的反馈：in＝将进这一框（蓝）、out＝将离开这一框（红）、null＝回到虚线。</summary>
    internal void SetGroupAlert(string? kind)
    {
        if (string.Equals(_groupAlert, kind, StringComparison.Ordinal))
        {
            return;
        }

        _groupAlert = kind;
        ApplyGroupAlert(fade: true);
    }

    private void ApplyGroupAlert(bool fade = false)
    {
        if (_groupAlert is null)
        {
            _frame.Stroke = new SolidColorBrush(NoteStyling.FallbackLine());
            _frame.StrokeDashArray = [4, 3];
            _frame.StrokeThickness = 1;

            if (fade)
            {
                // 无条件换一支往 0 的动画：淡入才刚起（读到的透明度还是 0）时若只看读数就走
                // "只写基值"那一支，那支 HoldEnd 的淡入会继续把环拉到 1 并停在那儿——状态已是
                // null、画面却一直亮着（11-02 他截的"另一个分组的入组状态边框还在"）。
                FadeHalo(0);
            }
            else
            {
                StopHaloFade();
                _groupHalo.Opacity = 0;
            }

            return;
        }

        AttachChrome(_groupHalo);

        var brush = _groupAlert == "in" ? ChromeStyling.Ring : ChromeStyling.Snap;
        var colour = ((Microsoft.UI.Xaml.Media.SolidColorBrush)brush).Color;

        _frame.Stroke = brush;
        _frame.StrokeDashArray = null;
        _frame.StrokeThickness = 1.5;
        _groupHalo.Stroke = new SolidColorBrush(Windows.UI.Color.FromArgb(56, colour.R, colour.G, colour.B));

        if (fade)
        {
            FadeHalo(1);
        }
        else
        {
            // 同上反过来：淡出还在播时直接写基值 1，HoldEnd 的淡出会赢，环就"亮着却看不见"。
            StopHaloFade();
            _groupHalo.Opacity = 1;
        }
    }

    private void FadeHalo(double to)
    {
        // 先把屏上那个值取下来：Stop 会把属性交回基值，淡出若从基值起步就看不见过程。
        var from = _groupHalo.Opacity;

        StopHaloFade();

        var animation = new Microsoft.UI.Xaml.Media.Animation.DoubleAnimation
        {
            From = from,
            To = to,
            Duration = TimeSpan.FromMilliseconds(Motion.BaseMs),
        };

        var board = new Microsoft.UI.Xaml.Media.Animation.Storyboard { FillBehavior = Microsoft.UI.Xaml.Media.Animation.FillBehavior.HoldEnd };

        Microsoft.UI.Xaml.Media.Animation.Storyboard.SetTarget(animation, _groupHalo);
        Microsoft.UI.Xaml.Media.Animation.Storyboard.SetTargetProperty(animation, "Opacity");
        board.Children.Add(animation);

        // HoldEnd 会一直按这个目标值出图，所以这支动画的引用要留着——下一次改方向前先 Stop 掉它，
        // 光写基值是压不过一条还在播的动画的。
        _haloFade = board;
        _haloFadeTo = to;
        board.Begin();
    }

    /// <summary>把正在播的那支警示环动画摘掉（属性回到基值）。</summary>
    private void StopHaloFade()
    {
        _haloFade?.Stop();
        _haloFade = null;
        _haloFadeTo = null;
    }

    /// <summary>
    /// 查找跳过来时的那一手：阻尼抖动（曲线与幅度在 <see cref="Motion.ArrivalShake"/> 里只写一次）。
    /// 走 Canvas.Left 的布局动画而不是 RenderTransform——卡片那一枚变换是折叠用的，
    /// 借它抖动会和"折进组里那一格"打架；这一手只有 240ms，落定后把位置写回起手那一格。
    /// </summary>
    /// <summary>自检读的那一格：这一手是否还在抖（同步点完就在抖，240ms 后落回 false）。</summary>
    internal bool IsShaking { get; private set; }

    internal void ShakeArrival()
    {
        var baseX = Canvas.GetLeft(this);

        if (double.IsNaN(baseX))
        {
            baseX = 0;
        }

        var shake = new Microsoft.UI.Xaml.Media.Animation.DoubleAnimationUsingKeyFrames
        {
            Duration = TimeSpan.FromMilliseconds(Motion.ArrivalShakeMs),
            EnableDependentAnimation = true,
        };

        foreach (var (atMs, px) in Motion.ArrivalShake)
        {
            shake.KeyFrames.Add(new Microsoft.UI.Xaml.Media.Animation.LinearDoubleKeyFrame
            {
                KeyTime = Microsoft.UI.Xaml.Media.Animation.KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(atMs)),
                Value = baseX + px,
            });
        }

        Microsoft.UI.Xaml.Media.Animation.Storyboard.SetTarget(shake, this);
        Microsoft.UI.Xaml.Media.Animation.Storyboard.SetTargetProperty(shake, "(Canvas.Left)");

        var board = new Microsoft.UI.Xaml.Media.Animation.Storyboard
        {
            FillBehavior = Microsoft.UI.Xaml.Media.Animation.FillBehavior.Stop,
        };

        board.Children.Add(shake);
        board.Completed += (_, _) =>
        {
            IsShaking = false;
            Canvas.SetLeft(this, baseX);
        };

        IsShaking = true;
        board.Begin();
    }

    public string NodeId { get; }

    public CanvasNodeKind Kind { get; }

    public bool ShowText { get; }

    /// <summary>True when the node is a file that could not be found or decoded.</summary>
    public bool IsBroken { get; private set; }

    /// <summary>那格"图没了/读不出"的字；图在的时候是 null。界面自检量这一格用它。</summary>
    public string? MissingText => _missing?.Text;

    internal Func<string, string?> ResolveAsset { get; }

    public bool Matches(CanvasNode node)
        => string.Equals(NodeId, node.Id, StringComparison.Ordinal) && Kind == node.Kind
        && LooksLikeImage(node.FilePath) == !ShowText;

    /// <summary>
    /// For the interface self-test: the card must not still be painting the property line the board
    /// took away, and taking it away without painting the readout in its place is hiding a mark, not
    /// showing one.
    /// </summary>
    internal bool PaintsTokenMarkup => _label.Text.Contains("@due(", StringComparison.Ordinal)
        || _label.Text.Contains("@tag(", StringComparison.Ordinal);

    internal bool PaintsReadout => _chips.Visibility == Visibility.Visible && _chips.Children.Count > 0;

    /// <summary>Self-test reading: the font size the painted chips were built with. It has to follow
    /// the camera — a chip repainted only when its text changes keeps the size of whichever zoom it
    /// was born at, and the marks then disagree with the正文 on the same card.</summary>
    internal double PaintedChipFontSize => ChipRows()
        .Select(row => row.Children.OfType<TextBlock>().FirstOrDefault())
        .FirstOrDefault(label => label is not null)?.FontSize ?? 0;

    /// <summary>自检那把尺：牌子树里 × 住在哪一格，返回（横排里, 牌面层上）两个计数。
    /// 前者必须 0——它一住进横排，每枚牌子都比那几个字宽一截空档（他 10-26 的第二条）；
    /// 后者必须等于牌子数——× 还得真在树上，点得到才删得掉。两个数一起读，
    /// 免得"树里找不到 ×"被当成通过。</summary>
    internal (int InFlow, int Overlay) PaintedChipCrossPlacement
    {
        get
        {
            var inFlow = 0;
            var overlay = 0;

            foreach (var chip in Chips())
            {
                if (chip.Child is not Grid face)
                {
                    continue;
                }

                inFlow += face.Children.OfType<StackPanel>()
                    .SelectMany(row => row.Children)
                    .OfType<TextBlock>()
                    .Count(text => text.Text == "×");
                // 11-02 起 × 是一枚圆钮（Grid 那一层里的 Border），不再是一块贴边的 Border：
                // 从牌面层的非横排子层里找，横排那一支一个都不许有。钮里装的可能是文字「×」，
                // 也可能（11-03 起）是官方 Dismiss 路径——数的是钮本身，不是它装的东西。
                overlay += face.Children.OfType<Grid>()
                    .SelectMany(layer => layer.Children)
                    .OfType<Border>()
                    .Count();
            }

            return (inFlow, overlay);
        }
    }

    internal int PaintedChipCount => Chips().Count();

    /// <summary>自检那把尺：把这一格所有牌子的 × 圆钮"悬停一次"长出来，返回长了几枚。
    /// 11-03 起那枚钮是悬停才建的（静止时不占元素），所以量它之前必须先催一次；
    /// 数返回值是为了钉"每枚牌子都长得出来"，不是数到一个 0 就当通过。</summary>
    internal int RevealChipCrosses()
    {
        var revealed = 0;

        foreach (var chip in Chips())
        {
            if (chip.Tag is Action reveal)
            {
                reveal();
                revealed++;
            }
        }

        return revealed;
    }

    /// <summary>自检那把尺之一：牌子宽 − 字宽 ≈ 左右内距。多出一格就是 × 还在占位。
    /// 取字最长的那枚：短牌的宽度由 MinWidth 兜底，量不出这条关系。</summary>
    internal (double ChipWidth, double LabelWidth) PaintedChipWidths
    {
        get
        {
            (double Chip, double Label) best = (0, 0);

            foreach (var chip in Chips())
            {
                var label = ChipRow(chip)?.Children.OfType<TextBlock>().FirstOrDefault();

                if (label is { ActualWidth: > 0 } && label.ActualWidth > best.Label)
                {
                    best = (chip.ActualWidth, label.ActualWidth);
                }
            }

            return best;
        }
    }

    private IEnumerable<StackPanel> ChipRows() => _chips.Children.OfType<StackPanel>();

    /// <summary>所有牌子（跨行）。</summary>
    private IEnumerable<Border> Chips() => ChipRows().SelectMany(row => row.Children).OfType<Border>();

    /// <summary>自检读回：牌子带排成了几行（换行那一刀量的就是这个）。</summary>
    internal int PaintedChipRowCount => _chipRowCount;

    /// <summary>自检读回：牌子带占的世界高度。</summary>
    internal double PaintedChipBandWorld => _chipBandWorld;

    /// <summary>自检读回：换行时用的内宽（世界单位）与每行排完后的实际宽。</summary>
    internal double PaintedChipWrapWidth => _chipWrapWidth;

    internal IReadOnlyList<double> PaintedChipRowWidths => ChipRows().Select(row => row.ActualWidth).ToList();

    private static StackPanel? ChipRow(Border chip)
        => chip.Child switch
        {
            StackPanel row => row,
            Grid face => face.Children.OfType<StackPanel>().FirstOrDefault(),
            _ => null,
        };

    /// <summary>For the interface self-test: the block path is what the card is drawing, and how many
    /// lines it drew. A note that measures one shape and paints another only shows up when both are
    /// read from the same realized card.</summary>
    internal bool PaintsMarkdown => _md.Visibility == Visibility.Visible;

    internal int PaintedMarkdownLines => _md.Children.Count;

    /// <summary>The words every painted block actually holds, in order, one per line. A block can
    /// exist as an element and still carry nothing — the child count looks identical from here, and
    /// the card is identical on the board until it is empty.</summary>
    internal string PaintedMarkdownWords => string.Join('\n', _md.Children.Select(child => child switch
    {
        TextBlock label => label.Text,
        Border bar when bar.Child is TextBlock quoted => quoted.Text,
        _ => string.Empty,
    }));

    /// <summary>
    /// 筛选这一发额外画在画面上的三样东西：折进锚点的位移（屏幕像素）、组框该画在哪、框上的后缀。
    /// </summary>
    /// <remarks>
    /// 全是渲染层的事，一个字节都不进文档：筛选把三格并到一处的样子要是被写回文件，
    /// 一次筛选就把便签的"真位置"同步给了手机。<see cref="FoldDX"/> 为 null 表示这一格正在播
    /// 展开／折回的动画，位移交给 Storyboard，布局代码不许插手。
    /// </remarks>
    public sealed record Display(
        double? FoldDX,
        double? FoldDY,
        bool FoldedAway,
        Grouping.Rect? ShownFrame,
        string? Suffix,
        bool Revealed = false,
        bool LensFocus = false,
        bool LensDimmed = false)
    {
        public static readonly Display None = new(null, null, false, null, null);
    }

    /// <summary>折叠的位移走这一枚变换：它不改 Canvas 上的位置，所以撤销一次筛选就是把它归零。
    /// 它挂在卡片根上、<b>屏幕像素</b>计；缩放那一枚在 <see cref="_paintZoom"/>（内容层），两者互不搭。</summary>
    private readonly TranslateTransform _fold = new();

    /// <summary>内容层的那一枚缩放：卡内一切几何都是世界单位，这一枚把世界投到屏幕上。
    /// 换档只改这两个数——合成器的事，不是重新排版的事。</summary>
    private readonly ScaleTransform _paintZoom = new();

    /// <summary>"阴影 ↔ 一条底线"这一级 LOD 的当前状态（null＝这一格还没判过）。</summary>
    private bool? _shadowLod;

    /// <summary>
    /// 这一格该不该画阴影。判断本身在 <see cref="NoteSurface.ShadowLod"/>（带迟滞，可单测），
    /// 这里只存"上一拍是什么"——那一拍不能没有：没有它，压在阈值上的板子来回滚轮每帧都要
    /// 把全部可见格的阴影层重铺一遍（实测 104ms/帧 vs 8.3ms）。
    /// </summary>
    private bool ShadowLod(double zoom)
    {
        var next = NoteSurface.ShadowLod(zoom, _shadowLod ?? NoteSurface.DrawsShadow(zoom));

        _shadowLod = next;

        return next;
    }

    /// <summary>这一档该不该画正文（屏幕字高并进一像素以下才不画，见 <see cref="NoteSurface.TextLod"/>）。</summary>
    private bool? _textLod;

    /// <summary>
    /// 换档那两位上一次画成什么样（11-03 K1）：翻档只切正文可见性与阴影画法这两笔，
    /// 不进整格内容账——进了就是"一跨档，全部可见格整套重跑"（300 格实测 543.7ms）。
    /// </summary>
    private bool? _lodText;

    private bool? _lodShadow;

    /// <summary>这一格走没走过"正文那一排"：组与图卡不归这条轻账管，别去动它们的可见性。</summary>
    private bool _textLodOwned;

    /// <summary>这一格自己整跑过几回内容（跨档该是 0 次；自检拿它当靶，全局那两枚计数器会被新露出来的格污染）。</summary>
    internal int FullRuns;

    /// <summary>这一格此刻画的是阴影还是那条底线（自检的正对照）。</summary>
    internal bool PaintsShadow => _lodShadow ?? NoteSurface.DrawsShadow(1);

    private bool TextLod(double zoom, double scale)
    {
        var next = NoteSurface.TextLod(zoom, scale, _textLod ?? NoteSurface.DrawsText(zoom, scale));

        _textLod = next;

        return next;
    }

    private readonly Grid _paint = new();

    /// <summary>卡面裁剪矩形随尺寸重建一次就够（剔除/改大小那一拍才变）。</summary>
    private string _clipKey = string.Empty;

    /// <summary>内容账（视图那一侧）：卡片画什么还取决于这几项——<b>没有裸的 view.Zoom</b>，
    /// 带裸缩放就是把"每档重量每张卡"请回来；图卡的解码档仍折算成阈值位（DecodeTier）留在账里。
    /// 阴影与正文那两位 11-03 起挪出去了：换档只值两笔可见性，走 <c>ApplyLod</c> 那条轻账。
    /// 文档那一侧不在这本账里，改由下面的输入签名判（每拍读九个数太贵）。</summary>
    private (
        double FontSize,
        bool Dark,
        int Today,
        string? TagFilter,
        int? DueFilter,
        bool? DoneFilter,
        bool Filtering,
        bool Revealed,
        bool FoldedAway,
        bool LensFocus,
        bool LensDimmed,
        string? Suffix,
        Grouping.Rect? Shown,
        double? FoldDX,
        double? FoldDY,

        // 换档那两位（阴影／正文）不在这一串里：它们只值两笔可见性，走 ApplyLod 那条轻账。
        // 一进这本账，跨过阈值的那一发就是全部可见格整套重跑（300 格实测 543.7ms，11-03 K1）。
        int DecodeTier,
        bool NoRoot,
        int FirstLine,

        // 卡片自己的框与正文：模型可能先改在 C# 属性上、Json 那枚引用还没换，
        // 只靠输入签名会把"这一拍什么都没变"报错——牌子、渐隐、块渲染就不重画（10-27 的 14 条红）。
        double CardWidth,
        double CardHeight,
        string? Text,
        string? Color,
        string? Label,

        // 闸门牌（helm-gate）：账本是插件在画的另一路文件，不进这笔账的话
        // ApplyGateChange 的重画会被内容账早退吞掉（实测牌停在旧字）。
        string? GateSource,
        string? GateLabel) _viewStamp;

    private bool _viewStampValid;

    // ── 这一格的输入签名 ─────────────────────────────────────────────────────
    // 文档里的值节点（JsonValue）是不可变的：CanvasNode 的 setter 改一个数/一个字，都会往
    // JsonObject 里**换一个新节点**。所以"值节点还是同一个对象"就等于"值没变"——不必把值读出来。
    // 每读一个数要走 JsonObject→JsonValue→double 一次转换，330 格板上那就是逐格循环的大头
    // （--perf 拆出来的：一次数的读约 1.3µs，一次节点身份的比只要零点几微秒）。
    private JsonObject? _sigJson;
    private JsonNode? _sigX;
    private JsonNode? _sigY;
    private JsonNode? _sigW;
    private JsonNode? _sigH;
    private JsonNode? _sigText;
    private JsonNode? _sigUrl;
    private JsonNode? _sigLabel;
    private JsonNode? _sigFile;
    private JsonNode? _sigColor;
    private bool _sigValid;

    // 值签名：模型上的几何/正文可能先改在 C# 属性上、Json 里那枚引用还没换（自检就是这么改的）。
    // 只比引用会把这些改动当成"这一拍什么都没变"，牌子、渐隐、块渲染就都不重画——10-27 那 14 条红就是它。
    private double _sigValueX, _sigValueY, _sigValueW, _sigValueH;
    private string? _sigValueText, _sigValueColor, _sigValueLabel;
    private bool _sigValueValid;
    private bool _worldBoxValid;
    private Grouping.Rect _worldBox;

    /// <summary>
    /// 这一格<b>自己</b>在世界里的盒（上一拍从文档读来缓存的那一份），不含组框的可见包围盒。
    /// 对齐基准要的就是这个——<c>SnapDrag</c> 原来逐格读 <c>node.X/Y/Width/Height</c> 四个数，
    /// 现在改读这里，一个数都不必再回文档取。
    /// </summary>
    internal Grouping.Rect OwnWorldBox => _worldBox;

    /// <summary>循环刚替这一格判过输入签名（<see cref="WorldBox"/>），Update 直接接着用，不再比第二遍。</summary>
    private bool _tickInputsSame;

    private bool _tickInputsChanged;

    // ── 上一拍写下去的那几笔 ────────────────────────────────────────────────
    // 一个依赖属性写一次要一两微秒（Width/Height 还要让布局失效），而绝大多数拍子里它们
    // 一个都没变。这里记着上一拍真写下去的值，只写"与上一拍不同"的那几笔。
    private double _placedX;
    private double _placedY;
    private double _placedW;
    private double _placedH;
    private double _placedZoom;
    private bool _placedValid;
    private double _foldPlacedX;
    private double _foldPlacedY;
    private bool _foldPlacedValid;
    private bool _hitTestPlaced;
    private bool _hitTestValid;
    private bool _collapsed;

    /// <summary>诊断读数：内容账命中/整跑的次数（--perf 与 arrange 日志用，测完不删——它是下一刀的秒表）。</summary>
    internal static long ContentGuardHits;
    internal static long ContentFullRuns;

    /// <summary>自检读回：内容层当前的缩放倍数（应等于相机 zoom）。</summary>
    internal double PaintZoomScale => _paintZoom.ScaleX;

    /// <summary>折叠位移的读写口（屏幕像素）：GroupFold 的 Storyboard Target 走这一枚，不认变换组。</summary>
    internal TranslateTransform FoldShift => _fold;

    /// <summary>自检读回：位移的落点（屏幕像素）；null＝这一格没被搬过。</summary>
    internal (double X, double Y)? FoldTarget => _foldPlacedValid ? (_foldPlacedX, _foldPlacedY) : null;

    /// <summary>
    /// 把不透明度从 <paramref name="from"/> 播到 <paramref name="to"/>（基值先写成落点）。
    /// 聚焦换档时用：上一档一边飞回家一边退成灰、新的一档一边飞出来一边亮起来——两边同一拍起步。
    /// </summary>
    internal void FadeTo(double from, double to)
    {
        Opacity = to;

        var fade = new Microsoft.UI.Xaml.Media.Animation.DoubleAnimation
        {
            From = from,
            To = to,
            Duration = TimeSpan.FromMilliseconds(Motion.BaseMs),
            EasingFunction = new Microsoft.UI.Xaml.Media.Animation.CubicEase
            {
                EasingMode = Microsoft.UI.Xaml.Media.Animation.EasingMode.EaseOut,
            },
        };

        var board = new Microsoft.UI.Xaml.Media.Animation.Storyboard
        {
            FillBehavior = Microsoft.UI.Xaml.Media.Animation.FillBehavior.Stop,
        };

        Microsoft.UI.Xaml.Media.Animation.Storyboard.SetTarget(fade, this);
        Microsoft.UI.Xaml.Media.Animation.Storyboard.SetTargetProperty(fade, "Opacity");
        board.Children.Add(fade);
        board.Begin();
    }

    /// <summary>
    /// 从 (fromX, fromY) 弹到 (toX, toY)：位移的落点先写成基值，关键帧按
    /// <see cref="Motion.SpringSamples"/> 打——0→1 的弹簧形状乘上两点之间的距离。
    /// 播完 <see cref="Microsoft.UI.Xaml.Media.Animation.FillBehavior.Stop"/> 回到基值，正好是落点。
    /// </summary>
    internal void SlideSpring(double fromX, double fromY, double toX, double toY)
    {
        _foldPlacedX = toX;
        _foldPlacedY = toY;
        _foldPlacedValid = true;
        _fold.X = toX;
        _fold.Y = toY;

        var board = new Microsoft.UI.Xaml.Media.Animation.Storyboard
        {
            FillBehavior = Microsoft.UI.Xaml.Media.Animation.FillBehavior.Stop,
        };

        AddSpring(board, "X", fromX, toX);
        AddSpring(board, "Y", fromY, toY);
        board.Begin();
    }

    private void AddSpring(Microsoft.UI.Xaml.Media.Animation.Storyboard board, string path, double from, double to)
    {
        var animation = new Microsoft.UI.Xaml.Media.Animation.DoubleAnimationUsingKeyFrames
        {
            Duration = TimeSpan.FromMilliseconds(Motion.SpringMs),
            EnableDependentAnimation = true,
        };

        var samples = Motion.SpringSamples;
        var span = to - from;

        for (var index = 0; index < samples.Count; index++)
        {
            animation.KeyFrames.Add(new Microsoft.UI.Xaml.Media.Animation.LinearDoubleKeyFrame
            {
                KeyTime = Microsoft.UI.Xaml.Media.Animation.KeyTime.FromTimeSpan(
                    TimeSpan.FromMilliseconds(index * Motion.SpringStepMs)),
                Value = from + (span * samples[index]),
            });
        }

        Microsoft.UI.Xaml.Media.Animation.Storyboard.SetTarget(animation, _fold);
        Microsoft.UI.Xaml.Media.Animation.Storyboard.SetTargetProperty(animation, path);
        board.Children.Add(animation);
    }

    /// <summary>自检读回：内容层（纸、字、牌子、渐隐、阴影）当前的可见态——缩放四档必须一格不差。</summary>
    internal (bool Above, bool Below, bool Chips, bool Markdown, bool Body) OverflowChromeVisible
        => (
            _fadeAbove.Visibility == Visibility.Visible,
            _fadeBelow.Visibility == Visibility.Visible,
            _chips.Visibility == Visibility.Visible,
            _md.Visibility == Visibility.Visible,
            _label.Visibility == Visibility.Visible
        );


    /// <summary>自检读回：非 markdown 那一支实际画进卡上的字。</summary>
    internal string PaintedBodyText => _label.Text;

    /// <summary>逐格分账（--perf 用）：开着的时候 Update 内部自己记三段。放在静态字段上是为了不让
    /// 每一格多背几个参数；关着的时候只多一次布尔判断。</summary>
    internal static bool Timing;

    internal static double TimingCameraMs;
    internal static double TimingStampMs;
    internal static double TimingFullMs;

    internal static void ResetTiming()
    {
        TimingCameraMs = 0;
        TimingStampMs = 0;
        TimingFullMs = 0;
    }

    /// <summary>
    /// 这一格画在世界里的框。几何没变就不去读文档里那四个数（每读一个约 1.3µs，330 格每拍就是
    /// 四毫秒上下），用上一拍缓存的那一份。
    /// <para>顺手替紧随其后的 <see cref="Update"/> 判一次输入签名（<c>_tickInputsSame</c>），
    /// 省掉同一拍比两遍——循环里这两句永远是挨着的。</para>
    /// </summary>
    internal Grouping.Rect WorldBox(CanvasNode node, Display display)
    {
        _tickInputsSame = SignatureSame(node);
        _tickInputsChanged = !_tickInputsSame;

        if (!_tickInputsSame)
        {
            RefreshInputs(node);
        }

        return display.ShownFrame ?? _worldBox;
    }

    /// <summary>这一拍这一格的输入（几何＋正文＋配色）有没有变——小地图要不要重对账看它。</summary>
    internal bool InputsChanged => _tickInputsChanged;

    /// <summary>
    /// 拖动中把这一枚<b>只搬、只拉伸</b>到它此刻的世界盒：写 Canvas 位置与布局盒，不跑
    /// <see cref="Update"/> 那条完整重排。松手那一拍的 <see cref="ArrangeAll"/> 才是结算。
    /// </summary>
    /// <remarks>
    /// 为什么单开一条：拖动中的帧预算只买得起"搬被拖的那几格"（10-26 定下的），组框要跟着走
    /// 就只能用同一笔预算。虚线矩形是根元素里 Stretch=Fill 的一枚，跟着布局盒自己长；名牌钉在
    /// 左上角，跟着 Canvas 位置走；环带 <c>_grab</c> 是透明命中区，拖动中指针早已捕获在板面上，
    /// 它晚一拍换几何没人看得见。代价实测：一帧完整 Update 3.7 毫秒，这一条要落在零头里。
    /// </remarks>
    internal void PreviewBox(BoardViewState view, CanvasNode node)
    {
        // 只读那四个数，不碰输入签名那条账：拖动中这一枚的几何每秒变几十次，把
        // _tickInputsSame 翻成"刚判过"会让下一发完整 Update 跳过重读——那时正文可能已经
        // 被 agent 改过了。签名留给完整那一拍去判。
        var screenX = view.ToScreenX(node.X);
        var screenY = view.ToScreenY(node.Y);

        if (!_placedValid || _placedX != screenX)
        {
            Canvas.SetLeft(this, screenX);
            _placedX = screenX;
        }

        if (!_placedValid || _placedY != screenY)
        {
            Canvas.SetTop(this, screenY);
            _placedY = screenY;
        }

        if (!_placedValid || _placedW != node.Width)
        {
            Width = node.Width;
            _placedW = node.Width;
        }

        if (!_placedValid || _placedH != node.Height)
        {
            Height = node.Height;
            _placedH = node.Height;
        }

        _placedValid = true;
    }

    /// <summary>这一格从画面上收起来（视口外、空组、筛选不画的那几格）。状态没变就一笔都不写。</summary>
    internal void Collapse()
    {
        if (_collapsed)
        {
            return;
        }

        _collapsed = true;
        Visibility = Visibility.Collapsed;
        IsHitTestVisible = false;
        Width = 0;
        Height = 0;

        // 宽高回到 0：再出现时那两笔必须重新写下去，否则纸上会留着 "0 宽" 的布局盒。
        _placedW = 0;
        _placedH = 0;
        _hitTestPlaced = false;
        _hitTestValid = true;
    }

    internal bool Collapsed => _collapsed;

    /// <summary>
    /// 这一格的九个数节点是不是都还是上一拍那几个。值节点不可变，改一个值必换一个节点，
    /// 所以"同一个节点"就等于"同一个值"——不用把它读出来比。
    /// </summary>
    private bool SignatureSame(CanvasNode node)
    {
        if (!_sigValid || !ReferenceEquals(node.Json, _sigJson))
        {
            return false;
        }

        var json = node.Json;

        return _sigValueValid
            && node.X == _sigValueX
            && node.Y == _sigValueY
            && node.Width == _sigValueW
            && node.Height == _sigValueH
            && string.Equals(node.Text, _sigValueText, StringComparison.Ordinal)
            && string.Equals(node.Color, _sigValueColor, StringComparison.Ordinal)
            && string.Equals(node.Label, _sigValueLabel, StringComparison.Ordinal)
            && ReferenceEquals(json["x"], _sigX)
            && ReferenceEquals(json["y"], _sigY)
            && ReferenceEquals(json["width"], _sigW)
            && ReferenceEquals(json["height"], _sigH)
            && ReferenceEquals(json["text"], _sigText)
            && ReferenceEquals(json["url"], _sigUrl)
            && ReferenceEquals(json["label"], _sigLabel)
            && ReferenceEquals(json["file"], _sigFile)
            && ReferenceEquals(json["color"], _sigColor);
    }

    /// <summary>把这一格的输入重记一遍；那四个几何数只在它们真变（或还没读过）时才读出来。</summary>
    private void RefreshInputs(CanvasNode node)
    {
        var json = node.Json;

        var geometryChanged = !_sigValid
            || !ReferenceEquals(json, _sigJson)
            || !ReferenceEquals(json["x"], _sigX)
            || !ReferenceEquals(json["y"], _sigY)
            || !ReferenceEquals(json["width"], _sigW)
            || !ReferenceEquals(json["height"], _sigH);

        _sigJson = json;
        _sigX = json["x"];
        _sigY = json["y"];
        _sigW = json["width"];
        _sigH = json["height"];
        _sigText = json["text"];
        _sigUrl = json["url"];
        _sigLabel = json["label"];
        _sigFile = json["file"];
        _sigColor = json["color"];
        _sigValid = true;

        _sigValueX = node.X;
        _sigValueY = node.Y;
        _sigValueW = node.Width;
        _sigValueH = node.Height;
        _sigValueText = node.Text;
        _sigValueColor = node.Color;
        _sigValueLabel = node.Label;
        _sigValueValid = true;

        if (geometryChanged || !_worldBoxValid)
        {
            _worldBox = new Grouping.Rect(node.Id, node.X, node.Y, node.Width, node.Height);
            _worldBoxValid = true;
        }
    }

    public void Update(CanvasNode node, BoardViewState view, double rasterizationScale, Display? display = null)
    {
        var timingStart = Timing ? System.Diagnostics.Stopwatch.GetTimestamp() : 0;

        display ??= Display.None;

        _windowNode = node;
        _windowView = view;
        _windowScale = rasterizationScale;

        // 输入签名：循环刚判过（WorldBox）就接着用；独立调用时自己判一次。
        var inputsSame = _tickInputsSame || SignatureSame(node);

        _tickInputsSame = false;

        if (!inputsSame)
        {
            RefreshInputs(node);
        }

        // ── 相机段：每一拍都走，全是比数＋只在真变时写。
        // 组框在筛选时画在"看得见的那几格"的包围盒上——框跟着便签走，而文档里那份一个字节不动。
        var shown = display.ShownFrame ?? _worldBox;

        var screenX = view.ToScreenX(shown.X);
        var screenY = view.ToScreenY(shown.Y);
        var layoutWidth = Math.Max(2, shown.Width);
        var layoutHeight = Math.Max(2, shown.Height);

        if (_collapsed)
        {
            _collapsed = false;
            Visibility = Visibility.Visible;
            _hitTestValid = false;
        }

        if (!_placedValid || _placedX != screenX)
        {
            Canvas.SetLeft(this, screenX);
            _placedX = screenX;
        }

        if (!_placedValid || _placedY != screenY)
        {
            Canvas.SetTop(this, screenY);
            _placedY = screenY;
        }

        // 布局盒＝世界尺寸；屏幕上的大小由内容层的 _paintZoom 投出来。换档那一拍这里一个数都不变，
        // 变的只有下面那两行——这就是"缩放＝同一张卡做变换，不是重排所有可见卡"。
        if (!_placedValid || _placedW != layoutWidth)
        {
            Width = layoutWidth;
            _placedW = layoutWidth;
        }

        if (!_placedValid || _placedH != layoutHeight)
        {
            Height = layoutHeight;
            _placedH = layoutHeight;
        }

        if (!_placedValid || _placedZoom != view.Zoom)
        {
            _paintZoom.ScaleX = _paintZoom.ScaleY = view.Zoom;
            _placedZoom = view.Zoom;
        }

        _placedValid = true;

        if (display.FoldDX is { } foldXNow && display.FoldDY is { } foldYNow
            && (!_foldPlacedValid || _foldPlacedX != foldXNow || _foldPlacedY != foldYNow))
        {
            _fold.X = foldXNow;
            _fold.Y = foldYNow;
            _foldPlacedX = foldXNow;
            _foldPlacedY = foldYNow;
            _foldPlacedValid = true;
        }

        // 折进去的那一格不能点：它不在那儿，只是滑过去的那一下让人看见了。
        // 聚焦时退成灰的那几格也不接鼠标：视角已经点名了目标，点灰的要算"点空白"。
        var wantsHit = !display.FoldedAway && !display.LensDimmed;

        if (!_hitTestValid || _hitTestPlaced != wantsHit)
        {
            IsHitTestVisible = wantsHit;
            _hitTestPlaced = wantsHit;
            _hitTestValid = true;
        }

        var timingBeforeStamp = Timing ? System.Diagnostics.Stopwatch.GetTimestamp() : 0;

        if (Timing)
        {
            TimingCameraMs += System.Diagnostics.Stopwatch.GetElapsedTime(timingStart, timingBeforeStamp).TotalMilliseconds;
        }

        // ── 内容账：文档那一侧由上面的输入签名判（同一个值节点＝同一个值），视图这一侧比这一串
        // 现成的小数。两边都没变就到此为止：平移的每一帧、缩放的每一拍，逐格走的只有上面那几笔。
        var text = TextLod(view.Zoom, rasterizationScale);
        var shadowed = ShadowLod(view.Zoom);

        // 换档（0.25 阴影线、亚像素文字线）不在这本账里：它只值两笔可见性/画法，走 ApplyLod 那条轻账。
        // 以前这两位进账，一跨档就是全部可见格整套重跑——300 格实测 543.7ms，而同样的两拍第二趟只 35.7ms，
        // 说明贵的不是画，是重跑（11-03 K1，他拍的"跨档只切可见性"）。
        if (_viewStampValid)
        {
            ApplyLod(node, view, text, shadowed);
        }

        var viewStamp = (
            view.FontSizeFor(node.Id),
            NoteStyling.IsDark,
            view.Today.DayNumber,
            view.TagFilter,
            (int?)view.DueFilter,
            view.DoneFilter,
            view.Filtering,
            display.Revealed,
            display.FoldedAway,
            display.LensFocus,
            display.LensDimmed,
            display.Suffix,
            display.ShownFrame,
            display.FoldDX,
            display.FoldDY,
            ShowText ? 0 : TierFor((int)(_worldBox.Width * view.Zoom * Math.Max(1, rasterizationScale))),
            XamlRoot is null,
            _firstLine,

            // 卡片自己的框与正文也进这一笔账：模型先改在 C# 属性上、Json 那枚引用还没换时，
            // 光靠 InputsChanged 会把"这一拍什么都没变"报错，牌子、渐隐、块渲染就不重画
            // （10-27 那 14 条红的成因就是这一格）。
            shown.Width,
            shown.Height,
            node.Text,
            node.Color,
            node.Label,

            // 闸门牌也进账：账本是插件在画的另一路文件，不改 node 不改视图，
            // 不进这一笔账的话 ApplyGateChange 重画的那发会被这一段早退吞掉（实测：牌永远停在旧字）。
            GateMark?.Source,
            GateMark?.Label);

        if (inputsSame && _viewStampValid && viewStamp.Equals(_viewStamp))
        {
            ContentGuardHits++;

            if (Timing)
            {
                TimingStampMs += System.Diagnostics.Stopwatch.GetElapsedTime(timingBeforeStamp).TotalMilliseconds;
            }

            return;
        }

        ContentFullRuns++;
        FullRuns++;
        _viewStamp = viewStamp;
        _viewStampValid = true;

        if (Timing)
        {
            TimingStampMs += System.Diagnostics.Stopwatch.GetElapsedTime(timingBeforeStamp).TotalMilliseconds;
        }

        var surface = NoteStyling.SurfaceFor(node.Color);
        var ink = NoteStyling.InkFor(node.Color, surface);
        var isGroup = Kind == CanvasNodeKind.Group;

        // A group is a container: no fill, no paper, only a dashed frame and its name.
        // 组才画虚线框、也才造那枚刷；普通卡上这枚矩形根本不进树（见构造函数那段）。
        if (isGroup)
        {
            AttachChrome(_frame);
            _frame.Visibility = Visibility.Visible;
            _frame.Stroke = new SolidColorBrush(NoteStyling.FallbackLine());
        }
        else
        {
            _frame.Visibility = Visibility.Collapsed;
        }
        _surface.Background = isGroup ? new SolidColorBrush(Colors.Transparent) : PaperFor(surface);
        _surface.BorderBrush = new SolidColorBrush(NoteStyling.FallbackLine());

        // Once per node per frame: the bar, the readout and the filter all read this same result,
        // and a pan re-runs it for every note on the board.
        // 表格卡的标记读的是卡下面那几行字，不是表格自己那份 markdown——否则一格"@d"能把整张表读成日期。
        var marks = Tags.Read(_entity?.Table is not null ? EntityCards.NoteOf(node) : node.Text, view.Today);
        var due = NoteMarks.StateOf(marks, view.Today);

        // 筛选是视图层的事：被滤掉的格子压暗但仍在原处，绝不移动、也不改文档（所以不进撤销栈）。
        // 组里那些没命中的是唯一的例外——它们被**折进**命中的那一格背后，但那也只是这一枚变换。
        var shows = display.Revealed || !view.Filtering || NoteMarks.Shows(due, marks.Tags, marks.Done, view.TagFilter, view.DueFilter, view.DoneFilter);

        // 聚焦（点左下角那两档之一）：聚起来的那几格全亮、其余退成灰。
        // 它压过筛选那一档——用户是点名要看这几格，不该再被他开着的筛选压暗。
        Opacity = display.FoldedAway ? 0d
            : display.LensFocus ? 1d
            : display.LensDimmed ? LensDimmedOpacity
            : shows ? 1d : 0.22d;
        _label.Foreground = new SolidColorBrush(isGroup ? NoteStyling.InkFor(null, NoteStyling.SurfaceFor(null)) : ink);

        // 色条的高度必须取**这一帧**的 Height。原来它排在 Width/Height 之前，缩放后拿到的是上一帧的
        // 高度，于是 2px 条短一截、又被 Grid 居中——上下各缺一块（他 10-24 报的"左缘没盖满"）。
        if (NoteMarks.BarFor(due, NoteStyling.IsDark) is { } tint && !isGroup)
        {
            AttachChrome(_dueBar);
            _dueBar.Width = NoteMarks.BarWidthPx;
            _dueBar.Height = Height;
            _dueBar.Fill = new SolidColorBrush(NoteStyling.ToColor(tint));
            _dueBar.Visibility = Visibility.Visible;
        }
        else
        {
            _dueBar.Visibility = Visibility.Collapsed;
        }

        if (display.FoldDX is { } foldX && display.FoldDY is { } foldY)
        {
            _fold.X = foldX;
            _fold.Y = foldY;
        }

        // The clip lives on the card now, so the shadow band below it can be painted; text that
        // no longer fits still stays inside the card instead of spilling over a neighbour.
        // 卡面尺寸是世界数：只有改大小/剔除那一拍才变——每发滚轮都 new 一枚几何体会把排版弄脏，
        // 这里按尺寸对账，变了才换。
        var clipKey = $"{Width:0.###}|{Height:0.###}";

        if (!string.Equals(clipKey, _clipKey, StringComparison.Ordinal))
        {
            _clipKey = clipKey;
            _surface.Clip = new RectangleGeometry { Rect = new Windows.Foundation.Rect(0, 0, Width, Height) };
        }

        Clip = null;

        var type = NoteTypography.For(view.FontSizeFor(node.Id));

        // A picture fills its card; only text gets the inset. The three numbers (size, leading,
        // padding) come from one NoteType so they cannot drift apart. 全是世界单位——换档不动它们。
        _surface.Padding = ShowText ? new Thickness(type.Padding) : new Thickness(0);

        var isTable = _entity?.Table is not null;
        var inset = type.Padding;
        var headerWorld = _entityHeader is null ? 0 : _entityHeader.HeaderWorld;
        var bodyWorld = Math.Max(0, Height - (inset * 2));

        // 表格按它自己的行数占高度（每行 30 世界像素，被压小的一格降到 18），剩下的那一截才是备注。
        // 先给备注留一行，表格才有"挤不掉的字"；两者都放不下时表格保住最小行高，备注吃渐隐。
        var tableWorld = 0d;

        if (isTable)
        {
            var leading = type.Leading;
            var note = EntityCards.NoteOf(node);
            var noteWant = Math.Max(1, NaturalVisualLines(note.Length == 0 ? " " : note, Math.Max(20, Width - (inset * 2)), type.Size, leading)) * leading;
            var rows = _entity!.Table!.RowCount;
            var natural = rows * EntityCardLayout.TableLineWorld;
            var floor = Math.Min(rows * 18, Math.Max(0, bodyWorld - headerWorld));

            tableWorld = Math.Min(
                Math.Clamp(bodyWorld - headerWorld - noteWant, floor, natural),
                Math.Max(0, bodyWorld - headerWorld));

            // 表格按世界单位排一次；屏幕上的大小归内容层那枚变换。
            _table.Render(
                _entity.Table,
                1.0,
                Math.Max(20, Width - (inset * 2)),
                tableWorld,
                ink,
                NoteStyling.IsDark);
        }

        if (_entityHeader is not null && !_content.Children.Contains(_headerHost))
        {
            _content.Children.Insert(0, _headerHost);
        }

        _headerHost.Visibility = _entityHeader is null ? Visibility.Collapsed : Visibility.Visible;
        _headerHost.Height = headerWorld;
        _table.Visibility = isTable ? Visibility.Visible : Visibility.Collapsed;

        _entityHeader?.Apply(
            1.0,
            ink,
            NoteStyling.ToColor(Badge.Muted(NoteStyling.IsDark)),
            NoteStyling.IsDark);

        UpdateEdge(node, view);
        _lodShadow = shadowed;

        if (isGroup)
        {
            _textLodOwned = false;
            PaintGroupPlate(node, view, type, display);
        }
        else if (ShowText)
        {
            _textLodOwned = true;
            var leading = type.Leading;

            // 牌子之间的空隙也随卡缩放（变换路线）：节奏是世界的一部分，不该随相机变。
            _chips.Spacing = 4;

            _label.FontSize = type.Size;
            _label.LineHeight = leading;

            // 表格卡这一格吃的字是卡下面那几行备注；表的 markdown 只是表格自己的备份，不拿来当正文。
            var body = Kind == CanvasNodeKind.Text || isTable
                ? marks.Body
                : _entity is not null
                    ? (node.Text ?? string.Empty)
                    : DisplayText(node);

            // 折进去的东西必须有一句"这里藏着几格"，否则筛选看起来像把便签删了。这句话长在框自己的
            // 标题那一格里，不新发明一种控件：同一件东西（组的标题）多说一句，而不是多一枚牌子。
            if (display.Suffix is { Length: > 0 } suffix)
            {
                body += suffix;
            }


            // 不许出现"半行字被横切"。卡片高度可能是在网页那侧量出来的——同一串字在
            // 微软雅黑下会比 Chrome 多占一行；也可能被手柄压小。两种情况下都只允许整行截断，
            // 并用省略号说明"后面还有"，而不是让最后一行露出半截。
            var room = Math.Max(0, Height - (inset * 2) - headerWorld - tableWorld);

            // 行数按**世界尺寸**取整，浮点留 epsilon 那条规矩不变：现在所有量本来就是世界数，
            // 换档不再乘来乘去，160% 那一发 42/21 的取整事故（他 10-25 报的"编辑完不长大"）
            // 从根上没了——同一档位内算式一字不变，档位之间更是同一份缓存。
            const double lineEpsilon = 1e-6;

            // 正文能用到卡片下沿（下内距被下一行的半行借走），所以"完整行数"只扣上内距；
            // 用两倍内距算会把本可以显示的下一行提前判成溢出，于是只能整行显示/整行消失。
            var lineRoom = Math.Max(0, Height - inset - headerWorld - tableWorld);

            IReadOnlyList<NoteMarks.Mark> chips = isGroup ? Array.Empty<NoteMarks.Mark>() : NoteMarks.Chips(marks, view.Today);

            // 闸门状态牌排在自家牌子最前面：它是这张卡"现在干到哪了"的头等事实。
            // 组不画牌子（组是关系不是任务），闸门牌也不给组。
            if (GateMark is { } gate && !isGroup)
            {
                chips = new[] { gate }.Concat(chips).ToList();
            }

            // 先把牌子排成行：带子占多高要等换行定下来才知道（他 11-03 的换行令）。
            var band = PaintChips(chips, ink, surface, Math.Max(20, Width - (inset * 2)));
            var withChips = (int)Math.Floor(((room - band) / leading) + lineEpsilon);

            // 卡片小得装不下一行正文加牌子带时，先保正文：色条还在，时间这件事不会变成沉默。
            var showChips = chips.Count > 0 && band > 0 && withChips >= 1;

            _chips.Visibility = showChips ? Visibility.Visible : Visibility.Collapsed;

            // The room the words actually get, in world units: the marks band is chrome and sits
            // under them, so it comes off before anything asks how many lines fit.
            var roomText = Math.Max(0, showChips ? room - band : room);

            _visibleLines = Math.Max(1, showChips ? withChips : (int)Math.Floor((lineRoom / leading) + lineEpsilon));

            var sourceLines = body.Split('\n');

            _totalLines = Math.Max(1, sourceLines.Length);

            // 文字换了（或换了一格）就回到第一行：谁都不该在一段新内容中间接着上次的位置读。
            if (!string.Equals(_windowKey, $"{node.Id}|{body}", StringComparison.Ordinal))
            {
                _windowKey = $"{node.Id}|{body}";
                _firstLine = 0;
            }

            _firstLine = Math.Clamp(_firstLine, 0, Math.Max(0, _totalLines - 1));

            var innerWidth = Math.Max(20, node.Width - (inset * 2));

            var blocks = (Kind == CanvasNodeKind.Text || _entity is not null) && Markdown.LooksLikeMarkdown(marks.Body);

            // 「这一格按哪种表示画」与「这一拍画没画字」是两件事：后者会被文字 LOD 关掉，
            // 前者只由内容决定。自检读的是前者（它要答"是不是同一张卡"），藏字那一档不许把
            // 块渲染读成单行。
            _paintsMarkdown = blocks;

            // 手动拉高以后窗口必须说实话：整段现在放得下了，就回到第一行——"↑ 更多"亮着
            // 而下面全是空白，比句末三个点更糟（他 10-22 报的第四条）。放不下但底下露着
            // 空档时，窗口贴住底：不许留"显示不完也滚不动"的半屏空白。
            if (_firstLine > 0)
            {
                if (blocks)
                {
                    var allLines = Markdown.Lines(marks.Body);

                    if (MarkdownPaint.Count(type, 1, allLines, roomText) >= allLines.Count)
                    {
                        _firstLine = 0;
                    }
                }
                else
                {
                    var need = Math.Max(
                        sourceLines.Length,
                        NaturalVisualLines(body, innerWidth, _label.FontSize, leading));

                    if (need <= _visibleLines)
                    {
                        _firstLine = 0;
                    }
                    else if (_totalLines - _firstLine < _visibleLines)
                    {
                        _firstLine = Math.Max(0, _totalLines - _visibleLines);
                    }
                }
            }

            _label.Text = _firstLine == 0 ? body : string.Join('\n', sourceLines.Skip(_firstLine));

            _hasMore = HiddenBelow(_label.Text, innerWidth, _label.FontSize, leading)
                       || _totalLines - _firstLine > _visibleLines;

            // 字并进一像素那一档以下不画正文（画了是灰噪，不是字）。11-03 起地板从"可辨线"降到"亚像素线"：
            // 他日常那一档（23%，字高 3.4 像素）整板没字，报「不显示有些影响观感」，而藏字每帧只省 11~12ms，
            // 比牌子那层（~50ms）小一个数量级。纸面、色条、牌子照旧——牌子是 chrome，本来就不随缩放缩。
            _label.Visibility = blocks || !text ? Visibility.Collapsed : Visibility.Visible;

            // 整跑已经把这一档的字画到位了，换档那笔轻账从这里接手。
            _lodText = text;
            if (blocks && !_textHost.Children.Contains(_md))
            {
                _textHost.Children.Insert(0, _md);   // 文字下面/牌子上面：顺序沿用原来那一挂
            }

            _md.Visibility = blocks && text ? Visibility.Visible : Visibility.Collapsed;

            // 有牌子时，多出来的那一行会被文本宿主继续画进牌子带（它不按行高裁字），
            // 而底部渐隐只能停在牌子顶边——渐变带下面于是又露出半行实字（他 10-26 的截图）。
            // 把正文裁在牌子顶边：渐隐的实色一端正好压在这条裁线上，下面一个像素都不漏。
            // markdown 走 _md，也共用这条裁线；两条路各裁自己的宿主。
            var textRowHeight = Math.Max(0, Height - (inset * 2) - headerWorld - tableWorld - band);

            // 裁线是世界数：只有改大小/换那一拍才动。每发都 new 一枚几何体会白脏一次排版，
            // 这里按"裁不裁＋裁到哪"对账，变了才换（_labelClipKey 兼记 null 那一档）。
            var labelClipKey = blocks ? "none" : showChips && _hasMore ? $"{innerWidth:0.###}|{textRowHeight:0.###}" : "off";

            if (!string.Equals(labelClipKey, _labelClipKey, StringComparison.Ordinal))
            {
                _labelClipKey = labelClipKey;

                if (labelClipKey == "off" || labelClipKey == "none")
                {
                    _label.Clip = null;
                }
                else
                {
                    _label.Clip = new RectangleGeometry
                    {
                        Rect = new Windows.Foundation.Rect(0, 0, innerWidth, textRowHeight),
                    };
                }
            }

            // 实体卡和表格卡的备注区天生是空的，空着不像"可以写"。所以在真正能输入的那块位置上
            // 亮一句淡色的"双击键入备注"——它不吃命中（IsHitTestVisible=false），双击照旧落到格子上。
            var hintWanted = !blocks && _entity is not null && body.Trim().Length == 0 && display.Suffix is null;

            _noteHint.Visibility = hintWanted ? Visibility.Visible : Visibility.Collapsed;

            if (hintWanted)
            {
                _noteHint.FontFamily = NoteStyling.NoteFont;
                _noteHint.FontSize = _label.FontSize;
                _noteHint.LineHeight = leading;
                _noteHint.MaxWidth = innerWidth;
                _noteHint.Foreground = new SolidColorBrush(NoteStyling.ToColor(Badge.Muted(NoteStyling.IsDark)));
            }

            if (blocks)
            {
                var lines = Markdown.Lines(marks.Body).Skip(_firstLine).ToList();
                var inkBrush = new SolidColorBrush(ink);
                var quoteBrush = new SolidColorBrush(NoteStyling.ToColor(Badge.Muted(NoteStyling.IsDark)));
                var barBrush = new SolidColorBrush(
                    NoteStyling.WithAlpha(Badge.LineColour(NoteStyling.IsDark), Badge.LineAlpha(NoteStyling.IsDark)));

                // 按“这一宽度下实际换行后的高度”量，不再按源行数：一段长句会换好几行，
                // 旧算法把它当一行，于是内容已经溢出还判“全放得下”——教程里没有渐隐就是这个。
                // 键里不再有缩放：量一次，四档通用（这条正是"缩放那一拍不再逐格重量"的落点）。
                var measureKey = $"{marks.Body}|{type.Size}|{_firstLine}|{innerWidth:0.##}|{NoteStyling.IsDark}|{XamlRoot is null}";

                if (!string.Equals(measureKey, _mdMeasureKey, StringComparison.Ordinal))
                {
                    _mdMeasureKey = measureKey;
                    var measured = MarkdownPaint.Measured(type, 1, lines, innerWidth, inkBrush, quoteBrush, barBrush, XamlRoot);
                    _mdTotalHeight = measured.Total;
                    _mdHeights = measured.Heights;

                    // 还没挂进树时量不出字；先按源行估一个底线，挂上树后 key 会变、再量一次。
                    if (_mdTotalHeight <= 0.5 && lines.Count > 0)
                    {
                        _mdHeights = lines
                            .Select(line => (double)Math.Max(4, Math.Round(type.Leading * line.Scale, MidpointRounding.AwayFromZero)))
                            .ToList();
                        _mdTotalHeight = _mdHeights.Sum();
                    }
                }

                var fullHeight = 0.0;
                var fullCount = 0;

                foreach (var lineHeight in _mdHeights)
                {
                    if (fullHeight + lineHeight > roomText + 0.5)
                    {
                        break;
                    }

                    fullHeight += lineHeight;
                    fullCount++;
                }

                _hasMore = _mdTotalHeight > roomText + 0.5;
                _visibleLines = Math.Max(1, fullCount);
                _belowFadeTop = inset + headerWorld + tableWorld + fullHeight;

                // 窗口走了第几行必须进这把钥匙：滚完两行以后不能还画上一屏字。
                var key = $"{measureKey}|{ink}";

                if (!string.Equals(key, _mdKey, StringComparison.Ordinal))
                {
                    _mdKey = key;
                    _md.Children.Clear();

                    foreach (var element in MarkdownPaint.PaintAll(type, 1, lines, inkBrush, quoteBrush, barBrush))
                    {
                        _md.Children.Add(element);
                    }
                }

                // markdown 共用牌子顶边那条裁线；也按对账换。
                var mdClipKey = showChips && _hasMore ? $"{innerWidth:0.###}|{textRowHeight:0.###}" : "off";

                if (!string.Equals(mdClipKey, _mdClipKey, StringComparison.Ordinal))
                {
                    _mdClipKey = mdClipKey;
                    _md.Clip = mdClipKey == "off"
                        ? null
                        : new RectangleGeometry { Rect = new Windows.Foundation.Rect(0, 0, innerWidth, textRowHeight) };
                }
            }
            else
            {
                // 渐隐从最后一条完整行的底边开始：行距再大，这条带子也一直盖到卡底，
                // 不会只盖住空白、让下面的字看起来“没有渐隐”。
                _belowFadeTop = inset + headerWorld + tableWorld + (_visibleLines * leading);

                if (!string.Equals("off", _mdClipKey, StringComparison.Ordinal))
                {
                    _mdClipKey = "off";
                    _md.Clip = null;
                }

                // 还有内容时多放一行在卡底外面：卡自己会裁掉大半行，渐隐再压上去，
                // 屏幕上就露出"半行正在淡出"，而不是一行都不剩、只剩一片空白。
                _label.MaxLines = _hasMore ? _visibleLines + 1 : _visibleLines;

                // 有溢出时不加省略号：WordEllipsis 会把放不下的下一行整行换成"…"，
                // 于是永远看不到半行；None 才允许它被卡片的 Clip 从中间裁断。
                _label.TextTrimming = _hasMore ? TextTrimming.None : TextTrimming.WordEllipsis;

                // 文字宿主本来只到下内距的上沿；溢出时把下内距让给它，半行才能真的画出来，
                // 再由卡片外沿裁断。没有溢出、或有牌子时恢复上下对称的内距。
                _surface.Padding = _hasMore && !showChips
                    ? new Thickness(inset, inset, inset, 0)
                    : new Thickness(inset);

                // 溢出布局（行数/多出的半行/下内距）变了就主动重排一次：已有便签在加载或拖高时
                // 没有文字变化，若只依赖 TextBlock 自己失效，半行会等下一次编辑才出现。
                var overflowKey = $"{_hasMore}|{_visibleLines}|{_label.MaxLines}|{leading:0.##}|{room:0.##}";

                if (!string.Equals(overflowKey, _overflowKey, StringComparison.Ordinal))
                {
                    _overflowKey = overflowKey;
                    _label.InvalidateMeasure();
                    _textHost.InvalidateMeasure();
                }

                // 多出的那一行不额外限高：标签照常排到卡底，由卡片自己的 Clip 从中间裁断，
                // 底部渐隐再把它吃掉——这才是"半行"。限成正文区高度反而会让整行消失。
                _label.ClearValue(FrameworkElement.HeightProperty);
            }
        }
        else if (Kind == CanvasNodeKind.File)
        {
            var wanted = (int)(node.Width * view.Zoom * Math.Max(1, rasterizationScale));
            var tier = TierFor(wanted);

            if (tier != _decodeTier || _loadedPath != node.FilePath)
            {
                _decodeTier = tier;
                LoadImage(node);
            }
        }

        UpdateOverflowFades(node, view, headerWorld + tableWorld);

        // Update 每拍都把 _frame 的描边按默认虚线铺回去；警示态（组钮拖那一拍）要压过它。
        ApplyGroupAlert();

        if (Timing)
        {
            TimingFullMs += System.Diagnostics.Stopwatch.GetElapsedTime(timingBeforeStamp).TotalMilliseconds;
        }
    }

    /// <summary>
    /// 组只画两样东西：压住顶边左上角的<b>名牌</b>，和沿虚线铺开的<b>环带</b>。框内不接鼠标，
    /// 所以单击组里的便签落在那枚便签上（他 10-22 定的语义）；点名牌或环带才＝选中整组。
    /// </summary>
    private void PaintGroupPlate(CanvasNode node, BoardViewState view, NoteType type, Display display)
    {
        // 名牌与环带只有组要；进树这一步放在这里（构造函数不再预挂那两枚空壳）。
        AttachChrome(_namePlate);
        AttachChrome(_grab);

        // 组名和正文走同一把世界尺：换档不动字号，屏幕上的大小归内容层那枚变换。
        _label.FontSize = type.Size;
        _label.LineHeight = type.Leading;

        var text = DisplayText(node);

        // 折进去的那几格要有一句"藏着几格"，这句话长在名牌上——它本来就是组的标题行。
        if (display.Suffix is { Length: > 0 } suffix)
        {
            text += suffix;
        }

        _label.Text = text;
        _label.MaxLines = 1;
        _label.MaxWidth = Math.Max(60, Width - 12);

        // 名牌不再套壳：透明底（保住"点名字＝选中整组"的可点区），不画边框、不圆角。
        _namePlate.Background = new SolidColorBrush(Colors.Transparent);
        _namePlate.BorderBrush = new SolidColorBrush(Colors.Transparent);

        // 抬到框顶边上方：负移走 RenderTransform，不碰布局——命中区跟着变换走，
        // 所以"点名字"和"看见名字"落在同一块像素上。
        // 抬升量用**行高**算，不用 _namePlate.DesiredSize——编辑别的格子那一拍里，这枚还没排版
        // 的 Border 量出来是 0 高，抬升塌成 -3 就把名牌压回框顶线上（10-22 他截图：新建时组的描述
        // 被挤进框里，点一下才回到上方）。行高是确定的，任何时候都算得出同一个位置。
        _nameLift.Y = -(_label.LineHeight + 3);

        _namePlate.Visibility = Visibility.Visible;

        var band = 7;
        var key = $"{Width:0.#}|{Height:0.#}";

        if (key != _grabKey)
        {
            _grabKey = key;
            _grab.Data = new GeometryGroup
            {
                FillRule = FillRule.EvenOdd,
                Children =
                {
                    new RectangleGeometry { Rect = new Windows.Foundation.Rect(-band, -band, Width + band * 2, Height + band * 2) },
                    new RectangleGeometry { Rect = new Windows.Foundation.Rect(band, band, Math.Max(0, Width - band * 2), Math.Max(0, Height - band * 2)) },
                },
            };
        }

        _grab.Visibility = Visibility.Visible;

        // 组没有行窗口这回事：滚轮在这一格上永远该交回板子去缩放。
        _hasMore = false;
        _firstLine = 0;
        _totalLines = 1;
        _visibleLines = 1;

        _md.Visibility = Visibility.Collapsed;
        _chips.Visibility = Visibility.Collapsed;
    }

    private static Microsoft.UI.Xaml.Shapes.Rectangle OverflowFade(VerticalAlignment align, string name)
        => new()
        {
            Name = name,
            VerticalAlignment = align,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            IsHitTestVisible = false,
            Visibility = Visibility.Collapsed,
            Fill = new LinearGradientBrush { StartPoint = new Windows.Foundation.Point(0, 0), EndPoint = new Windows.Foundation.Point(0, 1) },
        };

    /// <summary>
    /// 内容画不下时不再挂"更多 ↓"角标（他嫌突兀），改成在卡片上/下边缘压一道"表面色↔透明"的
    /// 渐隐遮罩：底下还有行就把最后一行淡进卡底；往上翻过则顶部淡一道。无字，靠视觉暗示。
    /// </summary>
    /// <summary>_paint 里那几件的固定层序：按需挂上去的时候必须插回原位，否则牌子会被纸盖住。</summary>
    private static readonly UIElement[] ChromeOrder = [];

    private void AttachChrome(UIElement child)
    {
        if (_paint.Children.Contains(child))
        {
            return;
        }

        // 目标位置＝"层序排在它前面的那些已有子元素"的个数。用成员判断，不看 IsLoaded——
        // 同一拍里 Update 跑两遍时 IsLoaded 还是 false，重复 Add 会让 XAML 抛 0x800F1000（10-24 的教训）。
        var order = ChromeOrderOf(child);
        var index = 0;

        foreach (var existing in _paint.Children)
        {
            if (ChromeOrderOf(existing) < order)
            {
                index++;
            }
        }

        _paint.Children.Insert(index, child);
    }

    /// <summary>层序表：_bands 最底，_dueBar 最上。</summary>
    private int ChromeOrderOf(UIElement child) => child switch
    {
        _ when ReferenceEquals(child, _bands) => 0,
        _ when ReferenceEquals(child, _surface) => 1,
        _ when ReferenceEquals(child, _fadeAbove) => 2,
        _ when ReferenceEquals(child, _fadeBelow) => 3,
        _ when ReferenceEquals(child, _press) => 4,
        _ when ReferenceEquals(child, _groupHalo) => 5,
        _ when ReferenceEquals(child, _frame) => 6,
        _ when ReferenceEquals(child, _grab) => 7,
        _ when ReferenceEquals(child, _namePlate) => 8,
        _ when ReferenceEquals(child, _dueBar) => 9,
        _ => 10,
    };

    private void UpdateOverflowFades(CanvasNode node, BoardViewState view, double headerWorld)
    {
        var textCard = ShowText && Kind != CanvasNodeKind.Group
            && (_label.Visibility == Visibility.Visible || _md.Visibility == Visibility.Visible);

        // 渐隐只取决于"内容画不画得下"，与 zoom 无关（他 10-27 的工单：缩放时看到的必须是同一张卡）。
        // 以前这里有一道 view.Zoom >= 0.5 的门槛：缩到总览那一档，淡出变成硬切一行，
        // 于是每一档缩放都要重算一遍——既改内容又花计算。
        var above = textCard && _firstLine > 0;
        var below = textCard && _hasMore;

        // 只有真要淡的那一面才进树（两条带各带一枚渐变刷）。以前是无条件挂两条、再按需 Collapsed——
        // 绝大多数卡不溢出，挂上去的两枚纯属占元素。10-28 阶段 1。
        if (above)
        {
            AttachChrome(_fadeAbove);
        }
        else if (_paint.Children.Contains(_fadeAbove))
        {
            // AttachChrome 是单向的（挂上就不摘），所以"曾经溢出过、后来放得下"的卡会一直留着它。
            // 这一步把"不再需要"也真的还回去——否则这一刀只把首帧的空壳推迟了，没省到。
            _paint.Children.Remove(_fadeAbove);
        }

        if (below)
        {
            AttachChrome(_fadeBelow);
        }
        else if (_paint.Children.Contains(_fadeBelow))
        {
            _paint.Children.Remove(_fadeBelow);
        }

        _fadeAbove.Visibility = above ? Visibility.Visible : Visibility.Collapsed;
        _fadeBelow.Visibility = below ? Visibility.Visible : Visibility.Collapsed;

        if (!above && !below)
        {
            return;
        }

        var surface = NoteStyling.SurfaceFor(node.Color);

        // 渐隐贴着**正文区**的上下边，而不是卡片外沿：卡片还有一圈内距，压在外沿上的
        // 那道只会盖住空白，文字一点都没被淡到（他 10-25 的第二张截图就是这一格）。
        var type = NoteTypography.For(view.FontSizeFor(node.Id));
        var inset = type.Padding;
        var leading = type.Leading;

        // 带子少于一整行高就等于没开：它整条躺在行与行的空隙里，一个字都不吃
        // （他 10-26 第一条说的"明明有渐隐但是看似没有生效"）。
        var aboveHeight = Math.Min(Math.Max(inset + leading, leading), Math.Max(8, Height - headerWorld));

        // 牌子那一排（标签／日期／序号）是常驻 chrome，**渐隐绝不能吃在它们身上**。
        // 以前这道遮罩永远贴着卡片外沿：有牌子时正文被裁的那条边在它上面一截，于是
        // 该淡的地方硬切、牌子反倒被抹掉下沿（他 10-25 的 @n7：两个读数同一个根因）。
        // 有牌子时：正文区的下沿在"卡片外沿－下内距－牌子带"那里（有牌子时正文借不到下内距，
        // 见上面 Padding 那一发），所以把遮罩从底沿抬起 内距＋牌子带 这么多。
        var chips = _chips.Visibility == Visibility.Visible ? _chipBandWorld : 0;
        var bottomMargin = chips > 0 ? inset + chips : 0;
        var fadeBottom = Math.Max(0, Height - bottomMargin);

        // 带子的上沿抬到"最后一行的行顶"：整行墨都走在渐变里——行顶几乎不盖，到裁线全盖。
        // 只按"最后一行底边到卡底"那点剩位算，正好排满整行的卡片会得到一条比行距还窄的带子，
        // 它整条躺在字底下的空隙里，一个字都不吃（他 10-26 第一条的"看似没有生效"）。
        _belowFadeLeading = leading;
        var belowTop = Math.Max(0, _belowFadeTop - leading);
        var belowHeight = Math.Clamp(
            Math.Max(fadeBottom - belowTop, leading),
            8,
            Math.Max(8, fadeBottom));

        _fadeAbove.Margin = new Thickness(0, headerWorld, 0, 0);
        _fadeBelow.Margin = new Thickness(0, 0, 0, bottomMargin);

        var paper = new Rgb(surface.R, surface.G, surface.B);

        if (above)
        {
            ApplyFade(_fadeAbove, aboveHeight, headerWorld, Height, paper, solidAtBottom: false);
        }

        if (below)
        {
            ApplyFade(_fadeBelow, belowHeight, fadeBottom - belowHeight, Height, paper, solidAtBottom: true);
        }
    }

    /// <summary>自检那把尺：底部渐隐这条带子的形状，两个数都按行距计——
    /// ① 带高／行距：小于 1 就是这条带子比行距还窄；
    /// ② 上沿抬到最后一行行盒底边**以上**多少：0 表示它整条躺在字底下的空隙里，
    /// 一个字都不吃，屏幕上就是"明明有渐隐却像没生效"（他 10-26 的第一条）。
    /// 读的是排版完的 ActualHeight，不是我们请求的那个数：请求对了而没排上去同样要红。</summary>
    internal (double HeightInLines, double OverLastLineInLines) OverflowFadeBelowShape
    {
        get
        {
            if (_fadeBelow.Visibility != Visibility.Visible || _belowFadeLeading <= 0)
            {
                return (0, 0);
            }

            var top = Height - _fadeBelow.Margin.Bottom - _fadeBelow.ActualHeight;

            return (_fadeBelow.ActualHeight / _belowFadeLeading, (_belowFadeTop - top) / _belowFadeLeading);
        }
    }

    /// <summary>自检那把尺：底部渐隐每一停的**颜色**必须等于卡面纸在同一高度的实际颜色。
    /// 这条在旧写法（拿卡面本色平铺）上是红的——那正是"放大看颜色会分层"的根因。</summary>
    internal bool OverflowFadeMatchesPaper
    {
        get
        {
            if (_fadeBelow.Visibility != Visibility.Visible
                || _fadeBelow.Fill is not LinearGradientBrush brush
                || brush.GradientStops.Count == 0
                || _fadeBelow.ActualHeight <= 0)
            {
                return false;
            }

            var paper = new Rgb(_paperKey.R, _paperKey.G, _paperKey.B);

            // 带子的上沿在卡片坐标里的哪儿：它贴着底沿（抬起 bottomMargin），高就是排版出来的那一格。
            var top = Height - _fadeBelow.Margin.Bottom - _fadeBelow.ActualHeight;

            foreach (var stop in brush.GradientStops)
            {
                var y = (top + (stop.Offset * _fadeBelow.ActualHeight)) / Math.Max(1, Height);
                var want = NoteSurface.PaperAt(paper, y, NoteStyling.IsDark);

                if (stop.Color.R != want.R || stop.Color.G != want.G || stop.Color.B != want.B)
                {
                    return false;
                }
            }

            return true;
        }
    }

    /// <summary>自检那把尺：底部渐隐与牌子那一排不许有交集（@n7 的根因就是它盖在牌子上）。</summary>
    internal bool FadeClearsChips()
    {
        if (_fadeBelow.Visibility != Visibility.Visible || _chips.Visibility != Visibility.Visible)
        {
            return true;
        }

        if (_fadeBelow.ActualWidth <= 0 || _chips.ActualHeight <= 0)
        {
            // 量不到就是这一枚根本没排过版——不许把它当成"通过"。
            return false;
        }

        var fade = _fadeBelow.TransformToVisual(this).TransformBounds(
            new Windows.Foundation.Rect(0, 0, _fadeBelow.ActualWidth, _fadeBelow.ActualHeight));
        var chips = _chips.TransformToVisual(this).TransformBounds(
            new Windows.Foundation.Rect(0, 0, _chips.ActualWidth, _chips.ActualHeight));

        return fade.Bottom <= chips.Top + 0.5;
    }

    /// <summary>自检那把尺之二：内容被压小时，底部那道渐隐必须真的亮着（"硬切边、没有渐隐"就是它没亮）。</summary>
    internal bool PaintsOverflowBelow => _fadeBelow.Visibility == Visibility.Visible && _fadeBelow.ActualHeight > 8;

    /// <summary>自检那把尺之三：有牌子时正文必须裁在牌子顶边。
    /// 不裁的话多出来的半行会继续画进牌子带，渐隐带下面又露出一截实字（他 10-26 的截图）。</summary>
    internal bool ClipsOverflowAtChips()
    {
        if (_fadeBelow.Visibility != Visibility.Visible || _chips.Visibility != Visibility.Visible)
        {
            return true;
        }

        FrameworkElement text = _md.Visibility == Visibility.Visible ? _md : _label;

        if (text.Visibility != Visibility.Visible)
        {
            return true;
        }

        if (text.Clip is not RectangleGeometry clip
            || text.ActualWidth <= 0 || text.ActualHeight <= 0 || _chips.ActualHeight <= 0)
        {
            return false;
        }

        var clipBounds = text.TransformToVisual(this).TransformBounds(clip.Rect);
        var chipsBounds = _chips.TransformToVisual(this).TransformBounds(
            new Windows.Foundation.Rect(0, 0, _chips.ActualWidth, _chips.ActualHeight));

        return clipBounds.Bottom <= chipsBounds.Top + 0.5 && clipBounds.Height >= Math.Max(8, _label.LineHeight) - 0.5;
    }

    /// <summary>
    /// Paints one overflow fade band. Its colour is not a flat card colour but <b>the card's own
    /// paper at that exact height</b>, and only its alpha moves.
    /// </summary>
    /// <remarks>
    /// The card is filled with a three-stop ramp (white over at the top edge, the chosen colour
    /// untouched at 42%, black over at the bottom). A band painted with the flat colour therefore
    /// differs from the paper it lies on everywhere except where the two happen to meet: the strip
    /// reads as a second sheet laid over the first, and the eye catches the step at both ends of
    /// the band. That is the "放大看颜色会分层" report of 2026-10-26, and it is why the three earlier
    /// fixes — band height, band position, the curve between the stops — could not land: none of
    /// them touched which colour was being laid down. Sampling the same ramp makes the band the
    /// paper, so there is nothing left to see except the words dissolving.
    /// </remarks>
    private static void ApplyFade(
        Microsoft.UI.Xaml.Shapes.Rectangle fade,
        double height,
        double topInCard,
        double cardHeight,
        Rgb paper,
        bool solidAtBottom)
    {
        fade.Height = height;

        if (fade.Fill is not LinearGradientBrush brush || cardHeight <= 0)
        {
            return;
        }

        // 五个停点走 smoothstep（3t²−2t³）：线性那一把在带子的第一个像素就开始吃字，
        // 接缝就露在那儿；两端缓开缓合，才看不出这道带子从哪儿开始。
        var offsets = new List<double> { 0, 0.25, 0.5, 0.75, 1 };

        // 纸自己的折点（42% 那一停）要是落进带子里，必须原样进停点：不然带子内部会把这条折线
        // 拉成一段直线，在带子中间又分出一层来。
        var kink = ((cardHeight * NoteSurface.PaperMiddleOffset) - topInCard) / height;

        if (kink is > 0 and < 1)
        {
            offsets.Add(kink);
        }

        offsets.Sort();

        brush.GradientStops.Clear();

        foreach (var t in offsets)
        {
            var rgb = NoteSurface.PaperAt(paper, (topInCard + (height * t)) / cardHeight, NoteStyling.IsDark);
            var eased = t * t * (3 - (2 * t));
            var alpha = solidAtBottom ? eased : 1 - eased;

            brush.GradientStops.Add(new GradientStop
            {
                Color = Color.FromArgb((byte)Math.Round(alpha * 255), rgb.R, rgb.G, rgb.B),
                Offset = t,
            });
        }
    }

    /// <summary>
    /// Paints the three paper layers, or the single bottom line when the zoom is below the
    /// shadow budget. Rebuilds only when the geometry actually changed: a pan moves the whole
    /// control and must not re-make N brushes per frame.
    /// </summary>
    /// <summary>
    /// 换档那一拍的轻账：只切"画不画正文"与"阴影 ↔ 底线"这两笔。整格重跑（牌子、裁切、行数、渐隐）
    /// 留给真变了的事——这两位一旦进内容账，跨过阈值的那一发就是全部可见格整套重跑。
    /// </summary>
    /// <remarks>
    /// 只有走过"正文那一排"的格子归它管（<see cref="_textLodOwned"/>）：组的牌子是 chrome，
    /// 图卡与表格的第二行各有自己的画法，都不该被这一档翻掉。
    /// </remarks>
    private void ApplyLod(CanvasNode node, BoardViewState view, bool text, bool shadowed)
    {
        if (text != _lodText)
        {
            _lodText = text;

            if (_textLodOwned)
            {
                _label.Visibility = _paintsMarkdown || !text ? Visibility.Collapsed : Visibility.Visible;
                _md.Visibility = _paintsMarkdown && text ? Visibility.Visible : Visibility.Collapsed;
            }
        }

        if (shadowed != _lodShadow)
        {
            _lodShadow = shadowed;

            // UpdateEdge 自己 keyed（_shadowKey 变了才拆建那三条带子），所以这里只管叫它来看一眼。
            UpdateEdge(node, view);
        }
    }

    private void UpdateEdge(CanvasNode node, BoardViewState view)
    {
        if (Kind == CanvasNodeKind.Group)
        {
            // The frame is drawn by _frame; a container carrying a plank of shadow is the bug the
            // first screenshot of this port caught.
            if (_shadowKey != "group")
            {
                _shadowKey = "group";
                _bands.Children.Clear();
                _press.Visibility = Visibility.Collapsed;
                _surface.BorderThickness = new Thickness(0);
            }

            return;
        }

        // 阴影几何是世界数（内容层那枚变换把它投到屏幕上），键里不再带逐档缩放——
        // 只有"影↔线"那一级 LOD 翻档（跨过 ShadowMinZoom）才值得重铺一次。
        // 翻不翻这一档读的是 <see cref="ShadowLod"/>（带迟滞），不是裸阈值比较。
        var shadowed = ShadowLod(view.Zoom);
        var key = $"{shadowed}|{node.Width:0.#}|{node.Height:0.#}|{NoteStyling.IsDark}";

        if (!shadowed)
        {
            // tldraw's own degradation is two things at once: the shadow goes away AND one bottom
            // line appears. Dropping the shadow without the line leaves a card with no edge at all.
            if (_shadowKey != "line")
            {
                _shadowKey = "line";
                _bands.Children.Clear();
                _press.Visibility = Visibility.Collapsed;
                _surface.BorderThickness = new Thickness(0, 0, 0, NoteSurface.FallbackLineWidth);
            }

            return;
        }

        if (_surface.BorderThickness.Bottom != 0)
        {
            _surface.BorderThickness = new Thickness(0);
        }

        if (_shadowKey == key)
        {
            return;
        }

        _shadowKey = key;
        _bands.Children.Clear();

        // One rectangle per blurred layer, filled with that layer's own tail. The first port
        // collapsed the two outer layers into one soft ellipse; a single sample point agreed
        // with it and his eye did not — an ellipse fades for about twice as long and never shows
        // where one layer's tail ends, so the card read as floating rather than sitting on paper.
        foreach (var band in NoteSurface.BandsFor(node.Id, 1.0, node.Width, node.Height))
        {
            if (band.Inset || band.Width <= 0 || band.Height <= 0)
            {
                continue;
            }

            var brush = new LinearGradientBrush
            {
                StartPoint = new Windows.Foundation.Point(0, 0),
                EndPoint = new Windows.Foundation.Point(0, 1),
            };

            foreach (var (offset, alpha) in band.Stops)
            {
                // Band.Stops' offsets are PIXELS along the band; a GradientStop wants 0–1.
                // Feeding them in raw clamps everything past the first sample to offset 1, which
                // turns the Gaussian tail into one straight ramp — that is the "shadow too long"
                // this line replaced.
                brush.GradientStops.Add(new GradientStop
                {
                    Color = NoteStyling.WithAlpha(band.Color, alpha),
                    Offset = Math.Clamp(offset / band.Height, 0, 1),
                });
            }

            var rect = new Microsoft.UI.Xaml.Shapes.Rectangle
            {
                Width = band.Width,
                Height = band.Height,
                Fill = brush,
                IsHitTestVisible = false,
            };

            Canvas.SetLeft(rect, band.X);
            Canvas.SetTop(rect, band.Y);
            _bands.Children.Add(rect);
        }

        var press = NoteSurface.BandsFor(node.Id, 1.0, node.Width, node.Height)
            .FirstOrDefault(band => band.Inset);

        if (press is { Width: > 0 })
        {
            var pressBrush = new LinearGradientBrush
            {
                StartPoint = new Windows.Foundation.Point(0, 0),
                EndPoint = new Windows.Foundation.Point(0, 1),
            };

            // The inset layer is a ~2.2% press along the inside of the top edge; at that depth one
            // linear fade is enough, and it is the layer the prototype draws as an inset shadow.
            byte PressAlpha(double fraction) => (byte)Math.Round(Math.Clamp(press.Alpha * fraction, 0, 1) * 255);

            pressBrush.GradientStops.Add(new GradientStop { Color = Color.FromArgb(PressAlpha(1), press.Color.R, press.Color.G, press.Color.B), Offset = 0 });
            pressBrush.GradientStops.Add(new GradientStop { Color = Color.FromArgb(PressAlpha(0), press.Color.R, press.Color.G, press.Color.B), Offset = 1 });

            _press.Width = press.Width;
            _press.Height = press.Height;
            _press.Fill = pressBrush;
            _press.Visibility = Visibility.Visible;
        }
        else
        {
            _press.Visibility = Visibility.Collapsed;
        }

        if (_bands.Children.Count == 0)
        {
            _press.Visibility = Visibility.Collapsed;
        }
    }

    /// <summary>
    /// Lays the mark chips out along the card's bottom-left. Rebuilt only when their content changed:
    /// a pan re-runs <c>Update</c> for every card, and re-making a chip mid-hover would hide the ×
    /// the delete depends on.
    /// </summary>
    /// <summary>
    /// 牌子带按卡片内宽<b>换行</b>（他 11-03：标签多了被挡住而不是换行）：每行放不下下一枚就起新行。
    /// 返回它占的世界高度（0＝没有牌子），正文的行数预算扣的就是这个数。
    /// </summary>
    private double PaintChips(IReadOnlyList<NoteMarks.Mark> chips, Color ink, Color surface, double wrapWidth)
    {
        _chipWrapWidth = wrapWidth;

        if (chips.Count == 0)
        {
            _chips.Children.Clear();
            _chipKey = string.Empty;
            _chipRowCount = 0;
            _chipBandWorld = 0;

            return 0;
        }

        // 键里带上换行宽：卡片被拉宽/拉窄时行要重分。缩放仍不进键（牌子按世界字号排一次）。
        var key = $"{wrapWidth:0.#}|{surface}|{string.Join("/", chips.Select(chip => $"{chip.Source}={chip.Label}"))}";

        if (key != _chipKey)
        {
            _chipKey = key;
            _chips.Children.Clear();

            const double spacing = 4;
            StackPanel? row = null;
            var rowWidth = 0.0;

            // 形状不在这里定。卡片这一排和编辑器里那一排走同一枚渲染器（ChromeStyling.Chip）。
            foreach (var chip in chips)
            {
                var source = chip.Source;

                var border = ChromeStyling.Chip(
                    chip,
                    NoteType.ChipFont(1),
                    ink,
                    surface,
                    () => RemoveMark?.Invoke(NodeId, source),
                    (at, stamp) => ChipPressed?.Invoke(NodeId, chip, at, stamp),
                    () => ChipDoublePressed?.Invoke(NodeId, chip),
                    ChipTip(chip));

                // 量一次自然宽：换行判据是"这一行还放不放得下下一枚"。
                border.Measure(new Windows.Foundation.Size(double.PositiveInfinity, double.PositiveInfinity));
                var width = border.DesiredSize.Width;

                if (row is null || (row.Children.Count > 0 && rowWidth + spacing + width > wrapWidth + 0.5))
                {
                    row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = spacing };
                    _chips.Children.Add(row);
                    rowWidth = 0;
                }

                row.Children.Add(border);
                rowWidth += (row.Children.Count > 1 ? spacing : 0) + width;
            }

            _chipRowCount = _chips.Children.Count;
        }

        _chipBandWorld = (_chipRowCount * NoteType.ChipBand(1)) + ((_chipRowCount - 1) * 4);

        return _chipBandWorld;
    }

    /// <summary>牌身的悬停说明跟着新三扇门写（他 11-03 拍：点＝聚焦、双击＝编辑、×＝删）。</summary>
    private static string ChipTip(NoteMarks.Mark chip)
    {
        if (chip.Done is not null)
        {
            return "点一下＝切换完成 · 双击＝编辑便签 · 点 × 去掉";
        }

        if (chip.IsDate)
        {
            return "点一下＝聚焦这个日期 · 双击＝编辑便签 · 点 × 去掉";
        }

        if (chip.Label.StartsWith("NO.", StringComparison.Ordinal))
        {
            return "双击＝编辑便签 · 点 × 去掉";
        }

        return "点一下＝聚焦这个标签 · 双击＝编辑便签 · 点 × 去掉";
    }

    private static string DisplayText(CanvasNode node) => node.Kind switch
    {
        CanvasNodeKind.Text => node.Text ?? string.Empty,
        CanvasNodeKind.Link => node.Url ?? node.Label ?? string.Empty,
        CanvasNodeKind.Group => node.Label ?? string.Empty,
        CanvasNodeKind.File => node.FilePath ?? string.Empty,
        _ => string.Empty,
    };

    /// <summary>构造那一侧的判定（图和字谁在）。增量重建对账时要按同一把尺算，所以不许有第二份。</summary>
    internal static bool LooksLikeImage(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return false;
        }

        return Array.IndexOf(ImageExtensions, Path.GetExtension(path).ToLowerInvariant()) >= 0;
    }

    private static int TierFor(int wantedPixels)
    {
        foreach (var tier in DecodeTiers)
        {
            if (tier >= wantedPixels)
            {
                return tier;
            }
        }

        return DecodeTiers[^1];
    }

    private void LoadImage(CanvasNode node)
    {
        var absolute = node.FilePath is null ? null : ResolveAsset(node.FilePath);

        if (absolute is null)
        {
            IsBroken = true;
            ShowMissing($"未找到\n{node.FilePath}");

            return;
        }

        try
        {
            var bitmap = new BitmapImage { DecodePixelWidth = _decodeTier };
            bitmap.UriSource = new Uri(absolute, UriKind.Absolute);

            _image.Source = bitmap;

            if (!ReferenceEquals(_surface.Child, _image))
            {
                _surface.Child = _image;
            }

            IsBroken = false;
            _loadedPath = node.FilePath;
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException)
        {
            // A tile that says what went wrong beats a blank square that says nothing.
            IsBroken = true;
            ShowMissing($"读不出这张图\n{Path.GetFileName(absolute)}");
        }
    }

    /// <summary>把这一格换成"说清楚它为什么空着"的那行字。同一枚元素已经在那格上就不再挂一次。</summary>
    private void ShowMissing(string text)
    {
        _missing ??= new TextBlock
        {
            TextWrapping = TextWrapping.Wrap,
            FontFamily = NoteStyling.NoteFont,
        };

        _missing.Text = text;

        if (!ReferenceEquals(_surface.Child, _missing))
        {
            _surface.Child = _missing;
        }
    }
}
