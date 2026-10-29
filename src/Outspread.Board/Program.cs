namespace Outspread.Board;

/// <summary>
/// 入口自己写（csproj 里 <c>DISABLE_XAML_GENERATED_MAIN</c>）：heal 那一发不出窗口，
/// 进程码必须是 heal 的真实结果——生成的 Main 恒 return 0，会把失败报成全过。
/// </summary>
/// <remarks>
/// 初始化顺序照抄生成的 <c>Main</c>（ComWrappers 先行、Start 回调里挂 DispatcherQueue
/// 同步上下文），少一步 WinUI 就起不来。退出码在 <see cref="App.ExitCode"/>：
/// 普通启动恒 0；自检与探针在循环里直接 <c>Environment.Exit</c>，不走这一步。
/// </remarks>
public static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        WinRT.ComWrappersSupport.InitializeComWrappers();

        Microsoft.UI.Xaml.Application.Start(_ =>
        {
            var context = new Microsoft.UI.Dispatching.DispatcherQueueSynchronizationContext(
                Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread());

            SynchronizationContext.SetSynchronizationContext(context);

            new App();
        });

        return App.ExitCode;
    }
}
