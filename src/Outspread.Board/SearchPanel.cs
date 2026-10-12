using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Outspread.Core;
using Windows.Foundation;

namespace Outspread.Board;

/// <summary>
/// 查找那一层：一格输入＋一份命中列表，点一行就把相机和选中放到那一格上。
/// </summary>
/// <remarks>
/// <para>
/// 这块板子的前提是"随手贴"，所以它最容易翻车的地方不是贴不进去，是贴进去三段话之后
/// 没有任何办法把它们找回来。在这层之前，板上唯一的检索是 `@t`/`@d` 两枚标记——
/// 那要求你贴的时候就想好了它将来会被怎么找。
/// </para>
/// <para>
/// 命中行为什么不带"把命中的那几个字加粗/染色"：这一套浮层只有两种线（正文墨、禁用墨），
/// 这正是他第三次打回"从字体、排版到动画都完全不一样"之后钉死的。为强调开第三种线，
/// 就是把刚收回去的多样性又请回来一次。所以左列是原文摘要，右列只有那一格是什么。
/// </para>
/// <para>
/// 这层占键盘的时候板子的键位一律不进（不变量 1），Esc 是纯放弃（不变量 5）：
/// 跳转只动相机与选中，不动文档，所以没有任何东西需要撤销。
/// </para>
/// </remarks>
public sealed partial class MainWindow
{
    /// <summary>一屏够读的行数。再多就不是"找东西"而是"把板子列一遍"了。</summary>
    private const int SearchRowsShown = 12;

    /// <summary>方向键一发的位移，单位是世界像素。</summary>
    private const double NudgePx = 1;

    private bool _searchOpen;

    private IReadOnlyList<BoardSearch.Hit> _searchHits = [];

    private int _searchCursor = -1;

    private TextBox _searchBox = null!;

    /// <summary>建窗时搭一次：以后只有文字和行数在变，结构不变，就不会长出第二、第三种组件。</summary>
    private void BuildSearchSurface()
    {
        _searchBox = PanelKit.Input("查找 · 空格＝都要命中，Enter 下一处，Esc 收起");

        SearchBody.Children.Add(_searchBox);

        _searchBox.TextChanged += (_, _) => RunSearch();
        _searchBox.KeyDown += OnSearchBoxKeyDown;
    }

    private void OpenSearch()
    {
        SetRestorePanel(false);
        SetSearchPanel(true);

        // 焦点必须真的落进那一格：这层的意义是"开始打字就在找"，
        // 而板子持焦点时打进去的每一个字都会变成板子的键位。
        _searchBox.Focus(FocusState.Programmatic);

        RunSearch();
    }

    private void SetSearchPanel(bool open)
    {
        if (open)
        {
            ChromeStyling.Ensure(NoteStyling.IsDark);
            ChromeStyling.ApplyFloatingChrome(SearchPanel);
        }

        SearchPanel.Visibility = open ? Visibility.Visible : Visibility.Collapsed;

        _searchOpen = open;
    }

    private void CloseSearchPanel()
    {
        if (!_searchOpen)
        {
            return;
        }

        SetSearchPanel(false);

        // 收起之后把焦点还给板子，否则下一个键会打在已经不存在的输入框上。
        BoardArea.Focus(FocusState.Programmatic);
    }

    /// <summary>面板自己的皮上按一发＝收起，并且不许穿到板子上去选中后面那格。</summary>
    private void OnSearchPanelPressed(object sender, PointerRoutedEventArgs e)
    {
        e.Handled = true;

        CloseSearchPanel();
    }

    private void OnSearchBoxKeyDown(object sender, KeyRoutedEventArgs e)
    {
        switch (e.Key)
        {
            case Windows.System.VirtualKey.Escape:
                CloseSearchPanel();
                e.Handled = true;
                break;

            case Windows.System.VirtualKey.Enter:
                JumpToHit(_searchCursor + (IsShiftDown() ? -1 : 1));
                e.Handled = true;
                break;

            case Windows.System.VirtualKey.F3:
                JumpToHit(_searchCursor + 1);
                e.Handled = true;
                break;
        }
    }

    private static bool IsShiftDown()
        => Microsoft.UI.Input.InputKeyboardSource
            .GetKeyStateForCurrentThread(Windows.System.VirtualKey.Shift)
            .HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down);

    private void RunSearch()
    {
        var query = _searchBox.Text;

        _searchHits = BoardSearch.Find(_document, query);
        _searchCursor = -1;

        if (string.IsNullOrWhiteSpace(query))
        {
            SearchScroll.Content = null;

            return;
        }

        if (_searchHits.Count == 0)
        {
            SearchScroll.Content = PanelKit.Host(PanelKit.Line("板上没有哪一格含这些字"));

            return;
        }

        var rows = new List<FrameworkElement>();

        for (var i = 0; i < Math.Min(SearchRowsShown, _searchHits.Count); i++)
        {
            var hit = _searchHits[i];
            var index = i;

            rows.Add(
                PanelKit.Row(
                    hit.Snippet,
                    KindWord(hit.Kind),
                    () => JumpToHit(index)));
        }

        if (_searchHits.Count > SearchRowsShown)
        {
            rows.Add(PanelKit.Line($"还有 {_searchHits.Count - SearchRowsShown} 处（再按 Enter 继续跳）"));
        }

        SearchScroll.Content = PanelKit.Host(rows.ToArray());
    }

    /// <summary>跳到第 index 处命中（越界就绕回）：相机居中、只选那一格。</summary>
    private void JumpToHit(int index)
    {
        if (_searchHits.Count == 0)
        {
            Say("现在没有可跳的命中");

            return;
        }

        _searchCursor = ((index % _searchHits.Count) + _searchHits.Count) % _searchHits.Count;

        var hit = _searchHits[_searchCursor];

        var node = _document.Nodes.FirstOrDefault(candidate => candidate.Id == hit.NodeId);

        if (node is null)
        {
            // 列表是上一发按键建的，板子在这之间可能被外面改过——旧 id 不猜，直接说。
            Say("那一格已经不在板上了（板子刚被改过？重新查找一次）");
            RunSearch();

            return;
        }

        CenterOn(node);

        ClearSelection();
        _selection.Add(node.Id);

        Rebuild();
        ProjectSelection();
        SaveSoon();

        Say($"第 {_searchCursor + 1}/{_searchHits.Count} 处命中 · 跳到了那一格（Enter 下一处，Shift＋Enter 上一处）");
    }

    /// <summary>把相机挪到那一格正中间，缩放不动——"看到它在哪"不等于"把它放大"。</summary>
    private void CenterOn(CanvasNode node)
    {
        _view.PanX = BoardArea.ActualWidth / 2 - (node.X + node.Width / 2) * _view.Zoom;
        _view.PanY = BoardArea.ActualHeight / 2 - (node.Y + node.Height / 2) * _view.Zoom;

        ArrangeAll();
    }

    /// <summary>右列那一个词：命中来自哪一型格子。<see cref="CanvasNode.NameOfKind"/> 给的是
    /// 格式里的令牌（text/file/link），那是校验文案要用的，不是给人读的那一列。</summary>
    private static string KindWord(CanvasNodeKind kind) => kind switch
    {
        CanvasNodeKind.File => "文件",
        CanvasNodeKind.Link => "链接",
        CanvasNodeKind.Group => "分组",
        _ => "正文",
    };

    /// <summary>
    /// 自检用的那半句话：从第一格有正文的格子里取第一个不是标记行的行的前几个字。
    /// 取自板子本身，所以换一块板子它仍然在问同一个问题——"搜板上有的东西搜得到吗"。
    /// </summary>
    private string ProbeTerm()
    {
        foreach (var node in BoardNotes())
        {
            // 文件格与链接格的 Text 是 null（规范里那两型没有正文），所以先收下再看。
            if (node.Text is not { Length: > 0 } text)
            {
                continue;
            }

            foreach (var line in text.Split('\n'))
            {
                var trimmed = line.Trim();

                // @d/@t 那两行是标记，不是"用户在找的那种内容"；标题的井号会跟着进查询串。
                if (trimmed.Length >= 4 && !trimmed.StartsWith('@') && !trimmed.StartsWith('#'))
                {
                    return trimmed[..4];
                }
            }
        }

        return string.Empty;
    }

    // -------------------------------------------------------------------------
    // 相机：看到全部 / 看到选中
    // -------------------------------------------------------------------------

    private void ZoomToFit() => Frame(_document.Nodes, "看到全部");

    private void ZoomToSelection()
    {
        var members = _document.Nodes.Where(node => _selection.Contains(node.Id)).ToList();

        if (members.Count == 0)
        {
            // 不合法的命令要明着拒绝（不变量 4）：说清它要什么、现在有什么。
            Say("『看到选中』要先选中点什么，现在选了 0 格");

            return;
        }

        Frame(members, "看到选中");
    }

    /// <summary>
    /// 把这些框收进视野，缩放取"两个轴都装得下"的那一档，并且只缩不放超过 100%——
    /// 这两条命令要的是看清全局，不是把一格放大到糊。
    /// </summary>
    private void Frame(IReadOnlyList<CanvasNode> members, string label)
    {
        if (members.Count == 0)
        {
            Say("板子是空的，回到 100%");
            ResetView();

            return;
        }

        var left = members.Min(node => node.X);
        var top = members.Min(node => node.Y);

        var width = Math.Max(members.Max(node => node.X + node.Width) - left, 1);
        var height = Math.Max(members.Max(node => node.Y + node.Height) - top, 1);

        var pad = Chrome.Radius * 2;

        var zoom = Math.Min(
            (BoardArea.ActualWidth - pad * 2) / width,
            (BoardArea.ActualHeight - pad * 2) / height);

        _view.Zoom = Math.Clamp(Math.Min(zoom, 1), MinZoom, MaxZoom);

        _view.PanX = BoardArea.ActualWidth / 2 - (left + width / 2) * _view.Zoom;
        _view.PanY = BoardArea.ActualHeight / 2 - (top + height / 2) * _view.Zoom;

        ArrangeAll();
        ProjectSelection();
        SaveSoon();

        Say($"{label}：{members.Count} 格，缩放 {Percent()}");
    }

    /// <summary>
    /// 方向键微调：一次一格世界像素，Shift 是 10 倍。它走事务，所以一发按键＝一步撤销。
    /// </summary>
    /// <remarks>
    /// 位移按世界像素而不是屏幕像素：缩到 20% 的时候"1 屏幕像素"是 5 世界像素，
    /// 那样微调的粗细就跟着相机走，而同一个键位要的是一次对齐上的移动。
    /// </remarks>
    private void Nudge(double dx, double dy)
    {
        var members = SelectedNotes();

        if (members.Count == 0)
        {
            // 不是静默 no-op（不变量 4）：说清楚这个键要什么。
            Say("方向键挪的是选中的格子，现在一格都没选");

            return;
        }

        BeginGesture($"挪了 {members.Count} 格");

        foreach (var node in members)
        {
            node.X += dx;
            node.Y += dy;
        }

        EndGesture();

        Rebuild();
        SaveSoon();
        ProjectSelection();

        Say($"挪了 {members.Count} 格 · {Math.Abs(dx) + Math.Abs(dy):0.#} 世界像素（Shift 是 10 倍，Ctrl＋Z 放回）");
    }
}
