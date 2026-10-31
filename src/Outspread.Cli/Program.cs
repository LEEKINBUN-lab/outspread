using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Outspread.Core;

namespace Outspread.Cli;

/// <summary>
/// The board's command line, which is also the agent's hand on the board.
/// </summary>
/// <remarks>
/// Every mutation goes through <see cref="CanvasStore.SavePatch"/>, the same named mutex
/// and atomic replace the window uses, and it patches whatever is on disk at that moment
/// rather than writing a copy this process read earlier. An agent and a human can therefore
/// write in the same second without either losing the other.
///
/// Per-node fields are accepted only in the shapes JSON Canvas 1.0 defines; the board does
/// not have a private language an agent would have to be told about.
/// </remarks>
internal static class Program
{
    private static int Main(string[] args)
    {
        // A GBK console shows Chinese as garbage unless the handle is switched, and this
        // program's whole output is Chinese. Redirected output cannot be re-encoded, so a
        // failure there is ignored rather than fatal.
        try
        {
            Console.OutputEncoding = Encoding.UTF8;
        }
        catch (Exception ex) when (ex is IOException or ArgumentException or NotSupportedException)
        {
        }

        var options = Options.Parse(args);

        try
        {
            return options.Command switch
            {
                "help" or "" or null => Help(),
                "list" => List(options),
                "show" => Show(options),
                "add" => Add(options),
                "patch" => Patch(options),
                "rm" => Remove(options),
                "validate" => Validate(options),
                "shot" => Shot(options),
                "new" => New(options),
                "sync" => SyncCommand.Run(options),
                "plugin" => PluginCommand.Run(options),
                var other => Fail($"不认识的命令 \"{other}\"；用 outspread help 看能做什么"),
            };
        }
        catch (UsageException ex)
        {
            return Fail(ex.Message);
        }
        catch (SyncSetupException ex)
        {
            // A missing repository or token is a sentence to act on, not a stack trace.
            return Fail(ex.Message);
        }
        catch (System.Net.Http.HttpRequestException ex)
        {
            return Fail($"够不到远端：{ex.Message}");
        }
        catch (CanvasDocumentException ex)
        {
            foreach (var line in ex.Issues)
            {
                Console.Error.WriteLine(line);
            }

            return 3;
        }
        catch (IOException ex)
        {
            return Fail($"读写失败：{ex.Message}");
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or InvalidOperationException or JsonException)
        {
            return Fail(ex.Message);
        }
    }

    private static int Help()
    {
        Console.WriteLine("""
            outspread — 铺陈板子的命令行（JSON Canvas 1.0）

              outspread list      [--board 路径] [--json]
              outspread show  ID  [--board 路径] [--json]
              outspread add       [--text 内容 | --text-file 文件 | --file assets/x.png | --url 链接 | --group 标题]
                               [--x 0 --y 0 --width 260 --height 90] [--color 1..6 或 #RRGGBB] [--id 自选]
              outspread patch ID  [--x --y --width --height --text --text-file --file --url --label --color] [--clear color|text]
              outspread rm    ID  [--board 路径]
              outspread validate  [--board 路径]
              outspread shot      [--out 路径.png] [--mode view|fit|node|chrome] [--id 节点] [--select 节点,节点]
                               [--panel filter|boards|cheat|banner|search|restore|rename|fold|unfold|pair|pull|shrink|scrolled]
                               问运行中的界面要一张 PNG；没开界面会明确报错，不会自己画一份
              outspread new   新板路径

              outspread sync link   --remote owner/仓库名 [--branch main] [--token-stdin | --token-file 路径]
                               定远端并把令牌存进 Windows 凭据管理器；顺手读一遍远端证明指得着
              outspread sync status [--json]
                               读数：rev / 远端令牌 / 待发几块 / 上次为什么没成 / 有几个冲突副本
              outspread sync once   [--verbose]
                               拉—合—推一轮。退出码 0=成，1=等一下还会成（离线、限流、盘上被人抢写过），
                               2=要人看一眼（令牌无效、远端没了、形状被拒、三轮没挤进去）
              outspread sync pair   [--json]
                               把手机要的配对载荷打在屏幕上：只出现一次，不落盘、不进日志；画成二维码还没接上
              outspread sync forget [--all]
                               忘掉这块板与远端的对应关系；远端和本地板子都不动
              outspread sync on|off [--board 路径]
                               这块板这台设备要不要同步。off＝测试板挡在远端仓外（窗口与 once 都拒发）；
                               标记在侧车里，是设备本地的，远端与别的设备不知道

              outspread plugin list
                               看本机登记了哪些可选插件（登记由插件自己的 install 写，这里只读）

            板子按 --board > TANKAI_BOARD > 向上找 boards/scratch.canvas > 本目录第一个 .canvas 的顺序定位。
            所有改动都是在磁盘当下那份上打补丁，并与界面共用同一把写锁。
            """);

        return 0;
    }

    private static int List(Options options)
    {
        var (path, document) = Open(options);

        if (options.Flag("json"))
        {
            Console.WriteLine(document.ToJson());
            return 0;
        }

        var nodes = document.Nodes;

        if (nodes.Count == 0)
        {
            Console.WriteLine($"{path}：板上没有节点");
            return 0;
        }

        Console.WriteLine(path);

        foreach (var (node, index) in nodes.Select((node, index) => (node, index)))
        {
            Console.WriteLine(Describe(node, index));
        }

        return 0;
    }

    private static int Show(Options options)
    {
        var (path, document) = Open(options);
        var node = Find(document, options.SinglePositional("show"));

        Console.WriteLine(path);
        var buffer = new MemoryStream();

        using (var writer = new Utf8JsonWriter(buffer, new JsonWriterOptions { Indented = true, IndentSize = 2 }))
        {
            node.Json.WriteTo(writer);
        }

        Console.WriteLine(Outspread.Core.JsonText.NormalizeNewLines(Encoding.UTF8.GetString(buffer.ToArray())));

        return 0;
    }

    private static int Add(Options options)
    {
        var (path, _) = Open(options);

        var text = options.Text();
        var file = options.Value("file");
        var url = options.Value("url");
        var label = options.Value("group");

        var kind = (options.IsTextRequested(), file, url, label) switch
        {
            (true, null, null, null) => CanvasNodeKind.Text,
            (false, not null, null, null) => CanvasNodeKind.File,
            (false, null, not null, null) => CanvasNodeKind.Link,
            (false, null, null, not null) => CanvasNodeKind.Group,
            _ => throw new UsageException("add 要指定 --text / --text-file / --file / --url / --group 中的一个"),
        };

        var x = options.Number("x", 0);
        var y = options.Number("y", 0);
        var width = options.Number("width", kind == CanvasNodeKind.File ? 480 : 260);
        var height = options.Number("height", kind == CanvasNodeKind.File ? 300 : 90);

        var patch = new DocPatch();
        var id = options.Value("id") ?? NodeIds.New(NodeIds.AgentNamespace);

        if (!NodeIds.IsValidId(id))
        {
            throw new UsageException($"id \"{id}\" 里只能用字母、数字和 _ - . :");
        }

        var json = new System.Text.Json.Nodes.JsonObject
        {
            ["id"] = id,
            ["type"] = CanvasNode.NameOfKind(kind),
            ["x"] = (long)Math.Round(x),
            ["y"] = (long)Math.Round(y),
            ["width"] = (long)Math.Round(width),
            ["height"] = (long)Math.Round(height),
        };

        switch (kind)
        {
            case CanvasNodeKind.Text:
                json["text"] = text;
                break;
            case CanvasNodeKind.File:
                json["file"] = file;
                break;
            case CanvasNodeKind.Link:
                json["url"] = url;
                break;
            case CanvasNodeKind.Group:
                json["label"] = label;
                break;
        }

        if (options.Value("color") is { } color)
        {
            json["color"] = color;
        }

        patch.Additions.Add(new AddedNode { Id = id, Json = json });

        return Apply(path, patch, $"加了 {id}");
    }

    private static int Patch(Options options)
    {
        var (path, document) = Open(options);
        var id = options.SinglePositional("patch");

        if (document.Find(id) is null)
        {
            throw new UsageException($"板上没有 id \"{id}\"；用 outspread list 看有哪些");
        }

        var patch = new DocPatch();

        foreach (var field in new[] { "x", "y", "width", "height" })
        {
            if (options.Raw(field) is { } raw)
            {
                patch.SetNumber(id, field, ParseNumber(raw, field));
            }
        }

        if (options.Text() is { } text)
        {
            patch.SetString(id, "text", text);
        }

        foreach (var (option, field) in new[]
        {
            ("file", "file"), ("url", "url"), ("label", "label"), ("color", "color"), ("type", "type"),
        })
        {
            if (options.Value(option) is { } value)
            {
                patch.SetString(id, field, value);
            }
        }

        foreach (var cleared in options.Values("clear"))
        {
            patch.Set(id, cleared, null);
        }

        if (patch.IsEmpty)
        {
            throw new UsageException("patch 什么都没要改：给一个 --x/--y/--width/--height/--text/--file/--url/--label/--color/--clear");
        }

        return Apply(path, patch, $"改了 {id}");
    }

    private static int Remove(Options options)
    {
        var (path, document) = Open(options);
        var id = options.SinglePositional("rm");

        if (document.Find(id) is null)
        {
            throw new UsageException($"板上没有 id \"{id}\"，没删任何东西");
        }

        var patch = new DocPatch();
        patch.Removals.Add(id);

        return Apply(path, patch, $"删了 {id}");
    }

    private static int New(Options options)
    {
        var target = options.SinglePositional("new");

        if (!CanvasStore.IsBoardPath(target))
        {
            target += CanvasStore.Extension;
        }

        if (File.Exists(target))
        {
            throw new UsageException($"{target} 已经在了，不覆盖");
        }

        CanvasStore.Write(target, CanvasDocument.CreateNew().ToJson());
        Console.WriteLine(target);

        return 0;
    }

    private static int Validate(Options options)
    {
        var (path, document) = Open(options);

        var issues = CanvasValidator.Validate(document);

        var errors = issues.Count(issue => issue.Severity == CanvasIssueSeverity.Error);

        Console.WriteLine($"{path}：{document.Nodes.Count} 个节点，{errors} 处错误，{issues.Count - errors} 处提醒");

        foreach (var issue in issues)
        {
            Console.WriteLine($"  {issue.Severity switch { CanvasIssueSeverity.Error => "错", _ => "提"}}  {issue.Message}");
        }

        return errors == 0 ? 0 : 1;
    }

    private static int Apply(string path, DocPatch patch, string summary)
    {
        var (reports, _, _) = CanvasStore.SavePatch(path, patch);

        var failures = reports
            .Where(report => report.Outcome is PatchOutcome.MissingNode or PatchOutcome.AlreadyPresent)
            .ToList();

        foreach (var report in failures)
        {
            Console.Error.WriteLine($"  未生效 {report.Outcome} {report.Id}：{report.Detail}");
        }

        var after = CanvasDocument.Parse(CanvasStore.ReadAllText(path), path);

        Console.WriteLine($"{summary} · 现在 {after.Nodes.Count} 个节点");

        return failures.Count == 0 ? 0 : 1;
    }

    private static int Shot(Options options)
    {
        var resolution = BoardLocator.Resolve(options.Value("board"));

        if (!File.Exists(resolution.BoardPath))
        {
            throw new UsageException($"{resolution.BoardPath} 不存在；先 outspread new 一块");
        }

        var mode = options.Value("mode") ?? "view";
        var outPath = options.Value("out") ?? $"shot-{DateTime.Now:yyyyMMdd-HHmmss}.png";

        var request = new ShotRequest(
            ShotProtocol.RequestedOp,
            outPath,
            mode,
            options.Value("id"),
            options.Value("select"),
            options.Value("panel"));

        var pipe = new NamedPipeClientStream(
            ".",
            ShotProtocol.PipeName(resolution.BoardPath),
            PipeDirection.InOut,
            PipeOptions.Asynchronous);

        try
        {
            // Two seconds: an open board answers immediately, so a timeout means nobody is
            // listening. Falling back to "draw it ourselves" would quietly produce a picture
            // that is not the one on his screen.
            pipe.Connect(TimeSpan.FromMilliseconds(2000).Milliseconds);
        }
        catch (Exception ex) when (ex is TimeoutException or IOException or UnauthorizedAccessException)
        {
            Console.Error.WriteLine(
                $"没有运行中的这块板子（{resolution.BoardPath}）。shot 只向界面要图：先启动 Outspread.Board.exe（双击 run-board.cmd），" +
                $"或用 --board 指到那块板正在打开的路径。原始错误：{ex.Message}");
            return 5;
        }

        string? line;

        try
        {
            // The board closes the pipe as soon as it has answered, so disposing our ends
            // afterwards throws "Cannot access a closed pipe" — on a request that worked.
            // Nothing is disposed with a using here, and the failures are swallowed.
            var writer = new StreamWriter(pipe, new UTF8Encoding(false)) { AutoFlush = false };
            writer.WriteLine(ShotProtocol.Serialize(request));
            writer.Flush();

            var reader = new StreamReader(pipe, Encoding.UTF8);
            line = reader.ReadLine();
        }
        catch (IOException ex)
        {
            Console.Error.WriteLine($"和界面说话说到一半断了：{ex.Message}");
            return 6;
        }
        finally
        {
            try
            {
                pipe.Dispose();
            }
            catch (IOException)
            {
            }
        }

        var reply = line is null ? null : ShotProtocol.ReadReply(line);

        if (reply is null)
        {
            Console.Error.WriteLine($"界面没给出能读的答复（原始一行：{line}）");
            return 6;
        }

        if (!reply.Ok)
        {
            Console.Error.WriteLine($"没截成：{reply.Error}");
            return 1;
        }

        Console.WriteLine($"PNG {reply.Path}");
        Console.WriteLine($"  {reply.Width}×{reply.Height} · 缩放 {reply.Zoom:0.###} · 板上 {reply.Nodes} 个节点");

        return 0;
    }

    private static (string Path, CanvasDocument Document) Open(Options options)
    {
        var resolution = BoardLocator.Resolve(options.Value("board"));

        if (!File.Exists(resolution.BoardPath))
        {
            throw new UsageException($"{resolution.BoardPath} 不存在（{resolution.Reason}）；先 outspread new 一块，或 --board 指个路径");
        }

        return (resolution.BoardPath, CanvasDocument.Parse(CanvasStore.ReadAllText(resolution.BoardPath), resolution.BoardPath));
    }

    private static CanvasNode Find(CanvasDocument document, string id)
        => document.Find(id) ?? throw new UsageException($"板上没有 id \"{id}\"；用 outspread list 看有哪些");

    private static string Describe(CanvasNode node, int index)
    {
        var builder = new StringBuilder();

        builder.Append(index.ToString().PadLeft(3));
        builder.Append("  ").Append(node.Id).Append("  ");
        builder.Append(CanvasNode.NameOfKind(node.Kind).PadRight(5));
        builder.Append($" x={Whole(node.X),5} y={Whole(node.Y),5} {Whole(node.Width)}×{Whole(node.Height)}");

        if (node.Color is { } color)
        {
            builder.Append("  color=").Append(color);
        }

        var content = node.Kind switch
        {
            CanvasNodeKind.Text => node.Text,
            CanvasNodeKind.File => node.FilePath,
            CanvasNodeKind.Link => node.Url,
            CanvasNodeKind.Group => node.Label,
            _ => null,
        };

        if (!string.IsNullOrEmpty(content))
        {
            var firstLine = content.Replace('\r', '\n').Split('\n')[0];

            builder.Append("  「").Append(firstLine.Length > 46 ? firstLine[..46] + "…" : firstLine).Append('」');
        }

        return builder.ToString();
    }

    private static long Whole(double value) => (long)Math.Round(value);

    private static double ParseNumber(string raw, string field)
        => double.TryParse(raw, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : throw new UsageException($"--{field} 要的是数字，给的是 \"{raw}\"");

    private static int Fail(string message)
    {
        Console.Error.WriteLine(message);
        return 2;
    }

    private sealed class UsageException(string message) : Exception(message);
}
