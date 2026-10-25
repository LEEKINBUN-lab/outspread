using System.Net.Http;
using Microsoft.UI.Dispatching;
using Outspread.Core;

namespace Outspread.Board;

/// <summary>
/// The window's hand on syncing: when to run a round, and what to say when one fails.
/// </summary>
/// <remarks>
/// A round is triggered by things that actually happened — a save landed, the board changed
/// under us, the window came back to the foreground — plus one poll for the two cases nothing
/// else can see: another device wrote while this machine sat idle, and the network came back
/// after a round failed. (This projection has no System.Net.NetworkInformation, so the
/// reconnect is picked up by the poll rather than by an event: up to two minutes of latency,
/// in exchange for not adding a reference to someone else's project file.)
///
/// That poll is what makes receiving possible at all, and it costs one request per round (a
/// folder whose tree token matches what we last wrote reads no files). The interval is a
/// latency dial, not a budget dial.
///
/// Nothing here edits the board. The engine writes through <see cref="CanvasStore"/>'s locked
/// atomic path, and the file watcher turns that write into the ordinary "并入了外面的改动" line
/// a CLI write already produces — so a sync that worked needs no new message, and one that did
/// not cannot hide.
/// </remarks>
internal sealed class SyncHost
{
    /// <summary>
    /// How long a change on another device may sit unnoticed while this window is open. A latency
    /// dial, not a budget: an idle round is one tree request, so eight seconds spends 450 of the
    /// 5000-per-hour allowance (9%). The phone's side of the same number is only spent while its
    /// screen is on, so the two ends together still sit under a tenth of the quota.
    /// </summary>
    private const int PollSeconds = 8;

    /// <summary>Where a round that did not get through backs off to; an offline machine must not ask every eight seconds.</summary>
    private const int RetryCeilingSeconds = 120;

    private readonly string _boardPath;
    private readonly Action<string> _say;
    private readonly DispatcherQueue _queue;
    private readonly Action<string> _onRemoteRename;
    private readonly DispatcherQueueTimer _timer;

    private SyncEngine? _engine;
    private SyncConfig? _engineConfig;
    private GitHubContents? _transport;
    private CancellationTokenSource _cancel = new();
    private bool _running;
    private bool _wantedAgain;
    private bool _stopped;

    /// <summary>Rounds in a row that did not get a real answer; drives the poll's backoff.</summary>
    private int _failures;

    /// <param name="onRemoteRename">
    /// 同伴把这块板改了名（契约「改名怎么跨端跟上」）。引擎只负责**拦下那一发**（本地一个字节不动），
    /// 搬文件、搬账本、换标签是界面的活。这里不当场调用：那一发正跑在这一轮的收尾里，而搬运会
    /// <c>Stop()</c> 掉当前这个宿主、把它正在用的传输 dispose 掉——所以排到队列的下一拍，等这一轮
    /// 彻底退出之后再动手。
    /// </param>
    public SyncHost(string boardPath, Action<string> say, DispatcherQueue queue, Action<string> onRemoteRename)
    {
        _boardPath = boardPath;
        _say = say;
        _queue = queue;
        _onRemoteRename = onRemoteRename;

        // WinUI 3 has no DispatcherTimer: the queue's own timer is the one that runs on this
        // window's thread, which is what Say() needs to be allowed to touch the visual tree.
        _timer = queue.CreateTimer();
        _timer.Interval = TimeSpan.FromSeconds(PollSeconds);
        _timer.Tick += (_, _) => _ = RoundAsync();

    }

    /// <summary>False until a repository is pointed at and a token is stored — a state with nothing to do.</summary>
    public bool Enabled
        => SyncConfig.Load(SyncLayout.Root) is { IsSet: true } && SyncCredentials.Read() is { Length: > 0 };

    /// <summary>
    /// The rename moved the ledger folder and the credential name. Both are reported once into
    /// the log — a window that silently stopped syncing is the failure this subsystem keeps
    /// meeting, so it gets a sentence rather than a guess.
    /// </summary>
    private void ReportMigrations()
    {
        SyncCredentials.Read();

        foreach (var note in new[] { SyncLayout.Migrate(), SyncCredentials.LastMigration }
                     .Where(item => !string.IsNullOrEmpty(item)))
        {
            Diagnostics.Write($"sync migrated: {note}");
        }
    }

    /// <summary>Starts the poll and runs one round at once. Silent when nothing is configured.</summary>
    public void Start()
    {
        if (Refused())
        {
            return;
        }

        _stopped = false;
        _cancel.Dispose();
        _cancel = new CancellationTokenSource();
        _failures = 0;
        _timer.Interval = TimeSpan.FromSeconds(PollSeconds);
        _timer.Start();

        _ = RoundAsync();
    }

    /// <summary>
    /// The one question every entry point has to pass. <see cref="NotifyChanged"/> fires on
    /// every save and a self test saves constantly, so answering it only in <see cref="Start"/>
    /// would let a test window start real network rounds — which is exactly what happened once
    /// this wiring was live: a gate run minted a device id for the temporary copy.
    /// </summary>
    private bool Refused()
    {
        if (InTempFolder(_boardPath))
        {
            // The window's own self test runs against a copy under %TEMP%, and the CLI's
            // fixtures live there too. Without this guard, every gate run would open a fresh
            // board called "selftest" on the far side and keep a ledger for it.
            Diagnostics.Write($"sync skipped for a board under the temp folder: {_boardPath}");

            return true;
        }

        if (!Enabled)
        {
            // A board with no repository pointed at it is not a failure worth interrupting
            // someone for; `outspread sync status` is where that state is reported.
            return true;
        }

        return false;
    }

    private static bool InTempFolder(string path)
    {
        var temp = Path.TrimEndingDirectorySeparator(Path.GetTempPath());
        var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));

        return full.StartsWith(temp + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }

    public void Stop()
    {
        _stopped = true;
        _cancel.Cancel();
        _timer.Stop();

        _transport?.Dispose();
        _transport = null;
    }

    /// <summary>
    /// Call when this disk holds something the far side has not seen — a save landed, or an
    /// agent's write was merged in. Both mean the same thing to a sync: there is something to send.
    /// </summary>
    public void NotifyChanged() => Wake();

    /// <summary>Call when the window comes back: a machine that slept should catch up at once.</summary>
    public void NotifyForeground() => Wake();

    /// <summary>
    /// A person (or a save) asked, so the poll goes back to its fast pace even if the last few
    /// rounds were refused. Without this, an edit made while the network was down would wait out
    /// the whole backoff before it was even attempted.
    /// </summary>
    private void Wake()
    {
        _failures = 0;
        _timer.Interval = TimeSpan.FromSeconds(PollSeconds);

        _ = RoundAsync();
    }

    /// <summary>
    /// The poll's interval is a consequence of what the last round got back, not a constant: a
    /// refused round retries at 16, 32, 64, 120 seconds instead of every eight, and one clean
    /// answer puts it back. <see cref="SyncOutcome.NothingToDo"/> is a clean answer — it means the
    /// far side was reached.
    /// </summary>
    private void RememberPace(SyncOutcome outcome)
    {
        var answered = outcome is SyncOutcome.NothingToDo or SyncOutcome.Pushed or SyncOutcome.Pulled
            or SyncOutcome.GaveUpConflict or SyncOutcome.DiskMovedUnderneath
            // 跟到改名不是网络没通：远端答得清清楚楚，只是这块板在别处换了名字。退避到 120 秒
            // 会让"搬完再补一轮"等得莫名其妙。
            or SyncOutcome.FollowedRename;

        _failures = answered ? 0 : _failures + 1;

        _timer.Interval = TimeSpan.FromSeconds(_failures == 0
            ? PollSeconds
            : Math.Min(RetryCeilingSeconds, PollSeconds << Math.Min(_failures, 4)));
    }

    /// <summary>
    /// One round at a time. A round asked to start while one is in flight is remembered as a
    /// single "go again when this lands" rather than run alongside it: two rounds of the same
    /// device merging against two different far states is a race we would have built ourselves.
    /// </summary>
    private async Task RoundAsync()
    {
        if (_stopped)
        {
            return;
        }

        if (_running)
        {
            _wantedAgain = true;

            return;
        }

        var config = SyncConfig.Load(SyncLayout.Root);

        // Every entry point goes through here, so this is where "this board does not sync" is
        // answered — including the temporary copy the window's own self test edits.
        if (config is null || Refused())
        {
            return;
        }

        ReportMigrations();

        _running = true;
        var cancel = _cancel.Token;

        try
        {
            var report = await EngineFor(config).SynchronizeAsync(cancel);

            // A round that was still in flight when this board was closed must not narrate
            // the file it can no longer read. Closing deletes the file; that is not a sync error.
            if (_stopped || cancel.IsCancellationRequested)
            {
                Diagnostics.Write("sync round dropped: board closed");

                return;
            }

            Say(report);
            RememberPace(report.Outcome);
        }
        catch (OperationCanceledException) when (cancel.IsCancellationRequested)
        {
            Diagnostics.Write("sync round canceled: board closed");
        }
        catch (Exception ex) when (ex is SyncSetupException or HttpRequestException or IOException or InvalidOperationException)
        {
            if (_stopped || cancel.IsCancellationRequested)
            {
                Diagnostics.Write("sync round dropped after stop");

                return;
            }

            // A configuration or transport problem gets the same one sentence as a refused
            // round. Neither of them is allowed to become a retry loop nobody can see.
            _say($"同步没走：{ex.Message}");

            Diagnostics.Write($"sync setup failed: {ex.GetType().Name} {ex.Message}");

            RememberPace(SyncOutcome.Offline);
        }
        finally
        {
            _running = false;

            if (_wantedAgain && !_stopped)
            {
                _wantedAgain = false;

                _ = RoundAsync();
            }
        }
    }

    /// <summary>
    /// The transport is kept between rounds on purpose. Resolving which branch to use costs one
    /// request, so a fresh adapter per poll would double what idle time spends — measured 2
    /// against 1 on the live API.
    /// </summary>
    private SyncEngine EngineFor(SyncConfig config)
    {
        var same = _transport is not null
            && _engineConfig is not null
            && _engineConfig.Host == config.Host
            && _engineConfig.Owner == config.Owner
            && _engineConfig.Repo == config.Repo
            && _engineConfig.Branch == config.Branch;

        if (!same)
        {
            _transport?.Dispose();
            _engine = null;

            _transport = (GitHubContents)config.OpenRemote();
            _engineConfig = config;
        }

        return _engine ??= new SyncEngine(
            _transport!,
            new SyncLedger(SyncLayout.LedgerFolder(_boardPath)),
            _boardPath,
            Diagnostics.Write);
    }

    /// <summary>Deletions this window has already told the user about, so a retry loop is not a loop of toasts.</summary>
    private readonly HashSet<string> _warnedRetries = new(StringComparer.Ordinal);

    private void Say(SyncReport report)
    {
        switch (report.Outcome)
        {
            case SyncOutcome.FollowedRename:
                // 引擎已经把那一发拦住了（本地未动）。搬是界面的活，排到下一拍再做。
                if (!string.IsNullOrEmpty(report.RenameTo))
                {
                    var stem = report.RenameTo!;

                    _queue.TryEnqueue(() => _onRemoteRename(stem));
                }

                break;

            case SyncOutcome.NothingToDo:
            case SyncOutcome.Pushed:
                // Nothing moved, or our state is now the far state. A board that is where it
                // should be is not a message.
                break;

            case SyncOutcome.Pulled:
                // The engine's write produced the watcher's own "并入了外面的改动" line already.
                Diagnostics.Write($"sync pulled r{report.Rev}");

                break;

            case SyncOutcome.GaveUpConflict:
                _say($"远端持续更新，三轮合并未完成 · 本地未修改，待上传副本保留在 {Path.GetFileName(report.ConflictCopy)}");

                break;

            case SyncOutcome.Offline:
            case SyncOutcome.RateLimited:
                if (report.PendingNodes > 0)
                {
                    _say($"未能上传 · 本地有 {report.PendingNodes} 处待同步");
                }

                break;

            case SyncOutcome.LocalUnreadable:
                // The raw exception can be a long path; keep it in the log and say the one
                // actionable sentence on screen. A deleted board is handled by the stop guard above.
                Diagnostics.Write($"sync local unreadable: {report.Detail}");
                _say("同步未执行：本地文件不可读（详见日志）");

                break;

            default:
                // 内部读数（r195／待发 N／英文原因）只进日志；屏幕上只留能动作的那几种（上面）。
                Diagnostics.Write($"sync {report.Outcome}: {report.StatusLine()}｜{report.Detail}");

                break;
        }

        foreach (var verdict in report.Verdicts)
        {
            // Every decision that was not "only one end touched this" leaves a line: a merge
            // that quietly picked a winner is exactly what this board cannot afford.
            Diagnostics.Write($"merge {verdict.Rule} {verdict.Id}/{verdict.Field} → {verdict.Chosen}: {verdict.Detail}");
        }

        foreach (var (path, reason) in report.Assets?.Held ?? [])
        {
            if (report.Assets!.Deleted.Contains(path) || !report.Assets.Retry.Contains(path))
            {
                // Either it went away anyway, or nothing this end can do will finish it (a
                // board beside this one is still showing it). That is not news every two minutes.
                continue;
            }

            Diagnostics.Write($"asset not reaped {path}: {reason}");

            if (_warnedRetries.Add(path))
            {
                // A picture the board no longer names and this round could not take away is a
                // deletion that has not finished. Said nothing, it looks like the app kept it.
                _say($"图片未能删除，将在下次同步重试：{path}｜{reason}");
            }
        }

        // A path that stopped being a problem is allowed to become one again, and say so.
        _warnedRetries.IntersectWith(report.Assets?.Retry ?? []);
    }
}
