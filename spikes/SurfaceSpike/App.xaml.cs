using Microsoft.UI.Xaml;

namespace Outspread.Spike;

public partial class App : Application
{
    private Window? _window;

    public App()
    {
        InitializeComponent();

        // spike 也要能自己红：起不来就非零退出，别让人对着一片空白读数。
        UnhandledException += (_, e) =>
        {
            Log.Write($"unhandled: {e.Exception}");

            Environment.Exit(1);
        };
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        Log.Write($"startup: {Environment.ProcessPath} · 参数 {string.Join(' ', Environment.GetCommandLineArgs().Skip(1))}");

        var window = new MainWindow();

        _window = window;
        window.Activate();
    }
}
