using System.Text.Json.Nodes;

namespace Outspread.Core.Tests;

public class PatchAndStoreTests
{
    private static CanvasDocument TwoNodes(out string first, out string second)
    {
        var document = CanvasDocument.CreateNew();

        var a = document.Add(CanvasNodeKind.Text, 0, 0, 200, 60);
        var b = document.Add(CanvasNodeKind.Text, 300, 0, 200, 60);

        a.Text = "first";
        b.Text = "second";

        first = a.Id;
        second = b.Id;

        return document;
    }

    [Fact]
    public void A_drag_produces_only_the_field_that_moved()
    {
        var before = TwoNodes(out var first, out _);
        var after = CanvasDocument.Parse(before.ToJson());

        after.Find(first)!.X = 1234;

        var patch = DocDiff.Between(before, after);

        Assert.Empty(patch.Additions);
        Assert.Empty(patch.Removals);
        Assert.Null(patch.Order);
        Assert.Single(patch.Changes);
        Assert.Equal("x", patch.Changes[0].Field);
        Assert.Equal(first, patch.Changes[0].Id);
    }

    [Fact]
    public void Appending_one_node_does_not_rewrite_the_order_of_the_rest()
    {
        var before = TwoNodes(out _, out _);
        var after = CanvasDocument.Parse(before.ToJson());

        after.Add(CanvasNodeKind.Text, 600, 0, 200, 60);

        var patch = DocDiff.Between(before, after);

        // Without this, adding a sticky note would emit a z-order statement naming
        // every node, and the diff would show the whole board as changed.
        Assert.Null(patch.Order);
        Assert.Single(patch.Additions);
    }

    [Fact]
    public void A_save_merges_with_a_node_an_agent_added_in_the_meantime()
    {
        using var scratch = new Scratch("merge");

        var human = TwoNodes(out var first, out _);
        CanvasStore.Write(scratch.BoardPath, human.ToJson());

        // What the board keeps from its last sync. Diffing against the file as it is
        // right now would read the agent's new node as something to delete.
        var baseSnapshot = CanvasDocument.Parse(CanvasStore.ReadAllText(scratch.BoardPath));

        var agent = CanvasDocument.Parse(CanvasStore.ReadAllText(scratch.BoardPath));
        var added = agent.Add(CanvasNodeKind.Text, 900, 900, 200, 60, id: "a-agent01");
        added.Text = "written by an agent";
        CanvasStore.Write(scratch.BoardPath, agent.ToJson());

        human.Find(first)!.X = 77;

        var patch = DocDiff.Between(baseSnapshot, human);
        Assert.DoesNotContain(patch.Removals, id => id == "a-agent01");

        var (reports, writtenHash) = CanvasStore.SavePatch(scratch.BoardPath, patch);

        var onDisk = CanvasDocument.Parse(CanvasStore.ReadAllText(scratch.BoardPath));

        Assert.Equal(77, onDisk.Find(first)!.X);
        Assert.NotNull(onDisk.Find("a-agent01"));
        Assert.Equal(3, onDisk.Nodes.Count);
        Assert.DoesNotContain(reports, report => report.Outcome == PatchOutcome.MissingNode);

        // The hash a save returns is what lets the watcher recognise its own write.
        // It is the digest itself (32 bytes), not its hex form.
        Assert.Equal(32, writtenHash.Length);
        Assert.Equal(
            CanvasStore.HashHex(CanvasStore.Sha256Of(File.ReadAllBytes(scratch.BoardPath))),
            CanvasStore.HashHex(writtenHash));
    }

    [Fact]
    public void A_reorder_is_visible_to_the_document_it_was_applied_to()
    {
        var document = TwoNodes(out var first, out var second);

        PatchApplier.Apply(document, new DocPatch { Order = [second, first] });

        // Regression: the order used to be written by replacing root["nodes"] with a new
        // array, while the document kept reading the array it loaded. The patch reported
        // Reordered and the board looked exactly as it had before.
        Assert.Equal($"{second},{first}", string.Join(',', document.Nodes.Select(node => node.Id)));
        Assert.Equal(
            $"{second},{first}",
            string.Join(',', CanvasDocument.Parse(document.ToJson()).Nodes.Select(node => node.Id)));
    }

    [Fact]
    public void Bringing_the_top_node_forward_moves_nothing()
    {
        var document = TwoNodes(out var first, out var second);

        document.BringToFront(second);

        Assert.Equal($"{first},{second}", string.Join(',', document.Nodes.Select(node => node.Id)));
    }

    [Fact]
    public void An_id_already_on_the_board_is_refused_and_names_the_id()
    {
        var document = TwoNodes(out var first, out _);

        var exception = Assert.Throws<CanvasDocumentException>(() =>
            document.Add(CanvasNodeKind.Text, 0, 0, 10, 10, id: first));

        Assert.Contains(first, exception.Message, StringComparison.Ordinal);
        Assert.Contains("patch the existing node", exception.Message, StringComparison.Ordinal);
        Assert.Equal(2, document.Nodes.Count);
    }

    [Fact]
    public void A_whole_document_write_is_the_way_an_agents_work_gets_destroyed()
    {
        using var scratch = new Scratch("clobber");

        var human = TwoNodes(out _, out _);
        CanvasStore.Write(scratch.BoardPath, human.ToJson());

        var agent = CanvasDocument.Parse(CanvasStore.ReadAllText(scratch.BoardPath));
        agent.Add(CanvasNodeKind.Text, 900, 900, 200, 60, id: "a-agent01");
        CanvasStore.Write(scratch.BoardPath, agent.ToJson());

        CanvasStore.Write(scratch.BoardPath, human.ToJson());

        // Records the hazard instead of pretending it is handled: nothing in the board
        // may save by dumping a document it loaded, because this is what it costs.
        Assert.Null(CanvasDocument.Parse(CanvasStore.ReadAllText(scratch.BoardPath)).Find("a-agent01"));
    }

    [Fact]
    public void Changing_a_node_that_is_not_there_is_reported_with_a_reason()
    {
        var patch = new DocPatch();
        patch.SetNumber("u-gone", "x", 5);

        var reports = PatchApplier.Apply(CanvasDocument.CreateNew(), patch);

        var report = Assert.Single(reports);
        Assert.Equal(PatchOutcome.MissingNode, report.Outcome);
        Assert.Contains("not on the board", report.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void Removing_a_node_the_other_writer_already_deleted_is_visible_not_silent()
    {
        var patch = new DocPatch();
        patch.Removals.Add("a-already-gone");

        var report = Assert.Single(PatchApplier.Apply(CanvasDocument.CreateNew(), patch));

        Assert.Equal(PatchOutcome.AlreadyAbsent, report.Outcome);
    }

    [Fact]
    public void The_inverse_patch_undoes_a_reorder_and_a_move()
    {
        var before = TwoNodes(out var first, out var second);
        var snapshot = CanvasDocument.Parse(before.ToJson());

        var after = CanvasDocument.Parse(before.ToJson());
        after.Find(first)!.X = 500;
        after.BringToFront(first);

        var forward = DocDiff.Between(snapshot, after);
        var backward = DocDiff.Inverse(snapshot, after);

        Assert.NotNull(forward.Order);

        var replayed = CanvasDocument.Parse(snapshot.ToJson());
        PatchApplier.Apply(replayed, forward);

        Assert.Equal(500, replayed.Find(first)!.X);
        Assert.Equal(after.Nodes.Select(node => node.Id), replayed.Nodes.Select(node => node.Id));

        PatchApplier.Apply(replayed, backward);

        Assert.Equal(0, replayed.Find(first)!.X);
        Assert.Equal(snapshot.Nodes.Select(node => node.Id), replayed.Nodes.Select(node => node.Id));
    }

    [Fact]
    public void An_order_statement_keeps_nodes_it_never_heard_of()
    {
        var document = TwoNodes(out var first, out var second);
        document.Add(CanvasNodeKind.Text, 0, 0, 10, 10, id: "a-late");

        PatchApplier.Apply(document, new DocPatch { Order = [second, first] });

        Assert.Equal($"{second},{first},a-late", string.Join(',', document.Nodes.Select(node => node.Id)));
        Assert.Equal(3, document.Nodes.Count);
    }

    [Fact]
    public void Simultaneous_writers_leave_a_parseable_board_and_no_leftover_temp_files()
    {
        using var scratch = new Scratch("concurrent");

        CanvasStore.Write(scratch.BoardPath, CanvasDocument.CreateNew().ToJson());

        var writers = Enumerable.Range(0, 4)
            .Select(index => new Thread(() =>
            {
                for (var round = 0; round < 10; round++)
                {
                    var id = $"w{index}-{round:00}";

                    var patch = new DocPatch();
                    patch.Additions.Add(new AddedNode { Id = id, Json = NodeOf(id, (index * 10) + round) });

                    CanvasStore.SavePatch(scratch.BoardPath, patch);
                }
            }))
            .ToList();

        foreach (var thread in writers)
        {
            thread.Start();
        }

        foreach (var thread in writers)
        {
            thread.Join();
        }

        var document = CanvasDocument.Parse(CanvasStore.ReadAllText(scratch.BoardPath));

        Assert.Equal(40, document.Nodes.Count);
        Assert.Equal(40, document.Nodes.Select(node => node.Id).Distinct().Count());
        Assert.Empty(Directory.GetFiles(scratch.Folder, "*.tmp"));
    }

    [Fact]
    public void A_contended_lock_raises_an_error_instead_of_dropping_the_save()
    {
        using var scratch = new Scratch("lock");

        CanvasStore.Write(scratch.BoardPath, CanvasDocument.CreateNew().ToJson());

        var released = new ManualResetEventSlim(false);

        // A mutex is recursive for the thread that owns it, so the holder has to be a
        // different thread — that is also what makes it look like the CLI or the board.
        var holder = new Thread(() =>
        {
            using var mutex = new Mutex(initiallyOwned: false, CanvasStore.MutexName);

            if (!mutex.WaitOne(TimeSpan.FromSeconds(2)))
            {
                released.Set();
                return;
            }

            released.Set();
            Thread.Sleep(6_000);
            mutex.ReleaseMutex();
        })
        { IsBackground = true };

        holder.Start();
        Assert.True(released.Wait(TimeSpan.FromSeconds(2)), "could not take the board lock; close the running board first");

        var exception = Assert.Throws<IOException>(() =>
        {
            var patch = new DocPatch();
            patch.Additions.Add(new AddedNode { Id = "u-blocked", Json = NodeOf("u-blocked", 0) });
            CanvasStore.SavePatch(scratch.BoardPath, patch);
        });

        Assert.Contains("did not release", exception.Message, StringComparison.Ordinal);

        // Nothing half-written is left behind while the save is refused.
        Assert.Empty(CanvasDocument.Parse(CanvasStore.ReadAllText(scratch.BoardPath)).Nodes);
        Assert.Empty(Directory.GetFiles(scratch.Folder, "*.tmp"));

        holder.Join(TimeSpan.FromSeconds(9));
    }

    private static JsonObject NodeOf(string id, int x) => new()
    {
        ["id"] = id,
        ["type"] = "text",
        ["x"] = x,
        ["y"] = 0,
        ["width"] = 120,
        ["height"] = 60,
        ["text"] = id,
    };
}
