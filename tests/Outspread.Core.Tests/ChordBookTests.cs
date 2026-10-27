using System.Text;
using Outspread.Core;

namespace Outspread.Core.Tests;

/// <summary>
/// 改键那一张表：语法、规范化、落盘与读回。
/// </summary>
/// <remarks>
/// 这一发只管"命令名 → 键位串"这一层；"哪条命令占着哪个键"属于命令表，归界面自检那一发。
/// 全部读写都在 %TEMP% 的一次性小目录里，碰不到他 <c>%LOCALAPPDATA%\Outspread\</c> 里的真表。
/// </remarks>
public sealed class ChordBookTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "outspread-chord-tests-" + Guid.NewGuid().ToString("N")[..8]);

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private string PathHere => ChordBook.PathIn(_root);

    [Theory]
    [InlineData("Alt+K", "Alt+K")]
    [InlineData("Ctrl+G", "Ctrl+G")]
    [InlineData("Shift+Number1", "Shift+Number1")]
    [InlineData("Alt+Left", "Alt+Left")]

    // 手写的顺序折成"派发拼串"那一个顺序：否则文件里的写法与按键拼出来的串永远对不上。
    [InlineData("Alt+Ctrl+K", "Ctrl+Alt+K")]
    [InlineData(" alt + k ", "Alt+K")]
    public void A_chord_is_canonicalised_to_the_order_dispatch_builds(string written, string canonical)
    {
        Assert.True(ChordBook.TryCanonical(written, out var got));
        Assert.Equal(canonical, got);
    }

    [Theory]
    [InlineData("")]
    [InlineData("K")]                 // 没有修饰键
    [InlineData("Ctrl+")]             // 没有主键
    [InlineData("Ctrl+Alt")]          // 只有两枚修饰键，没有主键
    [InlineData("Ctrl+Ctrl+K")]       // 修饰键重复
    [InlineData("Alt+B+K")]           // 两个主键
    [InlineData("Ctrl+Control")]      // 主键是修饰键
    [InlineData("Meta+K")]            // 不认识的修饰键
    [InlineData("Ctrl+左")]           // 主键不是 e.Key 的名字
    public void A_chord_needs_a_modifier_and_exactly_one_key(string written)
    {
        Assert.False(ChordBook.TryCanonical(written, out var canonical));
        Assert.Empty(canonical);
        Assert.False(ChordBook.IsBindable(written));
    }

    [Fact]
    public void Changing_one_command_leaves_every_other_on_its_default()
    {
        var book = ChordBook.Empty();

        Assert.True(book.Set("左对齐", "alt+k"));
        Assert.Equal("Alt+K", book.Resolve("左对齐", "Alt+Left"));
        Assert.Equal("Alt+Right", book.Resolve("右对齐", "Alt+Right"));
        Assert.True(book.Any);

        Assert.False(book.Set("左对齐", "K"));
        Assert.Equal("Alt+K", book.Resolve("左对齐", "Alt+Left"));
    }

    [Fact]
    public void What_is_written_is_what_comes_back()
    {
        Directory.CreateDirectory(_root);

        var book = ChordBook.Empty();

        Assert.True(book.Set("左对齐", "Alt+F9"));
        Assert.Null(book.Save(PathHere));

        var (reread, problem) = ChordBook.Load(PathHere);

        Assert.Null(problem);
        Assert.Equal("Alt+F9", reread.Resolve("左对齐", "Alt+Left"));

        // 恢复默认之后盘上也不许再留着那条——删掉一行就是"这一条回到表里的值"。
        reread.Reset();
        Assert.Null(reread.Save(PathHere));
        Assert.False(ChordBook.Load(PathHere).Book.Any);
    }

    [Fact]
    public void A_missing_file_is_no_remapping_rather_than_an_error()
    {
        var (book, problem) = ChordBook.Load(PathHere);

        Assert.Null(problem);
        Assert.False(book.Any);
        Assert.Equal("Alt+Left", book.Resolve("左对齐", "Alt+Left"));
    }

    [Fact]
    public void Lines_it_cannot_read_are_dropped_out_loud_and_the_rest_still_loads()
    {
        Directory.CreateDirectory(_root);

        File.WriteAllText(
            PathHere,
            string.Join(
                "\r\n",
                "# 键位",
                "左对齐=Alt+F9",
                "右对齐=不是键位",
                "缺等号的一行",
                string.Empty,
                "上对齐=ctrl+shift+u") + "\r\n",
            new UTF8Encoding(false));

        var (book, problem) = ChordBook.Load(PathHere);

        Assert.NotNull(problem);
        Assert.Contains("2 行", problem);
        Assert.Equal("Alt+F9", book.Resolve("左对齐", "Alt+Left"));
        Assert.Equal("Ctrl+Shift+U", book.Resolve("上对齐", "Alt+Up"));
        Assert.Equal("Alt+Right", book.Resolve("右对齐", "Alt+Right"));
    }

    [Fact]
    public void A_written_file_keeps_the_labels_as_they_were_given()
    {
        Directory.CreateDirectory(_root);

        var book = ChordBook.Empty();

        Assert.True(book.Set("堆叠", "Alt+B"));
        Assert.Null(book.Save(PathHere));

        // 中文标签要能原样往返：文件是 UTF-8，谁的码页都不许插手。
        Assert.Contains("堆叠=Alt+B", File.ReadAllText(PathHere, Encoding.UTF8));
    }

    [Fact]
    public void A_save_that_cannot_be_written_comes_back_as_a_reason()
    {
        // 父路径是一个文件：建目录这一步就过不去，返回的必须是"为什么"，而不是一声不吭。
        Directory.CreateDirectory(_root);

        var blocker = Path.Combine(_root, "chords.txt");

        File.WriteAllText(blocker, "not a directory");

        var book = ChordBook.Empty();

        Assert.True(book.Set("左对齐", "Alt+F9"));

        var problem = book.Save(Path.Combine(blocker, "chords.txt"));

        Assert.NotNull(problem);
        Assert.NotEmpty(problem);
    }
}
