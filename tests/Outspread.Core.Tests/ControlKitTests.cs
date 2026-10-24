using Outspread.Core;

namespace Outspread.Core.Tests;

/// <summary>
/// 控件令牌里那两条牵动手感的守恒：状态层一档比一档深，进出时长不对称。
/// </summary>
/// <remarks>
/// 图形那几枚令牌（画布、笔画、像素网格）10-24 跟着自绘线稿一起撤了——他判
/// "现在的图标还不如我们修改图标集之前的版本"，图形退回原样，那几把尺就没有被测物了。
/// 撤掉的算术不留在这里当摆设。
/// </remarks>
public sealed class ControlKitTests
{
    [Fact]
    public void 状态层一档比一档深()
        => Assert.True(ControlKit.HoverAlpha < ControlKit.PressAlpha);

    [Fact]
    public void 进来快出去慢()
    {
        Assert.True(ControlKit.HoverInMs < ControlKit.HoverOutMs, "进得快、出得慢才读作\"有反馈\"而不是\"闪一下\"");
        Assert.True(ControlKit.PressMs <= ControlKit.HoverInMs, "按下必须不比悬停慢，否则点下去那一下没有着落");
    }

    /// <summary>圆钮本体至少要到 WCAG 2.2 AA 那扇 24×24 的门。</summary>
    [Fact]
    public void 命中区不小于二十四()
        => Assert.True(ControlKit.ButtonPx >= 24);
}
