using Outspread.Core;

namespace Outspread.Board;

public sealed partial class MainWindow
{
    private IReadOnlyList<string> OwnedAttachmentsToRecycleAfterCardRemoval(IReadOnlyList<string> relatives)
    {
        if (_boardPath is null || IsAllNotes(_boardPath))
        {
            return [];
        }

        return relatives
            .Distinct(StringComparer.Ordinal)
            .Where(relative => !AttachmentStore.IsReferenced(_document, relative)
                && !AttachmentStore.IsReferencedInSiblingBoards(_boardPath, relative, _boardPath))
            .ToList();
    }

    private int RecycleOwnedAttachmentsAfterCardRemoval(IReadOnlyList<string> relatives)
    {
        var cleaned = 0;

        foreach (var relative in OwnedAttachmentsToRecycleAfterCardRemoval(relatives))
        {
            var full = AttachmentStore.ResolveExisting(_boardPath!, relative);

            if (full is not null && ShellRecycle.TryDeleteToRecycleBin(full))
            {
                cleaned++;
                Diagnostics.Write($"attachment recycled: {relative}");
            }
            else if (full is not null)
            {
                Diagnostics.Write($"attachment recycle failed: {full}");
            }
        }

        return cleaned;
    }

    private static int RecycleOwnedAttachmentsAfterBoardRemoval(string boardPath, IReadOnlyList<string> relatives)
    {
        var cleaned = 0;

        foreach (var relative in relatives.Distinct(StringComparer.Ordinal))
        {
            if (AttachmentStore.IsReferencedInSiblingBoards(boardPath, relative, boardPath))
            {
                continue;
            }

            var full = AttachmentStore.ResolveExisting(boardPath, relative);

            if (full is not null && ShellRecycle.TryDeleteToRecycleBin(full))
            {
                cleaned++;
                Diagnostics.Write($"board attachment recycled: {relative}");
            }
            else if (full is not null)
            {
                Diagnostics.Write($"board attachment recycle failed: {full}");
            }
        }

        return cleaned;
    }

    private static IReadOnlyList<string> OwnedAttachmentPathsOfBoardFile(string boardPath)
    {
        try
        {
            return AttachmentStore.OwnedPaths(CanvasDocument.Parse(CanvasStore.ReadAllText(boardPath), boardPath));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or CanvasDocumentException or System.Text.Json.JsonException)
        {
            Diagnostics.Write($"board attachments unreadable {boardPath}: {ex.Message}");
            return [];
        }
    }
}
