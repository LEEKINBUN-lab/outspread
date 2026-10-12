using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Outspread.Core;
using Windows.Foundation;

namespace Outspread.Board;

/// <summary>
/// 拉线：双击一格的边缘把那根线的起点钉在那一面上，指针带着自由端走，
/// 下一发按在另一格（或另一格的边缘）上就接上，按在空白上或 Esc 就是没画过。
/// </summary>
/// <remarks>
/// <para>
/// 为什么是"双击边缘"而不是"从边缘拖出去"：边缘那一发的按下已经归了缩放（压边＝改那条边，
/// 6 屏幕像素的命中带），同一发手势不能有两个主人。双击是那块面上唯一还没被占用的动作——
/// 双击空白＝新建、双击格内＝编辑，都在别的地方，三条互不打架。
/// </para>
/// <para>
/// 起点带"哪一面"一起钉住（w/e/n/s），落点取目标格离起点最近的那一面：拉线的人心里想的是
/// "从这格的右边出去，进那格的左边"，把这两半都存进 <c>fromSide/toSide</c> 才落得回他想要的那条路。
/// </para>
/// <para>
/// 未落定之前不落盘：整条路上文档一个字节都不动，所以 Esc 是纯放弃（不变量 5），
/// 撤销栈里也不会多出"我拉了一下没接上"的空格子（不变量 2）。
/// </para>
/// </remarks>
public sealed partial class MainWindow
{
    private (string FromId, string Side)? _pulling;

    private Polyline? _pullGhost;

    /// <summary>正在拉线＝这一发按键/按下归这条线，不归板子。</summary>
    private bool Pulling => _pulling is not null;

    /// <summary>从这一格的这一面起头。</summary>
    private void StartPull(string id, string side)
    {
        if (NodeOf(id) is not { } node)
        {
            return;
        }

        ClearPullGhost();

        _pulling = (id, SpecSide(side));

        _pullGhost = new Polyline
        {
            Stroke = new SolidColorBrush(NoteStyling.ToColor(SelectionChrome.Ring(NoteStyling.IsDark))),
            StrokeThickness = 2,
            StrokeDashArray = new DoubleCollection { 3, 3 },
            IsHitTestVisible = false,
        };

        GuideLayer.Children.Add(_pullGhost);

        Say($"拉线中：起点是这格的{SideWord(side)} · 点另一格就接上，点空白或 Esc＝没画过");
    }

    /// <summary>菜单/键位那一条路：从选中那一格靠视野中心的那一面起头。</summary>
    private void StartPullFromSelection()
    {
        if (_pulling is not null)
        {
            CancelPull();

            return;
        }

        var members = SelectedNotes();

        if (members.Count != 1)
        {
            Say($"拉线要先选中一格，现在选了 {members.Count} 格");

            return;
        }

        StartPull(members[0].Id, SideTowardViewCentre(members[0]));
    }

    /// <summary>指针在走：自由端跟着走（还没落定时它不属于任何一格）。</summary>
    private void TrackPull(Point screen)
    {
        if (_pulling is not { } pull || _pullGhost is null || NodeOf(pull.FromId) is not { } from)
        {
            return;
        }

        var anchor = CanvasEdge.SidePoint(from, pull.Side);

        _pullGhost.Points.Clear();
        _pullGhost.Points.Add(new Point(_view.ToScreenX(anchor.X), _view.ToScreenY(anchor.Y)));
        _pullGhost.Points.Add(screen);
    }

    /// <summary>
    /// 落定：命中另一格就画一条边（一个事务），命中空白或还是原来那一格就放弃。
    /// </summary>
    /// <returns>是否消费了这一发按下。</returns>
    private bool FinishPull(Point screen)
    {
        if (_pulling is not { } pull)
        {
            return false;
        }

        var target = NodeAt(screen);

        if (target is null || target.Id == pull.FromId)
        {
            CancelPull();
            Say("没有接到任何一格，这根线没画");

            return true;
        }

        if (NodeOf(pull.FromId) is not { } from)
        {
            CancelPull();

            return true;
        }

        var leave = CanvasEdge.SidePoint(from, pull.Side);

        var toSide = CanvasEdge.NearestSide(target, leave.X, leave.Y);

        CancelPull();

        BeginGesture("连了一根线");

        var edge = _document.AddEdge(pull.FromId, target.Id, pull.Side, toSide);

        EndGesture();

        Rebuild();
        SaveSoon();

        Say($"连上了：{SideWord(pull.Side)} → 那格的{SideWord(toSide)}（Ctrl＋Z 拆掉）");

        return true;
    }

    private void CancelPull()
    {
        _pulling = null;

        ClearPullGhost();
    }

    private void ClearPullGhost()
    {
        if (_pullGhost is not null)
        {
            GuideLayer.Children.Remove(_pullGhost);
            _pullGhost = null;
        }
    }

    /// <summary>最靠视野中心的那一面——从那儿起头，线一出去就在看得见的地方。</summary>
    private string SideTowardViewCentre(CanvasNode node)
    {
        var centreX = BoardArea.ActualWidth / 2;
        var centreY = BoardArea.ActualHeight / 2;

        var candidates = new (string Side, double Away)[]
        {
            (CanvasEdge.Left, Math.Abs(centreX - _view.ToScreenX(node.X))),
            (CanvasEdge.Right, Math.Abs(centreX - _view.ToScreenX(node.Right))),
            (CanvasEdge.Top, Math.Abs(centreY - _view.ToScreenY(node.Y))),
            (CanvasEdge.Bottom, Math.Abs(centreY - _view.ToScreenY(node.Bottom))),
        };

        return candidates.MinBy(candidate => candidate.Away).Side;
    }

    /// <summary>边缘命中带回的是 w/e/n/s（缩放那一套的坐标轴），文档里写的是格式那四个名字。</summary>
    private static string SpecSide(string band) => band switch
    {
        "w" => CanvasEdge.Left,
        "e" => CanvasEdge.Right,
        "n" => CanvasEdge.Top,
        _ => CanvasEdge.Bottom,
    };

    /// <summary>叠在上面的先命中——和指针选中同一套规矩：看到的顺序就是点的顺序。</summary>
    private CanvasNode? NodeAt(Point screen)
    {
        var world = new Point(_view.ToWorldX(screen.X), _view.ToWorldY(screen.Y));

        for (var index = _document.Nodes.Count - 1; index >= 0; index--)
        {
            var node = _document.Nodes[index];

            if (node.Width <= 0 || node.Height <= 0)
            {
                continue;
            }

            if (world.X >= node.X && world.X <= node.Right && world.Y >= node.Y && world.Y <= node.Bottom)
            {
                return node;
            }
        }

        return null;
    }

    private static string SideWord(string side) => side switch
    {
        CanvasEdge.Left => "左边",
        CanvasEdge.Right => "右边",
        CanvasEdge.Top => "上边",
        _ => "下边",
    };
}
