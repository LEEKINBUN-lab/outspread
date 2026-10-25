namespace Outspread.Core;

/// <summary>
/// Decides which board file a process opens, and says out loud why.
/// </summary>
/// <remarks>
/// A double-clicked executable has no useful working directory, and an agent
/// invoking the CLI usually has one. Both have to land on the same file, otherwise
/// the agent edits a board nobody is looking at. The chosen path is always
/// accompanied by the rule that produced it.
/// </remarks>
public static class BoardLocator
{
    public const string DefaultFolder = "boards";

    public const string DefaultFileName = "scratch.canvas";

    /// <summary>
    /// 首次开屏那块教程板。它和 <see cref="DefaultFileName"/> 是两件事：前者是"这台机器第一次
    /// 打开时给他看什么"，后者是"没点名时 CLI 和启动器兜底找哪一个文件"。2026-10-23 他把演示板
    /// 改成了教程板，并要首次开屏进它——之后的开屏由 <see cref="BoardSession"/> 记着的上一块板接。
    /// </summary>
    public const string QuickStartFileName = "快速入门.canvas";

    public const string BoardPathEnvironmentVariable = "TANKAI_BOARD";

    /// <summary>Environment overrides, applied in the order documented here.</summary>
    public sealed record Resolution(string BoardPath, string Reason, bool CreatedIfMissing = false);

    public static Resolution Resolve(string? explicitPath = null, string? searchStart = null)
    {
        if (!string.IsNullOrWhiteSpace(explicitPath))
        {
            var full = Path.GetFullPath(explicitPath);

            return new Resolution(full, $"requested on the command line ({full})");
        }

        var fromEnvironment = Environment.GetEnvironmentVariable(BoardPathEnvironmentVariable);

        if (!string.IsNullOrWhiteSpace(fromEnvironment))
        {
            var full = Path.GetFullPath(fromEnvironment);

            return new Resolution(full, $"{BoardPathEnvironmentVariable} ({full})");
        }

        var start = Path.GetFullPath(searchStart ?? Directory.GetCurrentDirectory());

        foreach (var directory in Ancestors(new DirectoryInfo(start)))
        {
            var defaultBoard = Path.Combine(directory, DefaultFolder, DefaultFileName);

            if (File.Exists(defaultBoard))
            {
                return new Resolution(defaultBoard, $"found by searching upward from {start}");
            }
        }

        foreach (var directory in Ancestors(new DirectoryInfo(start)))
        {
            var found = FirstBoardIn(directory);

            if (found is not null)
            {
                return new Resolution(found, $"first .canvas file in {directory}, found while searching upward");
            }
        }

        var created = FallbackPath();

        Directory.CreateDirectory(Path.GetDirectoryName(created)!);

        return new Resolution(
            created,
            $"no board found above {start}; using this program's own board folder. Point at one with --board or {BoardPathEnvironmentVariable}",
            CreatedIfMissing: true);
    }

    /// <summary>
    /// Where a board goes when nothing was found: under the user's local app data, not
    /// in whatever folder a double-clicked executable happened to be launched from.
    /// </summary>
    public static string FallbackPath()
        => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Outspread",
            DefaultFolder,
            DefaultFileName);

    /// <summary>
    /// The folder a running board should be watched from. Kept next to the board file
    /// so a board moved between projects travels with its own images.
    /// </summary>
    public static string BoardDirectory(string boardPath)
        => Path.GetDirectoryName(Path.GetFullPath(boardPath)) ?? ".";

    public static string? FirstBoardIn(string directory)
    {
        if (!Directory.Exists(directory))
        {
            return null;
        }

        foreach (var file in Directory.EnumerateFiles(directory, "*" + CanvasStore.Extension)
                     .OrderBy(path => path, StringComparer.Ordinal))
        {
            return file;
        }

        return null;
    }

    /// <summary>
    /// 向上找 <c>boards/快速入门.canvas</c>——首次开屏要给他看的那块教程板。找不到返回 null，
    /// 由调用方落回 <see cref="Resolve"/> 的老规矩。只认这个确切的名字：这不是"目录里第一块板"
    /// 那种猜测，猜错会把别人的板当成教程打开。
    /// </summary>
    public static string? FindQuickStart(string? searchStart = null)
    {
        var start = Path.GetFullPath(searchStart ?? Directory.GetCurrentDirectory());

        foreach (var directory in Ancestors(new DirectoryInfo(start)))
        {
            var candidate = Path.Combine(directory, DefaultFolder, QuickStartFileName);

            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        return null;
    }

    /// <summary>
    /// Every board in one folder, in the order <see cref="FirstBoardIn"/> would walk them.
    /// </summary>
    /// <remarks>
    /// Conflict copies are deliberately not <c>.canvas</c> (see <c>SyncEngine.ConflictCopyPath</c>),
    /// so they cannot show up here and turn one failed sync into a board nobody asked for.
    /// </remarks>
    public static IReadOnlyList<string> BoardsIn(string directory)
    {
        if (!Directory.Exists(directory))
        {
            return [];
        }

        return Directory.EnumerateFiles(directory, "*" + CanvasStore.Extension)
            .OrderBy(path => path, StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>How long a board's name may be. A tab strip has to show it without eliding the
    /// words that make it a task, and the ledger folder on the far side is named after it.</summary>
    public const int MaxBoardNameChars = 40;

    /// <summary>
    /// Turns the name a person typed into a board path inside <paramref name="directory"/>, or says
    /// in one sentence why it cannot.
    /// </summary>
    /// <remarks>
    /// The name is the only part of a new board the interface asks for, and it becomes a file name,
    /// a folder key in <c>boards/.sync/</c> and the tab's label. So the refusal has to be one string
    /// a person can act on — "invalid characters" is not, and a silent trim that lands on a different
    /// board than the one they typed is worse.
    /// </remarks>
    public static NewBoardResult NewBoard(string directory, string name)
        => CheckName(directory, name, ignorePath: null);

    /// <summary>
    /// 把一块已经在了的板改成另一个名字。它只回答"新名字能不能用、会落到哪个路径"，
    /// 不移动文件——移动、sidecar 和 watcher 的换绑属于界面那一层，Core 不碰进程状态。
    /// </summary>
    /// <remarks>
    /// 同名的这一发是合法的空操作；只改大小写也合法（Windows 不区分大小写，界面侧要用
    /// 临时名走两步）。目标已经是一块别的板时照旧拒绝，且不会把原文件当成"目标已存在"。
    /// </remarks>
    public static NewBoardResult Rename(string boardPath, string name)
    {
        var full = Path.GetFullPath(boardPath);

        return CheckName(BoardDirectory(full), name, ignorePath: full);
    }

    /// <summary>
    /// 一步新建用的名字：<c>未命名板1</c>、<c>未命名板2</c>……从 1 开始顺延。
    /// 被一个**有内容**的板占了就跳过；零节点的空文件（旧版留下的空壳或手工空板）直接复用，
    /// 这样名字被腾出来时仍先回到 1。名字本身的规则仍由 <see cref="NewBoard"/> 守。
    /// </summary>
    /// <param name="avoid">
    /// 现在开着的路径（大小写不敏感）。空壳如果已经在标签条上，不让新板去复用它。
    /// </param>
    public static NewBoardResult NewUntitledBoard(
        string directory,
        IReadOnlyCollection<string>? avoid = null)
    {
        for (var suffix = 1; suffix < 1000; suffix++)
        {
            var name = $"未命名板{suffix}";
            var path = Path.Combine(directory, name + CanvasStore.Extension);

            if (avoid is not null && avoid.Any(open => string.Equals(open, path, StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            if (!File.Exists(path))
            {
                return NewBoard(directory, name);
            }

            // 旧版关掉未命名板时留下的空壳：内容为空就直接拿来用，不再往下数。
            try
            {
                var document = CanvasDocument.Parse(CanvasStore.ReadAllText(path), path);

                if (document.Nodes.Count == 0)
                {
                    return new NewBoardResult(path, null);
                }
            }
            catch (Exception ex) when (ex is CanvasDocumentException or IOException or UnauthorizedAccessException)
            {
                // 读不懂/读不到就当成被占：绝不拿一个看不懂的文件当新板写掉。
            }
        }

        return NewBoardResult.Refuse("未命名板编号已达上限 999：请先腾出一个名称");
    }

    private static NewBoardResult CheckName(string directory, string name, string? ignorePath)
    {
        var trimmed = name.Trim();

        if (trimmed.Length == 0)
        {
            return NewBoardResult.Refuse("名称不能为空：请填写板名称，例如“周会”");
        }

        if (trimmed.EndsWith(CanvasStore.Extension, StringComparison.OrdinalIgnoreCase))
        {
            trimmed = trimmed[..^CanvasStore.Extension.Length];

            if (trimmed.Trim().Length == 0)
            {
                return NewBoardResult.Refuse("名称为空：仅提供了扩展名");
            }
        }

        if (trimmed.Length > MaxBoardNameChars)
        {
            return NewBoardResult.Refuse(
                $"名称长度为 {trimmed.Length} 个字符，超过上限 {MaxBoardNameChars}");
        }

        if (trimmed.StartsWith('.'))
        {
            return NewBoardResult.Refuse("名称不能以句点开头（该前缀保留给 .sync 等目录）");
        }

        if (trimmed.EndsWith('.'))
        {
            return NewBoardResult.Refuse("名称不能以句点结尾（Windows 会忽略该字符）");
        }

        var badIndex = trimmed.IndexOfAny(InvalidNameCharacters);

        if (badIndex >= 0)
        {
            return NewBoardResult.Refuse($"名称包含不能用于文件名的字符“{trimmed[badIndex]}”");
        }

        if (trimmed.Contains("..", StringComparison.Ordinal))
        {
            return NewBoardResult.Refuse("名称不能包含“..”（板只能创建在当前板目录内）");
        }

        var path = Path.Combine(directory, trimmed + CanvasStore.Extension);

        if (!string.Equals(path, ignorePath, StringComparison.OrdinalIgnoreCase) && File.Exists(path))
        {
            return NewBoardResult.Refuse($"板“{trimmed}”已存在");
        }

        return new NewBoardResult(path, null);
    }

    private static readonly char[] InvalidNameCharacters = ['/', '\\', ':', '*', '?', '"', '<', '>', '|'];

    /// <param name="BoardPath">The path to create, when <paramref name="Problem"/> is null.</param>
    /// <param name="Problem">One sentence the interface can show as-is; null means the name is usable.</param>
    public sealed record NewBoardResult(string? BoardPath, string? Problem)
    {
        public bool Ok => BoardPath is not null && Problem is null;

        public static NewBoardResult Refuse(string problem) => new(null, problem);
    }

    private static IEnumerable<string> Ancestors(DirectoryInfo start)
    {
        for (var current = start; current is not null; current = current.Parent)
        {
            yield return current.FullName;
        }
    }
}
