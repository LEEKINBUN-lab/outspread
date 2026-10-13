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
    /// <summary>How long a change on another device may sit unnoticed while this window is open.</summary>
    private const int PollSeconds = 120;

    private readonly string _boardPath;
    private readonly Action<string> _say;
    private readonly DispatcherQueueTimer _timer;

    private SyncEngine? _engine;
    private SyncConfig? _engineConfig;
    private GitHubContents? _transport;
    private bool _running;
    private bool _wantedAgain;

    public SyncHost(string boardPath, Action<string> say, DispatcherQueue queue)
    {
        _boardPath = boardPath;
        _say = say;

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
        _timer.Stop();

        _transport?.Dispose();
        _transport = null;
    }

    /// <summary>
    /// Call when this disk holds something the far side has not seen — a save landed, or an
    /// agent's write was merged in. Both mean the same thing to a sync: there is something to send.
    /// </summary>
    public void NotifyChanged() => _ = RoundAsync();

    /// <summary>Call when the window comes back: a machine that slept should catch up at once.</summary>
    public void NotifyForeground() => _ = RoundAsync();

    /// <summary>
    /// One round at a time. A round asked to start while one is in flight is remembered as a
    /// single "go again when this lands" rather than run alongside it: two rounds of the same
    /// device merging against two different far states is a race we would have built ourselves.
    /// </summary>
    private async Task RoundAsync()
    {
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

        try
        {
            Say(await EngineFor(config).SynchronizeAsync());
        }
        catch (Exception ex) when (ex is SyncSetupException or HttpRequestException or IOException or InvalidOperationException)
        {
            // A configuration or transport problem gets the same one sentence as a refused
            // round. Neither of them is allowed to become a retry loop nobody can see.
            _say($"同步没走：{ex.Message}");

            Diagnostics.Write($"sync setup failed: {ex.GetType().Name} {ex.Message}");
        }
        finally
        {
            _running = false;

            if (_wantedAgain)
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

    private void Say(SyncReport report)
    {
        switch (report.Outcome)
        {
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
                _say($"远端一直在变，三轮没挤进去 · 本地没动，要传的那份整份留在 {Path.GetFileName(report.ConflictCopy)}");

                break;

            case SyncOutcome.Offline:
            case SyncOutcome.RateLimited:
                if (report.PendingNodes > 0)
                {
                    _say($"还没传上去 · 本地 {report.PendingNodes} 处等着（{report.Detail}）");
                }

                break;

            default:
                _say($"{report.StatusLine()}｜{report.Detail}");

                break;
        }

        foreach (var verdict in report.Verdicts)
        {
            // Every decision that was not "only one end touched this" leaves a line: a merge
            // that quietly picked a winner is exactly what this board cannot afford.
            Diagnostics.Write($"merge {verdict.Rule} {verdict.Id}/{verdict.Field} → {verdict.Chosen}: {verdict.Detail}");
        }
    }
}
