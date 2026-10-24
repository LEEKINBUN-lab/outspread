using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Outspread.Core;

/// <summary>
/// 「所有便签」那一枚标签的数据核心：把若干块板（各带一个 <see cref="CanvasDocument"/>）并成
/// 一张合并文档，再按归属拆回各文件。纯函数，不碰磁盘、不碰 UI。
/// </summary>
/// <remarks>
/// <para>
/// 两块不同的板里可能各有一枚 id 相同的格子（JSON Canvas 的 id 只在单文件内约定唯一）。并成
/// 一张时若不改名，两枚会在按 id 索引的渲染/选中里互相顶掉。所以合并给每个节点 id、以及每条边
/// 端点引用的 id，都打上"归属键 + 分隔符"的命名空间，拆分时还原。归属键用板的完整路径。
/// </para>
/// <para>
/// 坐标一律不动——合并视图里每格仍用它在自己文件里的真实 x/y。于是并集的排布是"各文件的纯函数"，
/// 每次打开都一样，除非某格被删（他 10-22 要的那条：位置和相对关系不许每次重排）。
/// </para>
/// <para>
/// 只搬 nodes/edges 两组；每块板 root 上的其它未知字段由调用方保留（拆分只交回 nodes/edges，
/// 让 UI 塞回各自原始 root 再写盘），所以不会把 agent 写的、本程序不认识的字段丢掉。
/// </para>
/// </remarks>
public static class BoardMerge
{
    /// <summary>命名空间分隔符：一个不会出现在 id 或 Windows 路径里的控制字符。</summary>
    public const string Sep = "\u001F";

    /// <summary>把某文件里的原始 id 包成合并视图里唯一的命名空间 id。</summary>
    public static string MergeId(string ownerKey, string id) => ownerKey + Sep + id;

    /// <summary>还原：命名空间 id → (归属键, 原始 id)。没有分隔符则返回 false。</summary>
    public static bool TryUnmerge(string mergedId, out string ownerKey, out string id)
    {
        var at = mergedId.IndexOf(Sep, StringComparison.Ordinal);

        if (at < 0)
        {
            ownerKey = string.Empty;
            id = mergedId;
            return false;
        }

        ownerKey = mergedId[..at];
        id = mergedId[(at + Sep.Length)..];
        return true;
    }

    /// <summary>这枚命名空间 id 属于哪块板；不是命名空间 id 则为空串。</summary>
    public static string OwnerOf(string mergedId) => TryUnmerge(mergedId, out var owner, out _) ? owner : string.Empty;

    /// <summary>每块板独占一条竖向带，带与带之间的空。并集不再把不同板的格子叠在同一片坐标上。</summary>
    public const double RegionGapPx = 240;

    /// <summary>
    /// 给每块板算一个显示偏移：先把这块板自己的包围盒左上角挪到 x=0、y=<paramref name="gap"/> 之下的游标处，
    /// 再往下走一块。区域内每格的相对坐标一个都不动（<see cref="Merge"/> 只加这个偏移），
    /// 写回时再减掉同一个偏移，源文件里的坐标就还是原来的。
    /// </summary>
    public static Dictionary<string, (double X, double Y)> RegionOffsets(
        IReadOnlyList<(string OwnerKey, CanvasDocument Doc)> boards,
        double gap = RegionGapPx)
    {
        var offsets = new Dictionary<string, (double X, double Y)>(StringComparer.Ordinal);
        var cursor = 0d;

        foreach (var (owner, doc) in boards)
        {
            var nodes = doc.Nodes;

            if (nodes.Count == 0)
            {
                offsets[owner] = (0, cursor);
                cursor += gap;

                continue;
            }

            var left = nodes.Min(node => node.X);
            var top = nodes.Min(node => node.Y);
            var bottom = nodes.Max(node => node.Y + node.Height);

            offsets[owner] = (-left, cursor - top);
            cursor += Math.Max(0, bottom - top) + gap;
        }

        return offsets;
    }

    /// <summary>给一格加/减显示偏移（合并加、写回减）。不是数字就跳过，不改别人的字段。</summary>
    public static void ShiftNode(JsonObject node, double dx, double dy)
    {
        if (Math.Abs(dx) > 0.0001 && ReadNumber(node, "x") is { } x)
        {
            node["x"] = x + dx;
        }

        if (Math.Abs(dy) > 0.0001 && ReadNumber(node, "y") is { } y)
        {
            node["y"] = y + dy;
        }
    }

    /// <summary>
    /// 读一个数：本程序写出来的数可能是 Int64 背书的 JsonValue，<c>GetValue&lt;double&gt;</c> 会拒收
    /// （与 <see cref="CanvasNode.GetNumber"/> 同一条坑）；退一步用 Convert。
    /// </summary>
    private static double? ReadNumber(JsonObject node, string field)
    {
        if (node[field] is not JsonValue value)
        {
            return null;
        }

        try
        {
            return value.GetValue<double>();
        }
        catch (Exception ex) when (ex is InvalidOperationException or JsonException or FormatException)
        {
            try
            {
                return Convert.ToDouble(value.GetValue<object>(), CultureInfo.InvariantCulture);
            }
            catch (Exception inner) when (inner is InvalidOperationException or InvalidCastException or FormatException)
            {
                return null;
            }
        }
    }

    /// <summary>把多块板并成一张 root：每块板按 <paramref name="offsets"/> 挪进自己的区域，id 打命名空间，边端点跟着改名。</summary>
    public static JsonObject Merge(
        IReadOnlyList<(string OwnerKey, CanvasDocument Doc)> boards,
        IReadOnlyDictionary<string, (double X, double Y)>? offsets = null)
    {
        var nodes = new JsonArray();
        var edges = new JsonArray();

        foreach (var (ownerKey, doc) in boards)
        {
            if (doc.Root["nodes"] is JsonArray sourceNodes)
            {
                foreach (var item in sourceNodes)
                {
                    if (item is JsonObject node)
                    {
                        var clone = (JsonObject)node.DeepClone();
                        var shift = offsets is not null && offsets.TryGetValue(ownerKey, out var at) ? at : (X: 0d, Y: 0d);

                        ShiftNode(clone, shift.X, shift.Y);
                        nodes.Add(NamespaceNodeId(ownerKey, clone));
                    }
                }
            }

            if (doc.Root["edges"] is JsonArray sourceEdges)
            {
                foreach (var item in sourceEdges)
                {
                    if (item is JsonObject edge)
                    {
                        edges.Add(NamespaceEdgeEndpoints(ownerKey, (JsonObject)edge.DeepClone()));
                    }
                }
            }
        }

        var root = new JsonObject { ["nodes"] = nodes };

        if (edges.Count > 0)
        {
            root["edges"] = edges;
        }

        return root;
    }

    /// <summary>把一张合并 root 按归属拆回各板的 nodes/edges（id 还原）。归属键没出现过的板不会有条目。</summary>
    public static Dictionary<string, MergedParts> Split(JsonObject mergedRoot)
    {
        var result = new Dictionary<string, MergedParts>(StringComparer.Ordinal);

        if (mergedRoot["nodes"] is JsonArray nodes)
        {
            foreach (var item in nodes)
            {
                if (item is not JsonObject node)
                {
                    continue;
                }

                var clone = (JsonObject)node.DeepClone();
                var owner = UnmergeNodeId(clone);

                Parts(result, owner).Nodes.Add(clone);
            }
        }

        if (mergedRoot["edges"] is JsonArray edges)
        {
            foreach (var item in edges)
            {
                if (item is not JsonObject edge)
                {
                    continue;
                }

                var clone = (JsonObject)edge.DeepClone();
                var owner = OwnerOfEndpoint(clone);
                UnmergeEdgeEndpoints(clone);

                Parts(result, owner).Edges.Add(clone);
            }
        }

        return result;
    }

    private static JsonObject NamespaceNodeId(string ownerKey, JsonObject node)
    {
        if (node["id"] is JsonValue value && value.TryGetValue<string>(out var id))
        {
            node["id"] = MergeId(ownerKey, id);
        }

        return node;
    }

    private static string UnmergeNodeId(JsonObject node)
    {
        if (node["id"] is JsonValue value && value.TryGetValue<string>(out var merged) && TryUnmerge(merged, out var owner, out var id))
        {
            node["id"] = id;
            return owner;
        }

        return string.Empty;
    }

    private static JsonObject NamespaceEdgeEndpoints(string ownerKey, JsonObject edge)
    {
        foreach (var field in EndpointFields)
        {
            if (edge[field] is JsonValue value && value.TryGetValue<string>(out var id))
            {
                edge[field] = MergeId(ownerKey, id);
            }
        }

        return edge;
    }

    private static void UnmergeEdgeEndpoints(JsonObject edge)
    {
        foreach (var field in EndpointFields)
        {
            if (edge[field] is JsonValue value && value.TryGetValue<string>(out var merged) && TryUnmerge(merged, out _, out var id))
            {
                edge[field] = id;
            }
        }
    }

    private static string OwnerOfEndpoint(JsonObject edge)
    {
        foreach (var field in new[] { "fromNode", "a" })
        {
            if (edge[field] is JsonValue value && value.TryGetValue<string>(out var merged) && TryUnmerge(merged, out var owner, out _))
            {
                return owner;
            }
        }

        return string.Empty;
    }

    // 边引用节点 id 的两种拼法：spec 的 fromNode/toNode、原型的 a/b。fromSide/toSide 是"边"不是 id，不动。
    private static readonly string[] EndpointFields = ["fromNode", "toNode", "a", "b"];

    private static MergedParts Parts(Dictionary<string, MergedParts> result, string owner)
    {
        if (!result.TryGetValue(owner, out var parts))
        {
            parts = new MergedParts();
            result[owner] = parts;
        }

        return parts;
    }

    /// <summary>拆回某一块板的那部分：还原 id 之后的节点与边。调用方塞回该板原始 root 再写盘。</summary>
    public sealed class MergedParts
    {
        public JsonArray Nodes { get; } = [];

        public JsonArray Edges { get; } = [];
    }
}
