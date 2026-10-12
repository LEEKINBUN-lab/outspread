using System.Text.Json.Nodes;

namespace Outspread.Core;

/// <summary>Which device made a change, and how many pushes it had made when it did.</summary>
public readonly record struct MergeSide(string DeviceId, int Clock);

public enum MergeRule
{
    /// <summary>Both ends edited the same node's same position field. The higher clock won.</summary>
    PositionByClock,

    /// <summary>Both ends wrote different content into the same field. The far value stayed put; this end's became a new note.</summary>
    ContentConflictCopy,

    /// <summary>One end deleted a node the other end had just edited, so the edit survived and the deletion did not run.</summary>
    Revived,

    /// <summary>Both ends minted the same id for different nodes. The far one kept the id; this one was renumbered, references included.</summary>
    Renumbered,

    /// <summary>Both ends added byte-identical nodes under one id, so only one copy is on the board.</summary>
    Deduplicated,

    /// <summary>Both ends restacked the board. The far z-order is the one that was kept.</summary>
    OrderFromRemote,

    /// <summary>The patch model covers nodes, so anything else that differs between the ends is reported here rather than merged quietly.</summary>
    CarriedFromRemote,
}

public sealed record MergeVerdict(
    string Id,
    string Field,
    MergeRule Rule,
    string Chosen,
    string Detail);

public sealed class Merge3Result
{
    public required CanvasDocument Merged { get; init; }

    /// <summary>Every decision that was not "only one end touched this". Empty means a clean merge.</summary>
    public List<MergeVerdict> Verdicts { get; } = [];

    public bool HasConflicts
        => Verdicts.Any(verdict => verdict.Rule is MergeRule.ContentConflictCopy
            or MergeRule.Renumbered
            or MergeRule.CarriedFromRemote);
}

/// <summary>
/// Reconciles two boards that were edited apart from one shared ancestor.
/// </summary>
/// <remarks>
/// The format itself cannot say which end is newer — JSON Canvas 1.0 carries no
/// revision, timestamp or clock field — so the ancestor has to be supplied from
/// outside, which is what <see cref="SyncLedger"/> keeps. Without it this degrades
/// into whichever document is handed over last winning every field.
///
/// The far side is the carrier, not the local one: the merge is expressed as the
/// smallest patch on top of what the server already holds, which keeps a push from
/// rewriting lines it never edited.
///
/// Rules are fixed and total, because a second implementation on a phone has to
/// reach the same board from the same three inputs. Anything that would otherwise
/// be a silent choice is written into <see cref="Merge3Result.Verdicts"/> instead.
/// </remarks>
public static class Merge3
{
    /// <summary>Fields whose only cost is a note sliding around, so one of them must simply win.</summary>
    private static readonly HashSet<string> PositionFields = new(StringComparer.Ordinal)
    {
        "x", "y", "width", "height", "zIndex",
    };

    /// <summary>Where a conflict copy lands: directly under the note it disagrees with.</summary>
    private const int ConflictDropOffset = 40;

    public static Merge3Result Between(
        CanvasDocument commonBase,
        CanvasDocument local,
        CanvasDocument remote,
        MergeSide localSide,
        MergeSide remoteSide)
    {
        var ours = Split(DocDiff.Between(commonBase, local));
        var theirs = Split(DocDiff.Between(commonBase, remote));

        var merged = CanvasDocument.Parse(remote.ToJson());
        var result = new Merge3Result { Merged = merged };

        DeleteOnOurSideOnly(merged, ours, theirs, result);
        AddOurs(merged, local, ours, theirs, result);
        ApplyOurFields(merged, local, ours, theirs, localSide, remoteSide, result);
        Reorder(merged, ours, theirs, result);
        CarryRest(merged, local, remote, result);

        return result;
    }

    /// <summary>
    /// A deletion that the other end never noticed runs; a deletion that raced with an
    /// edit on the other end does not. Losing an edit nobody has seen the value of is
    /// the one failure mode a board cannot recover from.
    /// </summary>
    private static void DeleteOnOurSideOnly(
        CanvasDocument merged,
        Side ours,
        Side theirs,
        Merge3Result result)
    {
        foreach (var id in ours.Removed)
        {
            var editedTheirs = theirs.Changed.TryGetValue(id, out var fields) && fields.Count > 0;
            var addedTheirs = theirs.Added.ContainsKey(id);

            if (editedTheirs || addedTheirs)
            {
                result.Verdicts.Add(new MergeVerdict(
                    id,
                    "(deleted here)",
                    MergeRule.Revived,
                    "kept the far edit",
                    "this end deleted the node, the other end changed it; the change survived and the deletion was not applied"));
                continue;
            }

            merged.Remove(id);
        }
    }

    /// <summary>
    /// Nodes only this end has. Two ends minting the same id for different content is
    /// resolved by renaming ours, never theirs: their id may already be referenced by
    /// an agent's work we cannot see.
    /// </summary>
    /// <remarks>
    /// References are fixed only inside the nodes this merge itself brought in. A
    /// reference from a far-side node at the old id is correct and stays — the far node
    /// is the one that kept that id, and rewriting those pointers is how a line would
    /// end up attached to a copy of a note instead of the note.
    /// </remarks>
    private static void AddOurs(
        CanvasDocument merged,
        CanvasDocument local,
        Side ours,
        Side theirs,
        Merge3Result result)
    {
        var renamed = new Dictionary<string, string>(StringComparer.Ordinal);
        var broughtIn = new List<string>();

        foreach (var pair in ours.Added)
        {
            var mine = pair.Value;

            if (merged.Find(pair.Key) is null)
            {
                merged.NodeArray.Add(mine.DeepClone());
                broughtIn.Add(pair.Key);
                continue;
            }

            var existing = merged.Find(pair.Key)!.Json;

            if (JsonNode.DeepEquals(existing, mine))
            {
                result.Verdicts.Add(new MergeVerdict(
                    pair.Key,
                    "(added here)",
                    MergeRule.Deduplicated,
                    "one copy",
                    "both ends added a node that reads identically, so it stayed a single node"));
                continue;
            }

            var fresh = FreshId(merged);
            var copy = mine.DeepClone().AsObject();
            copy["id"] = fresh;
            merged.NodeArray.Add(copy);

            broughtIn.Add(fresh);
            renamed[pair.Key] = fresh;

            result.Verdicts.Add(new MergeVerdict(
                fresh,
                "(added here)",
                MergeRule.Renumbered,
                $"renumbered to {fresh}",
                $"both ends added a node under id \"{pair.Key}\" with different contents; the far one kept the id"));
        }

        if (renamed.Count == 0)
        {
            return;
        }

        foreach (var id in broughtIn)
        {
            var node = merged.Find(id);

            if (node?.Json["group"] is not JsonNode group
                || group.GetValueKind() != System.Text.Json.JsonValueKind.String)
            {
                continue;
            }

            if (renamed.TryGetValue(group.GetValue<string>(), out var replacement))
            {
                node.Json["group"] = replacement;
            }
        }
    }

    private static void ApplyOurFields(
        CanvasDocument merged,
        CanvasDocument local,
        Side ours,
        Side theirs,
        MergeSide localSide,
        MergeSide remoteSide,
        Merge3Result result)
    {
        foreach (var pair in ours.Changed)
        {
            var node = merged.Find(pair.Key);

            if (node is null)
            {
                // The other end deleted it. An edit beats a deletion, so the node comes
                // back carrying this end's values.
                var revived = local.Find(pair.Key);

                if (revived is null)
                {
                    continue;
                }

                merged.NodeArray.Add(revived.Json.DeepClone());

                result.Verdicts.Add(new MergeVerdict(
                    pair.Key,
                    string.Join(',', pair.Value.Keys),
                    MergeRule.Revived,
                    "kept the edit here",
                    "the far end deleted this node while this end was editing it"));
                continue;
            }

            var copies = new Dictionary<string, CanvasNode>(StringComparer.Ordinal);

            foreach (var field in pair.Value)
            {
                var mine = field.Value;
                var theirsValue = theirs.Changed.TryGetValue(pair.Key, out var theirFields)
                    ? theirFields.GetValueOrDefault(field.Key, NodeOf(node, field.Key))
                    : null;

                var touchedTheirs = theirs.Changed.TryGetValue(pair.Key, out var touched)
                    && touched.ContainsKey(field.Key);

                if (!touchedTheirs)
                {
                    Set(node, field.Key, mine);
                    continue;
                }

                if (JsonNode.DeepEquals(mine, theirsValue))
                {
                    continue;
                }

                if (PositionFields.Contains(field.Key))
                {
                    var weWin = Wins(localSide, remoteSide);
                    Set(node, field.Key, weWin ? mine : theirsValue);

                    result.Verdicts.Add(new MergeVerdict(
                        pair.Key,
                        field.Key,
                        MergeRule.PositionByClock,
                        weWin ? localSide.DeviceId : remoteSide.DeviceId,
                        $"both ends moved it: r{localSide.Clock} against r{remoteSide.Clock}"));
                    continue;
                }

                // Leave the far value on the note and carry ours into a copy of it, so
                // neither end's words disappear. Two fields in disagreement on one note
                // still produce one copy, and every disputed field lands in that copy.
                if (!copies.TryGetValue(pair.Key, out var copy))
                {
                    copy = DropCopy(merged, node, pair.Key, result);
                    copies[pair.Key] = copy;
                }

                Set(copy, field.Key, mine);
            }
        }
    }

    private static CanvasNode DropCopy(
        CanvasDocument merged,
        CanvasNode node,
        string id,
        Merge3Result result)
    {
        var fresh = FreshId(merged);
        var copy = node.Json.DeepClone().AsObject();

        copy["id"] = fresh;
        copy["y"] = (long)(node.Y + node.Height + ConflictDropOffset);

        merged.NodeArray.Add(copy);

        result.Verdicts.Add(new MergeVerdict(
            id,
            "(content)",
            MergeRule.ContentConflictCopy,
            "far value kept, this end's copy placed below",
            $"a second note holding what this end wrote was added as {fresh}"));

        return merged.Find(fresh)!;
    }

    /// <summary>
    /// A restack from the other end is already what the carrier holds, so only ours can
    /// be applied here — and if both ends restacked, theirs is kept rather than trying to
    /// interleave two z-orders, which has no honest answer.
    /// </summary>
    private static void Reorder(CanvasDocument merged, Side ours, Side theirs, Merge3Result result)
    {
        if (ours.Order is null)
        {
            return;
        }

        if (theirs.Order is not null)
        {
            result.Verdicts.Add(new MergeVerdict(
                "(board)",
                "(z-order)",
                MergeRule.OrderFromRemote,
                "far order",
                "both ends restacked the board; this end's order was not applied"));
            return;
        }

        PatchApplier.Apply(merged, new DocPatch { Order = ours.Order });
    }

    /// <summary>
    /// The patch model speaks about nodes and nothing else, so the parts of a board it
    /// cannot see — edges, and any top-level key an agent adds — are reported here when
    /// the ends disagree instead of vanishing under a merge that looked clean.
    /// </summary>
    private static void CarryRest(
        CanvasDocument merged,
        CanvasDocument local,
        CanvasDocument remote,
        Merge3Result result)
    {
        var localRoot = local.Root;
        var remoteRoot = remote.Root;

        foreach (var key in Union(localRoot.Select(pair => pair.Key), remoteRoot.Select(pair => pair.Key)))
        {
            if (key == "nodes")
            {
                continue;
            }

            if (JsonNode.DeepEquals(localRoot[key], remoteRoot[key]))
            {
                continue;
            }

            result.Verdicts.Add(new MergeVerdict(
                "(board)",
                key,
                MergeRule.CarriedFromRemote,
                "far version",
                $"this end and the far end disagree on \"{key}\", which merging by node id cannot resolve"));
        }
    }

    private static IEnumerable<string> Union(IEnumerable<string> a, IEnumerable<string> b)
    {
        var seen = new List<string>();
        var set = new HashSet<string>(StringComparer.Ordinal);

        foreach (var item in a.Concat(b))
        {
            if (set.Add(item))
            {
                seen.Add(item);
            }
        }

        return seen;
    }

    private static string FreshId(CanvasDocument merged)
    {
        for (var attempt = 0; attempt < 8; attempt++)
        {
            var candidate = NodeIds.NewAppId();

            if (merged.Find(candidate) is null)
            {
                return candidate;
            }
        }

        throw new CanvasDocumentException(
            ["could not mint a free node id for a merge, which means the board is full of ids that were not minted here."]);
    }

    /// <summary>
    /// Clock first, device name to break a tie. Both ends hold the same three inputs and
    /// the same rule, so they land on the same board; a tie decided by anything local
    /// would not survive that.
    /// </summary>
    private static bool Wins(MergeSide ours, MergeSide theirs)
    {
        if (ours.Clock != theirs.Clock)
        {
            return ours.Clock > theirs.Clock;
        }

        return string.CompareOrdinal(ours.DeviceId, theirs.DeviceId) > 0;
    }

    private static void Set(CanvasNode node, string field, JsonNode? value)
    {
        if (value is null)
        {
            node.Json.Remove(field);
            return;
        }

        node.Json[field] = value.DeepClone();
    }

    private static JsonNode? NodeOf(CanvasNode node, string field) => node.Json[field];

    /// <summary>One end's patch, laid out by id so two of them can be compared field by field.</summary>
    private sealed class Side
    {
        public required HashSet<string> Removed { get; init; }

        public required Dictionary<string, JsonObject> Added { get; init; }

        /// <summary>id to field to value; a null value is that field being removed.</summary>
        public required Dictionary<string, Dictionary<string, JsonNode?>> Changed { get; init; }

        public required List<string>? Order { get; init; }
    }

    private static Side Split(DocPatch patch)
    {
        var changed = new Dictionary<string, Dictionary<string, JsonNode?>>(StringComparer.Ordinal);

        foreach (var change in patch.Changes)
        {
            if (!changed.TryGetValue(change.Id, out var fields))
            {
                fields = new Dictionary<string, JsonNode?>(StringComparer.Ordinal);
                changed[change.Id] = fields;
            }

            // Last write wins inside one patch, which is how it reads once applied.
            fields[change.Field] = change.Value;
        }

        return new Side
        {
            Removed = [.. patch.Removals],
            Added = patch.Additions.ToDictionary(pair => pair.Id, pair => pair.Json, StringComparer.Ordinal),
            Changed = changed,
            Order = patch.Order,
        };
    }
}
