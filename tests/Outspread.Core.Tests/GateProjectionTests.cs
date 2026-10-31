using Outspread.Core;
using Xunit;

namespace Outspread.Core.Tests;

public class GateProjectionTests
{
    private static string WriteLedger(string boardPath, string json)
    {
        var ledger = CanvasStore.GatePath(boardPath);
        File.WriteAllText(ledger, json);
        return ledger;
    }

    [Fact]
    public void Missing_file_means_no_chips_and_no_throw()
    {
        Assert.Null(GateProjection.TryLoad(Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".canvas")));
    }

    [Fact]
    public void Parses_cards_statuses_and_skips_unmapped_nodes()
    {
        var dir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var board = Path.Combine(dir, "试板.canvas");
        File.WriteAllText(board, "{}");

        WriteLedger(board, """
            {
              "schemaVersion": 2,
              "nodes": [
                { "id": "t1", "title": "第一道", "status": "doing", "progress": 40, "card": "u111111111111" },
                { "id": "t2", "title": "堵住的", "status": "blocked", "card": "a222222222222" },
                { "id": "t3", "title": "没钉卡的", "status": "done" },
                { "id": "t4", "title": "怪状态", "status": "wat", "card": "u333333333333" },
                { "id": "t5", "title": "完成", "status": "done", "card": "u444444444444", "evidence": "验过" }
              ]
            }
            """);

        var chips = GateProjection.TryLoad(board);

        Assert.NotNull(chips);
        Assert.Equal(4, chips!.Count);

        var doing = chips.Single(c => c.CardId == "u111111111111");
        Assert.Equal(GateStatus.Doing, doing.Status);
        Assert.Equal(40, doing.Progress);
        Assert.Equal("第一道", doing.Title);

        Assert.Equal(GateStatus.Blocked, chips.Single(c => c.CardId == "a222222222222").Status);

        // 没钉卡的节点不画牌；不认识的 status 归 None 但仍然在场（显式，不悄悄丢）。
        Assert.DoesNotContain(chips, c => c.NodeId == "t3");
        Assert.Equal(GateStatus.None, chips.Single(c => c.CardId == "u333333333333").Status);
        Assert.Equal(GateStatus.Done, chips.Single(c => c.CardId == "u444444444444").Status);

        Directory.Delete(dir, recursive: true);
    }

    [Fact]
    public void Broken_ledger_degrades_to_null()
    {
        var dir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var board = Path.Combine(dir, "试板.canvas");
        File.WriteAllText(board, "{}");
        File.WriteAllText(CanvasStore.GatePath(board), "{ 这不是 JSON ");

        Assert.Null(GateProjection.TryLoad(board));

        Directory.Delete(dir, recursive: true);
    }

    [Fact]
    public void Done_chip_rides_the_completed_shape_and_others_carry_gate_colour()
    {
        var done = new GateChip("t1", "u111111111111", GateStatus.Done, 100, "第一道").ToMark();
        Assert.True(done.Done);
        Assert.Equal("已完成", done.Label);
        Assert.Equal("gate:t1", done.Source);

        var doing = new GateChip("t2", "u222222222222", GateStatus.Doing, 40, "x").ToMark();
        Assert.Null(doing.Done);
        Assert.Equal(GateStatus.Doing, doing.Gate);
        Assert.Equal("进行中", doing.Label);
    }

    [Fact]
    public void Gate_path_stays_in_the_sidecar_family()
    {
        Assert.EndsWith(".canvas.gate.json", CanvasStore.GatePath(@"D:\x\板.canvas"));
    }
}
