namespace Outspread.Core;

/// <summary>
/// What a note's text says about time and about grouping, and how that becomes visible.
/// </summary>
/// <remarks>
/// <para>
/// JSON Canvas has no due field and no tag field: both live in the text, which is what lets an
/// agent and a person write the same thing. That also means the only feedback a user gets is what
/// the surface chooses to draw — and a mark that draws nothing cannot be told apart from a mark
/// that was not understood. So every state here has a visible consequence, including
/// <see cref="DueState.Malformed"/>.
/// </para>
/// <para>
/// The bar colours are the prototype's published tokens, except "today": <c>--due-late #d13415</c>
/// light / <c>#ff977d</c> dark (Radix tomato11), and today is now Radix <b>blue11</b>
/// (<c>#0d74ce</c> / <c>#70b8ff</c>). Amber11 (<c>#ab6400</c>) sat too close to tomato11 to tell
/// apart at two pixels — he asked for blue on 10-24.
/// </para>
/// </remarks>
public static class NoteMarks
{
    /// <summary>The width of the edge bar, in screen pixels — it is a target-like signal, so it does not scale with zoom.</summary>
    public const double BarWidthPx = 2;

    public enum DueState
    {
        None,

        /// <summary>Carries an <c>@d…</c> shape that is not a real date (<c>@d2026-2-30</c>).</summary>
        Malformed,

        /// <summary>Strictly in the future.</summary>
        Upcoming,

        Today,

        /// <summary>Strictly in the past — the one that must be impossible to miss.</summary>
        Late,
    }

    public static DueState StateOf(string? text, DateOnly today) => StateOf(Tags.Read(text, today), today);

    /// <summary>「即将逾期」算几天内：他 11-02 定的 3 天（含今天）。</summary>
    public const int SoonDays = 3;

    /// <summary>这格算不算"还欠着的逾期"：有读得出的过去日期，且没标完成。</summary>
    /// <remarks>
    /// 完成的那几格不计入计数：左下角那两个数是"还要不要管"的提醒，已完成的格子挂在那儿
    /// 只会让数字永远降不下来。
    /// </remarks>
    public static bool IsOpenLate(NoteProperties marks, DateOnly today)
        => marks.Done != true && StateOf(marks, today) == DueState.Late;

    /// <summary>这格算不算"即将逾期"：今天到 today+<paramref name="days"/> 之间到期，且没标完成。</summary>
    public static bool IsOpenSoon(NoteProperties marks, DateOnly today, int days)
        => marks.Done != true && marks.Due is { } due && due >= today && due <= today.AddDays(days);

    /// <summary>即将逾期那一枚点子的颜色（与"今天"那条蓝、逾期那条红同属一条状态色语言）。</summary>
    public static Rgb SoonColour(bool dark) => dark ? new Rgb(255, 185, 0) : new Rgb(171, 100, 0);

    /// <summary>The same reading for a note whose marks were already parsed — the card re-reads its
    /// own text on every frame of a pan, so it must not pay for the parse three times.</summary>
    public static DueState StateOf(NoteProperties marks, DateOnly today)
    {
        if (marks.Due is { } due)
        {
            if (due < today)
            {
                return DueState.Late;
            }

            return due == today ? DueState.Today : DueState.Upcoming;
        }

        return marks.MalformedDue is null ? DueState.None : DueState.Malformed;
    }

    /// <summary>The bar's colour, or null when nothing should be drawn.</summary>
    public static Rgb? BarFor(DueState state, bool dark) => state switch
    {
        DueState.Late => dark ? new Rgb(255, 151, 125) : new Rgb(209, 52, 21),
        DueState.Today => dark ? new Rgb(112, 184, 255) : new Rgb(13, 116, 206),

        // A due marker nobody could read still gets an edge — in the snap-line red, because it is
        // an error on the board rather than a date. Silence here is the failure mode the whole
        // function exists to prevent.
        DueState.Malformed => new Rgb(255, 107, 107),
        _ => null,
    };

    /// <summary>Tags with how many notes carry them, most-used first, then alphabetical.</summary>
    public static IReadOnlyList<(string Tag, int Count)> Tally(IReadOnlyList<string?> texts)
        => texts.Where(text => !string.IsNullOrEmpty(text))
            .SelectMany(text => Tags.Of(text))
            .GroupBy(tag => tag, StringComparer.Ordinal)
            .Select(group => (group.Key, group.Count()))
            .OrderByDescending(pair => pair.Item2)
            .ThenBy(pair => pair.Item1, StringComparer.Ordinal)
            .ToList();

    /// <summary>
    /// The one line a card writes about itself: when it is due, and what it belongs to.
    /// </summary>
    /// <remarks>
    /// This is what a property line is <b>for</b>. The tokens are taken off the body so the card
    /// reads as notes and not as markup, and so a retired <c>#due:</c> / <c>#周会</c> cannot be mistaken for
    /// colours or headings — but a mark nobody can see is indistinguishable from a mark nobody
    /// understood, which is invariant 3 of docs/note-lifecycle.md. Every state has a spelling here,
    /// including the ones the edge bar stays silent about: an upcoming date draws no bar (only
    /// late, today and unreadable do), so without this line a scheduled note would look unscheduled.
    /// </remarks>
    public static string Readout(NoteProperties marks, DateOnly today)
    {
        var parts = new List<string>();

        if (marks.DueSource is not null)
        {
            parts.Add(DateLabel(marks, today));
        }

        if (marks.Sequence is { } ordinal)
        {
            parts.Add($"NO.{ordinal}");
        }

        if (marks.Done is { } done)
        {
            parts.Add(done ? "已完成" : "未完成");
        }

        parts.AddRange(marks.Tags);

        return string.Join(" · ", parts);
    }

    /// <summary>One deletable thing the card shows: what it says, and the exact span in the text that
    /// deleting it takes out.</summary>
    /// <param name="Label">The words on the chip.</param>
    /// <param name="Source">The span <see cref="Tags.Without"/> removes — the spelling that is actually
    /// in the file, because removing a guess is how a delete eats the wrong thing.</param>
    /// <param name="IsDate">Whether this chip carries a deadline (it goes first, and it is the one the
    /// edge bar agrees with).</param>
    /// <param name="State">The date's own reading, so the mark can carry the state colour the edge bar
    /// already carries. Tags leave it <see cref="DueState.None"/> — they have no state to show.</param>
    /// <param name="Done">完成标记的那一枚：true＝已完成、false＝未完成、null＝别的牌子。
    /// 牌子要据此换色与换形状（绿勾／红叉），不能再和标签长一样。</param>
    /// <param name="Gate">闸门插件的状态牌（helm-gate，只读）：None＝普通牌子。非 None 的牌子
    /// 不进编辑/删除，颜色按闸门状态来（进行中蓝、堵住橙、已完成走 Done 的绿勾、已放弃删除线）。</param>
    public readonly record struct Mark(string Label, string Source, bool IsDate, DueState State = DueState.None, bool? Done = null, GateStatus Gate = GateStatus.None);

    /// <summary>The chips a card's bottom-left corner is made of: the date first, then its tags.</summary>
    public static IReadOnlyList<Mark> Chips(NoteProperties marks, DateOnly today)
    {
        var chips = new List<Mark>();

        if (marks.DueSource is { } source)
        {
            chips.Add(new Mark(DateLabel(marks, today), source, true, StateOf(marks, today)));
        }

        if (marks.Sequence is { } ordinal && marks.SequenceSource is { } sequenceSource)
        {
            chips.Add(new Mark($"NO.{ordinal}", sequenceSource, false));
        }

        if (marks.Done is { } done && marks.DoneSource is { } doneSource)
        {
            // 文案就是那两个字：点牌身＝聚焦完成态（他 11-03 拍的 A），切换走编辑器里改 token。
            chips.Add(new Mark(done ? "已完成" : "未完成", doneSource, false, NoteMarks.DueState.None, done));
        }

        foreach (var tag in marks.Tags)
        {
            chips.Add(new Mark(tag, marks.TagSources.TryGetValue(tag, out var span) ? span : tag, false));
        }

        return chips;
    }

    /// <summary>The date half of the readout, in the words the state deserves.</summary>
    private static string DateLabel(NoteProperties marks, DateOnly today)
    {
        if (marks.Due is not { } due)
        {
            return "日期读不出";
        }

        var shown = due.Year == today.Year
            ? due.ToString("MM-dd", System.Globalization.CultureInfo.InvariantCulture)
            : due.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);

        // Only the two states that are already a deadline get a word in front of the date; an
        // upcoming one is just a date, and calling it 逾期 would be a lie about arithmetic.
        return due < today ? $"逾期 {shown}" : due == today ? $"今天 {shown}" : shown;
    }

    /// <summary>
    /// Whether a note survives a filter. The filter is a view concern — it never edits the
    /// document, so it must never appear in the undo stack either.
    /// </summary>
    /// <param name="tag">A tag in its canonical spelling (<c>#周会</c>), or null for "no tag filter".</param>
    /// <param name="tags">The note's own tags. Passed in rather than re-read here, because the card
    /// already parsed its text once for the bar and the readout.</param>
    /// <param name="actual">The note's due state, computed once by the caller — the state needs
    /// "today", and re-deriving it here would mean every filtered note re-parses its own text.</param>
    /// <param name="done">这一格自己写的完成状态：true＝@b1、false＝@b0、null＝没写。</param>
    /// <param name="doneFilter">null＝不按完成状态筛；true＝只看已完成；false＝只看"还没做完"——
    /// 那**包含没写 @b 的**：待办板上"没写"就是"还没做完"，只留明确 @b0 的那几格会让一整块
    /// 没标记的板子在筛选后变成空白。</param>
    public static bool Shows(DueState actual, IReadOnlyList<string> tags, bool? done, string? tag, DueState? dueFilter, bool? doneFilter)
    {
        if (tag is { Length: > 0 } wanted && !tags.Contains(wanted, StringComparer.Ordinal))
        {
            return false;
        }

        if (dueFilter is not null && actual != dueFilter.Value)
        {
            return false;
        }

        return doneFilter is null || (done == true) == doneFilter.Value;
    }
}
