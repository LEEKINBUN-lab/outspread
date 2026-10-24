namespace Outspread.Core;

/// <summary>
/// 编辑中那一发滚轮该把画面搬多少屏幕像素。
/// </summary>
/// <remarks>
/// 为什么单独成一个函数：这一发的意思从"缩放整块板子"改成"把正在写的那一格滚出来"，
/// 判据是纯算术（框在哪、视口多高、轮子往哪边转），可它决定的是**会不会有字看不见**。
/// 2026-10-24 他点的是"编辑时滚轮不要缩放，去把正在编辑的内容滚到看得见（超出显示范围的话）"
/// ——所以这里同时钉住另一半：已经全部看得见的那一发返回 0，界面要明着说一句，
/// 不许悄悄什么都不做（同一条规矩在 docs/interaction-machine.md 第四节第 4 条）。
/// </remarks>
public static class EditorScroll
{
    /// <param name="editorTop">编辑框在板面上的顶边（屏幕像素，可以为负＝探出上沿）。</param>
    /// <param name="editorHeight">编辑框现在多高（屏幕像素）。</param>
    /// <param name="viewportHeight">板面视口多高（屏幕像素）。</param>
    /// <param name="delta">滚轮那一格：负＝往下滚（要看下面的内容），正＝往上滚。</param>
    /// <param name="pixelsPerNotch">一格滚轮最多搬多少屏幕像素——上限在界面那侧，
    /// 因为"一格走多远"是手感，不是几何。</param>
    /// <returns>要加到相机 <c>PanY</c> 上的位移（屏幕像素）；0＝这一发没地方可滚。</returns>
    public static double Reveal(
        double editorTop,
        double editorHeight,
        double viewportHeight,
        int delta,
        double pixelsPerNotch)
    {
        if (delta == 0 || editorHeight <= 0 || viewportHeight <= 0 || pixelsPerNotch <= 0)
        {
            return 0;
        }

        // 探出上沿的那一截：把画面往下搬才看得见；探出下沿的那一截：往上搬。
        var above = Math.Max(0, -editorTop);
        var below = Math.Max(0, editorTop + editorHeight - viewportHeight);

        // 往下滚＝要看下面＝画面往上走（PanY 减小）；往上滚相反。
        var wanted = delta < 0 ? -below : above;

        return Math.Sign(wanted) * Math.Min(Math.Abs(wanted), pixelsPerNotch);
    }
}
