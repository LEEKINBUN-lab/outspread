using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace Outspread.Core;

/// <summary>
/// File copies this board owns. Bytes are content-addressed, the original name is kept for
/// recognition, and nothing here ever points outside the board folder.
/// </summary>
public static class AttachmentStore
{
    public const string DirectoryName = "attachments";

    /// <summary>At or below this size a dropped file is copied without asking.</summary>
    public const long AutoCacheLimitBytes = 10L * 1024 * 1024;

    private const int HashPrefixLength = 12;

    private const int MaxNameLength = 64;

    public static bool NeedsPrompt(long bytes) => bytes > AutoCacheLimitBytes;

    public static string BoardDirectory(string boardPath)
        => Path.GetDirectoryName(Path.GetFullPath(boardPath)) ?? ".";

    public static string AttachmentsDirectory(string boardPath)
        => Path.Combine(BoardDirectory(boardPath), DirectoryName);

    public static string FormatSize(long bytes)
    {
        if (bytes < 1024)
        {
            return $"{bytes} B";
        }

        if (bytes < 1024 * 1024)
        {
            return (bytes / 1024d).ToString("0.#", CultureInfo.InvariantCulture) + " KB";
        }

        if (bytes < 1024L * 1024 * 1024)
        {
            return (bytes / (1024d * 1024)).ToString("0.#", CultureInfo.InvariantCulture) + " MB";
        }

        return (bytes / (1024d * 1024 * 1024)).ToString("0.#", CultureInfo.InvariantCulture) + " GB";
    }

    public static string? SafeFileName(string? name)
    {
        var value = Path.GetFileName((name ?? string.Empty).Trim());

        if (value.Length == 0)
        {
            return null;
        }

        var invalid = Path.GetInvalidFileNameChars();
        var builder = new StringBuilder(value.Length);

        foreach (var character in value)
        {
            builder.Append(Array.IndexOf(invalid, character) >= 0 || character < ' ' ? '_' : character);
        }

        var cleaned = builder.ToString().Trim(' ', '.');
        var extension = Path.GetExtension(cleaned);
        var stem = Path.GetFileNameWithoutExtension(cleaned);

        if (stem.Length == 0)
        {
            stem = "file";
        }

        var room = Math.Max(8, MaxNameLength - extension.Length);

        return stem.Length > room ? stem[..room] + extension : stem + extension;
    }

    public static string RelativeName(string contentHash, string? originalName)
    {
        var name = SafeFileName(originalName) ?? "file";

        return $"{DirectoryName}/{contentHash[..Math.Min(HashPrefixLength, contentHash.Length)]}_{name}";
    }

    /// <summary>
    /// Returns the board-relative path for a file. Files already inside the board folder are
    /// left where they are; everything else is copied under <c>attachments/</c>.
    /// </summary>
    public static string WriteFile(string boardPath, string sourcePath, out bool reused)
    {
        reused = false;

        var boardDirectory = Path.GetFullPath(BoardDirectory(boardPath));
        var source = Path.GetFullPath(sourcePath);
        var relativeSource = Path.GetRelativePath(boardDirectory, source);

        if (!relativeSource.StartsWith("..", StringComparison.Ordinal)
            && !Path.IsPathRooted(relativeSource)
            && File.Exists(source))
        {
            var normalized = relativeSource.Replace('\\', '/');

            if (AssetStore.IsSafeRelativePath(normalized))
            {
                reused = true;
                return normalized;
            }
        }

        string hash;

        using (var stream = File.OpenRead(source))
        {
            hash = Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
        }

        var relative = RelativeName(hash, Path.GetFileName(source));
        var target = Path.GetFullPath(Path.Combine(boardDirectory, relative));

        Directory.CreateDirectory(Path.GetDirectoryName(target)!);

        if (File.Exists(target))
        {
            reused = true;
            return relative;
        }

        File.Copy(source, target);
        return relative;
    }

    public static string WriteBytes(string boardPath, byte[] content, string? originalName, out bool reused)
    {
        if (content.Length == 0)
        {
            throw new ArgumentException("refusing to store an empty file", nameof(content));
        }

        var hash = Convert.ToHexString(SHA256.HashData(content)).ToLowerInvariant();
        var relative = RelativeName(hash, originalName);
        var target = Path.Combine(BoardDirectory(boardPath), relative);

        Directory.CreateDirectory(Path.GetDirectoryName(target)!);

        if (File.Exists(target))
        {
            reused = true;
            return relative;
        }

        File.WriteAllBytes(target, content);
        reused = false;
        return relative;
    }

    public static string? ResolveExisting(string boardPath, string? relative)
        => AssetStore.ResolveExisting(boardPath, relative ?? string.Empty);

    public static bool IsOwnedPath(string? relative)
    {
        var normalized = AssetStore.NormalizeRelativePath(relative);

        return normalized is not null
            && normalized.StartsWith(DirectoryName + "/", StringComparison.OrdinalIgnoreCase);
    }

    public static IReadOnlyList<string> OwnedPaths(CanvasDocument document)
    {
        var paths = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (var node in document.Nodes)
        {
            if (node.Kind != CanvasNodeKind.File)
            {
                continue;
            }

            var normalized = AssetStore.NormalizeRelativePath(node.FilePath);

            if (normalized is not null && IsOwnedPath(normalized) && seen.Add(normalized))
            {
                paths.Add(normalized);
            }
        }

        return paths;
    }

    public static bool IsReferenced(CanvasDocument document, string relative)
    {
        var normalized = AssetStore.NormalizeRelativePath(relative);

        return normalized is not null && document.Nodes.Any(node =>
            node.Kind == CanvasNodeKind.File
            && string.Equals(AssetStore.NormalizeRelativePath(node.FilePath), normalized, StringComparison.Ordinal));
    }

    /// <summary>
    /// Boards that share this folder can point at the same content-addressed copy. A deletion
    /// may only take the file away when no sibling board still names it.
    /// </summary>
    public static bool IsReferencedInSiblingBoards(string boardPath, string relative, string? excludingBoardPath = null)
    {
        var normalized = AssetStore.NormalizeRelativePath(relative);

        if (normalized is null)
        {
            return false;
        }

        var directory = BoardDirectory(boardPath);
        var excluded = excludingBoardPath is null ? null : Path.GetFullPath(excludingBoardPath);

        foreach (var candidate in Directory.EnumerateFiles(directory, "*.canvas", SearchOption.TopDirectoryOnly))
        {
            if (excluded is not null && string.Equals(Path.GetFullPath(candidate), excluded, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            try
            {
                var document = CanvasDocument.Parse(File.ReadAllText(candidate), candidate);

                if (IsReferenced(document, normalized))
                {
                    return true;
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or CanvasDocumentException)
            {
                // An unreadable sibling must not become a reason to delete a file another board
                // might still use; the cautious answer is "it may be referenced".
                return true;
            }
        }

        return false;
    }

    public static string? OriginalNameFromRelative(string? relative)
    {
        var name = Path.GetFileName(AssetStore.NormalizeRelativePath(relative) ?? string.Empty);

        if (name.Length == 0)
        {
            return null;
        }

        var separator = name.IndexOf('_');

        return separator > 0 && separator < name.Length - 1 ? name[(separator + 1)..] : name;
    }
}
