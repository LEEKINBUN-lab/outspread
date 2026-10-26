namespace Outspread.Core;

/// <summary>
/// 这块板子住在哪儿、用户从哪儿点开它。
/// </summary>
/// <remarks>
/// 应用是 unpackaged 的：没有安装程序，每次构建 exe 都换一条 <c>bin\...</c> 路径，
/// 而开始菜单要的是**不会随构建漂移**的那一条。所以有一个稳定目录
/// （<c>%LOCALAPPDATA%\Programs\Outspread</c>），快捷方式永远指向它；从 bin 里跑起来的那一发
/// 把稳定目录对齐到当前构建。用户数据不在这儿——板子按绝对路径记在 prefs 里，换目录不改数据。
/// </remarks>
public static class AppInstall
{
    public const string BoardExeName = "Outspread.Board.exe";

    public static string StableDirectory()
        => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Programs",
            "Outspread");

    public static string StableExePath() => Path.Combine(StableDirectory(), BoardExeName);

    /// <summary>开始菜单指哪儿：稳定目录里那份在就指它；还没有（第一次启动）就指当前这个 exe。</summary>
    public static string PreferredLaunchTarget(string runningExe, bool stableExeExists)
        => stableExeExists && runningExe.Length > 0 ? StableExePath() : runningExe;

    /// <summary>
    /// 要不要把这一份构建拷进稳定目录：目标不在、或两边时间戳/大小不一致。
    /// 从稳定目录自己跑起来那一发永远 false——不然每次开板子都自我复制一遍。
    /// </summary>
    public static bool NeedsRefresh(string sourceExe, string targetExe)
    {
        if (sourceExe.Length == 0 || !File.Exists(sourceExe))
        {
            return false;
        }

        if (string.Equals(
                Path.GetDirectoryName(sourceExe)?.TrimEnd('\\'),
                Path.GetDirectoryName(targetExe)?.TrimEnd('\\'),
                StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (!File.Exists(targetExe))
        {
            return true;
        }

        var source = new FileInfo(sourceExe);
        var target = new FileInfo(targetExe);

        return source.Length != target.Length || source.LastWriteTimeUtc != target.LastWriteTimeUtc;
    }

    /// <summary>
    /// 把整个构建目录对齐进稳定目录。返回 null＝成；否则是一句话的原因。
    /// </summary>
    /// <remarks>
    /// 中途失败（稳定目录那份正被另一个实例占着）留下的是半份拷贝：下一发启动时时间戳仍不一致，
    /// 会再对一次——所以这里不需要回滚，需要的是别把它当成功。
    /// </remarks>
    public static string? Refresh(string sourceDirectory, string targetDirectory)
    {
        try
        {
            if (sourceDirectory.Length == 0 || !Directory.Exists(sourceDirectory))
            {
                return "找不到当前构建的目录";
            }

            Directory.CreateDirectory(targetDirectory);

            foreach (var file in Directory.EnumerateFiles(sourceDirectory, "*", SearchOption.AllDirectories))
            {
                var target = Path.Combine(
                    targetDirectory,
                    file[(sourceDirectory.TrimEnd('\\', '/').Length + 1)..]);

                var folder = Path.GetDirectoryName(target);

                if (folder is not null)
                {
                    Directory.CreateDirectory(folder);
                }

                File.Copy(file, target, overwrite: true);
            }

            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return ex.Message;
        }
    }
}

/// <summary>
/// 开始菜单里那枚「铺陈画板」。<see cref="AppInstall"/> 决定它该指哪儿，这里只负责落那条 .lnk。
/// </summary>
/// <remarks>
/// 走 WScript.Shell 而不是手写 .lnk 的二进制格式：那是这台机器上唯一成文的建快捷方式 API，
/// 而 .lnk 的链接格式是 MS-SHLLINK，自己拼反而更容易错。
/// </remarks>
public static class StartMenuShortcut
{
    public const string ShortcutFileName = "铺陈画板.lnk";

    /// <summary>SpecialFolder.Programs 是每个用户自己的开始菜单目录，不需要管理员。</summary>
    public static string ShortcutPath()
        => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Programs), ShortcutFileName);

    /// <summary>补一枚快捷方式。返回 null＝已就位；否则是一句话的原因。</summary>
    public static string? Ensure(string exe)
    {
        if (exe.Length == 0 || !File.Exists(exe))
        {
            return "目标 exe 不在，没写快捷方式";
        }

        try
        {
            var shellType = Type.GetTypeFromProgID("WScript.Shell");

            if (shellType is null)
            {
                return "这台机器没有 WScript.Shell";
            }

            var path = ShortcutPath();
            dynamic shell = Activator.CreateInstance(shellType)!;
            dynamic link = shell.CreateShortcut(path);

            // CreateShortcut 读已有那份：已经指着这个 exe 就一个字节都不写。
            if (File.Exists(path) && string.Equals((string)link.TargetPath, exe, StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            link.TargetPath = exe;
            link.WorkingDirectory = Path.GetDirectoryName(exe);
            link.IconLocation = $"{exe},0";
            link.Description = "铺陈画板";
            link.Save();

            return null;
        }
        catch (Exception ex)
        {
            return $"{ex.GetType().Name}: {ex.Message}";
        }
    }
}
