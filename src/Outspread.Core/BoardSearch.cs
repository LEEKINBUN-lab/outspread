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
    public static IReadOnlyList<Hit> Find(CanvasDocument document, string query, int limit = 50, int window = SnippetWindow)
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

        // 组的 id 也算活成员：外层框的名单里写着内层组的 id（嵌套档），别把装着东西的外层当空组藏掉。
        var liveIds = new HashSet<string>(nodes.Select(node => node.Id), StringComparer.Ordinal);

        for (var index = 0; index < nodes.Count; index++)
        {
            var node = nodes[index];

            // 空组（成员全删光了、渲染层也藏起来了）不参与检索：它顶着旧名字出现在结果里，
            // 点了却什么也看不见——他 10-25 报的"测试/分组不在了还搜得到"。
            if (node.Kind == CanvasNodeKind.Group && !GroupHasLiveMember(node, liveNotes, liveIds))
            {
                continue;
            }

            if (!TryMatch(node, terms, out var field, out var matches))
            {
                continue;
            }

            // 同一格里的每一处出现都给一条：他 10-25 要的"同一便签多个匹配就显示多条"。
            var first = matches[0].Start;

            for (var occurrence = 0; occurrence < matches.Count; occurrence++)
            {
                var (start, length) = matches[occurrence];
                var (snippet, offset) = Window(field, start, length, window);

                scored.Add(new Scored(
                    new Hit(node.Id, node.Kind, snippet, offset, length),
                    terms.Length,
                    first,
                    index,
                    occurrence));
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

    /// <summary>A hit plus the ranking keys it was sorted by, kept out of the public shape.</summary>
    private readonly record struct Scored(Hit Hit, int Matched, int FirstPosition, int Index, int Occurrence);

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

        // 先比这一格**第一处**命中的位置（旧的排序语义），再按板上顺序；同一格的多条按出现顺序
        // 挨在一起，不会被别格的行切开。
        var byFirst = a.FirstPosition.CompareTo(b.FirstPosition);

        if (byFirst != 0)
        {
            return byFirst;
        }

        var byIndex = a.Index.CompareTo(b.Index);

        return byIndex != 0 ? byIndex : a.Occurrence.CompareTo(b.Occurrence);
    }

    /// <summary>
    /// Whether <paramref name="node"/> carries every term, and if so the snippet window around its
    /// earliest in-field match plus the offset of the match within that window.
    /// </summary>
    /// <summary>
    /// 这一格是否含全部词；含的话交出摘要字段（折成一行）和该字段里每一处可高亮的出现。
    /// 同一处只算一次；一个词是另一个的前缀时取更长的那个。
    /// </summary>
    private static bool TryMatch(
        CanvasNode node,
        string[] terms,
        out string field,
        out List<(int Start, int Length)> matches)
    {
        field = string.Empty;
        matches = [];

        // Coverage is checked against the raw fields: a term never holds whitespace (the query was
        // split on it), so folding newlines to spaces cannot add or remove a substring match.
        foreach (var term in terms)
        {
            if (!Holds(node, term))
            {
                return false;
            }
        }

        field = SingleLine(BestField(node, terms));
        matches = AllMatches(field, terms);

        return matches.Count > 0;
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

    /// <summary>这一格里每一处可高亮的出现，按位置排序；同一处取更长的词。</summary>
    private static List<(int Start, int Length)> AllMatches(string field, string[] terms)
    {
        var found = new List<(int Start, int Length)>();

        foreach (var term in terms)
        {
            if (term.Length == 0)
            {
                continue;
            }

            var at = 0;

            while (at < field.Length
                && (at = field.IndexOf(term, at, StringComparison.OrdinalIgnoreCase)) >= 0)
            {
                found.Add((at, term.Length));
                at += term.Length;
            }
        }

        found.Sort((a, b) => a.Start != b.Start ? a.Start.CompareTo(b.Start) : b.Length.CompareTo(a.Length));

        var unique = new List<(int Start, int Length)>(found.Count);

        foreach (var match in found)
        {
            if (unique.Count == 0 || unique[^1].Start != match.Start)
            {
                unique.Add(match);
            }
        }

        return unique;
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
