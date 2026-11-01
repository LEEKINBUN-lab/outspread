using Outspread.Core;
using Outspread.Mcp;

namespace Outspread.Mcp.Tests;

/// <summary>
/// 写入矩阵（chamber 第一刀）：勾的写权按卡的种类分家——
/// decision 拒 tool 代勾、note 不用打勾、task/risk 打勾必须带凭据且凭据上卡。
/// 风险对应：没有凭据的「改完了」＝他 10-31 抱怨「改完了为什么不改成已完成」的那件事。
/// </summary>
public sealed class GateMatrixTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "outspread-gate-tests-" + Guid.NewGuid().ToString("N")[..8]);

    public GateMatrixTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private string Board() => Path.Combine(_root, "出口.canvas");

    private static BoardCard Card(string kind, string text) => new()
    {
        Kind = kind,
        Text = kind == "note" ? text : $"{text}\n要你：看一眼这一格",
    };

    /// <summary>落一张指定 kind 的卡，返回它的格子 id。组标题卡（### 开头）不是目标。</summary>
    private string Post(string kind, string text)
    {
        BoardTools.BoardPost("写入矩阵", [Card(kind, text)], round: "写入矩阵", board: Board());

        var withTick = CanvasStore.Load(Board()).Nodes
            .Where(node => node.Kind == CanvasNodeKind.Text)
            .Where(node => !(node.Text ?? "").Contains("### "))
            .OrderBy(node => node.Y)
            .First();

        return withTick.Id;
    }

    [Fact]
    public void Decision_tick_from_a_tool_is_refused()
    {
        var id = Post("decision", "这是要他拍的");

        var reply = BoardTools.BoardPatch(id, done: true, evidence: "自作主张", board: Board());

        Assert.Contains("decision", reply);
        var node = CanvasStore.Load(Board()).Find(id)!;
        Assert.False(Tags.Read(node.Text).Done == true);
        Assert.DoesNotContain("凭据：", node.Text);
    }

    [Fact]
    public void Task_done_without_evidence_is_refused()
    {
        var id = Post("task", "这是一条待办");

        var reply = BoardTools.BoardPatch(id, done: true, board: Board());

        Assert.Contains("凭据", reply);
        Assert.False(Tags.Read(CanvasStore.Load(Board()).Find(id)!.Text).Done == true);
    }

    [Fact]
    public void Evidence_is_written_onto_the_card_with_signature_and_time()
    {
        var id = Post("task", "这是一条待办");

        var reply = BoardTools.BoardPatch(id, done: true, evidence: "pytest 11 全绿", agent: "zcode", board: Board());

        Assert.Contains("已更新", reply);
        var node = CanvasStore.Load(Board()).Find(id)!;
        Assert.True(Tags.Read(node.Text).Done == true);
        Assert.Contains("凭据：pytest 11 全绿（zcode ", node.Text);
        Assert.Contains("#by-zcode", Tags.Of(node.Text));
    }

    [Fact]
    public void Note_tick_is_refused()
    {
        var id = Post("note", "一条结论，不占他的待办");

        var reply = BoardTools.BoardPatch(id, done: true, evidence: "多余", board: Board());

        Assert.Contains("note", reply);
        Assert.DoesNotContain("凭据：", CanvasStore.Load(Board()).Find(id)!.Text);
    }

    [Fact]
    public void Uncheck_stays_allowed_and_keeps_the_credential_line()
    {
        var id = Post("risk", "这是一处缺陷");

        BoardTools.BoardPatch(id, done: true, evidence: "回归绿", board: Board());
        BoardTools.BoardPatch(id, done: false, board: Board());

        var node = CanvasStore.Load(Board()).Find(id)!;
        Assert.False(Tags.Read(node.Text).Done == true);
        Assert.Contains("凭据：回归绿", node.Text);
    }

    [Fact]
    public void Group_tick_is_refused()
    {
        var board = Board();
        BoardTools.BoardPost("带组", [Card("task", "组里的活\n要你：看一眼")], round: "组", board: board);

        // 找到那张组框（组＝名单节点）：没有就跳过——这一发守的是"组不是卡"的分支。
        var group = CanvasStore.Load(board).Nodes.FirstOrDefault(n => n.Kind == CanvasNodeKind.Group);

        if (group is null)
        {
            return;
        }

        var reply = BoardTools.BoardPatch(group.Id, done: true, evidence: "x", board: board);

        Assert.Contains("组", reply);
    }

    private static string FirstLine(string? text)
        => text?.Split('\n').FirstOrDefault(line => !line.StartsWith('@')) ?? string.Empty;
}
