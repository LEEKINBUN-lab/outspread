using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Outspread.Core;

namespace Outspread.Board;

/// <summary>
/// 成组提议的那只手（10-22 他批的 C）：把一格拖到另两枚以上明显叠在一起，松手那一拍在格下浮
/// 一枚「编成组？这 N 格」。机器提议、人一下确认——成组从此不用先学会框选；而文档只在人点的
/// 那一发才动，一次挪动不会往 .canvas 里塞出跨端的结构。
/// 拆组的那只手（D）后来并进了便签操作条的组钮菜单（NoteActionRail.cs）——同一问不摆两处；
/// <see cref="DetachFromGroup"/> 留着，是那条菜单的落点。
/// 胶囊长在同一族纸白上，"问一次答一次"：下一次任何动作就收，不设计时器。
/// </summary>
public sealed partial class MainWindow
{
    private const double AssistZoomFloor = 0.5;

    private Border? _groupHint;
    private List<string> _groupHintIds = [];

    /// <summary>
    /// 这枚散格现在压着谁（含它自己）；少于 3 枚就不构成"堆成一堆"。
    /// </summary>
    /// <remarks>
    /// **只数没有归属的格子。** 10-24 他报"有些情况下，拖动其中一个便签移出分组时，一个分组会分裂成
    /// 两个分组"：那一枚移出名单之后就成了散格，而它的落点往往还压着老组员——提议把老组员也算进
    /// "散格堆"，点一下就在同一批格子上罩出第二个框，一个组变两个。自检那一步实测到的是
    /// "移出名单后原组 2 格、提议 3 枚（其中 2 枚已属别的组）"。
    /// 真正的散格堆（谁都不属于）照旧提议，那一发不受影响。
    /// </remarks>
    private List<string> ClusterIds(CanvasNode moved)
        => [moved.Id, .. Grouping.TouchedBy(RectOf(moved), BoardNotes().ConvertAll(RectOf))
            .Where(other => NodeOf(other.Id) is { } node && Groups().GroupOf(RectOf(node)) is null)
            .Select(other => other.Id)];

    /// <summary>拖动收口那一拍：挪的是散格、松手时压着另两枚以上 ⇒ 浮提议。别的情况一声不响。</summary>
    private void OfferGroupHint(CanvasNode? moved)
    {
        ClearGroupHint();

        if (moved is null || moved.Kind == CanvasNodeKind.Group
            || _view.Zoom < AssistZoomFloor
            || Groups().GroupOf(RectOf(moved)) is not null)
        {
            return;
        }

        var ids = ClusterIds(moved);

        if (ids.Count < 3)
        {
            return;
        }

        _groupHintIds = ids;

        _groupHint = AssistPill($"编成组？这 {_groupHintIds.Count} 个便签");
        _groupHint.Name = "GroupHint";
        _groupHint.PointerPressed += (_, args) =>
        {
            args.Handled = true;
            AcceptGroupHint();
        };

        HandleLayer.Children.Add(_groupHint);
        PlaceGroupHint();

        Diagnostics.Write(
            $"group hint: {moved.Id} 压着 {ids.Count - 1} 格 ⇒ 提议『编成组？这 {ids.Count} 格』");
    }

    /// <summary>点提议＝把那几格选进来再走同一条成组命令——不为提议另造一套落盘路径。</summary>
    private void AcceptGroupHint()
    {
        var ids = _groupHintIds;

        ClearGroupHint();

        if (ids.Any(id => NodeOf(id) is null))
        {
            return;
        }

        ClearSelection();

        foreach (var id in ids)
        {
            _selection.Add(id);
        }

        ProjectSelection();
        GroupSelection();
    }

    private void ClearGroupHint()
    {
        if (_groupHint is not null)
        {
            HandleLayer.Children.Remove(_groupHint);
            _groupHint = null;
            _groupHintIds = [];
        }
    }

    private void PlaceGroupHint()
    {
        if (_groupHint is null)
        {
            return;
        }

        if (NodeOf(_groupHintIds[0]) is not { } anchor)
        {
            ClearGroupHint();

            return;
        }

        _groupHint.Measure(new Windows.Foundation.Size(double.PositiveInfinity, double.PositiveInfinity));

        Canvas.SetLeft(_groupHint, _view.ToScreenX(anchor.X));
        Canvas.SetTop(_groupHint, _view.ToScreenY(anchor.Bottom) + 8);
    }

    /// <summary>拉出＝整格挪到框外右边一点，走一步可撤销的手势；框由 EndGesture 的贴回自己缩。</summary>
    private void DetachFromGroup(string id)
    {
        if (NodeOf(id) is not { } node)
        {
            return;
        }

        if (Groups().GroupOf(RectOf(node)) is not { } group)
        {
            return;
        }

        var rect = RectOf(node);
        var remaining = Groups().Members(group.Id).Where(member => member.Id != id).ToList();

        BeginGesture("把这个便签移出组");

        if (remaining.Count == 0)
        {
            // 组里只有它一枚：撤掉这个框＝取消分组，格子原地不动。
            _document.Remove(group.Id);
        }
        else
        {
            var hug = Grouping.Enclose(group.Id, remaining);

            if (NodeOf(group.Id) is { } frameNode)
            {
                // 出组写的是名单：这一格从此不在这张表里，把它拖回框里也不会自己回去。
                frameNode.SetGroupMembers([.. remaining.Select(member => member.Id)]);
                frameNode.X = hug.X;
                frameNode.Y = hug.Y;
                frameNode.Width = hug.Width;
                frameNode.Height = hug.Height;
            }

            // 缩完框后它若还在框里（和剩下的成员叠在一起），只挪出<b>最近那条边</b>一点点——
            // 不再甩到 group.Right+20 那种横跨整组的跳位（10-22 他：移出不该让便签跳）。
            if (hug.Holds(rect))
            {
                const double margin = 12;
                var left = rect.Right - hug.X + margin;
                var right = hug.Right - rect.X + margin;
                var up = rect.Bottom - hug.Y + margin;
                var down = hug.Bottom - rect.Y + margin;
                var min = Math.Min(Math.Min(left, right), Math.Min(up, down));

                if (min == left)
                {
                    node.X -= left;
                }
                else if (min == right)
                {
                    node.X += right;
                }
                else if (min == up)
                {
                    node.Y -= up;
                }
                else
                {
                    node.Y += down;
                }
            }
        }

        EndGesture();

        Rebuild();
        SaveSoon();
        var groupName = NodeOf(group.Id)?.Label is { Length: > 0 } label ? label : "未命名组";

        Say(remaining.Count == 0
            ? $"组“{groupName}”已无其他成员，组已解散，便签位置未变"
            : $"已从组“{groupName}”移出，便签位置尽量保持");

        Diagnostics.Write($"pull out: {id} 出 {group.Id}，剩 {remaining.Count} 枚，X→{node.X:0.#} Y→{node.Y:0.#}");
    }

    private static string ActualSizeOf(FrameworkElement element)
        => $"{element.ActualWidth:0.#}×{element.ActualHeight:0.#}";

    /// <summary>
    /// 「更多」/名牌那一族的胶囊。底色用<b>纸白</b>而不是板底：板底是 (248,250,252)，
    /// 用板底画出来的提议在板上只剩一条灰边——10-22 真机截图量到的就是这一格隐形。
    /// 字用板墨色，边用强调环——它是来提问的，不该找不着。
    /// </summary>
    private static Border AssistPill(string text)
    {
        // 纸白用缩放把手同一枚令牌（浅档 255,255,255 / 深档 27,30,34）：它本来就是
        // "板底上的 chrome"这一族；字色按这层底现算，边用强调环——它是来提问的，不该找不着。
        var face = SelectionChrome.HandleFace(NoteStyling.IsDark);
        var paper = NoteStyling.ToColor(face);
        var ink = NoteStyling.InkFor(paper);

        return new Border
        {
            Background = new SolidColorBrush(paper),
            BorderBrush = ChromeStyling.Ring,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(9, 3, 9, 4),
            Child = new TextBlock
            {
                Text = text,
                FontFamily = NoteStyling.NoteFont,
                FontSize = 12,
                FontWeight = Microsoft.UI.Text.FontWeights.Medium,
                Foreground = new SolidColorBrush(ink),
            },
        };
    }
}
