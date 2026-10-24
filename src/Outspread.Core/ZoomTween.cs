namespace Outspread.Core;

/// <summary>
/// 滚轮缩放的那一帧该走到哪：预览比例朝目标缓动，而不是一发跳一档。
/// </summary>
/// <remarks>
/// 2026-10-24 他要"缩放丝滑，别像忽然放大缩小"。旧的写法是每一发滚轮把合成缩放**立刻**
/// 推到 <c>×1.12</c>——快速滚就是一截一截的台阶。这里把它改成每帧朝目标走一段。
/// <para>
/// 为什么是指数平滑而不是原型那档 320ms 的 easeInOutCubic：滚轮的到达时刻是不定的，
/// 定长曲线要么每发重起一次动画（手快时永远在重起，看着更抖），要么排队（手停了还在动，
/// 像没跟上手）。指数平滑两样都躲开：目标一动就改道，停手后自己收敛。原型那条曲线是
/// "一次命令一次移动"的相机动画（点"看到全部"那一发），不是滚轮这一发——两件事别混。
/// </para>
/// </remarks>
public static class ZoomTween
{
    /// <summary>一帧吃掉剩余距离的比例。60fps 下约 6 帧走完 90%（≈100ms）。</summary>
    public const double PerFrame = 0.35;

    /// <summary>剩余不到目标的这个比例就当作到了：不吸附的话它会永远在逼近、永远不停帧。</summary>
    public const double SettleRatio = 0.0008;

    /// <summary>下一帧该显示的值：单调朝目标走，绝不过头。</summary>
    public static double Step(double current, double target, double perFrame = PerFrame)
    {
        if (current == target || perFrame <= 0)
        {
            return current;
        }

        var next = current + ((target - current) * Math.Clamp(perFrame, 0, 1));

        // 过了头（perFrame=1 时不会，但传进来的是别的数时会）就按目标收口。
        if ((target - current) * (target - next) < 0)
        {
            return target;
        }

        return Math.Abs(target - next) <= Math.Abs(target) * SettleRatio ? target : next;
    }
}
