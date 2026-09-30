using System;
using System.Collections.Generic;
using Protocol;

namespace HideAndSeek.Features.Replay
{
    /// <summary>
    /// 【幕表】—— 每一幕的时间语义集中在这里，别处不再各自算窗口。
    ///
    /// 为什么值得单独抽一个类：
    ///   现有实现里"窗口"的含义散在三处（`CollarBombHook` 拼 before/after、`TileWindow` 切段、
    ///   `TrimTape` 与 `TrySendHostTape` 各拿一个基准去套），而**自爆幕的 before 里含 6 秒**
    ///   这件事只写在注释里 ⇒ 一改就错，且错了看不出来（那个 6 秒偏移就是这么来的）。
    ///   这里把"这一幕的事件时刻是什么、窗口怎么算"写成有名字的函数。
    ///
    /// ★ 所有窗口都相对**事件时刻**，且返回的是**绝对区间**（见 ReplayWindow）。
    ///   素材从哪来（客户端磁带 / 服务端合成）**不影响窗口** —— 它只影响"这个区间里能取到哪些帧"。
    /// </summary>
    internal static class ActTable
    {
        /// <summary>
        /// 「自爆开始 → 真正死亡」的秒数 = 原版 `OnDeadCollarBomb` 里 `PushAfter(6000, OnDead)`。
        ///
        /// ★ 关键：`OnDeadCollarBomb` **不是"爆炸了"**，而是**"项圈开始自爆"** ——
        ///   t=0 广播 `DyingVfx` ⇒ 客户端 `PlayDying()` 开始（前 **1.0 秒是哑期**：
        ///   项圈亮起但 `intensity = 0`，只响音效），之后 4 轮越来越快的闪烁（共 5.875s），
        ///   而"人消失"在 t+6（`CreateBombCorpse` + `S_DESPAWN`）。
        ///   ⇒ 自爆幕的**事件时刻取 t0**，窗口右端靠 after 拉到 t0+6.5。
        /// </summary>
        public const float CollarToDeadSec = 6f;

        public static string Name(ActKind k)
        {
            switch (k)
            {
                case ActKind.Knife: return "拿刀";
                case ActKind.Kill: return "杀人";
                case ActKind.Final: return "最后";
                case ActKind.Tour: return "巡礼";
                case ActKind.SelfDestruct: return "自爆";
                case ActKind.BlackTail: return "黑方";
                default: return k.ToString();
            }
        }

        // ── 窗口规则（**唯一**的地方）──────────────────────────────────

        /// <summary>普通幕：拿刀 / 杀人 / 最后 / 巡礼 —— 事件前后各取一段。</summary>
        public static ReplayWindow.Span Plain(float eventAt, float before, float after)
            => ReplayWindow.Of(eventAt, before, after);

        /// <summary>
        /// 自爆幕：t0 = **自爆开始**那一刻。
        /// 窗口 = [t0 − 额外前秒, t0 + 6 + 后秒]（默认 [t0, t0+6.5]）。
        /// 用户口径："从严格的自爆开始时开始裁剪"。
        /// </summary>
        public static ReplayWindow.Span SelfDestruct(float t0, float extraBefore, float after)
            => ReplayWindow.Of(t0, extraBefore, CollarToDeadSec + after);

        /// <summary>
        /// 黑方收尾（**黑胜**）：与爆炸**同时** —— 窗口平分成 [爆炸 − tail/2, 爆炸 + tail/2]，
        /// 让爆炸落在这一幕的中间。用户口径："黑方的录制时段最好和爆炸同时，而不是在那之后"。
        /// </summary>
        public static ReplayWindow.Span BlackTailOnBomb(float bombAt, float tail)
            => ReplayWindow.Of(bombAt, tail / 2f, tail / 2f);

        /// <summary>黑方收尾（**白胜**）：窗口落在判定**之前** tail 秒（白胜是立刻结算，判定后没有素材）。</summary>
        public static ReplayWindow.Span BlackTailBeforeDecision(float decisionAt, float tail)
            => ReplayWindow.Of(decisionAt, tail, 0f);

        // ── 「谁在画面里」──────────────────────────────────────────────

        /// <summary>
        /// 从**帧本身**判断"谁会出现在画面里" —— 不需要任何近似（房间 / AOI 都是在猜它）。
        ///
        /// 判据：帧里出现过 <c>SpawnShot</c> 的玩家（每人取离窗口起点最近的那一枚）。
        /// 为什么这就等于"会出现在画面里"：客户端 `ApplySpawn` 是**唯一**会
        /// `SetInfo(PublicPlayerInfo)` 的地方，只有被 spawn 过的人才会被装配进场景；
        /// 而 `ApplyMove` 对没 spawn 过的人**静默无效**。
        ///
        /// ★ 它同时统一了两条素材路径：
        ///   · 客户端磁带 —— 帧就是 AOI 的**真实结果**，直接读即可；
        ///   · 服务端合成 —— 帧是我们自己铺的（`HostSynth` 会在窗口起点给每个有采样的人各发一枚），
        ///     所以"谁在画面里"同样由帧决定，而不是另行推断。
        /// </summary>
        /// <param name="excludeId">要排除的人（首帧/剪影槽位 —— 他不算"画面里的人"）。</param>
        public static List<int> VisibleIn(List<SnapShot> frames, float windowStart, int excludeId)
        {
            var best = new Dictionary<int, float>();
            if (frames != null)
            {
                foreach (var s in frames)
                {
                    if (s?.Type != ESnapShotType.SpawnShot || s.Spawn == null)
                        continue;
                    int id = s.Spawn.PlayerId;
                    if (id <= 0 || id == excludeId)
                        continue;
                    float d = Math.Abs(s.TimeStamp - windowStart);
                    if (best.TryGetValue(id, out float prev) && prev <= d)
                        continue;                       // 已有更近的
                    best[id] = d;
                }
            }
            return new List<int>(best.Keys);
        }
    }
}