using System.Text.Json;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Outspread.Core;

namespace Outspread.Board;

/// <summary>
/// 恢复那一层：一行一个写前快照，点它就把板子换回那一版。
/// </summary>
/// <remarks>
/// <para>
/// 快照早在落盘——每次写之前，锁内、原子改名之前，都要被替换掉的那一份已经进了
/// <c>.outspread-history</c>。但"磁盘上有一份"和"出事的时候够得着"是两件事：
/// 没有这一层，它只在有人去开资源管理器时才是备份。
/// </para>
/// <para>
/// 点一行不做确认框。确认框是把"你想清楚了吗"交给一次点击来背，而这里的真凭据是
/// 恢复自己也会强制存一份当前状态——按错了还能再点回来，所以它不需要先问。
/// </para>
/// </remarks>
public sealed partial class MainWindow
{
    private bool _restoreOpen;

    private void SetRestorePanel(bool open)
    {
        if (open)
        {
            // 这一层是"这块板子的历史"，没有板子路径就没有历史可列——不说清就等于给了一个空面板。
            if (_boardPath is not { } path)
            {
                Say("还没有板子文件，没有可恢复的版本");

                return;
            }

            ChromeStyling.Ensure(NoteStyling.IsDark);
            ChromeStyling.ApplyFloatingChrome(RestorePanel);

            RebuildRestorePanel(path);
        }

        RestorePanel.Visibility = open ? Visibility.Visible : Visibility.Collapsed;

        _restoreOpen = open;
    }

    private void CloseRestorePanel()
    {
        if (!_restoreOpen)
        {
            return;
        }

        SetRestorePanel(false);

        BoardArea.Focus(FocusState.Programmatic);
    }

    private void OpenRestorePanel()
    {
        SetSearchPanel(false);
        SetRestorePanel(true);
    }

    private void OnRestorePanelPressed(object sender, PointerRoutedEventArgs e)
    {
        e.Handled = true;

        CloseRestorePanel();
    }

    private void RebuildRestorePanel(string path)
    {
        var entries = Snapshots.List(path);

        // 这一行是这层唯一的自述：它要说清这些时刻是什么、有多少，否则点开一层看不懂的
        // 时间戳比没有这一层更让人不敢按。
        var rows = new List<FrameworkElement>
        {
            PanelKit.Line(entries.Count == 0
                ? "还没有可恢复的版本"
                : $"本机快照 · {entries.Count} 版（点一行回到那一版）"),
        };

        foreach (var entry in entries)
        {
            var captured = entry;

            rows.Add(
                PanelKit.Row(
                    captured.TakenAt.ToLocalTime().ToString("MM-dd HH:mm"),
                    $"{captured.Bytes / 1024.0:0.#} KB",
                    () => RestoreFrom(path, captured)));
        }

        RestoreScroll.Content = PanelKit.Host(rows.ToArray());
    }

    /// <summary>
    /// 换回某一版：先把"现在"存进快照（强制，不受 5 分钟间隔闸管），再让那一份落盘，
    /// 然后把内存里的文档跟上新落盘的这一份。
    /// </summary>
    private void RestoreFrom(string path, Snapshots.Entry entry)
    {
        try
        {
            var text = Snapshots.Read(entry);

            CanvasStore.Restore(path, text);

            _document = CanvasDocument.Parse(text, path);
            _baseJson = text;

            Rebuild();
            SaveSoon();
            ProjectSelection();

            Say($"已恢复到 {entry.TakenAt.ToLocalTime():MM-dd HH:mm}（再恢复一次可回到刚才）");

            // 行现在会自己吃掉按下那一发（浮层不再在按下时收），所以"选完就收起"要这层自己说。
            CloseRestorePanel();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
                                   or InvalidOperationException or JsonException or CanvasDocumentException)
        {
            // 说清是读不回还是写不进：这两种情况下用户要做的下一件事完全不同。
            Say($"没能恢复：{ex.Message}");
            Diagnostics.Write($"restore failed for {entry.Path}: {ex}");
        }
    }
}
