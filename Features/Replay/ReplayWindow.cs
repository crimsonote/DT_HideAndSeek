using System;

namespace HideAndSeek.Features.Replay
{
    /// <summary>
    /// 【唯一的窗口计算】—— 全系统只有这一个地方把"事件时刻 + 前/后秒数"变成时间区间。
    ///
    /// 为什么值得单独抽一个类（这是现有实现最贵的一个教训）：
    ///   现有实现**两条路各算一次窗口**：
    ///     · 房主侧合成：【`clip.At` − Before, `clip.At` + After】，
    ///       而 `At = ClientTime + atOffset` —— **含**偏移
    ///     · 客户端磁带：【磁带锚点 − Before, 磁带锚点 + After】，
    ///       而锚点 = `NormalTimeEdit` 的时刻 = 事件发生那一刻 —— **不含**偏移
    ///   两者相差一个 `atOffset`。自爆幕的 `atOffset = CollarToDeadSec = 6`
    ///   ⇒ 客户端磁带路的窗口落在 `[t0−6, t0−4.5]`，是**自爆开始之前**的画面
    ///   ⇒ 实测症状："看不到项圈闪烁"（看到的是站着的人）。
    ///
    ///   ⇒ 重写只有一条规则：**窗口永远相对"事件时刻"**。
    ///     素材从哪来**不影响窗口** —— 它只影响"这个区间里能取到哪些帧"。
    ///
    /// 各幕的"事件时刻"统一口径：
    ///   · 拿刀 / 杀人 —— 那一刻本身
    ///   · 最后 / 巡礼 —— 结算被拦下的那一刻
    ///   · 自爆 —— `OnDeadCollarBomb` 那一刻（t0）；窗口右端靠 After 拉到 t0+6.5
    ///     （t0 是"开始自爆"，`PushAfter(6000)` 的 t0+6 才是真正死亡）
    ///   · 黑方收尾 —— 爆炸那一刻（t0+6），窗口靠 Before/After 平分在它两侧
    /// </summary>
    internal static class ReplayWindow
    {
        /// <summary>时间区间（房主时钟量纲）。</summary>
        internal struct Span
        {
            public float From;
            public float To;

            public float Length => To - From;

            /// <summary>是否是可用区间（长度 &gt; 0）。</summary>
            public bool IsValid => To > From;

            /// <summary>区间是否包含某时刻（**左闭右开**，与现有实现的窗口过滤一致）。</summary>
            public bool Contains(float t) => t >= From && t < To;

            public override string ToString() => $"[{From:F2},{To:F2}]";
        }

        /// <summary>
        /// 一段窗口 = <c>[事件时刻 − before, 事件时刻 + after]</c>。
        /// <b>允许 before/after 为负</b>（平铺时靠前的段，其右端会落在事件之前）。
        /// </summary>
        internal static Span Of(float eventAt, float before, float after)
            => new Span { From = eventAt - before, To = eventAt + after };

        /// <summary>整段平移（用于"窗口整体挪到别处"）。</summary>
        internal static Span Shift(Span s, float dt)
            => new Span { From = s.From + dt, To = s.To + dt };

        /// <summary>
        /// 把 <paramref name="total"/> 按 <paramref name="count"/> 人**平铺**，返回第 <paramref name="index"/> 段。
        ///
        /// ★ 第一段独占 <paramref name="firstMin"/>：自爆幕里 `PlayDying()` 的前 **1.0 秒是哑期**
        ///   （项圈亮起但 `intensity = 0`，只响音效）。按人数平均分会让第一段整个落在哑期里
        ///   ⇒ 用户实测"第一个人看不清自爆开始的闪烁"。所以第一段至少拿 firstMin，余下再平分。
        ///
        /// ⚠ 返回的是**绝对区间**，不是相对偏移 —— 这是与现有实现最关键的区别：
        ///   现有实现返回 `segBefore/segAfter` 两个相对量，再由**两条路各自**拿不同锚点去解释，
        ///   于是同一次计算在两条路上得到两个不同的窗口。绝对区间没有这个自由度。
        /// </summary>
        internal static Span Tile(int index, int count, Span total, float firstMin)
        {
            float n = Math.Max(1, count);
            float len = Math.Max(0.5f, total.Length);

            // 第一段独占（单人时它就是整段）
            float first = n <= 1 ? len : Math.Min(len, Math.Max(firstMin, len / n));
            float perOther = n > 1 ? Math.Max(0f, len - first) / (n - 1) : 0f;

            float left = index <= 0 ? 0f : first + (index - 1) * perOther;
            float right = index <= 0 ? first : left + perOther;

            return new Span { From = total.From + left, To = total.From + right };
        }
    }
}
