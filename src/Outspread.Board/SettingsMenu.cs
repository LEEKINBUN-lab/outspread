using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;
using Outspread.Core;

namespace Outspread.Board;

/// <summary>
/// 标题条右端那枚 ⋮ 的菜单：整块板的动作（配对 / 恢复 / 筛选）都归这里，右键菜单只管板面上的东西。
/// </summary>
/// <remarks>
/// 2026-10-23 他看完原型定的一格：入口像浏览器一样放在标签条右端，菜单用原生 <c>MenuFlyout</c>
/// （和右键菜单同一件控件），只多给每行一枚 16px 图标。图标走 Segoe MDL2 的系统字形，
/// 不自己画路径——这三个符号是平台里现成的，画一遍只会多一份要维护的几何。
/// </remarks>
public sealed partial class MainWindow
{
    private void OpenSettingsMenu()
    {
        BuildSettingsMenu().ShowAt(
            SettingsButton,
            new FlyoutShowOptions { Placement = FlyoutPlacementMode.BottomEdgeAlignedRight });
    }

    /// <summary>
    /// 每次打开现建：菜单的条目是固定的三件，但主题/可用状态在换板之后仍要读当前这一拍。
    /// 自检也走这一条，它才能证明"配对/恢复/筛选真的搬过来了"而不是只搬了注释。
    /// </summary>
    private MenuFlyout BuildSettingsMenu()
    {
        var menu = new MenuFlyout();

        var pair = new MenuFlyoutItem { Text = "配对手机…", Icon = SettingsMenuIcon(FluentIcons.Phone) };
        pair.Click += (_, _) => OpenPairingPanel();
        menu.Items.Add(pair);

        menu.Items.Add(new MenuFlyoutSeparator());

        var restore = new MenuFlyoutItem { Text = "恢复…", Icon = SettingsMenuIcon(FluentIcons.History) };
        restore.Click += (_, _) => OpenRestorePanel();
        menu.Items.Add(restore);

        var filter = new MenuFlyoutItem { Text = "筛选：日期与标签…", Icon = SettingsMenuIcon(FluentIcons.Filter) };
        filter.Click += (_, _) =>
        {
            CloseChordPage();
            SetFilterPanel(true);
        };
        menu.Items.Add(filter);

        // 改键进设置，而不是进右键菜单：它改的是"这台机器上这套键位"，不是板面上的某个东西。
        // 他 10-27 因为 Alt+L 被系统抢走而要改一个键——那次得改代码重编，这条入口就是那么来的。
        var chords = new MenuFlyoutItem { Text = "键盘快捷键…", Icon = SettingsMenuIcon(FluentIcons.Keyboard) };
        chords.Click += (_, _) => OpenChordPage();
        menu.Items.Add(chords);

        menu.Items.Add(new MenuFlyoutSeparator());

        // 左侧操作条的三枚静态钮可以先关掉；排列/标签是条件钮，不走这一列。
        var rail = new MenuFlyoutSubItem { Text = "左侧按钮" };

        foreach (var (name, label) in new[] { ("group", "分组"), ("color", "换色"), ("delete", "删除") })
        {
            var toggle = new ToggleMenuFlyoutItem
            {
                Text = label,
                IsChecked = !_view.IsRailButtonHidden(name),
            };

            toggle.Click += (_, _) =>
            {
                _view.SetRailButtonHidden(name, !toggle.IsChecked);
                ApplyRailVisibility();
                SaveSoon();
            };

            rail.Items.Add(toggle);
        }

        menu.Items.Add(rail);

        var size = new MenuFlyoutSubItem { Text = "便签尺寸" };

        var autoSize = new ToggleMenuFlyoutItem
        {
            Text = "随文字（编辑时自动长高）",
            IsChecked = _view.NoteSize == NoteSizeMode.Automatic,
        };
        autoSize.Click += (_, _) => SetNoteSizeMode(NoteSizeMode.Automatic);
        size.Items.Add(autoSize);

        var fixedSize = new ToggleMenuFlyoutItem
        {
            Text = "固定（只由手柄改）",
            IsChecked = _view.NoteSize == NoteSizeMode.Fixed,
        };
        fixedSize.Click += (_, _) => SetNoteSizeMode(NoteSizeMode.Fixed);
        size.Items.Add(fixedSize);

        menu.Items.Add(size);

        return menu;
    }

    private static IconElement? SettingsMenuIcon(string path)
        // 官方路径画在 20×20 的格子里；给 16 的 PathIcon 会把右下切掉。
        => FluentIcons.Icon(path, 20, NoteStyling.ToColor(Badge.Muted(NoteStyling.IsDark)));

    /// <summary>「随文字 / 固定」：换档时只动版面尺寸，不写文档。</summary>
    private void SetNoteSizeMode(NoteSizeMode mode)
    {
        if (_view.NoteSize == mode)
        {
            return;
        }

        _view.NoteSize = mode;

        if (mode == NoteSizeMode.Automatic)
        {
            RefitAutoHeights();
        }

        Rebuild();
        SaveSoon();
    }
}
