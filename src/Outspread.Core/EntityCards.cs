using System.Text.Json.Nodes;

namespace Outspread.Core;

/// <summary>What a file/link node should look like on the board.</summary>
public enum EntityCardKind
{
    None,
    File,
    Folder,
    Web,
    Link,
    Table,
}

/// <summary>
/// Reads the entity a node represents. The JSON Canvas kind stays <c>file</c> or <c>link</c>;
/// this is the layer that tells a cached PDF from a folder or a web page.
/// </summary>
public static class EntityCards
{
    public const string EntityField = "entity";

    public const string TableField = "table";

    /// <summary>The free words under a table card. The table itself lives in <see cref="TableField"/>.</summary>
    public const string NoteField = "note";

    public static readonly string[] ImageExtensions =
        [".png", ".jpg", ".jpeg", ".gif", ".webp", ".bmp", ".ico", ".tiff"];

    public static readonly string[] VideoExtensions =
        [".mp4", ".mov", ".wmv", ".avi", ".mkv", ".webm", ".m4v"];

    public static bool LooksLikeImage(string? path) => HasExtension(path, ImageExtensions);

    public static bool LooksLikeVideo(string? path) => HasExtension(path, VideoExtensions);

    private static bool HasExtension(string? path, string[] extensions)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return false;
        }

        return Array.IndexOf(extensions, Path.GetExtension(path).ToLowerInvariant()) >= 0;
    }

    public static bool IsTable(CanvasNode node)
        => node.Json[TableField] is JsonObject table && table["rows"] is JsonArray;

    public static EntityCardKind KindOf(CanvasNode node)
    {
        if (IsTable(node))
        {
            return EntityCardKind.Table;
        }

        if (node.Kind == CanvasNodeKind.File)
        {
            return LooksLikeImage(node.FilePath) ? EntityCardKind.None : EntityCardKind.File;
        }

        if (node.Kind != CanvasNodeKind.Link)
        {
            return EntityCardKind.None;
        }

        var hint = node.GetString(EntityField);

        if (hint is "file" or "folder" or "web")
        {
            return hint switch
            {
                "file" => EntityCardKind.File,
                "folder" => EntityCardKind.Folder,
                _ => EntityCardKind.Web,
            };
        }

        if (!Uri.TryCreate(node.Url, UriKind.Absolute, out var uri))
        {
            return EntityCardKind.Link;
        }

        if (uri.Scheme is "http" or "https")
        {
            return EntityCardKind.Web;
        }

        if (uri.IsFile)
        {
            return Directory.Exists(uri.LocalPath) ? EntityCardKind.Folder : EntityCardKind.File;
        }

        return EntityCardKind.Link;
    }

    /// <summary>Can double-click open a text editor on this node?</summary>
    public static bool CanEditBody(CanvasNode node)
        => node.Kind switch
        {
            // A table card edits the words under it; the grid itself is edited cell by cell.
            CanvasNodeKind.Text => true,
            CanvasNodeKind.File => KindOf(node) == EntityCardKind.File,
            CanvasNodeKind.Link => KindOf(node) != EntityCardKind.None,
            _ => false,
        };

    /// <summary>The words under a table card. The grid lives in <see cref="TableField"/>, so these stay separate.</summary>
    public static string NoteOf(CanvasNode node) => node.GetString(NoteField) ?? string.Empty;

    public static void SetNote(CanvasNode node, string text) => node.SetString(NoteField, text);

    public static string DisplayName(CanvasNode node)
    {
        if (!string.IsNullOrWhiteSpace(node.Label))
        {
            return node.Label!.Trim();
        }

        var kind = KindOf(node);

        if (kind == EntityCardKind.Table)
        {
            return "表格";
        }

        if (node.Kind == CanvasNodeKind.File)
        {
            return Path.GetFileName(node.FilePath) is { Length: > 0 } name ? name : "文件";
        }

        if (node.Kind != CanvasNodeKind.Link || !Uri.TryCreate(node.Url, UriKind.Absolute, out var uri))
        {
            return node.Url ?? "链接";
        }

        if (uri.IsFile)
        {
            var leaf = Path.GetFileName(uri.LocalPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));

            return leaf.Length > 0 ? leaf : "文件";
        }

        return WebAddress.FallbackTitle(uri);
    }

    public static void SetHint(CanvasNode node, string hint) => node.SetString(EntityField, hint);

    public static string TypeLabel(CanvasNode node)
    {
        var kind = KindOf(node);

        if (kind == EntityCardKind.Table)
        {
            return "XLS";
        }

        if (kind == EntityCardKind.Folder)
        {
            return "DIR";
        }

        if (kind == EntityCardKind.Web)
        {
            return "WEB";
        }

        if (kind == EntityCardKind.File)
        {
            var path = node.FilePath ?? (Uri.TryCreate(node.Url, UriKind.Absolute, out var uri) ? uri.LocalPath : null);
            var extension = Path.GetExtension(path ?? string.Empty).TrimStart('.').ToUpperInvariant();

            return extension.Length is > 0 and <= 4 ? extension : "FILE";
        }

        return "LINK";
    }
}
