using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Outspread.Core;

namespace Outspread.Board;

/// <summary>
/// The filter: what the board reads out of everyone's text — overdue, due today, unreadable dates,
/// and the tags in use — and the lens those readings apply.
/// </summary>
/// <remarks>
/// <para>
/// This exists because a mark nobody can see is indistinguishable from a mark that was not
/// understood. Before it, writing a due date into a note changed the text and nothing else, so the
/// user had no way to learn whether the board had registered the date at all (invariant 3 of
/// docs/note-lifecycle.md).
/// </para>
/// <para>
/// It used to sit permanently in the bottom-left corner as a row of outlined chips with counts. That
/// was removed: the card's own edge bar already says "this one has a date", so the second place it
/// was said was a data summary standing in for the interface, always on screen, whether or not
/// anyone was asking. Now the readings live behind one funnel next to the zoom buttons, and the only
/// thing that appears unasked is the banner — and that appears only while a lens is actually on,
/// which is the state a board cannot show by itself.
/// </para>
/// <para>
/// The rows filter, they do not hide: a filtered-out note goes to 22% opacity and stays exactly where
/// it was, because moving notes to satisfy a lens would make the document look edited and would put
/// a step in the undo stack that the user never asked for.
/// </para>
/// </remarks>
public sealed partial class MainWindow
{
    /// <summary>
    /// A note made by a double-tap, until it earns text. Kept with the other view-only state:
    /// nothing about it belongs in the document.
    /// </summary>
    private string? _provisionalId;

    private bool _filterPanelOpen;

    /// <summary>横幅那一行：建窗时搭好的菜单行，之后只换字。</summary>
    private PanelKit.SheetRow _bannerRow = null!;

    /// <summary>
    /// The property lines the editor is not showing: what was on the note's mark lines when editing
    /// began, plus anything absorbed while typing. The buffer the user sees is the body alone, and the
    /// text written back to the file is both — which is what makes "回车以后它从文本栏消失、变成左下角
    /// 那枚牌子" true without the mark leaving the document.
    /// </summary>
    private string _editorProps = string.Empty;

    /// <summary>
    /// Enter on a line that is nothing but mark tokens absorbs that line into
    /// <see cref="_editorProps"/> and takes it out of the buffer — so the token disappears where it
    /// was typed and shows up as a chip. Any other Enter is a normal newline and is left alone.
    /// </summary>
    private bool AbsorbPropertyLine()
    {
        if (_editor is not { } editor)
        {
            return false;
        }

        var text = editor.Text;
        var caret = Math.Min(editor.SelectionStart, text.Length);
        var start = text.LastIndexOfAny(['\n', '\r'], Math.Max(0, caret - 1)) + 1;
        var end = start;

        while (end < text.Length && text[end] != '\n' && text[end] != '\r')
        {
            end++;
        }

        var line = text[start..end].Trim();

        if (!Tags.LooksLikePropertyLine(line))
        {
            return false;
        }

        _editorProps = _editorProps.Length == 0 ? line : _editorProps + "\n" + line;

        // 连着它后面那个换行一起吃掉，否则原地留一个空行。
        var stop = end < text.Length && text[end] == '\r' ? end + 1 : end;

        if (stop < text.Length && text[stop] == '\n')
        {
            stop++;
        }

        editor.Text = text[..start] + text[stop..];
        editor.Select(start, 0);

        PaintEditorChips();

        Say($"收到了 {line}");

        return true;
    }

    private void OnFilterClicked(object sender, RoutedEventArgs e)
    {
        SetFilterPanel(!_filterPanelOpen);
    }

    private void SetFilterPanel(bool open)
    {
        _filterPanelOpen = open;
        FilterPanel.Visibility = open ? Visibility.Visible : Visibility.Collapsed;

        if (open)
        {
            WriteFilterRows();
        }
    }

    private void CloseFilterPanel()
    {
        if (_filterPanelOpen)
        {
            SetFilterPanel(false);
        }
    }

    /// <summary>
    /// A press on the panel's own chrome — between the rows — means "this layer is finished with":
    /// it closes and does not reach the board underneath, so it neither deselects nor starts a pan.
    /// A press on a row never comes here; the button marks it handled first, which is why clicking a
    /// row leaves the panel open for the next choice.
    /// </summary>
    private void OnFilterPanelPressed(object sender, PointerRoutedEventArgs e)
    {
        e.Handled = true;

        CloseFilterPanel();
    }

    /// <summary>
    /// Refreshes the panel's rows and the banner. Called from the same rebuild the cards are painted
    /// in, so the counts cannot be a snapshot from whenever the board was opened.
    /// </summary>
    private void RebuildFilterPanel()
    {
        // The panel's brushes come from the theme pass; a rebuild can run before ArrangeAll does it.
        ChromeStyling.Ensure(NoteStyling.IsDark);
        ChromeStyling.ApplyFloatingChrome(FilterPanel);
        ChromeStyling.ApplyFloatingChrome(FilterBanner);

        if (_filterPanelOpen)
        {
            WriteFilterRows();
        }

        UpdateFilterBanner();
    }

    /// <summary>
    /// The panel's rows, written from the readings of this frame. The surface itself is the right-click
    /// menu's control — see <see cref="PanelKit"/> — so "筛选" and "日期" are disabled rows, not a second
    /// type of line, and the counts sit in the menu's own hint column.
    /// </summary>
    private void WriteFilterRows()
    {
        var rows = new List<PanelKit.SheetRow> { PanelKit.Line("日期") };

        var notes = _document.Nodes.Where(node => node.Kind != CanvasNodeKind.Group).ToList();
        var states = notes.Select(node => NoteMarks.StateOf(node.Text, _view.Today)).ToList();

        var dated = states.Count(state => state != NoteMarks.DueState.None);

        if (dated == 0)
        {
            rows.Add(PanelKit.Line("板上还没有日期"));
        }
        else
        {
            foreach (var state in new[] { NoteMarks.DueState.Late, NoteMarks.DueState.Today, NoteMarks.DueState.Upcoming, NoteMarks.DueState.Malformed })
            {
                var count = states.Count(reading => reading == state);

                // 0 的档不占一行——面板不是统计报表；但正被这一档筛着时必须留着，否则"清除"这一格消失了。
                if (count > 0 || _view.DueFilter == state)
                {
                    var wanted = state;

                    rows.Add(PanelKit.Row(
                        $"{(NoteIsFiltered(state) ? "✓ " : string.Empty)}{DueLabel(wanted)}",
                        count.ToString(),
                        () => SetDueFilter(wanted)));
                }
            }
        }

        var tally = NoteMarks.Tally(notes.Select(node => node.Text).ToList());

        rows.Add(PanelKit.Line("标签"));

        if (tally.Count == 0)
        {
            rows.Add(PanelKit.Line("板上还没有标签"));
        }

        foreach (var (tag, count) in tally)
        {
            var wanted = tag;
            var on = string.Equals(_view.TagFilter, wanted, StringComparison.Ordinal);

            rows.Add(PanelKit.Row($"{(on ? "✓ " : string.Empty)}{wanted}", count.ToString(), () => SetTagFilter(wanted)));
        }

        if (_view.Filtering)
        {
            // 面板里筛上的，就得在面板里能解开。原来只有板顶那条横幅能清，
            // 而横幅在面板打开时被面板自己压住了一半。
            rows.Add(PanelKit.Row("清除筛选", "全部", ClearFilters));
        }

        FilterScroll.Content = PanelKit.Host(rows.ToArray());
    }

    /// <summary>
    /// The due states in the words the board uses everywhere else — the enum names are English and
    /// would read as a bug report on a Chinese surface.
    /// </summary>
    private static string DueLabel(NoteMarks.DueState state) => state switch
    {
        NoteMarks.DueState.Late => "逾期",
        NoteMarks.DueState.Today => "今天",
        NoteMarks.DueState.Upcoming => "以后",
        NoteMarks.DueState.Malformed => "日期读不出",
        _ => "无日期",
    };

    private bool NoteIsFiltered(NoteMarks.DueState state) => _view.DueFilter == state;

    /// <summary>
    /// The narrow banner that only exists while a lens is on: what is being hidden, how much of the
    /// board survives it, and one click back to everything.
    /// </summary>
    private void UpdateFilterBanner()
    {
        if (!_view.Filtering)
        {
            FilterBanner.Visibility = Visibility.Collapsed;

            return;
        }

        var notes = _document.Nodes.Where(node => node.Kind != CanvasNodeKind.Group).ToList();

        var shown = notes.Count(node => NoteMarks.Shows(
            NoteMarks.StateOf(node.Text, _view.Today),
            Tags.Of(node.Text),
            _view.TagFilter,
            _view.DueFilter));

        // 内插里不放条件式：{ } 是属性模式的记号，编译器会先把它当成插补的结束符。
        var what = _view.TagFilter is { Length: > 0 } tag
            ? $"只看 {tag}"
            : _view.DueFilter is { } state
                ? $"只看{DueLabel(state)}的格子"
                : "没有筛选";

        // 折进组里的那几格必须有一句，否则筛选看起来像删了便签。框上那行标签会被卡片盖住
        // （框贴着成员收，没给标题留边），所以这句话由这条一直在屏幕上的横幅来说。
        var folded = FoldedAwayCount;

        _bannerRow.Text = $"{what} · {notes.Count} 格中的 {shown} 格"
            + (folded > 0 ? $" · {folded} 格折进了组" : string.Empty)
            + " · 点这清除";
        FilterBanner.Visibility = Visibility.Visible;
    }

    /// <summary>
    /// A chip on a card was clicked: take that span out of the note's text. This is a document edit
    /// (one undoable step, saved), not a view state — the mark was written into the text, so removing
    /// it has to go back through the same door.
    /// </summary>
    private void RemoveMarkFrom(string nodeId, string token)
    {
        if (NodeOf(nodeId) is not { } node || node.Kind != CanvasNodeKind.Text)
        {
            return;
        }

        var without = Tags.Without(node.Text, token);

        if (string.Equals(without, node.Text, StringComparison.Ordinal))
        {
            // 删不掉却说删了，比不删更糟。
            Say($"没在正文里找到「{token}」，所以什么都没动");
            return;
        }

        BeginGesture("去掉一枚标记");

        node.Text = without;
        FitNoteHeight(node);

        EndGesture();
        Rebuild();
        SaveSoon();

        Say($"去掉了 {token} · Ctrl＋Z 放回去");
    }

    /// <summary>
    /// The chips inside the editor. He asked for this specifically: pressing Enter on a tag must make
    /// it leave the text field <b>then</b>, not after the edit finishes — so the strip that will end up
    /// on the card is shown in the editor too, and deleting one there edits the same pending block.
    /// </summary>
    private StackPanel? _editorChips;

    private void PaintEditorChips()
    {
        if (_editorChips is null || _editor is null)
        {
            return;
        }

        var marks = Tags.Read(_editorProps);
        var size = NoteType.ChipFont(_view.Zoom);

        _editorChips.Children.Clear();

        foreach (var chip in NoteMarks.Chips(marks, _view.Today))
        {
            var source = chip.Source;

            _editorChips.Children.Add(ChromeStyling.Chip(chip, size, NoteStyling.InkFor(null, NoteStyling.SurfaceFor(null)), () =>
            {
                _editorProps = Tags.Without(_editorProps, source);

                PaintEditorChips();
            }));
        }

        _editorChips.Visibility = _editorChips.Children.Count > 0 ? Visibility.Visible : Visibility.Collapsed;

        // 贴在编辑器左下角：与卡片上那排牌子的位置是同一个地方，看着才是同一件事。
        var pad = Math.Clamp(NoteTypography.For(_view.FontSizeFor(_editingId ?? string.Empty)).Padding * _view.Zoom, 0, 60);

        Canvas.SetLeft(_editorChips, Canvas.GetLeft(_editor) + pad);
        Canvas.SetTop(_editorChips, Canvas.GetTop(_editor) + _editor.Height - pad - Math.Max(16, size * 1.6));
    }

    private void ClearFilters()
    {
        _view.TagFilter = null;
        _view.DueFilter = null;

        Rebuild();

        Say("筛选已清除，所有格子回到原本的亮度");
    }

    private void SetTagFilter(string? tag)
    {
        _view.TagFilter = string.Equals(_view.TagFilter, tag, StringComparison.Ordinal) ? null : tag;

        Rebuild();

        // 这两句走 Say() 而不是直接写字：回执那一枚只在 Say() 里浮出来，
        // 上一版直接改文字＝写进一块看不见的板子里。
        Say(_view.TagFilter is { } active
            ? $"只看 {active} 的格子（再点一次取消）"
            : "标签筛选已取消");
    }

    private void SetDueFilter(NoteMarks.DueState state)
    {
        _view.DueFilter = _view.DueFilter == state ? null : state;

        Rebuild();

        Say(_view.DueFilter is { } active
            ? $"只看{DueLabel(active)}的格子（再点一次取消）"
            : "到期筛选已取消");
    }
}
