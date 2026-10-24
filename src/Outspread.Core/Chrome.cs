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
/// 动效那几枚数。它们不是这里发明的：<c>design/feel.html</c> 的 <c>--dur-*</c> 就是这一套
/// （fast 83 / base 167 / slow 320 / enter 250 / hover 120），网页那侧每一处过渡都从那份令牌取。
/// 界面这一侧以前没有一处定义过时长，所以第一次要用到它的时候必须落在这枚文件里，
/// 而不是写进某个 Storyboard 的常数。
/// </summary>
/// <remarks>
/// 曲线对不上要说清楚：页面写的是 <c>cubic-bezier(0,0,0,1)</c>（进入）与 <c>(0.16,1,0.3,1)</c>（悬停），
/// 而这一投影里没有能喂任意贝塞尔的动画对象（<c>CubicBezier</c> 在 <c>Microsoft.UI.Xaml.winmd</c> 里
/// 0 命中），能给的只有平台的 <c>CubicEase</c>。所以这里只钉时长，曲线用平台的，差在哪记在调研那一页。
/// </remarks>
public static class Motion
{
    /// <summary>进入那一档：页面里的 <c>--dur-enter</c>。</summary>
    public const double EnterMs = 250;

    /// <summary>退出与淡出那一档：页面里的 <c>--dur-base</c>（微软的 base 档）。</summary>
    public const double BaseMs = 167;

    /// <summary>悬停那一档：页面里的 <c>--dur-hover</c>。</summary>
    public const double HoverMs = 120;
}

/// <summary>
/// 自绘小控件（操作条那几枚圆钮与它们的图标）的一套数——一枚控件的七个量各写一次，
/// 界面侧不许就地写字面数。
/// </summary>
/// <remarks>
/// 出处（10-24 两票调研，全部现量）：Chrome 源码——钮 34、图标画布 20、内衬 7、圆角 min(w,h)/2、
/// 悬停 alpha 0x14≈7.8% 且淡入淡出 250ms、焦点环 2px；Fluent 2 令牌——subtle fill 5→10→15%、
/// 90ms 进 250ms 出；M3 令牌——state layer .08/.12、short2 100ms。
/// <para>
/// 三家的共同点比差别重要，就两条：① <b>静止不画常驻描边</b>，圆面只在悬停/按下时出现；
/// ② <b>笔画按最终尺寸重画，不靠缩放</b>——实测 Fluent 图标 16px 档笔画 1.0、24px 档 1.5，
/// IBM 20px 档 1.25，Chrome 更彻底：它的图标是填充形状，~1.9dp 的重量烘在几何里。
/// 我方上一版是 24 网格 1.5 描边经 Viewbox 缩到 16，落屏 1.0 且坐标全成了小数——
/// 那既是他说的"太小"，也是他说的"晕染"。
/// </para>
/// </remarks>
public static class ControlKit
{
    /// <summary>
    /// 圆钮本体直径。34 那一档配 20 画布是 Chrome 的比（0.59），但我们这三枚浮在板面上、
    /// 底下没有工具栏那条底，缩到 34 就把图形顶满了——10-24 他判"变胖变粗一大圈"。
    /// 38 配 24 画布仍是 0.63，且笔画 2 在 24 网格上就是 Material 的 2dp/24 那一档。
    /// </summary>
    public const double ButtonPx = 38;




    /// <summary>悬停那一层的墨占比：Chrome 的 0x14/255≈7.8%。</summary>
    public const double HoverAlpha = 0.078;

    /// <summary>按下那一层：Chrome 涟漪 6% 与 Fluent 15% 之间，取 12%。</summary>
    public const double PressAlpha = 0.12;

    /// <summary>悬停淡入：Fluent 90ms 与 M3 short2 100ms 同档。</summary>
    public const double HoverInMs = 100;

    /// <summary>悬停淡出：Chrome 的 250ms。</summary>
    public const double HoverOutMs = 250;

    /// <summary>按下：Fluent 的 90ms 进。</summary>
    public const double PressMs = 90;



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

    /// <summary>
    /// The band for the two surfaces that are one sentence long — the filter banner and the receipt
    /// line. Same type system as a list row (one font, one body ink, the same inset), one step less
    /// air: 40px is the pitch a row needs when a finger has to hit one of many rows, and a sentence
    /// that is not a row does not need it. He asked for this on 2026-10-12 off a capture of the
    /// receipt chip ("这行字没居中，而且弹窗稍微大了点") — the centring half of that was a real
    /// defect (an empty right column still ate the 14px gap), this half is a deliberate second band.
    /// </summary>
    public const double OneLineRowPx = 30;

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

    /// <summary>
    /// 键位清单是一张<b>页面</b>，不是菜单——2026-10-23 他看过原型后定的一档：面板 760 宽、
    /// 内缩 16、两栏各 15 行、栏距 56、圆角 12。它和菜单的 4/8 不是同一件事：菜单是刚好装下
    /// 几行的浮层，清单是压在板面上读一分钟的参考页；4px 边距在 680×430 的清单上读起来就是
    /// "字顶到框上"，而那正是他说的"边距太窄"。
    /// <para>
    /// 页面沿用同一枚填充与同一层抬起；变的只有"页面该有多大的边"和"它比菜单更圆"这两格。
    /// </para>
    /// </summary>
    public const double PageWidthPx = 760;

    public const double PagePadPx = 16;

    public const double PageGutterPx = 56;

    public const double PageRadiusPx = 12;

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
    /// 2026-10-23 he re-picked the paper cap in the roomy page: 24 high, 7/1 padding, radius 5, 12px face.
    /// The old 23/6/4 was measured off <c>design/feel.html:425</c>; the page mockup's cap is the newer one.
    /// </summary>
    public const double KeyMinWidth = 22;

    public const double KeyHeight = 24;
    public const double KeyPadX = 7;
    public const double KeyPadY = 1;
    public const double KeyRadius = 5;
    public const double KeyGap = 4;
    public const double KeyFont = 12;

    /// <summary>Hover, the only state a row has: it means "this row is clickable", nothing else.</summary>
    public static Rgb Hover(bool dark) => Badge.LineColour(dark);

    public static double HoverAlpha(bool dark) => dark ? 0.07 : 0.045;

    /// <summary>The sheet's fill — the same surface the control cluster sits on.</summary>
    public static Rgb Fill(bool dark) => Badge.PanelColour(dark);
}

/// <summary>
/// 标签条那一排浏览器式标签的数值。这是一次**改皮**，不是换骨架：标签仍是原生
/// <c>TabView</c>，键盘、溢出滚动、关闭请求、标题栏拖拽都还由平台管；这里只定它长什么样。
/// </summary>
/// <remarks>
/// <para>
/// 2026-10-22 他对着截图点名四件事：标签难区分、缺动效、加号位置奇怪、新建浮窗不好看。他点头的
/// 原型是 <c>design/tabs.html</c>（对话里那版可点原型同源）。这一组的每个数都能回到那份原型的
/// <c>getComputedStyle</c>：条 38、标签 34、上沿圆角 8、字号 13、左内缩 12、关闭键 20 视觉
/// （命中区在界面侧补到 24）、加号 28、分隔线上下各缩 9。
/// </para>
/// <para>
/// 活动标签的填充**必须等于板底**：这是"这块屏现在在办哪件事"的那一格与它下面那张纸连成一片，
/// 浏览器标签之所以一眼能分出来，靠的不是多一条线，而是它和内容同色、其余标签退回条底。深色
/// 档也照这条走，活动标签会比条底更深，边缘那条发丝负责把它从条里切出来。
/// </para>
/// <para>
/// 非活动标签的墨色沿用 <see cref="Badge.Muted"/>，活动墨色沿用 <see cref="Sheet.Ink"/>：标签条
/// 不是浮层，但它是同一套界面里的字，不能在这里长第二套墨。条底/发丝/悬停/分隔线是这一格
/// 新有的三枚颜色，浅深各一套。
/// </para>
/// </remarks>
public static class TabStrip
{
    /// <summary>整条标题栏的高度：标签 34 加 4 的上沿空气。</summary>
    public const double StripHeight = 38;

    public const double TabHeight = 34;

    /// <summary>只圆上沿。标签下沿必须与板面接上，方角是"纸"，不是这里的事。</summary>
    public const double TopRadius = 8;

    public const double TabMinWidth = 82;

    public const double TabMaxWidth = 220;

    /// <summary>
    /// 改名那一枚的**字面下限**（编辑要有地方看：5 个字的框改不了 40 个字的名字）。长名字不靠
    /// "打字途中一路放宽"看全——每敲一下按内容再长一次，就是他 10-23 报的"文字位置会浮动"。
    /// 这一跳只在进改名那一拍发生一次，且标签向右长、左沿不动。上限见 <see cref="RenameTabMaxWidth"/>。
    /// </summary>
    public const double RenameTabMinWidth = 180;

    /// <summary>字面上限：名字再长也不把整条标签栏挤走。</summary>
    public const double RenameTabMaxWidth = 300;

    /// <summary>量出来的名字宽度再加这一点空气：光标那一格，和"最后一个字后面还敲得进字"的位置。</summary>
    public const double RenameSlackPx = 16;

    public const double FontPx = 13;

    /// <summary>标签左内缩。原型量的 12；关闭键占位另算。</summary>
    public const double PadLeft = 12;

    /// <summary>关闭键的视觉方框。命中区在界面侧取 24（他的可访问下限）。
    /// 2026-10-23：他要 Chrome 式，悬停底改成圆形——半径取半高（24/2），不是方角 6。</summary>
    public const double ClosePx = 20;

    public const double CloseHitPx = 24;

    public const double CloseGlyphPx = 10;

    public const double CloseRadius = 12;

    public const double PadRight = 4;

    /// <summary>加号那一枚：30×30、圆形悬停（Chrome 式），紧跟最后一枚标签，不再漂到标题条右边。
    /// 2026-10-23 他看过原型后把 28 的方角换成 30 的圆，并把全角 "＋" 换成系统 Add 字形——
    /// 字体字符的笔画与站位都不受控，正是他说的"廉价"。</summary>
    public const double AddPx = 30;

    public const double AddRadius = 15;

    /// <summary>十字的视觉长度（两笔各 12、1.5 粗）——不是字体字号；它是画出来的两根圆头细条。</summary>
    public const double AddGlyphPx = 12;

    /// <summary>
    /// 加号在 38 高的条里往下多落 2px：几何居中会让它和标签文字的光学中心差两格，
    /// 他 10-23 看出来的"加号高了一点"就是这 2px。整枚 30 的圆跟着下移，不是只挪十字。
    /// </summary>
    public const double AddOpticalDropPx = 2;

    /// <summary>标题条右端的设置入口：30×30、圆角 6、竖排三点（浏览器式）。</summary>
    public const double SettingsPx = 30;

    public const double SettingsRadius = 6;

    /// <summary>竖排三点：每枚 2.5px、间距 2.5px——画出来的圆点，不是 "⋮" 字形。</summary>
    public const double SettingsDotPx = 2.5;

    public const double SettingsDotGapPx = 2.5;

    /// <summary>它与右侧三枚窗口按钮之间留 6px；加号再留 8px，别顶到它。</summary>
    public const double SettingsInsetPx = 6;

    /// <summary>标签之间的发丝线上下各缩多少。</summary>
    public const double SeparatorInset = 9;

    /// <summary>标题条底：独立于板面的一层浅灰/近黑，让活动标签能"浮"出来。</summary>
    public static Rgb StripBackground(bool dark) => dark ? new Rgb(23, 25, 28) : new Rgb(236, 238, 242);

    /// <summary>
    /// 自绘标题按钮：系统框整条去掉之后由我们画三枚，尺寸沿用 Windows 的 46 宽，
    /// 高度跟这条 38 的标题条；右边让出的总宽就是 3×46。
    /// </summary>
    public const double CaptionButtonWidth = 46;

    public const double CaptionWidth = CaptionButtonWidth * 3;

    public const double CaptionGlyphPx = 10;

    /// <summary>关闭键悬停的红：Windows 自己的 caption close hover 色。</summary>
    public static Rgb CaptionCloseRed => new(196, 43, 28);

    /// <summary>活动标签填充＝板底。见类型注释：这一条是把"区分"从装饰变成结构。</summary>
    public static Rgb ActiveFill(bool dark) => BoardGrid.BoardBackground(dark);

    public static Rgb InactiveInk(bool dark) => Badge.Muted(dark);

    public static Rgb ActiveInk(bool dark) => Sheet.Ink(dark);

    /// <summary>条底那条发丝；活动标签会盖住它，非活动区才看得见。</summary>
    public static Rgb Line(bool dark) => dark ? new Rgb(255, 255, 255) : new Rgb(0, 0, 0);

    public static double LineAlpha(bool dark) => dark ? 0.11 : 0.11;

    /// <summary>非活动标签悬停时的淡底。</summary>
    public static Rgb Hover(bool dark) => Line(dark);

    public static double HoverAlpha(bool dark) => dark ? 0.075 : 0.055;

    /// <summary>两个非活动标签之间的竖发丝。</summary>
    public static double SeparatorAlpha(bool dark) => dark ? 0.10 : 0.09;

    /// <summary>活动标签自己的边缘发丝：浅色档压一道浅灰，深色档提一道浅白。</summary>
    public static double EdgeAlpha(bool dark) => dark ? 0.13 : 0.09;

    /// <summary>关闭键悬停时的底；比标签悬停实一档，说明这一下是点它。</summary>
    public static double CloseHoverAlpha(bool dark) => dark ? 0.13 : 0.09;

    /// <summary>标签条自己的动效档：悬停用平台那枚 120，增删由 TabView 的 AddDelete 过渡负责。</summary>
    public const double HoverMs = Motion.HoverMs;
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
