using System.Collections.Generic;

namespace Outspread.Core;

/// <summary>
/// 视口内"被上面那几格完全盖住"的判定：屏幕上一颗像素都轮不到它的便签，不必再栅格化它。
/// </summary>
/// <remarks>
/// 为什么要单独算而不是让合成器去做：WinUI 3 不做遮挡剔除（occlusion culling），
/// 一张纸面上的格子只要挂在树上，它底下的那几层就照旧被栅格化一遍、被混合一遍——
/// 那本账是每像素的，不是每对象的（10-28 实测：削掉 38% 的元素，画面"几乎没有变化"）。
/// 堆叠与成组恰恰是他日常最遮挡的用法（Alt+B 那一摞）。
///
/// 三条约束决定了这里只能保守：
/// ① 只有**不透明**的卡能当遮挡源——组框的纸面是 <c>Colors.Transparent</c>，压不住任何东西；
/// ② 被剔的那一格画的东西**不止自己的盒子**：下方的阴影带会伸出去（见
///   <see cref="NoteSurface.ShadowReachWorld"/>），所以判"被盖住"要比的是外扩后的那一圈；
/// ③ 摘掉的那一格自己也不再当遮挡源——它这一拍什么都不画，不能拿"看不见的东西"去判后面的可见。
///
/// 顺序是输入的一部分：调用方按绘制顺序（＝文档节点顺序＝<c>ItemLayer</c> 的子序）交进来，
/// 越靠后的越在上面。均匀缩放与平移都不改变格子之间的相对遮挡关系，所以这一发只在几何
/// 提交那一拍跑，不必每帧跑。
/// </remarks>
public static class Occlusion
{
    /// <param name="Id">这一格的节点号，原样带回，调用方按它找视觉。</param>
    /// <param name="Paper">纸面盒子（世界坐标）：这一格的实心矩形。接线方用它判 <c>CanCover</c>／给读数；
    /// 遮挡判定本身<b>不用</b>它（见 <see cref="Drawn"/> 那条）。</param>
    /// <param name="Drawn">这一格实际会画到的那一圈（世界坐标）＝盒子往下加阴影的外扩。
    /// 遮挡源与被剔者都比这一圈：上面那格把自己 footprint 里画的一切（含它自己的阴影带）
    /// 都盖在自己身下，同位叠摞才是"摘得掉"的形状——遮挡源只算纸盒的话，一摞便签一张都摘不动。</param>
    /// <param name="CanCover">这一格是不是一块实心的纸（组框、折走的、压暗的都算不是）。</param>
    /// <param name="CanCull">这一格允不允许被摘掉（选中/编辑/正拖着的都别摘）。</param>
    public readonly record struct Cell(string Id, Grouping.Rect Paper, Grouping.Rect Drawn, bool CanCover, bool CanCull);

    /// <summary>按绘制顺序交进来，返回这一拍该摘掉的那几格的号（按交进来的顺序，通常为空）。</summary>
    public static IReadOnlyList<string> CulledCells(IReadOnlyList<Cell> inDrawOrder)
    {
        if (inDrawOrder.Count == 0)
        {
            return [];
        }

        // 从最上面那一格往下走：走到哪一格，手上就攒着"压在它上面"的那几块画到的那一圈。
        var above = new List<Grouping.Rect>(inDrawOrder.Count);
        List<string>? culled = null;

        for (var index = inDrawOrder.Count - 1; index >= 0; index--)
        {
            var cell = inDrawOrder[index];

            if (cell.CanCull && above.Count > 0 && Covers(above, cell.Drawn))
            {
                (culled ??= []).Add(cell.Id);

                // 摘掉的那一格这一拍什么都不画，所以它不进遮挡源——即使它本来是一块实心纸。
                continue;
            }

            if (cell.CanCover)
            {
                above.Add(cell.Drawn);
            }
        }

        // 从顶往下收的，倒回文档顺序交出去：调用方按它找视觉，输入序才是它的习惯读法。
        culled?.Reverse();

        return culled ?? [];
    }

    /// <summary>
    /// 这些矩形（并集）有没有把 <paramref name="target"/> 盖严——一条缝都留不下。
    /// </summary>
    /// <remarks>
    /// 手法是按 x 切片：把 target 的横向切成"每个遮挡源的左右边"那几段，逐段把压在那一段上的
    /// 矩形纵向并一次区间，任何一段的并集没吃满 target 的纵向就判不成立。单矩形判"包含"是不对的：
    /// 一摞里压着下面那格的常常是上面那好几格各占一条，任何一格单独都盖不住。
    /// </remarks>
    public static bool Covers(IReadOnlyList<Grouping.Rect> covers, Grouping.Rect target)
    {
        // 先把横向真的相交的那几块挑出来：一张摊开的板子上，这一步通常一个都不剩。
        List<Grouping.Rect>? hitting = null;

        foreach (var rect in covers)
        {
            if (rect.X >= target.X + target.Width || rect.X + rect.Width <= target.X
                || rect.Y >= target.Y + target.Height || rect.Y + rect.Height <= target.Y)
            {
                continue;
            }

            (hitting ??= []).Add(rect);
        }

        if (hitting is null)
        {
            return false;
        }

        var left = target.X;
        var right = target.X + target.Width;
        var bottom = target.Y + target.Height;

        // 切片点＝target 的左右边加上每个遮挡源落在这段里的左右边。
        var cuts = new double[hitting.Count * 2 + 2];
        var taken = 0;

        foreach (var rect in hitting)
        {
            cuts[taken++] = Clamp(rect.X, left, right);
            cuts[taken++] = Clamp(rect.X + rect.Width, left, right);
        }

        cuts[taken++] = left;
        cuts[taken++] = right;
        System.Array.Sort(cuts, 0, taken);

        var bands = new (double Start, double End)[hitting.Count];

        for (var i = 0; i + 1 < taken; i++)
        {
            var x0 = cuts[i];
            var x1 = cuts[i + 1];

            if (x1 - x0 <= 0)
            {
                continue;
            }

            // 这一段 x 上，凡是横跨它的遮挡源，把它们的 y 区间并起来。
            var n = 0;

            foreach (var rect in hitting)
            {
                if (rect.X > x0 || rect.X + rect.Width < x1)
                {
                    continue;
                }

                bands[n++] = (System.Math.Max(rect.Y, target.Y), System.Math.Min(rect.Y + rect.Height, bottom));
            }

            if (!ReachesBottom(bands, n, target.Y, bottom))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>这些 y 区间的并集是否吃满 [top,bottom]——按起点排好再逐段接，接不上就有缝。</summary>
    private static bool ReachesBottom((double Start, double End)[] bands, int n, double top, double bottom)
    {
        if (n == 0)
        {
            return false;
        }

        // 逐段插入排序：n 是压在这一段 x 上的遮挡源数，堆叠那一摞也就几枚，不值得请快速排序。
        for (var i = 1; i < n; i++)
        {
            var key = bands[i];
            var j = i - 1;

            for (; j >= 0 && (bands[j].Start > key.Start || bands[j].Start == key.Start && bands[j].End > key.End); j--)
            {
                bands[j + 1] = bands[j];
            }

            bands[j + 1] = key;
        }

        var reached = top;

        for (var i = 0; i < n; i++)
        {
            if (bands[i].Start > reached)
            {
                return false;
            }

            if (bands[i].End > reached && (reached = bands[i].End) >= bottom)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// 这些矩形（并集）盖掉了 <paramref name="target"/> 多少面积——"看不见却照样栅格化"的那一块有多大。
    /// 只给读数用：判整格摘不摘要的是"有没有缝"（<see cref="Covers"/>），估省多少填充要的是面积。
    /// </summary>
    public static double CoveredArea(IReadOnlyList<Grouping.Rect> covers, Grouping.Rect target)
    {
        List<Grouping.Rect>? hitting = null;

        foreach (var rect in covers)
        {
            if (rect.X >= target.X + target.Width || rect.X + rect.Width <= target.X
                || rect.Y >= target.Y + target.Height || rect.Y + rect.Height <= target.Y)
            {
                continue;
            }

            (hitting ??= []).Add(rect);
        }

        if (hitting is null)
        {
            return 0;
        }

        var left = target.X;
        var right = target.X + target.Width;
        var bottom = target.Y + target.Height;
        var cuts = new double[hitting.Count * 2 + 2];
        var taken = 0;

        foreach (var rect in hitting)
        {
            cuts[taken++] = Clamp(rect.X, left, right);
            cuts[taken++] = Clamp(rect.X + rect.Width, left, right);
        }

        cuts[taken++] = left;
        cuts[taken++] = right;
        System.Array.Sort(cuts, 0, taken);

        var spans = new (double Start, double End)[hitting.Count];
        var covered = 0d;

        for (var i = 0; i + 1 < taken; i++)
        {
            var x0 = cuts[i];
            var x1 = cuts[i + 1];

            if (x1 - x0 <= 0)
            {
                continue;
            }

            var n = 0;

            foreach (var rect in hitting)
            {
                if (rect.X > x0 || rect.X + rect.Width < x1)
                {
                    continue;
                }

                spans[n++] = (Math.Max(rect.Y, target.Y), Math.Min(rect.Y + rect.Height, bottom));
            }

            if (n == 0)
            {
                continue;
            }

            // 逐段插入排序后求并的长度：不能"从顶往下接"——顶上露出一条、中间全盖住的那种，
            // 接着算会得出 0，而它恰恰是这一发要量的那一种。
            for (var k = 1; k < n; k++)
            {
                var key = spans[k];
                var j = k - 1;

                for (; j >= 0 && spans[j].Start > key.Start; j--)
                {
                    spans[j + 1] = spans[j];
                }

                spans[j + 1] = key;
            }

            var union = 0d;
            var openStart = spans[0].Start;
            var openEnd = spans[0].End;

            for (var k = 1; k < n; k++)
            {
                if (spans[k].Start > openEnd)
                {
                    union += openEnd - openStart;
                    openStart = spans[k].Start;
                    openEnd = spans[k].End;

                    continue;
                }

                if (spans[k].End > openEnd)
                {
                    openEnd = spans[k].End;
                }
            }

            covered += (x1 - x0) * (union + openEnd - openStart);
        }

        return Math.Min(covered, target.Width * target.Height);
    }

    private static double Clamp(double value, double lo, double hi)
        => value < lo ? lo : value > hi ? hi : value;
}
