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
        IsHitTestVisible = false,
    };

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
    /// The same body, painted as blocks, when it carries markdown: one element per line so a heading
    /// can be taller without the rest of the card inheriting its leading. Only one of
    /// <see cref="_label"/> and this is visible at a time — a note with no markup in it paints exactly
    /// the way it did before this existed.
    /// </summary>
    private readonly StackPanel _md = new() { Visibility = Visibility.Collapsed, Spacing = 0 };

    /// <summary>The card this block was built for. Rebuilding it every frame of a pan would re-take
    /// the layout a card has not changed.</summary>
    private string _mdKey = string.Empty;

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

    public NodeVisual(CanvasNode node, Func<string, string?> resolveAsset)
    {
        NodeId = node.Id;
        Kind = node.Kind;

        UseLayoutRounding = false;
        IsHitTestVisible = true;
        HorizontalContentAlignment = HorizontalAlignment.Stretch;
        VerticalContentAlignment = VerticalAlignment.Stretch;

        _label = new TextBlock
        {
            TextWrapping = TextWrapping.Wrap,
            FontFamily = NoteStyling.NoteFont,
        };

        _textHost.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        _textHost.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        Grid.SetRow(_label, 0);
        Grid.SetRow(_md, 0);
        Grid.SetRow(_chips, 1);
        _textHost.Children.Add(_label);
        _textHost.Children.Add(_md);
        _textHost.Children.Add(_chips);

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
        paint.Children.Add(_press);
        paint.Children.Add(_frame);
        // Once, here. It used to hang off Update behind a `!_dueBar.IsLoaded` guard, which is not a
        // membership test: a card updated twice before the tree has loaded — which is what a rebuild
        // inside one synchronous pass does — took IsLoaded as false and added a rectangle that was
        // already a child, and XAML answers that with 0x800F1000. Visibility is what Update changes.
        paint.Children.Add(_dueBar);
        Content = paint;

        ResolveAsset = resolveAsset;

        if (!ShowText)
        {
            LoadImage(node);
        }
    }

    public string NodeId { get; }

    public CanvasNodeKind Kind { get; }

    public bool ShowText { get; }

    /// <summary>True when the node is a file that could not be found or decoded.</summary>
    public bool IsBroken { get; private set; }

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

    public void Update(CanvasNode node, BoardViewState view, double rasterizationScale)
    {
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

        // 筛选是视图层的事：被滤掉的格子压暗但仍在原处，绝不移动、也不改文档（所以不进撤销栈）。
        Opacity = !view.Filtering || NoteMarks.Shows(due, marks.Tags, view.TagFilter, view.DueFilter) ? 1d : 0.22d;
        _label.Foreground = new SolidColorBrush(isGroup ? NoteStyling.InkFor(null, NoteStyling.SurfaceFor(null)) : ink);

        Canvas.SetLeft(this, view.ToScreenX(node.X));
        Canvas.SetTop(this, view.ToScreenY(node.Y));
        Width = Math.Max(2, node.Width * view.Zoom);
        Height = Math.Max(2, node.Height * view.Zoom);

        // The clip lives on the card now, so the shadow band below it can be painted; text that
        // no longer fits still stays inside the card instead of spilling over a neighbour.
        _surface.Clip = new RectangleGeometry { Rect = new Windows.Foundation.Rect(0, 0, Width, Height) };
        Clip = null;

        var type = NoteTypography.For(view.FontSizeFor(node.Id));

        // A picture fills its card; only text gets the inset. The three numbers (size, leading,
        // padding) come from one NoteType so they cannot drift apart.
        _surface.Padding = ShowText ? new Thickness(Math.Clamp(type.Padding * view.Zoom, 0, 60)) : new Thickness(0);

        UpdateEdge(node, view);

        if (ShowText)
        {
            var leading = Math.Max(4, type.Leading * view.Zoom);
            var inset = Math.Clamp(type.Padding * view.Zoom, 0, 60);
            var band = NoteType.ChipBand(view.Zoom);

            _label.FontSize = Math.Max(4, type.Size * view.Zoom);
            _label.LineHeight = leading;
            _label.Text = Kind == CanvasNodeKind.Text ? marks.Body : DisplayText(node);


            // 不许出现"半行字被横切"。卡片高度可能是在网页那侧量出来的——同一串字在
            // 微软雅黑下会比 Chrome 多占一行；也可能被手柄压小。两种情况下都只允许整行截断，
            // 并用省略号说明"后面还有"，而不是让最后一行露出半截。
            var room = Math.Max(0, Height - (inset * 2));
            var withChips = (int)Math.Floor((room - band) / leading);

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

            var blocks = Kind == CanvasNodeKind.Text && Markdown.LooksLikeMarkdown(marks.Body);

            _label.Visibility = blocks ? Visibility.Collapsed : Visibility.Visible;
            _md.Visibility = blocks ? Visibility.Visible : Visibility.Collapsed;

            if (blocks)
            {
                var lines = Markdown.Lines(marks.Body);
                var count = MarkdownPaint.Count(type, view.Zoom, lines, roomText);
                var key = $"{marks.Body}|{type.Size}|{view.Zoom:0.####}|{count}|{NoteStyling.IsDark}|{ink}";

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
                _label.MaxLines = Math.Max(1, showChips ? withChips : (int)Math.Floor(room / leading));
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

            _chips.Children.Add(ChromeStyling.Chip(chip, NoteType.ChipFont(zoom), ink, () => RemoveMark?.Invoke(NodeId, source)));
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
            _surface.Child = _label;
            _label.Text = $"未找到\n{node.FilePath}";
            return;
        }

        try
        {
            var bitmap = new BitmapImage { DecodePixelWidth = _decodeTier };
            bitmap.UriSource = new Uri(absolute, UriKind.Absolute);

            _image.Source = bitmap;
            _surface.Child = _image;
            IsBroken = false;
            _loadedPath = node.FilePath;
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException)
        {
            // A tile that says what went wrong beats a blank square that says nothing.
            IsBroken = true;
            _surface.Child = _label;
            _label.Text = $"读不出这张图\n{Path.GetFileName(absolute)}";
        }
    }
}
