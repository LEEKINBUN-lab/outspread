using System.IO.Pipes;
using System.Text;
using Outspread.Core;

namespace Outspread.Board;

/// <summary>
/// Answers <c>outspread shot</c> for one open board.
/// </summary>
/// <remarks>
/// Runs on its own background thread and hands each request to the UI thread, because a
/// capture has to happen where the visual tree lives. One pipe per board path: with two
/// boards open, a request cannot be answered by the wrong one.
/// </remarks>
internal sealed class ShotServer : IDisposable
{
    private readonly CancellationTokenSource _closed = new();

    private readonly Task _loop;

    private readonly Func<ShotRequest, ShotReply> _onUiThread;

    public ShotServer(string boardPath, Func<ShotRequest, ShotReply> handlerOnUiThread)
    {
        PipeName = ShotProtocol.PipeName(boardPath);
        _onUiThread = handlerOnUiThread;
        BoardPath = boardPath;

        // 线程池上的异步循环，不是一枚专门起的线程：见 Dispose 里那段"关窗口为什么要等一秒"。
        _loop = Task.Run(RunAsync);
    }

    public string PipeName { get; }

    public string BoardPath { get; }

    /// <summary>Set when the pipe could not be created at all, so it is not reported as listening.</summary>
    public string? StartupError { get; private set; }

    private async Task RunAsync()
    {
        var reportedFailure = false;

        while (!_closed.IsCancellationRequested)
        {
            try
            {
                using var pipe = new NamedPipeServerStream(
                    PipeName,
                    PipeDirection.InOut,
                    maxNumberOfServerInstances: 1,
                    PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous);

                await pipe.WaitForConnectionAsync(_closed.Token);

                using var reader = new StreamReader(pipe, Encoding.UTF8);
                using var writer = new StreamWriter(pipe, new UTF8Encoding(false)) { AutoFlush = false };

                var line = await reader.ReadLineAsync(_closed.Token);

                if (line is null)
                {
                    continue;
                }

                var request = ShotProtocol.ReadRequest(line);

                var reply = request is null
                    ? new ShotReply(false, null, Error: "请求不是能读的 JSON")
                    : _onUiThread(request);

                writer.WriteLine(ShotProtocol.Serialize(reply));
                await writer.FlushAsync(_closed.Token);
                pipe.WaitForPipeDrain();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
                                       or OperationCanceledException or InvalidOperationException)
            {
                if (!reportedFailure && ex is not OperationCanceledException)
                {
                    StartupError ??= ex.Message;
                }

                reportedFailure = true;
            }
        }
    }

    public void Dispose()
    {
        _closed.Cancel();

        /* 这里以前是 `_loop.Join(1s)`，而那一秒每次都花满：专用线程卡在不可取消的
           WaitForConnection 上，取消信号它根本看不见，所以每关一次窗口都要白等一秒
           （现读：`window closed · 收尾 1016ms`）。换成异步等待以后取消真的能打断它，
           这 200ms 等的只是"管道名确实交出去了"，好让下一台实例建得起同一根管道。 */
        try
        {
            _loop.Wait(TimeSpan.FromMilliseconds(200));
        }
        catch (Exception ex) when (ex is AggregateException or InvalidOperationException)
        {
        }

        _closed.Dispose();
    }
}
