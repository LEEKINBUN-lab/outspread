using System.Text;

namespace Outspread.Core;

/// <summary>
/// Full-text search over one board: every node a query can land on, each carrying the snippet the
/// UI will show and the span inside that snippet it will emphasise.
/// </summary>
/// <remarks>
/// <para>
/// A query is split on whitespace and every term must be found somewhere on the node (an AND). The
/// board's premise is "paste a paragraph and forget about it", and by the time someone searches they
/// remember two or three words, not their order — so order does not matter, but narrowing does: one
/// term that only ever lives inside a longer word would drag half the board back.
/// </para>
/// <para>
/// Matching is a plain case-insensitive substring, with no fuzzy, no pinyin and no stemming. The
/// corpus is mostly Chinese, where a substring <i>is</i> the correct semantic (there are no word
/// boundaries to get wrong) and a fuzzy match would only manufacture hits that are not there. The one
/// thing that is deliberately left out is whole-word: <c>@t</c>/<c>@d</c> marks live in the text, so
/// a matcher that treated a token as a word would lose the very thing the board filters on.
/// </para>
/// <para>
/// A node is searched through its fields in priority order — <c>Text</c>, then <c>Label</c>, then
/// <c>Url</c>, then <c>FilePath</c>; a null field is skipped and contributes nothing. The snippet is
/// cut from the first field in that order which actually carries a term, which is <c>Text</c> for any
/// note that matched on its own body. So a term that only a link's <c>Url</c> or a group's
/// <c>Label</c> satisfies still makes the node a hit, and the highlight lands on the field that
/// produced the snippet rather than an empty body.
/// </para>
/// <para>
/// The snippet is forced to a single line because the UI shows one per row and a row cannot hold a
/// line break; runs of newlines are folded to one space and all match offsets are computed on the
/// folded text, so <see cref="Hit.MatchStart"/>/<see cref="Hit.MatchLength"/> index into
/// <see cref="Hit.Snippet"/> and never past its end. Ranking is stable and total: most terms matched
/// first, then the earliest match position, then document order. Document order is the tiebreak
/// because <c>Nodes</c> is the board's z-order, so the result list reads top-down exactly like the
/// canvas — and because the index is unique it makes the order total, which is what lets an unstable
/// <see cref="List{T}.Sort(Comparison{T})}" /> still land identical nodes in board order.
/// </para>
/// </remarks>
public static class BoardSearch
{
    /// <summary>
    /// One matched node: what it is, the ~<see cref="SnippetWindow"/>-character window the UI shows,
    /// and the half-open span inside that window to bold.
    /// </summary>
    /// <remarks>
    /// <see cref="MatchStart"/> and <see cref="MatchLength"/> are offsets into <see cref="Snippet"/>,
    /// not into the node's stored text — they are guaranteed to satisfy
    /// <c>0 &lt;= MatchStart</c> and <c>MatchStart + MatchLength &lt;= Snippet.Length</c>, because the
    /// only thing that turns a search crash into a UI crash is bolding a range that runs off the end.
    /// </remarks>
    public readonly record struct Hit(
        string NodeId,
        CanvasNodeKind Kind,
        string Snippet,
        int MatchStart,
        int MatchLength);

    /// <summary>The window the snippet is cut to, before either ellipsis is added.</summary>
    private const int SnippetWindow = 80;

    /// <summary>
    /// The nodes carrying every term in <paramref name="query"/>, best match first, capped at
    /// <paramref name="limit"/>. An empty or whitespace-only query, or a non-positive limit, returns
    /// nothing — an empty query means "not typed yet", not "match everything".
    /// </summary>
    public static IReadOnlyList<Hit> Find(CanvasDocument document, string query, int limit = 50)
    {
        if (document is null || limit <= 0)
        {
            return [];
        }

        var terms = Split(query);

        if (terms.Length == 0)
        {
            return [];
        }

        var nodes = document.Nodes;
        var scored = new List<Scored>(nodes.Count);
        var liveNotes = nodes.Where(node => node.Kind != CanvasNodeKind.Group).ToList();
        var liveIds = new HashSet<string>(liveNotes.Select(node => node.Id), StringComparer.Ordinal);

        for (var index = 0; index < nodes.Count; index++)
        {
            var node = nodes[index];

            // 空组（成员全删光了、渲染层也藏起来了）不参与检索：它顶着旧名字出现在结果里，
            // 点了却什么也看不见——他 10-25 报的"测试/分组不在了还搜得到"。
            if (node.Kind == CanvasNodeKind.Group && !GroupHasLiveMember(node, liveNotes, liveIds))
            {
                continue;
            }

            if (TryScore(node, terms, out var position, out var length, out var snippet, out var start))
            {
                scored.Add(new Scored(new Hit(node.Id, node.Kind, snippet, start, length), terms.Length, position, index));
            }
        }

        // The document index closes the comparison, so no two entries ever tie and the result does not
        // depend on List.Sort's lack of stability.
        scored.Sort(Order);

        if (scored.Count > limit)
        {
            scored.RemoveRange(limit, scored.Count - limit);
        }

        var hits = new List<Hit>(scored.Count);

        for (var i = 0; i < scored.Count; i++)
        {
            hits.Add(scored[i].Hit);
        }

        return hits;
    }

    /// <summary>A hit plus the three ranking keys it was sorted by, kept out of the public shape.</summary>
    private readonly record struct Scored(Hit Hit, int Matched, int Position, int Index);

    private static int Order(Scored a, Scored b)
    {
        // More terms first. Under AND every result matched the whole query, so today this key is
        // constant; it leads the ranking so that loosening AND to a soft ranking later cannot silently
        // reorder what callers already depend on.
        var byTerms = b.Matched.CompareTo(a.Matched);

        if (byTerms != 0)
        {
            return byTerms;
        }

        var byPosition = a.Position.CompareTo(b.Position);

        if (byPosition != 0)
        {
            return byPosition;
        }

        return a.Index.CompareTo(b.Index);
    }

    /// <summary>
    /// Whether <paramref name="node"/> carries every term, and if so the snippet window around its
    /// earliest in-field match plus the offset of the match within that window.
    /// </summary>
    private static bool TryScore(
        CanvasNode node,
        string[] terms,
        out int position,
        out int length,
        out string snippet,
        out int start)
    {
        position = 0;
        length = 0;
        snippet = string.Empty;
        start = 0;

        // Coverage is checked against the raw fields: a term never holds whitespace (the query was
        // split on it), so folding newlines to spaces cannot add or remove a substring match.
        foreach (var term in terms)
        {
            if (!Holds(node, term))
            {
                return false;
            }
        }

        var field = SingleLine(BestField(node, terms));

        EarliestMatch(field, terms, out position, out length);

        (snippet, start) = Window(field, position, length, SnippetWindow);

        return true;
    }

    /// <summary>Whether any searchable field of the node contains the term.</summary>
    private static bool Holds(CanvasNode node, string term)
        => Contains(node.Text, term)
            || Contains(node.Label, term)
            || Contains(node.Url, term)
            || Contains(node.FilePath, term);

    private static bool Contains(string? field, string term)
        => field is not null && field.IndexOf(term, StringComparison.OrdinalIgnoreCase) >= 0;

    /// <summary>
    /// The highest-priority field that carries at least one term — the field the snippet is drawn from.
    /// </summary>
    private static string BestField(CanvasNode node, string[] terms)
    {
        if (HoldsAny(node.Text, terms))
        {
            return node.Text!;
        }

        if (HoldsAny(node.Label, terms))
        {
            return node.Label!;
        }

        if (HoldsAny(node.Url, terms))
        {
            return node.Url!;
        }

        if (HoldsAny(node.FilePath, terms))
        {
            return node.FilePath!;
        }

        // Unreachable for a scored node: every term was found in some field, so a field holds one.
        return string.Empty;
    }

    private static bool HoldsAny(string? field, string[] terms)
    {
        if (field is null)
        {
            return false;
        }

        foreach (var term in terms)
        {
            if (field.IndexOf(term, StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>The earliest occurrence of any term in the field; a longer term wins an exact tie.</summary>
    private static void EarliestMatch(string field, string[] terms, out int position, out int length)
    {
        position = 0;
        length = 0;
        var found = false;

        foreach (var term in terms)
        {
            var at = field.IndexOf(term, StringComparison.OrdinalIgnoreCase);

            if (at < 0)
            {
                continue;
            }

            // 同一处取更长的那个词：一个词是另一个的前缀时，高亮该盖住更完整的写法。
            if (!found || at < position || (at == position && term.Length > length))
            {
                position = at;
                length = term.Length;
                found = true;
            }
        }
    }

    /// <summary>
    /// Folds every run of line breaks into a single space so the snippet is one line. Positions are
    /// computed on the folded text — the blank line that collapses <c>\n\n</c> to one space is exactly
    /// what shifts an offset and what a caller must not compute against the raw string.
    /// </summary>
    private static string SingleLine(string text)
    {
        // The common case is a one-line note; scanning for a break before building keeps the hot path
        // from allocating a copy of every field it reads.
        if (text.IndexOf('\n') < 0 && text.IndexOf('\r') < 0)
        {
            return text;
        }

        var builder = new StringBuilder(text.Length);
        var inBreak = false;

        for (var i = 0; i < text.Length; i++)
        {
            if (text[i] is '\r' or '\n')
            {
                if (!inBreak)
                {
                    builder.Append(' ');
                    inBreak = true;
                }

                continue;
            }

            inBreak = false;
            builder.Append(text[i]);
        }

        return builder.ToString();
    }

    /// <summary>
    /// A window of at most <paramref name="max"/> characters around <paramref name="match"/>, with a
    /// leading/trailing <c>…</c> for whatever was cut off. <paramref name="match"/> and
    /// <paramref name="length"/> index into <paramref name="text"/>; the returned offsets index into
    /// the returned snippet.
    /// </summary>
    private static (string Snippet, int MatchStart) Window(string text, int match, int length, int max)
    {
        if (text.Length <= max)
        {
            return (text, match);
        }

        var width = Math.Max(max, length); // the window never clips the matched term itself
        var side = width - length;

        var start = match - (side / 2);

        if (start < 0)
        {
            start = 0;
        }

        if (start + width > text.Length)
        {
            start = text.Length - width;
        }

        if (start < 0)
        {
            start = 0;
        }

        var lead = start > 0;
        var tail = start + width < text.Length;
        var body = text.Substring(start, width);

        // The leading ellipsis is one UTF-16 character (U+2026), so an offset into the snippet is the
        // same offset into the body plus that one character; the trailing ellipsis never shifts it.
        var snippet = (lead ? "…" + body : body) + (tail ? "…" : string.Empty);
        var offset = match - start + (lead ? 1 : 0);

        return (snippet, offset);
    }

    private static string[] Split(string? query)
        => string.IsNullOrWhiteSpace(query)
            ? []
            : query!.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);

    /// <summary>
    /// 这个框还有没有活着的成员：有名单就按名单（指针指向已删格子的不复存在）；
    /// 没有名单的老框按几何收人。空组在画布上是隐形的，检索里也不该出现。
    /// </summary>
    private static bool GroupHasLiveMember(CanvasNode group, IReadOnlyList<CanvasNode> notes, HashSet<string> liveIds)
    {
        if (group.GroupMemberIds is { } roster)
        {
            return roster.Any(liveIds.Contains);
        }

        return notes.Any(note =>
            note.X >= group.X && note.X <= group.X + group.Width
            && note.Y >= group.Y && note.Y <= group.Y + group.Height);
    }
}
