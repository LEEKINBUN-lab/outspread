using System.Diagnostics;
using Microsoft.UI.Xaml;

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

        if (SelfTestMode)
        {
            window.BeginSelfTest();
        }
    }
}
