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

    /// <summary>
    /// 进程码由自定义 <see cref="Program.Main"/> 收尾时读这一笔：heal 那一发不出窗口，
    /// 活儿的结果只能放在这里带出去（生成的 Main 恒 return 0，会把失败报成全过）。
    /// </summary>
    public static int ExitCode { get; private set; }

    /// <summary>
    /// 诊断那一类启动（探针、<c>--perf</c>、显式 <c>--no-heal</c>）：<b>不许碰稳定目录与开始菜单那枚</b>。
    /// </summary>
    /// <remarks>
    /// 10-29 踩过：拿一份临时构建跑 A/B 截图，它启动时顺手把"日常那份"对齐成了那枚临时构建；
    /// 他再点开就跑到没有这两刀的那一版，一整轮"为什么完全没感觉"都白问。
    /// 读数的进程不该有资格改用户点开的那一枚。
    /// </remarks>
    private static bool DiagnosticLaunch
    {
        get
        {
            var args = Environment.GetCommandLineArgs();

            if (PerfMode || args.Contains("--no-heal", StringComparer.OrdinalIgnoreCase))
            {
                return true;
            }

            return args.Any(arg => arg.StartsWith("--pan-probe", StringComparison.OrdinalIgnoreCase)
                || arg.StartsWith("--zoom-probe", StringComparison.OrdinalIgnoreCase)
                || arg.StartsWith("--zoom-preview", StringComparison.OrdinalIgnoreCase)
                || arg.StartsWith("--drag-probe", StringComparison.OrdinalIgnoreCase)
                || arg.StartsWith("--zoom-band", StringComparison.OrdinalIgnoreCase));
        }
    }

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
        if (!SelfTestMode && !HealInstallMode && !DiagnosticLaunch
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
            // 这里不能 Environment.Exit：消息循环还没起来就那样退，WinUI 会以 0xC0000142
            // （STATUS_DLL_INIT_FAILED）收场——活儿全干完了，退出码却报成崩，check.cmd 第 6 步因此红。
            var healed = HealInstall(out var why);

            if (!healed)
            {
                Diagnostics.Write($"HEAL FAIL: --heal-install 没走通：{why}");
            }

            ExitCode = healed ? 0 : 1;

            // 循环的收场也得自己办：零窗口时 Application.Start 不会自己退（10-29 夜实测，
            // 直接 return 挂到看门狗也等不来），借一枚 Hide 的空窗、在循环里 Close 它——
            // 最后一扇窗关闭才是循环的正规结束，Main 那头拿到 ExitCode 走人。
            var offscreen = new Window();

            offscreen.AppWindow.Hide();

            // 不用方法组直传：TryEnqueue 封送"以投影对象为 target 的委托"会炸（10-29 夜实测
            // AsValue 失败、stowed 0xC000027B），lambda 走托管 CCW 那条路才是通的。
            Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread().TryEnqueue(() => offscreen.Close());

            // 兜底：万一 Close 这条路没走通，这一发不许挂死 check.cmd。15 秒后从旁边线程
            // 带着真实退出码硬退——循环已经转了这么久，selftest 那些发证明循环转起来之后
            // 再 Environment.Exit 不会炸成 0xC0000142。
            _ = Task.Run(async () =>
            {
                await Task.Delay(15_000);

                Environment.Exit(ExitCode);
            });

            return;
        }

        var window = new MainWindow();

        _window = window;
        window.Activate();

        // 窗口已经出来了再补稳定目录和开始菜单那枚：它们是给用户找入口用的，不该排在首帧前面；
        // 对齐一次构建是 42MB 的拷贝，更不能挡开板子。自检那一发跑在 %TEMP% 的副本上，两样都不动。
        // 诊断那一发（探针/--perf/--no-heal）同样不动：读数的进程不许换掉用户点开的那一枚（10-29 踩过）。
        if (!SelfTestMode && !DiagnosticLaunch)
        {
            _ = Task.Run(() => HealInstall(out _));
        }
        else if (DiagnosticLaunch)
        {
            Diagnostics.Write("install: 诊断那一发不动稳定目录与开始菜单那枚");
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

        var previewProbe = ProbeMilliseconds("--zoom-preview");

        if (previewProbe > 0)
        {
            window.BeginZoomPreviewProbe(previewProbe);
        }

        var dragProbe = ProbeMilliseconds("--drag-probe");

        if (dragProbe > 0)
        {
            window.BeginDragProbe(dragProbe);
        }

        var bandProbe = ProbeMilliseconds("--zoom-band");

        if (bandProbe > 0)
        {
            window.BeginZoomBandProbe(bandProbe);
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

    private static string BuildStamp()
    {
        try
        {
            // 本地时刻：这行是给人对着"我刚才几点编的"读的，写 UTC 会差八小时，
            // 10-29 就因为这条把一份 11:24 的构建读成了"03:24 的旧版"。
            return File.GetLastWriteTime(Environment.ProcessPath ?? string.Empty).ToString("yyyy-MM-dd HH:mm");
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
