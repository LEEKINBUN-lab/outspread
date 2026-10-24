using Outspread.Core;
using Xunit;

namespace Outspread.Core.Tests;

/// <summary>
/// The floating-panel tokens: the one place a panel's corner number and its translucency are written.
/// </summary>
/// <remarks>
/// There is no second copy of these values in the interface — <c>ChromeStyling</c> reads them — so a
/// test here is a test on what gets painted. The point of the class is his rule "别拿常数糊": the
/// objection was never to 8, it was to eight written four times.
/// </remarks>
public class ChromeTests
{
    [Fact]
    public void A_panel_is_rounded_while_a_card_is_square()
    {
        // 直角那条规矩的对象是纸。浮层不是纸，它不圆就是不贴合界面上的那一层玻璃。
        Assert.True(Chrome.Radius > 0);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void A_panel_is_translucent_but_still_readably_dense(bool dark)
    {
        Assert.True(Chrome.PanelAlpha is >= 0.85 and < 1, "一档半透明：挡得住色，看不出挡住");
        Assert.True(Chrome.PanelLineAlpha(dark) is > 0 and <= 0.5);

        // 一条线在暗档必须比在浅档更亮才看得见（板底本身反了个向）。
        Assert.True(Chrome.PanelLineAlpha(dark: true) > Chrome.PanelLineAlpha(dark: false));
        Assert.NotEqual(Chrome.PanelLineColour(dark: true), Chrome.PanelLineColour(dark: false));
    }

    [Fact]
    public void The_panel_fill_is_the_prototype_panel_not_the_board_floor()
    {
        // 浮层要有自己的地面：第一版把填充写成板底，于是面板和它浮在上面的东西同色。
        // 原型分得开——--panel 是白（深档 #1b1e22），板底是 249,250,251。
        Assert.Equal(Badge.PanelColour(dark: false), Chrome.PanelFill(dark: false));
        Assert.Equal(Badge.PanelColour(dark: true), Chrome.PanelFill(dark: true));
        Assert.NotEqual(BoardGrid.BoardBackground(dark: false), Chrome.PanelFill(dark: false));

        // 两档里面板都比板底更亮：浅档是"白纸抬在灰板上"，深档是"板底更黑、控件面抬起"。
        // （第一版我把这条写成了"深档要更暗"，那是把"抬起"和"压暗"搞混了——测试当场拒了。）
        Assert.True(Chrome.PanelFill(dark: false).B > BoardGrid.BoardBackground(dark: false).B);
        Assert.True(Chrome.PanelFill(dark: true).B > BoardGrid.BoardBackground(dark: true).B);
    }

    [Fact]
    public void Every_badge_member_is_a_target_and_the_cluster_is_the_prototype_s_shape()
    {
        // WCAG 2.5.8 的 24×24：一枚成员是 13px 字站在 20px 行上、上下各 7px 内距。
        Assert.True(Badge.LineHeight + (2 * Badge.MemberPaddingY) >= 24);

        // 键帽那一族（原来的 Badge.Cap*）随"只用灰字"那一轮删过，同日他又点名把键帽要回来，
        // 现在住在 Sheet.Key*（下面那项钉着它）。
        // 圆角（10-12 他点名把这一组改成圆的）：外壳用浮层那一枚半径；成员收一档，
        // 高亮块贴着内缘走才不会露出方角。界面侧 StyleBadge 就是照这两个数写的。
        Assert.Equal(Chrome.Radius - Badge.ContainerPaddingPx, Badge.MemberRadiusPx);
        Assert.True(Badge.MemberRadiusPx > 0, "成员还是方的，浮层就白改了");
        Assert.True(Badge.EdgeInsetPx > 0 && Badge.EdgeInsetPx <= 16);

        // 深浅两档的"线"必须各自比自己的面板可见：浅档黑 12%、深档白 16%。
        Assert.True(Badge.LineAlpha(dark: false) < Badge.LineAlpha(dark: true));
        Assert.NotEqual(Badge.PanelColour(dark: false), Badge.PanelColour(dark: true));
        Assert.True(Badge.Muted(dark: false).R < Badge.PanelColour(dark: false).R);
        Assert.True(Badge.Muted(dark: true).R > Badge.PanelColour(dark: true).R);
    }

    [Fact]
    public void A_sheet_carries_the_menu_s_air_and_is_lifted_off_the_board()
    {
        // 基准是他点头的那枚右键菜单：内空 167 装得下五行 32（4 + 160 + 4），字落在离边 16 的地方
        // （截图现量：边在 x=10、字的左缘在 x=26）。上一版三块浮层各写一份内缩——清单 16+12、
        // 筛选 0+12、菜单 4+12——同一个数三种答案，就是他说的"这里一个样子那里一个样"。
        Assert.Equal(4, Sheet.OuterPx);
        Assert.Equal(16, Sheet.OuterPx + Sheet.PadX);

        // 抬起：ThemeShadow 只有这一个旋钮，0 就是不浮。
        Assert.True(Lift.ZPx > 0, "浮层没有高度就还是贴在板子上的一块白");

        // 三种墨，全是现读来的，没有一种是"觉得该是这个色"：
        // 正文＝他那张菜单截图里"在这里新建一格"的最暗点，也是在真 MenuFlyoutItem 上量到的同一发；
        // 右列＝同一张图里"双击空白"的最暗点；静默行＝平台自己禁用档的墨（从一枚落地的禁用行上量的）。
        var ink = Sheet.Ink(dark: false);
        var hint = Sheet.HintInk(dark: false);
        var quiet = Sheet.Quiet(dark: false);

        Assert.Equal(27, ink.R);
        Assert.Equal(96, hint.R);
        Assert.Equal(161, quiet.R);

        // 三种墨必须是三个不同的档，撞成一份就等于没有分节。
        Assert.True(ink.R < hint.R && hint.R < quiet.R, "正文要比右列实，右列要比静默行实");
        Assert.Equal(12, Sheet.HintPx);

        // 键帽（10-23 他在原型里重新点的纸键帽那一版）：24 高、7/1 内距、圆角 5，且必须矮过
        // 它站着的带，否则会把行撑开——那是"两种线"的另一条来路。
        Assert.Equal(24, Sheet.KeyHeight);
        Assert.Equal(5, Sheet.KeyRadius);
        Assert.Equal(7, Sheet.KeyPadX);
        Assert.Equal(12, Sheet.KeyFont);
        Assert.True(Sheet.KeyMinWidth + (2 * Sheet.KeyPadX) >= 24, "一枚键也得按得到");
        Assert.True(Sheet.KeyHeight < Sheet.RowPx, "键帽不能比它站的带还高");

        // 键位清单是页面，不是菜单：760 宽、内缩 16、栏距 56、圆角 12；菜单仍是 4/8。
        Assert.Equal(760, Sheet.PageWidthPx);
        Assert.Equal(16, Sheet.PagePadPx);
        Assert.Equal(56, Sheet.PageGutterPx);
        Assert.Equal(12, Sheet.PageRadiusPx);
        Assert.True(Sheet.PageRadiusPx > Chrome.Radius, "页面比菜单更圆，这是 10-23 定下的一档");
        Assert.True(Sheet.PagePadPx > Sheet.OuterPx, "页面的边必须比菜单大，边距窄就是被这一条挤出来的");
    }

    [Fact]
    public void The_tab_strip_is_browser_shaped_and_the_active_tab_is_the_board()
    {
        // 2026-10-22：他点名的四格里，"难区分"与"加号位置"不是口味，是两条结构判据。
        // 活动标签的填充必须等于板底——这是标签与它下面那张纸连成一片的唯一来由；
        // 条底则必须与板底分开，否则活动标签再白也只是白上白。
        foreach (var dark in new[] { false, true })
        {
            Assert.Equal(BoardGrid.BoardBackground(dark), TabStrip.ActiveFill(dark));
            Assert.NotEqual(BoardGrid.BoardBackground(dark), TabStrip.StripBackground(dark));
            Assert.NotEqual(TabStrip.InactiveInk(dark), TabStrip.ActiveInk(dark));
        }

        // 上沿圆角只圆这一半；下沿不圆，因为它要和板面接上。
        Assert.Equal(8, TabStrip.TopRadius);
        Assert.Equal(38, TabStrip.StripHeight);
        Assert.Equal(34, TabStrip.TabHeight);
        Assert.True(TabStrip.StripHeight > TabStrip.TabHeight, "标签条要比标签高，上沿才有那 4 像素的空气");

        // 可访问下限：关闭键的视觉是 20，命中区取 24；加号本身 30。
        Assert.Equal(20, TabStrip.ClosePx);
        Assert.True(TabStrip.CloseHitPx >= 24, "关闭键的命中区不能低于 24");
        Assert.Equal(30, TabStrip.AddPx);
        Assert.True(TabStrip.AddPx >= 24, "加号也要按得到");

        // 10-23 他看原型选的 Chrome 式：× 的悬停底是正圆（半径＝半高），加号也是圆底。
        Assert.Equal(TabStrip.CloseHitPx / 2, TabStrip.CloseRadius);
        Assert.Equal(TabStrip.AddPx / 2, TabStrip.AddRadius);
        Assert.Equal(12, TabStrip.AddGlyphPx);
        Assert.Equal(30, TabStrip.SettingsPx);
        Assert.Equal(6, TabStrip.SettingsRadius);

        // × 的那一格由表头文字自己的右边距留（CloseHitPx），所以"有 ×／没 ×"两档内缩不再分开——
        // 两档一旦不等，模板换状态时标签宽度就跟着鼠标变。这一条由界面自检拿长名字正对照量
        // （`selftest 标签宽度`：×显 与 ×藏 两个宽度必须相等，且长名必须越过 82 的下限），
        // 常量表在这里量不到，故不再在此断言。

        // 悬停要有一档可看的差：非活动墨在浅底上要暗于条底，深底上要亮于条底。
        Assert.True(TabStrip.InactiveInk(false).R < TabStrip.StripBackground(false).R);
        Assert.True(TabStrip.InactiveInk(true).R > TabStrip.StripBackground(true).R);
    }

    [Fact]
    public void The_chip_is_chrome_measured_in_screen_pixels_not_paper_type()
    {
        // 12 / 22 是从原型的四版样张上量的（四版全是 font-size:12、高 22-24）。
        Assert.Equal(12, NoteType.ChipFontPx);
        Assert.Equal(22, NoteType.ChipBandPx);

        // 形状（10-12 他打回「一个方框看起来很廉价」之后按调研重定）：
        // 半径＝半高才是真胶囊（Primer 2em、Carbon 16px 都是这个意思），直角一家都没有；
        // 底色压在 18% 以下（便签自己是淡彩，撞色是这次调研点名的头号风险）；
        // 日期那一枚 resting 时不画底（0），标签画——两种容器，同一带高同一字号。
        Assert.Equal(NoteType.ChipBandPx / 2, NoteType.ChipRadiusPx);
        Assert.True(NoteType.ChipFillAlpha > 0 && NoteType.ChipFillAlpha <= 0.18);
        Assert.True(NoteType.ChipHoverAlpha > NoteType.ChipFillAlpha, "悬停必须比静置实，不然点不动的错觉就回来了");
        Assert.True(NoteType.ChipDotPx > 0 && NoteType.ChipDotPx < NoteType.ChipBandPx);

        // 放大时长，缩小不缩：缩到 40% 还跟着走的牌子就读不出来了，读不出来就等于没有。
        Assert.Equal(12, NoteType.ChipFont(0.4));
        Assert.Equal(22, NoteType.ChipBand(0.4));
        Assert.Equal(24, NoteType.ChipFont(2));
        Assert.Equal(44, NoteType.ChipBand(2));

        // 留出的一条带必须装得下牌子本身，否则最后一行字被切。
        Assert.True(NoteType.ChipBandPx > NoteType.ChipFontPx);

        foreach (var size in new[] { 14d, 15d, 16d })
        {
            Assert.True(NoteType.ChipFontPx < NoteTypography.For(size).Size, "牌子不能比正文还大声");
        }
    }
}
