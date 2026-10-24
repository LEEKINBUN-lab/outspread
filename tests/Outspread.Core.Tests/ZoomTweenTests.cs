namespace Outspread.Core.Tests;

/// <summary>
/// 滚轮缩放的那一帧：走多大步、什么时候算到了。
/// </summary>
/// <remarks>
/// 这一档钉的不是"好不好看"（那归他的眼睛），是三条会解释手感的硬事实：**不许 overshoot**、
/// **不许永远到不了**（不吸附就每帧都在逼近、每帧都在重画）、**手停了动画要在两帧内自己收口**。
/// </remarks>
public class ZoomTweenTests
{
    [Fact]
    public void The_first_frame_covers_a_third_of_what_is_left()
    {
        Assert.Equal(1.042, ZoomTween.Step(1, 1.12), 6);
    }

    [Fact]
    public void It_walks_to_the_target_without_ever_passing_it()
    {
        var current = 1d;

        for (var frame = 0; frame < 40; frame++)
        {
            var next = ZoomTween.Step(current, 2);

            Assert.True(next >= current, $"第 {frame} 帧倒退了：{current} → {next}");
            Assert.True(next <= 2, $"第 {frame} 帧冲过头：{next}");

            current = next;
        }

        Assert.Equal(2, current);
    }

    [Fact]
    public void Shrinking_behaves_the_same_way()
    {
        var current = 2d;

        for (var frame = 0; frame < 40; frame++)
        {
            var next = ZoomTween.Step(current, 0.2);

            Assert.True(next <= current && next >= 0.2, $"第 {frame} 帧不在两数之间：{current} → {next}");

            current = next;
        }

        Assert.Equal(0.2, current);
    }

    /// <summary>
    /// 24 帧＝400ms（60fps）。这条是"别拖手"的上限：超过它，停手以后画面还在自己走，
    /// 他读到的就是"不跟手"。真正用的那一发约 6 帧走完 90%。
    /// </summary>
    [Fact]
    public void It_settles_inside_24_frames()
    {
        var current = 1d;
        var frames = 0;

        while (current != 8 && frames < 60)
        {
            current = ZoomTween.Step(current, 8);
            frames++;
        }

        Assert.Equal(8, current);
        Assert.InRange(frames, 1, 24);
    }

    [Fact]
    public void Already_there_means_no_new_frame_at_all()
    {
        Assert.Equal(1.5, ZoomTween.Step(1.5, 1.5));
    }

    [Theory]
    [InlineData(0)]      // 每帧 0＝动画没开
    [InlineData(-1)]     // 负数不许倒着走
    public void A_broken_step_size_freezes_the_picture(double perFrame)
    {
        Assert.Equal(1, ZoomTween.Step(1, 1.12, perFrame));
    }

    [Fact]
    public void A_full_step_jumps_straight_to_the_target()
    {
        Assert.Equal(1.12, ZoomTween.Step(1, 1.12, perFrame: 1));
    }
}
