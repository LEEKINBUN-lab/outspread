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

    private readonly Thread _loop;

    private readonly Func<ShotRequest, ShotReply> _onUiThread;

    public ShotServer(string boardPath, Func<ShotRequest, ShotReply> handlerOnUiThread)
    {
        PipeName = ShotProtocol.PipeName(boardPath);
        _onUiThread = handlerOnUiThread;
        BoardPath = boardPath;

        _loop = new Thread(Run)
        {
            IsBackground = true,
            Name = "outspread-shot",
        };

        _loop.Start();
    }

    public string PipeName { get; }

    public string BoardPath { get; }

    /// <summary>Set when the pipe could not be created at all, so it is not reported as listening.</summary>
    public string? StartupError { get; private set; }

    private void Run()
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

                pipe.WaitForConnection();

                using var reader = new StreamReader(pipe, Encoding.UTF8);
                using var writer = new StreamWriter(pipe, new UTF8Encoding(false)) { AutoFlush = false };

                var line = reader.ReadLine();

                if (line is null)
                {
                    continue;
                }

                var request = ShotProtocol.ReadRequest(line);

                var reply = request is null
                    ? new ShotReply(false, null, Error: "请求不是能读的 JSON")
                    : _onUiThread(request);

                writer.WriteLine(ShotProtocol.Serialize(reply));
                writer.Flush();
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

        try
        {
            _loop.Join(TimeSpan.FromSeconds(1));
        }
        catch (Exception ex) when (ex is InvalidOperationException or ThreadStateException)
        {
        }

        _closed.Dispose();
    }
}
