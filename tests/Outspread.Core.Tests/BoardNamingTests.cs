namespace Outspread.Core.Tests;

/// <summary>
/// Which board file a name means — the question the tab strip asks every time someone types one.
/// </summary>
public class BoardNamingTests
{
    [Fact]
    public void A_name_becomes_a_board_in_the_folder_it_was_asked_for()
    {
        using var scratch = new Scratch("naming");

        var result = BoardLocator.NewBoard(scratch.Folder, "周会");

        Assert.True(result.Ok);
        Assert.Equal(Path.Combine(scratch.Folder, "周会.canvas"), result.BoardPath);
    }

    [Fact]
    public void A_name_with_the_extension_already_on_it_is_not_written_twice()
    {
        using var scratch = new Scratch("naming");

        var result = BoardLocator.NewBoard(scratch.Folder, "周会.canvas");

        Assert.Equal(Path.Combine(scratch.Folder, "周会.canvas"), result.BoardPath);
    }

    [Theory]
    [InlineData("../别的目录")]
    [InlineData("a/b")]
    [InlineData("a\\b")]
    [InlineData("c:d")]
    [InlineData("a..b")]
    [InlineData("名字.")]
    [InlineData(".sync")]
    [InlineData("有*号")]
    public void A_name_that_could_land_outside_the_folder_is_refused_and_nothing_is_written(string name)
    {
        using var scratch = new Scratch("naming");

        var result = BoardLocator.NewBoard(scratch.Folder, name);

        Assert.False(result.Ok);
        Assert.NotNull(result.Problem);

        // 拒了还留下东西＝下一次同名会被告知"这块板已经在了"，而屏上什么都没有。
        Assert.Empty(Directory.GetFileSystemEntries(scratch.Folder));
    }

    [Fact]
    public void An_existing_board_is_refused_rather_than_emptied()
    {
        using var scratch = new Scratch("naming");

        File.WriteAllText(Path.Combine(scratch.Folder, "周会.canvas"), "{\"nodes\":[],\"edges\":[]}");

        var result = BoardLocator.NewBoard(scratch.Folder, "周会");

        Assert.False(result.Ok);
        Assert.Contains("已存在", result.Problem, StringComparison.Ordinal);
    }

    [Fact]
    public void A_name_too_long_for_the_strip_is_refused_before_it_gets_there()
    {
        using var scratch = new Scratch("naming");

        var result = BoardLocator.NewBoard(scratch.Folder, new string('字', BoardLocator.MaxBoardNameChars + 1));

        Assert.False(result.Ok);
        Assert.Contains("上限", result.Problem, StringComparison.Ordinal);
    }

    [Fact]
    public void A_blank_name_says_so_instead_of_making_a_board_called_dot()
    {
        using var scratch = new Scratch("naming");

        foreach (var name in new[] { "", "   ", ".canvas" })
        {
            var result = BoardLocator.NewBoard(scratch.Folder, name);

            Assert.False(result.Ok);
            Assert.Contains("空", result.Problem, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void Renaming_resolves_the_new_path_without_touching_anything()
    {
        using var scratch = new Scratch("naming");

        var result = BoardLocator.Rename(scratch.BoardPath, "复盘");

        Assert.True(result.Ok);
        Assert.Equal(Path.Combine(scratch.Folder, "复盘.canvas"), result.BoardPath);
        Assert.False(File.Exists(scratch.BoardPath));
    }

    [Fact]
    public void Renaming_to_the_same_name_is_legal_and_lands_on_the_same_path()
    {
        using var scratch = new Scratch("naming");

        var result = BoardLocator.Rename(scratch.BoardPath, "scratch");

        Assert.True(result.Ok);
        Assert.Equal(scratch.BoardPath, result.BoardPath);
    }

    [Fact]
    public void Renaming_to_a_name_another_board_already_has_is_refused()
    {
        using var scratch = new Scratch("naming");

        File.WriteAllText(Path.Combine(scratch.Folder, "周会.canvas"), "{}");

        var result = BoardLocator.Rename(scratch.BoardPath, "周会");

        Assert.False(result.Ok);
        Assert.Contains("已存在", result.Problem, StringComparison.Ordinal);
    }

    [Fact]
    public void Renaming_only_the_case_is_legal_and_points_at_the_new_spelling()
    {
        using var scratch = new Scratch("naming");

        var result = BoardLocator.Rename(scratch.BoardPath, "Scratch");

        Assert.True(result.Ok);
        Assert.Equal("Scratch.canvas", Path.GetFileName(result.BoardPath));
    }

    [Theory]
    [InlineData("")]
    [InlineData("../别的目录")]
    [InlineData("名字.")]
    [InlineData("a..b")]
    [InlineData("有*号")]
    public void A_rename_that_could_land_outside_the_folder_is_refused(string name)
    {
        using var scratch = new Scratch("naming");

        var result = BoardLocator.Rename(scratch.BoardPath, name);

        Assert.False(result.Ok);
        Assert.NotNull(result.Problem);
    }

    [Fact]
    public void An_untitled_board_starts_at_one_and_skips_a_non_empty_name()
    {
        using var scratch = new Scratch("naming");

        File.WriteAllText(
            Path.Combine(scratch.Folder, "未命名板1.canvas"),
            """{"nodes":[{"id":"n1","type":"text","x":0,"y":0,"width":10,"height":10,"text":"x"}],"edges":[]}""");

        var result = BoardLocator.NewUntitledBoard(scratch.Folder);

        Assert.True(result.Ok);
        Assert.Equal(Path.Combine(scratch.Folder, "未命名板2.canvas"), result.BoardPath);
    }

    [Fact]
    public void An_empty_untitled_shell_is_reused_so_closing_and_creating_returns_to_one()
    {
        using var scratch = new Scratch("naming");

        File.WriteAllText(Path.Combine(scratch.Folder, "未命名板1.canvas"), """{"nodes":[],"edges":[]}""");

        var result = BoardLocator.NewUntitledBoard(scratch.Folder);

        Assert.True(result.Ok);
        Assert.Equal(Path.Combine(scratch.Folder, "未命名板1.canvas"), result.BoardPath);
    }

    [Fact]
    public void An_open_untitled_shell_is_not_reused()
    {
        using var scratch = new Scratch("naming");

        var first = Path.Combine(scratch.Folder, "未命名板1.canvas");
        File.WriteAllText(first, """{"nodes":[],"edges":[]}""");

        var result = BoardLocator.NewUntitledBoard(scratch.Folder, new[] { first });

        Assert.True(result.Ok);
        Assert.Equal(Path.Combine(scratch.Folder, "未命名板2.canvas"), result.BoardPath);
    }

    [Fact]
    public void The_folder_listing_carries_boards_and_nothing_else()
    {
        using var scratch = new Scratch("naming");

        File.WriteAllText(Path.Combine(scratch.Folder, "b.canvas"), "{}");
        File.WriteAllText(Path.Combine(scratch.Folder, "a.canvas"), "{}");

        // 视图件、冲突件、图都不许混进来：冲突副本一旦被视为一块板，一次失败的同步就凭空多出一块板。
        File.WriteAllText(Path.Combine(scratch.Folder, "a.canvas.view.json"), "{}");
        File.WriteAllText(Path.Combine(scratch.Folder, "a.canvas.conflict-DEV-20261021-2200-r1"), "{}");
        File.WriteAllText(Path.Combine(scratch.Folder, "notes.txt"), "");
        Directory.CreateDirectory(Path.Combine(scratch.Folder, "assets"));

        var boards = BoardLocator.BoardsIn(scratch.Folder);

        Assert.Equal(
            new[] { Path.Combine(scratch.Folder, "a.canvas"), Path.Combine(scratch.Folder, "b.canvas") },
            boards);
    }

    [Fact]
    public void A_folder_that_is_not_there_lists_no_boards_instead_of_throwing()
    {
        var boards = BoardLocator.BoardsIn(Path.Combine(Path.GetTempPath(), "outspread-tests", "没有这个目录"));

        Assert.Empty(boards);
    }
}
