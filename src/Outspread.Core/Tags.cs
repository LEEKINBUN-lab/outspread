using System.Globalization;
using System.Text.RegularExpressions;

namespace Outspread.Core;

/// <summary>
/// What a note says about time and about grouping, and the body that is left once the board has
/// taken those two things away.
/// </summary>
/// <param name="Due">A date the board believes, or null.</param>
/// <param name="MalformedDue">The token that looked like a due date and is not one
/// (<c>@due(2026-13-40)</c>). Never paired with <paramref name="Due"/>.</param>
/// <param name="Tags">Canonical tag names, each carrying its <c>#</c> whichever way it was written.</param>
/// <param name="Body">The text with the understood property lines removed — what a card paints.</param>
/// <remarks>
/// A JSON Canvas text node has no due field and no tag field, so both are written into the text,
/// which is also what lets a person and an agent write the same thing. See
/// docs/research-chrome-and-properties.md for why the shape is <c>@due(2026-10-09)</c> rather than a
/// new field in the file: the spec states no rule for unknown fields, and this file is read by three
/// programs, so an ignored field is a lost field.
/// </remarks>
public sealed record NoteProperties(
    DateOnly? Due,
    string? MalformedDue,
    IReadOnlyList<string> Tags,
    string Body)
{
    /// <summary>序号：<c>@n1</c> 就是 1 号（10-25 他加的，和日期、标签同一档）。</summary>
    public int? Sequence { get; init; }

    /// <summary>序号的原文 span（删牌子时要从正文里拿掉的那一段）。</summary>
    public string? SequenceSource { get; init; }

    /// <summary>完成状态：<c>@b1</c> 已完成、<c>@b0</c> 未完成，没写就是 null（@n2，他 10-25 加的）。
    /// 真身在正文那两枚字符里，牌子只是它的一种画法——所以 agent 与手机写 <c>@b1</c> 就够了。</summary>
    public bool? Done { get; init; }

    /// <summary>完成标记的原文 span（点 × 时要从正文里拿掉的那一段）。</summary>
    public string? DoneSource { get; init; }

    /// <summary>Whether a card earns the mark strip.</summary>
    public bool HasMarks => Due is not null || MalformedDue is not null || Sequence is not null || Done is not null || Tags.Count > 0;

    /// <summary>The exact span that produced <see cref="Due"/> (or the unreadable one that produced
    /// <see cref="MalformedDue"/>) — what a delete on the card has to take out of the text.</summary>
    public string? DueSource { get; init; }

    /// <summary>Each tag's own spelling in the text, so a delete removes what is actually there
    /// (<c>@t周会</c>, <c>@tag(周会)</c> or <c>#周会</c>) rather than a guess at it.</summary>
    public IReadOnlyDictionary<string, string> TagSources { get; init; } = EmptySources;

    /// <summary>The property lines that were consumed, in order, newline-joined — the editor hides
    /// these and must hand them back unchanged on commit.</summary>
    public string Props { get; init; } = string.Empty;

    internal static readonly Dictionary<string, string> EmptySources = new(StringComparer.Ordinal);

    /// <summary>Puts a hidden property block back in front of a body. Used when the editor commits:
    /// what the user saw was the body alone, and what the file keeps is both.</summary>
    public static string WithProps(string props, string body)
    {
        var kept = props.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        return kept.Length == 0 ? body : string.Join('\n', kept) + "\n" + body;
    }
}

/// <summary>
/// The two marks — <c>@dYYYY-MM-DD</c> and <c>@t周会</c> — and the rule for telling a property line
/// from prose. One spelling each: the earlier <c>#due:</c> / <c>#tag</c> forms and the bracketed
/// <c>@due(…)</c> / <c>@tag(…)</c> forms are no longer read at all.
/// </summary>
/// <remarks>
/// <para>
/// Dropping <c>#</c> is not housekeeping. A card now renders markdown, where a leading
/// <c>#</c> is a heading and <c>#word</c> is prose — so the same character meant two unrelated
/// things on the same line, and which one won was a parser detail nobody could see. He asked for
/// this by name: "我就是知道 # 会冲突所以才这么改".
/// </para>
/// <para>
/// Two rules here exist because each break was silent in the prototype:
/// </para>
/// <list type="number">
/// <item><description>A token counts only when a line holds <b>nothing but</b> tokens. Without that
/// rule a note explaining the syntax — there is one on the demo board — would have its own example
/// eaten off the card. An inline token is therefore stored, shown and not acted on: what is on the
/// card is what is in the file.</description></item>
/// <item><description>A date is checked against the calendar rather than a pattern, because
/// <c>TryParseExact</c> quietly turns 2026-02-30 into 2026-03-02 and calls that success.</description></item>
/// </list>
/// <para>
/// A tag's <i>display</i> name still carries a leading <c>#</c> (<c>#周会</c> on the chip and in the
/// banner). That is a label, not a syntax: nothing reads <c>#周会</c> typed into a note as a tag any
/// more, and the character is free for headings.
/// </para>
/// </remarks>
public static class Tags
{
    /// <summary>
    /// A short token: <c>@d2019-3-3</c> or <c>@t周会</c>. The value runs to the next space, so one line
    /// can hold several, and both <c>-</c> and <c>/</c> are accepted inside a date because that is how
    /// people actually type one.
    /// </summary>
    private static readonly Regex TokenShort = new(@"@(?<key>[dtnb])(?<value>\S*)", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>The next token at the head of the remainder, or <see cref="Match.Empty"/>.</summary>
    private static Match NextToken(string rest)
    {
        var match = TokenShort.Match(rest);

        return match.Success && match.Index == 0 ? match : Match.Empty;
    }

    /// <summary>Whether a short-token match is really the retired <c>@due(…)</c> / <c>@tag(…)</c>
    /// spelling — half-width or full-width bracket, since both were accepted while it lived.</summary>
    private static bool IsOldSpelling(Match token)
    {
        var key = token.Groups["key"].Value;
        var value = token.Groups["value"].Value;

        return key.Equals("d", StringComparison.OrdinalIgnoreCase)
            ? value.StartsWith("ue(", StringComparison.Ordinal) || value.StartsWith("ue（", StringComparison.Ordinal)
            : key.Equals("t", StringComparison.OrdinalIgnoreCase)
                && (value.StartsWith("ag(", StringComparison.Ordinal) || value.StartsWith("ag（", StringComparison.Ordinal));
    }

    /// <summary>
    /// Reads the marks out of one note's text and returns the body that is left to paint.
    /// </summary>
    /// <param name="today">Which year a month-day without a year belongs to (<c>@d12-04</c>).
    /// The card passes the view's frozen today, so a board and its chips cannot disagree about the
    /// year when the clock crosses midnight.</param>
    public static NoteProperties Read(string? text, DateOnly? today = null)
    {
        var raw = text ?? string.Empty;

        // Plain notes are the common case and this surface re-reads every note on every frame of a
        // pan, so the sigil is looked for before any line is split.
        if (!raw.Contains('@'))
        {
            return new NoteProperties(null, null, [], raw);
        }

        var calendar = today ?? DateOnly.FromDateTime(DateTime.Now);

        DateOnly? due = null;
        string? malformed = null;
        string? dueSource = null;
        int? sequence = null;
        string? sequenceSource = null;
        bool? done = null;
        string? doneSource = null;
        var tags = new List<string>();
        var sources = new Dictionary<string, string>(StringComparer.Ordinal);
        var kept = new List<string>();
        var props = new List<string>();

        foreach (var line in raw.Split('\n'))
        {
            if (!IsPropertyLine(line.Trim(), out var tokens))
            {
                kept.Add(line);
                continue;
            }

            // @n 后面必须是数字、@b 后面必须是 0 或 1：@nfoo、@b2 都不算标记，整行照样是正文——别把人家的话吃掉。
            if (tokens.Any(token => token.Groups["key"].Value.Equals("n", StringComparison.OrdinalIgnoreCase)
                    && !int.TryParse(token.Groups["value"].Value.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out _)
                || token.Groups["key"].Value.Equals("b", StringComparison.OrdinalIgnoreCase)
                    && !(token.Groups["value"].Value.Trim() is "0" or "1")))
            {
                kept.Add(line);
                continue;
            }

            props.Add(line.Trim());

            foreach (var token in tokens)
            {
                var key = token.Groups["key"].Value;
                var value = token.Groups["value"].Value.Trim();
                var isDate = key.Equals("d", StringComparison.OrdinalIgnoreCase);
                var isSequence = key.Equals("n", StringComparison.OrdinalIgnoreCase);

                if (key.Equals("b", StringComparison.OrdinalIgnoreCase))
                {
                    // 第一个 @b 说了算（和日期、序号同一条规矩）：一张卡不置两个完成状态。
                    if (done is null && value is "0" or "1")
                    {
                        done = value == "1";
                        doneSource = token.Value;
                    }

                    continue;
                }

                if (isSequence)
                {
                    // 第一个序号说了算；第二个不当第二个号（和日期同一条规矩）。
                    if (sequence is null
                        && int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var ordinal))
                    {
                        sequence = ordinal;
                        sequenceSource = token.Value;
                    }

                    continue;
                }

                if (isDate)
                {
                    if (due is not null || malformed is not null)
                    {
                        // The first date the board can believe wins; a second one is not a second
                        // deadline, and pretending otherwise would put two bars on one card.
                        continue;
                    }

                    // Both separators are the same date: he writes 2019/3/3 as fast as 2019-3-3.
                    if (TryDate(value.Replace('/', '-'), calendar, out var parsed))
                    {
                        due = parsed;
                    }
                    else
                    {
                        // A due the reader cannot resolve still has to show up: a note the user
                        // believes is scheduled is worse than a visible error.
                        malformed = token.Value;
                    }

                    dueSource = token.Value;

                    continue;
                }

                // One `@t` is one tag: `@t周会、急` is a tag named 周会、急, not two. Each chip has to
                // map back to exactly one span in the text, or deleting it would silently delete its
                // neighbour.
                if (value.Length > 0)
                {
                    AddTag(tags, sources, value, token.Value);
                }
            }
        }

        // The blank line a property line leaves behind is not the writer's paragraph break.
        if (props.Count > 0)
        {
            while (kept.Count > 0 && kept[0].Trim().Length == 0)
            {
                kept.RemoveAt(0);
            }

            while (kept.Count > 0 && kept[^1].Trim().Length == 0)
            {
                kept.RemoveAt(kept.Count - 1);
            }
        }

        var body = string.Join('\n', kept);

        return new NoteProperties(due, malformed, tags, body)
        {
            DueSource = dueSource,
            Sequence = sequence,
            SequenceSource = sequenceSource,
            Done = done,
            DoneSource = doneSource,
            TagSources = sources,
            Props = string.Join('\n', props),
        };
    }

    /// <summary>Distinct tags in the order they first appear. Never a due marker or a hex colour.</summary>
    public static IReadOnlyList<string> Of(string? text) => Read(text).Tags;

    /// <summary>True when the text carries a well-formed, real date.</summary>
    public static bool TryDue(string? text, out DateOnly due, DateOnly? today = null)
    {
        if (Read(text, today).Due is { } value)
        {
            due = value;

            return true;
        }

        due = default;

        return false;
    }

    /// <summary>
    /// The <c>@d…</c> span that looks like a due marker but is not a real date.
    /// Null when there is no marker at all.
    /// </summary>
    public static string? MalformedDue(string? text, DateOnly? today = null) => Read(text, today).MalformedDue;

    /// <summary>Whether a line is nothing but mark tokens — the editor asks this when Enter is pressed
    /// with the caret on that line, and absorbs the line into the hidden property block.</summary>
    public static bool LooksLikePropertyLine(string line) => IsPropertyLine((line ?? string.Empty).Trim(), out _);

    /// <summary>
    /// Takes one mark's span out of the text — the property block first, then the body — and hands
    /// back what is left. This is what deleting a chip on the card does to the file, so the same
    /// spelling rules apply: a line emptied by the delete disappears, and a token that cannot be
    /// found is left alone rather than guessed at.
    /// </summary>
    public static string Without(string? text, string token)
    {
        if (string.IsNullOrEmpty(text) || string.IsNullOrEmpty(token))
        {
            return text ?? string.Empty;
        }

        var read = Read(text);

        if (read.Props.Length > 0 && read.Props.Contains(token, StringComparison.Ordinal))
        {
            var kept = read.Props
                .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(line => line.Replace(token, string.Empty, StringComparison.Ordinal).Trim())
                .Where(line => line.Length > 0)
                .ToList();

            return NoteProperties.WithProps(string.Join('\n', kept), read.Body);
        }

        var index = read.Body.IndexOf(token, StringComparison.Ordinal);

        return index < 0
            ? text
            : NoteProperties.WithProps(read.Props, read.Body.Remove(index, token.Length));
    }

    /// <summary>
    /// 给一张便签补一个标签：已有同一个标签就不动，否则在属性块末尾补 <c>@t名</c>。
    /// 批量加标签只改正文里的这一行，牌子和手机读取的是同一份文件。
    /// </summary>
    public static string AddTag(string? text, string? tag)
    {
        var name = (tag ?? string.Empty).Trim();

        if (name.StartsWith('#'))
        {
            name = name[1..].Trim();
        }
        else if (name.StartsWith("@t", StringComparison.OrdinalIgnoreCase))
        {
            name = name[2..].Trim();
        }

        if (name.Length == 0 || name.Any(char.IsWhiteSpace))
        {
            return text ?? string.Empty;
        }

        var read = Read(text);
        var canonical = $"#{name}";

        if (read.Tags.Contains(canonical, StringComparer.Ordinal))
        {
            return text ?? string.Empty;
        }

        var token = $"@t{name}";
        var props = read.Props.Length == 0 ? token : read.Props + "\n" + token;

        return NoteProperties.WithProps(props, read.Body);
    }

    /// <summary>
    /// 把完成状态写成 <c>@b1</c>／<c>@b0</c>：属性块里已有 <c>@b</c> 就原地换值，没有就补上一枚。
    /// 改的是文件里那份文本——牌子上的字只是它的一种画法，所以撤销、同步与手机那侧读的都是同一件事。
    /// </summary>
    public static string SetDone(string? text, bool done)
    {
        var token = done ? "@b1" : "@b0";
        var read = Read(text);

        if (read.DoneSource is { Length: > 0 } existing)
        {
            return NoteProperties.WithProps(
                read.Props.Replace(existing, token, StringComparison.Ordinal), read.Body);
        }

        var props = read.Props.Length == 0 ? token : read.Props + "\n" + token;

        return NoteProperties.WithProps(props, read.Body);
    }

    /// <summary>
    /// True when the whole line is <c>@name(value)</c> tokens and whitespace. Returns the tokens it
    /// consumed, so the caller does not match the same line twice.
    /// </summary>
    private static bool IsPropertyLine(string line, out List<Match> tokens)
    {
        tokens = [];

        var rest = line;

        while (rest.Length > 0)
        {
            var match = NextToken(rest);

            // Anything left over before the next token — a word, a comma, a full stop — makes the
            // line prose again.
            if (!match.Success)
            {
                tokens.Clear();
                return false;
            }

            // The bracketed spelling reads as a short token with a weird value (@d + ue(…)). Left
            // alone it would be reported as a date the board cannot read — which blames the writer
            // for a date they wrote correctly. So the old shape is not a mark at all, the line stays
            // prose, and what is on the card is what they typed.
            if (IsOldSpelling(match))
            {
                tokens.Clear();
                return false;
            }

            tokens.Add(match);
            rest = rest[match.Length..].TrimStart();
        }

        return tokens.Count > 0;
    }

    /// <summary>Normalises every spelling to one canonical tag: <c>@t周会</c>, <c>@tag(周会)</c> and
    /// <c>#周会</c> are the same tag, and the board must not filter one and show the other.
    /// <paramref name="source"/> is the span to take out of the text when the chip is deleted.</summary>
    private static void AddTag(List<string> tags, Dictionary<string, string> sources, string name, string source)
    {
        var canonical = name.StartsWith('#') ? name : $"#{name}";

        if (tags.Contains(canonical, StringComparer.Ordinal))
        {
            return;
        }

        tags.Add(canonical);
        sources[canonical] = source;
    }

    /// <summary>
    /// Parse, then refuse anything the calendar did not have. <c>TryParseExact</c> <b>adjusts</b>
    /// 2026-02-30 into 2026-03-02 and calls that a success, which is precisely the silent yes this
    /// must not give: a note scheduled on a date that does not exist.
    /// <para>
    /// Three shapes are one date: <c>2026-12-04</c>, <c>27-1-1</c> (a one- or two-digit year is
    /// 20xx — 2027, not year 27) and <c>12-04</c> (no year: the year of <paramref name="today"/>).
    /// The short shapes are his 11-02 ask; reading <c>@d27-1-1</c> as year 27 is how a real
    /// deadline came out overdue by two thousand years.
    /// </para>
    /// </summary>
    private static bool TryDate(string raw, DateOnly today, out DateOnly due)
    {
        due = default;

        var parts = raw.Split('-');

        int year;
        int month;
        int day;

        if (parts.Length == 2)
        {
            // @d12-04：没写年份＝今年。他 11-02 要的第二种写法。
            if (!int.TryParse(parts[0], out month) || !int.TryParse(parts[1], out day))
            {
                return false;
            }

            year = today.Year;
        }
        else if (parts.Length == 3)
        {
            if (!int.TryParse(parts[0], out year)
                || !int.TryParse(parts[1], out month)
                || !int.TryParse(parts[2], out day))
            {
                return false;
            }

            // 一到两位年份是 20xx：@d27-1-1 就是 2027，不是公元 27 年（他 11-02 报的"0027 年、
            // 逾期或干脆读不出"）。四位照旧是它自己。
            if (parts[0].Length is >= 1 and <= 2 && year is >= 0 and <= 99)
            {
                year += 2000;
            }
        }
        else
        {
            return false;
        }

        if (year < 1 || year > 9999 || month < 1 || month > 12 || day < 1 || day > DateTime.DaysInMonth(year, month))
        {
            return false;
        }

        due = new DateOnly(year, month, day);

        return true;
    }
}
