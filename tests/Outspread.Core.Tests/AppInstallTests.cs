using Outspread.Core;

namespace Outspread.Core.Tests;

/// <summary>
/// 稳定目录与开始菜单那一枚的判断逻辑。拷贝本身跑在 %TEMP% 的两棵小树之间——
/// 这一发不许碰任何真实构建目录，也不许碰他的板子。
/// </summary>
public sealed class AppInstallTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "outspread-install-tests-" + Guid.NewGuid().ToString("N")[..8]);

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    [Fact]
    public void The_start_menu_points_at_the_stable_copy_once_there_is_one()
    {
        var running = @"D:\repo\bin\x64\Debug\win-x64\Outspread.Board.exe";

        Assert.Equal(AppInstall.StableExePath(), AppInstall.PreferredLaunchTarget(running, stableExeExists: true));
        Assert.Equal(running, AppInstall.PreferredLaunchTarget(running, stableExeExists: false));
    }

    [Fact]
    public void A_build_is_aligned_into_the_stable_folder_only_when_it_actually_differs()
    {
        var source = Write("build\\Outspread.Board.exe", "abc");
        var sameFolder = Path.Combine(_root, "build", "Outspread.Board.exe");

        // 从稳定目录自己跑起来那一发：源与目标同一个目录，永远不自我复制。
        Assert.False(AppInstall.NeedsRefresh(source, sameFolder));

        // 稳定目录里还没有那份（第一次）：要对齐。
        Assert.True(AppInstall.NeedsRefresh(source, Path.Combine(_root, "stable\\Outspread.Board.exe")));

        var target = Write("stable\\Outspread.Board.exe", "abc");

        File.SetLastWriteTimeUtc(target, File.GetLastWriteTimeUtc(source));

        Assert.False(AppInstall.NeedsRefresh(source, target));

        File.WriteAllText(target, "abcd");

        Assert.True(AppInstall.NeedsRefresh(source, target));

        // 源不在（被 clean 掉了）不能把空目录当"要拷贝"。
        Assert.False(AppInstall.NeedsRefresh(Path.Combine(_root, "没有这个.exe"), target));
    }

    [Fact]
    public void Refresh_copies_the_tree_and_keeps_subfolders()
    {
        Write("src\\Outspread.Board.exe", "exe");
        Write("src\\Assets\\outspread.ico", "ico");
        Write("src\\runtimes\\win-x64\\native.dll", "dll");

        var why = AppInstall.Refresh(Path.Combine(_root, "src"), Path.Combine(_root, "dst"));

        Assert.Null(why);
        Assert.Equal("exe", File.ReadAllText(Path.Combine(_root, "dst", "Outspread.Board.exe")));
        Assert.Equal("ico", File.ReadAllText(Path.Combine(_root, "dst", "Assets", "outspread.ico")));
        Assert.Equal("dll", File.ReadAllText(Path.Combine(_root, "dst", "runtimes", "win-x64", "native.dll")));
    }

    [Fact]
    public void Refresh_says_why_instead_of_claiming_a_copy_it_could_not_make()
    {
        var why = AppInstall.Refresh(Path.Combine(_root, "没有这一层"), Path.Combine(_root, "dst"));

        Assert.NotNull(why);
    }

    [Fact]
    public void An_aligned_copy_remembers_which_build_it_came_from()
    {
        Write("src\\Outspread.Board.exe", "exe");

        // 老副本没记来源：读不到就是读不到（这一发不许碰 %LOCALAPPDATA% 里那份真的）。
        Assert.Null(AppInstall.ReadSourceHint(Path.Combine(_root, "stable\\outspread-source.txt")));

        Assert.Null(AppInstall.Refresh(Path.Combine(_root, "src"), Path.Combine(_root, "dst")));
        Assert.Equal(Path.Combine(_root, "src"), AppInstall.ReadSourceHint(Path.Combine(_root, "dst", AppInstall.SourceHintFileName)));

        // 来源树被 clean 掉了：读到的还是那行路径，但它已经不能接手——由 StaleSourceExe 那一层判。
        Assert.Null(AppInstall.ReadSourceHint(Path.Combine(_root, "没有这一层", AppInstall.SourceHintFileName)));
    }

    [Fact]
    public void The_stable_copy_hands_the_window_to_a_newer_build_instead_of_running_stale()
    {
        var source = Write("build\\Outspread.Board.exe", "新的那一发");
        var stable = Write("stable\\Outspread.Board.exe", "旧");
        var stableDir = Path.Combine(_root, "stable");

        // 两边一模一样才叫"已对齐"，所以先把稳定副本的刻度挪到与来源同一发。
        File.SetLastWriteTimeUtc(stable, File.GetLastWriteTimeUtc(source));

        // 来源里那一份又大又新：从稳定目录点开时不能就这般把旧构建端上去。
        Assert.Equal(source, AppInstall.StaleSourceExe(stable, Path.Combine(_root, "build"), stableDir));

        // 对齐以后（时间戳与字节数都一致）就交给它自己跑，不再交接。
        Assert.Null(AppInstall.Refresh(Path.Combine(_root, "build"), stableDir));

        Assert.Null(AppInstall.StaleSourceExe(stable, Path.Combine(_root, "build"), stableDir));

        // 从 bin 里点开的那一发不是稳定副本：它就是最新的那一发，交接给它自己等于死循环。
        Assert.Null(AppInstall.StaleSourceExe(source, Path.Combine(_root, "build"), stableDir));

        // 没记来源（老副本）、来源被删掉：照旧跑这一发，不能把人晾在门外。
        Assert.Null(AppInstall.StaleSourceExe(stable, null, stableDir));
        Assert.Null(AppInstall.StaleSourceExe(stable, Path.Combine(_root, "这一层没了"), stableDir));
    }

    private string Write(string relative, string content)
    {
        var path = Path.Combine(_root, relative);

        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);

        return path;
    }
}
