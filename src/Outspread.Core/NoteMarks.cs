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

    public static DueState StateOf(string? text, DateOnly today) => StateOf(Tags.Read(text), today);

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
            parts.Add($"{ordinal} 号");
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
    public readonly record struct Mark(string Label, string Source, bool IsDate, DueState State = DueState.None);

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
            chips.Add(new Mark($"{ordinal} 号", sequenceSource, false));
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
    public static bool Shows(DueState actual, IReadOnlyList<string> tags, string? tag, DueState? dueFilter)
    {
        if (tag is { Length: > 0 } wanted && !tags.Contains(wanted, StringComparer.Ordinal))
        {
            return false;
        }

        return dueFilter is null || actual == dueFilter.Value;
    }
}
