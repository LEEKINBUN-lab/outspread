using Outspread.Core;

namespace Outspread.Board;

/// <summary>
/// The command table, and the commands that change the document's shape rather than its geometry.
/// </summary>
/// <remarks>
/// <para>
/// One row per command, read by the menu, by the keyboard dispatcher and by the self test — the
/// prototype's <c>CMDS</c> shape. That is not tidiness: his config defers bare-key shortcuts, which
/// leaves the menu as the only non-keyboard way to reach a command (WCAG 2.5.7), so a command that
/// exists in the key handler but not in the menu is a rule violation, and a menu entry with no
/// handler is a lie. Generating both from one table makes both impossible.
/// </para>
/// <para>
/// Chords are modifier combinations only, and every command states its precondition in the same
/// breath as its action — refusing says how many notes were needed and how many were offered.
/// </para>
/// </remarks>
public sealed partial class MainWindow
{
    /// <param name="Dispatched">
    /// False for the handful of keys the window still handles in its own switch (Esc, Ctrl+A,
    /// Ctrl+V, Ctrl+Z/Y, Delete, Ctrl+0). They live in this table so the cheat sheet cannot lie
    /// about them, but the table must not also run them — that would fire twice. M1b's registry
    /// pass is meant to fold those six into the switch-free path and drop this flag.
    /// </param>
    private readonly record struct Command(string Label, string Chord, string Section, Action Run, bool Dispatched = true)
    {
        public string MenuText => Chord is { Length: > 0 } chord ? $"{Label}    {chord}" : Label;
    }

    private Command[] Commands() =>
    [
        new("左对齐", "Alt+A", "排布", () => Arrange("左对齐", items => Aligning.Align(items, Aligning.Side.Left))),
        new("水平居中", "Alt+H", "排布", () => Arrange("水平居中", items => Aligning.Align(items, Aligning.Side.HCenter))),
        new("右对齐", "Alt+D", "排布", () => Arrange("右对齐", items => Aligning.Align(items, Aligning.Side.Right))),
        new("上对齐", "Alt+W", "排布", () => Arrange("上对齐", items => Aligning.Align(items, Aligning.Side.Top))),
        new("垂直居中", "Alt+V", "排布", () => Arrange("垂直居中", items => Aligning.Align(items, Aligning.Side.VCenter))),
        new("下对齐", "Alt+S", "排布", () => Arrange("下对齐", items => Aligning.Align(items, Aligning.Side.Bottom))),
        new("横向均分", "Alt+J", "排布", () => Arrange("横向均分", items => Aligning.Distribute(items, horizontal: true))),
        new("纵向均分", "Alt+K", "排布", () => Arrange("纵向均分", items => Aligning.Distribute(items, horizontal: false))),
        new("堆叠", "Alt+L", "排布", () => Arrange("堆叠", Aligning.Stack)),

        // 微调走的是窗口自己那枚 switch（e.Key 的名字是 Up/Down/Left/Right，写不成一张表里的键位串），
        // 但每一行仍在这里：菜单是单键快捷键缺席时唯一的替代面（WCAG 2.5.7），不在表里就等于够不着。
        new("上移一格", "↑", "排布", () => Nudge(0, -NudgePx), Dispatched: false),
        new("下移一格", "↓", "排布", () => Nudge(0, NudgePx), Dispatched: false),
        new("左移一格", "←", "排布", () => Nudge(-NudgePx, 0), Dispatched: false),
        new("右移一格", "→", "排布", () => Nudge(NudgePx, 0), Dispatched: false),
        new("按颜色聚拢", "Alt+Y", "聚拢", () => Cluster("按颜色聚拢", node => node.Color ?? "none")),
        new("按标签聚拢", "Alt+T", "聚拢", () => Cluster("按标签聚拢", node => Tags.Of(node.Text).FirstOrDefault() ?? "#无标签")),
        new("成组", "Ctrl+G", "组织", GroupSelection),
        new("拆组", "Ctrl+Shift+G", "组织", UngroupSelection),
        new("复制", "Ctrl+D", "组织", DuplicateSelection),

        // 连线按叠放次序把选中的串成一条链，全都在就是拆链（原型同一条命令两用）。
        new("连线", "Alt+C", "组织", ConnectSelection),
        new("全部选中", "Ctrl+A", "板子", SelectAll, Dispatched: false),
        new("粘贴剪贴板", "Ctrl+V", "板子", () => _ = PasteFromClipboardAsync(), Dispatched: false),
        new("取消选中", "Esc", "板子", () => { ClearSelection(); ProjectSelection(); }, Dispatched: false),
        new("删掉选中", "Delete", "板子", RemoveSelection, Dispatched: false),
        new("撤回", "Ctrl+Z", "板子", Undo, Dispatched: false),
        new("重做", "Ctrl+Y", "板子", Redo, Dispatched: false),
        new("回到 100%", "Ctrl+0", "看", ResetView, Dispatched: false),
        new("新建一格", "双击空白", "板子", NewNoteAtCentre, Dispatched: false),

        // 查找与恢复没有第二个入口的要不得：查找是"贴进去的东西找得回来"，恢复是"改坏了还够得着"。
        new("查找", "Ctrl+F", "板子", OpenSearch),
        new("恢复…", string.Empty, "板子", OpenRestorePanel),

        // Shift+1 / Shift+2 同样走那枚 switch（数字键的 e.Key 是 Number1），这里只负责菜单与清单上有它。
        new("看到全部", "Shift+1", "看", ZoomToFit, Dispatched: false),
        new("看到选中", "Shift+2", "看", ZoomToSelection, Dispatched: false),
    ];

    /// <summary>Every node on the board — the same call the keyboard's Ctrl+A makes.</summary>
    private void SelectAll()
    {
        foreach (var node in _document.Nodes)
        {
            _selection.Add(node.Id);
        }

        ProjectSelection();

        Say($"选中 {_selection.Count} 个节点 · 缩放 {Percent()}");
    }

    /// <summary>Sections in first-appearance order, so the menu reads like this table.</summary>
    private IEnumerable<IGrouping<string, Command>> Sections() => Commands().GroupBy(command => command.Section);

    // ---------------------------------------------------------------------
    // The group layer
    // ---------------------------------------------------------------------

    /// <summary>
    /// Which group's layer is active — the app's half of the prototype's <c>groupFocus</c>.
    /// </summary>
    /// <remarks>
    /// It is a property of the <b>selection</b>, not of the document: the same frame is either
    /// "the thing I'm working on" or "the box around the thing I'm working on". It resets with the
    /// selection, because a stale layer is how the second click stops being a second click.
    /// </remarks>
    private string? _groupFocus;

    private List<CanvasNode> SelectedNotes()
        => _document.Nodes.Where(node => _selection.Contains(node.Id) && node.Kind != CanvasNodeKind.Group).ToList();

    private List<CanvasNode> BoardNotes()
        => _document.Nodes.Where(node => node.Kind != CanvasNodeKind.Group).ToList();

    private List<Grouping.Rect> GroupRects()
        => _document.Nodes.Where(node => node.Kind == CanvasNodeKind.Group).Select(RectOf).ToList();

    private static Grouping.Rect RectOf(CanvasNode node) => new(node.Id, node.X, node.Y, node.Width, node.Height);

    private static Aligning.Placed PlacedOf(CanvasNode node)
        => new(node.Id, node.X, node.Y, node.Width, node.Height);

    /// <summary>Clear the selection and the layer with it — never one without the other.</summary>
    private void ClearSelection()
    {
        _selection.Clear();
        _groupFocus = null;
    }

    private void GroupSelection()
    {
        var members = SelectedNotes();

        if (members.Count < 2)
        {
            Say($"『成组』要至少 2 格，现在选了 {members.Count} 格");
            return;
        }

        BeginGesture("成组");

        var frame = Grouping.Enclose(string.Empty, members.ConvertAll(RectOf));
        var made = _document.Add(CanvasNodeKind.Group, frame.X, frame.Y, frame.Width, frame.Height);

        EndGesture();

        _groupFocus = made.Id;
        Rebuild();
        SaveSoon();
        ProjectSelection();

        Say($"成组：框住 {members.Count} 格，现在停在组这一层（再点一次才进单枚）");
    }

    private void UngroupSelection()
    {
        var groups = GroupRects();
        var hit = new HashSet<string>(StringComparer.Ordinal);

        foreach (var node in SelectedNotes())
        {
            if (Grouping.GroupOf(RectOf(node), groups) is { } group)
            {
                hit.Add(group.Id);
            }
        }

        foreach (var id in _selection.Where(selected => groups.Any(group => group.Id == selected)))
        {
            hit.Add(id);
        }

        if (hit.Count == 0)
        {
            Say("选中的格子里没有哪格在组里");
            return;
        }

        BeginGesture("拆组");

        foreach (var id in hit)
        {
            _document.Remove(id);
        }

        _groupFocus = null;

        EndGesture();
        Rebuild();
        SaveSoon();
        ProjectSelection();

        Say($"拆掉了 {hit.Count} 个组，格子的位置没动（Ctrl＋Z 可回退）");
    }

    private void DuplicateSelection()
    {
        var members = SelectedNotes();

        if (members.Count == 0)
        {
            Say("没有选中任何格子");
            return;
        }

        BeginGesture($"复制 {members.Count} 格");

        foreach (var node in members)
        {
            var copy = _document.Add(
                node.Kind,
                node.X + Aligning.MinGapPx,
                node.Y + Aligning.MinGapPx,
                node.Width,
                node.Height,
                node.Color);

            copy.Text = node.Text;
            copy.FilePath = node.FilePath;
            copy.Url = node.Url;
            copy.Label = node.Label;
        }

        EndGesture();
        Rebuild();
        SaveSoon();

        Say($"复制了 {members.Count} 格，副本在右下方各 {Aligning.MinGapPx:0} 世界像素");
    }

    /// <summary>
    /// Re-flow the whole board by a key, not just the selection — the command's promise is "put
    /// like with like", which means nothing over a subset.
    /// </summary>
    private void Cluster(string label, Func<CanvasNode, string> keyOf)
    {
        var items = BoardNotes();

        var outcome = Aligning.Cluster(items.ConvertAll(PlacedOf), items.Select(keyOf).ToList());

        if (!outcome.Ok)
        {
            Say($"『{label}』要至少 {outcome.Needed} 格，板上有 {outcome.Have} 格");
            return;
        }

        ApplyMoves(label, outcome);
    }
}
