using System.IO;
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
/// The size and the font size are computed from the camera on every view change instead of
/// letting a render transform stretch an already rasterised node: at 3× a transformed
/// <see cref="TextBlock"/> shows smeared glyphs, and reading a note after zooming in is most
/// of what this board is for.
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
    /// The 2px edge bar an <c>@d…</c> date earns. Screen pixels, not world ones: it is a signal to be
    /// noticed at any zoom, and a bar that thins to nothing at 40% is the same silence as no bar.
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
    private readonly StackPanel _chips = new()
    {
        Orientation = Orientation.Horizontal,
        Spacing = 4,
        Visibility = Visibility.Collapsed,
        VerticalAlignment = VerticalAlignment.Bottom,
    };

    /// <summary>Set by the window: (node id, the exact span in the text) → remove it, undoably.</summary>
    internal Action<string, string>? RemoveMark;

    /// <summary>Set by the window: (node id, the exact span) → open the editor with that mark selected.</summary>
    internal Action<string, string>? EditMark;

    /// <summary>The chips are rebuilt only when their content changed: re-making them every frame of a
    /// pan would drop the hover state the × depends on.</summary>
    private string _chipKey = string.Empty;

    /// <summary>Body on top, marks under it, in one row each — so the two cannot overlap and the band
    /// reserved for the marks is the band they actually take.</summary>
    private readonly Grid _textHost = new();

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
    private LinearGradientBrush PaperFor(Color surface)
    {
        if (_paper is not null && _paperKey.R == surface.R && _paperKey.G == surface.G && _paperKey.B == surface.B)
        {
            return _paper;
        }

        _paperKey = surface;

        var (top, bottom) = NoteSurface.PaperLight(NoteStyling.IsDark);
        var back = new Rgb(surface.R, surface.G, surface.B);

        _paper = new LinearGradientBrush { StartPoint = new Windows.Foundation.Point(0, 0), EndPoint = new Windows.Foundation.Point(0, 1) };
        _paper.GradientStops.Add(new GradientStop { Color = NoteStyling.ToColor(NoteSurface.Over(back, new Rgb(255, 255, 255), top)), Offset = 0 });
        _paper.GradientStops.Add(new GradientStop { Color = surface, Offset = NoteSurface.PaperMiddleOffset });
        _paper.GradientStops.Add(new GradientStop { Color = NoteStyling.ToColor(NoteSurface.Over(back, new Rgb(0, 0, 0), bottom)), Offset = 1 });

        return _paper;
    }

    private int _decodeTier;

    private string? _loadedPath;

    /// <summary>那格"图没了/读不出"的字。不复用 <see cref="_label"/>：它挂在 <c>_textHost</c> 里，
    /// 而 WinUI 不许把还有父亲的元素再挂一次——10-23 拿一块丢了图的板子开屏，就是这一句把窗口
    /// 直接打死（<c>Border.set_Child</c> 抛 ArgumentException）。</summary>
    private TextBlock? _missing;

    public NodeVisual(CanvasNode node, Func<string, string?> resolveAsset)
    {
        NodeId = node.Id;
        Kind = node.Kind;

        UseLayoutRounding = false;
        IsHitTestVisible = true;

        // 折叠走平移，不走 Canvas 位置：那条"不用 render transform"的规矩针对的是缩放（字形会糊），
        // 平移不改缩放，PanLayer 自己就是靠一枚 TranslateTransform 走的。
        RenderTransform = _fold;

        HorizontalContentAlignment = HorizontalAlignment.Stretch;
        VerticalContentAlignment = VerticalAlignment.Stretch;

        _label = new TextBlock
        {
            TextWrapping = TextWrapping.Wrap,
            FontFamily = NoteStyling.NoteFont,
        };

        _textHost.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        _textHost.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        Grid.SetRow(_md, 0);
        Grid.SetRow(_chips, 1);
        _textHost.Children.Add(_md);
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
            _textHost.Children.Add(_label);
        }

        _image = new Image
        {
            Stretch = Stretch.Uniform,
            IsHitTestVisible = false,
        };

        ShowText = Kind != CanvasNodeKind.File || !LooksLikeImage(node.FilePath);

        _surface.Child = ShowText ? _textHost : _image;

        var paint = new Grid();
        paint.Children.Add(_bands);
        paint.Children.Add(_surface);
        paint.Children.Add(_fadeAbove);
        paint.Children.Add(_fadeBelow);
        paint.Children.Add(_press);
        paint.Children.Add(_groupHalo);
        paint.Children.Add(_frame);
        paint.Children.Add(_grab);
        paint.Children.Add(_namePlate);
        // Once, here. It used to hang off Update behind a `!_dueBar.IsLoaded` guard, which is not a
        // membership test: a card updated twice before the tree has loaded — which is what a rebuild
        // inside one synchronous pass does — took IsLoaded as false and added a rectangle that was
        // already a child, and XAML answers that with 0x800F1000. Visibility is what Update changes.
        paint.Children.Add(_dueBar);
        Content = paint;

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

    /// <summary>自检那一发读的三件事：这格一共几行、视窗装得下几行、现在从第几行开始。</summary>
    internal (int Total, int Visible, int First, bool HasMore, bool Blocks) WindowState()
        => (_totalLines, _visibleLines, _firstLine, _hasMore, _md.Visibility == Visibility.Visible);

    /// <summary>
    /// 自检那一发读的组的门形：框内还吃不吃鼠标、环带与名牌开没开、名牌抬到了顶边上方几像素。
    /// </summary>
    internal (bool SurfaceHittable, Visibility Grab, Visibility Plate, double PlateLift, string? Alert) GroupChrome()
        => (_surface.IsHitTestVisible, _grab.Visibility, _namePlate.Visibility, _nameLift.Y, _groupAlert);

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

            if (fade && _groupHalo.Opacity > 0)
            {
                FadeHalo(0);
            }
            else
            {
                _groupHalo.Opacity = 0;
            }

            return;
        }

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
            _groupHalo.Opacity = 1;
        }
    }

    private void FadeHalo(double to)
    {
        var animation = new Microsoft.UI.Xaml.Media.Animation.DoubleAnimation
        {
            To = to,
            Duration = TimeSpan.FromMilliseconds(Motion.BaseMs),
        };

        var board = new Microsoft.UI.Xaml.Media.Animation.Storyboard { FillBehavior = Microsoft.UI.Xaml.Media.Animation.FillBehavior.HoldEnd };

        Microsoft.UI.Xaml.Media.Animation.Storyboard.SetTarget(animation, _groupHalo);
        Microsoft.UI.Xaml.Media.Animation.Storyboard.SetTargetProperty(animation, "Opacity");
        board.Children.Add(animation);
        board.Begin();
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
        bool Revealed = false)
    {
        public static readonly Display None = new(null, null, false, null, null);
    }

    /// <summary>折叠的位移走这一枚变换：它不改 Canvas 上的位置，所以撤销一次筛选就是把它归零。</summary>
    private readonly TranslateTransform _fold = new();

    public void Update(CanvasNode node, BoardViewState view, double rasterizationScale, Display? display = null)
    {
        display ??= Display.None;

        _windowNode = node;
        _windowView = view;
        _windowScale = rasterizationScale;

        var surface = NoteStyling.SurfaceFor(node.Color);
        var ink = NoteStyling.InkFor(node.Color, surface);
        var isGroup = Kind == CanvasNodeKind.Group;

        // A group is a container: no fill, no paper, only a dashed frame and its name.
        _frame.Visibility = isGroup ? Visibility.Visible : Visibility.Collapsed;
        _frame.Stroke = new SolidColorBrush(NoteStyling.FallbackLine());
        _surface.Background = isGroup ? new SolidColorBrush(Colors.Transparent) : PaperFor(surface);
        _surface.BorderBrush = new SolidColorBrush(NoteStyling.FallbackLine());

        // Once per node per frame: the bar, the readout and the filter all read this same result,
        // and a pan re-runs it for every note on the board.
        var marks = Tags.Read(node.Text);
        var due = NoteMarks.StateOf(marks, view.Today);

        // 筛选是视图层的事：被滤掉的格子压暗但仍在原处，绝不移动、也不改文档（所以不进撤销栈）。
        // 组里那些没命中的是唯一的例外——它们被**折进**命中的那一格背后，但那也只是这一枚变换。
        var shows = display.Revealed || !view.Filtering || NoteMarks.Shows(due, marks.Tags, view.TagFilter, view.DueFilter);

        Opacity = display.FoldedAway ? 0d : shows ? 1d : 0.22d;
        _label.Foreground = new SolidColorBrush(isGroup ? NoteStyling.InkFor(null, NoteStyling.SurfaceFor(null)) : ink);

        // 组框在筛选时画在"看得见的那几格"的包围盒上——框跟着便签走，而文档里那份一个字节不动。
        var shown = display.ShownFrame ?? new Grouping.Rect(node.Id, node.X, node.Y, node.Width, node.Height);

        Canvas.SetLeft(this, view.ToScreenX(shown.X));
        Canvas.SetTop(this, view.ToScreenY(shown.Y));
        Width = Math.Max(2, shown.Width * view.Zoom);
        Height = Math.Max(2, shown.Height * view.Zoom);

        // 色条的高度必须取**这一帧**的 Height。原来它排在 Width/Height 之前，缩放后拿到的是上一帧的
        // 高度，于是 2px 条短一截、又被 Grid 居中——上下各缺一块（他 10-24 报的"左缘没盖满"）。
        if (NoteMarks.BarFor(due, NoteStyling.IsDark) is { } tint && !isGroup)
        {
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
        _surface.Clip = new RectangleGeometry { Rect = new Windows.Foundation.Rect(0, 0, Width, Height) };
        Clip = null;

        var type = NoteTypography.For(view.FontSizeFor(node.Id));

        // A picture fills its card; only text gets the inset. The three numbers (size, leading,
        // padding) come from one NoteType so they cannot drift apart.
        _surface.Padding = ShowText ? new Thickness(Math.Clamp(type.Padding * view.Zoom, 0, 60)) : new Thickness(0);

        UpdateEdge(node, view);

        if (isGroup)
        {
            PaintGroupPlate(node, view, type, display);
        }
        else if (ShowText)
        {
            var leading = Math.Max(4, type.Leading * view.Zoom);
            var inset = Math.Clamp(type.Padding * view.Zoom, 0, 60);
            var band = NoteType.ChipBand(view.Zoom);

            _label.FontSize = Math.Max(4, type.Size * view.Zoom);
            _label.LineHeight = leading;

            var body = Kind == CanvasNodeKind.Text ? marks.Body : DisplayText(node);

            // 折进去的东西必须有一句"这里藏着几格"，否则筛选看起来像把便签删了。这句话长在框自己的
            // 标题那一格里，不新发明一种控件：同一件东西（组的标题）多说一句，而不是多一枚牌子。
            if (display.Suffix is { Length: > 0 } suffix)
            {
                body += suffix;
            }


            // 不许出现"半行字被横切"。卡片高度可能是在网页那侧量出来的——同一串字在
            // 微软雅黑下会比 Chrome 多占一行；也可能被手柄压小。两种情况下都只允许整行截断，
            // 并用省略号说明"后面还有"，而不是让最后一行露出半截。
            var room = Math.Max(0, Height - (inset * 2));

            // 行数按**世界尺寸**取整，或至少给浮点留一个 epsilon：所有量都乘过 zoom，
            // 76 高、17 内距、21 行高在 160% 时得到 2.0000000000000004 还是 1.9999999999999998
            // 全看这一发的乘除顺序——后者 floor 成 1，第二行整行消失，屏幕上看不出还有内容。
            // 他 10-25 报的"编辑完不长大、渐隐也没提示"就是这一格（同样 42/21 在别的缩放是 2）。
            const double lineEpsilon = 1e-6;
            var withChips = (int)Math.Floor(((room - band) / leading) + lineEpsilon);

            IReadOnlyList<NoteMarks.Mark> chips = isGroup ? Array.Empty<NoteMarks.Mark>() : NoteMarks.Chips(marks, view.Today);

            // 卡片小得装不下一行正文加一排牌子时，先保正文：色条还在，时间这件事不会变成沉默。
            var showChips = chips.Count > 0 && withChips >= 1;

            _chips.Visibility = showChips ? Visibility.Visible : Visibility.Collapsed;

            if (showChips)
            {
                PaintChips(chips, view.Zoom, ink);
            }

            // The room the words actually get, in screen pixels: the marks band is chrome and sits
            // under them, so it comes off before anything asks how many lines fit.
            var roomText = Math.Max(0, showChips ? room - band : room);

            _visibleLines = Math.Max(1, showChips ? withChips : (int)Math.Floor((room / leading) + lineEpsilon));

            var sourceLines = body.Split('\n');

            _totalLines = Math.Max(1, sourceLines.Length);

            // 文字换了（或换了一格）就回到第一行：谁都不该在一段新内容中间接着上次的位置读。
            if (!string.Equals(_windowKey, $"{node.Id}|{body}", StringComparison.Ordinal))
            {
                _windowKey = $"{node.Id}|{body}";
                _firstLine = 0;
            }

            _firstLine = Math.Clamp(_firstLine, 0, Math.Max(0, _totalLines - 1));

            var innerWidth = Math.Max(20, (node.Width * view.Zoom) - (inset * 2));

            var blocks = Kind == CanvasNodeKind.Text && Markdown.LooksLikeMarkdown(marks.Body);

            // 手动拉高以后窗口必须说实话：整段现在放得下了，就回到第一行——"↑ 更多"亮着
            // 而下面全是空白，比句末三个点更糟（他 10-22 报的第四条）。放不下但底下露着
            // 空档时，窗口贴住底：不许留"显示不完也滚不动"的半屏空白。
            if (_firstLine > 0)
            {
                if (blocks)
                {
                    var allLines = Markdown.Lines(marks.Body);

                    if (MarkdownPaint.Count(type, view.Zoom, allLines, roomText) >= allLines.Count)
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

            _label.Visibility = blocks ? Visibility.Collapsed : Visibility.Visible;
            _md.Visibility = blocks ? Visibility.Visible : Visibility.Collapsed;

            if (blocks)
            {
                var lines = Markdown.Lines(marks.Body).Skip(_firstLine).ToList();
                var count = MarkdownPaint.Count(type, view.Zoom, lines, roomText);

                _hasMore = lines.Count > count;
                // 窗口走了第几行必须进这把钥匙：以前只按"画了几行"缓存，
                // 于是滚完两行以后这里判定"没变"，屏幕上还是一模一样的那一屏字。
                var key = $"{marks.Body}|{type.Size}|{view.Zoom:0.####}|{count}|{_firstLine}|{NoteStyling.IsDark}|{ink}";

                if (key != _mdKey)
                {
                    _mdKey = key;
                    _md.Children.Clear();

                    foreach (var element in MarkdownPaint.Lines(
                        type,
                        view.Zoom,
                        lines,
                        new SolidColorBrush(ink),
                        new SolidColorBrush(NoteStyling.ToColor(Badge.Muted(NoteStyling.IsDark))),
                        new SolidColorBrush(NoteStyling.WithAlpha(Badge.LineColour(NoteStyling.IsDark), Badge.LineAlpha(NoteStyling.IsDark))),
                        roomText))
                    {
                        _md.Children.Add(element);
                    }
                }
            }
            else
            {
                _label.MaxLines = _visibleLines;
                _label.TextTrimming = TextTrimming.WordEllipsis;
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

        UpdateOverflowFades(node, view);

        // Update 每拍都把 _frame 的描边按默认虚线铺回去；警示态（组钮拖那一拍）要压过它。
        ApplyGroupAlert();
    }

    /// <summary>
    /// 组只画两样东西：压住顶边左上角的<b>名牌</b>，和沿虚线铺开的<b>环带</b>。框内不接鼠标，
    /// 所以单击组里的便签落在那枚便签上（他 10-22 定的语义）；点名牌或环带才＝选中整组。
    /// </summary>
    private void PaintGroupPlate(CanvasNode node, BoardViewState view, NoteType type, Display display)
    {
        var zoom = view.Zoom;

        _label.FontSize = Math.Max(10, type.Size * zoom);
        _label.LineHeight = Math.Max(12, type.Leading * zoom);

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
    private void UpdateOverflowFades(CanvasNode node, BoardViewState view)
    {
        var textCard = Kind == CanvasNodeKind.Text;
        var shown = view.Zoom >= 0.5;
        var above = textCard && shown && _firstLine > 0;
        var below = textCard && shown && _hasMore;

        _fadeAbove.Visibility = above ? Visibility.Visible : Visibility.Collapsed;
        _fadeBelow.Visibility = below ? Visibility.Visible : Visibility.Collapsed;

        if (!above && !below)
        {
            return;
        }

        var surface = NoteStyling.SurfaceFor(node.Color);
        var solid = Color.FromArgb(255, surface.R, surface.G, surface.B);
        var clear = Color.FromArgb(0, surface.R, surface.G, surface.B);
        var height = Math.Clamp(22 * view.Zoom, 12, 34);

        if (above)
        {
            ApplyFade(_fadeAbove, height, solid, clear);
        }

        if (below)
        {
            ApplyFade(_fadeBelow, height, clear, solid);
        }
    }

    private static void ApplyFade(Microsoft.UI.Xaml.Shapes.Rectangle fade, double height, Color from, Color to)
    {
        fade.Height = height;

        if (fade.Fill is LinearGradientBrush brush)
        {
            brush.GradientStops.Clear();
            brush.GradientStops.Add(new GradientStop { Color = from, Offset = 0 });
            brush.GradientStops.Add(new GradientStop { Color = to, Offset = 1 });
        }
    }

    /// <summary>
    /// Paints the three paper layers, or the single bottom line when the zoom is below the
    /// shadow budget. Rebuilds only when the geometry actually changed: a pan moves the whole
    /// control and must not re-make N brushes per frame.
    /// </summary>
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

        var key = $"{view.Zoom:0.###}|{node.Width:0.#}|{node.Height:0.#}|{NoteStyling.IsDark}";

        if (!NoteSurface.DrawsShadow(view.Zoom))
        {
            // tldraw's own degradation is two things at once: the shadow goes away AND one bottom
            // line appears. Dropping the shadow without the line leaves a card with no edge at all.
            if (_shadowKey != "line")
            {
                _shadowKey = "line";
                _bands.Children.Clear();
                _press.Visibility = Visibility.Collapsed;
                _surface.BorderThickness = new Thickness(0, 0, 0, Math.Max(1, NoteSurface.FallbackLineWidth * view.Zoom));
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
        foreach (var band in NoteSurface.BandsFor(node.Id, view.Zoom, node.Width, node.Height))
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

        var press = NoteSurface.BandsFor(node.Id, view.Zoom, node.Width, node.Height)
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
    private void PaintChips(IReadOnlyList<NoteMarks.Mark> chips, double zoom, Color ink)
    {
        var key = string.Join("/", chips.Select(chip => $"{chip.Source}={chip.Label}"));

        if (key == _chipKey)
        {
            return;
        }

        _chipKey = key;
        _chips.Children.Clear();

        // 形状不在这里定。卡片这一排和编辑器里那一排走同一枚渲染器（ChromeStyling.Chip）——
        // 这里以前是第二份实现，画的是 1px 实线方框，正是他打回的「一个方框看起来很廉价」，
        // 而编辑器里那份又是另一种画法。一处定义，两处同形。
        foreach (var chip in chips)
        {
            var source = chip.Source;

            _chips.Children.Add(ChromeStyling.Chip(
                chip,
                NoteType.ChipFont(zoom),
                ink,
                () => RemoveMark?.Invoke(NodeId, source),
                () => EditMark?.Invoke(NodeId, source)));
        }
    }

    private static string DisplayText(CanvasNode node) => node.Kind switch
    {
        CanvasNodeKind.Text => node.Text ?? string.Empty,
        CanvasNodeKind.Link => node.Url ?? node.Label ?? string.Empty,
        CanvasNodeKind.Group => node.Label ?? string.Empty,
        CanvasNodeKind.File => node.FilePath ?? string.Empty,
        _ => string.Empty,
    };

    private static bool LooksLikeImage(string? path)
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
