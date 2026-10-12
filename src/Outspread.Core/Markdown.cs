using System.Text.RegularExpressions;

namespace Outspread.Core;

/// <summary>What a rendered markdown line is: a heading of some level, a list item, a checkbox, a
/// quote, or plain text.</summary>
public enum BlockKind
{
    Plain,
    Heading1,
    Heading2,
    Heading3,
    Bullet,
    Numbered,
    TodoOpen,
    TodoDone,
    Quote,
    Divider,
}

/// <summary>Which inline decoration a piece of text carries.</summary>
[Flags]
public enum SpanStyle
{
    None = 0,
    Bold = 1,
    Italic = 2,
    Code = 4,
    Link = 8,
}

/// <summary>One run of text with one style, inside a line.</summary>
/// <param name="Text">The words as they should read, markers removed.</param>
/// <param name="Style">What decorates them.</param>
public readonly record struct Span(string Text, SpanStyle Style);

/// <summary>
/// One painted line of a card: how big it is and what runs it holds.
/// </summary>
/// <param name="Kind">The block it was written as.</param>
/// <param name="Scale">Its size relative to the note's own font size.</param>
/// <param name="Prefix">Text drawn before the runs — a bullet or a checkbox. A quote carries none;
/// its bar is a box, drawn by the card.</param>
/// <param name="Spans">The runs, markers already removed.</param>
public readonly record struct MarkdownLine(BlockKind Kind, double Scale, string Prefix, IReadOnlyList<Span> Spans)
{
    /// <summary>Whether anything on this line is bold, which is what a heading's size is paired with.</summary>
    public bool Bold => Kind is BlockKind.Heading1 or BlockKind.Heading2 or BlockKind.Heading3
        || Spans.Any(span => (span.Style & SpanStyle.Bold) != 0);

    /// <summary>The whole line as plain text, for measuring and for the ellipsis.</summary>
    public string Text => Prefix + string.Concat(Spans.Select(span => span.Text));
}

/// <summary>
/// The markdown subset a card actually renders — the spec says a text node's content is "plain text
/// with Markdown syntax", so this is the format's own promise, kept.
/// </summary>
/// <remarks>
/// <para>
/// Scope, decided in docs/research-chrome-and-properties.md §3: headings 1–3, bold, italic, inline
/// code, bullet lists, checkboxes, blockquotes, and links (their text, underlined — a card cannot
/// also be a hyperlink target while it is being dragged). <b>Not</b> tables, images, HTML, footnotes,
/// or nested lists deeper than one level.
/// </para>
/// <para>
/// Two rules are load-bearing and both come from what he asked for:
/// </para>
/// <list type="number">
/// <item><description>The editor still shows the raw text. Rendered-while-editing would need a
/// two-way conversion between markup and styled runs, which is where the interesting data-loss bugs
/// live; Obsidian and Excalidraw both keep raw-in-editor for the same reason.</description></item>
/// <item><description>Heights come from <see cref="BlockHeight"/>: the same function the fit-measure
/// and the paint call, so a heading that makes the first line taller can never leave the last line
/// cut off — the one defect class this whole file has been fighting.</description></item>
/// </list>
/// </remarks>
public static class Markdown
{
    public const double Heading1Scale = 1.35;
    public const double Heading2Scale = 1.2;

    private static readonly Regex HeadingPattern = new(@"^(?<hashes>#{1,3})\s+(?<text>.*)$", RegexOptions.Compiled);

    private static readonly Regex BulletPattern = new(@"^[-*+]\s+(?<text>.*)$", RegexOptions.Compiled);

    /// <summary><c>1. 项</c> or <c>1) 项</c>. The number the writer typed is kept as the prefix — a
    /// card that renumbers a list the author numbered is a card that contradicts its own text.</summary>
    private static readonly Regex NumberedPattern = new(@"^(?<num>\d{1,2})[.)]\s+(?<text>.*)$", RegexOptions.Compiled);

    /// <summary><c>---</c>, <c>***</c> or <c>___</c>: one rule across the card, nothing else.</summary>
    private static readonly Regex DividerPattern = new(@"^(?:-{3,}|\*{3,}|_{3,})$", RegexOptions.Compiled);

    private static readonly Regex TodoPattern = new(@"^[-*+]\s+\[(?<mark>[ xX])\]\s*(?<text>.*)$", RegexOptions.Compiled);

    private static readonly Regex QuotePattern = new(@"^>\s?(?<text>.*)$", RegexOptions.Compiled);

    private static readonly Regex InlinePattern = new(
        @"(\*\*(?<bold>.+?)\*\*|\*(?<italic>.+?)\*|`(?<code>.+?)`|\[(?<linkText>[^\]]*)\]\((?<url>[^)]*)\))",
        RegexOptions.Compiled);

    /// <summary>Whether a card should be painted as blocks at all. A note with no markup in it stays
    /// the single text block it always was, so this cannot change how an ordinary note looks.</summary>
    public static bool LooksLikeMarkdown(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return false;
        }

        foreach (var line in text.Split('\n'))
        {
            var trimmed = line.Trim();

            if (HeadingPattern.IsMatch(trimmed) || BulletPattern.IsMatch(trimmed) || QuotePattern.IsMatch(trimmed)
                || NumberedPattern.IsMatch(trimmed) || DividerPattern.IsMatch(trimmed))
            {
                return true;
            }

            if (trimmed.Contains("**", StringComparison.Ordinal)
                || trimmed.Contains('`')
                || trimmed.Contains("](", StringComparison.Ordinal)
                || (trimmed.Contains('*') && InlinePattern.IsMatch(trimmed)))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Parses a body into the lines a card paints. Blank lines are kept (they are spacing
    /// the writer chose) but carry no runs.</summary>
    public static IReadOnlyList<MarkdownLine> Lines(string? body)
    {
        var lines = new List<MarkdownLine>();

        foreach (var raw in (body ?? string.Empty).Split('\n'))
        {
            lines.Add(Line(raw.Trim()));
        }

        return lines;
    }

    private static MarkdownLine Line(string trimmed)
    {
        if (trimmed.Length == 0)
        {
            return new MarkdownLine(BlockKind.Plain, 1, string.Empty, []);
        }

        var todo = TodoPattern.Match(trimmed);

        if (todo.Success)
        {
            var done = todo.Groups["mark"].Value is "x" or "X";

            return new MarkdownLine(
                done ? BlockKind.TodoDone : BlockKind.TodoOpen,
                1,
                done ? "☑ " : "☐ ",
                Inlines(todo.Groups["text"].Value));
        }

        if (DividerPattern.IsMatch(trimmed))
        {
            // No text, no prefix: the card draws the rule itself. It still costs one line box, so
            // the height arithmetic below stays "one block = one leading" with no special case.
            return new MarkdownLine(BlockKind.Divider, 1, string.Empty, []);
        }

        var numbered = NumberedPattern.Match(trimmed);

        if (numbered.Success)
        {
            return new MarkdownLine(
                BlockKind.Numbered,
                1,
                $"{numbered.Groups["num"].Value}. ",
                Inlines(numbered.Groups["text"].Value));
        }

        var heading = HeadingPattern.Match(trimmed);

        if (heading.Success)
        {
            var level = heading.Groups["hashes"].Value.Length;

            return new MarkdownLine(
                level switch { 1 => BlockKind.Heading1, 2 => BlockKind.Heading2, _ => BlockKind.Heading3 },
                level switch { 1 => Heading1Scale, 2 => Heading2Scale, _ => 1 },
                string.Empty,
                Inlines(heading.Groups["text"].Value));
        }

        var quote = QuotePattern.Match(trimmed);

        if (quote.Success)
        {
            // No prefix glyph: the prototype draws the quote as a 2px box-border with 8px of gap,
            // and a "│" in the text would be the same bar twice, one of them misaligned by the
            // glyph's own advance. The line's own Text is then the quoted words, which is what the
            // ellipsis and the measure need.
            return new MarkdownLine(BlockKind.Quote, 1, string.Empty, Inlines(quote.Groups["text"].Value));
        }

        var bullet = BulletPattern.Match(trimmed);

        if (bullet.Success)
        {
            return new MarkdownLine(BlockKind.Bullet, 1, "• ", Inlines(bullet.Groups["text"].Value));
        }

        return new MarkdownLine(BlockKind.Plain, 1, string.Empty, Inlines(trimmed));
    }

    /// <summary>Splits one line into runs, dropping the markers. Unmatched markers stay as written —
    /// a stray <c>**</c> is the writer's text, not a parse error to swallow.</summary>
    public static IReadOnlyList<Span> Inlines(string text)
    {
        var spans = new List<Span>();
        var rest = text;

        while (rest.Length > 0)
        {
            var match = InlinePattern.Match(rest);

            if (!match.Success)
            {
                spans.Add(new Span(rest, SpanStyle.None));
                break;
            }

            if (match.Index > 0)
            {
                spans.Add(new Span(rest[..match.Index], SpanStyle.None));
            }

            if (match.Groups["bold"].Success)
            {
                spans.Add(new Span(match.Groups["bold"].Value, SpanStyle.Bold));
            }
            else if (match.Groups["italic"].Success)
            {
                spans.Add(new Span(match.Groups["italic"].Value, SpanStyle.Italic));
            }
            else if (match.Groups["code"].Success)
            {
                spans.Add(new Span(match.Groups["code"].Value, SpanStyle.Code));
            }
            else
            {
                spans.Add(new Span(match.Groups["linkText"].Value, SpanStyle.Link));
            }

            rest = rest[(match.Index + match.Length)..];
        }

        return spans;
    }

    /// <summary>
    /// How tall one line's box is, in world units. The text engine takes a whole number of pixels
    /// per line, so this rounds once per line and the paint draws exactly this — the two cannot
    /// then disagree about where the last line ends.
    /// <para>
    /// Measured in this projection, not assumed (the card self-test prints the table):
    /// 21→21, 25.2→25, 28.35→28, 21.5→22, 28.5→29, 28.6→29, 30.5→31 — half-up, so
    /// <see cref="MidpointRounding.AwayFromZero"/> rather than the default banker's rounding, which
    /// would have drawn 21.5 as 21×… and 28.5 as 28.
    /// </para>
    /// <para>
    /// The prototype draws a heading's line box as 28.34px because a browser keeps fractions; this
    /// says 28. That is the one place the two cannot be 1:1, and it is under a pixel per heading,
    /// in the direction that keeps the text inside its frame rather than cutting the last line.
    /// </para>
    /// </summary>
    public static double LineHeight(NoteType type, MarkdownLine line)
        => Math.Round(type.Leading * line.Scale, MidpointRounding.AwayFromZero);

    /// <summary>The height a block of lines needs, in world units: the sum of the boxes above.</summary>
    public static double BlockHeight(NoteType type, IReadOnlyList<MarkdownLine> lines)
    {
        var height = 0.0;

        foreach (var line in lines)
        {
            height += LineHeight(type, line);
        }

        return height;
    }

    /// <summary>How many of these lines the room holds, charged exactly as the paint draws them.</summary>
    public static int Fits(NoteType type, IReadOnlyList<MarkdownLine> lines, double room)
    {
        var used = 0.0;
        var count = 0;

        foreach (var line in lines)
        {
            used += LineHeight(type, line);

            if (used > room)
            {
                break;
            }

            count++;
        }

        return count;
    }
}
