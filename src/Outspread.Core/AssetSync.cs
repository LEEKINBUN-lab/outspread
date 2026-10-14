using System.Text;

namespace Outspread.Core;

/// <summary>What an asset round actually did. Every list is a sentence a status line can use.</summary>
public sealed class AssetReport
{
    public List<string> Uploaded { get; } = [];

    public List<string> Downloaded { get; } = [];

    /// <summary>Pictures this end has but will not send, with the reason.</summary>
    public List<(string Path, string Reason)> Refused { get; } = [];

    /// <summary>Pictures the board points at that are not on this machine and not on the far one.</summary>
    public List<string> Absent { get; } = [];

    /// <summary>Pictures this round took away, from the far side, this machine, or both.</summary>
    public List<string> Deleted { get; } = [];

    /// <summary>Pictures a tombstone named but this round left in place, with the reason.</summary>
    public List<(string Path, string Reason)> Held { get; } = [];

    /// <summary>
    /// The subset of <see cref="Held"/> that is worth asking again: the far side refused, the
    /// file was locked, a board beside this one could not be read. A picture another board is
    /// still showing is not pending work — it is in use, and the device that eventually drops
    /// it will notice its own deletion.
    /// </summary>
    public List<string> Retry { get; } = [];

    public bool DidAnything
        => Uploaded.Count > 0 || Downloaded.Count > 0 || Refused.Count > 0 || Absent.Count > 0
        || Deleted.Count > 0 || Held.Count > 0;

    /// <summary>
    /// Adds what another pass did to this one, so a round that pulled, pushed and reaped
    /// still answers with one list per kind of thing that happened.
    /// </summary>
    public void Absorb(AssetReport other)
    {
        ArgumentNullException.ThrowIfNull(other);

        Uploaded.AddRange(other.Uploaded);
        Downloaded.AddRange(other.Downloaded);
        Refused.AddRange(other.Refused);
        Deleted.AddRange(other.Deleted);
        Held.AddRange(other.Held);
        Retry.AddRange(other.Retry);

        foreach (var name in other.Absent)
        {
            if (!Absent.Contains(name))
            {
                Absent.Add(name);
            }
        }
    }
}

/// <summary>
/// Moves the pictures a board points at, and nothing else.
/// </summary>
/// <remarks>
/// Only files some node's <c>file</c> actually references are carried. That is deliberate:
/// the assets folder routinely holds pasted images that are being looked at, not used, and
/// <see cref="AssetStore.UnreferencedImages"/> already takes the position that an orphan is
/// reported and never deleted. Uploading them would spend the far side's quota and move
/// content nobody put on the board.
///
/// Deletions are carried, but only through a tombstone: the board file lists what it uses and
/// cannot say what it removed, so <see cref="Tombstones"/> derives the difference against this
/// device's own ancestor and the engine writes it into the board's ledger entry. The far side
/// is then reaped — after the board that no longer names the picture was accepted, never before.
/// A tombstone never fires against a path something still references, which is what keeps the
/// race honest: if a note comes back to life on another device (<c>Revived</c>), its picture
/// moots the tombstone and goes back up under the same name.
///
/// Steady state costs no requests: the tree call a round already makes lists the assets, and
/// a picture present on both sides is not read.
/// </remarks>
public static class AssetSync
{
    /// <summary>
    /// How far back the ledger remembers deleted pictures. Older entries fall off, and the
    /// worst that costs is a stale copy on a device that was offline the whole time: it keeps
    /// a file it no longer shows, and never puts it back where nobody points at it.
    /// </summary>
    public const int MaxTombstones = 64;

    /// <summary>
    /// The size the file API carries in one call. Above it the endpoint still answers but
    /// without the bytes, so this end refuses the picture by name rather than letting a
    /// halfway upload look like a sync.
    /// </summary>
    public const int MaxCarriedBytes = 1024 * 1024;

    /// <summary>The folder a board's relative paths resolve against, as a repository path.</summary>
    public static string RepoPrefix(SyncPaths paths)
    {
        var directory = Path.GetDirectoryName(paths.DocPath)?.Replace('\\', '/');

        return string.IsNullOrEmpty(directory) ? string.Empty : directory + "/";
    }

    /// <summary>
    /// Every path a node points at, normalised the way <see cref="AssetStore"/> writes them
    /// and with anything reaching outside the board folder dropped — a document an agent wrote
    /// must not be able to aim the sync at a file elsewhere on this machine.
    /// </summary>
    public static IReadOnlyList<string> ReferencedBy(CanvasDocument document)
    {
        var seen = new List<string>();
        var set = new HashSet<string>(StringComparer.Ordinal);

        foreach (var node in document.Nodes)
        {
            var relative = AssetStore.NormalizeRelativePath(node.FilePath);

            if (relative is not null && node.Kind == CanvasNodeKind.File && set.Add(relative))
            {
                seen.Add(relative);
            }
        }

        return seen;
    }

    /// <summary>
    /// What the ledger has to remember so that a deletion reaches the other ends: the far
    /// side's list, plus what this board stopped pointing at since its own ancestor, minus
    /// anything it points at again.
    /// </summary>
    /// <remarks>
    /// Moot entries are dropped rather than kept as a warning: a picture that came back into
    /// use is exactly the case where a tombstone must stop firing, and re-adding the same
    /// picture lands on the same content-hashed name, so a stale entry would otherwise delete
    /// the file the board is now showing.
    /// </remarks>
    public static IReadOnlyList<string> Tombstones(
        CanvasDocument? ancestor,
        CanvasDocument merged,
        IReadOnlyList<string>? farDropped)
    {
        var used = new HashSet<string>(ReferencedBy(merged), StringComparer.Ordinal);

        var kept = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        // Oldest first, so trimming takes off the entries that have had the most time to
        // reach everybody. An entry that falls off costs a stale local copy on a device that
        // was offline the whole while, and nothing on the far side.
        foreach (var relative in (farDropped ?? []).Concat(ancestor is null ? [] : ReferencedBy(ancestor)))
        {
            if (used.Contains(relative) || !seen.Add(relative))
            {
                continue;
            }

            kept.Add(relative);
        }

        return kept.Count > MaxTombstones ? kept[(kept.Count - MaxTombstones)..] : kept;
    }

    /// <summary>
    /// Brings in pictures this machine does not have. Called before the reconciled board is
    /// published, so the window never loads a board whose image is still on its way.
    /// </summary>
    public static async Task<AssetReport> PullAsync(
        IRemoteFiles remote,
        string boardPath,
        CanvasDocument document,
        RemoteTree tree,
        SyncPaths paths,
        CancellationToken cancel)
    {
        var report = new AssetReport();
        var prefix = RepoPrefix(paths);

        foreach (var relative in ReferencedBy(document))
        {
            var target = AssetStore.ResolveExisting(boardPath, relative);

            if (target is not null)
            {
                continue;
            }

            var repoPath = prefix + relative;
            var token = tree.TokenFor(repoPath);

            if (token is null)
            {
                report.Absent.Add(relative);

                continue;
            }

            var file = await remote.ReadAsync(repoPath, cancel);

            if (file is null)
            {
                report.Absent.Add(relative);

                continue;
            }

            WriteAtomically(Path.Combine(AssetStore.BoardDirectory(boardPath), relative.Replace('/', Path.DirectorySeparatorChar)), file.Content);
            report.Downloaded.Add(relative);
        }

        return report;
    }

    /// <summary>
    /// Sends pictures this machine has and the far side does not. A refusal or a lost race is
    /// reported per picture and does not fail the round: the board itself is already up there.
    /// </summary>
    public static async Task<AssetReport> PushAsync(
        IRemoteFiles remote,
        string boardPath,
        CanvasDocument document,
        RemoteTree tree,
        SyncPaths paths,
        CancellationToken cancel)
    {
        var report = new AssetReport();
        var prefix = RepoPrefix(paths);

        foreach (var relative in ReferencedBy(document))
        {
            var repoPath = prefix + relative;

            if (tree.TokenFor(repoPath) is not null)
            {
                continue;
            }

            var local = AssetStore.ResolveExisting(boardPath, relative);

            if (local is null)
            {
                report.Absent.Add(relative);

                continue;
            }

            var bytes = File.ReadAllBytes(local);

            if (bytes.Length > MaxCarriedBytes)
            {
                report.Refused.Add((relative, $"{bytes.Length} 字节，超过这一端能搬的 {MaxCarriedBytes} 字节"));

                continue;
            }

            // Null token means "create": the path was not in the tree a moment ago. If another
            // device put one there in between, the answer is a refusal we report, not a
            // overwrite of a picture we never looked at.
            var written = await remote.WriteAsync(repoPath, bytes, null, cancel);

            switch (written.Outcome)
            {
                case WriteOutcome.Written:
                    report.Uploaded.Add(relative);

                    break;

                case WriteOutcome.Conflict:
                    report.Refused.Add((relative, "这一发期间远端已经有了同名的图，没有覆盖它"));

                    break;

                default:
                    report.Refused.Add((relative, written.Detail ?? written.Outcome.ToString()));

                    break;
            }
        }

        return report;
    }

    /// <summary>
    /// Takes away the pictures this board no longer names, from the far side and from this
    /// machine. Called only after the board that stopped naming them was accepted, so a
    /// refused round can never leave a still-displayed picture deleted.
    /// </summary>
    /// <remarks>
    /// One folder can hold several boards and they share its assets, so "my board stopped
    /// pointing at it" is not yet "nothing points at it". The authority for that is the far
    /// side — every device converges there — with this machine's own board files checked
    /// beside it, because a board that was never pushed has no remote copy to be consulted.
    /// A sibling that cannot be read counts as still using it: an unfinished collection costs
    /// a file, a wrong one costs somebody's picture.
    /// </remarks>
    public static async Task<AssetReport> ReapAsync(
        IRemoteFiles remote,
        string boardPath,
        CanvasDocument document,
        RemoteTree tree,
        SyncPaths paths,
        IReadOnlyList<string> dropped,
        CancellationToken cancel)
    {
        var report = new AssetReport();

        if (dropped.Count == 0)
        {
            return report;
        }

        var prefix = RepoPrefix(paths);
        var (usedFar, unreadable) = await FarSiblingsAsync(remote, tree, paths, cancel);
        var usedHere = new HashSet<string>(ReferencedBy(document), StringComparer.Ordinal);

        foreach (var relative in dropped)
        {
            if (usedHere.Contains(relative))
            {
                report.Held.Add((relative, "这块板又引用回它了，墓碑作废"));

                continue;
            }

            if (usedFar.Contains(relative))
            {
                report.Held.Add((relative, "仓库里另一块板还在引用它"));

                continue;
            }

            if (unreadable is not null)
            {
                report.Held.Add((relative, unreadable));
                report.Retry.Add(relative);

                continue;
            }

            if (LocalSiblingsUse(boardPath, relative))
            {
                report.Held.Add((relative, "这台机器上另一块板还在引用它"));

                continue;
            }

            var repoPath = prefix + relative;
            var token = tree.TokenFor(repoPath);
            var removedFar = false;

            if (token is not null)
            {
                var outcome = await remote.DeleteAsync(repoPath, token, cancel);

                if (outcome is not (WriteOutcome.Written or WriteOutcome.Missing))
                {
                    report.Held.Add((relative, $"远端不收这一发删除：{outcome}"));
                    report.Retry.Add(relative);

                    continue;
                }

                removedFar = outcome == WriteOutcome.Written;
            }

            var local = AssetStore.ResolveExisting(boardPath, relative);

            if (local is null)
            {
                if (removedFar)
                {
                    report.Deleted.Add(relative);
                }

                continue;
            }

            if (!TryDeleteFile(local))
            {
                report.Held.Add((relative, removedFar
                    ? "远端那份已经清了，本地这份删不掉（被占用），下次同步再试"
                    : "本地这份删不掉（被占用），下次同步再试"));
                report.Retry.Add(relative);

                continue;
            }

            report.Deleted.Add(relative);
        }

        return report;
    }

    /// <summary>
    /// What the other board files in this folder point at, read from the far side. The board
    /// this round is publishing is skipped: it is allowed to have just dropped the picture,
    /// and its own references are checked where that is asked.
    /// </summary>
    private static async Task<(HashSet<string> Used, string? Unreadable)> FarSiblingsAsync(
        IRemoteFiles remote,
        RemoteTree tree,
        SyncPaths paths,
        CancellationToken cancel)
    {
        var prefix = RepoPrefix(paths);

        var used = new HashSet<string>(StringComparer.Ordinal);
        var unreadable = false;

        foreach (var entry in tree.Entries)
        {
            if (!entry.Path.StartsWith(prefix, StringComparison.Ordinal)
                || !entry.Path.EndsWith(".canvas", StringComparison.OrdinalIgnoreCase)
                || string.Equals(entry.Path, paths.DocPath, StringComparison.Ordinal))
            {
                continue;
            }

            var file = await remote.ReadAsync(entry.Path, cancel);

            if (file is null)
            {
                continue;
            }

            try
            {
                foreach (var relative in ReferencedBy(CanvasDocument.Parse(Encoding.UTF8.GetString(file.Content), entry.Path)))
                {
                    used.Add(relative);
                }
            }
            catch (CanvasDocumentException)
            {
                unreadable = true;
            }
        }

        return (used, unreadable ? "仓库里另一块板读不懂，这一发不清图" : null);
    }

    /// <summary>Whether a board file beside this one, on this machine, points at the picture.</summary>
    private static bool LocalSiblingsUse(string boardPath, string relative)
    {
        var directory = AssetStore.BoardDirectory(boardPath);

        foreach (var candidate in Directory.EnumerateFiles(directory, "*.canvas"))
        {
            if (!candidate.EndsWith(".canvas", StringComparison.OrdinalIgnoreCase)
                || string.Equals(Path.GetFullPath(candidate), Path.GetFullPath(boardPath), StringComparison.Ordinal))
            {
                continue;
            }

            try
            {
                if (ReferencedBy(CanvasDocument.Parse(File.ReadAllText(candidate), candidate)).Contains(relative))
                {
                    return true;
                }
            }
            catch (CanvasDocumentException)
            {
                // Unreadable means unknown, and unknown means the picture stays.
                return true;
            }
        }

        return false;
    }

    private static bool TryDeleteFile(string path)
    {
        try
        {
            File.Delete(path);

            return true;
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>
    /// Same shape the board file uses: write beside, then rename over. A picture left half
    /// written by a killed process would otherwise sit there under the name its own content
    /// hash is supposed to guarantee.
    /// </summary>
    private static void WriteAtomically(string path, byte[] bytes)
    {
        var directory = Path.GetDirectoryName(path);

        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var temporary = $"{path}.{Environment.ProcessId}.tmp";

        try
        {
            using (var stream = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
            {
                stream.Write(bytes);
                stream.Flush(flushToDisk: true);
            }

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
                    // A leftover .tmp is noise, not a lost picture.
                }
            }
        }
    }
}
