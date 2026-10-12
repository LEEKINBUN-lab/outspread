namespace Outspread.Core;

/// <summary>
/// Write-ahead snapshots: a copy of the board's own bytes, kept next to the file, taken every time
/// something is about to replace them.
/// </summary>
/// <remarks>
/// <para>
/// This exists because <b>sync is not a backup</b> — Obsidian says so in its own words
/// (<c>obsidian-help</c> → Getting started/Back up.md), and the same evidence says the thing that
/// actually saves a user is a device-local history (File recovery: snapshots at a minimum interval,
/// kept for days, restorable, including <c>.canvas</c>). Before this, the only recovery from a bad
/// merge or a half-written file was Ctrl+Z, which dies with the window.
/// </para>
/// <para>
/// Three rules, each with a test:
/// <list type="number">
/// <item><b>Only inside the write lock, before the atomic move.</b> A snapshot taken outside the lock
/// can capture a state that never existed on disk.</item>
/// <item><b>A minimum spacing, plus a content check.</b> Routine saves are throttled to one per
/// <see cref="MinInterval"/>, and a save whose bytes match the newest snapshot stores nothing —
/// otherwise a board left open while panning would fill the disk with identical copies.</item>
/// <item><b>A restore always snapshots first, bypassing the throttle.</b> The moment before a
/// restore is exactly the moment where being wrong is most expensive.</item>
/// </list>
/// </para>
/// <para>
/// The folder name is ASCII on purpose (<c>.outspread-history</c>): this project has already been
/// bitten once by a multi-byte name meeting a tool that reads bytes in the OEM code page, and a
/// history folder that cannot be listed is worse than no history folder.
/// </para>
/// </remarks>
public static class Snapshots
{
    public const string FolderName = ".outspread-history";

    /// <summary>Minimum spacing between two routine snapshots of the same board.</summary>
    public static readonly TimeSpan MinInterval = TimeSpan.FromMinutes(5);

    /// <summary>How many to keep at most, newest first.</summary>
    public const int Keep = 50;

    /// <summary>Snapshots older than this are pruned — but never below <see cref="Floor"/>.</summary>
    public static readonly TimeSpan MaxAge = TimeSpan.FromDays(7);

    /// <summary>The floor that <see cref="MaxAge"/> must not cut into: a board nobody touched for a
    /// month still has somewhere to go back to.</summary>
    public const int Floor = 10;

    /// <summary>The timestamp baked into a file name, local time (a restore is chosen by eye).</summary>
    public const string StampFormat = "yyyyMMdd-HHmmss";

    public static string DirectoryFor(string boardPath)
        => Path.Combine(
            Path.GetDirectoryName(Path.GetFullPath(boardPath)) ?? ".",
            FolderName);

    public static string FileNameFor(string boardPath, DateTimeOffset at)
        => $"{Path.GetFileNameWithoutExtension(boardPath)}@{at.ToLocalTime().ToString(StampFormat)}.canvas";

    public readonly record struct Entry(string Path, string BoardName, DateTimeOffset TakenAt, long Bytes)
    {
        /// <summary>Local clock, to the minute — what a person scans a list by.</summary>
        public string Display => TakenAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm");
    }

    /// <summary>
    /// The newest snapshot's bytes, or null when there is none. Read before a write so the caller can
    /// skip storing a copy of something it already has.
    /// </summary>
    public static string? NewestText(string boardPath)
    {
        var entries = List(boardPath);

        if (entries.Count == 0)
        {
            return null;
        }

        try
        {
            return CanvasStore.ReadAllText(entries[0].Path);
        }
        catch (IOException)
        {
            return null;
        }
    }

    /// <summary>
    /// Store <paramref name="currentText"/> (the bytes about to be replaced) if the rules allow it.
    /// Returns the path written, or null when nothing was stored. Never throws: a history folder that
    /// cannot be written must not stop the board from saving.
    /// </summary>
    public static string? TryPreserve(string boardPath, string currentText, DateTimeOffset at, bool force = false)
    {
        if (string.IsNullOrEmpty(currentText))
        {
            // A brand-new board has nothing to protect yet.
            return null;
        }

        try
        {
            var directory = DirectoryFor(boardPath);
            Directory.CreateDirectory(directory);

            var existing = List(boardPath);

            if (!force)
            {
                if (existing.Count > 0)
                {
                    var last = existing[0];

                    if (at - last.TakenAt < MinInterval)
                    {
                        return null;
                    }

                    // 存了也一样：一份和上一份同字节的快照只是把目录撑大。
                    if (string.Equals(Read(last), currentText, StringComparison.Ordinal))
                    {
                        return null;
                    }
                }
            }

            var path = Unique(Path.Combine(directory, FileNameFor(boardPath, at)));

            // Same volume, same trick the board itself uses: write aside, then move.
            var temporary = $"{path}.{Environment.ProcessId}.tmp";

            try
            {
                File.WriteAllText(temporary, currentText, new System.Text.UTF8Encoding(false));
                File.Move(temporary, path, overwrite: true);
            }
            finally
            {
                if (File.Exists(temporary))
                {
                    try
                    {
                        File.Delete(temporary);
                    }
                    catch (IOException)
                    {
                        // Leftover noise, not a lost board.
                    }
                }
            }

            Prune(boardPath, at);

            return path;
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>
    /// The stamp is to the second, because that is what a person reads a list by — so two snapshots
    /// taken in the same second would land on one name and the second would overwrite the first.
    /// A forced store (a restore) can land in the same second as a routine one, so a taken name
    /// steps to <c>.2</c>, <c>.3</c> … rather than eating an older copy.
    /// </summary>
    private static string Unique(string path)
    {
        if (!File.Exists(path))
        {
            return path;
        }

        var stem = path[..^Path.GetExtension(path).Length];

        for (var n = 2; n < 100; n++)
        {
            var candidate = $"{stem}.{n}{CanvasStore.Extension}";

            if (!File.Exists(candidate))
            {
                return candidate;
            }
        }

        return $"{stem}.{Environment.ProcessId}{CanvasStore.Extension}";
    }

    /// <summary>Newest first. A name that does not parse is left out rather than guessed at.</summary>
    public static IReadOnlyList<Entry> List(string boardPath)
    {
        var directory = DirectoryFor(boardPath);

        if (!Directory.Exists(directory))
        {
            return Array.Empty<Entry>();
        }

        var stem = Path.GetFileNameWithoutExtension(boardPath);
        var entries = new List<Entry>();

        foreach (var path in Directory.GetFiles(directory, "*.canvas"))
        {
            var name = Path.GetFileNameWithoutExtension(path);

            var split = name.LastIndexOf('@');

            if (split <= 0 || !name[..split].Equals(stem, StringComparison.Ordinal))
            {
                continue;
            }

            // 同一秒里存下第二份时名字会带 .2 / .3（见 Unique），时间戳取第一段。
            if (!DateTimeOffset.TryParseExact(
                    name[(split + 1)..].Split('.')[0], StampFormat,
                    System.Globalization.CultureInfo.InvariantCulture,
                    System.Globalization.DateTimeStyles.None,
                    out var taken))
            {
                continue;
            }

            long bytes = 0;

            try
            {
                bytes = new FileInfo(path).Length;
            }
            catch (IOException)
            {
                // A listing that is missing a size is still a listing.
            }

            entries.Add(new Entry(path, Path.GetFileName(boardPath), taken, bytes));
        }

        return entries.OrderByDescending(entry => entry.TakenAt).ToList();
    }

    public static string Read(Entry entry) => CanvasStore.ReadAllText(entry.Path);

    /// <summary>
    /// Drop the oldest beyond <see cref="Keep"/> (a hard size guard), and beyond <see cref="MaxAge"/>
    /// — but the age rule never cuts into the newest <see cref="Floor"/>, so a board nobody touched
    /// for a month still has somewhere to go back to.
    /// </summary>
    public static int Prune(string boardPath, DateTimeOffset now)
    {
        var dropped = 0;

        foreach (var (entry, index) in List(boardPath).Select((value, i) => (value, i)))
        {
            var tooMany = index >= Keep;
            var tooOld = now - entry.TakenAt > MaxAge && index >= Floor;

            if (!tooMany && !tooOld)
            {
                continue;
            }

            try
            {
                File.Delete(entry.Path);
                dropped++;
            }
            catch (IOException)
            {
                // Cannot delete right now; the next save tries again.
            }
            catch (UnauthorizedAccessException)
            {
            }
        }

        return dropped;
    }
}
