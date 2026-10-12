using Microsoft.UI.Xaml;

namespace Outspread.Board;

public partial class App : Application
{
    private Window? _window;

    public App()
    {
        InitializeComponent();

        UnhandledException += (_, e) => Diagnostics.Write($"unhandled: {e.Exception}");
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        var window = new MainWindow();

        _window = window;
        window.Activate();

        if (Environment.GetCommandLineArgs().Contains("--selftest", StringComparer.OrdinalIgnoreCase))
        {
            window.BeginSelfTest();
        }
    }
}
