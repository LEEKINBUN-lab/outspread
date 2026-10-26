using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Outspread.Core;
using Windows.UI;

namespace Outspread.Board;

/// <summary>
/// WinUI brushes for the selection chrome and the floating panels, rebuilt when the theme flips.
/// </summary>
/// <remarks>
/// The colours themselves live in <see cref="SelectionChrome"/> and <see cref="Chrome"/>; this class
/// only carries them into a toolkit that wants brush objects. It is not a second source of truth —
/// every value here is a call into Core, so a test on the Core side is a test on what gets painted.
/// </remarks>
internal static class ChromeStyling
{
    private static bool? _built;

    public static SolidColorBrush Ring { get; private set; } = null!;
    public static SolidColorBrush SelectionFill { get; private set; } = null!;
    public static SolidColorBrush InnerContrast { get; private set; } = null!;
    public static SolidColorBrush HandleFace { get; private set; } = null!;
    public static SolidColorBrush HandleEdge { get; private set; } = null!;
    public static SolidColorBrush MarqueeFill { get; private set; } = null!;
    public static SolidColorBrush Snap { get; private set; } = null!;
    public static SolidColorBrush PanelFill { get; private set; } = null!;
    public static SolidColorBrush PanelSolid { get; private set; } = null!;
    public static SolidColorBrush PanelLine { get; private set; } = null!;

    public static void Ensure(bool dark)
    {
        if (_built == dark)
        {
            return;
        }

        _built = dark;

        Ring = Opaque(SelectionChrome.Ring(dark));
        SelectionFill = Tinted(SelectionChrome.Fill(dark), SelectionChrome.SelectionFillAlpha(dark));
        InnerContrast = Tinted(SelectionChrome.InnerContrast(dark), SelectionChrome.InnerContrastAlpha(dark));
        HandleFace = Opaque(SelectionChrome.HandleFace(dark));
        HandleEdge = Tinted(new Rgb(0, 0, 0), SelectionChrome.HandleEdgeAlpha(dark));
        MarqueeFill = Tinted(SelectionChrome.Fill(dark), SelectionChrome.MarqueeFillAlpha(dark));
        Snap = Opaque(SelectionChrome.Snap(dark));
        PanelFill = Tinted(Chrome.PanelFill(dark), Chrome.PanelAlpha);
        var floor = Chrome.PanelFill(dark);
        var opaque = Color.FromArgb(255, floor.R, floor.G, floor.B);

        // 贴在板面上的数据表不用浮层那档半透明：94% 的白底下会透出便签文字，行与行之间全是影子。
        PanelSolid = new SolidColorBrush(opaque);

        // WinUI 3 的 AcrylicBrush 没有 BackgroundSource（那是 UWP 的口子），所以这里只能给出
        // 色与不透明度 + 一个兜底色；到底有没有真的模糊，靠两次出图对比，不靠属性名。
        PanelSurface = AcrylicWanted()
            ? new AcrylicBrush
            {
                TintColor = opaque,
                TintOpacity = Chrome.PanelAlpha,
                FallbackColor = opaque,
            }
            : PanelFill;
        PanelLine = Tinted(Chrome.PanelLineColour(dark), Chrome.PanelLineAlpha(dark));
    }

    /// <summary>
    /// Dresses a floating surface the one way floating surfaces are dressed: rounded, translucent,
    /// hairlined, lifted by a shadow, and with the sheet's own air around the rows. Nothing on this app
    /// draws a second corner number, a second inset, or a second lift.
    /// </summary>
    /// <remarks>
    /// This is the only place a floating surface's chrome is written. The key sheet, the filter panel,
    /// the filter banner and the say-chip all come through here, and the right-click menu is what they
    /// are dressed to look like — so a surface that wants a different skin has to change this method,
    /// which is the point.
    /// </remarks>
    public static void ApplyFloatingChrome(Border host)
    {
        host.CornerRadius = new CornerRadius(Chrome.Radius);
        host.Background = PanelSurface;
        host.BorderBrush = PanelLine;
        host.BorderThickness = new Thickness(1);
        host.Padding = new Thickness(Sheet.OuterPx);

        Raise(host);
    }

    /// <summary>
    /// 抬起来这件事：这个投影里没有 <c>ContainerVisual.Shadows</c>（UWP 那套合成投影集合没有过到
    /// WinUI 3，编译器当场拒绝那个成员），过来的是 <c>UIElement.Shadow</c> ＋ <c>ThemeShadow</c>——
    /// 框架自己的那一层抬起，影落在 <see cref="ShadowReceiver"/> 上。
    /// 挂不上要说出来：浮层一旦没有投影，它就又变回"贴在板子上的一块白"，那正是被打回的样子。
    /// </summary>
    private static void Raise(UIElement host)
    {
        if (ShadowReceiver is not { } receiver)
        {
            // 穿皮是每一帧都会走的路（StyleBadge 挂在 arrange 上），所以这一句只响一次。
            if (!_liftComplaint)
            {
                _liftComplaint = true;

                Diagnostics.Write($"浮层抬不起来：还没定投影落在谁身上（{host.GetType().Name} 起，这一版所有浮层都没有影）");
            }

            return;
        }

        // 穿皮是每一帧都会走的路（StyleBadge 挂在 arrange 上），所以已经抬起的就直接返回。
        if (host.Shadow is ThemeShadow && host.Translation.Z == (float)Lift.ZPx)
        {
            return;
        }

        if (host.Shadow is not ThemeShadow)
        {
            var cast = new ThemeShadow();

            cast.Receivers.Add(receiver);
            host.Shadow = cast;
        }

        host.Translation = new System.Numerics.Vector3(0, 0, (float)Lift.ZPx);
    }

    private static bool _liftComplaint;

    /// <summary>
    /// 这一枚浮层上抬起来了没有（读到 1＝挂了影且真的有了高度）。自检要现读它，因为"皮是同一套"
    /// 这句话里最容易悄悄掉的就是这一件——它不改框高，只改看着像不像浮起来。
    /// </summary>
    public static int ShadowCount(UIElement host)
        => host.Shadow is ThemeShadow && host.Translation.Z > 0 ? 1 : 0;

    /// <summary>ThemeShadow 只往 receivers 上画影，所以要有一枚"谁的地面接这个影"——板子自己。
    /// 由界面在建窗时指一次。</summary>
    public static UIElement? ShadowReceiver { get; set; }

    /// <summary>
    /// The panel's own surface: an <see cref="AcrylicBrush"/> over the prototype's <c>--panel</c>.
    /// Whether this actually blurs anything on a desktop (unpackaged) window is an experiment, not an
    /// assumption — <c>HostBackdrop</c> samples what is behind the <i>window</i>, and this window is
    /// opaque, so the honest fallback is the flat translucent fill. See docs/port-parity.md.
    /// </summary>
    public static Brush PanelSurface { get; private set; } = null!;

    /// <summary>
    /// The one renderer for a mark on a card — the card's bottom-left rail and the editor's strip both
    /// come through here, because they were two renderers of the same thing and that is how a mark ended
    /// up a bordered square on one surface and a borderless one on the other.
    /// <para>
    /// Shape is the 2026-10-12 research's verdict, recorded with its sources on the tokens themselves
    /// (<c>NoteType.Chip*</c>): a **date** is a state dot plus the words, no container; a **tag** is a
    /// pill (radius = half the band) with a soft fill and no stroke. Nothing square, nothing outlined —
    /// no design system in the set draws a mark with a square corner, and the two families that look
    /// closest to what we had (a 1px box) are exactly the ones that deleted the border.
    /// </para>
    /// <para>
    /// The one risk the research named for going container-free is losing the click affordance, so the
    /// pill is not gone but *unpainted at rest*: hovering fills it and grows the ×. Fluent reserves a
    /// transparent stroke the same way.
    /// </para>
    /// </summary>
    /// <summary>
    /// 卡片/编辑器上的一枚标记牌。点牌身＝编辑这枚标记，只有右侧的 × 才删它——他不是要
    /// "点哪儿都删"：× 显示出来却点不到，等于没有。
    /// <para>
    /// 牌宽＝那几个字的自然宽，× 不占位（<paramref name="surface"/> 只用来给 × 算一块盖得住字的底）。
    /// </para>
    /// </summary>
    public static Border Chip(NoteMarks.Mark mark, double size, Color ink, Color surface, Action onDelete, Action onEdit)
    {
        var scale = size / NoteType.ChipFontPx;
        var self = new Rgb(ink.R, ink.G, ink.B);

        // 完成状态那一枚不是"又一张标签"：绿＝已完成、红＝未完成，配勾／叉，颜色是满的。
        // 旁的那些标签仍是同一族的灰底墨字——他 10-26 报的"颜色一样、看不出来"就是这一格。
        // 平台自己的成功/错误色（generic.xaml 里 SystemFillColorSuccess/Critical 那一对）：
        // **淡实底 + 深色字与图标**。满色底压在浅卡上太重（他 10-26 报"颜色太深"），
        // 而淡底不是半透明——它是实色，只是浅；对比度 4.79:1 / 4.84:1，都过 AA。
        var dark = NoteStyling.IsDark;

        var stateColour = mark.Done switch
        {
            true => dark ? new Rgb(0x6C, 0xCB, 0x5F) : new Rgb(0x0F, 0x7B, 0x0F),
            false => dark ? new Rgb(0xFF, 0x99, 0xA4) : new Rgb(0xC4, 0x2B, 0x1C),
            null => self,
        };

        var stateFill = mark.Done switch
        {
            true => dark ? new Rgb(0x39, 0x3D, 0x1B) : new Rgb(0xDF, 0xF6, 0xDD),
            false => dark ? new Rgb(0x44, 0x27, 0x26) : new Rgb(0xFD, 0xE7, 0xE9),
            null => self,
        };

        var tinted = mark.Done is not null;

        // × 压在牌尾的那块底：悬停底色压在卡面**之后**的实色。用实色而不是半透明，是因为它的活
        // 就是把底下那几个字盖掉——半压上去只会变成"× 叠在字上"糊成一团。
        var crossShroud = OpaqueOver(surface, NoteStyling.ToColor(stateFill), tinted ? 1 : NoteType.ChipHoverAlpha);

        var body = tinted ? NoteStyling.ToColor(stateColour) : NoteStyling.WithAlpha(self, NoteType.ChipInkAlpha);
        var rest = tinted
            ? NoteStyling.ToColor(stateFill)
            : NoteStyling.WithAlpha(stateColour, mark.IsDate ? 0 : NoteType.ChipFillAlpha);
        var hover = tinted ? NoteStyling.ToColor(stateFill) : NoteStyling.WithAlpha(stateColour, NoteType.ChipHoverAlpha);

        var inner = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = NoteType.ChipGapPx * scale,
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Left,
            // 左右内距挂在这一排字上，不挂整枚牌：× 那一块要能顶到胶囊的右沿。
            Padding = new Thickness(NoteType.ChipPadX * scale, 0, NoteType.ChipPadX * scale, 0),
        };

        if (mark.Done is { } finished)
        {
            // 形状同样来自官方库（勾／叉的 20_regular），不自绘。
            if (FluentIcons.Glyph(finished ? FluentIcons.CheckmarkFilled : FluentIcons.DismissFilled, 14 * scale, body) is { } glyph)
            {
                inner.Children.Add(glyph);
            }
        }

        if (mark.IsDate)
        {
            // 点用左缘那条色条同一枚状态色：同一件事在一张卡上只说一种颜色，不然读起来像两个状态。
            var state = NoteMarks.BarFor(mark.State, NoteStyling.IsDark);

            inner.Children.Add(new Microsoft.UI.Xaml.Shapes.Ellipse
            {
                Width = NoteType.ChipDotPx * scale,
                Height = NoteType.ChipDotPx * scale,
                Fill = new SolidColorBrush(state is { } rgb ? NoteStyling.ToColor(rgb) : body),
                VerticalAlignment = VerticalAlignment.Center,
            });
        }

        var label = new TextBlock
        {
            Text = mark.Label,
            FontFamily = NoteStyling.NoteFont,
            FontSize = size,
            LineHeight = 16 * scale,
            Foreground = new SolidColorBrush(body),
            VerticalAlignment = VerticalAlignment.Center,
        };

        // 原型写了 font-variant-numeric: tabular-nums；WinUI 3 的 TextBlock 没有这一面（Typography 类不在投影里），

        var cross = new TextBlock
        {
            Text = "×",
            FontFamily = NoteStyling.NoteFont,
            FontSize = size,
            Foreground = new SolidColorBrush(body),
            VerticalAlignment = VerticalAlignment.Center,
        };

        // × 自己一块命中区：点它才删，点牌身是编辑。
        // 它**不占牌宽**（10-26 他点的第二格）：牌子宽度只有那几个字，× 悬停时压在牌子右端
        // 那一小块上——不常驻，所以不挡正常浏览；不占位，所以一排牌子没有空档。
        // 它不"长出来"：占位与不占位之间不切换宽度，切的只有透明度。
        var crossHost = new Border
        {
            Child = cross,
            Background = new SolidColorBrush(crossShroud),
            CornerRadius = new CornerRadius(0, NoteType.ChipRadiusPx * scale, NoteType.ChipRadiusPx * scale, 0),
            Padding = new Thickness(5 * scale, 0, 4 * scale, 0),
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Stretch,
            Opacity = 0,
            IsHitTestVisible = false,
        };

        inner.Children.Add(label);

        // 牌面是一层 Grid：字在左，× 盖在右端。两枚互不计入对方的宽度。
        var face = new Grid();

        face.Children.Add(inner);
        face.Children.Add(crossHost);

        var border = new Border
        {
            Child = face,
            BorderThickness = new Thickness(0),
            Background = new SolidColorBrush(rest),
            CornerRadius = new CornerRadius(NoteType.ChipRadiusPx * scale),
            Height = NoteType.ChipBandPx * scale,
            // 至少留两个字那么宽：不然"n7"这种短牌一悬停就被 × 整枚盖没。
            MinWidth = (NoteType.ChipPadX * 2 + NoteType.ChipCrossHitPx) * scale,
        };

        ToolTipService.SetToolTip(border, mark.IsDate ? "点一下＝改这个日期；点 × 去掉" : "点一下＝改这个标签；点 × 去掉");

        border.PointerEntered += (_, _) =>
        {
            border.Background = new SolidColorBrush(hover);
            crossHost.Opacity = 1;
            crossHost.IsHitTestVisible = true;
        };
        border.PointerExited += (_, _) =>
        {
            border.Background = new SolidColorBrush(rest);
            crossHost.Opacity = 0;
            crossHost.IsHitTestVisible = false;
        };
        border.PointerPressed += (_, args) =>
        {
            // 这一发不归拖动：牌子是卡片上的控件，按到它就是要点它。
            args.Handled = true;

            onEdit();
        };

        crossHost.PointerPressed += (_, args) =>
        {
            // 吃掉这一发，牌身那一层不能再收到：点 × 只删，不进编辑。
            args.Handled = true;

            onDelete();
        };

        return border;
    }

    /// <summary>半透明色压在实色面上以后的实色。× 那一小块要有块盖得住字的底，而这底必须仍是
    /// 牌子那一档颜色——直接把悬停色画成不透明会比牌子深一块，看着像牌尾贴了个补丁。</summary>
    private static Color OpaqueOver(Color surface, Color overlay, double alpha)
    {
        var t = Math.Clamp(alpha, 0, 1);

        return Color.FromArgb(
            255,
            (byte)Math.Round(Math.Clamp(surface.R + ((long)overlay.R - surface.R) * t, 0, 255)),
            (byte)Math.Round(Math.Clamp(surface.G + ((long)overlay.G - surface.G) * t, 0, 255)),
            (byte)Math.Round(Math.Clamp(surface.B + ((long)overlay.B - surface.B) * t, 0, 255)));
    }

    /// <summary>
    /// Acrylic is behind a switch, and the switch now defaults to <b>off</b> because of what the two
    /// shots measured. The same filter panel was shot twice at the same camera, once with each fill,
    /// and the panel's own blank right margin read: flat 255/254, acrylic 243–244, board floor behind
    /// it 249 with the dot grid present. The prototype's panel is <c>rgba(255,255,255,.94)</c> over a
    /// 249 floor, which composites to 254.7 — the flat fill is 0.3 levels off, acrylic is 11 off, and
    /// the dot grid does not come through either way, so on this channel acrylic buys a grey panel and
    /// no frost.
    /// <para>
    /// What the capture cannot see: if the desktop window manager composites the wallpaper behind the
    /// live window, a person looking at the screen sees something a RenderTargetBitmap of the app's
    /// own tree never records. <c>TANKAI_ACRYLIC=1</c> is the way to look at that variant without
    /// rebuilding; this comment is the reason it is not the default.
    /// </para>
    /// </summary>
    private static bool AcrylicWanted()
        => string.Equals(Environment.GetEnvironmentVariable("TANKAI_ACRYLIC"), "1", StringComparison.OrdinalIgnoreCase);

    private static SolidColorBrush Opaque(Rgb rgb) => new(Color.FromArgb(255, rgb.R, rgb.G, rgb.B));

    private static SolidColorBrush Tinted(Rgb rgb, double alpha)
        => new(Color.FromArgb((byte)Math.Round(alpha * 255), rgb.R, rgb.G, rgb.B));

    /// <summary>
    /// 基准那一发的读数：右键菜单是浮层的基准，而它的皮不是我们写的，是 WinUI 主题里那几枚资源。
    /// 把它们的现值打出来，是为了让"我们的令牌等于基准"这句话有一边的读数撑着——而不是只量了自己。
    /// </summary>
    public static string DescribeFlyoutBaseline(FrameworkElement root)
    {
        string[] keys =
        [
            "MenuFlyoutPresenterBackground",
            "MenuFlyoutPresenterBorderBrush",
            "MenuFlyoutPresenterBorderThemeThickness",
            "MenuFlyoutPresenterPadding",
            "MenuFlyoutPresenterItemPadding",
            "FlyoutPresenterBackground",
            "FlyoutPresenterBorderThemeThickness",
            "FlyoutPresenterPadding",
            "OverlayCornerRadius",

            // 动画那一发：菜单弹出来那一下用的是主题动画资源，能读到就把同一个 Begin 到浮层上，
            // 读不到就退回 PopupThemeTransition（平台给"这是一层弹出来的东西"的那个 transition）。
            "PopupMenuThemeAnimation",
            "PopupMenuThemeAnimationFromBottom",
            "PointerDownPopupThemeAnimation",
        ];

        var parts = new List<string>();

        foreach (var key in keys)
        {
            parts.Add($"{key.Replace("MenuFlyoutPresenter", "menu.").Replace("FlyoutPresenter", "flyout.")}={Show(Lookup(root, key))}");
        }

        return string.Join("  ", parts);

        // 这个投影里 FrameworkElement 没有 TryFindResource/FindResource，所以自己走一遍：
        // 元素往上的资源链，再是应用那一份（框架的泛型字典在运行时并进这一条里）。
        static object? Lookup(FrameworkElement root, string key)
        {
            for (DependencyObject? node = root; node is not null; node = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetParent(node))
            {
                if (node is FrameworkElement element && element.Resources is { } dictionary && dictionary.TryGetValue(key, out var found))
                {
                    return found;
                }
            }

            if (Application.Current?.Resources is { } app && app.TryGetValue(key, out var fromApp))
            {
                return fromApp;
            }

            return null;
        }

        static string Show(object? value) => value switch
        {
            null => "未定义",
            SolidColorBrush brush => $"#{brush.Color.A:X2}{brush.Color.R:X2}{brush.Color.G:X2}{brush.Color.B:X2}",
            Color color => $"#{color.A:X2}{color.R:X2}{color.G:X2}{color.B:X2}",
            Thickness t => $"{t.Left:0.##},{t.Top:0.##},{t.Right:0.##},{t.Bottom:0.##}",
            CornerRadius r => $"{r.TopLeft:0.##},{r.TopRight:0.##},{r.BottomRight:0.##},{r.BottomLeft:0.##}",
            double d => d.ToString("0.##"),
            _ => value.GetType().Name,
        };
    }
}
