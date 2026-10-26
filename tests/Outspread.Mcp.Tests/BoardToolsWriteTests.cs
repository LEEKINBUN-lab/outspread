using Outspread.Core;
using Outspread.Mcp;

namespace Outspread.Mcp.Tests;

/// <summary>
/// 出口那几枚工具的写分支：落在 %TEMP% 的一块板上，绝不碰他真在用的那几块。
/// 2026-10-26 他勾 @n10 时我在自己机器上撞出来的两发都锁在这里：
/// 结论卡没号（他没法引用）、回执报了板上不存在的号（凭据是假的）。
/// </summary>
public sealed class BoardToolsWriteTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "outspread-mcp-tests-" + Guid.NewGuid().ToString("N")[..8]);

    public BoardToolsWriteTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    [Fact]
    public void Every_kind_gets_its_own_number_and_the_receipt_shows_those_exact_ones()
    {
        var board = SeededBoard(ordinal: 7);

        var receipt = BoardTools.BoardPost(
            "编号",
            [
                Card("note", "这是一条结论"),
                Card("task", "这是一条待办"),
                Card("risk", "这是一处缺陷"),
                Card("decision", "这是要他拍的"),
            ],
            round: "编号",
            board: board);

        var ordinals = CanvasStore.Load(board).Nodes
            .Select(node => Tags.Read(node.Text).Sequence)
            .Where(sequence => sequence is not null)
            .Select(sequence => sequence!.Value)
            .ToList();

        // 四格各一枚号，接在板上已有的 7 后面且不与之相撞：他"@n10 已结"这样引用才有唯一落点。
        Assert.Equal([7, 8, 9, 10, 11], ordinals.Order().ToArray());

        // 回执是他读到的凭据：上面写的号必须就是板上的号，一个都不能是编出来的。
        foreach (var line in receipt.Split('\n').Where(line => line.StartsWith("@n", StringComparison.Ordinal)))
        {
            var shown = int.Parse(line.Split(' ')[0][2..]);

            Assert.Contains(shown, ordinals);
        }
    }

    [Fact]
    public void Only_the_three_kinds_that_ask_something_of_him_carry_a_tick_mark()
    {
        var board = SeededBoard(ordinal: 0);

        BoardTools.BoardPost(
            "负担",
            [
                Card("note", "结论不该算进他的待办"),
                Card("task", "待办要算"),
                Card("risk", "缺陷要算"),
                Card("decision", "要拍的更要算"),
            ],
            round: "负担",
            board: board);

        var notes = CanvasStore.Load(board).Nodes
            .Where(node => Tags.Read(node.Text).Done is not null)
            .ToList();

        // 结论挂上牌子，"还欠几格"就会把无关的结论算进他的负担——那一行读数就不是凭据了。
        Assert.Equal(3, notes.Count);
        Assert.All(notes, node => Assert.False(Tags.Read(node.Text).Done));

        var unread = BoardTools.BoardRead(board, limit: 40)!.Split('\n')[1];

        Assert.Contains("还欠 3 格", unread);
    }

    [Fact]
    public void A_card_that_already_carries_marks_keeps_yours_instead_of_getting_a_second_number()
    {
        var board = SeededBoard(ordinal: 3);

        var receipt = BoardTools.BoardPost(
            "沿用",
            [Card("note", "@t我自己标的 @n99 @b0\n这行本来就是他的标记行")],
            round: "沿用",
            board: board);

        var kept = CanvasStore.Load(board).Nodes
            .Select(node => Tags.Read(node.Text).Sequence)
            .Where(sequence => sequence is not null)
            .Select(sequence => sequence!.Value)
            .ToList();

        // 沿用他自己那行的号，不再叠第二个号，也不把 4 这个空号发出去。
        Assert.Equal([3, 99], kept.Order().ToArray());
        Assert.Contains("@n—（沿用你写的标记行）", receipt);
    }

    private static BoardCard Card(string kind, string text) => new() { Kind = kind, Text = text };

    /// <summary>
    /// 种一块只有一格（带 @n&lt;ordinal&gt;）的板，用来验"接号"接的是板上已有的最大号。
    /// 标记必须自成一行且只有标记：后面跟着正文的那一行按板子的规矩算正文，不算号。
    /// </summary>
    private string SeededBoard(int ordinal)
    {
        var path = Path.Combine(_root, $"出口{ordinal}.canvas");

        File.WriteAllText(path, $$"""
            {"nodes":[{"id":"u1","type":"text","x":0,"y":0,"width":300,"height":100,"text":"@t已有 @n{{ordinal}}\n他先写的那格"}],"edges":[]}
            """);

        return path;
    }
}
