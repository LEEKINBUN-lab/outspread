namespace Outspread.Core.Tests;

/// <summary>
/// What survives a restart: which board this window was last on, and which boards were open at all.
/// Both are files on disk, so both are tested here rather than by launching the window.
/// </summary>
public class BoardSessionTests
{
    [Fact]
    public void A_remembered_board_is_read_back_as_an_absolute_path()
    {
        using var scratch = new Scratch("session-remember");
        var state = Path.Combine(scratch.Folder, "state");
        File.WriteAllText(scratch.BoardPath, CanvasDocument.CreateNew().ToJson());

        Assert.Null(BoardSession.RememberLastBoard(scratch.BoardPath, state));
        Assert.Equal(Path.GetFullPath(scratch.BoardPath), BoardSession.ReadLastBoard(state));
    }

    [Fact]
    public void No_memory_reads_as_null_so_the_first_launch_rule_runs()
    {
        using var scratch = new Scratch("session-empty");

        Assert.Null(BoardSession.ReadLastBoard(Path.Combine(scratch.Folder, "nothing-here")));
    }

    [Fact]
    public void A_remembered_board_that_was_deleted_reads_as_null()
    {
        using var scratch = new Scratch("session-gone");
        var state = Path.Combine(scratch.Folder, "state");
        var doomed = Path.Combine(scratch.Folder, "closed.canvas");
        File.WriteAllText(doomed, CanvasDocument.CreateNew().ToJson());

        Assert.Null(BoardSession.RememberLastBoard(doomed, state));
        File.Delete(doomed);

        Assert.Null(BoardSession.ReadLastBoard(state));
    }

    /// <summary>
    /// 2026-10-23 他要的第二条记忆：开着的那一排整排回来（"我打开后没有快速入门的页面了"）。
    /// 顺序就是标签顺序，所以这一条断言的是**顺序**，不是"数量对"。
    /// </summary>
    [Fact]
    public void The_open_row_comes_back_in_tab_order()
    {
        using var scratch = new Scratch("session-open-row");
        var state = Path.Combine(scratch.Folder, "state");
        var boards = new[] { "快速入门", "周会", "未命名板1" }
            .Select(name => Path.Combine(scratch.Folder, name + CanvasStore.Extension))
            .ToArray();

        foreach (var board in boards)
        {
            File.WriteAllText(board, CanvasDocument.CreateNew().ToJson());
        }

        Assert.Null(BoardSession.RememberOpenBoards(boards, state));
        Assert.Equal(boards.Select(Path.GetFullPath), BoardSession.ReadOpenBoards(state));
    }

    [Fact]
    public void A_board_closed_since_last_time_does_not_come_back_as_a_dead_tab()
    {
        using var scratch = new Scratch("session-open-row-gone");
        var state = Path.Combine(scratch.Folder, "state");
        var kept = Path.Combine(scratch.Folder, "留着.canvas");
        var doomed = Path.Combine(scratch.Folder, "空板.canvas");
        File.WriteAllText(kept, CanvasDocument.CreateNew().ToJson());
        File.WriteAllText(doomed, CanvasDocument.CreateNew().ToJson());

        Assert.Null(BoardSession.RememberOpenBoards(new[] { kept, doomed }, state));
        File.Delete(doomed);

        Assert.Equal(new[] { Path.GetFullPath(kept) }, BoardSession.ReadOpenBoards(state));
    }

    [Fact]
    public void No_remembered_row_reads_as_empty_not_as_a_broken_state()
    {
        using var scratch = new Scratch("session-open-row-empty");

        // 空表＝"没有记忆"，调用方按第一次开屏处理；它不能抛，也不能被当成"他关掉了所有板子"。
        Assert.Empty(BoardSession.ReadOpenBoards(Path.Combine(scratch.Folder, "nothing-here")));
    }

    [Fact]
    public void The_quick_start_board_is_found_while_walking_up()
    {
        using var scratch = new Scratch("session-welcome");
        var boards = Path.Combine(scratch.Folder, "boards");
        var nested = Path.Combine(scratch.Folder, "bin", "x64");
        Directory.CreateDirectory(boards);
        Directory.CreateDirectory(nested);

        var welcome = Path.Combine(boards, BoardLocator.QuickStartFileName);
        File.WriteAllText(welcome, CanvasDocument.CreateNew().ToJson());

        Assert.Equal(welcome, BoardLocator.FindQuickStart(nested));
    }

    [Fact]
    public void A_board_with_another_name_is_not_mistaken_for_the_quick_start()
    {
        using var scratch = new Scratch("session-not-welcome");
        var boards = Path.Combine(scratch.Folder, "boards");
        Directory.CreateDirectory(boards);
        File.WriteAllText(Path.Combine(boards, "周会.canvas"), CanvasDocument.CreateNew().ToJson());

        // 断言的是"认名字，不认目录里第一块 .canvas"，而不是"这一发一定 null"——%TEMP% 的祖先里
        // 若真有一块教程板，那也不该由这条测试来判红。
        Assert.NotEqual(
            Path.Combine(boards, "周会.canvas"),
            BoardLocator.FindQuickStart(scratch.Folder));
    }
}
