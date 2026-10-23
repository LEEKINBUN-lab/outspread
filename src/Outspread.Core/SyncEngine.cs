using System.Text;

namespace Outspread.Core;

public enum SyncOutcome
{
    /// <summary>Neither side moved since this device last looked, and no file was read to find that out.</summary>
    NothingToDo,

    /// <summary>What this device holds is now what the far side holds.</summary>
    Pushed,

    /// <summary>The far side's state came into this device's file; there was nothing of ours to send.</summary>
    Pulled,

    /// <summary>Compare-and-set refused every round. Nothing on this disk was changed.</summary>
    GaveUpConflict,

    /// <summary>The board file on this disk changed while the round was in flight, so it was not overwritten.</summary>
    DiskMovedUnderneath,

    /// <summary>Could not reach the far side.</summary>
    Offline,

    /// <summary>The account cannot write there. Only a human with a token fixes this.</summary>
    Unauthorized,

    /// <summary>Rate limited or transiently refused. The same round may succeed later.</summary>
    RateLimited,

    /// <summary>
    /// The far side refused this content for shape or size. Retrying the same bytes will never
    /// work, which is why it is not reported as a limit: a limit empties itself, a refusal does not.
    /// </summary>
    RemoteRejected,

    /// <summary>The repository or branch is gone. This file is the only copy that exists.</summary>
    RemoteGone,

    /// <summary>The far document is not readable as a board. Sync stopped; this disk was not touched.</summary>
    RemoteUnreadable,

    /// <summary>This device's own board file is not readable. Nothing was sent.</summary>
    LocalUnreadable,

    /// <summary>
    /// 这块板在别的设备上被改了名：远端旧名那份不在了，改名标记指着一个还在仓里的新名字。
    /// 本轮**一个字节都没动本地**（这是这条存在的意义：把"改名"和"远端删了"分开），
    /// 搬本地文件与账本是宿主（界面）的活，它做完会按新名字再来一轮。
    /// </summary>
    FollowedRename,
}

/// <summary>
/// One round of getting this board and the far copy back into agreement.
/// </summary>
/// <remarks>
/// The engine never edits a board. It reads what is on disk, reads what is far away,
/// reconciles the two against the ancestor this device kept, and publishes the result
/// through the same locked atomic write the board itself uses.
///
/// Every refusal is reported as a sentence someone can act on, including the round that
/// did nothing: an honest "nothing moved" is what keeps "it says synced" from meaning
/// "something gave up quietly".
/// </remarks>
public sealed record SyncReport(
    SyncOutcome Outcome,
    string Detail,
    int Rev,
    int PendingNodes,
    int Rounds,
    IReadOnlyList<MergeVerdict> Verdicts,
    string? ConflictCopy = null,
    AssetReport? Assets = null,

    /** 只在 <see cref="SyncOutcome.FollowedRename"/> 那一格有值：改名标记指着的新板名。 */
    string? RenameTo = null)
{
    /// <summary>
    /// The one line a status bar shows. Readings, not adjectives: "synced" is not a
    /// reading, "r12 · 3 waiting" is.
    /// </summary>
    public string StatusLine(string? remoteDigest = null)
    {
        var head = Outcome switch
        {
            SyncOutcome.NothingToDo => "同步",
            SyncOutcome.Pushed => "已上传",
            SyncOutcome.Pulled => "已并入远端",
            SyncOutcome.GaveUpConflict => "未同步：远端一直在变",
            SyncOutcome.DiskMovedUnderneath => "远端已前进，本地待下轮并入",
            SyncOutcome.Offline => "未同步：离线",
            SyncOutcome.Unauthorized => "同步已停：需要重新登录",
            SyncOutcome.RateLimited => "未同步：远端限流",
            SyncOutcome.RemoteRejected => "远端不收这份：形状或大小",
            SyncOutcome.RemoteGone => "远端不见了：本地是唯一副本",
            SyncOutcome.RemoteUnreadable => "远端那份读不懂：已停用同步",
            SyncOutcome.LocalUnreadable => "未同步：本地文件不可读",
            SyncOutcome.FollowedRename => "这块板被改了名，本地未动",
            _ => "同步",
        };

        var digest = string.IsNullOrEmpty(remoteDigest)
            ? string.Empty
            : $" · 远端 {remoteDigest[..Math.Min(6, remoteDigest.Length)]}";

        return $"{head} r{Rev}{digest} · 待发 {PendingNodes}";
    }
}

public sealed class SyncEngine
{
    /// <summary>
    /// Two devices pushing at each other cannot need three rounds. Needing three means a
    /// third writer, and that is worth stopping in front of rather than out-arguing.
    /// </summary>
    private const int MaxRounds = 3;

    private readonly IRemoteFiles _remote;
    private readonly SyncLedger _ledger;
    private readonly SyncPaths _paths;
    private readonly string _boardPath;
    private readonly Action<string>? _trace;

    public SyncEngine(
        IRemoteFiles remote,
        SyncLedger ledger,
        string boardPath,
        Action<string>? trace = null)
    {
        _remote = remote;
        _ledger = ledger;
        _boardPath = boardPath;
        _paths = SyncPaths.ForBoard(boardPath);
        _trace = trace;
    }

    public async Task<SyncReport> SynchronizeAsync(CancellationToken cancel = default)
    {
        var state = _ledger.Load();

        if (!File.Exists(_boardPath))
        {
            return Finish(state, new SyncReport(
                SyncOutcome.LocalUnreadable,
                $"board file is missing: {_boardPath}",
                state.Rev,
                0,
                0,
                []));
        }

        try
        {
            var report = await RoundAsync(state, cancel);

            // 改名标记是这一轮**发完自己的板之后**才补的那一发（契约第 5 条的 ③）：新名那份没上去
            // 之前先把标记写进去，同伴会跟到一个仓里还不存在的名字上。发不上去就留着 `pendingRename`
            // 下一轮再补——本机改名已经成了，不因此回滚（第 6 条）。
            if (report.Outcome is SyncOutcome.Pushed or SyncOutcome.Pulled or SyncOutcome.NothingToDo
                && !string.IsNullOrEmpty(state.PendingRenameTo))
            {
                await FinishRenameAsync(state, cancel);
            }

            return report;
        }
        catch (Exception e) when (e is HttpRequestException or IOException or TaskCanceledException)
        {
            // A transport failure is not a merge result. The ledger keeps pointing at the
            // ancestor it already had, so the next round merges against the same thing
            // instead of against whatever arrived half-way.
            state.LastError = e.Message;
            state.LastErrorKind = nameof(SyncOutcome.Offline);
            _ledger.Save(state);

            Note($"离线，本地与远端都没动：{e.Message}");

            return new SyncReport(SyncOutcome.Offline, e.Message, state.Rev, PendingNow(), 0, []);
        }
    }

    /// <summary>
    /// How much this device has not managed to send. Read off the ancestor rather than
    /// remembered, so a status line can say "3 waiting" without the engine having had to
    /// succeed at anything first.
    /// </summary>
    private int PendingNow()
    {
        var ancestor = _ledger.ReadBase(_ledger.Load());

        if (ancestor is null || !File.Exists(_boardPath))
        {
            return 0;
        }

        try
        {
            return DocDiff.Between(
                ancestor,
                CanvasDocument.Parse(CanvasStore.ReadAllText(_boardPath), _boardPath)).NodeCount;
        }
        catch (CanvasDocumentException e)
        {
            Note($"本地文件读取失败，待发数量按 0 报：{e.Message}");

            return 0;
        }
    }

    private async Task<SyncReport> RoundAsync(SyncState state, CancellationToken cancel)
    {
        var tree = await _remote.ReadTreeAsync(cancel);

        if (tree is null)
        {
            Note("远端仓库或分支不在了，本地一个字没动");

            return Finish(state, new SyncReport(
                SyncOutcome.RemoteGone,
                "the far repository or branch is gone; this file is the only copy",
                state.Rev,
                0,
                0,
                []));
        }

        var docToken = tree.TokenFor(_paths.DocPath);
        var manifestToken = tree.TokenFor(_paths.ManifestPath);

        string localText;
        CanvasDocument localDoc;

        try
        {
            localText = CanvasStore.ReadAllText(_boardPath);
            localDoc = CanvasDocument.Parse(localText, _boardPath);
        }
        catch (Exception e) when (e is CanvasDocumentException or IOException or UnauthorizedAccessException)
        {
            Note($"本地文件读取失败，本轮上传跳过：{e.Message}");

            return Finish(state, new SyncReport(
                SyncOutcome.LocalUnreadable,
                e.Message,
                state.Rev,
                0,
                0,
                []));
        }

        var digest = Ledger.Digest(Ledger.Encode(localText));

        // Both tokens come from the single tree call, so proving nothing moved cost one
        // request and zero file reads. That is what lets the interval be short without
        // spending quota to sit still.
        // `docToken == state.DocToken` 在**两边都是 null** 时会成立，而"都是 null"意思是"这个路径下
        // 根本没有文件"——不是"没人动过"。改名以后新名字那份就是这一格（2026-10-23 真机排练抓到：
        // 标记发出去了、新名那份一直没上传，同伴跟过去会落在一个不存在的名字上）。所以空转的判据
        // 多一条：这一台确实往这个路径发过东西。
        if (docToken == state.DocToken && docToken is not null
            && manifestToken == state.ManifestToken && digest == state.PublishedSha)
        {
            // Idle is not the same as carried: the board can have been up there since before
            // this asset layer existed, and the picture it points at must still catch up. The
            // cost of asking is zero — a path already in the tree is never read or written.
            var idleAssets = await CarryAssetsAsync(localDoc, tree, cancel);

            // A deletion this device started and could not finish is still work, so an
            // otherwise idle round goes back for it. With nothing pending this costs no
            // request: the candidates are checked against the tree already in hand.
            await ReapAsync(localDoc, state.PendingDeletions, tree, idleAssets, state, cancel);

            return new SyncReport(SyncOutcome.NothingToDo, "neither side moved", state.Rev, 0, 0, [], null, idleAssets);
        }

        var ancestor = _ledger.ReadBase(state);
        // 同一个洞：两个 null 不等于"远端还是我们写的那份"。
        var farStillOurs = docToken == state.DocToken && docToken is not null && ancestor is not null;

        CanvasDocument far;
        SyncManifest? manifest;
        List<MergeVerdict> verdicts;
        CanvasDocument merged;
        var pending = ancestor is null ? 0 : DocDiff.Between(ancestor, localDoc).NodeCount;

        if (farStillOurs)
        {
            // The far file still carries the token we wrote it with, so its bytes are the
            // bytes kept as the ancestor. Reading them again costs a request to confirm
            // something already known.
            far = ancestor!;
            manifest = null;
            merged = localDoc;
            verdicts = [];
        }
        else
        {
            var read = await ReadFarAsync(state, localDoc, cancel);

            if (read.RenamedTo is not null)
            {
                // 契约第 1、2 条要拦的就是这一发：这块板在别的设备上改了名，本地一个字节都不许动。
                // 没有这条的话，安卓会拿空文档去合并（把整块板清空再推回去），桌面会把它当
                // "第一次上传"重新发一份。搬文件与账本是宿主的活，它搬完会按新名字再来一轮。
                Note($"远端把这块板改名为 {read.RenamedTo}：本地未动，等宿主搬完再来一轮");

                return Finish(state, new SyncReport(
                    SyncOutcome.FollowedRename,
                    read.RenamedTo,
                    state.Rev,
                    pending,
                    0,
                    [],
                    RenameTo: read.RenamedTo));
            }

            if (read.Unreadable)
            {
                return Finish(state, new SyncReport(
                    SyncOutcome.RemoteUnreadable,
                    read.Detail ?? "the far document is not readable as a board",
                    state.Rev,
                    pending,
                    0,
                    []));
            }

            far = read.Doc;
            manifest = read.Manifest;

            // 远端**这个路径下没有文件**不等于"远端把每一格都删了"：那是"远端对这条路没意见"。
            // 不补这一句，改名以后的第一发会拿搬过来的祖先去比一份空的远端，每一格都被判成
            // "另一端删了"，于是把本地清空再推上去（2026-10-23 真机排练抓到，排练乙 1 格 → 0 格）。
            // 判据退回"首轮没有祖先"那一条：拿远端当基线，本地有的格子就是本地新增。
            if (read.FarMissing)
            {
                ancestor = null;
            }
            docToken = read.DocToken ?? docToken;

            var merge = Merge3.Between(
                ancestor ?? CanvasDocument.CreateNew(),
                localDoc,
                far,
                new MergeSide(state.DeviceId, state.Clock),
                new MergeSide(manifest?.By ?? "far", manifest?.Clock ?? 0));

            merged = merge.Merged;
            verdicts = merge.Verdicts;
        }

        foreach (var verdict in verdicts)
        {
            Note($"{verdict.Rule} · {verdict.Id} · {verdict.Field}：{verdict.Detail}");
        }

        var delta = DocDiff.Between(far, merged);

        // What this board stopped pointing at since its own ancestor, plus what the far
        // ledger already remembers. Only this device can notice its own deletion, and only
        // the ledger can tell the other ends afterwards — except for a deletion this device
        // started and could not finish, which waits in its own account, because a round that
        // finds the far side exactly where it left it never reads that ledger.
        var dropped = AssetSync.Tombstones(
            ancestor, merged, manifest?.DroppedAssets ?? state.PendingDeletions);

        var assets = await CarryAssetsAsync(merged, tree, cancel);

        var (round, published, toReap) = delta.IsEmpty
            ? (await PullOnlyAsync(state, localText, merged, manifest, verdicts, cancel), merged, dropped)
            : await PushAsync(
                state, localDoc, localText, merged, manifest, verdicts,
                docToken, manifestToken, pending, delta.NodeCount, dropped, cancel);

        // Reaping waits for the board to be accepted, and works from what that accepted round
        // finally merged: a retry inside the publish can revive a note along with its picture,
        // and a tombstone must not fire against the board that is now up there.
        //
        // The ledger remembers a deletion until newer ones push it out, so the list stays
        // non-empty long after the file is gone. Only paths that are actually still somewhere
        // reach the far side: an idle round must not spend a second request because a round
        // from last week deleted a picture.
        // Reaping waits for the board to be accepted, and works from what that accepted round
        // finally merged: a retry inside the publish can revive a note along with its picture,
        // and a tombstone must not fire against the board that is now up there.
        if (round.Outcome is SyncOutcome.Pushed or SyncOutcome.Pulled)
        {
            await ReapAsync(published, toReap, tree, assets, state, cancel);
        }

        // Reported at the one place every non-idle path leaves through, rather than inside the
        // two branches: a round that carried pictures and then refused to publish the board
        // still has to say which pictures it moved.
        return assets.DidAnything ? round with { Assets = assets } : round;
    }

    /// <summary>
    /// Publishes the merged board. Comes back with what it finally put up there, because a
    /// refused attempt re-merges, and whoever reaps pictures afterwards has to work from the
    /// board that was accepted rather than the one first proposed.
    /// </summary>
    private async Task<(SyncReport Report, CanvasDocument Merged, IReadOnlyList<string> Dropped)> PushAsync(
        SyncState state,
        CanvasDocument localDoc,
        string localText,
        CanvasDocument merged,
        SyncManifest? manifest,
        List<MergeVerdict> verdicts,
        string? docToken,
        string? manifestToken,
        int pending,
        int pushed,
        IReadOnlyList<string> dropped,
        CancellationToken cancel)
    {
        var text = merged.ToJson();
        var bytes = Ledger.Encode(text);
        var accepted = false;
        var rounds = 0;

        for (var attempt = 1; attempt <= MaxRounds; attempt++)
        {
            rounds = attempt;

            var written = await _remote.WriteAsync(_paths.DocPath, bytes, docToken, cancel);

            if (written.Outcome == WriteOutcome.Written)
            {
                docToken = written.Token;

                // The ledger entry is a different file: writing this board does not invalidate
                // its token, and dropping the token we read turns the next write into a "create"
                // that the platform then refuses — which is how a round ended up reporting a
                // board that was uploaded but an account that was not.
                accepted = true;
                break;
            }

            if (written.Outcome != WriteOutcome.Conflict)
            {
                var (outcome, detail) = Describe(written.Outcome, written.Detail);

                return (
                    Finish(state, new SyncReport(outcome, detail, state.Rev, pending, rounds, verdicts)),
                    merged,
                    dropped);
            }

            // The far side is not what we merged against. Take its version of the truth and
            // reconcile again, with the same ancestor — that is the only way a refusal
            // becomes progress instead of a retry loop over identical bytes.
            docToken = written.Token;

            Note($"第 {attempt} 发被远端挡回，它已不是我们合并时那份；重新合并");

            var read = await ReadFarAsync(state, localDoc, cancel);

            if (read.Unreadable)
            {
                return (
                    Finish(state, new SyncReport(
                        SyncOutcome.RemoteUnreadable,
                        read.Detail ?? "the far document is not readable as a board",
                        state.Rev,
                        pending,
                        rounds,
                        verdicts)),
                    merged,
                    dropped);
            }

            var ancestor = _ledger.ReadBase(state) ?? CanvasDocument.CreateNew();

            var merge = Merge3.Between(
                ancestor,
                localDoc,
                read.Doc,
                new MergeSide(state.DeviceId, state.Clock),
                new MergeSide(read.Manifest?.By ?? "far", read.Manifest?.Clock ?? 0));

            merged = merge.Merged;
            verdicts = merge.Verdicts;
            manifest = read.Manifest;
            docToken = read.DocToken ?? docToken;
            manifestToken = read.ManifestToken;
            text = merged.ToJson();
            bytes = Ledger.Encode(text);

            // Re-derived against what this retry actually merged, not what the first attempt
            // proposed: the far side may have brought a note back, and with it its picture.
            dropped = AssetSync.Tombstones(ancestor, merged, read.Manifest?.DroppedAssets);

            if (DocDiff.Between(read.Doc, merged).IsEmpty)
            {
                // What this end wanted to say is already on the far side. Nothing to push;
                // the ancestor and the local file still have to be brought in line.
                return (await PullOnlyAsync(state, localText, merged, manifest, verdicts, cancel), merged, dropped);
            }
        }

        if (!accepted)
        {
            // Gave up. This disk keeps every byte it had, and the state this device wanted
            // to publish is written out whole beside it rather than dropped.
            var copy = ConflictCopyPath(state.Rev);
            File.WriteAllText(copy, text, new UTF8Encoding(false));

            state.LastError = "the far side kept changing under us";
            state.LastErrorKind = nameof(SyncOutcome.GaveUpConflict);
            _ledger.Save(state);

            Note($"挤了 {rounds} 轮都没进去，本地未动；要上传的那份整份留在 {Path.GetFileName(copy)}");

            return (
                new SyncReport(
                    SyncOutcome.GaveUpConflict,
                    "the far side kept changing under us; this file was not modified and the intended state was written out beside it",
                    state.Rev,
                    pending,
                    rounds,
                    verdicts,
                    copy),
                merged,
                []);
        }

        var clock = Math.Max(state.Clock, manifest?.Clock ?? 0) + 1;
        var rev = Math.Max(manifest?.Rev ?? 0, state.Rev) + 1;

        var next = new SyncManifest
        {
            Schema = Ledger.Schema,
            Rev = rev,
            ContentSha = Ledger.Digest(bytes),
            By = state.DeviceId,
            Clock = clock,
            At = DateTimeOffset.UtcNow.ToString("O"),
            DroppedAssets = [.. dropped],
        };

        var ledgerWritten = await WriteLedgerEntryAsync(next, manifestToken, cancel);

        // A refusal for the ledger entry is worth one more attempt only when it was a token
        // mismatch, and then only with the token read fresh: a refusal response does not
        // carry the current token, so reusing whatever we already had would ask the same
        // question twice. An account that cannot write, or a body the platform will not
        // accept, is terminal and is reported as such.
        if (ledgerWritten.Outcome == WriteOutcome.Conflict)
        {
            var fresh = await _remote.ReadTreeAsync(cancel);

            ledgerWritten = await WriteLedgerEntryAsync(next, fresh?.TokenFor(_paths.ManifestPath), cancel);
        }

        state.Rev = rev;
        state.Clock = clock;
        state.DocToken = docToken;
        state.ManifestToken = ledgerWritten.Outcome == WriteOutcome.Written ? ledgerWritten.Token : null;
        state.ContentSha = Ledger.Digest(bytes);
        state.PublishedSha = Ledger.Digest(bytes);
        state.BaseFile = _ledger.KeepBase(merged);
        state.LastError = ledgerWritten.Outcome == WriteOutcome.Written
            ? null
            : "the board reached the far side but its ledger entry did not";
        state.LastErrorKind = ledgerWritten.Outcome == WriteOutcome.Written ? null : "ledger";
        _ledger.Save(state);

        if (state.ManifestToken is null)
        {
            Note("板子上去了，账本没上去；下一轮会补记，期间远端看到的时钟比实际旧一发");
        }

        if (!PublishIfUnchanged(localText, text))
        {
            Note("板子上去了，本地在这期间又被改了，所以没有覆盖它；下一轮并回来");

            return (
                Finish(state, new SyncReport(
                    SyncOutcome.DiskMovedUnderneath,
                    "this file changed while the round was in flight; it was not overwritten and will be merged next round",
                    state.Rev,
                    pending,
                    rounds,
                    verdicts)),
                merged,
                dropped);
        }

        return (
            new SyncReport(
                SyncOutcome.Pushed,
                $"{pushed} 处改动上去了",
                state.Rev,
                0,
                rounds,
                verdicts),
            merged,
            dropped);
    }

    /// <summary>
    /// Pictures first, board second, and on every path that touches the far side — including a
    /// round with nothing to say about the board itself.
    /// </summary>
    private async Task<AssetReport> CarryAssetsAsync(CanvasDocument document, RemoteTree tree, CancellationToken cancel)
    {
        var pulled = await AssetSync.PullAsync(_remote, _boardPath, document, tree, _paths, cancel);

        pulled.Absorb(await AssetSync.PushAsync(_remote, _boardPath, document, tree, _paths, cancel));

        return pulled;
    }

    /// <summary>
    /// Takes away the pictures the board no longer names, and remembers the ones this round
    /// could not finish so a later round tries them.
    /// </summary>
    /// <remarks>
    /// The ledger keeps a deletion until newer ones push it out, so a candidate list is rarely
    /// empty even long after the file is gone. Only paths that still exist somewhere reach the
    /// far side: the tree already in hand answers that for free, which is what keeps a steady
    /// round at one request.
    /// </remarks>
    private async Task ReapAsync(
        CanvasDocument published,
        IReadOnlyList<string> candidates,
        RemoteTree tree,
        AssetReport assets,
        SyncState state,
        CancellationToken cancel)
    {
        var prefix = AssetSync.RepoPrefix(_paths);

        var live = candidates
            .Where(relative => tree.TokenFor(prefix + relative) is not null
                || AssetStore.ResolveExisting(_boardPath, relative) is not null)
            .ToList();

        var retry = live;

        if (live.Count > 0)
        {
            // Read fresh before deleting: a board another device published while this round
            // ran may be the one still pointing at the picture, and a deletion is the one
            // thing here the next round cannot undo.
            var now = await _remote.ReadTreeAsync(cancel);

            if (now is not null)
            {
                var reaped = await AssetSync.ReapAsync(
                    _remote, _boardPath, published, now, _paths, live, cancel);

                assets.Absorb(reaped);
                retry = reaped.Retry;
            }
        }

        if (!retry.SequenceEqual(state.PendingDeletions))
        {
            state.PendingDeletions = [.. retry];
            _ledger.Save(state);
        }
    }

    private async Task<SyncReport> PullOnlyAsync(
        SyncState state,
        string localText,
        CanvasDocument merged,
        SyncManifest? manifest,
        List<MergeVerdict> verdicts,
        CancellationToken cancel)
    {
        var text = merged.ToJson();
        var bytes = Ledger.Encode(text);

        if (!PublishIfUnchanged(localText, text))
        {
            return Finish(state, new SyncReport(
                SyncOutcome.DiskMovedUnderneath,
                "this file changed while the far copy was being read; it was not overwritten",
                state.Rev,
                PendingNow(),
                1,
                verdicts));
        }

        var tree = await _remote.ReadTreeAsync(cancel);

        state.DocToken = tree?.TokenFor(_paths.DocPath);
        state.ManifestToken = tree?.TokenFor(_paths.ManifestPath);
        state.ContentSha = Ledger.Digest(bytes);
        state.PublishedSha = Ledger.Digest(bytes);
        state.BaseFile = _ledger.KeepBase(merged);
        state.Rev = manifest?.Rev ?? state.Rev;
        state.LastError = null;
        state.LastErrorKind = null;
        _ledger.Save(state);

        return new SyncReport(SyncOutcome.Pulled, "远端那份已并入本地", state.Rev, 0, 1, verdicts);
    }

    /// <summary>
    /// Reads the far board and its ledger entry, and re-merges against this device's own
    /// ancestor. A far document that cannot be parsed comes back flagged rather than thrown,
    /// because the answer to a half-written remote file is to stop, not to overwrite it.
    /// </summary>
    private async Task<FarState> ReadFarAsync(SyncState state, CanvasDocument localDoc, CancellationToken cancel)
    {
        var file = await _remote.ReadAsync(_paths.DocPath, cancel);

        if (file is null)
        {
            var tree = await _remote.ReadTreeAsync(cancel);

            // 契约第 1 条：只有"我确实跟这块板同步过"才去查改名标记（多发一发读，只在这一格）。
            // 从没上过仓的板走的是老路——这一份就是第一次上传。
            var renamed = await FollowRenameAsync(state, tree, cancel);

            if (renamed is not null)
            {
                return new FarState(CanvasDocument.CreateNew(), RenamedTo: renamed);
            }

            Note("远端还没有这块板，这一份是第一次上传");

            return new FarState(
                CanvasDocument.CreateNew(),
                tree?.TokenFor(_paths.DocPath),
                null,
                tree?.TokenFor(_paths.ManifestPath),
                Unreadable: false,
                Detail: null,
                FarMissing: true);
        }

        CanvasDocument far;

        try
        {
            far = CanvasDocument.Parse(Encoding.UTF8.GetString(file.Content), _paths.DocPath);
        }
        catch (CanvasDocumentException e)
        {
            state.LastError = e.Message;
            state.LastErrorKind = nameof(SyncOutcome.RemoteUnreadable);
            _ledger.Save(state);

            Note($"远端那份读不懂，已停用同步，本地未动：{e.Message}");

            return new FarState(Doc: null!, Unreadable: true, Detail: e.Message);
        }

        var manifestFile = await _remote.ReadAsync(_paths.ManifestPath, cancel);

        var manifest = manifestFile is null
            ? null
            : SyncManifest.Parse(Encoding.UTF8.GetString(manifestFile.Content));

        return new FarState(far, file.Token, manifest, manifestFile?.Token, Unreadable: false, Detail: null);
    }

    /// <summary>
    /// Writes the reconciled board only if the file still holds the bytes it held when the
    /// round read it. The named mutex covers this program's own writers and nothing else, so
    /// the check runs inside it; a refused publish rewrites the same bytes it was given.
    /// </summary>
    private bool PublishIfUnchanged(string expectedText, string text)
    {
        var expected = Ledger.Digest(Ledger.Encode(expectedText));
        var stillOurs = false;

        CanvasStore.Transact(_boardPath, current =>
        {
            stillOurs = Ledger.Digest(Ledger.Encode(current)) == expected;

            return stillOurs ? text : current;
        });

        return stillOurs;
    }

    private async Task<WriteResult> WriteLedgerEntryAsync(SyncManifest manifest, string? expectToken, CancellationToken cancel)
        => await _remote.WriteAsync(_paths.ManifestPath, SyncManifest.ToBytes(manifest), expectToken, cancel);

    /// <summary>
    /// The copy lands next to the board but with an extension that is not `.canvas`, because
    /// a name like `scratch.conflict-r2.canvas` would be picked up by the board locator's
    /// "first .canvas in the folder" rule the moment the real board went missing — a refusal
    /// to sync must never be able to become the board.
    /// </summary>
    /// <remarks>
    /// The name also carries the machine and the minute, because a conflict copy with neither is
    /// a dead file: two devices syncing the same board produce copies seconds apart, and "which one
    /// came from the laptop" has to be readable off the filename in Explorer, not guessed from a
    /// modification time. Same reason Obsidian names its conflicted copies.
    /// </remarks>
    private string ConflictCopyPath(int rev)
    {
        var directory = AssetStore.BoardDirectory(_boardPath);
        var name = Path.GetFileName(_boardPath);

        return Path.Combine(
            directory,
            $"{name}.conflict-{DeviceTag()}-{DateTime.Now:yyyyMMdd-HHmm}-r{rev + 1}");
    }

    /// <summary>机器名要能当文件名用：只留字母数字与 <c>-</c> <c>_</c>，其余换成 <c>-</c>，并截到 20 个字符。</summary>
    private static string DeviceTag()
    {
        var raw = Environment.MachineName;
        var kept = new char[Math.Min(raw.Length, 20)];
        var written = 0;

        foreach (var character in raw)
        {
            if (written == kept.Length)
            {
                break;
            }

            kept[written++] = char.IsLetterOrDigit(character) || character is '-' or '_' ? character : '-';
        }

        return written == 0 ? "device" : new string(kept, 0, written);
    }

    private SyncReport Finish(SyncState state, SyncReport report)
    {
        if (report.Outcome != SyncOutcome.NothingToDo)
        {
            state.LastError = report.Detail;
            state.LastErrorKind = report.Outcome.ToString();
            _ledger.Save(state);
        }

        return report;
    }

    private static (SyncOutcome, string) Describe(WriteOutcome outcome, string? detail)
        => outcome switch
        {
            WriteOutcome.Unauthorized => (SyncOutcome.Unauthorized, detail ?? "the account cannot write there"),
            WriteOutcome.RetryLater => (SyncOutcome.RateLimited, detail ?? "refused for now"),
            WriteOutcome.Rejected => (SyncOutcome.RemoteRejected, detail ?? "the far side refused this content"),
            WriteOutcome.Missing => (SyncOutcome.RemoteGone, detail ?? "the far path is not there"),
            _ => (SyncOutcome.Offline, detail ?? "the far side refused the write"),
        };

    private void Note(string message) => _trace?.Invoke(message);

    private sealed record FarState(
        CanvasDocument Doc,
        string? DocToken = null,
        SyncManifest? Manifest = null,
        string? ManifestToken = null,
        bool Unreadable = false,
        string? Detail = null,

        /// <summary>远端这个路径下根本没有文件（"没意见"，不是"删光了"）。</summary>
        bool FarMissing = false,

        /// <summary>改名标记指着的新板名（非空 ⇒ 本轮必须原地停下，见 <see cref="SyncOutcome.FollowedRename"/>）。</summary>
        string? RenamedTo = null);

    /// <summary>
    /// 我这块板的远端那份不见了：是被删了，还是被改了名？只有改名标记能分这一格。
    /// 答 null 的四种情形都退回今天已知的路（把"不见了"当删除）：从没同步过这块板、仓里没有这份
    /// 日志、读不懂它、链没解出一个**还在仓里**的名字（契约第 2 条）。
    /// </summary>
    private async Task<string?> FollowRenameAsync(SyncState state, RemoteTree? tree, CancellationToken cancel)
    {
        if (tree is null || (state.PublishedSha is null && state.BaseFile is null))
        {
            return null;
        }

        var marker = await _remote.ReadAsync(RenameLog.RepoPath, cancel);

        if (marker is null)
        {
            return null;
        }

        var log = RenameLog.Parse(Encoding.UTF8.GetString(marker.Content), out var problem);

        if (log is null || problem is not null)
        {
            Note($"改名标记读不懂，这一轮按\u201c远端删了\u201d走老路：{problem}");

            return null;
        }

        var stem = SyncPaths.BoardStem(_boardPath);

        var target = log.Follow(stem, name => tree.TokenFor(SyncPaths.DocPathFor(name)) is not null);

        return target is not null && target != stem ? target : null;
    }

    /// <summary>
    /// 一条本机改名的收尾（契约第 5 条的 ③→④）：先把标记追加进仓，再把仓里旧名那两份删掉。
    /// <para>
    /// 次序是硬要求：③ 没落定之前不许做 ④，否则同伴看到的是"旧文件没了"而没有标记可查，那一发
    /// 就成了一次真的删除。两样都落了才清 pending——任何一样没成，下一轮开头再补，而补的时候
    /// ③ 认得自己那条已经在仓里了（不重写第二份），④ 只删还没删成的那一份。
    /// </para>
    /// <para>
    /// 发不上去**不**改本轮的结论、也不回滚本机改名（第 6 条）：留着 <c>pendingRename</c>、把原因
    /// 记进账本——与 <c>pendingDeletions</c> 同一形状。读不懂现有那份标记时**不覆盖**：那里面可能
    /// 有条别人的改名，用我这一条去顶掉它是不可逆的。
    /// </para>
    /// </summary>
    private async Task FinishRenameAsync(SyncState state, CancellationToken cancel)
    {
        var from = state.PendingRenameFrom!;
        var to = state.PendingRenameTo!;

        if (!await MarkerIsInAsync(state, from, to, cancel))
        {
            return;
        }

        if (!await ReapOldNameAsync(state, from, cancel))
        {
            return;
        }

        state.PendingRenameFrom = null;
        state.PendingRenameTo = null;
        state.PendingRenameDocToken = null;
        state.PendingRenameManifestToken = null;
        state.LastError = null;
        state.LastErrorKind = null;
        _ledger.Save(state);
    }

    /// <summary>第 5 条的 ③：这条改名在仓里了吗？没有就追加一发（条件写，撞一次就重读再试）。</summary>
    private async Task<bool> MarkerIsInAsync(SyncState state, string from, string to, CancellationToken cancel)
    {
        for (var attempt = 0; attempt < 2; attempt++)
        {
            var file = await _remote.ReadAsync(RenameLog.RepoPath, cancel);
            var log = RenameLog.Parse(file is null ? null : Encoding.UTF8.GetString(file.Content), out var problem);

            if (log is null || problem is not null)
            {
                state.LastError = $"改名标记读不懂，没往里追加：{problem}";
                _ledger.Save(state);
                Note(state.LastError);

                return false;
            }

            if (log.Has(from, to, state.DeviceId))
            {
                // 上一轮发出去了、只是 ④ 没走完：这一发不重写第二份（重写是一个空提交）。
                Note($"改名标记早在仓里了，只欠删旧名那两份：{from} → {to}");

                return true;
            }

            log.Append(new RenameEntry(from, to, state.DeviceId, RenameLog.Stamp(DateTimeOffset.UtcNow)));

            var written = await _remote.WriteAsync(RenameLog.RepoPath, RenameLog.ToBytes(log), file?.Token, cancel);

            if (written.Outcome is WriteOutcome.Written)
            {
                Note($"改名标记已发到远端：{from} → {to}");

                return true;
            }

            if (attempt == 1)
            {
                // 两轮都挤不进去：留着待发，下一轮再补。这不是"改名失败"，本机已经改完了。
                state.LastError = $"改名标记这轮没发出去：{written.Detail}";
                _ledger.Save(state);
                Note(state.LastError);
            }
        }

        return false;
    }

    /// <summary>
    /// 第 5 条的 ④：删掉仓里旧名那两份，各带**改名当时**的令牌。
    /// <para>
    /// 拿旧令牌而不是删的时候现读一份，是为了"那两份已经不是我们发出去的了"那一格：别人在旧名字上
    /// 又建了一块板，条件不过就不删（<see cref="WriteOutcome.Conflict"/>）。没有凭据的那一路（旧版本
    /// 留下的 pending、从没写过的 manifest）也按"不删"办——宁多留一份，不删错一份。
    /// </para>
    /// </summary>
    /// <returns>true＝两份都清掉了（或本来就没得清）；false＝还有一份没删成，下一轮再补。</returns>
    private async Task<bool> ReapOldNameAsync(SyncState state, string from, CancellationToken cancel)
    {
        var owed = new List<string>();
        var done = true;

        foreach (var (path, token) in new[]
                 {
                     (SyncPaths.DocPathFor(from), state.PendingRenameDocToken),
                     (SyncPaths.ManifestPathFor(from), state.PendingRenameManifestToken),
                 })
        {
            if (token is null)
            {
                continue;
            }

            var outcome = await _remote.DeleteAsync(path, token, cancel);

            if (outcome is WriteOutcome.Written or WriteOutcome.Missing)
            {
                Note($"仓里旧名那份清了：{path}");

                continue;
            }

            done = false;
            owed.Add(path);
        }

        if (!done)
        {
            state.LastError = $"旧名那份没删成：{string.Join("、", owed)}";
            _ledger.Save(state);
            Note(state.LastError);
        }

        return done;
    }
}
