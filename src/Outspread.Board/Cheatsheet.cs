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
/// The cost of taking Tab is named: Tab is also how a keyboard user walks the focus ring, and that
/// ring is what he called "通用的找按钮" on 2026-10-23 — so the ring is gone on purpose. Every Tab
/// press is swallowed here: the board answers it with this sheet, and inside the editor, the rename
/// box or a floating panel it answers nothing at all.
/// </para>
/// </remarks>
public sealed partial class MainWindow
{
    private bool _cheatsheetOpen;

    private void OnCheatPreviewKeyDown(object sender, KeyRoutedEventArgs e)
    {
        // 改键页在等新键位时，这一发键归它：先于下面每一条（包括 Tab 与编辑中那一发回车），
        // 否则"按 Esc 取消"会先把选中清掉，按 Tab 会先弹清单。
        if (TryTakeChordCapture(e))
        {
            e.Handled = true;

            return;
        }

        // 编辑中的一发回车只能在这一层接。10-24 查"Ctrl+Enter 不收笔"量的：同一发键在 preview
        // 记到了、在编辑框自己的 KeyDown 上一次都没到（换行照样插进去了）——所以把提交挂在控件上
        // 是死的，中文输入法在场时他按多少次都不收笔。"回车收下标记行"那条也是同一条路。
        if (e.Key == Windows.System.VirtualKey.Enter && _editingId is not null)
        {
            e.Handled = HandleEditorEnter();

            return;
        }

        // Tab 归板子的条件是"现在没有在写字、也没有面板在收键"，不是"焦点恰好在 BoardArea"。
        // 10-23 他报"第一下 Tab 没反应、要按第二下"：第一下被当成了焦点导航，焦点落到板面，
        // 第二下才满足旧守卫。现在那第一下就把焦点交给板面并直接开清单——在标签条/按钮上按
        // Tab 也是同一个意思。同一批改动里他还点名"找按钮"那一下：所以不归板子的 Tab 也一律
        // 截住（见下），不再让平台把焦点挪到别的控件上。
        if (e.Key != Windows.System.VirtualKey.Tab)
        {
            // 板子的组合键要先于焦点控件拿到这一发：Alt＋方向、Ctrl＋方向这类是很多控件的
            // 内建键（切焦点、翻页、前进后退），放到 KeyDown 就晚了——控件已经把事件吃掉了。
            // 在 preview 里命中就直接执行并标 Handled，平台/控件都不会再看见它。
            if (TryDispatchBoardChord(e))
            {
                e.Handled = true;
            }

            return;
        }

        // 无论这一发归不归板子，Tab 都不许再走到平台的焦点遍历上——他 10-23 点名的就是编辑框、
        // 改名框、浮层里那一下"找按钮"：焦点跳到别的控件，改名框跟着乱。截在这里，那些场合的
        // Tab 什么都不做；键位清单仍只在板子能接键的时候开。
        e.Handled = true;

        if (!BoardCanTakeBoardKeys())
        {
            return;
        }

        BoardArea.Focus(FocusState.Programmatic);
        ShowCheatsheet();
    }

    /// <summary>
    /// 组合键的提前派发（preview 那一层）：命中命令表里"键盘要执行"的那一条就当场跑掉。
    /// 返回 false＝这一发不是板子的键，交给后面的控件/系统。
    /// </summary>
    private bool TryDispatchBoardChord(KeyRoutedEventArgs e)
    {
        if (!BoardCanTakeBoardKeys())
        {
            return false;
        }

        var control = Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(Windows.System.VirtualKey.Control)
            .HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down);
        var alt = Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(Windows.System.VirtualKey.Menu)
            .HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down);
        var shift = Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(Windows.System.VirtualKey.Shift)
            .HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down);

        var chord = ChordFrom(e.Key, control, shift, alt);

        return TryRunCommandForChord(chord);
    }

    /// <summary>
    /// 这一发键能不能归板子：没有在编辑、没有浮层在收键、焦点不在文本框里。
    /// 焦点在标签条/按钮上也算——那是"手在板子上"，不是"在打字"。
    /// </summary>
    private bool BoardCanTakeBoardKeys()
    {
        if (_editingId is not null || _filterPanelOpen || _searchOpen || _restoreOpen || _cheatsheetOpen || _chordPageOpen
            || _renamingPath is not null)
        {
            return false;
        }

        var focused = Microsoft.UI.Xaml.Input.FocusManager.GetFocusedElement(BoardArea.XamlRoot) as DependencyObject;

        for (var node = focused; node is not null; node = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetParent(node))
        {
            if (node is TextBox)
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>那一枚修饰键此刻按没按下（读的是线程的键盘状态，不走事件）。</summary>
    private static bool KeyDownIs(Windows.System.VirtualKey key)
        => Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(key)
            .HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down);

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

        // 键位清单是"页面"，不是菜单：2026-10-23 他看过原型后选的宽裕两栏——内缩 16、圆角 12，
        // 菜单那五枚仍是 4/8。同一枚填充、同一层抬起，变的只是这张参考页该有多大的边。
        CheatPanel.Padding = new Thickness(Sheet.PagePadPx);
        CheatPanel.CornerRadius = new CornerRadius(Sheet.PageRadiusPx);

        // 分节字样删了（他 10-12 点名）："排布 / 组织 / 板子"是我给这张表起的分类名，
        // 不是他按下键时要找的东西。清单里只该有一种行：这一发按键干什么。
        // 清单只放"猜不到的键"：复制/粘贴、Esc、方向键微调、双击空白新建、关板子这几行
        // 他 10-24 点名为噪音（见 Command.Sheet）。菜单与键盘仍走同一张表。
        var rows = Sections().SelectMany(section => section)
            .Where(command => command.Sheet)
            .Select(command => PanelKit.Row(command.Label, ChordsText(command), keys: true))
            .ToList();

        // 两栏、栏内尽量均分：760 是一张页面而不是一条横幅，单栏 ≈336 让"名字｜键帽"各自有地方。
        // 窗口比 760 窄时整张页跟着收，两栏一起收，不把字裁在右缘外。
        const int columns = 2;
        const double pageMinWidth = 420;
        var perColumn = (rows.Count + columns - 1) / columns;

        var pageWidth = Math.Min(Sheet.PageWidthPx, Math.Max(pageMinWidth, BoardArea.ActualWidth - 48));
        var columnWidth = (pageWidth - 2 - (Sheet.PagePadPx * 2) - Sheet.PageGutterPx) / 2;

        CheatPanel.Width = pageWidth;

        // 窗口矮的时候让整张页跟着收，多出来的行由面板自己的 ScrollViewer 接——不能把页脚裁在屏外。
        CheatPanel.MaxHeight = Math.Max(240, Math.Min(660, BoardArea.ActualHeight - 48));

        // 每栏定宽：横排 StackPanel 在无限宽测量下会让每栏按内容各自称重，两栏就不等宽了。
        // 行本身是 ContentControl（HorizontalContentAlignment=Stretch），给了栏宽就跟着长。
        var pair = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = Sheet.PageGutterPx,
        };

        for (var column = 0; column < columns; column++)
        {
            var slice = rows.Skip(column * perColumn).Take(perColumn).ToArray();

            if (slice.Length > 0)
            {
                var host = PanelKit.Host(slice);
                host.Width = columnWidth;
                pair.Children.Add(host);
            }
        }

        CheatScroll.Content = pair;

        Diagnostics.Write(
            $"键位清单: {rows.Count} 行 × {columns} 栏（每栏最多 {perColumn} 行）、页 {pageWidth:0.#} 宽、栏 {columnWidth:0.#} 宽、内缩 {Sheet.PagePadPx:0.#}、圆角 {Sheet.PageRadiusPx:0.#}");

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

        Say(string.Empty);
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
        BeginGesture("新建便签");

        var node = CreateTextNote(string.Empty, FreeOfGroups(new Point(_view.ToWorldX(screen.X), _view.ToWorldY(screen.Y))));

        EndGesture();

        _provisionalId = node.Id;
        BeginEdit(node.Id);
    }
}
