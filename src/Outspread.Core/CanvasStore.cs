using System.Security.Cryptography;
using System.Text;

namespace Outspread.Core;

/// <summary>
/// Reads and writes board files.
/// </summary>
/// <remarks>
/// Everything that changes a document goes through <see cref="Transact"/>: a named
/// mutex held across read-modify-write, then an atomic same-volume move. The board,
/// the CLI and an agent each write without a lock step, and without this the
/// interleaving produces a half-written file or a lost edit.
///
/// A mutex timeout is raised as an error rather than swallowed. Silent returns on
/// contention are how a drag can look like it saved and not have saved.
/// </remarks>
public static class CanvasStore
{
    /// <summary>
    /// The write lock is per board file, not per machine.
    ///
    /// <para>
    /// It used to be one global name for every board, which is a real defect and not a detail: the
    /// CLI writing board A and the window writing board B (or two boards open at once, or a test
    /// suite holding one board's lock for six seconds) serialise behind each other and the loser
    /// gets "nothing was saved, try again" after <see cref="LockTimeout"/>. Mutual exclusion is only
    /// ever wanted between writers of *the same file*, so the name is derived from the file.
    /// </para>
    /// </summary>
    public static string MutexNameFor(string path)
    {
        var full = Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar).ToUpperInvariant();
        var digest = SHA256.HashData(Encoding.UTF8.GetBytes(full));

        return $@"Global\outspread.canvas-{Convert.ToHexString(digest[..8]).ToLowerInvariant()}";
    }

    public const string Extension = ".canvas";

    private static readonly TimeSpan LockTimeout = TimeSpan.FromSeconds(5);

    public static bool IsBoardPath(string? path)
        => !string.IsNullOrEmpty(path)
        && string.Equals(Path.GetExtension(path), Extension, StringComparison.OrdinalIgnoreCase);

    /// <summary>Path of the app-owned sidecar that holds the camera and per-node font sizes.</summary>
    public static string SidecarPath(string boardPath) => boardPath + ".view.json";

    public static string ReadAllText(string path)
    {
        using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete);

        using var reader = new StreamReader(stream, Encoding.UTF8);
        return reader.ReadToEnd();
    }

    public static CanvasDocument Load(string path) => CanvasDocument.Parse(ReadAllText(path), path);

    public static CanvasDocument LoadOrCreate(string path)
    {
        if (!File.Exists(path))
        {
            var document = CanvasDocument.CreateNew();
            Write(path, document.ToJson());
            return document;
        }

        return Load(path);
    }

    /// <summary>Replaces the whole file. Use only for a document nobody else touched since it was read.</summary>
    public static byte[] Write(string path, string text)
    {
        return Transact(path, _ => text);
    }

    /// <summary>
    /// 把一份快照写回板子。走的是同一条原子写，但**先无条件存当前**：恢复正是"万一我选错了"
    /// 代价最大的那一刻，所以间隔与同内容两道闸在这里绕开。
    /// </summary>
    public static byte[] Restore(string path, string text) => Transact(path, _ => text, forceSnapshot: true);

    /// <summary>
    /// Reads the file as it is on disk right now, applies the patch to that, and
    /// publishes. Nodes an agent added in the meantime survive; the patch is only
    /// what the human actually did.
    /// </summary>
    /// <returns>
    /// <paramref name="WrittenJson"/> 是这一发真正落盘的那份 JSON：调用方要"盘上现值"
    /// （界面拿它当 <c>_baseJson</c>）就用它，不必再回读一遍自己刚写的文件——锁内现成的
    /// <c>current</c> 已经读过、析过，丢掉重读重析是白花（一次存盘省两读）。
    /// </returns>
    public static (IReadOnlyList<PatchReport> Reports, byte[] WrittenHash, string WrittenJson) SavePatch(string path, DocPatch patch)
    {
        var reports = new List<PatchReport>();

        if (patch.IsEmpty)
        {
            return (reports, Array.Empty<byte>(), string.Empty);
        }

        string writtenJson = string.Empty;

        var writtenHash = Transact(path, current =>
        {
            var onDisk = current.Length > 0
                ? CanvasDocument.Parse(current, path)
                : CanvasDocument.CreateNew();

            reports.AddRange(PatchApplier.Apply(onDisk, patch));

            writtenJson = onDisk.ToJson();

            return writtenJson;
        });

        return (reports, writtenHash, writtenJson);
    }

    public static string HashHex(byte[] hash) => Convert.ToHexString(hash).ToLowerInvariant();

    /// <summary>For recognising this program's own write in file-system notifications.</summary>
    public static byte[] Sha256Of(byte[] bytes) => SHA256.HashData(bytes);

    /// <summary>
    /// Runs <paramref name="serialize"/> while holding the write lock, then publishes
    /// whatever it returns. Returns the hash of the bytes written, so a caller can
    /// recognise its own write in the file-system notifications it gets back.
    /// <para>
    /// Inside the lock and before the atomic move, the bytes about to be replaced are
    /// snapshotted (<see cref="Snapshots"/>). That placement is the whole point: outside
    /// the lock a snapshot can record a state that never existed on disk, and after the
    /// move the state it should have recorded is already gone.
    /// </para>
    /// </summary>
    public static byte[] Transact(string path, Func<string, string> serialize, bool forceSnapshot = false)
    {
        var directory = Path.GetDirectoryName(Path.GetFullPath(path));

        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        using var mutex = new Mutex(initiallyOwned: false, MutexNameFor(path));
        var held = false;

        try
        {
            try
            {
                held = mutex.WaitOne(LockTimeout);
            }
            catch (AbandonedMutexException)
            {
                held = true;
            }

            if (!held)
            {
                throw new IOException(
                    $"the board file is being written by something else and did not release within " +
                    $"{(int)LockTimeout.TotalSeconds} s; nothing was saved, try again");
            }

            var current = File.Exists(path) ? ReadAllText(path) : string.Empty;
            var text = serialize(current);
            var bytes = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false).GetBytes(text);

            // 要被替换掉的那一份，先存下来（间隔与同内容两道闸在 Snapshots 里）。
            Snapshots.TryPreserve(path, current, DateTimeOffset.Now, force: forceSnapshot);

            var temporary = $"{path}.{Environment.ProcessId}.tmp";

            try
            {
                using (var stream = new FileStream(
                    temporary,
                    FileMode.Create,
                    FileAccess.Write,
                    FileShare.None,
                    4096,
                    FileOptions.WriteThrough))
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
                        // A leftover .tmp is noise, not a lost board.
                    }
                }
            }

            return SHA256.HashData(bytes);
        }
        finally
        {
            if (held)
            {
                mutex.ReleaseMutex();
            }
        }
    }
}
