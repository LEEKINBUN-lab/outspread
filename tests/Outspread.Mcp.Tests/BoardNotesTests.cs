using Outspread.Core;
using Outspread.Mcp;

namespace Outspread.Mcp.Tests;

/// <summary>
/// 零参数看板：用户在另一个项目里说「看看画板」，agent 不需要先找路径。
/// 记忆文件放 %TEMP% 的独立目录，绝不碰他真在用的那几块板。
/// </summary>
public sealed class BoardNotesTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "outspread-board-notes-" + Guid.NewGuid().ToString("N")[..8]);

    public BoardNotesTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    [Fact]
    public void Zero_argument_reads_the_board_he_was_last_on_and_leaves_no_inbox_file()
    {
        var guide = Board("快速入门", "教程便签", "u111111111111");
        var todo = Board("待办事项", "交论文", "u222222222222");

        Remember(guide, todo);
        Last(todo);

        var notes = BoardTools.BoardNotesFor(_root);

        Assert.Contains("板：待办事项", notes);
        Assert.Contains("交论文", notes);
        Assert.DoesNotContain("教程便签", notes);

        // 只读工具不该像 board_inbox 那样落账：下一次真正的对账不能被这一发抹平。
        Assert.False(File.Exists(todo + ".inbox.json"));
    }

    [Fact]
    public void The_all_notes_sentinel_reads_the_union_instead_of_saying_there_is_no_board()
    {
        var guide = Board("快速入门", "教程便签", "u111111111111");
        var todo = Board("待办事项", "交论文", "u222222222222");

        Remember(guide, todo);
        Last("<<所有便签>>");

        var notes = BoardTools.BoardNotesFor(_root);

        Assert.Contains("所有便签：跨 2 块板", notes);
        Assert.Contains("教程便签", notes);
        Assert.Contains("交论文", notes);
    }

    [Fact]
    public void A_bare_board_name_resolves_against_the_open_boards()
    {
        var guide = Board("快速入门", "教程便签", "u111111111111");
        var todo = Board("待办事项", "交论文", "u222222222222");

        Remember(guide, todo);
        Last(todo);

        var notes = BoardTools.BoardNotesFor(_root, "快速入门");

        Assert.Contains("板：快速入门", notes);
        Assert.Contains("教程便签", notes);
        Assert.DoesNotContain("交论文", notes);
    }

    [Fact]
    public void Board_list_shows_the_last_board_the_open_ones_and_siblings()
    {
        var guide = Board("快速入门", "教程便签", "u111111111111");
        var todo = Board("待办事项", "交论文", "u222222222222");
        Board("备忘录", "买菜", "u333333333333");

        Remember(guide, todo);
        Last(todo);

        var list = BoardTools.BoardListFor(_root);

        Assert.Contains("他最后在看：待办事项", list);
        Assert.Contains("- 快速入门 | 1 格", list);
        Assert.Contains("- 待办事项 | 1 格 | 最后在看", list);
        Assert.Contains("同目录还有：备忘录", list);
    }

    [Fact]
    public void No_memory_says_so_instead_of_inventing_a_board()
    {
        var empty = Path.Combine(_root, "empty");
        Directory.CreateDirectory(empty);

        Assert.Contains("没有记忆中的板", BoardTools.BoardNotesFor(empty));
        Assert.Contains("没有记忆", BoardTools.BoardListFor(empty));
    }

    private string Board(string name, string text, string id)
    {
        var path = Path.Combine(_root, name + ".canvas");

        File.WriteAllText(
            path,
            "{\n  \"nodes\": [\n    {\n"
            + $"      \"id\": \"{id}\",\n"
            + "      \"type\": \"text\",\n      \"x\": 0,\n      \"y\": 0,\n"
            + "      \"width\": 200,\n      \"height\": 80,\n"
            + $"      \"text\": \"{text}\"\n"
            + "    }\n  ],\n  \"edges\": []\n}\n");

        return path;
    }

    private void Remember(params string[] boards)
        => File.WriteAllLines(Path.Combine(_root, BoardSession.OpenBoardsFileName), boards);

    private void Last(string board)
        => File.WriteAllText(Path.Combine(_root, BoardSession.LastBoardFileName), board);
}
