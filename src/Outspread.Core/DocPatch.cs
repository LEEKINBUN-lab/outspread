using System.Text.Json;
using System.Text.Json.Nodes;

namespace Outspread.Core;

/// <summary>
/// The one way anything changes a board: field sets on a known id, whole node
/// insertions, deletions, and an optional full z-order.
/// </summary>
/// <remarks>
/// Autosave, undo, redo and the agent-facing CLI all go through this instead of
/// dumping a whole document. That is what lets a save merge with an edit an agent
/// made in the same second: the board re-reads the file and replays only the
/// things a human actually did, so it cannot silently overwrite a node it had
/// never seen.
/// </remarks>
public sealed class DocPatch
{
    public List<NodeFieldChange> Changes { get; } = [];

    public List<AddedNode> Additions { get; } = [];

    public List<string> Removals { get; } = [];

    /// <summary>Connections that appeared. The whole edge object travels, because a patch that only
    /// carried the two ends would drop the sides the writer chose.</summary>
    public List<JsonObject> EdgeAdditions { get; } = [];

    /// <summary>Edge ids that disappeared.</summary>
    public List<string> EdgeRemovals { get; } = [];

    /// <summary>Complete id sequence, applied last. Null when z-order is untouched.</summary>
    public List<string>? Order { get; set; }

    public bool IsEmpty
        => Changes.Count == 0 && Additions.Count == 0 && Removals.Count == 0 && Order is null
        && EdgeAdditions.Count == 0 && EdgeRemovals.Count == 0;

    public void Set(string id, string field, JsonNode? value)
        => Changes.Add(new NodeFieldChange(id, field, value?.DeepClone()));

    public void SetString(string id, string field, string? value)
        => Changes.Add(new NodeFieldChange(id, field, value is null ? null : JsonValue.Create(value)));

    public void SetNumber(string id, string field, double value)
        => Changes.Add(new NodeFieldChange(id, field, JsonValue.Create((long)Math.Round(value))));

    public int NodeCount => Changes.Count + Additions.Count + Removals.Count + EdgeAdditions.Count + EdgeRemovals.Count;
}

public sealed record NodeFieldChange(string Id, string Field, JsonNode? Value);

public sealed class AddedNode
{
    public required string Id { get; init; }

    public required JsonObject Json { get; init; }

    /// <summary>Insert position in the node array; null appends, which means on top.</summary>
    public int? Index { get; init; }
}

public enum PatchOutcome
{
    Updated,
    Created,
    Removed,
    MissingNode,
    AlreadyAbsent,
    AlreadyPresent,
    Reordered,
}

public readonly record struct PatchReport(string Id, PatchOutcome Outcome, string? Detail = null);

public static class PatchApplier
{
    public static IReadOnlyList<PatchReport> Apply(CanvasDocument target, DocPatch patch)
    {
        var reports = new List<PatchReport>();

        // 连线先落：它不依赖格子的增删结果，而格子被删掉时指向它的线由校验那边提醒，不在这里偷偷删。
        foreach (var id in patch.EdgeRemovals)
        {
            reports.Add(new PatchReport(
                id,
                target.RemoveEdge(id) ? PatchOutcome.Removed : PatchOutcome.AlreadyAbsent));
        }

        foreach (var edge in patch.EdgeAdditions)
        {
            target.AppendEdge(edge);
        }

        foreach (var id in patch.Removals)
        {
            reports.Add(new PatchReport(
                id,
                target.Remove(id) ? PatchOutcome.Removed : PatchOutcome.AlreadyAbsent));
        }

        foreach (var addition in patch.Additions)
        {
            if (target.Find(addition.Id) is not null)
            {
                reports.Add(new PatchReport(
                    addition.Id,
                    PatchOutcome.AlreadyPresent,
                    "a node with this id is already on the board; the incoming one was not added"));
                continue;
            }

            var array = target.NodeArray;
            var json = addition.Json.DeepClone().AsObject();

            if (addition.Index is int index && index >= 0 && index < array.Count)
            {
                array.Insert(index, json);
            }
            else
            {
                array.Add(json);
            }

            reports.Add(new PatchReport(addition.Id, PatchOutcome.Created));
        }

        foreach (var change in patch.Changes)
        {
            var node = target.Find(change.Id);

            if (node is null)
            {
                reports.Add(new PatchReport(
                    change.Id,
                    PatchOutcome.MissingNode,
                    $"tried to set \"{change.Field}\" on a node that is not on the board"));
                continue;
            }

            if (change.Value is null)
            {
                node.Json.Remove(change.Field);
            }
            else
            {
                node.Json[change.Field] = change.Value.DeepClone();
            }

            reports.Add(new PatchReport(change.Id, PatchOutcome.Updated, change.Field));
        }

        if (patch.Order is { Count: > 0 } order)
        {
            ApplyOrder(target, order, reports);
        }

        return reports;
    }

    private static void ApplyOrder(CanvasDocument target, List<string> order, List<PatchReport> reports)
    {
        var array = target.NodeArray;

        // The reorder happens inside the array the document already holds. Swapping a
        // new array in under root["nodes"] leaves the document reading the old one, and
        // the order then looks applied while nothing on the board moves.
        var named = new HashSet<string>(order, StringComparer.Ordinal);
        var byId = new Dictionary<string, JsonNode>(StringComparer.Ordinal);
        var leftovers = new List<JsonNode>();

        foreach (var item in array.ToArray())
        {
            if (item is null)
            {
                continue;
            }

            var id = item is JsonObject obj ? CanvasDocument.ReadId(obj) : null;

            if (id is not null && named.Contains(id) && !byId.ContainsKey(id))
            {
                byId[id] = item;
            }
            else
            {
                leftovers.Add(item);
            }
        }

        array.Clear();

        foreach (var id in order)
        {
            if (byId.Remove(id, out var item))
            {
                array.Add(item);
            }
        }

        // Nodes an agent added after this order was computed must not vanish because no
        // list mentioned them; they land on top.
        foreach (var item in leftovers)
        {
            array.Add(item);
        }

        reports.Add(new PatchReport("(order)", PatchOutcome.Reordered, $"{array.Count} nodes"));
    }
}

/// <summary>
/// Turns two states of a board into the patch that carries the first into the
/// second. This is how undo and redo persist: an undo is not a document dump, it
/// is the reverse patch of what happened.
/// </summary>
public static class DocDiff
{
    public static DocPatch Between(CanvasDocument before, CanvasDocument after)
    {
        var patch = new DocPatch();

        // 连线按 id 集合比：一边有一边没有就是增或删；字段变了（换了进出面）记成"先删后加"，
        // 落盘结果一样，撤销栈里也还是一笔。
        var beforeEdges = before.Edges.ToDictionary(edge => edge.Id, edge => edge);
        var afterEdges = after.Edges.ToDictionary(edge => edge.Id, edge => edge);

        foreach (var (id, edge) in afterEdges)
        {
            if (!beforeEdges.TryGetValue(id, out var was))
            {
                patch.EdgeAdditions.Add(edge.Raw.DeepClone().AsObject());
            }
            else if (!string.Equals(was.Raw.ToJsonString(), edge.Raw.ToJsonString(), StringComparison.Ordinal))
            {
                patch.EdgeRemovals.Add(id);
                patch.EdgeAdditions.Add(edge.Raw.DeepClone().AsObject());
            }
        }

        foreach (var id in beforeEdges.Keys)
        {
            if (!afterEdges.ContainsKey(id))
            {
                patch.EdgeRemovals.Add(id);
            }
        }

        var (beforeById, beforeOrder) = Snapshot(before);
        var (afterById, afterOrder) = Snapshot(after);

        foreach (var id in beforeOrder)
        {
            if (!afterById.ContainsKey(id))
            {
                patch.Removals.Add(id);
            }
        }

        foreach (var id in afterOrder)
        {
            if (!beforeById.ContainsKey(id))
            {
                patch.Additions.Add(new AddedNode
                {
                    Id = id,
                    Json = afterById[id].Json,
                    Index = afterOrder.IndexOf(id),
                });
            }
        }

        foreach (var id in beforeOrder)
        {
            if (!afterById.TryGetValue(id, out var current))
            {
                continue;
            }

            var previous = beforeById[id];

            foreach (var field in UnionOfFields(previous.Json, current.Json))
            {
                if (field == "id")
                {
                    continue;
                }

                if (!JsonNode.DeepEquals(previous.Json[field], current.Json[field]))
                {
                    patch.Set(id, field, current.Json[field]);
                }
            }
        }

        if (OrderChanged(beforeOrder, afterOrder, patch))
        {
            patch.Order = afterOrder;
        }

        return patch;
    }

    /// <summary>The reverse patch, which is what an undo step stores.</summary>
    public static DocPatch Inverse(CanvasDocument before, CanvasDocument after) => Between(after, before);

    private static bool OrderChanged(List<string> beforeOrder, List<string> afterOrder, DocPatch patch)
    {
        var removed = new HashSet<string>(patch.Removals, StringComparer.Ordinal);
        var added = new HashSet<string>(patch.Additions.Select(pair => pair.Id), StringComparer.Ordinal);

        // Deletions and appended insertions cannot disturb the relative order of the
        // nodes that were already there, so only that sequence is compared. Emitting an
        // order statement when nothing really moved would rewrite every line of the file.
        var survivorsBefore = beforeOrder.Where(id => !removed.Contains(id)).ToList();
        var survivorsAfter = afterOrder.Where(id => !added.Contains(id)).ToList();

        if (survivorsBefore.Count != survivorsAfter.Count)
        {
            return true;
        }

        for (var i = 0; i < survivorsBefore.Count; i++)
        {
            if (!string.Equals(survivorsBefore[i], survivorsAfter[i], StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    private static IEnumerable<string> UnionOfFields(JsonObject a, JsonObject b)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (var pair in a)
        {
            if (seen.Add(pair.Key))
            {
                yield return pair.Key;
            }
        }

        foreach (var pair in b)
        {
            if (seen.Add(pair.Key))
            {
                yield return pair.Key;
            }
        }
    }

    private static (Dictionary<string, CanvasNode> ById, List<string> Order) Snapshot(CanvasDocument document)
    {
        var byId = new Dictionary<string, CanvasNode>(StringComparer.Ordinal);
        var order = new List<string>(document.Nodes.Count);

        foreach (var node in document.Nodes)
        {
            // First occurrence wins; CanvasDocument.Parse has already refused a file
            // where an id repeats, so this is only a guard for a patched-up document.
            if (byId.TryAdd(node.Id, node))
            {
                order.Add(node.Id);
            }
        }

        return (byId, order);
    }
}
