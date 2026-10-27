using System.IO.Pipes;
using System.Text;
using Outspread.Core;
using Outspread.Mcp;

namespace Outspread.Mcp.Tests;

/// <summary>
/// 取证通道（board_shot → 板面那根命名管道）的线形。
/// 2026-10-27 拍不到产品真图锁在这里：工具把 JSON 直接写进管道却没有行末符，
/// 板面那头按一行读（ReadLineAsync）就永远等不到结尾；管道只许一个实例，
/// 挂住的那次把名额占死，之后每一发都报"这块板没开着"——一句假话。
/// </summary>
public sealed class ShotTransportTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "outspread-shot-tests-" + Guid.NewGuid().ToString("N")[..8]);

    public ShotTransportTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    [Fact]
    public async Task A_shot_request_arrives_as_one_whole_line_and_the_reply_comes_back()
    {
        var board = Path.Combine(_root, "shot.canvas");

        // 板面不存在也没关系：这一发验的是管道上的线形，工具在连上之前不读文件。
        await File.WriteAllTextAsync(board, "{\"nodes\":[],\"edges\":[]}");

        using var pipe = new NamedPipeServerStream(
            ShotProtocol.PipeName(board),
            PipeDirection.InOut,
            1,
            PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous);

        var served = Task.Run(async () =>
        {
            await pipe.WaitForConnectionAsync();

            using var reader = new StreamReader(pipe, Encoding.UTF8);
            using var writer = new StreamWriter(pipe, new UTF8Encoding(false));

            // 3 秒是装置的时限，不是协议的时限：挂死的形状正是"这一行永远没有结尾"。
            var line = await reader.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(3));

            Assert.NotNull(line);

            var request = ShotProtocol.ReadRequest(line!);

            Assert.NotNull(request);
            Assert.Equal(ShotProtocol.RequestedOp, request!.Op);
            Assert.Equal("node", request.Mode);
            Assert.Equal("p1", request.NodeId);

            await writer.WriteLineAsync(ShotProtocol.Serialize(
                new ShotReply(true, Path.Combine(_root, "card.png"), 240, 120, 1.0, 3)));
            await writer.FlushAsync();
        });

        var receipt = await BoardTools.BoardShot(
            Path.Combine(_root, "card.png"),
            mode: "node",
            board: board,
            id: "p1").WaitAsync(TimeSpan.FromSeconds(10));

        await served;

        // 回执里的号必须是板面真报回来的那三个读数，不是工具自己编的。
        Assert.Contains("card.png", receipt, StringComparison.Ordinal);
        Assert.Contains("240×120", receipt, StringComparison.Ordinal);
        Assert.Contains("3 个节点", receipt, StringComparison.Ordinal);
    }
}
