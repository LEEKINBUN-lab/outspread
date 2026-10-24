using System.Text.Json.Nodes;
using Outspread.Core;
using Xunit;

namespace Outspread.Core.Tests;

/// <summary>
/// 「所有便签」并集核心：合并/拆分的往返、跨文件撞号、边端点重映射、坐标不动。
/// </summary>
public class BoardMergeTests
{
    private const string A = "boards/a.canvas";
    private const string B = "boards/b.canvas";

    private static CanvasDocument Doc(string json) => CanvasDocument.Parse(json);

    [Fact]
    public void 合并再拆分_还原每块板的节点_坐标不动_未知字段不丢()
    {
        var a = Doc("""{"nodes":[{"id":"a1","type":"text","x":10,"y":20,"width":100,"height":50,"text":"A one","custom":"keep"}],"edges":[]}""");
        var b = Doc("""{"nodes":[{"id":"b1","type":"text","x":300,"y":40,"width":100,"height":50,"text":"B one"}],"edges":[]}""");

        var parts = BoardMerge.Split(BoardMerge.Merge([(A, a), (B, b)]));

        var nodeA = Assert.Single(parts[A].Nodes)!.AsObject();
        Assert.Equal("a1", nodeA["id"]!.GetValue<string>());
        Assert.Equal(10, nodeA["x"]!.GetValue<double>());
        Assert.Equal(20, nodeA["y"]!.GetValue<double>());
        Assert.Equal("keep", nodeA["custom"]!.GetValue<string>()); // 本程序不认识的字段必须活着回来

        var nodeB = Assert.Single(parts[B].Nodes)!.AsObject();
        Assert.Equal("b1", nodeB["id"]!.GetValue<string>());
        Assert.Equal(300, nodeB["x"]!.GetValue<double>());
    }

    [Fact]
    public void 两块板撞号的格子_并集里不互相顶掉_拆分各还原()
    {
        var a = Doc("""{"nodes":[{"id":"dup","type":"text","x":5,"y":5,"width":9,"height":9,"text":"from A"}],"edges":[]}""");
        var b = Doc("""{"nodes":[{"id":"dup","type":"text","x":77,"y":77,"width":9,"height":9,"text":"from B"}],"edges":[]}""");

        var merged = BoardMerge.Merge([(A, a), (B, b)]);

        // 并集里两枚 id 不同（命名空间），都还在。
        var ids = ((JsonArray)merged["nodes"]!).Select(n => n!["id"]!.GetValue<string>()).ToList();
        Assert.Equal(2, ids.Count);
        Assert.Distinct(ids);

        var parts = BoardMerge.Split(merged);
        Assert.Equal("from A", parts[A].Nodes[0]!["text"]!.GetValue<string>());
        Assert.Equal("from B", parts[B].Nodes[0]!["text"]!.GetValue<string>());
        Assert.Equal("dup", parts[A].Nodes[0]!["id"]!.GetValue<string>());
        Assert.Equal("dup", parts[B].Nodes[0]!["id"]!.GetValue<string>());
    }

    [Fact]
    public void 边的端点跟着改名_拆分归到它所属的板()
    {
        var a = Doc("""{"nodes":[{"id":"n1","type":"text","x":0,"y":0,"width":9,"height":9},{"id":"n2","type":"text","x":50,"y":0,"width":9,"height":9}],"edges":[{"id":"e1","fromNode":"n1","toNode":"n2","fromSide":"right","toSide":"left"}]}""");

        var merged = BoardMerge.Merge([(A, a)]);
        var edge = ((JsonArray)merged["edges"]!)[0]!.AsObject();
        Assert.Equal(BoardMerge.MergeId(A, "n1"), edge["fromNode"]!.GetValue<string>());
        Assert.Equal(BoardMerge.MergeId(A, "n2"), edge["toNode"]!.GetValue<string>());
        Assert.Equal("right", edge["fromSide"]!.GetValue<string>()); // 边不是 id，不该被改

        var parts = BoardMerge.Split(merged);
        var back = Assert.Single(parts[A].Edges)!.AsObject();
        Assert.Equal("n1", back["fromNode"]!.GetValue<string>());
        Assert.Equal("n2", back["toNode"]!.GetValue<string>());
    }

    [Fact]
    public void 命名空间id_可反查归属与原始id()
    {
        var mergedId = BoardMerge.MergeId(A, "x9");
        Assert.True(BoardMerge.TryUnmerge(mergedId, out var owner, out var id));
        Assert.Equal(A, owner);
        Assert.Equal("x9", id);
        Assert.Equal(A, BoardMerge.OwnerOf(mergedId));
        Assert.False(BoardMerge.TryUnmerge("plain-id-no-sep", out _, out _));
    }

    [Fact]
    public void 并集按块分区_不同板的格子不再叠在同一片坐标上()
    {
        var a = Doc("""{"nodes":[{"id":"a1","type":"text","x":10,"y":20,"width":100,"height":50},{"id":"a2","type":"text","x":150,"y":20,"width":100,"height":50}],"edges":[]}""");
        var b = Doc("""{"nodes":[{"id":"b1","type":"text","x":10,"y":20,"width":100,"height":50}],"edges":[]}""");
        var boards = new List<(string OwnerKey, CanvasDocument Doc)> { (A, a), (B, b) };

        var offsets = BoardMerge.RegionOffsets(boards);
        var merged = BoardMerge.Merge(boards, offsets);
        var nodes = ((JsonArray)merged["nodes"]!).Select(item => item!.AsObject()).ToList();

        var a1 = nodes.First(node => node["id"]!.GetValue<string>() == BoardMerge.MergeId(A, "a1"));
        var a2 = nodes.First(node => node["id"]!.GetValue<string>() == BoardMerge.MergeId(A, "a2"));
        var b1 = nodes.First(node => node["id"]!.GetValue<string>() == BoardMerge.MergeId(B, "b1"));

        // 带内相对位置一个都不动；两块板各自从 x=0 起。
        Assert.Equal(140, a2["x"]!.GetValue<double>() - a1["x"]!.GetValue<double>(), 3);
        Assert.Equal(0, a1["x"]!.GetValue<double>(), 3);
        Assert.Equal(0, b1["x"]!.GetValue<double>(), 3);

        // 带与带之间至少隔 RegionGapPx。
        var aBottom = a1["y"]!.GetValue<double>() + 50;
        Assert.True(b1["y"]!.GetValue<double>() >= aBottom + BoardMerge.RegionGapPx - 0.001);
    }

    [Fact]
    public void 写回时减掉区域偏移_源坐标只动用户搬的那一点()
    {
        var a = Doc("""{"nodes":[{"id":"a1","type":"text","x":10,"y":20,"width":100,"height":50}],"edges":[]}""");
        var boards = new List<(string OwnerKey, CanvasDocument Doc)> { (A, a) };
        var offsets = BoardMerge.RegionOffsets(boards);
        var merged = BoardMerge.Merge(boards, offsets);

        var node = ((JsonArray)merged["nodes"]!)[0]!.AsObject();
        BoardMerge.ShiftNode(node, 7, 9);   // 在并集里搬 (+7,+9)

        var parts = BoardMerge.Split(merged);

        foreach (var part in parts[A].Nodes.OfType<JsonObject>())
        {
            BoardMerge.ShiftNode(part, -offsets[A].X, -offsets[A].Y);
        }

        var back = parts[A].Nodes[0]!.AsObject();

        Assert.Equal(17, back["x"]!.GetValue<double>(), 3);
        Assert.Equal(29, back["y"]!.GetValue<double>(), 3);
    }
}
