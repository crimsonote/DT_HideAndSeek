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
    /// ★ 所有窗口都相对**事件时刻**，且返回的是**绝对区间**（见 <see cref="ReplayWindow"/>）。
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
        ///   ⇒ 所以自爆幕的**事件时刻取 t0**，窗口右端靠 after 拉到 t0+6.5 —— 而不是把事件定在 t0+6。
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
        /// 自爆幕：<paramref name="t0"/> = **自爆开始**那一刻。
        /// 窗口 = <c>[t0 − 额外前秒, t0 + 6 + 后秒]</c>（默认 <c>[t0, t0+6.5]</c>）。
        /// 用户口径："从严格的自爆开始时开始裁剪"。
        /// </summary>
        public static ReplayWindow.Span SelfDestruct(float t0, float extraBefore, float after)
            => ReplayWindow.Of(t0, extraBefore, CollarToDeadSec + after);

        /// <summary>
        /// 黑方收尾（**黑胜**）：与爆炸**同时** —— 窗口平分成 <c>[爆炸 − tail/2, 爆炸 + tail/2]</c>，
        /// 让爆炸落在这一幕的中间。用户口径："黑方的录制时段最好和爆炸同时，而不是在那之后"。
        /// </summary>
        public static ReplayWindow.Span BlackTailOnBomb(float bombAt, float tail)
            => ReplayWindow.Of(bombAt, tail / 2f, tail / 2f);

        /// <summary>黑方收尾（**白胜**）：窗口落在判定**之前** <c>tail</c> 秒（白胜是立刻结算，判定后没有素材）。</summary>
        public static ReplayWindow.Span BlackTailBeforeDecision(float decisionAt, float tail)
            => ReplayWindow.Of(decisionAt, tail, 0f);

        // ── roster（"全员出场帧"的数据源）────────────────────────────

        /// <summary>
        /// 构建本幕的 roster —— "**会出现在画面里的那些人**"，每人一份"窗口起点时的样子"。
        ///
        /// 为什么由房主侧采样产出，而不是像旧实现那样"从客户端磁带里捞 SpawnShot"：
        ///   客户端 `Player.Move` 只把移动包发给 `Session`(自己) / 所有死者 / **AOI 内**的活人与观察者
        ///   ⇒ 活人录的磁带**只有 AOI 内的人**。旧实现从里面捞，实测只捞到 1 枚，等于空转；
        ///   而"每台机器装配好自己的 0 号替身"正是靠这一轮出场帧，缺了会让它的换道具镜头打在空引用上
        ///   ⇒ 回放永久卡死。
        ///
        /// roster 的口径 = **主角 + 窗口内活动过的人**。
        ///   · 不包含"整幕没动过、且不在主角附近"的人 —— 与旧实现（AOI）的语义相近；
        ///   · ⚠ 已知取舍：同房间但整幕没动的人也会被排除（原版会录到他）。
        ///     要收窄成"与主角同房间过的人"需要再遍历一次 `Range` 比较 `RoomId`，留待需要时再加。
        /// </summary>
        public static List<PublicPlayerInfo> BuildRoster(int subjectId, ReplayWindow.Span window)
            => BuildRoster(subjectId, window, out _);

        /// <summary>同上，并回传 id 集合（剪影决策要用"谁在画面里"这个信息）。</summary>
        public static List<PublicPlayerInfo> BuildRoster(int subjectId, ReplayWindow.Span window, out List<int> ids)
        {
            var seen = new HashSet<int>();
            if (subjectId > 0)
                seen.Add(subjectId);

            foreach (var s in HostRecorder.Range(window.From, window.To))
                seen.Add(s.Id);

            var list = new List<PublicPlayerInfo>(seen.Count);
            foreach (int id in seen)
            {
                var info = HostRecorder.At(id, window.From);
                if (info != null)
                    list.Add(info);
            }

            ids = new List<int>(seen);
            return list;
        }
    }
}
