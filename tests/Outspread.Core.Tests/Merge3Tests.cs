using System.Text.Json.Nodes;

namespace Outspread.Core.Tests;

/// <summary>
/// The merge rules are a contract two implementations have to reach the same board from,
/// so each rule gets its own case rather than one test that a phone could read as "roughly
/// the same".
/// </summary>
public class Merge3Tests
{
    private static CanvasDocument Board(params (string Id, string Text)[] notes)
    {
        var document = CanvasDocument.CreateNew();

        foreach (var (id, text) in notes)
        {
            var node = document.Add(CanvasNodeKind.Text, 0, 0, 200, 60, id: id);
            node.Text = text;
        }

        return document;
    }

    private static CanvasDocument Copy(CanvasDocument document) => CanvasDocument.Parse(document.ToJson());

    private static MergeSide Side(string device, int clock) => new(device, clock);

    [Fact]
    public void Two_ends_adding_different_notes_keep_both()
    {
        var common = Board(("uone", "shared start"));

        var local = Copy(common);
        local.Add(CanvasNodeKind.Text, 400, 0, 200, 60, id: "uphone");

        var remote = Copy(common);
        remote.Add(CanvasNodeKind.Text, 800, 0, 200, 60, id: "uboard");

        var merged = Merge3.Between(common, local, remote, Side("phone", 4), Side("board", 5));

        Assert.Equal(3, merged.Merged.Nodes.Count);
        Assert.NotNull(merged.Merged.Find("uphone"));
        Assert.NotNull(merged.Merged.Find("uboard"));
        Assert.Empty(merged.Verdicts);
    }

    [Fact]
    public void One_field_changed_by_only_one_end_arrives_without_a_verdict()
    {
        var common = Board(("uone", "shared start"));

        var local = Copy(common);
        local.Find("uone")!.Text = "typed on the phone";

        var merged = Merge3.Between(common, local, Copy(common), Side("phone", 4), Side("board", 5));

        Assert.Equal("typed on the phone", merged.Merged.Find("uone")!.Text);
        Assert.Empty(merged.Verdicts);
    }

    [Fact]
    public void Two_ends_writing_different_words_to_one_note_keep_both_sets_of_words()
    {
        var common = Board(("uone", "shared start"));

        var local = Copy(common);
        local.Find("uone")!.Text = "typed on the phone";

        var remote = Copy(common);
        remote.Find("uone")!.Text = "typed on the board";

        var merged = Merge3.Between(common, local, remote, Side("phone", 4), Side("board", 5));

        var kept = merged.Merged.Find("uone");

        Assert.NotNull(kept);
        Assert.Equal("typed on the board", kept!.Text);

        // Nothing is thrown away: what this end wrote becomes a note of its own, placed
        // under the one it disagrees with, so it is found by looking rather than by luck.
        var copies = merged.Merged.Nodes
            .Where(node => node.Id != "uone")
            .ToList();

        Assert.Single(copies);
        Assert.Equal("typed on the phone", copies[0].Text);
        Assert.Equal(kept.Y + kept.Height + 40, copies[0].Y);

        Assert.True(merged.HasConflicts);
        Assert.Contains(merged.Verdicts, verdict => verdict.Rule == MergeRule.ContentConflictCopy);
    }

    [Fact]
    public void Two_ends_moving_one_note_settle_on_the_higher_clock()
    {
        var common = Board(("uone", "one"));

        var local = Copy(common);
        local.Find("uone")!.X = 100;

        var remote = Copy(common);
        remote.Find("uone")!.X = 900;

        var behind = Merge3.Between(common, local, remote, Side("phone", 4), Side("board", 9));
        var ahead = Merge3.Between(common, local, remote, Side("phone", 9), Side("board", 4));

        // A dragged note has no words to lose, and two copies of it would be worse than a
        // position either end can see is not what they left.
        Assert.Equal(900, behind.Merged.Find("uone")!.X);
        Assert.Equal(100, ahead.Merged.Find("uone")!.X);
        Assert.Contains(behind.Verdicts, verdict => verdict.Rule == MergeRule.PositionByClock);
    }

    [Fact]
    public void Equal_device_names_still_reach_the_same_answer()
    {
        var common = Board(("uone", "one"));

        var local = Copy(common);
        local.Find("uone")!.X = 100;

        var remote = Copy(common);
        remote.Find("uone")!.X = 900;

        var tie = Merge3.Between(common, local, remote, Side("zz-phone", 7), Side("aa-board", 7));
        var mirrored = Merge3.Between(common, remote, local, Side("aa-board", 7), Side("zz-phone", 7));

        // Both ends see the same two sides and the same rule, so a tie must not be able to
        // resolve differently depending on which machine is asking.
        Assert.Equal(100, tie.Merged.Find("uone")!.X);
        Assert.Equal(100, mirrored.Merged.Find("uone")!.X);
    }

    [Fact]
    public void An_edit_beats_a_deletion_the_other_end_never_saw()
    {
        var common = Board(("uone", "one"), ("utwo", "two"));

        var local = Copy(common);
        local.Find("uone")!.Text = "rewritten here";

        var remote = Copy(common);
        remote.Remove("uone");

        var merged = Merge3.Between(common, local, remote, Side("phone", 4), Side("board", 5));

        var revived = merged.Merged.Find("uone");

        Assert.NotNull(revived);
        Assert.Equal("rewritten here", revived!.Text);
        Assert.Contains(merged.Verdicts, verdict => verdict.Rule == MergeRule.Revived);
    }

    [Fact]
    public void A_deletion_the_other_end_left_alone_deletes()
    {
        var common = Board(("uone", "one"), ("utwo", "two"));

        var local = Copy(common);
        local.Remove("utwo");

        var remote = Copy(common);
        remote.Find("uone")!.Text = "edited over there";

        var merged = Merge3.Between(common, local, remote, Side("phone", 4), Side("board", 5));

        Assert.Null(merged.Merged.Find("utwo"));
        Assert.Equal("edited over there", merged.Merged.Find("uone")!.Text);
        Assert.Empty(merged.Verdicts);
    }

    [Fact]
    public void Two_ends_minting_the_same_id_for_different_notes_rename_ours()
    {
        var common = Board(("uone", "one"));

        var local = Copy(common);
        local.Add(CanvasNodeKind.Text, 400, 0, 200, 60, id: "ushared").Text = "ours";

        var remote = Copy(common);
        remote.Add(CanvasNodeKind.Text, 800, 0, 200, 60, id: "ushared").Text = "theirs";

        var merged = Merge3.Between(common, local, remote, Side("phone", 4), Side("board", 5));

        var shared = merged.Merged.Find("ushared");

        Assert.NotNull(shared);
        Assert.Equal("theirs", shared!.Text);

        var renamed = merged.Merged.Nodes.Single(node => node.Id != "ushared" && node.Id != "uone");

        Assert.Equal("ours", renamed.Text);
        Assert.NotEqual("ushared", renamed.Id);
        Assert.Contains(merged.Verdicts, verdict => verdict.Rule == MergeRule.Renumbered);
    }

    [Fact]
    public void A_group_renamed_here_moves_our_children_and_leaves_theirs_pointing_at_the_note_that_kept_the_id()
    {
        var common = Board(("uone", "one"));

        // Both ends minted "ug" for a group of their own. This end's child sits inside this
        // end's group; the far child sits inside the far group, which is the one that kept
        // the id. Rewriting both would detach the far child from a group that still exists.
        var local = Copy(common);
        local.Add(CanvasNodeKind.Group, 0, 0, 600, 400, id: "ug").Label = "our group";
        var ourChild = local.Add(CanvasNodeKind.Text, 40, 40, 200, 60, id: "uchild");
        ourChild.Text = "ours";
        ourChild.Json["group"] = "ug";

        var remote = Copy(common);
        remote.Add(CanvasNodeKind.Group, 0, 0, 900, 700, id: "ug").Label = "their group";
        var theirChild = remote.Add(CanvasNodeKind.Text, 60, 60, 200, 60, id: "fchild");
        theirChild.Text = "theirs";
        theirChild.Json["group"] = "ug";

        var merged = Merge3.Between(common, local, remote, Side("phone", 4), Side("board", 5));

        Assert.Equal("their group", merged.Merged.Find("ug")!.Label);
        Assert.Equal("ug", merged.Merged.Find("fchild")!.Json["group"]!.GetValue<string>());

        // Our group is the node whose label is ours, under whatever id it ended up with; the
        // child we added has to follow it, and only it.
        var ourGroup = merged.Merged.Nodes.Single(node => node.Label == "our group");

        Assert.NotEqual("ug", ourGroup.Id);
        Assert.Equal(ourGroup.Id, merged.Merged.Find("uchild")!.Json["group"]!.GetValue<string>());
        Assert.NotNull(merged.Merged.Find(ourGroup.Id));
        Assert.Contains(merged.Verdicts, verdict => verdict.Rule == MergeRule.Renumbered);
    }

    [Fact]
    public void Two_ends_adding_the_same_note_by_accident_stay_one_note()
    {
        var common = Board(("uone", "one"));

        var local = Copy(common);
        local.Add(CanvasNodeKind.Text, 400, 0, 200, 60, id: "ushared").Text = "identical words";

        var remote = Copy(common);
        remote.Add(CanvasNodeKind.Text, 400, 0, 200, 60, id: "ushared").Text = "identical words";

        var merged = Merge3.Between(common, local, remote, Side("phone", 4), Side("board", 5));

        Assert.Equal(2, merged.Merged.Nodes.Count);
        Assert.Contains(merged.Verdicts, verdict => verdict.Rule == MergeRule.Deduplicated);
        Assert.False(merged.HasConflicts);
    }

    [Fact]
    public void Two_ends_restacking_the_board_keep_the_fars_order_and_say_so()
    {
        var common = Board(("uone", "one"), ("utwo", "two"), ("uthree", "three"));

        var local = Copy(common);
        local.BringToFront("uone");

        var remote = Copy(common);
        remote.BringToFront("utwo");

        var merged = Merge3.Between(common, local, remote, Side("phone", 4), Side("board", 5));

        var order = string.Join(',', merged.Merged.Nodes.Select(node => node.Id));

        Assert.Equal("uone,uthree,utwo", order);
        Assert.Contains(merged.Verdicts, verdict => verdict.Rule == MergeRule.OrderFromRemote);
    }

    [Fact]
    public void Lines_the_two_ends_disagree_about_are_reported_because_node_ids_cannot_merge_them()
    {
        var common = Board(("uone", "one"));
        AddEdge(common, "eone", "uone", "as drawn");

        var local = Copy(common);
        SetEdgeLabel(local, "eone", "relabeled here");

        var merged = Merge3.Between(common, local, Copy(common), Side("phone", 4), Side("board", 5));

        // The patch layer speaks about nodes and nothing else. Silently taking the far
        // edges would be a loss nobody was told about, so it is a verdict instead.
        Assert.Equal("as drawn", EdgeLabel(merged.Merged, "eone"));
        Assert.Contains(merged.Verdicts, verdict => verdict.Rule == MergeRule.CarriedFromRemote && verdict.Field == "edges");
        Assert.True(merged.HasConflicts);
    }

    [Fact]
    public void A_board_that_only_grew_on_one_side_merges_into_the_other_side_exactly()
    {
        var common = Board(("uone", "one"));

        var local = Copy(common);
        var added = local.Add(CanvasNodeKind.Text, 400, 0, 200, 60, id: "uphone");
        added.Text = "added here";
        added.Color = "3";

        var result = Merge3.Between(common, local, Copy(common), Side("phone", 4), Side("board", 5));

        var roundTrip = CanvasDocument.Parse(result.Merged.ToJson());

        Assert.Equal(
            local.Nodes.Select(node => $"{node.Id}:{node.Text}:{node.Color}"),
            roundTrip.Nodes.Select(node => $"{node.Id}:{node.Text}:{node.Color}"));
    }

    private static void AddEdge(CanvasDocument document, string id, string from, string label)
    {
        if (document.Root["edges"] is not JsonArray edges)
        {
            edges = new JsonArray();
            document.Root["edges"] = edges;
        }

        edges.Add(new JsonObject
        {
            ["id"] = id,
            ["fromNode"] = from,
            ["toNode"] = from,
            ["label"] = label,
        });
    }

    private static void SetEdgeLabel(CanvasDocument document, string id, string label)
    {
        foreach (var edge in document.Root["edges"]!.AsArray().OfType<JsonObject>())
        {
            if (edge["id"]?.GetValue<string>() == id)
            {
                edge["label"] = label;
            }
        }
    }

    private static string? EdgeLabel(CanvasDocument document, string id)
        => document.Root["edges"]?.AsArray()
            .OfType<JsonObject>()
            .FirstOrDefault(edge => edge["id"]?.GetValue<string>() == id)?["label"]?
            .GetValue<string>();
}
