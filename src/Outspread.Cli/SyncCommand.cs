using Outspread.Core;

namespace Outspread.Cli;

/// <summary>
/// <c>outspread sync …</c> — the hand that moves a board to the far repository and back.
/// </summary>
/// <remarks>
/// This is the layer the window and the phone both have to agree with, so it prints numbers
/// rather than reassurance: which revision the far side is on, what token this device last
/// wrote, how many notes are waiting, and what failed the last time it tried.
///
/// A token is never echoed. It is read from standard input or a file the caller owns and put
/// into the credential store, which keeps it out of the shell history, out of the process
/// list, and out of the board folder that gets copied and zipped.
/// </remarks>
internal static class SyncCommand
{
    public static int Run(Options options)
    {
        var sub = options.Positional.Count switch
        {
            0 => "once",
            1 => options.Positional[0],
            _ => throw new ArgumentException($"outspread sync 只接受一个子命令，给了 {string.Join(' ', options.Positional)}"),
        };

        // link works without a board: pointing this machine at a repository is a step a
        // machine with nothing on it yet can still take.
        var boardPath = sub == "link" ? Locate(options).BoardPath : RequireBoard(options);

        // The product rename moved both the ledger folder and the credential name. Doing this
        // here, before any sub-command reads them, is what keeps "I never changed anything"
        // from becoming "sync quietly stopped".
        ReportMigrations(options);

        return sub switch
        {
            "link" => Link(options),
            "status" => Status(options, boardPath),
            "once" or "push" or "pull" => Once(options, boardPath),
            "pair" => PairCommand.Run(options),
            "forget" => Forget(options, boardPath),
            _ => throw new ArgumentException($"outspread sync 不认识 \"{sub}\"；能用的有 link、status、once、pair、forget"),
        };
    }

    private static BoardLocator.Resolution Locate(Options options) => BoardLocator.Resolve(options.Value("board"));

    /// <summary>
    /// What the rename moved, said out loud: a ledger folder that quietly became empty and a
    /// credential that quietly became unreadable both look like "sync is off", which is the one
    /// thing a user should never have to guess at.
    /// </summary>
    private static IReadOnlyList<string> Notices()
    {
        SyncCredentials.Read();

        return new[] { SyncLayout.Migrate(), SyncCredentials.LastMigration }
            .Where(note => !string.IsNullOrEmpty(note))
            .Select(note => note!)
            .ToList();
    }

    private static void ReportMigrations(Options options)
    {
        if (options.Flag("json"))
        {
            // The JSON answer carries them as a field instead; printing into a machine-readable
            // stream would corrupt it.
            return;
        }

        foreach (var note in Notices())
        {
            Console.WriteLine($"  ! {note}");
        }
    }

    private static string RequireBoard(Options options)
    {
        var resolution = Locate(options);

        if (!File.Exists(resolution.BoardPath))
        {
            throw new SyncSetupException($"{resolution.BoardPath} 不存在（{resolution.Reason}）；先 outspread new 一块，或 --board 指个路径");
        }

        return resolution.BoardPath;
    }

    private static int Fail(string message)
    {
        Console.Error.WriteLine(message);

        return 2;
    }

    /// <summary>
    /// Points this device at a repository and proves the pointer works by reading the folder
    /// back. Without that read a typo in the name would only surface as a failed upload hours
    /// later, in a place nobody is watching.
    /// </summary>
    private static int Link(Options options)
    {
        var config = LoadConfig() ?? new SyncConfig();

        if (options.Value("remote") is { Length: > 0 } remote)
        {
            var parts = remote.Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

            if (parts.Length != 2)
            {
                return Fail("--remote 要的是 owner/仓库名 这种两段，给的是 \"" + remote + "\"");
            }

            config.Owner = parts[0];
            config.Repo = parts[1];
        }

        if (options.Value("branch") is { Length: > 0 } branch)
        {
            config.Branch = branch;
        }

        if (options.Value("host") is { Length: > 0 } host)
        {
            config.Host = host;
        }

        var token = ReadToken(options);

        if (token is not null)
        {
            if (!SyncCredentials.Write(token, user: config.Owner))
            {
                return Fail("令牌没能写进 Windows 凭据管理器（advapi32 拒了）；配置没改，盘上任何东西也没动");
            }

            Console.WriteLine($"  令牌已存入凭据管理器（{SyncCredentials.Target}，指纹 {SyncCredentials.Fingerprint(token)}，明文不显示）");
        }

        if (!config.IsSet)
        {
            return Fail("还缺 owner/仓库名：outspread sync link --remote 你的用户名/仓库名 --token-stdin");
        }

        Console.WriteLine($"  远端 {config.Host} {config.ShortName}:{config.Branch}");

        var transport = config.OpenRemote();
        var tree = transport.ReadTreeAsync(default).GetAwaiter().GetResult();

        if (tree is null)
        {
            // The pointer is only saved once it has been shown to reach something: a device
            // left pointing at a name that 404s would fail later, in a round nobody watches.
            return Fail($"读不到 {config.ShortName}：仓库名错了，或者这把令牌对它没有权限。配置没有写下去，任何东西都没动");
        }

        config.Save(SyncLayout.Root);

        Console.WriteLine($"  通了，配置已写入 {Path.Combine(SyncLayout.Root, SyncConfig.FileName)}");

        // 仓根那份非机密的说明书：手机不必靠配对码就知道这堆文件按哪版格式说话。
        var workspace = new SyncWorkspace
        {
            WrittenBy = config.Owner,
            At = DateTimeOffset.UtcNow.ToString("O"),
        };

        var stamp = transport.WriteAsync(
            SyncWorkspace.RepoPath,
            workspace.ToBytes(),
            tree.TokenFor(SyncWorkspace.RepoPath),
            default).GetAwaiter().GetResult();

        Console.WriteLine(stamp.Outcome == WriteOutcome.Written
            ? $"  已写 {SyncWorkspace.RepoPath}（格式 {workspace.Format}）"
            : $"  {SyncWorkspace.RepoPath} 没写上：{stamp.Detail}");

        // The file carried the old product name. Leaving both in the folder would mean two
        // answers to "which format is this", so the superseded one is removed once, here.
        var staleToken = tree.TokenFor(SyncWorkspace.LegacyRepoPath);

        if (staleToken is not null)
        {
            var removed = transport.DeleteAsync(SyncWorkspace.LegacyRepoPath, staleToken, default).GetAwaiter().GetResult();

            Console.WriteLine(removed == WriteOutcome.Written
                ? $"  旧名的 {SyncWorkspace.LegacyRepoPath} 已从远端删掉（内容已在新名里）"
                : $"  旧名的 {SyncWorkspace.LegacyRepoPath} 没删掉（{removed}），新名那份已经在了");
        }

        var boards = tree.Entries.Where(entry => entry.Path.StartsWith("boards/", StringComparison.Ordinal)).ToList();

        Console.WriteLine($"  远端共有 {tree.Entries.Count} 个文件，其中 boards/ 下 {boards.Count} 个");

        foreach (var entry in boards)
        {
            Console.WriteLine($"    {entry.Path}  {entry.Bytes} 字节  token {Short(entry.Token)}");
        }

        return 0;
    }

    private static int Status(Options options, string boardPath)
    {
        var config = LoadConfig();
        var ledger = new SyncLedger(SyncLayout.LedgerFolder(boardPath));
        var state = ledger.Load();

        var pending = Pending(ledger, state, boardPath);
        var conflicts = ConflictCopies(boardPath);

        if (options.Flag("json"))
        {
            var payload = new Dictionary<string, object?>
            {
                ["board"] = boardPath,
                ["host"] = config?.Host,
                ["remote"] = config is null || !config.IsSet ? null : config.ShortName,
                ["branch"] = config?.Branch,
                ["docPath"] = SyncPaths.ForBoard(boardPath).DocPath,
                ["deviceId"] = state.DeviceId,
                ["rev"] = state.Rev,
                ["clock"] = state.Clock,
                ["docToken"] = state.DocToken,
                ["manifestToken"] = state.ManifestToken,
                ["contentSha"] = state.ContentSha,
                ["publishedSha"] = state.PublishedSha,
                ["pendingNodes"] = pending,
                ["baseFile"] = state.BaseFile,
                ["lastErrorKind"] = state.LastErrorKind,
                ["lastError"] = state.LastError,
                ["conflictCopies"] = conflicts,
                ["ledgerFolder"] = ledger.Folder,
                ["hasToken"] = SyncCredentials.Read() is not null,
                ["notices"] = Notices(),
            };

            Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(payload, Ledger.Options));

            return 0;
        }

        var token = SyncCredentials.Read();

        Console.WriteLine($"  板子    {boardPath}");
        Console.WriteLine($"  远端    {(config is null || !config.IsSet ? "还没定（outspread sync link --remote owner/仓库名）" : $"{config.Host} {config.ShortName}:{config.Branch} → {SyncPaths.ForBoard(boardPath).DocPath}")}");
        Console.WriteLine($"  令牌    {(token is null ? "没有" : $"{SyncCredentials.Origin()}，指纹 {SyncCredentials.Fingerprint(token)}")}");
        Console.WriteLine($"  设备    {state.DeviceId}");
        Console.WriteLine($"  账本    r{state.Rev} · 远端 {Short(state.ContentSha)} · 待发 {pending}");
        Console.WriteLine($"  祖先    {(state.BaseFile is null ? "还没有（首轮取并集）" : state.BaseFile)}");
        Console.WriteLine($"  上次    {(string.IsNullOrEmpty(state.LastError) ? "没有失败记录" : $"{state.LastErrorKind}：{state.LastError}")}");
        Console.WriteLine($"  冲突副本  {conflicts.Count} 个" + (conflicts.Count > 0 ? $"：{string.Join('、', conflicts.Select(Path.GetFileName))}" : string.Empty));
        Console.WriteLine($"  账本目录  {ledger.Folder}");


        return 0;
    }

    /// <summary>
    /// One round. The exit code carries the answer, because a caller that greps for 没出错
    /// must not have to read Chinese to find out whether the board is where it thinks it is.
    /// </summary>
    private static int Once(Options options, string boardPath)
    {
        var config = LoadConfig() ?? throw new SyncSetupException("还没定远端仓库：outspread sync link --remote owner/仓库名 --token-stdin");

        var trace = new List<string>();
        var ledger = new SyncLedger(SyncLayout.LedgerFolder(boardPath));
        var engine = new SyncEngine(config.OpenRemote(), ledger, boardPath, line =>
        {
            trace.Add(line);

            if (options.Flag("verbose"))
            {
                Console.Error.WriteLine($"  · {line}");
            }
        });

        var report = engine.SynchronizeAsync(default).GetAwaiter().GetResult();

        Console.WriteLine($"  {report.StatusLine(report.Rev > 0 ? ledger.Load().ContentSha : null)}");
        Console.WriteLine($"  {report.Outcome} · rounds={report.Rounds} · 裁决={report.Verdicts.Count}{(report.ConflictCopy is null ? string.Empty : $" · 副本={Path.GetFileName(report.ConflictCopy)}")}");

        foreach (var verdict in report.Verdicts)
        {
            Console.WriteLine($"    {verdict.Rule} {verdict.Id}/{verdict.Field} → {verdict.Chosen}（{verdict.Detail}）");
        }

        if (report.Assets is { } assets)
        {
            foreach (var name in assets.Uploaded)
            {
                Console.WriteLine($"    图已上传 {name}");
            }

            foreach (var name in assets.Downloaded)
            {
                Console.WriteLine($"    图已取回 {name}");
            }

            foreach (var pair in assets.Refused)
            {
                Console.WriteLine($"    图没搬 {pair.Path}：{pair.Reason}");
            }

            foreach (var name in assets.Absent)
            {
                Console.WriteLine($"    图两头都没有 {name}：板子上那一格会空着");
            }
        }

        if (!options.Flag("verbose"))
        {
            foreach (var line in trace.Where(line => line.Contains("读不懂") || line.Contains("没上去") || line.Contains("挡回")))
            {
                Console.WriteLine($"    ! {line}");
            }
        }

        return report.Outcome switch
        {
            SyncOutcome.NothingToDo or SyncOutcome.Pushed or SyncOutcome.Pulled => 0,
            SyncOutcome.Offline or SyncOutcome.RateLimited or SyncOutcome.DiskMovedUnderneath => 1,
            _ => 2,
        };
    }

    /// <summary>Drops this board's ledger, keeping the ancestor and any conflict copies for a human.</summary>
    private static int Forget(Options options, string boardPath)
    {
        var ledger = new SyncLedger(SyncLayout.LedgerFolder(boardPath));
        var state = ledger.Load();

        if (options.Flag("all"))
        {
            if (Directory.Exists(ledger.Folder))
            {
                Directory.Delete(ledger.Folder, recursive: true);
            }

            Console.WriteLine($"  删掉了 {ledger.Folder}（含祖先）。远端一个字没动。");

            return 0;
        }

        state.DocToken = null;
        state.ManifestToken = null;
        state.PublishedSha = null;
        state.BaseFile = null;
        state.Rev = 0;
        state.LastError = null;
        state.LastErrorKind = null;
        ledger.Save(state);

        Console.WriteLine($"  忘了这块板与远端的对应关系（r{state.Rev} 起算）。远端一个字没动，本地的板子文件也没动。");

        return 0;
    }

    private static SyncConfig? LoadConfig() => SyncConfig.Load(SyncLayout.Root);

    /// <summary>
    /// How many nodes differ between this disk and the ancestor this device last agreed on —
    /// which is what "waiting" means. It is computed from the two documents rather than
    /// remembered, so a count is never a number a previous round promised.
    /// </summary>
    private static int Pending(SyncLedger ledger, SyncState state, string boardPath)
    {
        var ancestor = ledger.ReadBase(state);

        if (ancestor is null || !File.Exists(boardPath))
        {
            return 0;
        }

        try
        {
            return DocDiff.Between(ancestor, CanvasDocument.Parse(CanvasStore.ReadAllText(boardPath), boardPath)).NodeCount;
        }
        catch (CanvasDocumentException)
        {
            // A board that cannot be read has no count to report; the reason is in the
            // document's own error, and `once` will say it out loud when it tries to sync.
            return 0;
        }
    }

    private static List<string> ConflictCopies(string boardPath)
    {
        var directory = AssetStore.BoardDirectory(boardPath);
        var name = Path.GetFileName(boardPath);

        // `conflict-*` rather than `conflict-r*`: the name gained a machine and a minute on
        // 2026-10-12, and copies written before that are still sitting on people's disks.
        return Directory.Exists(directory)
            ? Directory.GetFiles(directory, $"{name}.conflict-*").ToList()
            : [];
    }

    private static string? ReadToken(Options options)
    {
        if (options.Flag("tokenstdin"))
        {
            // From a pipe, so the token never appears in the command line, the shell history,
            // or the process list an agent can enumerate.
            var piped = Console.In.ReadToEnd().Trim();

            return piped.Length > 0 ? piped : null;
        }

        if (options.Value("tokenfile") is { Length: > 0 } path)
        {
            return File.ReadAllText(path).Trim();
        }

        return null;
    }

    private static string Short(string? value)
        => string.IsNullOrEmpty(value) ? "—" : value[..Math.Min(8, value.Length)] + "…";
}
