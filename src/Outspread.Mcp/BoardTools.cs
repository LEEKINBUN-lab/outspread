using System.ComponentModel;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using ModelContextProtocol.Server;
using Outspread.Core;

namespace Outspread.Mcp;

/// <summary>
/// 板子给 agent 用的那五枚工具。写盘全走 <see cref="CanvasStore.SavePatch"/>——
/// 和界面同一把命名互斥、同一套原子替换，所以 agent 和你可以在同一秒各写各的。
/// </summary>
[McpServerToolType]
public sealed class BoardTools
{
    private const int CardWidth = 360;

    private const int CardHeight = 90;

    private const int ColumnGap = 24;

    [McpServerTool(Name = "board_post"), Description(
        "把一份汇报写成卡片贴到画板上——这是 agent 的**表达出口**：写完你就能在板子上看到，不用去翻聊天记录。"
        + "会先落一枚标题卡，再把每条结论各落一张卡，全部打上同一个 @标签，方便你用筛选面板只看这一轮。"
        + "返回落到了哪块板、每格的编号。")]
    public static string BoardPost(
        [Description("标题，会成为这一组最上面那枚横贯整行的卡")] string title,
        [Description("要写的条目，每条一张卡；正文支持 Markdown，也可以自带 @d/@t/@n/@b 标记行")] string[] items,
        [Description("主题短名（建议英文或小写，会打成 @标签）。不填就用标题的前几个字。")] string? topic = null,
        [Description("画板文件路径。不填就落到你的「Agent 出口」板（没有会自动建）。")] string? board = null,
        [Description("一行摆几张卡，1 到 4，默认 3。")] int columns = 3)
    {
        if (string.IsNullOrWhiteSpace(title))
        {
            return "标题不能空：这一发没写。";
        }

        if (items.Length == 0)
        {
            return "一条都没有，这一发没写。";
        }

        var path = ResolveBoard(board);
        var tag = "@t" + NormaliseTag(topic is { Length: 0 } or null ? title : topic!);
        var document = CanvasStore.LoadOrCreate(path);

        var (left, top) = FreeColumnBelow(document);
        var across = Math.Clamp(columns, 1, 4);
        var rowWidth = (across * CardWidth) + ((across - 1) * ColumnGap);

        var patch = new DocPatch();
        var written = new List<string>();

        patch.Additions.Add(Card(NodeIds.New(NodeIds.AgentNamespace), left, top, $"### {title}\n\n{tag}\n{DateTime.Now:yyyy-MM-dd HH:mm}", rowWidth, 88));

        var rowTop = top + 88 + ColumnGap;
        var rowTallest = 0.0;
        var slot = 0;

        foreach (var item in items)
        {
            var body = item.Trim();

            if (body.Length == 0)
            {
                continue;
            }

            if (slot % across == 0 && slot > 0)
            {
                rowTop += rowTallest + ColumnGap;
                rowTallest = 0;
            }

            // 标记行放最前面：他板子上那套约定是"@n8 @b0 换行 正文"，牌子才会落到卡片左下角。
            var text = Tags.LooksLikePropertyLine(body.Split('\n')[0]) ? body : $"{tag}\n{body}";

            // 高度按**折行后**的行数估（一行按 15px 一个汉字算），再多留一行：
            // 只数硬换行会把一句长句当一行，卡片短一截，多出来的那行被渐隐吃掉——看着像话没说完。
            var wrapped = body.Split('\n')
                .Sum(line => Math.Max(1, (int)Math.Ceiling(line.Length / (CardWidth / 17.0))));
            var height = Math.Max(CardHeight, 44 + (22 * (wrapped + 2)));
            var x = left + ((slot % across) * (CardWidth + ColumnGap));

            patch.Additions.Add(Card(NodeIds.New(NodeIds.AgentNamespace), x, rowTop, text, CardWidth, height));
            written.Add($"{x:0},{rowTop:0} {height:0}");
            rowTallest = Math.Max(rowTallest, height);
            slot++;
        }

        CanvasStore.SavePatch(path, patch);

        return $"已写到 {path}\n标题：{title}\n标签：{tag}\n落了 {written.Count} 条 + 1 枚标题卡（一行 {across} 张），位置：{string.Join(" / ", written)}";
    }

    [McpServerTool(Name = "board_read"), Description("读一块板：每格一行，给编号、首行、标签、位置。想知道板子上现在有什么就先叫它。")]
    public static string BoardRead(
        [Description("画板文件路径。不填就用你的「Agent 出口」板。")] string? board = null,
        [Description("最多列几格，默认 40。")] int limit = 40)
    {
        var path = ResolveBoard(board);

        if (!File.Exists(path))
        {
            return $"{path} 还没有这块板。";
        }

        var document = CanvasStore.Load(path);
        var lines = document.Nodes
            .Where(node => node.Kind != CanvasNodeKind.Group)
            .OrderBy(node => node.Y)
            .ThenBy(node => node.X)
            .Take(Math.Clamp(limit, 1, 200))
            .Select(node => $"{node.Id} | {FirstLine(node.Text)} | {string.Join(",", Tags.Of(node.Text))} | {node.X:0},{node.Y:0} {node.Width:0}×{node.Height:0}");

        return $"{path}\n" + string.Join("\n", lines);
    }

    [McpServerTool(Name = "board_search"), Description("在一块板里找字（也认 @标签），返回命中的格子。回答\"上次那条写哪去了\"这类问题用它。")]
    public static string BoardSearch(
        [Description("要找的词，不分大小写；以 @ 开头就按标签找")] string query,
        [Description("画板文件路径。不填就用你的「Agent 出口」板。")] string? board = null)
    {
        var path = ResolveBoard(board);

        if (!File.Exists(path) || query.Trim().Length == 0)
        {
            return $"{path}：没这块板或没给词。";
        }

        var needle = query.Trim();
        var byTag = needle.StartsWith('@');

        var hits = CanvasStore.Load(path).Nodes
            .Where(node => byTag
                ? Tags.Of(node.Text).Any(tag => needle[1..].Equals(tag, StringComparison.OrdinalIgnoreCase))
                : (node.Text ?? string.Empty).Contains(needle, StringComparison.OrdinalIgnoreCase))
            .Take(40)
            .Select(node => $"{node.Id} | {FirstLine(node.Text)} | {node.X:0},{node.Y:0}")
            .ToList();

        return hits.Count == 0 ? $"没找到「{needle}」。" : string.Join("\n", hits);
    }

    [McpServerTool(Name = "board_patch"), Description(
        "改一格：换正文、加一个 @标签、或把它标成已完成（@b1）。你用来收口，agent 用来更新同一件事而不是再贴一张新的。")]
    public static string BoardPatch(
        [Description("格子编号")] string id,
        [Description("新正文；不填就不动")] string? text = null,
        [Description("要加的 @标签（不带 @）")] string? add_tag = null,
        [Description("true＝标已完成，false＝标未完成，不填＝不动")] bool? done = null,
        [Description("画板文件路径。不填就用你的「Agent 出口」板。")] string? board = null)
    {
        var path = ResolveBoard(board);

        if (!File.Exists(path))
        {
            return $"{path}：没这块板。";
        }

        var document = CanvasStore.Load(path);
        var node = document.Find(id);

        if (node is null)
        {
            return $"{path} 里没有 {id} 这一格。";
        }

        var body = text ?? node.Text ?? string.Empty;

        if (add_tag is { Length: > 0 })
        {
            body = Tags.AddTag(body, add_tag!.TrimStart('@'));
        }

        if (done is { } finished)
        {
            body = Tags.SetDone(body, finished);
        }

        var patch = new DocPatch();

        patch.SetString(node.Id, "text", body);

        CanvasStore.SavePatch(path, patch);

        return $"{node.Id} 已更新：{FirstLine(body)}";
    }

    [McpServerTool(Name = "board_shot"), Description(
        "向**开着的那块板**要一张 PNG（它画的就是你屏幕上那张）。落完一批东西后用来自查。没人开着这块板会直接说没人，不会自己另画一张。")]
    public static async Task<string> BoardShot(
        [Description("PNG 落盘路径")] string @out,
        [Description("view=当前视野 / fit=整块板 / node=单格（要配 id）")] string mode = "view",
        [Description("画板文件路径。不填就用你的「Agent 出口」板。")] string? board = null,
        [Description("mode=node 时要拍的那格编号")] string? id = null)
    {
        var path = ResolveBoard(board);

        var request = new ShotRequest(ShotProtocol.RequestedOp, @out, mode, id);

        var pipe = new NamedPipeClientStream(".", ShotProtocol.PipeName(path), PipeDirection.InOut, PipeOptions.Asynchronous);

        try
        {
            await pipe.ConnectAsync(2000);
        }
        catch (Exception ex)
        {
            return $"这块板没开着（{ex.GetType().Name}），没人能答这一发。";
        }

        try
        {
            await using (pipe)
            {
                await JsonSerializer.SerializeAsync(pipe, request);
                await pipe.FlushAsync();

                var reply = await JsonSerializer.DeserializeAsync<ShotReply>(pipe);

                return reply is null
                    ? "界面答了个空的。"
                    : reply.Ok
                        ? $"{reply.Path} · {reply.Width}×{reply.Height} · 缩放 {reply.Zoom:P0} · {reply.Nodes} 个节点"
                        : $"没拍成：{reply.Error}";
            }
        }
        catch (Exception ex)
        {
            return $"没拍成：{ex.Message}";
        }
    }

    private static AddedNode Card(string id, double x, double y, string text, double width, double height)
        => new()
        {
            Id = id,
            Json = new JsonObject
            {
                ["id"] = id,
                ["type"] = "text",
                ["x"] = (long)Math.Round(x),
                ["y"] = (long)Math.Round(y),
                ["width"] = (long)Math.Round(width),
                ["height"] = (long)Math.Round(height),
                ["text"] = text,
            },
        };

    /// <summary>
    /// 「Agent 出口」落在哪儿：贴着**他真实开着的板**放。闸门会把自检跑在 %TEMP% 的副本上并留下
    /// last-board，那块出口板不能落在临时目录——所以先翻 open-boards 里第一条不在 temp 的，
    /// 再看 last-board，都没有就退回应用的默认板目录。
    /// </summary>
    private static string ResolveBoard(string? board)
    {
        if (board is { Length: > 0 })
        {
            return Path.GetFullPath(board);
        }

        var prefs = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Outspread");

        var anchors = new List<string>();

        foreach (var file in new[] { "open-boards.txt", "last-board.txt" })
        {
            var path = Path.Combine(prefs, file);

            if (File.Exists(path))
            {
                anchors.AddRange(File.ReadAllLines(path).Select(line => line.Trim()).Where(line => line.Length > 0));
            }
        }

        var temp = Path.TrimEndingDirectorySeparator(Path.GetTempPath());

        var directory = anchors
            .Select(candidate => Path.GetFullPath(candidate))
            .FirstOrDefault(candidate =>
                File.Exists(candidate)
                && !Path.TrimEndingDirectorySeparator(candidate)
                    .StartsWith(temp + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) is { } real
            ? Path.GetDirectoryName(real)!
            : Path.GetDirectoryName(BoardLocator.FallbackPath())!;

        return Path.Combine(directory, "Agent 出口.canvas");
    }

    /// <summary>新内容一律落在"最低那格下面"：他往上看就是历史，往下走就是最新。</summary>
    private static (double X, double Y) FreeColumnBelow(CanvasDocument document)
    {
        var bottom = document.Nodes
            .Where(node => node.Kind != CanvasNodeKind.Group)
            .Select(node => node.Y + node.Height)
            .DefaultIfEmpty(0);

        var lowest = bottom.Max();
        var left = document.Nodes.Count == 0 ? 0 : document.Nodes.Min(node => node.X);

        return (left, lowest + ColumnGap);
    }

    private static string NormaliseTag(string raw)
    {
        var builder = new StringBuilder();

        foreach (var character in raw.Trim())
        {
            if (char.IsLetterOrDigit(character) || character is '-' or '_')
            {
                builder.Append(char.ToLowerInvariant(character));
            }
            else if (builder.Length > 0 && builder[^1] != '-')
            {
                builder.Append('-');
            }

            if (builder.Length >= 24)
            {
                break;
            }
        }

        return builder.ToString().Trim('-');
    }

    private static string FirstLine(string? text)
    {
        var line = (text ?? string.Empty)
            .Split('\n')
            .FirstOrDefault(candidate => candidate.Trim().Length > 0 && !Tags.LooksLikePropertyLine(candidate))?
            .Trim() ?? string.Empty;

        return line.Length <= 46 ? line : line[..46] + "…";
    }
}
