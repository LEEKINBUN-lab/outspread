using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Server;

namespace Outspread.Mcp;

/// <summary>
/// 板子的 MCP 服务：让 agent 能把话**写在板上**给你看，而不是只在聊天里说一遍。
/// </summary>
/// <remarks>
/// 每一发写都走 <c>CanvasStore.SavePatch</c>——和界面同一把命名互斥、同一套原子替换，
/// 所以 agent 和你可以在同一秒各写各的，谁都不覆盖谁。这里刻意不自己画 JSON-RPC：
/// 协议用官方 C# SDK，本仓只出"板子"这一层语义。
/// </remarks>
internal static class Program
{
    private static async Task<int> Main(string[] args)
    {
        var builder = Host.CreateApplicationBuilder(args);

        // 默认那个 console logger 写到 stdout，而 stdout 就是 MCP 协议的管子——日志一进去，
        // 客户端第一行读到的就是 "info: ..."，握手当场碎。日志一律改道 stderr。
        builder.Logging.ClearProviders();
        builder.Logging.AddConsole(options => options.LogToStandardErrorThreshold = LogLevel.Trace);

        builder.Services.AddMcpServer()
            .WithStdioServerTransport()
            .WithTools<BoardTools>();

        await builder.Build().RunAsync();

        return 0;
    }
}
