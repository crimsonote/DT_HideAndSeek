using System;
using System.Collections.Generic;
using BepInEx.Configuration;
using HarmonyLib;
using Protocol;
using Server.Game;
using HideAndSeek.Core;
using HideAndSeek.Features.Rule;
using HideAndSeek.Features.Weapon;
using GamePlayer = Server.Game.Player;

namespace HideAndSeek.Features.Replay
{
    /// <summary>
    /// 回放重写版 —— 当前处于**影子模式**：只计算并打印"如果换我，这一幕会怎么排"，
    /// **不发包、不改游戏状态、不播回放**。同一局里现有实现（<c>[EndReplay]</c>）照常工作，
    /// 于是日志里会并列出现两套决策，可以直接对照。
    ///
    /// 为什么先做影子模式：验证一次改动需要打一局，而"打一局"的成本不该反复花在用户身上。
    /// 影子模式让**你照常玩**，决策差异在日志里自己显形。
    ///
    /// 接入约定（重要）：
    ///   · 全部钩子用 **Postfix** —— 现有实现的 `TotalResultHook` 是 Prefix 且会 `return false`，
    ///     而 Harmony 里"某个 Prefix 返回 false"会让**后面的 Prefix 不再执行**（Postfix 仍会执行）。
    ///     用 Postfix 才能保证影子逻辑一定跑得到。
    ///   · 配置项**暂时借用**现有实现的 <c>[EndReplay]</c> 那些值（阶段 6 会把它们搬过来），
    ///     所以现在不需要新增任何配置键。
    /// </summary>
    [PatchFeature(
        section: "ReplayDraft",
        description: "回放重写版（当前：影子模式）。只计算并打印幕表，不发包、不播回放 —— 用于与现有实现【EndReplay】对照。",
        defaultEnabled: true,
        side: FeatureSide.Host)]
    internal static class ReplayFeature
    {
        [ConfigField(true, "影子模式：只计算与记录，不发包、不播回放。用于与现有实现对照。")]
        public static ConfigEntry<bool> ShadowOnly;

        // ── 状态（**一个**地方）────────────────────────────────────────
        private static readonly List<Act> Acts = new List<Act>();
        private static int _nextKey;
        private static int _murderDeaths;
        private static string _ending = "结算前";
        private static bool _reported;

        private static bool Armed
            => !ModeRuntime.Bypass && (ShadowOnly?.Value ?? true) && Diagnostics.IsLoaded("ReplayDraft");

        private static float Now()
        {
            try { return Managers.Game.ClientTime; }
            catch { return 0f; }
        }

        private static int NextKey() => _nextKey++;

        private static Act Add(ActKind kind, int subjectId, int recorderId, ReplayWindow.Span window, string note)
        {
            var act = new Act
            {
                Kind = kind,
                Key = NextKey(),
                SubjectId = subjectId,
                RecorderId = recorderId,
                Window = window,
                Note = note,
            };
            Acts.Add(act);
            return act;
        }

        private static bool HasKind(ActKind kind)
        {
            foreach (var a in Acts)
            {
                if (a.Kind == kind)
                    return true;
            }
            return false;
        }

        private static void Reset()
        {
            Acts.Clear();
            _nextKey = 0;
            _murderDeaths = 0;
            _ending = "结算前";
            _reported = false;
            HostRecorder.Clear();
            ReplayDirector.Reset();
        }

        // 借用现有实现的配置值（阶段 6 搬过来就独立了）
        private static float KnifeBefore => EndReplayFeature.KnifeBeforeSec?.Value ?? 1f;
        private static float KnifeAfter => EndReplayFeature.KnifeAfterSec?.Value ?? 1f;
        private static float KillBefore => EndReplayFeature.KillBeforeSec?.Value ?? 3f;
        private static float KillAfter => EndReplayFeature.KillAfterSec?.Value ?? 1f;
        private static float EndBefore => EndReplayFeature.EndBeforeSec?.Value ?? 3f;
        private static float EndAfter => EndReplayFeature.EndAfterSec?.Value ?? 1f;
        private static float TourBefore => EndReplayFeature.TourBeforeSec?.Value ?? 2f;
        private static float TourAfter => EndReplayFeature.TourAfterSec?.Value ?? 0.5f;
        private static float SelfBefore => EndReplayFeature.SelfDestructBeforeSec?.Value ?? 0f;
        private static float SelfAfter => EndReplayFeature.SelfDestructAfterSec?.Value ?? 0.5f;
        private static float BlackTail => Math.Max(0f, EndReplayFeature.BlackTailSec?.Value ?? 3f);
        private static int MaxClips => EndReplayFeature.MaxClips?.Value ?? 12;

        // ── 钩子 ────────────────────────────────────────────────────────

        /// <summary>一局开始：清状态、开采样。</summary>
        [HarmonyPatch(typeof(GameRoom), nameof(GameRoom.ChangeGameState), new[] { typeof(EGameState) })]
        internal static class RoundStartHook
        {
            [HarmonyPostfix]
            private static void Postfix(EGameState state)
            {
                if (state == EGameState.Survive)
                    Reset();
            }
        }

        /// <summary>
        /// 【采样点】`Player.Move` —— 服务端每次收到 `C_MOVE` 后调用它。
        /// 用它而不是定时 5Hz，是为了让服务端合成的密度与客户端录制**同源**。
        /// </summary>
        [HarmonyPatch(typeof(GamePlayer), "Move", new[] { typeof(PosInfo), typeof(bool) })]
        internal static class SampleHook
        {
            [HarmonyPostfix]
            private static void Postfix(GamePlayer __instance)
            {
                if (!Armed)
                    return;
                HostRecorder.NoteMove(__instance);
            }
        }

        /// <summary>「拿刀」—— `DelayAcquireWeapon` 是拔刀后的入口，只在真正拿到武器的人身上跑。</summary>
        [HarmonyPatch(typeof(GamePlayer), "DelayAcquireWeapon")]
        internal static class KnifeHook
        {
            [HarmonyPostfix]
            private static void Postfix(GamePlayer __instance)
            {
                if (!Armed)
                    return;
                // 只在自提模式登记：开局发刀没有"跑刀"过程，录出来只是站着不动。
                if (WeaponGrantFeature.GiveAtStart?.Value ?? false)
                    return;
                if (__instance?.PublicInfo == null || __instance.Session == null)
                    return;
                if (__instance.Color != EPlayerColor.Black && __instance.Color != EPlayerColor.Dark)
                    return;

                int id = __instance.PublicInfo.PlayerId;
                Add(ActKind.Knife, id, id, ActTable.Plain(Now(), KnifeBefore, KnifeAfter), "拿刀");
            }
        }

        /// <summary>「杀人」—— 原版在 `OnDamaged` 里已经请凶手录过一段，我们只是登记。</summary>
        [HarmonyPatch(typeof(GamePlayer), nameof(GamePlayer.OnDead))]
        internal static class KillHook
        {
            [HarmonyPostfix]
            private static void Postfix(GamePlayer __instance, GamePlayer black, EDeathType type)
            {
                if (!Armed)
                    return;
                if (type != EDeathType.Murder)
                    return;                    // 项圈自爆与审判处决都不算

                _murderDeaths++;

                if (black?.PublicInfo == null)
                    return;

                int victim = __instance?.PublicInfo?.PlayerId ?? 0;
                int id = black.PublicInfo.PlayerId;
                Add(ActKind.Kill, id, id, ActTable.Plain(Now(), KillBefore, KillAfter), $"#{id} → #{victim}");
            }
        }

        /// <summary>
        /// 「自爆」—— `OnDeadCollarBomb` 只是**开始**自爆（真正死亡在 6 秒后）。
        /// 窗口取 <c>[t0 − 额外前秒, t0 + 6 + 后秒]</c>，即从"自爆开始"那一刻起算。
        /// ⚠ 它对每个存活白方各调一次 ⇒ 必须用 <see cref="HasKind"/> 保证只登记一次。
        /// </summary>
        [HarmonyPatch(typeof(GamePlayer), "OnDeadCollarBomb")]
        internal static class CollarBombHook
        {
            [HarmonyPostfix]
            private static void Postfix(GamePlayer __instance)
            {
                if (!Armed)
                    return;
                if (__instance?.PublicInfo == null || __instance.IsSpectator)
                    return;

                float t0 = Now();
                HostRecorder.NoteBomb(t0, __instance.PublicInfo.PlayerId, __instance.PublicInfo.Pos);

                if (HasKind(ActKind.SelfDestruct))
                    return;

                var room = GameRoom.Instance;
                if (room == null)
                    return;

                var subjects = AliveWhites(room);
                var window = ActTable.SelfDestruct(t0, SelfBefore, SelfAfter);
                int seq = 0;
                foreach (int id in subjects)
                {
                    if (Acts.Count >= MaxClips)
                        break;
                    // 平铺：第一段独占哑期（见 ReplayWindow.Tile 的说明）
                    var seg = ReplayWindow.Tile(seq, subjects.Count, window, 1.5f);
                    var p = FindPlayer(room, id);
                    Add(ActKind.SelfDestruct, id, p?.Session != null ? id : 0, seg, $"被处决者 #{id}");
                    seq++;
                }

                int blackId = FindBlackId(room);
                if (blackId > 0 && Acts.Count < MaxClips)
                {
                    // 黑方收尾：与爆炸同时
                    Add(ActKind.BlackTail, blackId, 0,
                        ActTable.BlackTailOnBomb(t0 + ActTable.CollarToDeadSec, BlackTail), "黑方收尾");
                }
            }
        }

        /// <summary>「巡礼」—— 白胜：每个幸存者各一段，**同一段时间**、视角不同、依次播。</summary>
        [HarmonyPatch(typeof(WhiteWinFeature), nameof(WhiteWinFeature.TriggerWhiteWin))]
        internal static class WhiteWinHook
        {
            [HarmonyPostfix]
            private static void Postfix()
            {
                if (!Armed)
                    return;
                _ending = "白方胜利";

                if (HasKind(ActKind.Tour))
                    return;

                var room = GameRoom.Instance;
                if (room == null)
                    return;

                int blackId = FindBlackId(room);
                var subjects = AliveWhites(room);
                var window = ActTable.Plain(Now(), TourBefore, TourAfter);
                foreach (int id in subjects)
                {
                    if (Acts.Count >= MaxClips)
                        break;
                    var p = FindPlayer(room, id);
                    Add(ActKind.Tour, id, p?.Session != null ? id : 0, window, $"幸存者 #{id}");
                }

                if (blackId > 0 && Acts.Count < MaxClips)
                {
                    Add(ActKind.BlackTail, blackId, 0,
                        ActTable.BlackTailBeforeDecision(Now(), BlackTail), "黑方收尾（白胜）");
                }
            }
        }

        /// <summary>只做记录：黑方胜利的收尾演出跑过了。</summary>
        [HarmonyPatch(typeof(GameRoom), "GameOver")]
        internal static class BlackWinHook
        {
            [HarmonyPostfix]
            private static void Postfix()
            {
                _ending = "黑方胜利";
            }
        }

        /// <summary>
        /// 【结算】用 **Postfix**：现有实现的 `TotalResultHook` 是 Prefix 且会拦下这一次调用
        /// （`return false`），而 Harmony 在 Prefix 返回 false 时**仍会执行 Postfix** ⇒ 影子逻辑一定跑得到。
        /// 现有实现稍后会自己再调一次 `ChangeGameState(TotalResult)`，那次它的 Prefix 放行，
        /// 于是这里会被调用第二次 —— 用 <see cref="_reported"/> 去重。
        /// </summary>
        [HarmonyPatch(typeof(GameRoom), nameof(GameRoom.ChangeGameState), new[] { typeof(EGameState) })]
        internal static class SettleHook
        {
            [HarmonyPostfix]
            private static void Postfix(EGameState state)
            {
                if (state != EGameState.TotalResult)
                    return;
                if (!Armed)
                    return;

                // 结算时补登记"结算类"的幕（它们的事件时刻就是"结算被拦下"这一刻）。
                RegisterSettlementActs();

                Report();   // 概览：阈值 / 采样
                // 影子：逐幕做一次完整装配并打印结果（不发包、不广播、不播放）。
                ReplayDirector.ShadowAssemble(Acts, EndReplayFeature.BombBlackout?.Value ?? false);
            }
        }

        /// <summary>
        /// 结算时补登记"结算类"的幕 —— 它们的事件时刻都是**结算被拦下**这一刻。
        ///
        /// 规则（避免同一件事登记两遍）：
        ///   · 已经有「自爆」幕（黑胜处决）⇒ 不再排「最后时段」：那一刻白方全死了，没有存活着可拍；
        ///   · 已经有「巡礼」幕（白胜）⇒ 同上，`TriggerWhiteWin` 已经排过；
        ///   · 否则 ⇒ 为**还活着的白方**各排一段「最后时段」（纯刀杀造成的黑胜局就是这种）；
        ///   · 「黑方收尾」若还没有就补一段 —— 黑胜时与爆炸同时，没有自爆就落在结算前。
        /// </summary>
        private static void RegisterSettlementActs()
        {
            try
            {
                var room = GameRoom.Instance;
                if (room == null)
                    return;

                int blackId = FindBlackId(room);
                float now = Now();

                if (!HasKind(ActKind.SelfDestruct) && !HasKind(ActKind.Tour))
                {
                    int seq = 0;
                    foreach (int id in AliveWhites(room))
                    {
                        if (Acts.Count >= MaxClips)
                            break;
                        var p = FindPlayer(room, id);
                        Add(ActKind.Final, id, p?.Session != null ? id : 0,
                            ActTable.Plain(now, EndBefore, EndAfter), $"存活者 #{id}");
                        seq++;
                    }
                    if (seq > 0)
                        Plugin.Log.LogInfo($"[HS-Shadow] 结算补登记「最后时段」{seq} 段（存活白方各一段、同一段时间）。");
                }

                if (!HasKind(ActKind.BlackTail) && blackId > 0 && Acts.Count < MaxClips)
                {
                    // 黑方收尾的时刻：有自爆就跟着爆炸（自爆开始 + 6s），否则落在结算前。
                    float anchor = now;
                    foreach (var a in Acts)
                    {
                        if (a.Kind == ActKind.SelfDestruct)
                        {
                            anchor = a.Window.From + ActTable.CollarToDeadSec;
                            break;
                        }
                    }
                    Add(ActKind.BlackTail, blackId, 0,
                        ActTable.BlackTailOnBomb(anchor, BlackTail), "黑方收尾（结算补登记）");
                }
            }
            catch (Exception ex)
            {
                Plugin.Log.LogWarning($"[HS-Shadow] 结算补登记失败 — {ex.Message}");
            }
        }

        // ── 输出 ────────────────────────────────────────────────────────

        private static void Report()
        {
            if (_reported)
                return;
            _reported = true;

            try
            {
                int white = 0;
                var room = GameRoom.Instance;
                if (room != null)
                {
                    foreach (var p in room.Players)
                    {
                        if (p?.PublicInfo == null || p.IsSpectator)
                            continue;
                        if (p.Color == EPlayerColor.Black || p.Color == EPlayerColor.Dark)
                            continue;
                        white++;
                    }
                }
                int need = EndReplayFeature.DeathRatioPercent?.Value ?? 30;
                bool armed = _murderDeaths > 0 && white > 0 && _murderDeaths * 100 > white * need;

                Plugin.Log.LogInfo($"[HS-Shadow] ════ 影子幕表（{_ending}）：{Acts.Count} 幕，"
                    + $"刀杀 {_murderDeaths}/{white} 人（阈值 {need}% ⇒ {(armed ? "会播" : "不会播")}）════");
                Plugin.Log.LogInfo($"[HS-Shadow] 采样：{HostRecorder.Stats()}");

                foreach (var a in Acts)
                {
                    // 剪影槽位：必须在"本幕会出现在画面里的人"之外，所以先算 roster
                    ActTable.BuildRoster(a.SubjectId, a.Window, out var rosterIds);
                    var sil = SilhouetteResolver.Resolve(a.SubjectId, rosterIds);
                    a.SilhouetteId = sil.Id;

                    Plugin.Log.LogInfo($"[HS-Shadow]   {ActTable.Name(a.Kind),-4} key={a.Key,-3} "
                        + $"窗口={a.Window} 主角=#{a.SubjectId,-2} 剪影={sil} "
                        + $"roster={rosterIds.Count} 人  来源={(a.RecorderId > 0 ? "客户端优先" : "服务端合成")}  {a.Note}");
                }

                if (Acts.Count == 0)
                    Plugin.Log.LogInfo("[HS-Shadow]   （这一幕是空的）");
            }
            catch (Exception ex)
            {
                Plugin.Log.LogWarning($"[HS-Shadow] 打印幕表失败 — {ex.Message}");
            }
        }

        // ── 小工具 ──────────────────────────────────────────────────────

        private static List<int> AliveWhites(GameRoom room)
        {
            var list = new List<int>();
            try
            {
                int blackId = FindBlackId(room);
                foreach (var p in room.AlivePlayers)
                {
                    int id = p?.PublicInfo?.PlayerId ?? 0;
                    if (id <= 0 || id == blackId || p.IsSpectator)
                        continue;
                    list.Add(id);
                }
            }
            catch (Exception ex)
            {
                Plugin.Log.LogWarning($"[HS-Shadow] 统计存活白方失败 — {ex.Message}");
            }
            return list;
        }

        private static int FindBlackId(GameRoom room)
        {
            foreach (var p in room.Players)
            {
                if (p?.PublicInfo != null && (p.Color == EPlayerColor.Black || p.Color == EPlayerColor.Dark))
                    return p.PublicInfo.PlayerId;
            }
            foreach (var p in room.DeadPlayers)
            {
                if (p?.PublicInfo != null && (p.Color == EPlayerColor.Black || p.Color == EPlayerColor.Dark))
                    return p.PublicInfo.PlayerId;
            }
            return 0;
        }

        private static GamePlayer FindPlayer(GameRoom room, int id)
        {
            foreach (var p in room.Players)
            {
                if (p?.PublicInfo != null && p.PublicInfo.PlayerId == id)
                    return p;
            }
            foreach (var p in room.DeadPlayers)
            {
                if (p?.PublicInfo != null && p.PublicInfo.PlayerId == id)
                    return p;
            }
            return null;
        }
    }
}
