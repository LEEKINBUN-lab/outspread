using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Outspread.Core;

/// <summary>
/// 一块板上**被选中的那几格**，拆下来单独带得走的样子：一份能贴回任何一块板的 JSON Canvas
/// 片段，加一份给别的程序看的纯文字。
/// </summary>
/// <remarks>
/// <para>
/// 为什么要"一份东西两个门面"：按 Ctrl+C 的人可能是在往同一扇窗口的另一枚标签里搬，也可能是在
/// 往记事本里搬。前者要的是格子的颜色、大小、图、线头——那些在 JSON 里；后者打开剪贴板一看是
/// <c>{"nodes":[</c> 就等于这发复制对它是废的。所以剪贴板上放的是<b>几行谁都能读的字</b>，
/// 这份带结构的片段落在应用自己的落盘件里（<c>BoardClipboard</c>），贴的时候两样一起认——
/// 不走剪贴板的自定义格式是因为这一版 WinUI 投影里 <c>DataPackageView</c> 只有写没有读。
/// </para>
/// <para>
/// 片段本身**就是一份合法的 .canvas**（<c>{"nodes":[…],"edges":[…]}</c>），不是另一个私有格式：
/// 认这个格式的程序不用学新东西。我们自己的那式多带两个键：<c>sourceBoard</c>（图那张格子的
/// 字节不在片段里，在原来那块板旁边的 <c>assets/</c> 里）与 <c>plainText</c>（剪贴板上那几行字的
/// 原文）——贴的时候用它判"这份片段还是不是刚复制的那一发"，别人又复制了别的东西就不算。
/// </para>
/// <para>
/// id 不在这里铸：贴进哪块板由那块板自己命名（<c>CanvasDocument.AppendNode</c>），否则同一份
/// 片段贴两次会留下两格互相指认不了的东西。
/// </para>
/// </remarks>
public static class BoardFragment
{
    private const string SourceBoard = "sourceBoard";

    private const string PlainTextKey = "plainText";

    /// <summary>
    /// 把选中的那几格拆成一份片段。<paramref name="ids"/> 给的是便签（组与线头在这里头自己配）。
    /// </summary>
    /// <param name="ids">要带走的那些格的 id。顺序就是数组顺序（贴回去的叠放次序跟它一致）。</param>
    /// <param name="plainText">给 <c>text/plain</c> 的那几行字：一行一格，图与链接写它指着什么。</param>
    public static string? From(
        CanvasDocument document,
        IReadOnlyList<string> ids,
        string boardPath,
        out string plainText)
    {
        var taken = ids.Distinct(StringComparer.Ordinal).ToList();

        // 组跟着成员走：只搬成员会把那圈虚线框丢在原地，贴过去的一堆格子就不再是一件东西。
        // 规则是"组里有一格被选中，组就进来"——半选也要有框，否则贴出来的排布和屏幕上看到的不一样。
        // 两种成员关系都认：本应用写的是框上的 members 名单（他 11-03 报的"复制粘贴后分组丢失"
        // 就是这里只认了成员的 group 字段）；别的工具写的板子用成员的 group 字段。
        var groupIds = document.Nodes
            .Where(node => node.Kind == CanvasNodeKind.Group
                && ((node.GroupMemberIds?.Any(member => taken.Contains(member, StringComparer.Ordinal)) ?? false)
                    || document.Nodes.Any(member => member.Kind != CanvasNodeKind.Group
                        && member.GetString("group") == node.Id
                        && taken.Contains(member.Id, StringComparer.Ordinal))))
            .Select(node => node.Id)
            .ToList();

        var inside = taken.Concat(groupIds).ToHashSet(StringComparer.Ordinal);

        var nodes = new JsonArray();

        foreach (var id in taken.Concat(groupIds))
        {
            if (document.Find(id) is { } node)
            {
                nodes.Add(node.Json.DeepClone());
            }
        }

        var edges = new JsonArray();

        // 两头都在这一堆里的线才带得走：一头在原地，贴过去就成了一条指着不存在的格子的线。
        foreach (var edge in document.Edges.Where(edge => inside.Contains(edge.FromNode) && inside.Contains(edge.ToNode)))
        {
            edges.Add(edge.Raw.DeepClone());
        }

        plainText = PlainText(document, inside);

        if (nodes.Count == 0)
        {
            return null;
        }

        // 一行都没字的一堆格子也要有一句可对账的话：那几行字同时是剪贴板上的内容和片段的凭据，
        // 两边各写一套就会错开（空的那一发永远贴不出来）。
        if (plainText.Length == 0)
        {
            plainText = "（空格子）";
        }

        var root = new JsonObject
        {
            ["nodes"] = nodes,
            ["edges"] = edges,
            [SourceBoard] = boardPath,
            [PlainTextKey] = plainText,
        };

        return CanvasDocument.Emit(root);
    }

    /// <summary>
    /// 这串字是一段能贴的片段吗？只认"有 <c>nodes</c> 数组"这一条：剪贴板上的文本什么都有可能，
    /// 判宽了会把人家正常贴进来的一段 JSON 当成板子。
    /// </summary>
    public static bool LooksLikeFragment(string? text)
    {
        if (string.IsNullOrWhiteSpace(text) || !text.TrimStart().StartsWith('{'))
        {
            return false;
        }

        try
        {
            return JsonNode.Parse(text) is JsonObject root && root["nodes"] is JsonArray { Count: > 0 };
        }
        catch (JsonException)
        {
            return false;
        }
    }

    /// <summary>片段里那几枚节点（顺序照原样）。读不懂返回空表，由调用方去走"贴一段文字"那条路。</summary>
    public static IReadOnlyList<JsonObject> NodesOf(string? text)
    {
        try
        {
            return JsonNode.Parse(text ?? string.Empty) is JsonObject root && root["nodes"] is JsonArray array
                ? array.OfType<JsonObject>().ToList()
                : [];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    /// <summary>片段里两头都在内的线。带外头的直接丢：贴过去是一条指着不存在的格子的线。</summary>
    public static IReadOnlyList<JsonObject> EdgesOf(string? text, IReadOnlyDictionary<string, string> idMap)
    {
        try
        {
            if (JsonNode.Parse(text ?? string.Empty) is not JsonObject root || root["edges"] is not JsonArray array)
            {
                return [];
            }

            return array.OfType<JsonObject>()
                .Where(edge => idMap.ContainsKey(Text(edge, "fromNode") ?? string.Empty)
                    && idMap.ContainsKey(Text(edge, "toNode") ?? string.Empty))
                .ToList();
        }
        catch (JsonException)
        {
            return [];
        }
    }

    /// <summary>这份片段该在剪贴板上留下哪几行字——对不上就说明人又复制了别的东西，这份不算数。</summary>
    public static string? ExpectedTextOf(string? text)
    {
        try
        {
            return JsonNode.Parse(text ?? string.Empty) is JsonObject root ? Text(root, PlainTextKey) : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>这些格子原来在哪块板旁边——图那张格子的字节在那儿的 <c>assets/</c> 里。</summary>
    public static string? SourceBoardOf(string? text)
    {
        try
        {
            return JsonNode.Parse(text ?? string.Empty) is JsonObject root ? Text(root, SourceBoard) : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// 一行一格。图写它的文件名、链接写它指向的地址——别的程序拿到这三行，至少知道复制的是几格
    /// 什么东西，而不是一串 <c>{"nodes":[</c>。
    /// </summary>
    public static string PlainText(CanvasDocument document, IReadOnlyCollection<string> ids)
    {
        var builder = new StringBuilder();

        foreach (var node in document.Nodes.Where(node => ids.Contains(node.Id, StringComparer.Ordinal)))
        {
            var line = node.Kind switch
            {
                CanvasNodeKind.File => node.FilePath ?? node.Label ?? "(一张没有路径的图)",
                CanvasNodeKind.Link => node.Url ?? node.Label ?? "(一条没有地址的线)",
                CanvasNodeKind.Group => node.Label ?? "(一组)",
                _ => node.Text ?? string.Empty,
            };

            builder.Append(line.TrimEnd()).Append('\n');
        }

        // 逐格一行用 LF：`AppendLine` 给的是平台行尾，那几行字要同时进剪贴板和片段的
        // `plainText` 做对账——一处 CRLF 就让"这份还是不是刚复制的那一发"永远对不上。
        return builder.ToString().TrimEnd('\n');
    }

    private static string? Text(JsonObject? obj, string name)
        => obj is not null && obj.TryGetPropertyValue(name, out var value) && value?.GetValueKind() == JsonValueKind.String
            ? value.GetValue<string>()
            : null;
}
