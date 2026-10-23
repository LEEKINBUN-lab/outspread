namespace Outspread.Core;

/// <summary>
/// What this window remembers between launches: which board it was last on, and which boards were
/// open at all.
/// </summary>
/// <remarks>
/// <para>
/// 2026-10-23 他要的第二条：只有"上次那一块"是不够的——他开着「快速入门」＋两块命名板，关掉再打开
/// 只剩一块，其余的标签凭空没了（"我打开后没有快速入门的页面了"）。所以开着的这一排也进记忆。
/// </para>
/// <para>
/// It is a cache, not content: the files live under <c>%LOCALAPPDATA%\Outspread\</c> and hold paths.
/// A missing or unreadable file means "no memory", which is exactly the first-launch answer —
/// so read failures are reported as empty rather than thrown, while write failures are returned to
/// the caller so they can be logged. Losing these costs one launch's convenience, never a board.
/// 一块被记住的板子如果文件已经不在（改名搬走、空板关完即删），读的时候直接丢掉：把不存在的标签
/// 摆回条上，点下去只会报"读不到"。
/// </para>
/// </remarks>
public static class BoardSession
{
    public const string LastBoardFileName = "last-board.txt";

    public const string OpenBoardsFileName = "open-boards.txt";

    /// <summary>Where the board and the CLI keep their per-user state (the log lives here too).</summary>
    public static string DefaultDirectory()
        => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Outspread");

    /// <summary>
    /// The board this window was last on, or null when there is no memory or the remembered file is
    /// gone. A remembered path that no longer exists must not be handed back: the caller would open
    /// it and fail, when the right answer is the first-launch board.
    /// </summary>
    public static string? ReadLastBoard(string? directory = null)
    {
        var path = Path.Combine(directory ?? DefaultDirectory(), LastBoardFileName);

        try
        {
            if (!File.Exists(path))
            {
                return null;
            }

            var recorded = File.ReadAllText(path).Trim();

            return recorded.Length > 0 && File.Exists(recorded) ? recorded : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>
    /// Records <paramref name="boardPath"/> for the next launch. Returns null on success, or the
    /// reason it could not be written — the caller logs that; it never blocks opening a board.
    /// </summary>
    public static string? RememberLastBoard(string boardPath, string? directory = null)
    {
        var folder = directory ?? DefaultDirectory();
        var path = Path.Combine(folder, LastBoardFileName);
        var staged = path + ".tmp";

        try
        {
            Directory.CreateDirectory(folder);
            File.WriteAllText(staged, Path.GetFullPath(boardPath));
            File.Move(staged, path, overwrite: true);

            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return ex.Message;
        }
    }

    /// <summary>
    /// 上一次开着的那一排板子，按当时的顺序；文件已经不在了的那些直接不回来。
    /// 没有记忆（第一次开屏、或这份被删了）时返回空表——那不是"没有开过板子"，
    /// 调用方要按"没有记忆"处理，别把空表当成"他关掉了所有板子"。
    /// </summary>
    public static IReadOnlyList<string> ReadOpenBoards(string? directory = null)
    {
        var path = Path.Combine(directory ?? DefaultDirectory(), OpenBoardsFileName);

        try
        {
            if (!File.Exists(path))
            {
                return [];
            }

            return File.ReadAllLines(path)
                .Select(line => line.Trim())
                .Where(line => line.Length > 0 && File.Exists(line))
                .Distinct(StringComparer.Ordinal)
                .ToArray();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    /// <summary>记下开着的这一排（顺序就是标签顺序）。同 <see cref="RememberLastBoard"/>：写不成只回报，不挡任何事。</summary>
    public static string? RememberOpenBoards(IEnumerable<string> boardPaths, string? directory = null)
    {
        var folder = directory ?? DefaultDirectory();
        var path = Path.Combine(folder, OpenBoardsFileName);
        var staged = path + ".tmp";

        try
        {
            Directory.CreateDirectory(folder);
            File.WriteAllLines(staged, boardPaths.Select(Path.GetFullPath));
            File.Move(staged, path, overwrite: true);

            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return ex.Message;
        }
    }
}
