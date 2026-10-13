using System.Text;
using System.Text.Json;
using QRCoder;
using Outspread.Core;

namespace Outspread.Cli;

/// <summary>
/// What a phone needs in order to join this board's folder, assembled into the one payload a
/// pairing step would carry.
/// </summary>
/// <remarks>
/// The address half is not a secret and could travel anywhere. The token half is, and it is
/// exactly why this command exists as its own thing: everything here is written so that the
/// secret is produced once, shown once, and never reaches a log, a file, an exception message,
/// or the shell history.
///
/// It deliberately carries no board list. A board is a path inside the folder, and the far
/// side's own file listing answers that at the moment it is asked; an inventory written into a
/// pairing code goes stale and a stale inventory is worse than none.
/// </remarks>
internal static class PairCommand
{
    public static int Run(Options options)
    {
        var config = SyncConfig.Load(SyncLayout.Root);

        if (config is not { IsSet: true })
        {
            Console.Error.WriteLine("这台机器还没定远端仓库，没有可配对的东西：先 outspread sync link --remote owner/仓库名");

            return 2;
        }

        var token = SyncCredentials.Read();

        if (string.IsNullOrEmpty(token))
        {
            Console.Error.WriteLine("读不到令牌（凭据管理器里没有，环境变量里也没有）。先 outspread sync link --token-stdin，或用 cmdkey 存一次");

            return 2;
        }

        var text = PairingPayload.Build(config, token);

        if (options.Flag("json"))
        {
            // Machine-readable, and only ever to a terminal someone is looking at right now:
            // a redirect would put the token in a file.
            Console.WriteLine(text);

            return 0;
        }

        var boardPath = Locate(options).BoardPath;
        var paths = SyncPaths.ForBoard(boardPath);

        // The payload itself is not printed on the human path: the token lives in the code and
        // nowhere else on this screen. `--json` is the deliberate escape hatch (it writes to the
        // terminal only, and a redirect would put the token in a file, which is why the wording
        // below says so out loud).
        Console.WriteLine($"这一块板的远端路径：{paths.DocPath} 与 {paths.ManifestPath}");
        Console.WriteLine($"载荷 {Encoding.UTF8.GetByteCount(text)} 字节（其中令牌 {token.Length} 字符，指纹 {SyncCredentials.Fingerprint(token)}）");
        Console.WriteLine();
        Console.WriteLine("扫这个：它里面是一枚**能写你仓库的令牌**。屏幕会被看到，日志和文件不会——");
        Console.WriteLine("这份文字不落盘、不进日志，想再看一次得重新跑这条命令。");
        Console.WriteLine("配对成功后，手机端把令牌存进它自己的密钥库；撤销就在 GitHub 上撤这一把。");
        Console.WriteLine();

        // QRCoder is the one third-party package in this project, and it is only here: it turns
        // the text into a module grid. The grid-to-glyphs mapping lives in Core where it can be
        // tested without a dependency (QrTerminalTests).
        using var generator = new QRCodeGenerator();
        using var code = generator.CreateQrCode(text, QRCodeGenerator.ECCLevel.Q);

        // 这句里不写"90 天"：到期日是建这把 PAT 时选的，载荷里没有那一位，代码也读不到它——
        // 屏幕上写一个具体天数就是把它变成机制的一部分，而它不是。能说的只有撤销在哪。
        Console.WriteLine("手机扫这个（屏幕上的这一块码）：");
        Console.WriteLine();

        // Measured here rather than trusted from docs: QRCoder hands back the code at its exact
        // version size (a 192-byte payload at ECC Q came out 73×73 = 4×14+17), so the four-module
        // light border this end has to add — a code printed edge-to-edge is harder to find.
        var picture = QrTerminal.Render(code.ModuleMatrix, QrTerminal.QuietZoneModules);
        var lines = picture.Split('\n', StringSplitOptions.RemoveEmptyEntries);

        // A code wider than the window gets wrapped by the terminal, and a wrapped code is not a
        // code: it scans as nothing. Better to say so than to draw something unscannable.
        var needed = lines[0].Length + 2;

        var available = SafeWindowWidth();

        if (available >= needed)
        {
            foreach (var line in lines)
            {
                Console.WriteLine(line);
            }
        }
        else
        {
            Console.WriteLine($"  画不开：这一块码要 {needed} 列，而当前窗口只有 {available} 列。");
            Console.WriteLine("  把终端窗口拉宽再跑一次（折行的码扫不出来，所以这里不画半张）。");
        }

        Console.WriteLine();
        Console.WriteLine("（扫不出来时用 outspread sync pair --json 取那一行文字；注意它会把令牌打到终端上。）");

        return 0;
    }

    private static BoardLocator.Resolution Locate(Options options) => BoardLocator.Resolve(options.Value("board"));

    /// <summary>
    /// A redirected output has no window to measure, and reading <c>Console.WindowWidth</c>
    /// then throws — so an unknown width is treated as wide enough rather than letting a probe
    /// crash the thing it was guarding.
    /// </summary>
    private static int SafeWindowWidth()
    {
        try
        {
            return Console.IsOutputRedirected ? int.MaxValue : Console.WindowWidth;
        }
        catch (Exception ex) when (ex is IOException or ArgumentException)
        {
            return int.MaxValue;
        }
    }
}
