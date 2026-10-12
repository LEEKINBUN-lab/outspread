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

    private static IEnumerable<string> Ancestors(DirectoryInfo start)
    {
        for (var current = start; current is not null; current = current.Parent)
        {
            yield return current.FullName;
        }
    }
}
