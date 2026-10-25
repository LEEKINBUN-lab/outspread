using System.Text.RegularExpressions;

namespace Outspread.Core;

/// <summary>
/// 退役的默认板名：板子删了以后同步账本还留着（用来防止“删了又被另一端传回来”），
/// 但远端那份已经不在时，账本没有继续存在的理由，名字应当释放，否则新建默认名会
/// 未命名板3、4、5 一路涨下去（他 10-25 问的那条）。
/// </summary>
public static class SyncHousekeeping
{
    private static readonly Regex Untitled = new("^未命名板\\d+$", RegexOptions.CultureInvariant);

    /// <summary>
    /// 纯选择/删除那一半：<paramref name="tree"/> 是远端当前文件表；只释放"默认名 / 自检残留"、
    /// 不在 <paramref name="keepStems"/> 里、且远端已经没有对应板文件的账本目录。
    /// </summary>
    public static IReadOnlyList<string> ReleaseUntitledLedgers(
        RemoteTree tree,
        string root,
        IReadOnlyCollection<string> keepStems)
    {
        if (!Directory.Exists(root))
        {
            return [];
        }

        var keep = new HashSet<string>(keepStems, StringComparer.OrdinalIgnoreCase);
        var released = new List<string>();

        foreach (var folder in Directory.EnumerateDirectories(root))
        {
            var stem = Path.GetFileName(folder);

            if (keep.Contains(stem)
                || (!Untitled.IsMatch(stem) && !stem.StartsWith("selftest", StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            if (tree.TokenFor(SyncPaths.DocPathFor(stem)) is not null)
            {
                // 远端还有这块板：账本是"别让它被传回来"的凭据，连同名字一起留着。
                continue;
            }

            try
            {
                Directory.Delete(folder, recursive: true);
                released.Add(stem);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // 锁着就下一轮再试；释放失败不影响开板。
            }
        }

        return released;
    }

    /// <summary>联网那一半：读一次远端文件表，再按上面的规则释放。</summary>
    public static async Task<IReadOnlyList<string>> ReleaseUntitledLedgersAsync(
        IReadOnlyCollection<string> keepStems,
        CancellationToken cancel = default)
    {
        var config = SyncConfig.Load(SyncLayout.Root);

        if (config is null || !config.IsSet)
        {
            return [];
        }

        IRemoteFiles remote;

        try
        {
            remote = config.OpenRemote();
        }
        catch (Exception ex) when (ex is SyncSetupException or InvalidOperationException or IOException)
        {
            return [];
        }

        try
        {
            var tree = await remote.ReadTreeAsync(cancel);

            return tree is null ? [] : ReleaseUntitledLedgers(tree, SyncLayout.Root, keepStems);
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or InvalidOperationException or TaskCanceledException)
        {
            return [];
        }
        finally
        {
            if (remote is IDisposable disposable)
            {
                disposable.Dispose();
            }
        }
    }
}
