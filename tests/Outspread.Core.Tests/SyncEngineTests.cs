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

    private static (SyncEngine Engine, SyncLedger Ledger, List<string> Trace) Build(Scratch scratch, FakeRemote far)
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

            Assert.DoesNotContain((byte)13, File.ReadAllBytes(Path.Combine(folder, SyncConfig.FileName)));
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
}
