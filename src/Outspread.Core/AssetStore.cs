using System.Security.Cryptography;

namespace Outspread.Core;

/// <summary>
/// Images that live next to a board, named by what they contain.
/// </summary>
/// <remarks>
/// Content-addressed names do three things at once: pasting the same screenshot
/// twice stays one file, a git diff of the folder reads as added or removed
/// pictures rather than churn, and an agent can point a node at a file it wrote
/// itself without inventing a naming scheme.
/// </remarks>
public static class AssetStore
{
    public const string DirectoryName = "assets";

    private const int HashPrefixLength = 12;

    public static string BoardDirectory(string boardPath)
        => Path.GetDirectoryName(Path.GetFullPath(boardPath)) ?? ".";

    public static string AssetsDirectory(string boardPath)
        => Path.Combine(BoardDirectory(boardPath), DirectoryName);

    /// <summary>Relative path as it goes into a node's <c>file</c>, forward slashes.</summary>
    public static string RelativeName(ReadOnlySpan<byte> content, string extension)
    {
        var hash = Convert.ToHexString(SHA256.HashData(content)).ToLowerInvariant();

        return $"{DirectoryName}/img_{hash[..HashPrefixLength]}{NormalizeExtension(extension)}";
    }

    /// <summary>
    /// Writes the image if it is new. Returns the relative path and whether the file
    /// already held these exact bytes.
    /// </summary>
    /// <remarks>
    /// The extension is whatever the bytes actually are. Naming a JPEG "img_x.png" would
    /// make the document lie about its own contents, and re-encoding a photo to PNG to
    /// avoid that costs about five times the space.
    /// </remarks>
    public static string WriteImage(string boardPath, byte[] content, string extension, out bool alreadyPresent)
    {
        if (content.Length == 0)
        {
            throw new ArgumentException("refusing to store an empty image", nameof(content));
        }

        var relative = RelativeName(content, extension);
        var target = Path.Combine(BoardDirectory(boardPath), relative);

        Directory.CreateDirectory(Path.GetDirectoryName(target)!);

        if (File.Exists(target))
        {
            alreadyPresent = true;
            return relative;
        }

        File.WriteAllBytes(target, content);
        alreadyPresent = false;
        return relative;
    }

    private static string NormalizeExtension(string extension)
    {
        if (string.IsNullOrWhiteSpace(extension))
        {
            return ".png";
        }

        var cleaned = extension.TrimStart('.').ToLowerInvariant();

        return "." + (cleaned.Length is > 0 and <= 5 ? cleaned : "png");
    }

    public static string? ResolveExisting(string boardPath, string relative)
    {
        if (!IsSafeRelativePath(relative))
        {
            return null;
        }

        var full = Path.GetFullPath(Path.Combine(BoardDirectory(boardPath), relative));

        return full.StartsWith(BoardDirectory(boardPath) + Path.DirectorySeparatorChar, StringComparison.Ordinal)
            && File.Exists(full)
                ? full
                : null;
    }

    /// <summary>
    /// Syntactic guard: a node's path must stay inside the board folder. Without it
    /// a document — including one an agent wrote — could aim the board at any file on
    /// this machine, and the app would happily read it.
    /// </summary>
    public static bool IsSafeRelativePath(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        if (Path.IsPathRooted(value) || value.Contains(':') || value.StartsWith('/') || value.StartsWith('\\'))
        {
            return false;
        }

        foreach (var segment in value.Split('/', '\\'))
        {
            if (segment == "..")
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Normalises whatever separators an agent used into the form this board writes,
    /// and refuses anything unsafe rather than guessing at a correction.
    /// </summary>
    public static string? NormalizeRelativePath(string? value)
    {
        if (!IsSafeRelativePath(value))
        {
            return null;
        }

        return value!.Replace('\\', '/');
    }

    /// <summary>
    /// Files in the assets folder that no node points at any more. Reported, never
    /// deleted: a board is the kind of thing where "unused" often means "not wired up
    /// yet", and an agent may be mid-way through a batch of pastes.
    /// </summary>
    public static IReadOnlyList<string> UnreferencedImages(string boardPath, CanvasDocument document)
    {
        var referenced = new HashSet<string>(StringComparer.Ordinal);

        foreach (var node in document.Nodes)
        {
            var normalized = NormalizeRelativePath(node.FilePath);

            if (normalized is not null)
            {
                referenced.Add(normalized);
            }
        }

        var directory = AssetsDirectory(boardPath);

        if (!Directory.Exists(directory))
        {
            return [];
        }

        var orphans = new List<string>();

        foreach (var file in Directory.EnumerateFiles(directory))
        {
            var relative = $"{DirectoryName}/{Path.GetFileName(file)}";

            if (!referenced.Contains(relative))
            {
                orphans.Add(relative);
            }
        }

        return orphans;
    }
}
