using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Outspread.Core;
using Windows.Foundation;

namespace Outspread.Board;

/// <summary>
/// The cheat sheet: hold <c>Tab</c> and everything the board answers to appears, read off the same
/// table the menu is generated from.
/// </summary>
/// <remarks>
/// <para>
/// It is a <b>hold</b> because the question being asked is "what does this key do" — a panel you
/// have to enter and then remember to leave is a panel you get stuck in. Releasing Tab closes it,
/// and so does losing the window.
/// </para>
/// <para>
/// The cost of taking Tab is named: Tab is also how a keyboard user walks the focus ring. It is
/// therefore swallowed only while the board surface itself holds focus and no note is being edited
/// — inside the editor, and on the mark-rail chips, Tab still does what it always did.
/// </para>
/// </remarks>
public sealed partial class MainWindow
{
    private bool _cheatsheetOpen;

    /// <summary>空格是板子的键（按一下＝回到默认位置默认大小），不是"按下焦点所在的那枚按钮"。
    /// 上一版焦点停在右下角的缩小键上，一敲空格板子就缩——他这次点名的就是这一下。</summary>
    private bool _spaceTap;

    /// <summary>按住空格拖过＝这一发是平移，松手时不再复位。</summary>
    private bool _spacePanned;

    private void OnCheatPreviewKeyDown(object sender, KeyRoutedEventArgs e)
    {
        // 只在"板子自己持焦点、且没在编辑任何格子"时截 Tab：编辑里、面板的按钮上，
        // Tab 还得是 Tab。
        var boardHasFocus = ReferenceEquals(Microsoft.UI.Xaml.Input.FocusManager.GetFocusedElement(BoardArea.XamlRoot), BoardArea);

        if (e.Key == Windows.System.VirtualKey.Space && _editingId is null && BareSpace())
        {
            // 在 Tab 之前截：空格要抢在"焦点按钮被激活"前面。按住它平移仍然成立——那条路读的是
            // 键盘状态（GetKeyStateForCurrentThread），不走这枚事件。
            _spaceTap = true;
            _spacePanned = false;
            e.Handled = true;

            return;
        }

        if (e.Key != Windows.System.VirtualKey.Tab || _editingId is not null || !boardHasFocus)
        {
            return;
        }

        ShowCheatsheet();

        e.Handled = true;
    }

    /// <summary>空格只单独算一件事：不带 Ctrl/Alt/Shift 的那一发才是"复位视图"。</summary>
    private static bool BareSpace()
    {
        static bool Down(Windows.System.VirtualKey key) => Microsoft.UI.Input.InputKeyboardSource
            .GetKeyStateForCurrentThread(key).HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down);

        return !Down(Windows.System.VirtualKey.Control) && !Down(Windows.System.VirtualKey.Menu) && !Down(Windows.System.VirtualKey.Shift);
    }

    /// <summary>松手那一下才决定这是"按一下复位"还是"按住平移了一次"。</summary>
    private void OnSpacePreviewKeyUp(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key != Windows.System.VirtualKey.Space || !_spaceTap)
        {
            return;
        }

        _spaceTap = false;

        if (!_spacePanned && _editingId is null)
        {
            ResetView();
        }

        _spacePanned = false;
        e.Handled = true;
    }

    private void OnCheatKeyUp(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == Windows.System.VirtualKey.Tab)
        {
            CloseCheatsheet();
        }
    }

    private void ShowCheatsheet()
    {
        if (_cheatsheetOpen)
        {
            return;
        }

        ChromeStyling.Ensure(NoteStyling.IsDark);
        ChromeStyling.ApplyFloatingChrome(CheatPanel);

        // 顶部那行"键位"删了（他 10-12 点名）：按住 Tab 出来的东西不需要自报家门，
        // 而那一行还是整块清单里唯一一句不是命令的话，读起来像表头压着表头。
        var groups = Sections().ToList();

        // 每一节现在占一行标题加它的命令行，全是菜单那一枚控件的高度——所以"齐不齐"只由行数决定。
        var heights = groups.Select(section => (section.Count() + 1) * Sheet.RowPx).ToList();
        var total = heights.Sum();

        // 两栏，切在让两栏最齐的那道**分节边界**上：一栏到底是一张 23 行的清单，
        // 而按组轮流塞（上一版的作法）会让某一栏的收尾空半截——他截图里指的就是那个。
        // 分节永远不跨栏拆开：读到一半被换到右栏，等于没有分节。
        var split = 1;
        var best = double.MaxValue;
        var left = 0.0;

        for (var i = 0; i < groups.Count - 1; i++)
        {
            left += heights[i];

            var gap = Math.Abs(left - (total - left));

            if (gap < best - 0.001)
            {
                best = gap;
                split = i + 1;
            }
        }

        var columns = new[] { new List<PanelKit.SheetRow>(), new List<PanelKit.SheetRow>() };

        for (var i = 0; i < groups.Count; i++)
        {
            var column = columns[i < split ? 0 : 1];

            // 分节的标题就是菜单里那种"灰掉的行"：这一套语言里没有第二种线。
            column.Add(PanelKit.Line(groups[i].Key));

            foreach (var command in groups[i])
            {
                column.Add(PanelKit.Row(command.Label, command.Chord, keys: true));
            }
        }

        // 两栏各按自己的内容长：这里不能用星号列——presenter 的模板里有一枚横向禁滚的 ScrollViewer，
        // 它在"可用宽度由内容决定"的宿主里会退化成最小宽度（量到过：整块清单被压到 240 宽、
        // 右列的键位被挤出可视区）。横排 StackPanel 给的是无限宽测量，两栏才各自称出自己的体重。
        var pair = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 34,
        };

        pair.Children.Add(PanelKit.Host(columns[0].ToArray()));
        pair.Children.Add(PanelKit.Host(columns[1].ToArray()));

        CheatScroll.Content = pair;

        // 板底在浮层下面透出来一点：它盖住的那格字仍可辨，这是"悬浮"而不是"遮挡"的唯一可读凭据。
        CheatScrim.Visibility = Visibility.Visible;
        CheatPanel.Visibility = Visibility.Visible;
        _cheatsheetOpen = true;
    }

    private void CloseCheatsheet()
    {
        if (!_cheatsheetOpen)
        {
            return;
        }

        CheatPanel.Visibility = Visibility.Collapsed;
        CheatScrim.Visibility = Visibility.Collapsed;
        _cheatsheetOpen = false;
    }

    /// <summary>
    /// A press on the sheet's own chrome — the gap between rows, not a row — closes it and does not
    /// reach the board. It is in the board's tree now rather than in a <c>Popup</c>, so without this
    /// the sheet would sit on top of whatever note is under it and select that one on the way past.
    /// </summary>
    private void OnCheatPanelPressed(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
    {
        e.Handled = true;

        CloseCheatsheet();

        Say("键位清单收起了（按住 Tab 再看）");
    }

    /// <summary>
    /// A menu route to create a note. Before it the only way to make a card was to double-tap empty
    /// board, which is undiscoverable from the menu — and the menu is the accessibility surface
    /// while bare-key shortcuts stay off.
    /// </summary>
    private void NewNoteAtCentre() => NewNoteAt(new Point(BoardArea.ActualWidth / 2, BoardArea.ActualHeight / 2));

    /// <summary>
    /// A menu route to create a note, at wherever the menu was opened. Before it the only way to make
    /// a card was to double-tap empty board, which is undiscoverable from the menu — and the menu is
    /// the accessibility surface while bare-key shortcuts stay off.
    /// </summary>
    private void NewNoteAt(Point screen)
    {
        BeginGesture("新建一格");

        var node = CreateTextNote(string.Empty, new Point(_view.ToWorldX(screen.X), _view.ToWorldY(screen.Y)));

        EndGesture();

        _provisionalId = node.Id;
        BeginEdit(node.Id);
    }
}
