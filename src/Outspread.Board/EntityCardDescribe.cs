using System.Diagnostics;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Outspread.Core;

namespace Outspread.Board;

/// <summary>Resolves a node into the words and targets its header shows.</summary>
public sealed partial class MainWindow
{
    private EntityCardInfo? DescribeEntity(CanvasNode node)
    {
        var kind = EntityCards.KindOf(node);

        if (kind == EntityCardKind.None)
        {
            return null;
        }

        if (kind == EntityCardKind.Table)
        {
            // A table card shows the grid and the words under it — nothing else. No tile, no badge,
            // no "N 列 × M 行": he asked for a table, not a report about one.
            return TableGrid.FromJson(node.Json[EntityCards.TableField]) is { } table
                ? new EntityCardInfo(EntityCardKind.Table, string.Empty, string.Empty, string.Empty, EntityBadgeTone.Neutral, null, [], null, null, table)
                : null;
        }

        if (kind is EntityCardKind.Web or EntityCardKind.Link)
        {
            if (!Uri.TryCreate(node.Url, UriKind.Absolute, out var uri))
            {
                return null;
            }

            var web = kind == EntityCardKind.Web;
            var host = WebAddress.Host(uri);

            return new EntityCardInfo(
                kind,
                node.Label ?? (web ? WebAddress.FallbackTitle(uri) : host),
                web ? host : (uri.Scheme.Length > 0 ? uri.Scheme : "链接"),
                web ? "网页" : "链接",
                web ? EntityBadgeTone.Neutral : EntityBadgeTone.Linked,
                web ? "只存标题与域名，不抓快照；删卡不动网页本体" : "外部链接；删卡不动目标",
                [],
                null,
                web ? uri : null,
                null);
        }

        var target = EntityTargetPath(node);
        var existing = target is not null && (kind == EntityCardKind.Folder ? Directory.Exists(target) : File.Exists(target));
        var linked = node.Kind == CanvasNodeKind.Link;
        var owned = node.Kind == CanvasNodeKind.File && AttachmentStore.IsOwnedPath(node.FilePath);
        var title = node.Label;

        if (string.IsNullOrWhiteSpace(title))
        {
            title = target is not null
                ? Path.GetFileName(target.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))
                : EntityCards.DisplayName(node);
        }

        if (kind == EntityCardKind.Folder)
        {
            var media = new List<EntityThumb>();
            var count = 0;

            if (existing)
            {
                try
                {
                    var entries = Directory.EnumerateFileSystemEntries(target!).Take(200).ToList();
                    count = entries.Count;

                    // 预览用缩略图，不用子项名字标签：一个装图片的文件夹，看一眼就知道里头是什么。
                    media.AddRange(entries
                        .Where(entry => EntityCards.LooksLikeImage(entry) || EntityCards.LooksLikeVideo(entry))
                        .OrderBy(entry => entry, StringComparer.CurrentCultureIgnoreCase)
                        .Select(entry => new EntityThumb(entry, EntityCards.LooksLikeVideo(entry)))
                        .Take(4));
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    existing = false;
                }
            }

            return new EntityCardInfo(
                kind,
                title ?? "文件夹",
                existing ? $"{count}{(count >= 200 ? "+" : string.Empty)} 项" : "原位置找不到",
                existing ? "链接原件" : "找不到",
                existing ? EntityBadgeTone.Linked : EntityBadgeTone.Missing,
                existing ? LinkedTip : MissingTip(target),
                media,
                target,
                null,
                null);
        }

        string meta;
        string badge;
        EntityBadgeTone tone;
        string? tip = null;

        if (!existing)
        {
            meta = "原位置找不到";
            badge = "找不到";
            tone = EntityBadgeTone.Missing;
            tip = MissingTip(target);
        }
        else
        {
            var info = new FileInfo(target!);
            var extension = Path.GetExtension(target!).TrimStart('.').ToUpperInvariant();

            meta = $"{(extension.Length > 0 ? extension : "文件")} · {AttachmentStore.FormatSize(info.Length)} · {ModifiedLabel(info.LastWriteTime)}";

            if (owned)
            {
                badge = "原件副本";
                tone = EntityBadgeTone.Owned;
                tip = OwnedTip;
            }
            else if (linked)
            {
                badge = "链接原件";
                tone = EntityBadgeTone.Linked;
                tip = LinkedTip;
            }
            else
            {
                badge = "本板文件";
                tone = EntityBadgeTone.Neutral;
                tip = "文件就在这块板子的目录里；删卡会连着它一起进回收站";
            }
        }

        return new EntityCardInfo(
            kind,
            title ?? "文件",
            meta,
            badge,
            tone,
            tip,
            [],
            target,
            null,
            null);
    }

    // 警示不再常驻卡面（他 10-25：太挤）——同一句话改停在牌子上才说。
    private const string OwnedTip = "根据原文件创建的副本；删掉这张卡时，这份副本一并移到回收站";

    private const string LinkedTip = "只放链接，原件还在原处；原件被移动或删除后这张卡会失效";

    private static string MissingTip(string? target)
        => target is { Length: > 0 } path
            ? $"原件不在这里了：{path}"
            : "原件不在这里了，卡片还在";

    /// <summary>The board a node's relative file path resolves against.</summary>
    private string EntityBoardPath(CanvasNode node)
    {
        if (_boardPath is null)
        {
            return UnfiledPath;
        }

        if (!IsAllNotes(_boardPath))
        {
            return _boardPath;
        }

        return BoardMerge.TryUnmerge(node.Id, out var owner, out _) ? owner : UnfiledPath;
    }

    private string? EntityTargetPath(CanvasNode node)
    {
        if (node.Kind == CanvasNodeKind.File)
        {
            var relative = AssetStore.NormalizeRelativePath(node.FilePath);

            if (relative is null)
            {
                return null;
            }

            var board = EntityBoardPath(node);
            var resolved = AssetStore.ResolveExisting(board, relative);

            return resolved ?? Path.GetFullPath(Path.Combine(AssetStore.BoardDirectory(board), relative));
        }

        return Uri.TryCreate(node.Url, UriKind.Absolute, out var uri) && uri.IsFile
            ? uri.LocalPath
            : null;
    }

    private void OpenEntity(string id)
    {
        if (NodeOf(id) is not { } node)
        {
            return;
        }

        var kind = EntityCards.KindOf(node);
        var name = EntityCards.DisplayName(node);

        try
        {
            if (kind is EntityCardKind.Web or EntityCardKind.Link
                && Uri.TryCreate(node.Url, UriKind.Absolute, out var uri)
                && uri.Scheme is "http" or "https")
            {
                Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true });
                return;
            }

            var target = kind is EntityCardKind.File or EntityCardKind.Folder ? EntityTargetPath(node) : null;

            if (target is null)
            {
                Say($"「{name}」没有可打开的位置");
                return;
            }

            if (kind == EntityCardKind.Folder)
            {
                if (!Directory.Exists(target))
                {
                    Say($"找不到「{name}」");
                    return;
                }
            }
            else if (!File.Exists(target))
            {
                Say($"找不到「{name}」");
                return;
            }

            Process.Start(new ProcessStartInfo(target) { UseShellExecute = true });
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
        {
            Say($"无法打开「{name}」：{ex.Message}");
        }
    }

    /// <summary>右键菜单那两枚动作对着的那一格：只选了一格、且它确实是个实体卡。</summary>
    private EntityCardInfo? SelectedEntity()
        => _selection.Count == 1 && NodeOf(_selection.First()) is { } node ? DescribeEntity(node) : null;

    private void OpenSelectedEntity()
    {
        if (_selection.Count == 1 && SelectedEntity()?.CanOpen == true)
        {
            OpenEntity(_selection.First());
        }
    }

    private void RevealSelectedEntity()
    {
        if (_selection.Count == 1 && SelectedEntity()?.CanReveal == true)
        {
            RevealEntity(_selection.First());
        }
    }

    /// <summary>打开所在文件夹并选中原件——链接型卡片需要的是「它在哪儿」，不是「它的内容」。</summary>
    private void RevealEntity(string id)
    {
        if (NodeOf(id) is not { } node)
        {
            return;
        }

        var name = EntityCards.DisplayName(node);
        var target = EntityTargetPath(node);

        if (target is null || !(File.Exists(target) || Directory.Exists(target)))
        {
            Say($"找不到「{name}」");
            return;
        }

        try
        {
            var full = Path.GetFullPath(target.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));

            Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{full}\"") { UseShellExecute = false });
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
        {
            Say($"打不开所在文件夹：{ex.Message}");
        }
    }

    private static string ModifiedLabel(DateTime value)
    {
        var local = value.ToLocalTime();
        var today = DateTime.Today;

        if (local.Date == today)
        {
            return "今天";
        }

        if (local.Date == today.AddDays(-1))
        {
            return "昨天";
        }

        return $"{local.Month} 月 {local.Day} 日";
    }

    private static bool InsideButton(object? source)
    {
        var current = source as DependencyObject;

        while (current is not null)
        {
            if (current is Button)
            {
                return true;
            }

            current = VisualTreeHelper.GetParent(current);
        }

        return false;
    }
}
