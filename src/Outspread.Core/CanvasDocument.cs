using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Outspread.Core;

/// <summary>
/// A JSON Canvas 1.0 document, kept as the live JSON tree it was read as.
/// </summary>
/// <remarks>
/// The document is deliberately not projected onto a typed model: the spec has
/// no statement about unknown properties, so anything this program does not
/// model would be dropped on the next save. Agents write edges, groups and
/// fields we have not met yet, and those must survive a round trip through the
/// board untouched.
/// </remarks>
public sealed class CanvasDocument
{
    private readonly JsonObject _root;
    private readonly JsonArray _nodes;

    private CanvasDocument(JsonObject root, JsonArray nodes)
    {
        _root = root;
        _nodes = nodes;
    }

    public JsonObject Root => _root;

    /// <summary>缓存的那一份包装列表，与逐槽的 JSON 引用（用来验缓存有没有作废）。</summary>
    private List<CanvasNode>? _nodesCache;

    private JsonNode?[]? _nodesCacheSlots;

    /// <summary>
    /// 节点表。**惰性缓存 + 逐槽身份校验**：读的时候先比"数量＋每一槽是不是同一个 JSON 对象"，
    /// 全都对得上就直接把上一份交回去，不再为每一格 new 一枚包装。
    /// </summary>
    /// <remarks>
    /// 为什么认引用而不是打脏标记：能改这张表的路子有三类，靠人记得通知一定会漏——
    /// ① 自己的 <c>Add</c>/<c>AppendNode</c>/<c>Remove</c>/<c>Move</c>；
    /// ② <c>NodeArray</c> 这个口子（<c>PatchApplier</c> 会把它作用在**活动文档**上，增删与整表重排都可能）；
    /// ③ 整份文档换掉（载入、撤销、恢复、跨板并集）。
    /// 认引用三类全覆盖：谁把某一槽换成了别的 JSON 对象，这里立刻看出来。
    /// 只改值（patch 写某个字段）不换引用，包装本来就是活的，天然正确。
    /// </remarks>
    public IReadOnlyList<CanvasNode> Nodes
    {
        get
        {
            if (_nodesCache is { } cache && SlotsStillMatch())
            {
                return cache;
            }

            var list = new List<CanvasNode>(_nodes.Count);
            var slots = new JsonNode?[_nodes.Count];

            for (var index = 0; index < _nodes.Count; index++)
            {
                var item = _nodes[index];

                slots[index] = item;

                if (item is JsonObject obj)
                {
                    list.Add(new CanvasNode(obj));
                }
            }

            _nodesCache = list;
            _nodesCacheSlots = slots;

            return list;
        }
    }

    /// <summary>
    /// 缓存还成不成立：数量一致，且每一槽还是当初那个 JSON 对象（引用比较，不读属性、不分配）。
    /// 这是"不靠谁通知我"的全部依仗——漏判一条就会把旧包装交回去，所以下面那发测试专钉这一点。
    /// </summary>
    private bool SlotsStillMatch()
    {
        if (_nodesCacheSlots is not { } slots || slots.Length != _nodes.Count || _nodesCache is not { } cache
            || cache.Count != slots.Length)
        {
            return false;
        }

        for (var index = 0; index < slots.Length; index++)
        {
            if (!ReferenceEquals(slots[index], _nodes[index]))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// The connections, as the file writes them. Reading is tolerant of both the spec's
    /// <c>fromNode/fromSide</c> and the prototype's <c>a/fs</c> spelling, because the prototype board
    /// predates the spec keys and its edges must still draw. Writing always uses the spec keys.
    /// </summary>
    public IReadOnlyList<CanvasEdge> Edges
    {
        get
        {
            var list = new List<CanvasEdge>();

            if (_root["edges"] is not JsonArray array)
            {
                return list;
            }

            foreach (var item in array)
            {
                if (item is not JsonObject obj)
                {
                    continue;
                }

                var id = Text(obj, "id");
                var from = Text(obj, "fromNode") ?? Text(obj, "a");
                var to = Text(obj, "toNode") ?? Text(obj, "b");

                if (id is null || from is null || to is null)
                {
                    continue;
                }

                list.Add(new CanvasEdge(
                    id,
                    from,
                    to,
                    Text(obj, "fromSide") ?? Text(obj, "fs"),
                    Text(obj, "toSide") ?? Text(obj, "ts"),
                    obj));
            }

            return list;
        }
    }

    private static string? Text(JsonObject obj, string field)
        => obj[field] is JsonValue value && value.TryGetValue(out string? text) ? text : null;

    /// <summary>
    /// Records a connection. The sides are written out (rather than left to the automatic reading) so
    /// the picture does not move when a card is dragged past the midpoint — the prototype learned this
    /// by writing them at creation time, and the spec has the fields for it.
    /// </summary>
    /// <param name="fromSide">留空＝自动朝向对方；拉线那一发把人挑的那一面写进来。</param>
    public CanvasEdge AddEdge(string fromId, string toId, string? fromSide = null, string? toSide = null)
    {
        var from = Find(fromId) ?? throw new ArgumentException($"板上没有 id {fromId}", nameof(fromId));
        var to = Find(toId) ?? throw new ArgumentException($"板上没有 id {toId}", nameof(toId));
        var auto = CanvasEdge.AutoSides(from, to);

        fromSide ??= auto.From;
        toSide ??= auto.To;

        if (_root["edges"] is not JsonArray array)
        {
            array = [];
            _root["edges"] = array;
        }

        var obj = new JsonObject
        {
            ["id"] = NodeIds.NewAppId(),
            ["fromNode"] = fromId,
            ["fromSide"] = fromSide,
            ["toNode"] = toId,
            ["toSide"] = toSide,
        };

        array.Add(obj);

        return new CanvasEdge((string)obj["id"]!, fromId, toId, fromSide, toSide, obj);
    }

    /// <summary>Puts an edge object onto the board as it came from a patch. An id that is already
    /// there is left alone — the first one wins, so two writers cannot double-draw one arrow.</summary>
    public void AppendEdge(JsonObject edge)
    {
        if (_root["edges"] is not JsonArray array)
        {
            array = [];
            _root["edges"] = array;
        }

        var id = Text(edge, "id");

        if (id is null || Edges.Any(existing => existing.Id == id))
        {
            return;
        }

        array.Add(edge.DeepClone().AsObject());
    }

    /// <summary>Drops a connection by id. False when no edge carried it.</summary>
    public bool RemoveEdge(string id)
    {
        if (_root["edges"] is not JsonArray array)
        {
            return false;
        }

        foreach (var item in array.ToList())
        {
            if (item is JsonObject obj && Text(obj, "id") == id)
            {
                array.Remove(obj);

                return true;
            }
        }

        return false;
    }

    /// <summary>Records which face one end attaches to — the field the spec has for it. False when it
    /// already said that, so a drag that changed nothing does not become a step in the undo stack.</summary>
    /// <summary>
    /// 把一根线的某一头挪到<b>另一格</b>上（拖端点拖到别的便签＝换归属），同时写下它进出的那一面。
    /// 目标不是便签、或本来就是那一格，都返回 false——不改文档就不进撤销栈。
    /// </summary>
    public bool MoveEdgeEnd(string edgeId, bool fromEnd, string nodeId, string side)
    {
        var edge = Edges.FirstOrDefault(item => item.Id == edgeId);

        if (edge is null || Find(nodeId) is not { } target || target.Kind == CanvasNodeKind.Group)
        {
            return false;
        }

        var current = fromEnd ? edge.FromNode : edge.ToNode;

        if (current == nodeId)
        {
            return false;
        }

        edge.Raw[fromEnd ? "fromNode" : "toNode"] = nodeId;
        edge.Raw[fromEnd ? "fromSide" : "toSide"] = side;

        return true;
    }

    public bool SetEdgeSide(string edgeId, bool fromEnd, string side)
    {
        var edge = Edges.FirstOrDefault(item => item.Id == edgeId);

        if (edge is null)
        {
            return false;
        }

        var field = fromEnd ? "fromSide" : "toSide";

        if (string.Equals(Text(edge.Raw, field), side, StringComparison.Ordinal))
        {
            return false;
        }

        edge.Raw[field] = side;

        return true;
    }

    /// <summary>An edge between these two cards in this direction, or null.</summary>
    public CanvasEdge? FindEdge(string fromId, string toId)
        => Edges.FirstOrDefault(edge => edge.FromNode == fromId && edge.ToNode == toId);

    public static CanvasDocument CreateNew()
    {
        var root = new JsonObject
        {
            ["nodes"] = new JsonArray(),
            ["edges"] = new JsonArray(),
        };

        return new CanvasDocument(root, (JsonArray)root["nodes"]!);
    }

    /// <summary>
    /// Reads a document, refusing only shapes that cannot be edited: a non-object
    /// root, a <c>nodes</c> value that is not an array, a node without an id, and
    /// two nodes sharing one id. Lesser problems are reported by
    /// <see cref="CanvasValidator"/> instead, so a board an agent wrote with a
    /// bad colour still opens rather than becoming unreadable.
    /// </summary>
    public static CanvasDocument Parse(string json, string? origin = null)
    {
        JsonNode? parsed;

        try
        {
            parsed = JsonNode.Parse(json);
        }
        catch (JsonException ex)
        {
            throw new CanvasDocumentException(
                [$"{Describe(origin)}is not valid JSON: {ex.Message}"]);
        }

        if (parsed is not JsonObject root)
        {
            throw new CanvasDocumentException(
                [$"{Describe(origin)}root must be a JSON object."]);
        }

        JsonArray nodes;

        if (root.ContainsKey("nodes"))
        {
            if (root["nodes"] is not JsonArray array)
            {
                throw new CanvasDocumentException(
                    [$"{Describe(origin)}\"nodes\" must be an array."]);
            }

            nodes = array;
        }
        else
        {
            nodes = new JsonArray();
            root["nodes"] = nodes;
        }

        var issues = new List<string>();
        var seen = new Dictionary<string, int>(StringComparer.Ordinal);

        for (var index = 0; index < nodes.Count; index++)
        {
            if (nodes[index] is not JsonObject node)
            {
                issues.Add($"{Describe(origin)}node #{index} is not an object.");
                continue;
            }

            var id = node["id"] is { } idNode && idNode.GetValueKind() == JsonValueKind.String
                ? idNode.GetValue<string>()
                : null;

            if (string.IsNullOrEmpty(id))
            {
                issues.Add($"{Describe(origin)}node #{index} has no \"id\".");
                continue;
            }

            if (seen.TryGetValue(id, out var first))
            {
                issues.Add(
                    $"{Describe(origin)}id \"{id}\" is used by node #{first} and node #{index}. " +
                    "Give one of them a new id.");
            }
            else
            {
                seen[id] = index;
            }
        }

        if (issues.Count > 0)
        {
            throw new CanvasDocumentException(issues);
        }

        return new CanvasDocument(root, nodes);
    }

    /// <summary>
    /// 逐格扫一遍 JSON，**只对通过筛选的那几枚**造 <see cref="CanvasNode"/>。
    /// </summary>
    /// <remarks>
    /// 给"指针每一帧都要扫全板"的那几条路（拖动时的对齐扫描就是）。直接 <c>foreach (_document.Nodes)</c>
    /// 也能写，但那个属性每读一次就为**全部**节点各 new 一枚包装——600 格的板上一帧四次，
    /// 就是"便签一多，拖动变卡"里真正在付费的那一笔（10-27 实测：每帧 0.844ms@60 格 → 2.394ms@600 格）。
    /// </remarks>
    public IEnumerable<CanvasNode> NodesWhere(Func<string, bool> keepId)
    {
        foreach (var item in _nodes)
        {
            if (item is not JsonObject obj)
            {
                continue;
            }

            var id = ReadId(obj);

            if (keepId(id))
            {
                yield return new CanvasNode(obj);
            }
        }
    }

    public CanvasNode? Find(string id)
    {
        // 直接扫 JSON，别走 `Nodes`：那个属性每读一次就 new 一整张表（N 枚 CanvasNode），
        // 而 Find 在指针每一帧要被调好几枚——10-27 量到"拖一格每帧 2.4ms（600 格）"，
        // 其中三枚物化就是这里来的。这里只造命中那一枚。
        for (var index = 0; index < _nodes.Count; index++)
        {
            if (_nodes[index] is JsonObject node && ReadId(node) == id)
            {
                return new CanvasNode(node);
            }
        }

        return null;
    }

    public CanvasNode Add(
        CanvasNodeKind kind,
        double x,
        double y,
        double width,
        double height,
        string? color = null,
        string? id = null,
        char namespacePrefix = NodeIds.AppNamespace)
    {
        if (id is not null && Find(id) is not null)
        {
            // An explicitly requested id that already exists is refused rather than
            // quietly renamed: the caller asked for that name, and a silent second
            // name is how an agent's reference stops pointing anywhere.
            throw new CanvasDocumentException(
                [$"cannot add a node with id \"{id}\" — this board already has one. Mint a new id, or patch the existing node."]);
        }

        var node = new CanvasNode(new JsonObject());

        node.Id = id ?? FreshId(namespacePrefix);
        node.Kind = kind;
        node.X = x;
        node.Y = y;
        node.Width = width;
        node.Height = height;

        if (color is not null)
        {
            node.Color = color;
        }

        // New things land on top, which in this format is the end of the array.
        _nodes.Add(node.Json);

        return node;
    }

    /// <summary>
    /// 把一枚节点原样放上板——贴过来的那一格带着它自己的字段（颜色、字号、<c>zIndex</c>、
    /// 我方没建模的别人的字段），不能只搬这程序认得的那六个。
    /// <para>
    /// 与 <see cref="Add"/> 同一落点（数组末尾＝盖在最上面），但**id 撞了就另铸一个**并回过来：
    /// 复制粘贴的语义是"再来一份"，同一块板上贴两次不能互相指认。调用方拿返回的 id 去改
    /// 组归属与线头。
    /// </para>
    /// </summary>
    public CanvasNode AppendNode(JsonObject node, char namespacePrefix = NodeIds.AppNamespace)
    {
        var copy = node.DeepClone().AsObject();
        var id = Text(copy, "id");

        if (id is null || Find(id) is not null)
        {
            id = FreshId(namespacePrefix);
            copy["id"] = id;
        }

        if (Text(copy, "type") is null)
        {
            copy["type"] = "text";
        }

        _nodes.Add(copy);

        return new CanvasNode(copy);
    }

    public bool Remove(string id)
    {
        for (var index = 0; index < _nodes.Count; index++)
        {
            if (_nodes[index] is JsonObject node && ReadId(node) == id)
            {
                _nodes.RemoveAt(index);
                return true;
            }
        }

        return false;
    }

    public void BringToFront(string id) => Move(id, to: _nodes.Count);

    public void SendToBack(string id) => Move(id, to: 0);

    public string ToJson()
    {
        var stream = new MemoryStream();

        using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions
        {
            Indented = true,
            IndentSize = 2,
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        }))
        {
            _root.WriteTo(writer);
        }

        // Written explicitly rather than trusted to the writer: CRLF in a board file
        // means every line of every node shows as changed in a diff. The writer also
        // leaves the closing brace unterminated, and a file without a final newline
        // makes every future append look like an edit of the last line.
        var text = JsonText.NormalizeNewLines(Encoding.UTF8.GetString(stream.ToArray()));

        return text.EndsWith('\n') ? text : text + "\n";
    }

    /// <summary>
    /// 把任意一份 JSON 写成**这块板自己那副样子**：缩进两格、LF、结尾一个换行、中文不转义。
    /// <see cref="ToJson"/> 走的就是这一份，剪贴板上的片段也走这一份——两处两套写法，
    /// 迟早会有一处把 CRLF 或 <c>\uXXXX</c> 写进本该给人读的地方。
    /// </summary>
    public static string Emit(JsonObject root)
    {
        var stream = new MemoryStream();

        using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions
        {
            Indented = true,
            IndentSize = 2,
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        }))
        {
            root.WriteTo(writer);
        }

        var text = JsonText.NormalizeNewLines(Encoding.UTF8.GetString(stream.ToArray()));

        return text.EndsWith('\n') ? text : text + "\n";
    }

    internal JsonArray NodeArray => _nodes;

    /// <summary>
    /// Mints an id, checking it against this board rather than trusting the random
    /// generator to have stayed out of the way.
    /// </summary>
    private string FreshId(char namespacePrefix)
    {
        for (var attempt = 0; attempt < 8; attempt++)
        {
            var candidate = NodeIds.New(namespacePrefix);

            if (Find(candidate) is null)
            {
                return candidate;
            }
        }

        throw new CanvasDocumentException(
            ["could not mint a free node id after 8 attempts, which means the board is full of ids that were not minted here."]);
    }

    private void Move(string id, int to)
    {
        for (var index = 0; index < _nodes.Count; index++)
        {
            if (_nodes[index] is JsonObject node && ReadId(node) == id)
            {
                if (index == to || (to == _nodes.Count && index == _nodes.Count - 1))
                {
                    return;
                }

                var moved = _nodes[index];
                _nodes.RemoveAt(index);

                if (to > _nodes.Count)
                {
                    to = _nodes.Count;
                }

                _nodes.Insert(to, moved);
                return;
            }
        }
    }

    internal static string? ReadId(JsonObject node)
    {
        var idNode = node["id"];
        return idNode is not null && idNode.GetValueKind() == JsonValueKind.String
            ? idNode.GetValue<string>()
            : null;
    }

    private static string Describe(string? origin)
        => string.IsNullOrEmpty(origin) ? string.Empty : $"{origin}: ";
}

public sealed class CanvasDocumentException : Exception
{
    public CanvasDocumentException(IReadOnlyList<string> issues)
        : base(string.Join(Environment.NewLine, issues))
    {
        Issues = issues;
    }

    public IReadOnlyList<string> Issues { get; }
}
