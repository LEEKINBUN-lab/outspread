using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Outspread.Core;

namespace Outspread.Board;

/// <summary>
/// 筛上以后，组里<b>没命中</b>的那几格折到<b>命中</b>的那格背后；鼠标回到这个组上就摊开，走了再折回去。
/// </summary>
/// <remarks>
/// <para>
/// 三条边界写死在这儿：
/// </para>
/// <list type="number">
/// <item>折叠只长在画面上（一枚 <c>TranslateTransform</c> ＋ 透明度），文档一个字节不动。筛选把三格
/// 并到一处的样子要是写回文件，一次筛选就把便签的"真位置"同步给了手机。</item>
/// <item>藏起来的东西必须有一句"这里折着几格"，否则筛选看起来像把便签删了。那句话长在框自己的标题
/// 那一格里，不新发明一种控件。</item>
/// <item>摊开的那几格<b>仍然压暗</b>（它们没命中）。所以"筛没筛上"这件事不会因为一次悬停而说谎。</item>
/// </list>
/// <para>
/// 时长来自 <see cref="Motion"/>（也就是网页那份令牌）；曲线只有平台的 <c>CubicEase</c> 可用，
/// 与页面的贝塞尔差在哪记在 docs/research-chrome-and-properties.md。
/// </para>
/// </remarks>
public sealed partial class MainWindow
{
    private string? _expandedGroup;

    /// <summary>正在播动画的格子：那几格的位移交给 Storyboard，布局这一发不许插手。</summary>
    private readonly HashSet<string> _tweening = new(StringComparer.Ordinal);

    /// <summary>折起来的格子 → 它属于哪个组、该滑到哪。每次排版重建。</summary>
    private readonly Dictionary<string, (string Group, double DX, double DY)> _folded = new(StringComparer.Ordinal);

    /// <summary>hover 在哪一格上要摊开哪个组：框本身，加上接住东西的那一格。</summary>
    private readonly Dictionary<string, string> _foldOwners = new(StringComparer.Ordinal);

    private bool Shows(CanvasNode node)
    {
        var marks = Tags.Read(node.Text, _view.Today);

        return NoteMarks.Shows(NoteMarks.StateOf(marks, _view.Today), marks.Tags, marks.Done, _view.TagFilter, _view.DueFilter, _view.DoneFilter);
    }

    /// <summary>这一次算出来的画面状态；横幅那句"折进了几格"读的就是它。</summary>
    private Dictionary<string, NodeVisual.Display> _displays = [];

    internal int FoldedAwayCount => _displays.Count(pair => pair.Value.FoldedAway);

    /// <summary>
    /// 这一发排版每个格子该被画成什么样。没在筛的时候返回空表——那条路一格都不该被动过。
    /// </summary>
    private Dictionary<string, NodeVisual.Display> BuildDisplays()
    {
        _folded.Clear();
        _foldOwners.Clear();

        var displays = new Dictionary<string, NodeVisual.Display>(StringComparer.Ordinal);

        if (!_view.Filtering)
        {
            _displays = displays;

            return displays;
        }

        var notes = BoardNotes();

        foreach (var frame in GroupRects())
        {
            var members = Groups().Members(frame.Id).Select(member => NodeOf(member.Id)!).ToList();

            var rects = members.ConvertAll(RectOf);

            // 子组框没有正文可筛：它永远算"看得见"，不许被折到某一格背后（嵌套档）。
            var plan = Folding.PlanOf(members
                .Select((node, i) => (rects[i], node.Kind == CanvasNodeKind.Group || Shows(node)))
                .ToList());

            if (!plan.AnythingFolded)
            {
                continue;
            }

            var visible = new List<Grouping.Rect>();

            for (var i = 0; i < members.Count; i++)
            {
                if (!plan.Folds[i].Folded)
                {
                    visible.Add(rects[i]);
                }
            }

            var expanded = string.Equals(_expandedGroup, frame.Id, StringComparison.Ordinal);

            for (var i = 0; i < members.Count; i++)
            {
                var fold = plan.Folds[i];
                var id = members[i].Id;

                if (!fold.Folded)
                {
                    continue;
                }

                var dx = fold.DX * _view.Zoom;
                var dy = fold.DY * _view.Zoom;

                _folded[id] = (frame.Id, dx, dy);

                displays[id] = _tweening.Contains(id)
                    ? new NodeVisual.Display(null, null, false, null, null)
                    : expanded
                        ? new NodeVisual.Display(0, 0, false, null, null)
                        : new NodeVisual.Display(dx, dy, true, null, null);
            }

            // 摊开的时候框要回到全组：藏起来的东西没了，框就没有"只框住命中者"这回事了。
            displays[frame.Id] = new NodeVisual.Display(
                null,
                null,
                false,
                Grouping.Enclose(frame.Id, expanded ? rects : visible),
                $" · 折叠 {plan.FoldedCount} 个便签",
                Revealed: true);

            _foldOwners[frame.Id] = frame.Id;

            if (plan.AnchorId is not null)
            {
                _foldOwners[plan.AnchorId] = frame.Id;
            }
        }

        _displays = displays;

        return displays;
    }

    private void OnVisualEntered(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
    {
        if (sender is NodeVisual visual && _foldOwners.TryGetValue(visual.NodeId, out var groupId))
        {
            SetGroupExpanded(groupId, expanded: true);
        }
    }

    private void OnVisualExited(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
    {
        if (sender is NodeVisual visual && _foldOwners.TryGetValue(visual.NodeId, out var groupId)
            && string.Equals(_expandedGroup, groupId, StringComparison.Ordinal))
        {
            SetGroupExpanded(groupId, expanded: false);
        }
    }

    /// <summary>
    /// 摊开或折回一个组。位移与淡入淡出走 Storyboard，落点由 <see cref="BuildDisplays"/> 负责——
    /// 动画停下来的那一格，和下一次排版算出来的那一格必须是同一个位置。
    /// </summary>
    /// <param name="animate">
    /// False 用于取证：直接把状态摆到位再排版，不等那一秒。动画的中帧拍不到，也不该拿"等一会儿"
    /// 换确定性，所以 <c>shot --panel fold／unfold</c> 走这一档，两个终态各自可复验。
    /// </param>
    private void SetGroupExpanded(string groupId, bool expanded, bool animate = true)
    {
        if (string.Equals(_expandedGroup, groupId, StringComparison.Ordinal) && expanded)
        {
            return;
        }

        _expandedGroup = expanded ? groupId : null;

        var targets = _folded.Where(pair => pair.Value.Group == groupId).Select(pair => pair.Key).ToList();

        if (animate)
        {
            foreach (var id in targets)
            {
                if (_realized.TryGetValue(id, out var visual))
                {
                    var fold = _folded[id];

                    TweenFold(visual, expanded ? 0 : fold.DX, expanded ? 0 : fold.DY, expanded ? 0.22 : 0, expanded);
                }
            }
        }

        if (!animate || targets.Count == 0)
        {
            ArrangeAll();
        }
    }

    /// <summary>
    /// 找一块"一组里有的命中、有的不命中"的形状——取证那一发要拍的就是它，而它不该由拍的人造。
    /// </summary>
    private (string Group, string Tag)? FoldProbe()
    {
        foreach (var frame in GroupRects())
        {
            var members = Groups().Members(frame.Id).Select(member => NodeOf(member.Id)!).ToList();

            if (members.Count < 2)
            {
                continue;
            }

            var partial = NoteMarks.Tally(members.ConvertAll(node => node.Text))
                .FirstOrDefault(pair => pair.Count > 0 && pair.Count < members.Count);

            if (partial.Tag is { Length: > 0 })
            {
                return (frame.Id, partial.Tag);
            }
        }

        return null;
    }

    /// <summary>关掉当前那一层的展开：Esc 与"手离开了这扇窗口"走这里。</summary>
    private bool CollapseExpandedGroup()
    {
        if (_expandedGroup is not { } groupId)
        {
            return false;
        }

        SetGroupExpanded(groupId, expanded: false);

        return true;
    }

    private void TweenFold(NodeVisual visual, double toX, double toY, double toOpacity, bool entering)
    {
        // 折叠平移住在卡片根那枚 TranslateTransform 上（屏幕像素）；缩放住在内容层，两枚不打架。
        if (visual.FoldShift is not { } shift)
        {
            return;
        }

        var ms = entering ? Motion.EnterMs : Motion.BaseMs;

        _tweening.Add(visual.NodeId);

        var board = new Storyboard { FillBehavior = FillBehavior.Stop };

        AddShift(board, shift, "X", toX, ms, entering);
        AddShift(board, shift, "Y", toY, ms, entering);
        AddShift(board, visual, "Opacity", toOpacity, ms, entering);

        board.Completed += (_, _) =>
        {
            _tweening.Remove(visual.NodeId);

            // 落点交回布局：动画把值停在结束时那一格，排版负责它到底该站哪儿。
            ArrangeAll();
        };

        board.Begin();
    }

    private static void AddShift(
        Storyboard board,
        DependencyObject target,
        string path,
        double to,
        double ms,
        bool entering)
    {
        var animation = new DoubleAnimation
        {
            To = to,
            Duration = TimeSpan.FromMilliseconds(ms),

            // 摊开＝减速到位；折回＝加速离开。页面那两条贝塞尔这一投影喂不进去（只有平台的 CubicEase），
            // 差的这一格记在 docs/research-chrome-and-properties.md。
            EasingFunction = new CubicEase
            {
                EasingMode = entering ? EasingMode.EaseOut : EasingMode.EaseIn,
            },
        };

        Storyboard.SetTarget(animation, target);
        Storyboard.SetTargetProperty(animation, path);

        board.Children.Add(animation);
    }

    /// <summary>筛选换了、板子换了：折叠状态没有意义了，全部归零。</summary>
    private void ResetFolding()
    {
        _expandedGroup = null;
        _tweening.Clear();
        _folded.Clear();
        _foldOwners.Clear();
    }
}
