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

    public bool DidAnything
        => Uploaded.Count > 0 || Downloaded.Count > 0 || Refused.Count > 0 || Absent.Count > 0;
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
/// Deletions are not carried. A picture the other end removed stays here, unreferenced —
/// which is the same state it is in after a human deletes a note here, and it loses nothing
/// that is still displayed. Making a cross-device delete reliable needs tombstones, which the
/// format does not have and this does not invent (see docs/sync-contract.md).
///
/// Steady state costs no requests: the tree call a round already makes lists the assets, and
/// a picture present on both sides is not read.
/// </remarks>
public static class AssetSync
{
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
