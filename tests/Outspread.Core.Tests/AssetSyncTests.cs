namespace Outspread.Core.Tests;

/// <summary>
/// The asset layer is judged on what it refuses to do as much as on what it carries: an
/// orphaned picture must not be uploaded, an oversized one must not take the round down, and a
/// path from the document must never become a way to reach the rest of the disk.
/// </summary>
public class AssetSyncTests
{
    private static CanvasDocument WithImage(string id, string relative)
    {
        var document = CanvasDocument.CreateNew();

        var node = document.Add(CanvasNodeKind.File, 0, 0, 200, 100, id: id);
        node.FilePath = relative;

        return document;
    }

    private static void PutLocalImage(Scratch scratch, string name, byte[] bytes)
    {
        var path = Path.Combine(AssetStore.AssetsDirectory(scratch.BoardPath), name);

        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, bytes);
    }

    private static RemoteTree Tree(FakeRemote far)
        => far.ReadTreeAsync(default).GetAwaiter().GetResult()!;

    [Fact]
    public async Task A_referenced_picture_this_end_has_and_the_far_side_lacks_goes_up()
    {
        using var scratch = new Scratch("asset-push");

        var bytes = new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 };
        PutLocalImage(scratch, "img_aaa.png", bytes);

        var document = WithImage("ufile01", "assets/img_aaa.png");
        CanvasStore.Write(scratch.BoardPath, document.ToJson());

        var far = new FakeRemote();
        var paths = SyncPaths.ForBoard(scratch.BoardPath);

        far.Publish(paths.DocPath, document.ToJson());

        var report = await AssetSync.PushAsync(far, scratch.BoardPath, document, Tree(far), paths, default);

        Assert.Equal(new[] { "assets/img_aaa.png" }, report.Uploaded);
        Assert.Equal(bytes, far.Files["boards/assets/img_aaa.png"]);
    }

    [Fact]
    public async Task A_picture_nothing_points_at_stays_home()
    {
        using var scratch = new Scratch("asset-orphan");

        PutLocalImage(scratch, "img_used.png", new byte[] { 1, 2, 3 });
        PutLocalImage(scratch, "img_orphan.png", new byte[] { 9, 9, 9 });

        var document = WithImage("ufile01", "assets/img_used.png");
        CanvasStore.Write(scratch.BoardPath, document.ToJson());

        var far = new FakeRemote();
        var paths = SyncPaths.ForBoard(scratch.BoardPath);

        far.Publish(paths.DocPath, document.ToJson());

        var report = await AssetSync.PushAsync(far, scratch.BoardPath, document, Tree(far), paths, default);

        Assert.DoesNotContain("assets/img_orphan.png", report.Uploaded);
        Assert.False(far.Files.ContainsKey("boards/assets/img_orphan.png"));
    }

    [Fact]
    public async Task A_picture_too_big_for_this_endpoint_is_refused_by_name_and_the_rest_still_goes()
    {
        using var scratch = new Scratch("asset-large");

        PutLocalImage(scratch, "img_huge.png", new byte[AssetSync.MaxCarriedBytes + 1]);
        PutLocalImage(scratch, "img_small.png", new byte[] { 1, 2, 3 });

        var document = CanvasDocument.CreateNew();

        document.Add(CanvasNodeKind.File, 0, 0, 200, 100, id: "ubig").FilePath = "assets/img_huge.png";
        document.Add(CanvasNodeKind.File, 300, 0, 200, 100, id: "usmall").FilePath = "assets/img_small.png";

        CanvasStore.Write(scratch.BoardPath, document.ToJson());

        var far = new FakeRemote();
        var paths = SyncPaths.ForBoard(scratch.BoardPath);

        far.Publish(paths.DocPath, document.ToJson());

        var report = await AssetSync.PushAsync(far, scratch.BoardPath, document, Tree(far), paths, default);

        Assert.Equal(new[] { "assets/img_small.png" }, report.Uploaded);
        Assert.Single(report.Refused);
        Assert.Equal("assets/img_huge.png", report.Refused[0].Path);
    }

    [Fact]
    public async Task A_picture_the_board_points_at_and_nobody_has_is_named_rather_than_shrugged_at()
    {
        using var scratch = new Scratch("asset-missing");

        var document = WithImage("ufile01", "assets/img_gone.png");
        CanvasStore.Write(scratch.BoardPath, document.ToJson());

        var far = new FakeRemote();
        var paths = SyncPaths.ForBoard(scratch.BoardPath);

        far.Publish(paths.DocPath, document.ToJson());

        var push = await AssetSync.PushAsync(far, scratch.BoardPath, document, Tree(far), paths, default);
        var pull = await AssetSync.PullAsync(far, scratch.BoardPath, document, Tree(far), paths, default);

        Assert.Equal(new[] { "assets/img_gone.png" }, push.Absent);
        Assert.Equal(new[] { "assets/img_gone.png" }, pull.Absent);
    }

    [Fact]
    public async Task A_picture_the_far_side_has_and_this_end_lacks_comes_down_byte_for_byte()
    {
        using var scratch = new Scratch("asset-pull");

        var document = WithImage("ufile01", "assets/img_their.png");
        CanvasStore.Write(scratch.BoardPath, document.ToJson());

        var bytes = new byte[] { 42, 42, 7, 7, 7, 1 };
        var far = new FakeRemote();
        var paths = SyncPaths.ForBoard(scratch.BoardPath);

        far.Publish(paths.DocPath, document.ToJson());
        far.PublishRaw("boards/assets/img_their.png", bytes);

        var report = await AssetSync.PullAsync(far, scratch.BoardPath, document, Tree(far), paths, default);

        Assert.Equal(new[] { "assets/img_their.png" }, report.Downloaded);

        var landed = AssetStore.ResolveExisting(scratch.BoardPath, "assets/img_their.png");

        Assert.NotNull(landed);
        Assert.Equal(bytes, File.ReadAllBytes(landed!));
    }

    [Fact]
    public async Task A_picture_already_on_both_sides_costs_no_request()
    {
        using var scratch = new Scratch("asset-idle");

        var document = WithImage("ufile01", "assets/img_aaa.png");
        CanvasStore.Write(scratch.BoardPath, document.ToJson());

        PutLocalImage(scratch, "img_aaa.png", new byte[] { 1, 2, 3 });

        var far = new FakeRemote();
        var paths = SyncPaths.ForBoard(scratch.BoardPath);

        far.Publish(paths.DocPath, document.ToJson());
        far.PublishRaw("boards/assets/img_aaa.png", new byte[] { 1, 2, 3 });

        var report = await AssetSync.PushAsync(far, scratch.BoardPath, document, Tree(far), paths, default);
        await AssetSync.PullAsync(far, scratch.BoardPath, document, Tree(far), paths, default);

        Assert.False(report.DidAnything);
        Assert.Equal(0, far.WriteCalls);
        Assert.Equal(0, far.ReadCalls);
    }

    [Fact]
    public void A_document_pointing_outside_the_board_folder_is_not_a_way_to_reach_the_disk()
    {
        var document = CanvasDocument.CreateNew();

        var upward = document.Add(CanvasNodeKind.File, 0, 0, 100, 100, id: "uevil01");
        upward.FilePath = "../../Windows/system32/config/sam";

        var rooted = document.Add(CanvasNodeKind.File, 0, 200, 100, 100, id: "uevil02");
        rooted.FilePath = "C:/Windows/temp/x.png";

        // The sync layer inherits the wall the renderer already stands behind, rather than
        // keeping its own looser copy of it.
        Assert.Empty(AssetSync.ReferencedBy(document));
    }

    [Fact]
    public void Asset_paths_are_written_the_way_the_board_reads_them()
    {
        var paths = SyncPaths.ForBoard(Path.Combine("whatever", "boards", "scratch.canvas"));

        Assert.Equal("boards/", AssetSync.RepoPrefix(paths));
        Assert.Equal("boards/scratch.canvas", paths.DocPath);
    }

    [Fact]
    public void A_tombstone_names_what_the_board_stopped_pointing_at_since_its_own_ancestor()
    {
        var ancestor = CanvasDocument.CreateNew();
        ancestor.Add(CanvasNodeKind.File, 0, 0, 200, 100, id: "ufile01").FilePath = "assets/img_aaa.png";
        ancestor.Add(CanvasNodeKind.File, 300, 0, 200, 100, id: "ufile02").FilePath = "assets/img_bbb.png";

        var merged = WithImage("ufile02", "assets/img_bbb.png");

        Assert.Equal(new[] { "assets/img_aaa.png" }, AssetSync.Tombstones(ancestor, merged, null));
    }

    [Fact]
    public void A_picture_that_came_back_into_use_moots_its_own_tombstone()
    {
        // The name is a digest of the content, so re-adding the same picture lands on the very
        // path the ledger says is gone. Without this rule the tombstone would eat the picture
        // the board is currently showing.
        var merged = WithImage("ufile01", "assets/img_aaa.png");

        Assert.Empty(AssetSync.Tombstones(null, merged, new[] { "assets/img_aaa.png" }));
    }

    [Fact]
    public void The_ledger_keeps_the_newest_deletions_and_lets_the_oldest_fall_off()
    {
        var far = Enumerable.Range(0, AssetSync.MaxTombstones)
            .Select(index => $"assets/old{index:00}.png")
            .ToList();

        var ancestor = CanvasDocument.CreateNew();
        ancestor.Add(CanvasNodeKind.File, 0, 0, 200, 100, id: "ufile09").FilePath = "assets/fresh.png";

        var kept = AssetSync.Tombstones(ancestor, CanvasDocument.CreateNew(), far);

        Assert.Equal(AssetSync.MaxTombstones, kept.Count);
        Assert.DoesNotContain("assets/old00.png", kept);
        Assert.Equal("assets/fresh.png", kept[^1]);
    }

    [Fact]
    public async Task Reaping_takes_the_picture_off_the_far_side_and_off_this_machine()
    {
        using var scratch = new Scratch("asset-reap");

        PutLocalImage(scratch, "img_aaa.png", new byte[] { 1, 2, 3 });

        var document = CanvasDocument.CreateNew();
        CanvasStore.Write(scratch.BoardPath, document.ToJson());

        var far = new FakeRemote();
        var paths = SyncPaths.ForBoard(scratch.BoardPath);

        far.Publish(paths.DocPath, document.ToJson());
        far.PublishRaw("boards/assets/img_aaa.png", new byte[] { 1, 2, 3 });

        var report = await AssetSync.ReapAsync(
            far, scratch.BoardPath, document, Tree(far), paths, new[] { "assets/img_aaa.png" }, default);

        Assert.Equal(new[] { "assets/img_aaa.png" }, report.Deleted);
        Assert.False(far.Files.ContainsKey("boards/assets/img_aaa.png"));
        Assert.Null(AssetStore.ResolveExisting(scratch.BoardPath, "assets/img_aaa.png"));
    }

    [Fact]
    public async Task A_picture_another_board_in_the_repository_still_uses_is_not_reaped()
    {
        using var scratch = new Scratch("asset-reap-shared");

        PutLocalImage(scratch, "img_aaa.png", new byte[] { 1, 2, 3 });

        var document = CanvasDocument.CreateNew();
        var paths = SyncPaths.ForBoard(scratch.BoardPath);

        var other = WithImage("ufile01", "assets/img_aaa.png");

        var far = new FakeRemote();
        far.Publish(paths.DocPath, document.ToJson());
        far.Publish("boards/other.canvas", other.ToJson());
        far.PublishRaw("boards/assets/img_aaa.png", new byte[] { 1, 2, 3 });

        var report = await AssetSync.ReapAsync(
            far, scratch.BoardPath, document, Tree(far), paths, new[] { "assets/img_aaa.png" }, default);

        Assert.Empty(report.Deleted);
        Assert.Single(report.Held);
        Assert.Contains("另一块板", report.Held[0].Reason);
        Assert.True(far.Files.ContainsKey("boards/assets/img_aaa.png"));
    }

    [Fact]
    public async Task A_board_sitting_here_without_a_remote_copy_still_holds_its_picture()
    {
        using var scratch = new Scratch("asset-reap-local");

        PutLocalImage(scratch, "img_aaa.png", new byte[] { 1, 2, 3 });

        var document = CanvasDocument.CreateNew();
        var paths = SyncPaths.ForBoard(scratch.BoardPath);

        // A second board in this folder that was never pushed: nothing on the far side says it
        // exists, so the far check alone would delete a picture this machine is displaying.
        CanvasStore.Write(Path.Combine(scratch.Folder, "other.canvas"), WithImage("ufile01", "assets/img_aaa.png").ToJson());
        CanvasStore.Write(scratch.BoardPath, document.ToJson());

        var far = new FakeRemote();
        far.Publish(paths.DocPath, document.ToJson());
        far.PublishRaw("boards/assets/img_aaa.png", new byte[] { 1, 2, 3 });

        var report = await AssetSync.ReapAsync(
            far, scratch.BoardPath, document, Tree(far), paths, new[] { "assets/img_aaa.png" }, default);

        Assert.Empty(report.Deleted);
        Assert.Contains(report.Held, held => held.Reason.Contains("这台机器"));
        Assert.True(far.Files.ContainsKey("boards/assets/img_aaa.png"));
    }

    [Fact]
    public async Task An_unreadable_board_beside_this_one_holds_every_picture_that_round()
    {
        using var scratch = new Scratch("asset-reap-corrupt");

        PutLocalImage(scratch, "img_aaa.png", new byte[] { 1, 2, 3 });

        var document = CanvasDocument.CreateNew();
        var paths = SyncPaths.ForBoard(scratch.BoardPath);

        var far = new FakeRemote();
        far.Publish(paths.DocPath, document.ToJson());
        far.Publish("boards/other.canvas", "not a canvas at all, and not JSON");
        far.PublishRaw("boards/assets/img_aaa.png", new byte[] { 1, 2, 3 });

        var report = await AssetSync.ReapAsync(
            far, scratch.BoardPath, document, Tree(far), paths, new[] { "assets/img_aaa.png" }, default);

        Assert.Empty(report.Deleted);
        Assert.Equal(0, far.DeleteCalls);
        Assert.True(far.Files.ContainsKey("boards/assets/img_aaa.png"));
    }

    [Fact]
    public async Task Reaping_an_empty_list_asks_the_far_side_for_nothing()
    {
        using var scratch = new Scratch("asset-reap-idle");

        var document = CanvasDocument.CreateNew();
        var paths = SyncPaths.ForBoard(scratch.BoardPath);

        var far = new FakeRemote();

        var report = await AssetSync.ReapAsync(
            far, scratch.BoardPath, document, Tree(far), paths, [], default);

        Assert.False(report.DidAnything);
        Assert.Equal(0, far.DeleteCalls);
    }
}
