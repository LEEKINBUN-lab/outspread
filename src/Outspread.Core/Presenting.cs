using System.Globalization;
using System.Text.RegularExpressions;

namespace Outspread.Core;

/// <summary>
/// 演示模式的那一摞"页"：<b>一枚组就是一页</b>，页序＝组标签里的落板时间序——与
/// <c>board_tidy</c> 排块的是同一把尺（组名一律 "MM-dd HH:mm · 标题"），Miro 那个
/// "帧顺序要自己排、社区常年排乱"的知名痛点在这里由约定直接堵死。
/// </summary>
/// <remarks>
/// 调研（10-30 夜）：tldraw 用帧当页、方向键 zoomToBounds 翻页；Miro 的帧式演示最成熟但顺序靠手动；
/// FigJam 至今没有原生演示（论坛长年 feature request），Heptabase 也没有——这是真差距。
/// 我们不引新文档语义：组本来就在、成员名单本来就在，演示只是相机的编舞。
/// </remarks>
public static partial class Presenting
{
    /// <param name="GroupId">组的节点号（翻页时高亮/定位可用）。</param>
    /// <param name="Label">组的标签原样带回——页面上要念出来的就是它。</param>
    /// <param name="Bounds">这一页的取景框：成员盒的并集（世界坐标）。</param>
    public readonly record struct Slide(string GroupId, string Label, Grouping.Rect Bounds);

    /// <summary>收集演示页：有成员的组各一页，按组标签时间戳排，没戳的（老内容）排最后。</summary>
    public static IReadOnlyList<Slide> SlidesOf(CanvasDocument document)
    {
        var slides = new List<Slide>();

        foreach (var group in document.Nodes)
        {
            if (group.Kind != CanvasNodeKind.Group)
            {
                continue;
            }

            var frame = new Grouping.Rect(group.Id, group.X, group.Y, group.Width, group.Height);
            var boxes = new List<Grouping.Rect>();
            var memberIds = group.GroupMemberIds;

            if (memberIds is not null)
            {
                // 成员是一张显式名单（10-24 起）：不在板上的号跳过，名单空了这一页不存在。
                foreach (var id in memberIds)
                {
                    if (document.Find(id) is { } member)
                    {
                        boxes.Add(new Grouping.Rect(member.Id, member.X, member.Y, member.Width, member.Height));
                    }
                }
            }
            else
            {
                // 老组没有名单，退回几何那一档（与 CanvasDocument 同一条退路）。
                var notes = document.Nodes
                    .Where(node => node.Kind != CanvasNodeKind.Group)
                    .Select(node => new Grouping.Rect(node.Id, node.X, node.Y, node.Width, node.Height))
                    .ToList();

                boxes.AddRange(Grouping.Members(frame, notes).Select(rect =>
                    new Grouping.Rect(rect.Id, rect.X, rect.Y, rect.Width, rect.Height)));
            }

            if (boxes.Count == 0)
            {
                continue;
            }

            var left = boxes.Min(box => box.X);
            var top = boxes.Min(box => box.Y);
            var right = boxes.Max(box => box.X + box.Width);
            var bottom = boxes.Max(box => box.Y + box.Height);

            slides.Add(new Slide(group.Id, group.Label ?? string.Empty,
                new Grouping.Rect(group.Id, left, top, right - left, bottom - top)));
        }

        return slides
            .Select((slide, order) => (Slide: slide, Order: order))
            .OrderBy(pair => TryStamp(pair.Slide.Label, out var stamp) ? stamp : DateTime.MaxValue)
            .ThenBy(pair => pair.Slide.Label, StringComparer.Ordinal)
            .ThenBy(pair => pair.Order)
            .Select(pair => pair.Slide)
            .ToList();
    }

    /// <summary>组名开头的 "MM-dd HH:mm"：解析失败（老组没有戳）回 false，排序时排最后。</summary>
    private static bool TryStamp(string? label, out DateTime stamp)
    {
        stamp = default;

        if (string.IsNullOrEmpty(label))
        {
            return false;
        }

        var match = StampPrefix().Match(label);

        if (!match.Success)
        {
            return false;
        }

        // 年份取当下：页序只看组与组之间的先后，闰年 2-29 在错误年份会解不动——那就回 false 排后，不炸。
        return DateTime.TryParseExact(
            $"{DateTime.Now.Year:D4}-{match.Groups[1].Value}",
            "yyyy-MM-dd HH:mm",
            CultureInfo.InvariantCulture,
            DateTimeStyles.None,
            out stamp);
    }

    [GeneratedRegex(@"^(\d{1,2}-\d{1,2} \d{1,2}:\d{2})")]
    private static partial Regex StampPrefix();
}
