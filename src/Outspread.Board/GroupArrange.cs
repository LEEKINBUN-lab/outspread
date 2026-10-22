using Microsoft.UI.Xaml;
using Outspread.Core;

namespace Outspread.Board;

/// <summary>
/// 组这件事上，界面这一侧只有一条根：<b>框是派生的</b>——它是成员包围盒加一圈 Pad，
/// 不是一件可以被单独摆到某处的东西。
/// </summary>
/// <remarks>
/// <para>
/// 这条以前只在"成组"那一次成立：<c>Enclose</c> 全仓只有一个调用点（<c>GroupSelection</c>），
/// 之后成员被搬走、被排成一列、被聚拢，框都留在原地。于是"点击排序后分组混乱"是必然的：
/// 实测那一发 Ctrl+A＋左对齐，25 个节点里连框一起排，框停在 <c>-24,40 620×300</c>，
/// 它那些成员要的是 <c>-34,30 420×280</c>（自检读到的原话）。
/// </para>
/// <para>
/// 现在每一步结束都重算一次框，并且只写"这一步真的让某个框的派生值变了"的那些：
/// 一次改颜色不该顺手把与它无关的框搬走，而外部改动留下的旧框会在下一次真动到它时归位。
/// </para>
/// <para>
/// 第二条规矩是<b>排布按块作用</b>（<see cref="SelectedUnits"/>）：一个组是一件东西，整组一起平移，
/// 组内相对位置不变。只剩一件时退到"排它里面那几枚"——一件东西没法对齐自己，而屏幕上看得见的是
/// 那几枚便签。所以"点一下组再按左对齐"排组内，"框选一片"排块，同一把尺量的两种局面。
/// </para>
/// </remarks>
public sealed partial class MainWindow
{
    /// <summary>这一步开始时每个框"应该是多大"。null 表示现在不在任何一步里。</summary>
    private Dictionary<string, Grouping.Rect>? _derivedFramesAtBegin;

    /// <summary>
    /// 开板时先把有成员的组框贴回成员包围盒。`RefitFrames` 只在一次手势结束时跑；一块
    /// 从外部改过、或上一版留下的旧板，组框可能还停在旧位置。画出来就是"虚线框切过便签"。
    /// 空组不动：它没有成员可以贴，渲染层会把它藏起来，等人把便签挪进来再复活。
    /// </summary>
    private bool RefitLoadedGroups()
    {
        if (_document is null)
        {
            return false;
        }

        var hug = DerivedFrames();
        var changed = 0;
        var details = new List<string>();

        foreach (var frame in GroupRects())
        {
            var group = NodeOf(frame.Id);

            if (group is null || group.Kind != CanvasNodeKind.Group)
            {
                continue;
            }

            if (!hug.TryGetValue(frame.Id, out var derived) || SameGroupBox(frame, derived))
            {
                continue;
            }

            group.X = derived.X;
            group.Y = derived.Y;
            group.Width = derived.Width;
            group.Height = derived.Height;
            changed++;
            details.Add(
                $"{frame.Id} {frame.X:0.#},{frame.Y:0.#} {frame.Width:0.#}×{frame.Height:0.#}" +
                $" → {derived.X:0.#},{derived.Y:0.#} {derived.Width:0.#}×{derived.Height:0.#}");
        }

        if (changed > 0)
        {
            Diagnostics.Write($"group refit on load: {changed} 组（{string.Join("；", details)}）");
        }

        return changed > 0;
    }

    private static bool SameGroupBox(Grouping.Rect left, Grouping.Rect right)
        => Math.Abs(left.X - right.X) <= 0.001
            && Math.Abs(left.Y - right.Y) <= 0.001
            && Math.Abs(left.Width - right.Width) <= 0.001
            && Math.Abs(left.Height - right.Height) <= 0.001;

    /// <summary>没有成员的组：框没有可贴的东西，渲染层把它藏起来，别拿一个空的大方框压着别人的便签。</summary>
    private HashSet<string> EmptyGroupIds()
    {
        var notes = BoardNotes().ConvertAll(RectOf);
        var empty = new HashSet<string>(StringComparer.Ordinal);

        foreach (var frame in GroupRects())
        {
            if (!notes.Exists(note => frame.Holds(note)))
            {
                empty.Add(frame.Id);
            }
        }

        return empty;
    }

    /// <summary>
    /// 自检：有成员的组框必须已经等于成员包围盒；空组必须不可见、不可点。
    /// 这是对 10-22 截图里"虚线框切过便签、旁边还有一个空的大框"那两格的回归锁。
    /// </summary>
    private void SelfTestGroups(List<string> problems)
    {
        if (_document is null)
        {
            problems.Add("自检组框：窗口没有文档");

            return;
        }

        var groups = _document.Nodes.Where(node => node.Kind == CanvasNodeKind.Group).ToList();

        if (groups.Count == 0)
        {
            Diagnostics.Write("selftest 组框: 板上没有组，这一发不适用（不是通过）");

            return;
        }

        var notes = BoardNotes();
        var hug = 0;
        var hidden = 0;
        var culled = 0;

        // "有成员的组必须画出来"这条只能对**在视野里**的组成立：ArrangeAll 会按视口剔除，
        // 一块滚出视野的组本就该 Collapsed（line 572），那不是漏画，是省下的那一发解码。
        // 不先按同一把尺量视野，这条断言会在板子变宽、组落在视野外时假红——10-22 他手工
        // 拖过"测试"那格、把演示组挤到视野外之后，自检就是这样红的。
        var viewport = _view.WorldViewport(BoardArea.ActualWidth, BoardArea.ActualHeight);
        var margin = CullMargin / _view.Zoom;
        var window = new Grouping.Rect(string.Empty, viewport.X - margin, viewport.Y - margin,
            viewport.Width + 2 * margin, viewport.Height + 2 * margin);

        foreach (var group in groups)
        {
            var stored = RectOf(group);
            var members = notes.Where(note => stored.Holds(RectOf(note))).ToList();
            var visual = _realized.TryGetValue(group.Id, out var realized) ? realized : null;
            var onScreen = window.Intersects(stored);

            if (members.Count == 0)
            {
                if (visual is null
                    || (visual.Visibility == Visibility.Collapsed && !visual.IsHitTestVisible))
                {
                    hidden++;
                }
                else
                {
                    problems.Add(
                        $"空组 {group.Id} 还露着：Visibility={visual?.Visibility}、Hit={visual?.IsHitTestVisible}");
                }

                continue;
            }

            var derived = Grouping.Enclose(group.Id, members.ConvertAll(RectOf));

            if (!SameGroupBox(stored, derived))
            {
                problems.Add(
                    $"组 {group.Id} 的框没贴成员：框 {stored.X:0.#},{stored.Y:0.#} {stored.Width:0.#}×{stored.Height:0.#}，" +
                    $"成员要 {derived.X:0.#},{derived.Y:0.#} {derived.Width:0.#}×{derived.Height:0.#}");
            }

            if (!onScreen)
            {
                // 组有成员但在视野外：它被剔除是正常的，只数不进可见性断言。
                culled++;
            }
            else if (visual is null || visual.Visibility != Visibility.Visible)
            {
                problems.Add(
                    $"有成员的组 {group.Id} 没画出来：Visibility={visual?.Visibility}、Hit={visual?.IsHitTestVisible}，" +
                    $"框 {stored.X:0.#},{stored.Y:0.#} {stored.Width:0.#}×{stored.Height:0.#}，" +
                    $"成员 " + string.Join("、", members.Select(member =>
                        $"{member.Id}@中心{RectOf(member).CenterX:0.#},{RectOf(member).CenterY:0.#}")));
            }

            hug++;
        }

        Diagnostics.Write(
            $"selftest 组框: {groups.Count} 组，有成员 {hug} 组贴包围盒（其中视野外 {culled} 组被剔除，不算漏画）、空组 {hidden} 组已藏");
    }

    /// <summary>
    /// 回归锁：新建的一格必须是自由的——不该因为那一带还压着一块看不见（空／视野外）的旧组框
    /// 就被悄悄收进组里。10-22 那块"演示：组"残留框正是这样：成员被拖走后它空了、藏了，
    /// 但矩形还在原位，新建／挪进来的格子落进去就"莫名其妙多出来一个组"。
    /// </summary>
    /// <remarks>
    /// 判点选在所有组都够不着的远端 (20000,20000)，量的是建出来那一刻 <c>GroupOf</c> 的归属，
    /// 建完即撤（一次 Undo 收回），不给临时副本留脏格。这条锁住"建出来就自由"，
    /// 而不是替"几何成员制"改判据——后者会波及同步契约与安卓端，得单独定。
    /// </remarks>
    private void SelfTestFreshNoteIsFree(List<string> problems)
    {
        if (_document is null)
        {
            problems.Add("自检新建自由格：窗口没有文档");

            return;
        }

        BeginGesture("自检新建自由格");

        var freeNote = CreateTextNote("自检自由格", new Windows.Foundation.Point(20000, 20000));

        EndGesture();

        if (Grouping.GroupOf(RectOf(freeNote), GroupRects()) is { } landedIn)
        {
            problems.Add(
                $"新建的格子落进了组 {landedIn.Id}：20000,20000 这一带还压着一块组框，" +
                $"新建一格不该被它接住");
        }

        Undo();

        Diagnostics.Write("selftest 新建自由格: 远端建一格 → GroupOf 为空 → 撤销收回（建出来就是自由的）");
    }

    /// <summary>
    /// 此刻的派生框：每个组 → Enclose(它现在那些成员)，并且**量到不再变大为止**。
    /// 没有成员的组保持它自己（不凭空缩成一点）。
    /// </summary>
    /// <remarks>
    /// 一步量完会留下"半程"：把框贴回成员以后，它可能正好把下一枚便签的中心盖进去——
    /// 那枚就此成了成员，框还得再长一圈。10-22 的自检抓到的正是半程：Ctrl+A＋左对齐把一列
    /// 格子送进组，第一程写出的框高 287，而盖进来的那枚图片格要 388，虚线框从它中间切过。
    /// 上限 32 圈：几何包含的收敛方向是单调扩张/收缩到稳定，真到不了稳定的形状留给下一发
    /// 排版再量，不许在这里死循环。
    /// </remarks>
    private Dictionary<string, Grouping.Rect> DerivedFrames()
    {
        var notes = BoardNotes().ConvertAll(RectOf);
        var derived = new Dictionary<string, Grouping.Rect>(StringComparer.Ordinal);

        foreach (var frame in GroupRects())
        {
            var box = frame;

            for (var growth = 0; growth < 32; growth++)
            {
                var members = notes.Where(note => box.Holds(note)).ToList();

                if (members.Count == 0)
                {
                    break;
                }

                var next = Grouping.Enclose(frame.Id, members);
                var steady = SameGroupBox(box, next);

                box = next;

                if (steady)
                {
                    break;
                }
            }

            derived[frame.Id] = box;
        }

        return derived;
    }

    /// <summary>
    /// 把框贴回它的成员上：只动这一步真的改变了派生值的那些组，并把它们写进同一步历史里
    /// （撤销一发的结果必须是"格子和框一起回去"，不是"格子回去了框还在那儿"）。
    /// </summary>
    private void RefitFrames()
    {
        if (_derivedFramesAtBegin is null)
        {
            return;
        }

        var now = DerivedFrames();

        foreach (var (groupId, atBegin) in _derivedFramesAtBegin)
        {
            var frameNode = NodeOf(groupId);

            if (frameNode is null || !now.TryGetValue(groupId, out var derived))
            {
                continue;
            }

            if (SameBox(atBegin, derived))
            {
                // 这一步没让这一格的派生值动过：不写它的字节。
                continue;
            }

            frameNode.X = derived.X;
            frameNode.Y = derived.Y;
            frameNode.Width = derived.Width;
            frameNode.Height = derived.Height;
        }

        static bool SameBox(Grouping.Rect left, Grouping.Rect right)
            => Math.Abs(left.X - right.X) <= 0.001
                && Math.Abs(left.Y - right.Y) <= 0.001
                && Math.Abs(left.Width - right.Width) <= 0.001
                && Math.Abs(left.Height - right.Height) <= 0.001;
    }

    /// <summary>
    /// 选中的东西归成"件"：一个组算一件（成员一起动），散格各算一件。
    /// </summary>
    /// <remarks>
    /// 只剩一件时退到<b>排它里面那几枚</b>：一件东西没法对齐自己。所以"点一下组再按左对齐"排的是
    /// 组内那几枚，"框选一片"排的是块——同一把尺量的两种局面，不是两套语义。
    /// 退回来时用的就是选中的那几枚，不是全组：只选了两枚时排那两枚，框随后 hug 全部成员。
    /// </remarks>
    private IReadOnlyList<Grouping.Unit> SelectedUnits()
    {
        var rects = SelectedNotes().ConvertAll(RectOf);

        if (rects.Count == 0)
        {
            return [];
        }

        var units = Grouping.UnitsOf(rects, GroupRects());

        return units.Count == 1 && units[0].MemberIds.Count > 1
            ? rects.ConvertAll(rect => new Grouping.Unit(rect.Id, rect, [rect.Id]))
            : units;
    }

    /// <summary>
    /// 把按"件"算出来的移动落到格子头上：一件动了，它那些成员整体平移同样的位移。
    /// </summary>
    private void ApplyUnitMoves(string label, IReadOnlyList<Grouping.Unit> units, Aligning.Outcome outcome)
    {
        if (!outcome.Ok)
        {
            Say($"『{label}』要至少 {outcome.Needed} 件（一格或一组算一件），现在能排的有 {outcome.Have} 件");

            return;
        }

        var deltas = new Dictionary<string, (double X, double Y)>(StringComparer.Ordinal);
        var byUnit = units.ToDictionary(unit => unit.Id, StringComparer.Ordinal);

        foreach (var move in outcome.Moves)
        {
            if (!byUnit.TryGetValue(move.Id, out var unit))
            {
                continue;
            }

            var delta = (X: move.X - unit.Box.X, Y: move.Y - unit.Box.Y);

            foreach (var id in unit.MemberIds)
            {
                deltas[id] = delta;
            }
        }

        var moved = deltas.Count(pair => Math.Abs(pair.Value.X) > 0.001 || Math.Abs(pair.Value.Y) > 0.001);

        if (moved == 0)
        {
            Say($"{label}：这些格子已经在位子上，没动任何东西");

            return;
        }

        // 有几件是"整块走的组"：这是这一发和"逐枚排"唯一的区别，说出来才看得出没记错。
        var blocks = units.Count(unit => unit.MemberIds.Count > 1);

        BeginGesture(label);

        foreach (var (id, delta) in deltas)
        {
            if (NodeOf(id) is { } node)
            {
                node.X += delta.X;
                node.Y += delta.Y;
            }
        }

        EndGesture();

        Rebuild();
        SaveSoon();

        Say($"{label}：搬了 {moved} 格" +
            (blocks > 0 ? $"，{blocks} 个组整块跟着走" : string.Empty) +
            "（Ctrl＋Z 可回退）");
    }
}
