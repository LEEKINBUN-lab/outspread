namespace Outspread.Core.Tests;

/// <summary>
/// Rounds against an in-process far side. Everything here is the part a live host makes
/// hardest to arrange on purpose: the refusal, the race, the half-written file, the cable
/// that comes out mid-round.
/// </summary>
public class SyncEngineTests
{
    private const string DocPath = "boards/scratch.canvas";

    private const string LedgerPath = "boards/.sync/scratch.manifest.json";

    private static CanvasDocument Board(params (string Id, string Text)[] notes)
    {
        var document = CanvasDocument.CreateNew();

        foreach (var (id, text) in notes)
        {
            document.Add(CanvasNodeKind.Text, 0, 0, 200, 60, id: id).Text = text;
        }

        return document;
    }

    private static CanvasDocument Copy(CanvasDocument document) => CanvasDocument.Parse(document.ToJson());

    private static void WriteBoard(Scratch scratch, CanvasDocument document)
        => CanvasStore.Write(scratch.BoardPath, document.ToJson());

    private static string BoardText(Scratch scratch) => CanvasStore.ReadAllText(scratch.BoardPath);

    private static (SyncEngine Engine, SyncLedger Ledger, List<string> Trace) Build(Scratch scratch, IRemoteFiles far)
    {
        var ledger = new SyncLedger(Path.Combine(scratch.Folder, "ledger"));
        var trace = new List<string>();

        return (new SyncEngine(far, ledger, scratch.BoardPath, trace.Add), ledger, trace);
    }

    [Fact]
    public async Task A_first_round_uploads_the_board_and_its_ledger_entry()
    {
        using var scratch = new Scratch("sync-first");
        WriteBoard(scratch, Board(("uone", "one")));

        var far = new FakeRemote();
        var (engine, ledger, _) = Build(scratch, far);

        var report = await engine.SynchronizeAsync();

        Assert.Equal(SyncOutcome.Pushed, report.Outcome);
        Assert.NotNull(far.Text(DocPath));
        Assert.Contains("uone", far.Text(DocPath));

        var manifest = SyncManifest.Parse(far.Text(LedgerPath)!);

        Assert.NotNull(manifest);
        Assert.Equal(1, manifest!.Rev);

        var state = ledger.Load();

        Assert.Equal(1, state.Rev);
        Assert.Equal(1, state.Clock);
        Assert.NotNull(state.BaseFile);
        Assert.Null(state.LastError);
    }

    [Fact]
    public async Task Nothing_moving_costs_one_request_and_not_one_file_read()
    {
        using var scratch = new Scratch("sync-idle");
        WriteBoard(scratch, Board(("uone", "one")));

        var far = new FakeRemote();
        var (engine, _, _) = Build(scratch, far);

        await engine.SynchronizeAsync();

        far.ResetCounters();

        var report = await engine.SynchronizeAsync();

        Assert.Equal(SyncOutcome.NothingToDo, report.Outcome);
        Assert.Equal(1, far.TreeCalls);
        Assert.Equal(0, far.ReadCalls);
        Assert.Equal(0, far.WriteCalls);
    }

    [Fact]
    public async Task A_note_added_far_away_arrives_without_losing_the_one_added_here()
    {
        using var scratch = new Scratch("sync-both");
        WriteBoard(scratch, Board(("uone", "one")));

        var far = new FakeRemote();
        var (engine, ledger, _) = Build(scratch, far);

        await engine.SynchronizeAsync();

        // The phone writes while this board is closed, straight onto the far side.
        var asThePhoneLeftIt = CanvasDocument.Parse(far.Text(DocPath)!);
        asThePhoneLeftIt.Add(CanvasNodeKind.Text, 400, 0, 200, 60, id: "uphone").Text = "pasted on the phone";
        far.Publish(DocPath, asThePhoneLeftIt.ToJson());

        var here = CanvasDocument.Parse(BoardText(scratch));
        here.Add(CanvasNodeKind.Text, 800, 0, 200, 60, id: "uboard").Text = "pasted on the board";
        WriteBoard(scratch, here);

        var report = await engine.SynchronizeAsync();

        Assert.Equal(SyncOutcome.Pushed, report.Outcome);

        var uploaded = CanvasDocument.Parse(far.Text(DocPath)!);

        Assert.NotNull(uploaded.Find("uphone"));
        Assert.NotNull(uploaded.Find("uboard"));
        Assert.NotNull(uploaded.Find("uone"));

        // This disk ends up holding the reconciled board, not only what it wrote itself.
        var onDisk = CanvasDocument.Parse(BoardText(scratch));

        Assert.NotNull(onDisk.Find("uphone"));
        Assert.Equal(3, onDisk.Nodes.Count);

        Assert.Equal(report.Rev, ledger.Load().Rev);
    }

    [Fact]
    public async Task A_write_refused_by_a_stale_token_remedies_itself_on_the_next_round()
    {
        using var scratch = new Scratch("sync-race");
        WriteBoard(scratch, Board(("uone", "one")));

        var far = new FakeRemote();
        var (engine, ledger, _) = Build(scratch, far);

        await engine.SynchronizeAsync();

        // The phone's note lands on the far side between this round's read of the token and
        // its write, so the first attempt is refused rather than silently winning.
        far.ArmDiverge(DocPath, Board(("uone", "one"), ("uphone", "pasted on the phone")).ToJson());

        var here = CanvasDocument.Parse(BoardText(scratch));
        here.Add(CanvasNodeKind.Text, 800, 0, 200, 60, id: "uboard").Text = "pasted on the board";
        WriteBoard(scratch, here);

        var report = await engine.SynchronizeAsync();

        Assert.Equal(SyncOutcome.Pushed, report.Outcome);
        Assert.Equal(2, report.Rounds);

        var uploaded = CanvasDocument.Parse(far.Text(DocPath)!);

        Assert.NotNull(uploaded.Find("uboard"));
        Assert.NotNull(uploaded.Find("uphone"));

        var onDisk = CanvasDocument.Parse(BoardText(scratch));

        Assert.Equal(3, onDisk.Nodes.Count);
        Assert.NotNull(onDisk.Find("uphone"));
        Assert.Equal(report.Rev, ledger.Load().Rev);
    }

    [Fact]
    public async Task A_far_side_that_never_stops_moving_leaves_this_disk_alone()
    {
        using var scratch = new Scratch("sync-giveup");
        WriteBoard(scratch, Board(("uone", "one")));

        var far = new FakeRemote();
        var (engine, ledger, trace) = Build(scratch, far);

        await engine.SynchronizeAsync();

        var state = ledger.Load();

        var arrivals = 0;

        // Every write finds a new note already there: a third writer that will not settle.
        far.DivergeEveryWrite(DocPath, () =>
        {
            arrivals++;

            return Board(("uone", "one"), ($"uarrival{arrivals}", "still arriving")).ToJson();
        });

        var here = CanvasDocument.Parse(BoardText(scratch));
        here.Add(CanvasNodeKind.Text, 800, 0, 200, 60, id: "uboard").Text = "pasted on the board";
        WriteBoard(scratch, here);

        var boardBefore = BoardText(scratch);

        var report = await engine.SynchronizeAsync();

        Assert.Equal(SyncOutcome.GaveUpConflict, report.Outcome);
        Assert.Equal(3, report.Rounds);

        // Nothing this device wrote was thrown away, and nothing it had was rewritten: the
        // board that never stopped moving was not adopted either.
        Assert.Equal(boardBefore, BoardText(scratch));
        Assert.DoesNotContain("uarrival", BoardText(scratch));
        Assert.NotNull(report.ConflictCopy);
        Assert.True(File.Exists(report.ConflictCopy));

        // The copy must never be mistaken for a board: it sits in the board's own folder, so a
        // name ending in `.canvas` would make it a candidate the moment the real board is gone.
        Assert.False(CanvasStore.IsBoardPath(report.ConflictCopy));

        var copyName = Path.GetFileName(report.ConflictCopy!);

        // 名字里要看得见"哪台机器、哪一分钟"：同一块板子两台设备相隔几秒各留一份的时候，
        // 只能靠资源管理器里那一行认，靠修改时间猜不算认得出。
        Assert.Matches(@"\.conflict-.+-\d{8}-\d{4}-r\d+$", copyName);

        Assert.DoesNotContain(
            copyName,
            Directory.GetFiles(Path.GetDirectoryName(report.ConflictCopy!)!, "*.canvas")
                .Select(Path.GetFileName));

        var copy = CanvasDocument.Parse(File.ReadAllText(report.ConflictCopy!));

        Assert.NotNull(copy.Find("uboard"));

        var after = ledger.Load();

        Assert.Equal(state.BaseFile, after.BaseFile);
        Assert.Equal(state.Rev, after.Rev);
        Assert.Equal(nameof(SyncOutcome.GaveUpConflict), after.LastErrorKind);
        Assert.Contains(trace, line => line.Contains("重新合并"));
    }

    [Fact]
    public async Task An_unauthorized_write_stops_the_round_instead_of_spending_three_on_it()
    {
        using var scratch = new Scratch("sync-auth");
        WriteBoard(scratch, Board(("uone", "one")));

        var far = new FakeRemote();
        var (engine, ledger, _) = Build(scratch, far);

        far.FailNextWrite(DocPath, WriteOutcome.Unauthorized, "the token cannot write to this repository");

        var report = await engine.SynchronizeAsync();

        Assert.Equal(SyncOutcome.Unauthorized, report.Outcome);
        Assert.Contains("token cannot write", report.Detail);

        // One attempt, not three: retrying an account that cannot write burns quota and
        // changes nothing.
        Assert.Equal(1, far.WriteCalls);
        Assert.Null(far.Text(DocPath));

        var state = ledger.Load();

        Assert.Equal(nameof(SyncOutcome.Unauthorized), state.LastErrorKind);
        Assert.Null(state.DocToken);
    }

    [Fact]
    public async Task A_half_written_far_document_stops_sync_and_touches_nothing()
    {
        using var scratch = new Scratch("sync-corrupt");
        WriteBoard(scratch, Board(("uone", "one")));

        var far = new FakeRemote();
        var (engine, ledger, _) = Build(scratch, far);

        await engine.SynchronizeAsync();

        far.Corrupt(DocPath);

        var here = CanvasDocument.Parse(BoardText(scratch));
        here.Add(CanvasNodeKind.Text, 800, 0, 200, 60, id: "uboard").Text = "pasted on the board";
        WriteBoard(scratch, here);

        var before = BoardText(scratch);

        var report = await engine.SynchronizeAsync();

        Assert.Equal(SyncOutcome.RemoteUnreadable, report.Outcome);
        Assert.Equal(before, BoardText(scratch));

        // The refusal to publish means the pending note is still pending on the next look,
        // not that this round reported and moved on.
        var state = ledger.Load();

        Assert.Equal(nameof(SyncOutcome.RemoteUnreadable), state.LastErrorKind);
        Assert.Equal("pasted on the board", CanvasDocument.Parse(BoardText(scratch)).Find("uboard")!.Text);
    }

    [Fact]
    public async Task A_disconnected_round_keeps_the_ancestor_the_ledger_had()
    {
        using var scratch = new Scratch("sync-offline");
        WriteBoard(scratch, Board(("uone", "one")));

        var far = new FakeRemote();
        var (engine, ledger, _) = Build(scratch, far);

        await engine.SynchronizeAsync();

        var before = ledger.Load();

        var here = CanvasDocument.Parse(BoardText(scratch));
        here.Add(CanvasNodeKind.Text, 800, 0, 60, 60, id: "uboard").Text = "pasted while offline";
        WriteBoard(scratch, here);

        far.DropConnection();

        var offline = await engine.SynchronizeAsync();

        Assert.Equal(SyncOutcome.Offline, offline.Outcome);

        var during = ledger.Load();

        Assert.Equal(before.BaseFile, during.BaseFile);
        Assert.Equal(before.DocToken, during.DocToken);
        Assert.Equal(before.PublishedSha, during.PublishedSha);

        // Coming back is the point of a ledger: the note still reaches the far side, and it
        // reaches it merged against what arrived in the meantime rather than over it.
        var whileAway = Board(("uone", "one"), ("uphone", "pasted on the phone"));
        far.Publish(DocPath, whileAway.ToJson());

        far.RestoreConnection();

        var restored = await engine.SynchronizeAsync();

        Assert.Equal(SyncOutcome.Pushed, restored.Outcome);

        var uploaded = CanvasDocument.Parse(far.Text(DocPath)!);

        Assert.NotNull(uploaded.Find("uboard"));
        Assert.NotNull(uploaded.Find("uphone"));
    }

    [Fact]
    public async Task A_local_edit_underneath_a_round_is_not_written_over()
    {
        using var scratch = new Scratch("sync-underneath");
        WriteBoard(scratch, Board(("uone", "one")));

        var far = new FakeRemote();
        var (engine, ledger, _) = Build(scratch, far);

        await engine.SynchronizeAsync();

        var here = CanvasDocument.Parse(BoardText(scratch));
        here.Add(CanvasNodeKind.Text, 800, 0, 200, 60, id: "uboard").Text = "pasted on the board";
        WriteBoard(scratch, here);

        // The agent writes straight into the round, through the CLI's own path, while this
        // device is between reading the board and publishing the merge.
        far.OnWrite = path =>
        {
            if (path != DocPath)
            {
                return;
            }

            var withAgent = CanvasDocument.Parse(BoardText(scratch));
            withAgent.Add(CanvasNodeKind.Text, 400, 400, 200, 60, id: "a-agent01").Text = "written by an agent";
            WriteBoard(scratch, withAgent);
        };

        var report = await engine.SynchronizeAsync();

        far.OnWrite = null;

        Assert.Equal(SyncOutcome.DiskMovedUnderneath, report.Outcome);

        var onDisk = CanvasDocument.Parse(BoardText(scratch));

        Assert.NotNull(onDisk.Find("a-agent01"));
        Assert.NotNull(onDisk.Find("uboard"));

        // The board did reach the far side, so the next round has to bring the agent's node
        // up there rather than consider this finished.
        var state = ledger.Load();

        Assert.NotNull(state.LastError);

        var next = await engine.SynchronizeAsync();

        Assert.Equal(SyncOutcome.Pushed, next.Outcome);
        Assert.NotNull(CanvasDocument.Parse(far.Text(DocPath)!).Find("a-agent01"));
    }

    [Fact]
    public async Task Two_ends_writing_different_words_to_one_note_both_survive_the_round_trip()
    {
        using var scratch = new Scratch("sync-words");
        WriteBoard(scratch, Board(("uone", "shared start")));

        var far = new FakeRemote();
        var (engine, _, _) = Build(scratch, far);

        await engine.SynchronizeAsync();

        var asThePhoneLeftIt = CanvasDocument.Parse(far.Text(DocPath)!);
        asThePhoneLeftIt.Find("uone")!.Text = "typed on the phone";
        far.Publish(DocPath, asThePhoneLeftIt.ToJson());

        var here = CanvasDocument.Parse(BoardText(scratch));
        here.Find("uone")!.Text = "typed on the board";
        WriteBoard(scratch, here);

        var report = await engine.SynchronizeAsync();

        Assert.Equal(SyncOutcome.Pushed, report.Outcome);
        Assert.Contains(report.Verdicts, verdict => verdict.Rule == MergeRule.ContentConflictCopy);

        var uploaded = CanvasDocument.Parse(far.Text(DocPath)!);

        Assert.Equal("typed on the phone", uploaded.Find("uone")!.Text);
        Assert.Contains(uploaded.Nodes, node => node.Text == "typed on the board");
        Assert.Equal(2, uploaded.Nodes.Count);
    }

    [Fact]
    public async Task Two_boards_can_share_one_folder_without_naming_each_other()
    {
        using var scratch = new Scratch("sync-two-boards");

        var first = Path.Combine(scratch.Folder, "boards", "one.canvas");
        var second = Path.Combine(scratch.Folder, "boards", "two.canvas");

        CanvasStore.Write(first, Board(("uone", "one")).ToJson());
        CanvasStore.Write(second, Board(("utwo", "two")).ToJson());

        var far = new FakeRemote();

        var firstLedger = new SyncLedger(Path.Combine(scratch.Folder, "ledger-one"));
        var secondLedger = new SyncLedger(Path.Combine(scratch.Folder, "ledger-two"));

        var reportOne = await new SyncEngine(far, firstLedger, first).SynchronizeAsync();
        var reportTwo = await new SyncEngine(far, secondLedger, second).SynchronizeAsync();

        Assert.Equal(SyncOutcome.Pushed, reportOne.Outcome);
        Assert.Equal(SyncOutcome.Pushed, reportTwo.Outcome);

        Assert.NotNull(far.Text("boards/one.canvas"));
        Assert.NotNull(far.Text("boards/two.canvas"));

        // One board's ledger entry must not be able to answer for the other's clock.
        Assert.NotNull(far.Text("boards/.sync/one.manifest.json"));
        Assert.NotNull(far.Text("boards/.sync/two.manifest.json"));
        Assert.DoesNotContain("uone", far.Text("boards/two.canvas"));
    }

    [Fact]
    public void The_repository_note_reads_the_same_on_both_sides_of_the_ocean()
    {
        var bytes = new SyncWorkspace { WrittenBy = "board", At = "2026-10-12T00:00:00Z" }.ToBytes();

        // Two implementations compare these bytes. A CRLF in a JSON file written on Windows
        // would make one content answer as two digests. Indented JSON has many line feeds —
        // the point is that none of them is preceded by a carriage return.
        Assert.DoesNotContain((byte)13, bytes);
        Assert.Equal((byte)10, bytes[^1]);
        Assert.True(bytes.Count(value => value == 10) > 1);

        var back = SyncWorkspace.Parse(System.Text.Encoding.UTF8.GetString(bytes));

        Assert.NotNull(back);
        Assert.Equal("jsoncanvas-1.0", back!.Format);
    }

    [Fact]
    public void A_credential_written_by_cmdkey_is_still_one_token()
    {
        // cmdkey stores the blob as UTF-16. Decoded as UTF-8 that is the token with a NUL
        // between every character, and the HTTP header refuses it far from the real mistake.
        var utf16 = System.Text.Encoding.Unicode.GetBytes("github_pat_abc");

        Assert.Equal("github_pat_abc", SyncCredentials.DecodeStored(utf16));
        Assert.Equal("github_pat_abc", SyncCredentials.DecodeStored(System.Text.Encoding.UTF8.GetBytes("github_pat_abc")));

        // Built from code points rather than escapes: a pasted token arrives with CRLF.
        Assert.Equal("github_pat_abc", SyncCredentials.DecodeStored(
            System.Text.Encoding.Unicode.GetBytes("github_pat_abc" + (char)13 + (char)10)));
    }

    [Fact]
    public async Task A_round_that_moved_a_picture_says_which_one_even_though_the_board_changed_too()
    {
        using var scratch = new Scratch("sync-assets");

        var document = CanvasDocument.CreateNew();
        document.Add(CanvasNodeKind.File, 0, 0, 200, 100, id: "ufile01").FilePath = "assets/img_aaa.png";
        CanvasStore.Write(scratch.BoardPath, document.ToJson());

        var image = Path.Combine(AssetStore.AssetsDirectory(scratch.BoardPath), "img_aaa.png");
        Directory.CreateDirectory(Path.GetDirectoryName(image)!);
        File.WriteAllBytes(image, new byte[] { 1, 2, 3, 4 });

        var far = new FakeRemote();
        var (engine, _, _) = Build(scratch, far);

        var report = await engine.SynchronizeAsync();

        Assert.Equal(SyncOutcome.Pushed, report.Outcome);
        Assert.NotNull(report.Assets);
        Assert.Equal(new[] { "assets/img_aaa.png" }, report.Assets!.Uploaded);
        Assert.Equal(new byte[] { 1, 2, 3, 4 }, far.Files["boards/assets/img_aaa.png"]);

        // The same round on a second device that lacks the picture has to name it as taken
        // down, or a panel can only stay silent about the one change the user can see.
        using var other = new Scratch("sync-assets-b");

        var mirror = CanvasDocument.Parse(CanvasStore.ReadAllText(scratch.BoardPath));
        CanvasStore.Write(other.BoardPath, mirror.ToJson());

        var secondLedger = new SyncLedger(Path.Combine(other.Folder, "ledger"));
        var second = new SyncEngine(far, secondLedger, other.BoardPath);

        await second.SynchronizeAsync();
        far.PublishRaw("boards/assets/img_bbb.png", new byte[] { 7, 7, 7 });

        var boardWithSecond = CanvasDocument.Parse(CanvasStore.ReadAllText(other.BoardPath));
        boardWithSecond.Add(CanvasNodeKind.File, 300, 0, 200, 100, id: "ufile02").FilePath = "assets/img_bbb.png";
        CanvasStore.Write(other.BoardPath, boardWithSecond.ToJson());

        var pulled = await second.SynchronizeAsync();

        Assert.NotNull(pulled.Assets);
        Assert.Contains("assets/img_bbb.png", pulled.Assets!.Downloaded);
        Assert.True(File.Exists(Path.Combine(other.Folder, "assets", "img_bbb.png")));
    }

    [Fact]
    public void The_ledger_entry_a_phone_reads_is_line_ending_free()
    {
        // This file is committed to the repository and read by both ends; the phone reads LF.
        // .NET's indented writer emits the platform line ending, so without normalising, one
        // content answers as two digests depending on which machine wrote it.
        var bytes = SyncManifest.ToBytes(new SyncManifest { Rev = 3, ContentSha = "ab", By = "pc", Clock = 3 });

        Assert.DoesNotContain((byte)13, bytes);
        Assert.Contains((byte)10, bytes);

        var config = new SyncConfig { Owner = "o", Repo = "r" };
        var folder = Path.Combine(Path.GetTempPath(), "outspread-lf-check", Guid.NewGuid().ToString("N")[..8]);

        try
        {
            config.Save(folder);

            var written = File.ReadAllBytes(Path.Combine(folder, SyncConfig.FileName));

            Assert.DoesNotContain((byte)13, written);

            // The phone ends these with one LF; a device-local file that differs only by a
            // trailing byte is a difference nobody asked for.
            Assert.Equal((byte)10, written[^1]);
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    [Fact]
    public void A_token_with_the_newline_that_came_with_it_is_still_one_token()
    {
        // cmdkey and a piped --token-stdin both hand over a trailing newline, and a header
        // value carrying one fails deep inside the request instead of where it went wrong.
        Assert.Equal("github_pat_abc", SyncCredentials.Normalize("github_pat_abc\r\n"));
        Assert.Equal("github_pat_abc", SyncCredentials.Normalize("  github_pat_abc  "));
        Assert.Null(SyncCredentials.Normalize("   \r\n"));
        Assert.Null(SyncCredentials.Normalize(null));
    }

    [Fact]
    public void A_device_name_is_a_fact_and_not_a_roll_of_the_dice()
    {
        using var scratch = new Scratch("sync-device");

        var ledger = new SyncLedger(Path.Combine(scratch.Folder, "ledger"));

        // The device name settles merge ties, so reading the ledger twice must not print two
        // different answers, and a status line shown before the first sync must be the same
        // name the far side will later see.
        var first = ledger.Load();
        var second = ledger.Load();

        Assert.Equal(first.DeviceId, second.DeviceId);
        Assert.True(File.Exists(Path.Combine(ledger.Folder, "state.json")));

        Assert.Equal(first.DeviceId, new SyncLedger(Path.Combine(scratch.Folder, "ledger")).Load().DeviceId);
    }

    [Fact]
    public async Task A_refusal_for_size_is_not_reported_as_a_limit()
    {
        using var scratch = new Scratch("sync-size");
        WriteBoard(scratch, Board(("uone", "one")));

        var far = new FakeRemote();
        var (engine, _, _) = Build(scratch, far);

        far.FailNextWrite(DocPath, WriteOutcome.Rejected, "HTTP 422: content is over what this endpoint carries");

        var report = await engine.SynchronizeAsync();

        // A limit empties itself and the same bytes may go through later; a refusal for shape
        // never will, so the two must not share a sentence or a retry policy.
        Assert.Equal(SyncOutcome.RemoteRejected, report.Outcome);
        Assert.DoesNotContain("限流", report.StatusLine());
        Assert.Equal(1, far.WriteCalls);
    }

    [Fact]
    public async Task The_status_line_reports_readings_and_never_the_word_synced_without_a_number()
    {
        using var scratch = new Scratch("sync-status");
        WriteBoard(scratch, Board(("uone", "one")));

        var far = new FakeRemote();
        var (engine, ledger, _) = Build(scratch, far);

        var pushed = await engine.SynchronizeAsync();

        var state = ledger.Load();

        Assert.Equal($"已上传 r1 · 远端 {state.ContentSha!.Substring(0, 6)} · 待发 0", pushed.StatusLine(state.ContentSha));

        far.DropConnection();

        var here = CanvasDocument.Parse(BoardText(scratch));
        here.Add(CanvasNodeKind.Text, 800, 0, 200, 60, id: "uboard").Text = "waiting";
        WriteBoard(scratch, here);

        var offline = await engine.SynchronizeAsync();

        Assert.StartsWith("未同步：离线", offline.StatusLine());
        Assert.Contains("待发 1", offline.StatusLine());
    }

    [Fact]
    public void A_missing_local_board_is_reported_without_colloquial_wording()
    {
        var report = new SyncReport(
            SyncOutcome.LocalUnreadable,
            "Could not find file 'D:\\board\\未命名板1.canvas'",
            0,
            0,
            0,
            []);

        Assert.Equal("未同步：本地文件不可读 r0 · 待发 0", report.StatusLine());
    }

    private static void PutImage(Scratch scratch, string name, byte[] bytes)
    {
        var path = Path.Combine(AssetStore.AssetsDirectory(scratch.BoardPath), name);

        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, bytes);
    }

    private static CanvasDocument BoardWithImage(string relative)
    {
        var document = CanvasDocument.CreateNew();

        document.Add(CanvasNodeKind.Text, 0, 0, 200, 60, id: "unote1").Text = "字";
        document.Add(CanvasNodeKind.File, 0, 100, 200, 100, id: "ufile01").FilePath = relative;

        return document;
    }

    private static SyncManifest? FarManifest(FakeRemote far, SyncPaths paths)
    {
        var text = far.Text(paths.ManifestPath);

        return text is null ? null : SyncManifest.Parse(text);
    }

    [Fact]
    public async Task Deleting_a_picture_card_takes_the_file_off_the_far_side_and_writes_it_into_the_ledger()
    {
        using var scratch = new Scratch("sync-reap");

        var withImage = BoardWithImage("assets/img_aaa.png");
        WriteBoard(scratch, withImage);
        PutImage(scratch, "img_aaa.png", new byte[] { 1, 2, 3, 4 });

        var far = new FakeRemote();
        var (engine, _, _) = Build(scratch, far);
        var paths = SyncPaths.ForBoard(scratch.BoardPath);

        await engine.SynchronizeAsync();

        Assert.True(far.Files.ContainsKey("boards/assets/img_aaa.png"));

        var without = CanvasDocument.Parse(BoardText(scratch));
        without.Remove("ufile01");
        WriteBoard(scratch, without);

        var report = await engine.SynchronizeAsync();

        Assert.Equal(SyncOutcome.Pushed, report.Outcome);
        Assert.NotNull(report.Assets);
        Assert.Equal(new[] { "assets/img_aaa.png" }, report.Assets!.Deleted);
        Assert.Empty(report.Assets.Held);

        // Gone from the repository, off this machine, and written down where the other end
        // will read it: the board file itself cannot say a picture was removed.
        Assert.False(far.Files.ContainsKey("boards/assets/img_aaa.png"));
        Assert.Null(AssetStore.ResolveExisting(scratch.BoardPath, "assets/img_aaa.png"));
        Assert.Equal(new[] { "assets/img_aaa.png" }, FarManifest(far, paths)!.DroppedAssets);
    }

    [Fact]
    public async Task The_other_end_reads_the_tombstone_and_loses_its_copy_without_one_verdict()
    {
        using var first = new Scratch("sync-reap-a");
        using var second = new Scratch("sync-reap-b");

        WriteBoard(first, BoardWithImage("assets/img_aaa.png"));
        PutImage(first, "img_aaa.png", new byte[] { 1, 2, 3, 4 });

        var far = new FakeRemote();
        var paths = SyncPaths.ForBoard(first.BoardPath);

        var (firstEngine, _, _) = Build(first, far);
        await firstEngine.SynchronizeAsync();

        // The second device has the same board and the same picture, and no idea yet that
        // anything happened.
        CanvasStore.Write(second.BoardPath, CanvasStore.ReadAllText(first.BoardPath));
        PutImage(second, "img_aaa.png", new byte[] { 1, 2, 3, 4 });

        var secondLedger = new SyncLedger(Path.Combine(second.Folder, "ledger"));
        var secondEngine = new SyncEngine(far, secondLedger, second.BoardPath);

        await secondEngine.SynchronizeAsync();

        var without = CanvasDocument.Parse(CanvasStore.ReadAllText(first.BoardPath));
        without.Remove("ufile01");
        WriteBoard(first, without);

        await firstEngine.SynchronizeAsync();

        var applied = await secondEngine.SynchronizeAsync();

        Assert.Equal(SyncOutcome.Pulled, applied.Outcome);
        Assert.Empty(applied.Verdicts);
        Assert.Equal(new[] { "assets/img_aaa.png" }, applied.Assets!.Deleted);
        Assert.Null(AssetStore.ResolveExisting(second.BoardPath, "assets/img_aaa.png"));
    }

    [Fact]
    public async Task A_picture_brought_back_by_a_racing_edit_is_not_eaten_by_its_own_tombstone()
    {
        using var first = new Scratch("sync-reap-race-a");
        using var second = new Scratch("sync-reap-race-b");

        WriteBoard(first, BoardWithImage("assets/img_aaa.png"));
        PutImage(first, "img_aaa.png", new byte[] { 1, 2, 3, 4 });

        var far = new FakeRemote();
        var paths = SyncPaths.ForBoard(first.BoardPath);

        var (firstEngine, _, _) = Build(first, far);
        await firstEngine.SynchronizeAsync();

        CanvasStore.Write(second.BoardPath, CanvasStore.ReadAllText(first.BoardPath));
        PutImage(second, "img_aaa.png", new byte[] { 1, 2, 3, 4 });

        var secondLedger = new SyncLedger(Path.Combine(second.Folder, "ledger"));
        var secondEngine = new SyncEngine(far, secondLedger, second.BoardPath);

        await secondEngine.SynchronizeAsync();

        // One end deletes the card; the other end is still typing into it.
        var without = CanvasDocument.Parse(CanvasStore.ReadAllText(first.BoardPath));
        without.Remove("ufile01");
        WriteBoard(first, without);

        await firstEngine.SynchronizeAsync();

        Assert.False(far.Files.ContainsKey("boards/assets/img_aaa.png"));

        var edited = CanvasDocument.Parse(CanvasStore.ReadAllText(second.BoardPath));
        edited.Find("ufile01")!.Text = "还在写";
        WriteBoard(second, edited);

        var revived = await secondEngine.SynchronizeAsync();

        Assert.Contains(revived.Verdicts, verdict => verdict.Rule == MergeRule.Revived);

        // The picture is on the board again, so the ledger's word that it was gone has to
        // stop: an entry that outlives the reference it was about is a deletion waiting to
        // break a card nobody deleted.
        Assert.Equal(new byte[] { 1, 2, 3, 4 }, far.Files["boards/assets/img_aaa.png"]);
        Assert.Empty(FarManifest(far, paths)!.DroppedAssets);
        Assert.NotNull(AssetStore.ResolveExisting(second.BoardPath, "assets/img_aaa.png"));
    }

    [Fact]
    public async Task A_round_that_could_not_publish_takes_no_picture_away()
    {
        using var scratch = new Scratch("sync-reap-refused");

        WriteBoard(scratch, BoardWithImage("assets/img_aaa.png"));
        PutImage(scratch, "img_aaa.png", new byte[] { 1, 2, 3, 4 });

        var far = new FakeRemote();
        var (engine, _, _) = Build(scratch, far);
        var paths = SyncPaths.ForBoard(scratch.BoardPath);

        await engine.SynchronizeAsync();

        var without = CanvasDocument.Parse(BoardText(scratch));
        without.Remove("ufile01");
        WriteBoard(scratch, without);

        far.FailNextWrite(paths.DocPath, WriteOutcome.Unauthorized);

        var refused = await engine.SynchronizeAsync();

        Assert.NotEqual(SyncOutcome.Pushed, refused.Outcome);
        Assert.Equal(0, far.DeleteCalls);
        Assert.True(far.Files.ContainsKey("boards/assets/img_aaa.png"));
        Assert.NotNull(AssetStore.ResolveExisting(scratch.BoardPath, "assets/img_aaa.png"));
    }

    [Fact]
    public async Task A_tombstone_with_nothing_left_to_delete_costs_no_extra_request()
    {
        using var first = new Scratch("sync-reap-cost-a");
        using var second = new Scratch("sync-reap-cost-b");

        WriteBoard(first, BoardWithImage("assets/img_aaa.png"));
        PutImage(first, "img_aaa.png", new byte[] { 1, 2, 3, 4 });

        var far = new FakeRemote();
        var (firstEngine, _, _) = Build(first, far);
        var paths = SyncPaths.ForBoard(first.BoardPath);

        await firstEngine.SynchronizeAsync();

        CanvasStore.Write(second.BoardPath, CanvasStore.ReadAllText(first.BoardPath));
        PutImage(second, "img_aaa.png", new byte[] { 1, 2, 3, 4 });

        var secondLedger = new SyncLedger(Path.Combine(second.Folder, "ledger"));
        var secondEngine = new SyncEngine(far, secondLedger, second.BoardPath);

        await secondEngine.SynchronizeAsync();

        var without = CanvasDocument.Parse(CanvasStore.ReadAllText(first.BoardPath));
        without.Remove("ufile01");
        WriteBoard(first, without);

        await firstEngine.SynchronizeAsync();

        // The other end takes its own copy away and publishes. Its ledger keeps naming the
        // picture — that is how a third device would learn — long after nothing has it.
        var edited = CanvasDocument.Parse(CanvasStore.ReadAllText(second.BoardPath));
        edited.Add(CanvasNodeKind.Text, 400, 0, 200, 60, id: "unote9").Text = "另一台写的";
        WriteBoard(second, edited);

        await secondEngine.SynchronizeAsync();

        Assert.Equal(new[] { "assets/img_aaa.png" }, FarManifest(far, paths)!.DroppedAssets);

        // So a round that reads a ledger naming a picture nobody has left must be able to tell
        // that without asking the far side a second time.
        var again = CanvasDocument.Parse(CanvasStore.ReadAllText(first.BoardPath));
        again.Add(CanvasNodeKind.Text, 700, 0, 200, 60, id: "unote8").Text = "又一笔";
        WriteBoard(first, again);

        far.ResetCounters();

        var later = await firstEngine.SynchronizeAsync();

        Assert.Equal(SyncOutcome.Pushed, later.Outcome);
        Assert.Equal(0, far.DeleteCalls);
        Assert.Equal(1, far.TreeCalls);
    }

    [Fact]
    public void The_tombstone_list_sits_last_in_the_ledger_entry()
    {
        var bytes = SyncManifest.ToBytes(new SyncManifest
        {
            Rev = 3,
            ContentSha = "ab",
            By = "pc",
            Clock = 3,
            At = "2026-10-14T00:00:00Z",
            DroppedAssets = ["assets/img_aaa.png", "assets/img_bbb.png"],
        });

        var text = System.Text.Encoding.UTF8.GetString(bytes);

        // The phone reads this file by shape as well as by name, and pinned three claims about
        // it: `droppedAssets` after `at`, LF only, and no trailing newline. The byte lengths it
        // measured (207 / 153) belong to its own fixture, so they are not repeated here — a
        // number copied from a reading whose inputs you did not set is a claim, not a test.
        Assert.DoesNotContain((byte)13, bytes);
        Assert.Equal((byte)'}', bytes[^1]);
        Assert.True(
            text.IndexOf("\"droppedAssets\"", StringComparison.Ordinal) > text.IndexOf("\"at\"", StringComparison.Ordinal),
            "droppedAssets must not move ahead of at: " + text);

        // The phone pins these same six inputs in its own test and reads 207 / 153 bytes back.
        // The numbers here are not copied from it: they were recomputed from this emitter with
        // its fixture, and came out byte-for-byte equal. That makes the pair a cross-end anchor
        // rather than one side's claim — and it is why changing the indent, the property order
        // or the encoder has to break this test, because it breaks the other end too.
        var two = SyncManifest.ToBytes(new SyncManifest
        {
            Rev = 3,
            ContentSha = "deadbeef",
            By = "enmusubi-3acd0c",
            Clock = 2,
            At = "2026-10-12T00:00:00Z",
            DroppedAssets = ["assets/img_aaa.png", "assets/img_bbb.png"],
        });

        Assert.Equal(207, two.Length);
        Assert.Equal(
            "28f341842a515bc19e31449cba66f05947137acaaf107fba89c6e3e7b6c2cb2e",
            Ledger.Digest(two));

        var without = SyncManifest.ToBytes(new SyncManifest
        {
            Rev = 3,
            ContentSha = "deadbeef",
            By = "enmusubi-3acd0c",
            Clock = 2,
            At = "2026-10-12T00:00:00Z",
        });

        Assert.Equal(153, without.Length);
        Assert.Equal(
            "f1e2fe5cee6d370cf594cee06f0f1c78f8469928229ecb09f3374c51baa981ec",
            Ledger.Digest(without));
    }

    [Fact]
    public async Task A_deletion_the_far_side_refused_is_asked_again_by_a_round_with_nothing_else_to_do()
    {
        using var scratch = new Scratch("sync-reap-retry");

        WriteBoard(scratch, BoardWithImage("assets/img_aaa.png"));
        PutImage(scratch, "img_aaa.png", new byte[] { 1, 2, 3, 4 });

        var far = new FakeRemote();
        var (engine, ledger, _) = Build(scratch, far);

        await engine.SynchronizeAsync();

        var without = CanvasDocument.Parse(BoardText(scratch));
        without.Remove("ufile01");
        WriteBoard(scratch, without);

        far.FailNextDelete();

        var refused = await engine.SynchronizeAsync();

        Assert.Equal(SyncOutcome.Pushed, refused.Outcome);
        Assert.Single(refused.Assets!.Held);

        // retry ⊆ held, by construction — and the window's notice filter walks held and asks
        // this one, so a retry nobody held would go unsaid.
        Assert.Equal(refused.Assets.Held.Select(pair => pair.Path), refused.Assets.Retry);
        Assert.True(far.Files.ContainsKey("boards/assets/img_aaa.png"));

        far.ResetCounters();

        // The board is perfectly still now, and so is the far side. A deletion this device
        // started is still its own unfinished business, which is why it waits in this device's
        // account rather than only in a ledger nobody reads twice.
        var idle = await engine.SynchronizeAsync();

        Assert.Equal(SyncOutcome.NothingToDo, idle.Outcome);
        Assert.Equal(new[] { "assets/img_aaa.png" }, idle.Assets!.Deleted);

        // Two tree calls, not one: the round's own, and the one right before the deletion.
        // That second request is the price of a residual, and only a residual pays it.
        Assert.Equal(2, far.TreeCalls);

        Assert.False(far.Files.ContainsKey("boards/assets/img_aaa.png"));
        Assert.Null(AssetStore.ResolveExisting(scratch.BoardPath, "assets/img_aaa.png"));
        Assert.Empty(ledger.Load().PendingDeletions);
    }

    /// <summary>
    /// 回归锁（10-30 夜刀四）：真 host 从 UI 线程的 DispatcherQueueTimer 进引擎，SyncEngine 原来
    /// 一个 ConfigureAwait 都没有——每个续体都钉回 UI 线程，空转一发也做读+析+SHA2。
    /// 让渡型远端给这一轮一个<b>真实的异步边界</b>（全是已完成任务的远端走 await 的同步快路，
    /// 什么也量不出来）：旧引擎在让渡那拍 Post 回调用方的上下文（≥2），新引擎落在线程池（引擎 0 次）。
    /// </summary>
    private sealed class YieldingRemote(IRemoteFiles inner) : IRemoteFiles
    {
        private async Task<T> Yield<T>(Task<T> task)
        {
            // 真让渡且不回调用方的上下文：Task.Yield 的续体总是走当前 SynchronizationContext，
            // 那样这一发自己就把计数污染了。Task.Run 过一道线程池再 ConfigureAwait，两样都占住。
            await Task.Run(static () => { }).ConfigureAwait(false);

            return await task.ConfigureAwait(false);
        }

        public Task<RemoteTree?> ReadTreeAsync(CancellationToken cancel) => Yield(inner.ReadTreeAsync(cancel));

        public Task<RemoteFile?> ReadAsync(string path, CancellationToken cancel) => Yield(inner.ReadAsync(path, cancel));

        public Task<WriteResult> WriteAsync(string path, byte[] content, string? expectToken, CancellationToken cancel)
            => Yield(inner.WriteAsync(path, content, expectToken, cancel));

        public Task<WriteOutcome> DeleteAsync(string path, string expectToken, CancellationToken cancel)
            => Yield(inner.DeleteAsync(path, expectToken, cancel));
    }

    private sealed class CountingContext : SynchronizationContext
    {
        public int Posts;

        public override void Post(SendOrPostCallback d, object? state)
        {
            Posts++;

            base.Post(d, state);
        }
    }

    [Fact]
    public async Task A_round_never_hops_back_to_the_callers_synchronization_context()
    {
        using var scratch = new Scratch("sync-context");
        WriteBoard(scratch, Board(("uone", "one")));

        var (engine, _, _) = Build(scratch, new YieldingRemote(new FakeRemote()));

        var context = new CountingContext();
        var previous = SynchronizationContext.Current;

        SynchronizationContext.SetSynchronizationContext(context);

        try
        {
            var report = await engine.SynchronizeAsync();

            Assert.Equal(SyncOutcome.Pushed, report.Outcome);

            // 引擎内部一个续体都没回调用方的上下文；唯一可能的一发 Post 是测试自己 await 的回执。
            // 旧引擎在让渡那一拍就已经 Post 回来了（≥2）——这条一旦红，就是又把续体钉回调用方了。
            Assert.True(context.Posts <= 1, $"引擎回调用方上下文 {context.Posts} 次——续体在往调用线程塞同步活");
        }
        finally
        {
            SynchronizationContext.SetSynchronizationContext(previous);
        }
    }
}
