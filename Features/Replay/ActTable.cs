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
        /// 本幕**录制者认识的人**（候选池）—— 只从帧本身取 id，不做任何距离/房间推断：
        ///   ① 有 `MoveShot` 的（AOI 内在动，位置在实时更新）
        ///   ② 有 `SpawnShot` 的（客户端认识他；AOI 外的人位置冻结、但照样有一串 SpawnShot）
        ///
        /// ★ 这里**只给 id，不判"看不看得见"** —— 因为判"看得见"要算距离，而算距离必须**两边同源**：
        ///   `SpawnShot` 里的位置是"他最后一次进入视野时"的（可能很旧），
        ///   拿它去减 `MoveShot` 的实时位置，是两个基准的数相减，结论没有意义。
        ///   ⇒ 距离判定交给调用方（`ReplayDirector.SceneIds`），那里两边都取 `HostRecorder` 的权威采样。
        ///
        /// 曾经试过、但**被 35 份 dump 回溯否定**的两条判据（留档，免得再走一遍）：
        ///   · "有 SpawnShot 就是在画面里" —— 不行：AOI 外的人照样有（实测 `#2` 有 7 枚、间隔 1.00s）；
        ///   · "整秒的 SpawnShot 是自动注册、非整秒是 `S_SPAWN`" —— 不行：`Recording` 里
        ///     `isStartShot ? SurvivalTime : ClientTime` 只区分"是不是每轮第一枚"，
        ///     `#2~#4` 的帧是 `59.37/60.37/61.37…`（非整秒）却正是 `RecordAllType` 录的。
        /// </summary>
        /// <param name="excludeId">要排除的人（首帧/剪影槽位 —— 他不算"画面里的人"）。</param>
        /// <param name="moved">回传"**有位置更新**"的那些 id —— 他们在画面里是确定的，调用方不必再判距离。</param>
        public static List<int> KnownIn(List<SnapShot> frames, int excludeId, out List<int> moved)
        {
            var ids = new HashSet<int>();
            var mvIds = new HashSet<int>();
            moved = new List<int>();
            if (frames == null)
                return moved;

            foreach (var s in frames)
            {
                var mv = s?.Move;
                if (mv != null && mv.PlayerId > 0 && mv.PlayerId != excludeId)
                {
                    ids.Add(mv.PlayerId);
                    mvIds.Add(mv.PlayerId);
                    continue;
                }
                var sp = s?.Spawn;
                if (sp != null && sp.PlayerId > 0 && sp.PlayerId != excludeId)
                    ids.Add(sp.PlayerId);
            }
            moved.AddRange(mvIds);
            return new List<int>(ids);
        }
    }
}