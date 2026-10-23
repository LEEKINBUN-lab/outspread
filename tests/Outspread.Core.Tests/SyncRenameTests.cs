namespace Outspread.Core.Tests;

/// <summary>
/// 改名跨端跟上（契约「改名怎么跨端跟上」）。这一批用例判的是两件不可逆的事：
///  - **跟的人不许动本地**：把"远端那份不见了"读成删除，今天会拿空文档去合并，把整块板清空再推回去；
///  - **发的人顺序要对**：新名那份没上去之前不许先把标记写进去，否则同伴会跟到一个仓里还不存在的名字。
/// 解析规则本身（链、决胜、上限）在 <see cref="RenameLogTests"/> 与安卓的镜像测试里，靠同一批向量钉住。
/// </summary>
public class SyncRenameTests
{
    private const string OldDoc = "boards/周会.canvas";

    private const string NewDoc = "boards/项目周报.canvas";

    private static CanvasDocument Board(string id, string text)
    {
        var document = CanvasDocument.CreateNew();

        document.Add(CanvasNodeKind.Text, 0, 0, 200, 60, id: id).Text = text;

        return document;
    }

    [Fact]
    public async Task A_renamed_board_uploads_its_own_file_before_the_marker()
    {
        using var scratch = new Scratch("rename-publish");

        // 本机改名已经落地：盘上这块板就叫新名字了（引擎的路径是从文件名派生的）。
        var newPath = Path.Combine(scratch.Folder, "项目周报.canvas");

        CanvasStore.Write(newPath, Board("uone", "one").ToJson());

        var far = new FakeRemote();
        var ledger = new SyncLedger(Path.Combine(scratch.Folder, "ledger"));
        var order = new List<string>();

        far.OnWrite = order.Add;

        var seeded = ledger.Load();
        // 摘要拿本地这份的真字节算：写一个假串会让空转那一格进不去，而"改名以后新名那份到底
        // 有没有被上传"恰恰藏在那一格后面（2026-10-23 真机排练抓到的缺陷）。
        seeded.PublishedSha = Ledger.Digest(Ledger.Encode(CanvasStore.ReadAllText(newPath)));
        seeded.PendingRenameFrom = "周会";
        seeded.PendingRenameTo = "项目周报";
        ledger.Save(seeded);

        var report = await new SyncEngine(far, ledger, newPath).SynchronizeAsync();

        Assert.Equal(SyncOutcome.Pushed, report.Outcome);
        Assert.Equal(
            new[] { NewDoc, "boards/.sync/项目周报.manifest.json", RenameLog.RepoPath },
            order);

        var marker = RenameLog.Parse(far.Text(RenameLog.RepoPath));

        Assert.NotNull(marker);
        Assert.Equal("周会", Assert.Single(marker!.Entries).From);

        var state = ledger.Load();

        Assert.Null(state.PendingRenameTo);
        Assert.Null(state.LastError);
    }

    [Fact]
    public async Task A_marker_the_far_side_never_accepts_stays_pending_and_is_asked_again()
    {
        using var scratch = new Scratch("rename-retry");
        var newPath = Path.Combine(scratch.Folder, "项目周报.canvas");

        CanvasStore.Write(newPath, Board("uone", "one").ToJson());

        var far = new FakeRemote();
        var ledger = new SyncLedger(Path.Combine(scratch.Folder, "ledger"));
        var seeded = ledger.Load();

        seeded.PendingRenameFrom = "周会";
        seeded.PendingRenameTo = "项目周报";
        ledger.Save(seeded);

        // 另一台一直在往这份日志里写：条件写永远挤不进去。这不是"改名失败"——本机早改完了。
        var other = 0;

        far.DivergeEveryWrite(RenameLog.RepoPath, () =>
        {
            var rival = new RenameLog();
            rival.Append(new RenameEntry("别的板", "别的名字", "RIVAL", $"2026-10-23T22:00:{++other:00}Z"));

            return System.Text.Encoding.UTF8.GetString(RenameLog.ToBytes(rival));
        });

        await new SyncEngine(far, ledger, newPath).SynchronizeAsync();

        var afterFirst = ledger.Load();

        Assert.Equal("项目周报", afterFirst.PendingRenameTo);
        Assert.NotNull(afterFirst.LastError);

        // 同伴的条目一个字都没被顶掉（用我这一条去覆盖别人的改名是不可逆的）。
        var farLog = RenameLog.Parse(far.Text(RenameLog.RepoPath));

        Assert.Equal("别的板", Assert.Single(farLog!.Entries).From);
    }

    [Fact]
    public async Task A_board_the_far_side_renamed_leaves_this_disk_alone()
    {
        using var scratch = new Scratch("rename-follow");
        var oldPath = Path.Combine(scratch.Folder, "周会.canvas");

        CanvasStore.Write(oldPath, Board("uone", "one").ToJson());

        var far = new FakeRemote();
        var ledger = new SyncLedger(Path.Combine(scratch.Folder, "ledger"));

        // 先把这块板正常同步一次：这样它才有祖先与 publishedSha（= 契约第 1 条说的"确实同步过"）。
        var first = await new SyncEngine(far, ledger, oldPath).SynchronizeAsync();

        Assert.Equal(SyncOutcome.Pushed, first.Outcome);

        // 别的设备改了名：新名那份在仓里、旧名那份没了、标记指着新名。
        var marker = new RenameLog();
        marker.Append(new RenameEntry("周会", "项目周报", "OTHER-DEVICE", "2026-10-23T22:00:00Z"));
        far.Publish(NewDoc, far.Text(OldDoc)!);
        far.Publish(RenameLog.RepoPath, Encoding(marker));
        far.Remove(OldDoc);

        var before = CanvasStore.ReadAllText(oldPath);

        far.ResetCounters();

        var report = await new SyncEngine(far, ledger, oldPath).SynchronizeAsync();

        Assert.Equal(SyncOutcome.FollowedRename, report.Outcome);
        Assert.Equal("项目周报", report.RenameTo);
        Assert.Equal(0, far.WriteCalls);
        Assert.Equal(0, far.DeleteCalls);
        Assert.Equal(before, CanvasStore.ReadAllText(oldPath));
    }

    [Fact]
    public async Task A_vanished_board_with_no_marker_uploads_what_this_device_holds()
    {
        using var scratch = new Scratch("rename-no-marker");
        var oldPath = Path.Combine(scratch.Folder, "周会.canvas");

        CanvasStore.Write(oldPath, Board("uone", "one").ToJson());

        var far = new FakeRemote();
        var ledger = new SyncLedger(Path.Combine(scratch.Folder, "ledger"));

        await new SyncEngine(far, ledger, oldPath).SynchronizeAsync();
        far.Remove(OldDoc);

        var report = await new SyncEngine(far, ledger, oldPath).SynchronizeAsync();

        // 仓里那份不见了、又没有标记可查：这一发按"远端对这条路没意见"处理，把本地这份传上去。
        // 改之前这里是 Pulled ＋ 本地零格（把"文件不见了"读成"另一端把每一格都删了"）——同一条洞，
        // 同一条补法。记进契约：**远端整块板的删除从此只能由显式删除请求产生，不来自"文件不见了"**。
        Assert.Equal(SyncOutcome.Pushed, report.Outcome);
        Assert.Single(CanvasDocument.Parse(CanvasStore.ReadAllText(oldPath)).Nodes);
        Assert.NotNull(far.Text(OldDoc));
    }

    /// <summary>
    /// 2026-10-23 真机排练抓到的那一发，走**真实顺序**：先正常同步一次（于是有祖先、仓里有旧名），
    /// 再搬文件＋搬账本，再同步。第二发看到的远端是"新名这个路径下没有文件"——那不是"删光了"，
    /// 判错就把本地清空再推上去（排练乙 1 格 → 0 格就是这么来的）。
    /// </summary>
    [Fact]
    public async Task A_renamed_board_uploads_what_it_holds_instead_of_taking_the_empty_far_down()
    {
        using var scratch = new Scratch("rename-upload");
        var root = Path.Combine(scratch.Folder, "sync");
        var oldPath = Path.Combine(scratch.Folder, "周会.canvas");
        var newPath = Path.Combine(scratch.Folder, "项目周报.canvas");

        CanvasStore.Write(oldPath, Board("uone", "one").ToJson());

        var far = new FakeRemote();

        var first = await new SyncEngine(far, new SyncLedger(SyncLayout.LedgerFolder(oldPath, root)), oldPath)
            .SynchronizeAsync();

        Assert.Equal(SyncOutcome.Pushed, first.Outcome);
        Assert.NotNull(far.Text(OldDoc));

        File.Move(oldPath, newPath);

        Assert.Null(SyncLayout.RenameLedger(oldPath, newPath, root));

        var second = await new SyncEngine(far, new SyncLedger(SyncLayout.LedgerFolder(newPath, root)), newPath)
            .SynchronizeAsync();

        Assert.Equal(SyncOutcome.Pushed, second.Outcome);
        Assert.Contains("uone", far.Text(NewDoc));
        Assert.Single(CanvasDocument.Parse(CanvasStore.ReadAllText(newPath), newPath).Nodes);
        Assert.Equal("周会", RenameLog.Parse(far.Text(RenameLog.RepoPath)!)!.Entries.Single().From);
    }

    [Fact]
    public async Task A_marker_pointing_at_a_name_that_is_not_in_the_repo_is_not_followed()
    {
        using var scratch = new Scratch("rename-dead-marker");
        var oldPath = Path.Combine(scratch.Folder, "周会.canvas");

        CanvasStore.Write(oldPath, Board("uone", "one").ToJson());

        var far = new FakeRemote();
        var ledger = new SyncLedger(Path.Combine(scratch.Folder, "ledger"));

        await new SyncEngine(far, ledger, oldPath).SynchronizeAsync();

        var marker = new RenameLog();
        marker.Append(new RenameEntry("周会", "项目周报", "OTHER-DEVICE", "2026-10-23T22:00:00Z"));
        far.Publish(RenameLog.RepoPath, Encoding(marker));
        far.Remove(OldDoc);

        var report = await new SyncEngine(far, ledger, oldPath).SynchronizeAsync();

        // 半截发布不由标记来兜（契约第 2 条）：目标不在仓里 ⇒ 不跟，也不停在半路。
        Assert.NotEqual(SyncOutcome.FollowedRename, report.Outcome);
    }

    [Fact]
    public void Renaming_moves_the_ledger_and_clears_the_two_tokens()
    {
        var root = Path.Combine(Path.GetTempPath(), "outspread-rename-ledger-" + Guid.NewGuid().ToString("N")[..8]);
        var oldPath = Path.Combine(root, "boards", "周会.canvas");
        var newPath = Path.Combine(root, "boards", "项目周报.canvas");

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(oldPath)!);
            CanvasStore.Write(oldPath, Board("uone", "one").ToJson());

            var ledger = new SyncLedger(SyncLayout.LedgerFolder(oldPath, root));
            var state = ledger.Load();

            state.DocToken = "old-doc-token";
            state.ManifestToken = "old-manifest-token";
            state.Rev = 7;
            state.PublishedSha = "published-under-the-old-name";
            state.BaseFile = "base-abc.canvas";
            ledger.Save(state);
            File.WriteAllText(Path.Combine(ledger.Folder, "base-abc.canvas"), "{}");

            Assert.Null(SyncLayout.RenameLedger(oldPath, newPath, root));

            var moved = new SyncLedger(SyncLayout.LedgerFolder(newPath, root)).Load();

            Assert.Null(moved.DocToken);
            Assert.Null(moved.ManifestToken);
            Assert.Equal(7, moved.Rev);
            Assert.Equal("published-under-the-old-name", moved.PublishedSha);
            Assert.Equal("base-abc.canvas", moved.BaseFile);
            Assert.Equal("周会", moved.PendingRenameFrom);
            Assert.Equal("项目周报", moved.PendingRenameTo);

            // ④ 的凭据：清掉的是"现在这两个路径归谁"，留下的是"旧名那两份当时是我们那份"。
            Assert.Equal("old-doc-token", moved.PendingRenameDocToken);
            Assert.Equal("old-manifest-token", moved.PendingRenameManifestToken);

            Assert.True(File.Exists(Path.Combine(SyncLayout.LedgerFolder(newPath, root), "base-abc.canvas")), "祖先必须跟着搬");
            Assert.False(Directory.Exists(SyncLayout.LedgerFolder(oldPath, root)));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Renaming_onto_an_existing_ledger_refuses_and_says_so()
    {
        var root = Path.Combine(Path.GetTempPath(), "outspread-rename-clash-" + Guid.NewGuid().ToString("N")[..8]);
        var oldPath = Path.Combine(root, "boards", "周会.canvas");
        var newPath = Path.Combine(root, "boards", "项目周报.canvas");

        try
        {
            Directory.CreateDirectory(SyncLayout.LedgerFolder(oldPath, root));
            Directory.CreateDirectory(SyncLayout.LedgerFolder(newPath, root));

            var problem = SyncLayout.RenameLedger(oldPath, newPath, root);

            Assert.NotNull(problem);
            Assert.Contains("已经在了", problem);
            Assert.True(Directory.Exists(SyncLayout.LedgerFolder(oldPath, root)), "撞名那一发一个字都不许搬");
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void A_board_that_never_reached_the_far_side_leaves_no_pending_marker()
    {
        var root = Path.Combine(Path.GetTempPath(), "outspread-rename-never-" + Guid.NewGuid().ToString("N")[..8]);
        var oldPath = Path.Combine(root, "boards", "周会.canvas");
        var newPath = Path.Combine(root, "boards", "项目周报.canvas");

        try
        {
            Directory.CreateDirectory(SyncLayout.LedgerFolder(oldPath, root));

            Assert.Null(SyncLayout.RenameLedger(oldPath, newPath, root));

            var moved = new SyncLedger(SyncLayout.LedgerFolder(newPath, root)).Load();

            // 仓里根本没有旧名那份，标记是纯噪音（契约第 7 条）。
            Assert.Null(moved.PendingRenameTo);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    /// <summary>
    /// 跟着别人的标记搬过来那一台：账本照搬、祖先照带，但**不欠仓里一条标记**（第 5 条的 ③④ 只属于
    /// 改名那台）。少了这一格，每一次跨端改名都会在仓里多出一条 <c>by</c> 指着没改名那台的记录——
    /// <c>by</c> 就不再等于"谁改的名"，而且每台多花一发请求（2026-10-23 真机排练里真看见了两条）。
    /// </summary>
    [Fact]
    public void Following_a_rename_moves_the_ledger_without_owing_a_marker()
    {
        var root = Path.Combine(Path.GetTempPath(), "outspread-rename-follow-" + Guid.NewGuid().ToString("N")[..8]);
        var oldPath = Path.Combine(root, "boards", "周会.canvas");
        var newPath = Path.Combine(root, "boards", "项目周报.canvas");

        try
        {
            Directory.CreateDirectory(SyncLayout.LedgerFolder(oldPath, root));

            var ledger = new SyncLedger(SyncLayout.LedgerFolder(oldPath, root));
            var state = ledger.Load();

            state.DocToken = "old-doc-token";
            state.PublishedSha = "published-under-the-old-name";
            ledger.Save(state);

            Assert.Null(SyncLayout.RenameLedger(oldPath, newPath, root, publishMarker: false));

            var moved = new SyncLedger(SyncLayout.LedgerFolder(newPath, root)).Load();

            Assert.Null(moved.PendingRenameTo);
            Assert.Null(moved.PendingRenameDocToken);
            Assert.Null(moved.DocToken);
            Assert.Equal("published-under-the-old-name", moved.PublishedSha);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    /// <summary>
    /// 第 5 条的 ④：标记落进仓以后，仓里旧名那两份（板与账本）跟着删掉。走**真实顺序**——
    /// 先正常同步一次（于是有祖先、有旧路径的令牌），再搬文件＋搬账本，再同步。
    /// 同伴从此看得见"旧名那份不见了"，也就看得见那条标记。
    /// </summary>
    [Fact]
    public async Task A_rename_reaps_the_old_copy_once_the_marker_is_in()
    {
        using var scratch = new Scratch("rename-reap");
        var root = Path.Combine(scratch.Folder, "sync");
        var oldPath = Path.Combine(scratch.Folder, "周会.canvas");
        var newPath = Path.Combine(scratch.Folder, "项目周报.canvas");

        CanvasStore.Write(oldPath, Board("uone", "one").ToJson());

        var far = new FakeRemote();
        var deletes = new List<string>();

        far.OnDelete = deletes.Add;

        await new SyncEngine(far, new SyncLedger(SyncLayout.LedgerFolder(oldPath, root)), oldPath).SynchronizeAsync();

        Assert.NotNull(far.Text(OldDoc));

        File.Move(oldPath, newPath);

        Assert.Null(SyncLayout.RenameLedger(oldPath, newPath, root));

        await new SyncEngine(far, new SyncLedger(SyncLayout.LedgerFolder(newPath, root)), newPath).SynchronizeAsync();

        Assert.NotNull(far.Text(NewDoc));
        Assert.Equal(new[] { OldDoc, "boards/.sync/周会.manifest.json" }, deletes);
        Assert.Null(far.Text(OldDoc));
        Assert.Null(far.Text("boards/.sync/周会.manifest.json"));
        Assert.Equal("周会", Assert.Single(RenameLog.Parse(far.Text(RenameLog.RepoPath)!)!.Entries).From);
        Assert.Null(new SyncLedger(SyncLayout.LedgerFolder(newPath, root)).Load().PendingRenameTo);
    }

    /// <summary>
    /// ④ 没删成 ⇒ 待发留着、下一轮**只补删**：不再往仓里写第二份同一条标记（那是一枚空提交），
    /// 也不许把旧名那份当成没删过。
    /// </summary>
    [Fact]
    public async Task A_reap_the_far_side_refused_asks_again_without_writing_the_marker_twice()
    {
        using var scratch = new Scratch("rename-reap-refused");
        var root = Path.Combine(scratch.Folder, "sync");
        var oldPath = Path.Combine(scratch.Folder, "周会.canvas");
        var newPath = Path.Combine(scratch.Folder, "项目周报.canvas");

        CanvasStore.Write(oldPath, Board("uone", "one").ToJson());

        var far = new FakeRemote();

        await new SyncEngine(far, new SyncLedger(SyncLayout.LedgerFolder(oldPath, root)), oldPath).SynchronizeAsync();

        File.Move(oldPath, newPath);

        Assert.Null(SyncLayout.RenameLedger(oldPath, newPath, root));

        var ledger = new SyncLedger(SyncLayout.LedgerFolder(newPath, root));

        far.FailNextDelete();

        var written = new List<string>();

        far.OnWrite = written.Add;

        await new SyncEngine(far, ledger, newPath).SynchronizeAsync();

        var held = ledger.Load();

        Assert.NotNull(far.Text(OldDoc));
        Assert.Equal("周会", held.PendingRenameFrom);
        Assert.NotNull(held.LastError);
        Assert.Single(RenameLog.Parse(far.Text(RenameLog.RepoPath)!)!.Entries);

        written.Clear();

        await new SyncEngine(far, ledger, newPath).SynchronizeAsync();

        Assert.Null(far.Text(OldDoc));
        Assert.Null(ledger.Load().PendingRenameFrom);

        // 补的那一发只欠删：把同一条标记再写一遍是一枚空提交（内容一模一样，`Append` 会去重，
        // 所以光看仓里那条数抓不住它——要看这一发到底有没有又写了一次）。
        Assert.DoesNotContain(RenameLog.RepoPath, written);
    }

    /// <summary>
    /// 旧名字被人占走了：删的条件是**改名当时**那份的令牌，不是"看着像我们的路径"。
    /// 条件不过就不删（第 5 条），这一发宁可仓里多留一份没人认领的板。
    /// </summary>
    [Fact]
    public async Task A_reap_never_deletes_a_file_the_far_side_moved_under_us()
    {
        using var scratch = new Scratch("rename-reap-rival");
        var root = Path.Combine(scratch.Folder, "sync");
        var oldPath = Path.Combine(scratch.Folder, "周会.canvas");
        var newPath = Path.Combine(scratch.Folder, "项目周报.canvas");

        CanvasStore.Write(oldPath, Board("uone", "one").ToJson());

        var far = new FakeRemote();

        await new SyncEngine(far, new SyncLedger(SyncLayout.LedgerFolder(oldPath, root)), oldPath).SynchronizeAsync();

        File.Move(oldPath, newPath);

        Assert.Null(SyncLayout.RenameLedger(oldPath, newPath, root));

        var ledger = new SyncLedger(SyncLayout.LedgerFolder(newPath, root));

        // 有人在旧名字上重新写了一块板：那份已经不是我们的了。
        far.Publish(OldDoc, Board("urival", "别人在旧名字上建的板").ToJson());

        await new SyncEngine(far, ledger, newPath).SynchronizeAsync();

        Assert.Contains("urival", far.Text(OldDoc));
        Assert.Equal("周会", ledger.Load().PendingRenameFrom);
    }

    private static string Encoding(RenameLog log)
        => System.Text.Encoding.UTF8.GetString(RenameLog.ToBytes(log));
}
