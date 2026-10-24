using Outspread.Core;

namespace Outspread.Core.Tests;

/// <summary>
/// 控件令牌里那三把"防晕染"的尺。已知答案先跑一遍再信它：
/// 96 DPI 下 2 必须还是 2，125% 下必须升到 3 个物理像素，小数笔画必须被判不 crisp。
/// </summary>
public sealed class ControlKitTests
{
    [Theory]
    [InlineData(1.0, 2.0)]        // 本机 AppliedDPI=96：2 逻辑＝2 物理，正好落整
    [InlineData(1.25, 2.4)]       // 125%：2×1.25=2.5 → 取整 3 物理像素 → 2.4 逻辑
    [InlineData(1.5, 2.0)]        // 150%：2×1.5=3，本来就整
    [InlineData(0.0, 2.0)]        // 拿不到缩放（还没上屏）时不许除零
    public void 笔画换算落整设备像素(double scale, double expected)
        => Assert.Equal(expected, ControlKit.CrispStroke(scale), 9);

    [Theory]
    [InlineData(1.0, 2.0, true)]
    [InlineData(1.0, 1.5, false)]   // 上一版缩完落屏 1.0，但 1.5 这种半像素笔画必然摊成两行
    [InlineData(1.25, 2.4, true)]
    [InlineData(1.25, 2.0, false)]  // 2 逻辑在 125% 上是 2.5 物理——看着就虚
    public void 判据认得出半像素笔画(double scale, double logical, bool expected)
        => Assert.Equal(expected, ControlKit.IsCrispStroke(scale, logical));

    [Theory]
    [InlineData(7.0, true)]
    [InlineData(7.5, true)]
    [InlineData(4.6666667, false)] // 24 网格的 7 缩到 16 之后就是这个数——晕染的坐标侧成因
    public void 坐标必须在半格网格上(double value, bool expected)
        => Assert.Equal(expected, ControlKit.OnPixelGrid(value));

    [Fact]
    public void 光学盒是画布的八成()
        => Assert.Equal(0.8, ControlKit.GlyphLivePx / ControlKit.GlyphCanvasPx, 9);

    /// <summary>静止不画常驻描边这一条是三家共同点，写死在令牌里而不是靠界面侧自觉。</summary>
    [Fact]
    public void 状态层一档比一档深()
    {
        Assert.True(ControlKit.HoverAlpha < ControlKit.PressAlpha);
        Assert.True(ControlKit.HoverInMs < ControlKit.HoverOutMs, "进得快、出得慢才读作\"有反馈\"而不是\"闪一下\"");
    }
}
