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
}
