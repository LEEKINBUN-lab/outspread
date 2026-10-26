using System.Diagnostics;
using Microsoft.UI.Xaml;
using Outspread.Core;

namespace Outspread.Board;

public partial class App : Application
{
    private Window? _window;

    /// <summary>这一发是不是自检那一发。决定"出了意外该以什么退出码收场"。</summary>
    private static bool SelfTestMode
        => Environment.GetCommandLineArgs().Contains("--selftest", StringComparer.OrdinalIgnoreCase);

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
        var window = new MainWindow();

        _window = window;
        window.Activate();

        // 窗口已经出来了再补稳定目录和开始菜单那枚：它们是给用户找入口用的，不该排在首帧前面；
        // 对齐一次构建是 42MB 的拷贝，更不能挡开板子。自检那一发跑在 %TEMP% 的副本上，两样都不动。
        if (!SelfTestMode)
        {
            var runningExe = Environment.ProcessPath ?? string.Empty;

            _ = Task.Run(() =>
            {
                if (AppInstall.NeedsRefresh(runningExe, AppInstall.StableExePath()))
                {
                    if (AppInstall.Refresh(Path.GetDirectoryName(runningExe) ?? string.Empty, AppInstall.StableDirectory()) is { } why)
                    {
                        Diagnostics.Write($"install: 稳定目录没对齐（{why}），快捷方式这一发仍指现有那份");
                    }
                    else
                    {
                        Diagnostics.Write($"install: 稳定目录已对齐到 {runningExe}");
                    }
                }

                var target = AppInstall.PreferredLaunchTarget(runningExe, File.Exists(AppInstall.StableExePath()));

                if (StartMenuShortcut.Ensure(target) is { } reason)
                {
                    Diagnostics.Write($"shortcut: 没补上 {reason}");
                }
                else
                {
                    Diagnostics.Write($"shortcut: 开始菜单那枚指着 {target}");
                }
            });
        }

        if (SelfTestMode)
        {
            window.BeginSelfTest();
        }
    }
}
