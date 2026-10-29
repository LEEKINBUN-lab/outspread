namespace Outspread.Core.Tests;

/// <summary>
/// 演示模式的那一摞页：一枚组一页、序＝组名时间戳。翻错了页的代价是"他念着 A 讲的是 B"，
/// 所以页序、空页、缺号的容错都在这里钉住。
/// </summary>
public class PresentingTests
{
    [Fact]
    public void Pages_follow_the_stamp_in_the_label_and_untimed_groups_come_last()
    {
        var document = CanvasDocument.CreateNew();

        var note = document.Add(CanvasNodeKind.Text, 0, 0, 100, 60);
        var late = document.Add(CanvasNodeKind.Group, 0, 0, 400, 300, id: "a-late");
        var early = document.Add(CanvasNodeKind.Group, 0, 0, 400, 300, id: "a-early");
        var untimed = document.Add(CanvasNodeKind.Group, 0, 0, 400, 300, id: "a-untimed");

        late.Label = "10-29 09:00 · 乙";
        late.SetGroupMembers([note.Id]);
        early.Label = "10-28 08:00 · 甲";
        early.SetGroupMembers([note.Id]);
        untimed.Label = "老组没有戳";
        untimed.SetGroupMembers([note.Id]);

        var slides = Presenting.SlidesOf(document);

        Assert.Equal(["a-early", "a-late", "a-untimed"], slides.Select(slide => slide.GroupId).ToArray());
    }

    [Fact]
    public void A_group_with_no_live_members_makes_no_page()
    {
        var document = CanvasDocument.CreateNew();

        var empty = document.Add(CanvasNodeKind.Group, 0, 0, 400, 300, id: "a-empty");
        empty.Label = "10-29 09:00 · 空组";
        empty.SetGroupMembers([]);
        var ghost = document.Add(CanvasNodeKind.Group, 0, 0, 400, 300, id: "a-ghost");
        ghost.Label = "10-29 09:01 · 号都指空";
        ghost.SetGroupMembers(["a-gone-1", "a-gone-2"]);

        Assert.Empty(Presenting.SlidesOf(document));
    }

    [Fact]
    public void Bounds_are_the_union_of_the_member_boxes()
    {
        var document = CanvasDocument.CreateNew();

        var left = document.Add(CanvasNodeKind.Text, 0, 10, 100, 60);
        var right = document.Add(CanvasNodeKind.Text, 500, 200, 120, 80);
        var group = document.Add(CanvasNodeKind.Group, 0, 0, 700, 320, id: "a-group");

        group.Label = "10-30 02:00 · 两枚";
        group.SetGroupMembers([left.Id, right.Id]);

        var slide = Assert.Single(Presenting.SlidesOf(document));

        Assert.Equal(0, slide.Bounds.X);
        Assert.Equal(10, slide.Bounds.Y);
        Assert.Equal(620, slide.Bounds.Width);
        Assert.Equal(270, slide.Bounds.Height);
    }

    [Fact]
    public void A_group_without_a_members_list_falls_back_to_geometry()
    {
        var document = CanvasDocument.CreateNew();

        document.Add(CanvasNodeKind.Text, 20, 20, 100, 60);
        var group = document.Add(CanvasNodeKind.Group, 0, 0, 400, 300, id: "a-old");

        group.Label = "10-30 02:01 · 老组";

        // 没写 members 名单的老组：按几何收到框里的那几格当成员（与文档同一条退路），不至于开天窗。
        Assert.Single(Presenting.SlidesOf(document));
    }
}
