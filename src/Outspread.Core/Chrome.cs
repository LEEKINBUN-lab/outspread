namespace Outspread.Core;

/// <summary>
/// The one set of numbers every floating surface on the interface is drawn with.
/// </summary>
/// <remarks>
/// <para>
/// The square-corner rule is about <b>paper</b>: a note is a slip on a desk, and a desk slip has no
/// rounded corners and is not translucent. A floating panel is not paper, it is a piece of the
/// interface laid over the desk, so it gets the other treatment — rounded, translucent, lifted.
/// Both decisions live here as one token each rather than as a number written at each call site,
/// which is what "别拿常数糊" objects to.
/// </para>
/// <para>
/// These surfaces are in the window's own visual tree rather than in a <c>Popup</c>, and that is a
/// measurement decision: a <c>Popup</c> is a separate top-level window, so <c>outspread shot</c> —
/// which renders the board element — cannot see one. A panel nobody can photograph is a panel whose
/// look is a claim with no reading behind it.
/// </para>
/// </remarks>
public static class Chrome
{
    /// <summary>Corner radius of a floating panel, in screen pixels.</summary>
    public const double Radius = 8;

    /// <summary>How opaque a floating panel is over the board: enough to read against any card
    /// colour behind it, little enough that the note it covers stays legible.</summary>
    public const double PanelAlpha = 0.94;

    /// <summary>The panel's fill: the prototype's own <c>--panel</c> (white / #1b1e22), held back to
    /// <see cref="PanelAlpha"/>. It was the board floor in the first cut, which made the floating
    /// panel the same colour as what it floats on — the prototype distinguishes the two.</summary>
    public static Rgb PanelFill(bool dark) => Badge.PanelColour(dark);

    /// <summary>Alpha of a floating panel's own edge, so a light panel on a light board still has a border.</summary>
    public static double PanelLineAlpha(bool dark) => dark ? 0.34 : 0.16;

    /// <summary>The edge's colour: ink in the light theme, the board's own near-black pressed with a
    /// light hairline in the dark one.</summary>
    public static Rgb PanelLineColour(bool dark) => dark ? new Rgb(214, 216, 220) : new Rgb(44, 48, 56);
}

/// <summary>
/// The design language for floating surfaces — one set of numbers that the key sheet, the filter
/// panel and the context menu all read, so a row is a row wherever it appears.
/// </summary>
/// <remarks>
/// The base is the right-click menu, because that is the surface he pointed at ("右键菜单的样子其实就
/// 挺不错的"), and its metrics were measured off the screenshot he sent of it (240×219 px): a row
/// pitch of 32, a glyph band of 12–13 (a 14px face), a fill of rgb(251,252,253). What he was
/// objecting to was also measured, in the same way, on the two surfaces he rejected: the filter
/// panel's rows ran 21–30 apart at 13px on pure white, and the key sheet's at 24px on 13. Three
/// rhythms in one product is the "廉价" he named — so nothing here is per-surface.
/// <para>
/// The prototype defines the same numbers as <c>--sheet-*</c> in <c>design/feel.html</c> and every
/// floating surface there reads them; the page now reports 32px for every row it draws.
/// </para>
/// </remarks>
public static class Sheet
{
    /// <summary>
    /// Row pitch, screen pixels. It was 32 — the menu's own band, measured off his screenshot — and the
    /// surfaces were pinned to that on purpose. On 2026-10-12, with the reuse accepted, he asked for
    /// more air ("间距能不能拉大"), so the band is 40: the height a real <c>MenuFlyoutItem</c> measures
    /// when nothing compresses it (read off a realized row this round: 39.62). The live menu stays at
    /// its 32 — it is the system's surface, not ours — but every sheet in this app shares this one
    /// number, so there is still exactly one rhythm.
    /// </summary>
    public const double RowPx = 40;

    /// <summary>The command name's size; the menu's glyph band was 12–13px tall.</summary>
    public const double FontPx = 14;

    /// <summary>A row's own left/right inset.</summary>
    public const double PadX = 12;

    /// <summary>
    /// The sheet's own padding — the air between the panel's edge and the first row's box.
    ///
    /// <para>
    /// This is the number that made the three surfaces disagree even after the rows were pinned: the
    /// menu has 4 (measured: its inner box is 167 tall for five 32px rows, i.e. 4 + 160 + 4), the
    /// filter panel had 0, the key sheet had 16. So a row sat 16px from the edge in one surface,
    /// 12px in another and 28px in the third — same row, three different panels.
    /// </para>
    /// </summary>
    public const double OuterPx = 4;

    /// <summary>Gap between the name column and the key column.</summary>
    public const double GapPx = 14;

    /// <summary>The right column: the menu's own accelerator text is 12px (read off the template of a
    /// live <c>MenuFlyoutItem</c>: 『2』/12px, and off his screenshot of the menu's "双击空白").</summary>
    public const double HintPx = 12;

    /// <summary>A line that is not a command — a section title, a quiet note. The menu has no such
    /// thing, so the nearest thing it does have is a disabled row, and this is that row's ink read off
    /// a live one (rgb 161,161,162 over the sheet's 245). Reproduced rather than re-invented: a second
    /// "header type" is exactly what he was looking at when he said the surfaces are not one component.</summary>
    public static Rgb Quiet(bool dark) => dark ? new Rgb(150, 155, 162) : new Rgb(161, 161, 162);

    /// <summary>The row's own ink: rgb 27,27,27, read twice — off his screenshot of the menu and off a
    /// realized <c>MenuFlyoutItem</c> in this app. It is 3 levels darker than the paper ink, and the
    /// two surfaces are not the same material, so this is not a second copy of that number.</summary>
    public static Rgb Ink(bool dark) => dark ? new Rgb(245, 246, 247) : new Rgb(27, 27, 27);

    /// <summary>The right column's ink: rgb 96,96,96 off his screenshot of "双击空白"／"Ctrl+0".</summary>
    public static Rgb HintInk(bool dark) => dark ? new Rgb(185, 190, 197) : new Rgb(96, 96, 96);

    /// <summary>
    /// A chord is drawn as keys, not as a word — he asked for this on 2026-10-12 ("Ctrl这些文字用按钮图标
    /// 来替换，更美观一点"), one round after the keycaps were dropped to make the sheets read as the menu.
    /// It is the right column's renderer, not a third kind of line: same row, same band, same label.
    /// The numbers are the prototype's, measured off <c>design/feel.html:425</c> (<c>#cheat .keys kbd</c>):
    /// a cap came out 28.72×23 with 22 min-width, 1/6 padding, a 1px edge with a 2px bottom lip, radius 4,
    /// 4 apart, 12px face.
    /// </summary>
    public const double KeyMinWidth = 22;

    public const double KeyHeight = 23;
    public const double KeyPadX = 6;
    public const double KeyPadY = 1;
    public const double KeyRadius = 4;
    public const double KeyGap = 4;
    public const double KeyFont = 12;

    /// <summary>Hover, the only state a row has: it means "this row is clickable", nothing else.</summary>
    public static Rgb Hover(bool dark) => Badge.LineColour(dark);

    public static double HoverAlpha(bool dark) => dark ? 0.07 : 0.045;

    /// <summary>The sheet's fill — the same surface the control cluster sits on.</summary>
    public static Rgb Fill(bool dark) => Badge.PanelColour(dark);
}

/// <summary>
/// How far a floating surface is lifted off the board — the one thing the right-click menu has and
/// every in-tree panel lacked, which is what "这里一个样子那里一个样" was actually reading.
/// </summary>
/// <remarks>
/// <para>
/// The menu's shadow is not a brush we can hand over: <c>MenuFlyoutPresenterBackground</c> resolves to
/// <c>#00000000</c> and its border to <c>#0F000000</c>, so the white and the lift are drawn by the
/// system's popup host, not by the presenter. A panel that lives in the board's own visual tree (which
/// it must, or <c>outspread shot</c> cannot photograph it) has to draw that lift itself.
/// </para>
/// <para>
/// WinUI 3 has no <c>ContainerVisual.Shadows</c> (the composition shadow collections of UWP did not
/// cross into this projection — the compiler refuses the member). What did cross is
/// <c>UIElement.Shadow</c> with a <c>ThemeShadow</c>: the framework's own elevation, which is the same
/// device the platform's drag-drop uses. It has one knob — how far the element is lifted — so that is
/// the one number here, and the target it was set against is the profile below.
/// </para>
/// <para>
/// The target, measured off his screenshot of the menu over a board floor of 250: directly below the
/// panel 215 at 1px, 218 at 3px, 225 at 6px, 233 at 10px; to either side 236 at 1px, 243 at 5px,
/// 247 at 9px; nothing above it. A soft shadow offset downward, ~14% at the edge gone by ~12px.
/// <para>
/// What <c>ZPx = 24</c> actually drew, read off <c>shot --panel cheat</c> over a scrim floor of 135:
/// 110 at 1px below the edge (18% black), 129 at 12px (4%); on the sides 5.5% at 1px against the
/// menu's 5.6%. So the sides are the match, the bottom is a shade heavier than the baseline, and
/// there is ~6% of cast above the panel where the menu has none — that last one is the framework's
/// shape, not a knob here: lowering the lift weakens the bottom too, which is the side that carries it.
/// </para>
/// </remarks>
public static class Lift
{
    /// <summary>How far a floating surface is lifted off the board, in composition pixels. The only
    /// knob ThemeShadow gives: more height, so a wider and softer cast.</summary>
    public const double ZPx = 24;
}

/// <summary>
/// The control cluster in the bottom-right corner — undo, redo, zoom out, the ratio, zoom in, and the
/// funnel — as one set of numbers read off the prototype, not invented on this side.
/// </summary>
/// <remarks>
/// <para>
/// Every value here was measured with <c>getComputedStyle</c> against <c>design/feel.html#badge</c> on
/// 2026-10-11: container 228.22×34 at 12px from the board's right and bottom edges, one 1px hairline,
/// square corners, 2px padding inside, 2px between members; a member is 26.22×28 (12px glyph on an
/// 18px line, 5px 9px padding, no border, transparent until hovered); the separator is 1px with
/// 2px 3px margin; the ratio holds 52px and is centred in ink, not in muted.
/// </para>
/// <para>
/// The reason this is a class and not XAML attributes: the first version of this cluster was written
/// straight into XAML with default <c>Button</c> styles, and what appeared on screen was Microsoft's
/// control design — three grey boxes with borders and 20px symbols — next to the prototype's one
/// hairline pill. Anything that has not been through the prototype gets measured here first.
/// </para>
/// </remarks>
public static class Badge
{
    /// <summary>Distance from the board's right and bottom edges, screen pixels (prototype: 12/12).</summary>
    public const double EdgeInsetPx = 12;

    /// <summary>Padding inside the hairline container, and the gap between its members.</summary>
    public const double ContainerPaddingPx = 2;

    /// <summary>圆角：他 10-12 点名要把右下角那一组改成圆角。成员那一圈取"容器半径减容器内距"，
    /// 这样高亮块贴在外壳内缘上，不会露出方角。</summary>
    public const double MemberRadiusPx = Chrome.Radius - ContainerPaddingPx;

    public const double MemberGapPx = 2;

    /// <summary>A member's own padding — 7/11 after he judged the 5/9 version too small to hit
    /// comfortably. With a 20px line box that lands a member at ≈33.5 tall (prototype measured).</summary>
    public const double MemberPaddingX = 11;

    public const double MemberPaddingY = 7;

    /// <summary>The glyphs are characters, not icon fonts: the prototype writes − (U+2212) and ＋
    /// (U+FF0B) at 13px on a 20px line, so both products draw them from the same font stack.</summary>
    public const double FontSize = 13;

    public const double LineHeight = 20;

    public const string ZoomOutGlyph = "−";
    public const string ZoomInGlyph = "＋";
    public const string UndoGlyph = "↶";
    public const string RedoGlyph = "↷";

    /// <summary>The ratio's own width, so the cluster does not twitch as the number changes length.</summary>
    public const double RatioMinWidth = 52;

    public const double SeparatorWidthPx = 1;
    public const double SeparatorMarginX = 3;
    public const double SeparatorMarginY = 2;

    /// <summary>The surface's floor. It used to be the prototype's literal white; now it is the
    /// right-click menu's own measured fill — <c>rgb(251,252,253)</c>, read off the screenshot he sent
    /// of that menu — because he pointed at the menu as the one floating surface that looks right, and
    /// a second value for a second panel is exactly the "这里一个样子那里一个样" he objected to.
    /// The prototype's <c>--panel</c> carries the same number in both themes.</summary>
    public static Rgb PanelColour(bool dark) => dark ? new Rgb(27, 30, 34) : new Rgb(251, 252, 253);

    /// <summary>Prototype <c>--line</c>: black 12% in the light theme, white 16% in the dark one. Also
    /// the hover fill — the prototype has no separate hover colour.</summary>
    public static Rgb LineColour(bool dark) => dark ? new Rgb(255, 255, 255) : new Rgb(0, 0, 0);

    public static double LineAlpha(bool dark) => dark ? 0.16 : 0.12;

    /// <summary>Prototype <c>--muted</c> / <c>--text</c>: a glyph waits in muted, the ratio speaks in ink.</summary>
    public static Rgb Muted(bool dark) => dark ? new Rgb(185, 190, 197) : new Rgb(90, 96, 104);

    public static Rgb Ink(bool dark) => dark ? new Rgb(245, 246, 247) : new Rgb(30, 30, 30);

    /// <summary>Prototype <c>:disabled</c> — muted, no fill, 38% opacity.</summary>
    public static double DisabledOpacity => 0.38;
}
