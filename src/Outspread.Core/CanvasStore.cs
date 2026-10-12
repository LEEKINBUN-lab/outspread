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
    public const string MutexName = @"Global\outspread.canvas";

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
    /// Reads the file as it is on disk right now, applies the patch to that, and
    /// publishes. Nodes an agent added in the meantime survive; the patch is only
    /// what the human actually did.
    /// </summary>
    public static (IReadOnlyList<PatchReport> Reports, byte[] WrittenHash) SavePatch(string path, DocPatch patch)
    {
        var reports = new List<PatchReport>();

        if (patch.IsEmpty)
        {
            return (reports, Array.Empty<byte>());
        }

        var writtenHash = Transact(path, _ =>
        {
            var onDisk = File.Exists(path)
                ? CanvasDocument.Parse(ReadAllText(path), path)
                : CanvasDocument.CreateNew();

            reports.AddRange(PatchApplier.Apply(onDisk, patch));

            return onDisk.ToJson();
        });

        return (reports, writtenHash);
    }

    public static string HashHex(byte[] hash) => Convert.ToHexString(hash).ToLowerInvariant();

    /// <summary>For recognising this program's own write in file-system notifications.</summary>
    public static byte[] Sha256Of(byte[] bytes) => SHA256.HashData(bytes);

    /// <summary>
    /// Runs <paramref name="serialize"/> while holding the write lock, then publishes
    /// whatever it returns. Returns the hash of the bytes written, so a caller can
    /// recognise its own write in the file-system notifications it gets back.
    /// </summary>
    public static byte[] Transact(string path, Func<string, string> serialize)
    {
        var directory = Path.GetDirectoryName(Path.GetFullPath(path));

        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        using var mutex = new Mutex(initiallyOwned: false, MutexName);
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

            var text = serialize(File.Exists(path) ? ReadAllText(path) : string.Empty);
            var bytes = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false).GetBytes(text);

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
