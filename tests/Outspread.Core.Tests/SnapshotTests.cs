using System;
using System.IO;
using System.Linq;
using Outspread.Core;
using Xunit;

namespace Outspread.Core.Tests;

/// <summary>
/// The write-ahead history: the copy of the bytes about to be replaced, and the rules that decide
/// when one is kept. Ordered the way Obsidian documents its own recovery — a device-local snapshot
/// at a minimum spacing, kept for days, restorable — and the gates are the ones that keep that from
/// turning into a disk full of copies.
/// </summary>
public class SnapshotTests : IDisposable
{
    private readonly Scratch _scratch = new("snapshots");

    private static readonly DateTimeOffset T0 = new(2026, 10, 12, 2, 0, 0, TimeSpan.FromHours(8));

    public void Dispose() => _scratch.Dispose();

    private string BoardNamed(string name)
    {
        var path = Path.Combine(_scratch.Folder, name);

        File.WriteAllText(path, """{"nodes":[],"edges":[]}""");

        return path;
    }

    [Fact]
    public void A_brand_new_board_has_nothing_to_protect_yet()
    {
        var board = BoardNamed("new.canvas");

        Assert.Null(Snapshots.TryPreserve(board, string.Empty, T0));
        Assert.Empty(Snapshots.List(board));
    }

    [Fact]
    public void The_bytes_about_to_be_replaced_are_the_ones_kept()
    {
        var board = BoardNamed("kept.canvas");
        var before = File.ReadAllText(board);

        var stored = Snapshots.TryPreserve(board, before, T0);

        Assert.NotNull(stored);
        Assert.Equal(before, Snapshots.Read(Snapshots.List(board).Single()));
    }

    [Fact]
    public void Routine_saves_inside_the_interval_store_one()
    {
        var board = BoardNamed("throttle.canvas");

        File.WriteAllText(board, "v1");
        Assert.NotNull(Snapshots.TryPreserve(board, "v1", T0));

        File.WriteAllText(board, "v2");
        Assert.Null(Snapshots.TryPreserve(board, "v2", T0.AddMinutes(1)));

        // 过了间隔才允许下一份，且存的是"要被换掉的那一份"（v2），不是最早那份。
        Assert.NotNull(Snapshots.TryPreserve(board, "v2", T0.AddMinutes(6)));

        var kept = Snapshots.List(board).Select(Snapshots.Read).ToList();

        Assert.Equal(2, kept.Count);
        Assert.Equal("v1", kept[1]);
        Assert.Equal("v2", kept[0]);
    }

    [Fact]
    public void Identical_bytes_are_never_stored_twice()
    {
        var board = BoardNamed("same.canvas");

        File.WriteAllText(board, "v1");
        Assert.NotNull(Snapshots.TryPreserve(board, "v1", T0));
        Assert.Null(Snapshots.TryPreserve(board, "v1", T0.AddHours(3)));

        Assert.Single(Snapshots.List(board));
    }

    [Fact]
    public void A_restore_snapshots_even_though_it_is_not_the_hour()
    {
        var board = BoardNamed("restore.canvas");

        File.WriteAllText(board, "v1");
        Assert.NotNull(Snapshots.TryPreserve(board, "v1", T0));

        File.WriteAllText(board, "v2");

        // 同一分钟内、内容也不同，但 force 绕过两道闸：选错快照的那一刻最贵。
        Assert.NotNull(Snapshots.TryPreserve(board, "v2", T0.AddMinutes(1), force: true));
        Assert.Equal(2, Snapshots.List(board).Count);
    }

    [Fact]
    public void Listing_skips_names_that_are_not_ours()
    {
        var board = BoardNamed("parse.canvas");
        var folder = Snapshots.DirectoryFor(board);
        Directory.CreateDirectory(folder);

        File.WriteAllText(board, "v1");
        Snapshots.TryPreserve(board, "v1", T0);

        File.WriteAllText(Path.Combine(folder, "other@20261012-020000.canvas"), "x");
        File.WriteAllText(Path.Combine(folder, "parse@not-a-time.canvas"), "x");
        File.WriteAllText(Path.Combine(folder, "parse@20261012-020000.txt"), "x");

        Assert.Single(Snapshots.List(board));
    }

    [Fact]
    public void Pruning_respects_the_cap_and_the_floor()
    {
        var board = BoardNamed("prune.canvas");
        File.WriteAllText(board, "body");

        // 60 份，间隔 6 分钟：上限 50，所以最老的 10 份该走。
        for (var i = 0; i < 60; i++)
        {
            Snapshots.TryPreserve(board, $"body-{i}", T0.AddMinutes(6 * i), force: true);
        }

        Assert.True(Snapshots.List(board).Count <= Snapshots.Keep);
        Assert.Equal(Snapshots.Keep, Snapshots.List(board).Count);

        // 全是老快照时，年龄闸不许把最近的 Floor 份也削掉。
        var old = T0.AddYears(2);
        Snapshots.Prune(board, old);

        Assert.True(Snapshots.List(board).Count >= Snapshots.Floor);
    }

    [Fact]
    public void The_store_itself_takes_the_snapshot_before_the_move()
    {
        var board = BoardNamed("store.canvas");
        var created = File.ReadAllText(board);

        CanvasStore.Write(board, "one");
        CanvasStore.Write(board, "two");

        // 第一次写就把"要被替换掉的那一份"存下来了；第二次紧挨着（同一分钟内）⇒ 间隔闸把它挡掉，
        // 所以这里只有一份，而且内容是刚被换掉的初始内容，不是新写进去的。
        var kept = Snapshots.List(board);

        Assert.Single(kept);
        Assert.Equal(created, Snapshots.Read(kept[0]));
        Assert.Equal("two", File.ReadAllText(board));

        // 恢复走强制闸：即便在同一分钟内，也要先把"two"存下来再写回。
        CanvasStore.Restore(board, created);

        Assert.Equal(created, File.ReadAllText(board));
        Assert.Equal(2, Snapshots.List(board).Count);
        Assert.Contains(Snapshots.List(board), entry => Snapshots.Read(entry) == "two");
    }

    [Fact]
    public void A_snapshot_never_stops_a_save()
    {
        // 历史目录写不成（这里拿一个同名文件占住它）时，板子本身必须照样存下去：
        // 备份装置不许变成写入的阻塞点。
        var board = BoardNamed("blocked.canvas");
        File.WriteAllText(board, "v1");

        File.WriteAllText(Snapshots.DirectoryFor(board), "not a folder");

        CanvasStore.Write(board, "v2");

        Assert.Equal("v2", File.ReadAllText(board));
    }
}
