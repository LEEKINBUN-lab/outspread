using System.Diagnostics;
using Microsoft.UI.Xaml;
using Outspread.Core;

namespace Outspread.Board;

public partial class App : Application
{
    private Window? _window;

    internal static bool PerfMode => Environment.GetCommandLineArgs().Any(arg => arg.Equals("--perf", StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// <c>--pan-probe &lt;毫秒&gt;</c> / <c>--zoom-probe &lt;毫秒&gt;</c>：持续平移或持续滚轮那么多毫秒，
    /// 给外面的 GPU/CPU 采样器一个**有界**的窗口。上限 30 秒是故意的——探针不许变成一枚在后台吃 CPU 的东西。
    /// </summary>
    private static int ProbeMilliseconds(string flag)
    {
        var args = Environment.GetCommandLineArgs();

        for (var index = 0; index < args.Length - 1; index++)
        {
            if (args[index].Equals(flag, StringComparison.OrdinalIgnoreCase)
                && int.TryParse(args[index + 1], out var milliseconds))
            {
                return Math.Clamp(milliseconds, 500, 30_000);
            }
        }

        return 0;
    }

    /// <summary>这一发是不是自检那一发。决定"出了意外该以什么退出码收场"。</summary>
    private static bool SelfTestMode
        => Environment.GetCommandLineArgs().Contains("--selftest", StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// 只对齐稳定目录与开始菜单那枚，不开窗口。<c>check.cmd</c> 用它：
    /// 构建完顺手把开始菜单那份跟上，免得从开始菜单点开的是旧的一版。
    /// </summary>
    private static bool HealInstallMode
        => Environment.GetCommandLineArgs().Contains("--heal-install", StringComparer.OrdinalIgnoreCase);

    /// <summary>自检跑在 %TEMP% 的副本上：它切来切去的那几块板不能写进"上次打开的板"。</summary>
    public static bool SelfTest => SelfTestMode;

    /// <summary>进程起来的那一刻，用来把"打开慢"切成几段分别量。</summary>
    private static readonly DateTime Boot = Process.GetCurrentProcess().StartTime;

    public static double SinceBoot() => (DateTime.Now - Boot).TotalMilliseconds;

    public App()
    {
        Diagnostics.Write($"startup: 进到 App 构造 +{SinceBoot():0}ms");

        InitializeComponent();

        // 自检里抛出来的东西必须以非零退出码落地：cmd 的 `if errorlevel 1` 看不见负数
        // （崩溃码 0xC00000FD 换成正数是 -1073741685，比 1 小），所以一条"跑飞了"的自检
        // 会被 check.cmd 念成"全过"。这一句把那条路堵死——红灯要能自己红。
        UnhandledException += (_, e) =>
        {
            Diagnostics.Write($"unhandled: {e.Exception}");

            if (SelfTestMode)
            {
                Environment.Exit(1);
            }
        };
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        Diagnostics.Write($"startup: 这一发跑的是 {Environment.ProcessPath}，构建于 {BuildStamp()}");

        // 从开始菜单点开的那一发可能是上一次对齐时的旧构建，而它自己拷不动自己（这份正被系统锁着）。
        // 所以这里先看一眼来源树里有没有更新的：有就让它接手窗口，这一发直接退——
        // 他不用点第二下，看到的也是最新那一版。交接失败（那条路径没了）就照旧跑下去，别把人晾在门外。
        if (!SelfTestMode && !HealInstallMode
            && AppInstall.StaleSourceExe(
                Environment.ProcessPath ?? string.Empty,
                AppInstall.ReadSourceHint(AppInstall.SourceHintPath()),
                AppInstall.StableDirectory()) is { } handover)
        {
            if (TryStartBoard(handover))
            {
                Diagnostics.Write($"install: 稳定目录那枚是旧构建，已交给 {handover} 接手并对齐");
                Environment.Exit(0);

                return;
            }

            Diagnostics.Write($"install: 想把窗口交给 {handover}，但那枚起不来——这一发仍跑旧构建");
        }

        if (HealInstallMode)
        {
            // 构建完顺手把开始菜单那枚跟上：不出窗口，失败就用非零退出码说话（check.cmd 的那一步会红）。
            Environment.Exit(HealInstall(out var why) ? 0 : Fail(why));

            return;
        }

        var window = new MainWindow();

        _window = window;
        window.Activate();

        // 窗口已经出来了再补稳定目录和开始菜单那枚：它们是给用户找入口用的，不该排在首帧前面；
        // 对齐一次构建是 42MB 的拷贝，更不能挡开板子。自检那一发跑在 %TEMP% 的副本上，两样都不动。
        if (!SelfTestMode)
        {
            _ = Task.Run(() => HealInstall(out _));
        }

        if (SelfTestMode)
        {
            window.BeginSelfTest();
        }

        if (PerfMode)
        {
            window.BeginPerfProbe();
        }

        var panProbe = ProbeMilliseconds("--pan-probe");

        if (panProbe > 0)
        {
            window.BeginPanProbe(panProbe);
        }

        var zoomProbe = ProbeMilliseconds("--zoom-probe");

        if (zoomProbe > 0)
        {
            window.BeginZoomProbe(zoomProbe);
        }

        var dragProbe = ProbeMilliseconds("--drag-probe");

        if (dragProbe > 0)
        {
            window.BeginDragProbe(dragProbe);
        }
    }

    /// <summary>把当前构建对齐进稳定目录，并把开始菜单那枚指过去。成 → true；不成还把原因留在 <paramref name="why"/> 里。</summary>
    private static bool HealInstall(out string why)
    {
        var runningExe = Environment.ProcessPath ?? string.Empty;
        var trouble = string.Empty;

        if (AppInstall.NeedsRefresh(runningExe, AppInstall.StableExePath()))
        {
            if (AppInstall.Refresh(Path.GetDirectoryName(runningExe) ?? string.Empty, AppInstall.StableDirectory()) is { } reason)
            {
                trouble = $"稳定目录没对齐（{reason}），快捷方式这一发仍指现有那份";
                Diagnostics.Write($"install: {trouble}");
            }
            else
            {
                Diagnostics.Write($"install: 稳定目录已对齐到 {runningExe}");
            }
        }

        var target = AppInstall.PreferredLaunchTarget(runningExe, File.Exists(AppInstall.StableExePath()));

        if (StartMenuShortcut.Ensure(target) is { } reason2)
        {
            trouble = $"没补上快捷方式（{reason2}）";
            Diagnostics.Write($"shortcut: {trouble}");
        }
        else
        {
            Diagnostics.Write($"shortcut: 开始菜单那枚指着 {target}");
        }

        why = trouble;

        return trouble.Length == 0;
    }

    private static int Fail(string why)
    {
        // 与自检同一套说法：check.cmd 的那一步红了，日志里要能一眼看到是哪一句把它判红的。
        Diagnostics.Write($"HEAL FAIL: --heal-install 没走通：{why}");

        return 1;
    }

    private static string BuildStamp()
    {
        try
        {
            return File.GetLastWriteTimeUtc(Environment.ProcessPath ?? string.Empty).ToString("yyyy-MM-dd HH:mm");
        }
        catch (Exception ex)
        {
            return $"读不到（{ex.GetType().Name}）";
        }
    }

    private static bool TryStartBoard(string exe)
    {
        try
        {
            Process.Start(new ProcessStartInfo(exe)
            {
                UseShellExecute = true,
                WorkingDirectory = Path.GetDirectoryName(exe) ?? string.Empty,
            });

            return true;
        }
        catch (Exception ex)
        {
            Diagnostics.Write($"spawn: {exe} 起不来（{ex.GetType().Name}: {ex.Message}）");

            return false;
        }
    }
}
