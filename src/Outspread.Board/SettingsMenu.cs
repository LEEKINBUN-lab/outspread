using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;

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

        var pair = new MenuFlyoutItem { Text = "配对手机…", Icon = SettingsMenuIcon("\uE717") };
        pair.Click += (_, _) => OpenPairingPanel();
        menu.Items.Add(pair);

        menu.Items.Add(new MenuFlyoutSeparator());

        var restore = new MenuFlyoutItem { Text = "恢复…", Icon = SettingsMenuIcon("\uE81C") };
        restore.Click += (_, _) => OpenRestorePanel();
        menu.Items.Add(restore);

        var filter = new MenuFlyoutItem { Text = "筛选：日期与标签…", Icon = SettingsMenuIcon("\uE71C") };
        filter.Click += (_, _) => SetFilterPanel(true);
        menu.Items.Add(filter);

        return menu;
    }

    private static FontIcon SettingsMenuIcon(string glyph) => new()
    {
        Glyph = glyph,
        FontFamily = new FontFamily("Segoe MDL2 Assets"),
        FontSize = 16,
    };
}
