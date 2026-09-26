using System.Collections.Generic;
using BepInEx.Configuration;
using HarmonyLib;
using Protocol;
using Server.Game;
using HideAndSeek.Core;
using HideAndSeek.Features.Vision;
using GamePlayer = Server.Game.Player;

namespace HideAndSeek.Features.Skill
{
    /// <summary>
    /// 路易斯（念力 `Telekinesis`）重写。
    ///
    /// 原版：`SkillComponent.UseTelekinesis(targetId)`（Assembly-CSharp:177576）给目标挂
    /// `EBuffType.DeadDetective`，目标死后进侦探。这里保留原效果，另加四条：
    ///
    ///   1. 被标记的目标死亡 ⇒ **杀手时停 3 秒**，随后**清空体力**
    ///      —— 时停用 `EBuffType.TheWorld`（我们 stasis 命令就是这么做的），
    ///         力竭用 `EBuffType.Exhausted`（原版自己就这么用：`:174229 AddBuff(Exhausted, 5000)`）
    ///   2. 凶手若是路易斯自己 ⇒ **不施加**上述负面效果（需求明确）
    ///   3. 路易斯的技能随后重新进入周转（默认 30 秒）
    ///   4. 路易斯每 15 秒获得 3 秒的目标实时位置
    ///      —— 走 `S_PIN_MOVE`（红毛 `ComplyRules` 用的就是这条，云端 :42246），复用 `WhiteRadarFeature.SendPin`
    ///
    /// ⚠ 跨局必须清标记表：它按 PlayerId 索引，而座位号跨局会复用（今天已因此栽过两次）。
    /// </summary>
    [PatchFeature("LuisAbility",
        "路易斯（念力）重写：被标记目标死亡 ⇒ 杀手时停+清空体力（凶手是路易斯自己则豁免）；技能转入周转；每 15 秒获得 3 秒目标位置。",
        defaultEnabled: false, side: FeatureSide.Host)]
    internal static class LuisAbilityFeature
    {
        [ConfigField(3f, "目标死亡后，杀手的时停秒数（0 = 关闭）。", Min = 0f, Max = 30f)]
        public static ConfigEntry<float> StasisSeconds;

        [ConfigField(5000f, "时停结束后清空体力的持续毫秒数。原版自己用的是 5000ms（:174229 AddBuff(Exhausted, 5000)）。", Min = 0f, Max = 60000f)]
        public static ConfigEntry<float> ExhaustMs;

        [ConfigField(30f, "目标死亡后，路易斯技能的周转秒数（0 = 不干预）。", Min = 0f, Max = 600f)]
        public static ConfigEntry<float> RechargeSeconds;

        [ConfigField(15f, "位置追踪的间隔秒数（0 = 关闭追踪）。", Min = 0f, Max = 120f)]
        public static ConfigEntry<float> TraceIntervalSeconds;

        [ConfigField(3f, "每次位置追踪持续的秒数。", Min = 0.5f, Max = 30f)]
        public static ConfigEntry<float> TraceDurationSeconds;

        /// <summary>被标记的目标 PlayerId → 标记他的路易斯 PlayerId。</summary>
        private static readonly Dictionary<int, int> MarkedBy = new Dictionary<int, int>();

        /// <summary>路易斯 PlayerId → 追踪到期时刻（SurviveTime）。</summary>
        private static readonly Dictionary<int, float> TraceUntil = new Dictionary<int, float>();

        /// <summary>路易斯 PlayerId → 下次可以再发一次追踪的时刻。</summary>
        private static readonly Dictionary<int, float> NextTraceAt = new Dictionary<int, float>();

        /// <summary>路易斯 PlayerId → 最后一次发出的箭头位置（删除时 Pos 必须精确同值）。</summary>
        private static readonly Dictionary<int, PosInfo> TraceMarks = new Dictionary<int, PosInfo>();

        private static float Now => TimeManager.Instance?.SurviveTime ?? 0f;

        private static bool IsLuis(GamePlayer p)
            => p?.SkillComponent?.Data != null && p.SkillComponent.Data.Type == ESkillType.Telekinesis;

        // ── ① 记录标记：原版给目标挂 DeadDetective 的那一刻 ──
        [HarmonyPatch(typeof(SkillComponent), "UseTelekinesis")]
        internal static class MarkHook
        {
            [HarmonyPostfix]
            private static void Postfix(SkillComponent __instance, int targetId)
            {
                if (ModeRuntime.Bypass)
                    return;

                var owner = __instance?.Owner;
                if (owner?.PublicInfo == null || targetId <= 0)
                    return;
                if (!IsLuis(owner))
                    return;

                MarkedBy[targetId] = owner.PublicInfo.PlayerId;
                TraceUntil.Remove(owner.PublicInfo.PlayerId);
                NextTraceAt[owner.PublicInfo.PlayerId] = Now;   // 用技能后马上就能看到第一次追踪
            }
        }

        // ── ② 目标死亡：按凶手施加负面效果 + 给路易斯周转 ──
        // OnDead(Player black, EDeathType type)（:175968）—— 第一个参数就是凶手，用 __0 取，
        // 免得依赖 EDeathType 的命名空间。
        [HarmonyPatch(typeof(GamePlayer), nameof(GamePlayer.OnDead))]
        internal static class DeadHook
        {
            [HarmonyPostfix]
            private static void Postfix(GamePlayer __instance, GamePlayer __0)
            {
                if (ModeRuntime.Bypass || __instance?.PublicInfo == null)
                    return;

                int deadPid = __instance.PublicInfo.PlayerId;
                int luisPid;
                if (!MarkedBy.TryGetValue(deadPid, out luisPid))
                    return;                                    // 死者不是被路易斯标记的人

                MarkedBy.Remove(deadPid);                      // 一次标记只结算一次
                var killer = __0;

                // 需求：凶手是路易斯自己时，不受这一套负面效果
                bool killerIsLuis = killer?.PublicInfo != null && killer.PublicInfo.PlayerId == luisPid;

                if (!killerIsLuis && killer?.PublicInfo != null)
                {
                    float stasis = StasisSeconds?.Value ?? 3f;
                    if (stasis > 0f)
                    {
                        try
                        {
                            killer.BuffComponent?.AddBuff(EBuffType.TheWorld, (int)(stasis * 1000f));
                            GameRoom.Instance?.BroadcastWorldSFX(ESoundType.TheWorldSfx,
                                killer.PublicInfo.Pos, 896f);
                        }
                        catch (global::System.Exception ex)
                        {
                            Plugin.Log.LogWarning($"[HS] LuisAbility：时停失败 — {ex.Message}");
                        }
                    }

                    // 需求：时停 **3 秒结束后**才被清空体力（不是同时）。
                    // 所以排一个延迟任务，到点再补 Exhausted。
                    // 时停为 0 时就立刻给（那种配置下"结束后"就是"现在"）。
                    float exhaust = ExhaustMs?.Value ?? 5000f;
                    if (exhaust > 0f)
                    {
                        var victim = killer;
                        int delaySec = stasis > 0f ? (int)global::System.Math.Ceiling(stasis) : 0;
                        try
                        {
                            if (delaySec <= 0)
                            {
                                victim.BuffComponent?.AddBuff(EBuffType.Exhausted, (int)exhaust);
                            }
                            else
                            {
                                TimeManager.Instance?.PushSurvivalJob(delaySec, delegate
                                {
                                    try { victim?.BuffComponent?.AddBuff(EBuffType.Exhausted, (int)exhaust); }
                                    catch (global::System.Exception ex2)
                                    {
                                        Plugin.Log.LogWarning($"[HS] LuisAbility：延迟清空体力失败 — {ex2.Message}");
                                    }
                                });
                            }
                        }
                        catch (global::System.Exception ex)
                        {
                            Plugin.Log.LogWarning($"[HS] LuisAbility：清空体力失败 — {ex.Message}");
                        }
                    }
                }

                // 路易斯的技能转入周转
                float recharge = RechargeSeconds?.Value ?? 30f;
                if (recharge > 0f)
                {
                    var room = GameRoom.Instance;
                    if (room?.Players != null)
                    {
                        for (int i = 0; i < room.Players.Count; i++)
                        {
                            var p = room.Players[i];
                            if (p?.PublicInfo == null || p.PublicInfo.PlayerId != luisPid)
                                continue;
                            // CoolSkill 是 SkillComponent 的 **private**（:177253），外部调不了 ⇒
                            // 复刻它的三步：① 关掉"可用" ② 发 S_COOLTIME_SKILL（客户端据此显示、自行每秒递减）
                            // ③ 到点后再打开"可用"。
                            try
                            {
                                p.CanUseSkill = false;
                                p.Session?.Send(new S_COOLTIME_SKILL { Cooltime = (int)recharge });
                                var who = p;
                                TimeManager.Instance?.PushSurvivalJob((int)recharge, delegate
                                {
                                    if (who != null)
                                        who.CanUseSkill = true;
                                });
                            }
                            catch (global::System.Exception ex)
                            {
                                Plugin.Log.LogWarning($"[HS] LuisAbility：技能周转失败 — {ex.Message}");
                            }
                            break;
                        }
                    }
                }

                Plugin.Log.LogInfo(
                    $"[HS] LuisAbility：标记目标 #{deadPid} 已死亡，凶手 #{killer?.PublicInfo?.PlayerId ?? 0}"
                    + (killerIsLuis ? "（是路易斯自己，豁免）" : "，已施加时停与力竭") + "。");
            }
        }

        /// <summary>
        /// 给路易斯发一次"指向目标"的世界箭头。
        ///
        /// `UI_Arrow` 全场只有一个槽位（`:13807-13819` 复用同一个对象），
        /// 所以"重发"就是**更新同一个箭头的目标位置** —— 不需要先删。
        /// 只有"结束追踪"时才需要发 `S_REMOVE_ARROW`（且 Pos 必须与最后一发精确同值）。
        /// </summary>
        private static void SendTraceArrow(GamePlayer luis, GamePlayer target)
        {
            if (luis?.Session == null || target?.PublicInfo == null)
                return;
            try
            {
                luis.Session.Send(new S_NOTIFY_ARROW
                {
                    Type = EArrowType.CharacterArrow,
                    Pos = target.PublicInfo.Pos
                });
                TraceMarks[luis.PublicInfo.PlayerId] = target.PublicInfo.Pos;
            }
            catch (global::System.Exception ex)
            {
                Plugin.Log.LogWarning($"[HS] LuisAbility：追踪箭头发包失败 — {ex.Message}");
            }
        }

        // ── ③a 跟随：挂目标的 Move（和红毛一样，10 Hz 级），只在追踪窗口内发 ──
        [HarmonyPatch(typeof(GamePlayer), nameof(GamePlayer.Move), new[] { typeof(PosInfo), typeof(bool) })]
        internal static class MoveHook
        {
            [HarmonyPostfix]
            private static void Postfix(GamePlayer __instance)
            {
                if (ModeRuntime.Bypass || TraceUntil.Count == 0 || __instance?.PublicInfo == null)
                    return;

                int targetPid = __instance.PublicInfo.PlayerId;
                int luisPid;
                if (!MarkedBy.TryGetValue(targetPid, out luisPid))
                    return;                                  // 移动的不是被标记的人

                float until;
                if (!TraceUntil.TryGetValue(luisPid, out until) || Now >= until)
                    return;                                  // 不在 3 秒窗口内

                var room = GameRoom.Instance;
                if (room?.Players == null)
                    return;

                for (int i = 0; i < room.Players.Count; i++)
                {
                    var luis = room.Players[i];
                    if (luis?.PublicInfo != null && luis.PublicInfo.PlayerId == luisPid)
                    {
                        SendTraceArrow(luis, __instance);
                        break;
                    }
                }
            }
        }

        // ── ③ 位置追踪：每 TraceIntervalSeconds 给路易斯发一次，持续 TraceDurationSeconds ──
        [HarmonyPatch(typeof(GameRoom), "SurvivalTick")]
        internal static class TraceHook
        {
            [HarmonyPostfix]
            private static void Postfix()
            {
                if (ModeRuntime.Bypass || MarkedBy.Count == 0)
                    return;

                var room = GameRoom.Instance;
                if (room?.Players == null)
                    return;

                float interval = TraceIntervalSeconds?.Value ?? 15f;
                if (interval <= 0f)
                    return;

                float now = Now;

                foreach (var kv in MarkedBy)
                {
                    int targetPid = kv.Key, luisPid = kv.Value;

                    GamePlayer luis = null, target = null;
                    for (int i = 0; i < room.Players.Count; i++)
                    {
                        var p = room.Players[i];
                        if (p?.PublicInfo == null)
                            continue;
                        if (p.PublicInfo.PlayerId == luisPid) luis = p;
                        else if (p.PublicInfo.PlayerId == targetPid) target = p;
                    }
                    if (luis?.Session == null || target?.PublicInfo == null)
                        continue;
                    if (!luis.IsAlive || !target.IsAlive)
                    {
                        // 需求：目标死亡后 ⇒ **不再通报位置**。
                        // 注意不只是"停止重发" —— 箭头是死数据，必须主动撤掉（同 S_REMOVE_ARROW）。
                        PosInfo dead;
                        if (TraceMarks.TryGetValue(luisPid, out dead))
                        {
                            TraceMarks.Remove(luisPid);
                            TraceUntil.Remove(luisPid);
                            try
                            {
                                luis.Session.Send(new S_REMOVE_ARROW
                                {
                                    Type = EArrowType.CharacterArrow,
                                    Pos = dead
                                });
                            }
                            catch { }
                        }
                        continue;
                    }

                    float next;
                    if (!NextTraceAt.TryGetValue(luisPid, out next))
                        next = now;
                    if (now < next)
                        continue;

                    NextTraceAt[luisPid] = now + interval;
                    TraceUntil[luisPid] = now + (TraceDurationSeconds?.Value ?? 3f);

                    // ★ 改用 S_NOTIFY_ARROW + EArrowType.CharacterArrow。
                    //
                    // 为什么不用 S_PIN_MOVE：那个包在客户端会**同时**刷"场景 HUD 地图"和"平板"两处
                    // （Handle_S_PIN_MOVE :42246/:42247 是无条件两行，同一个 Type，客户端没有任何
                    //   按 ID 分岔的逻辑）⇒ 用它在平板上必然留下标记。
                    // 而且世界箭头（SetComplyRulesArrow）要求 Type 是**真实 PlayerId**
                    // （:13801 GetPlayerCache(target.ID) 为 null 就直接 return）——
                    // 我们原来那套 92000+pid 根本没有箭头，只有地图 pin。
                    //
                    // S_NOTIFY_ARROW 那条链（:42569 → MyPlayer.SetArrow :13765 → UI_Arrow）
                    // **完全不碰 pin / 地图**，正是"指向人的世界箭头、地图零痕迹"。
                    SendTraceArrow(luis, target);   // 起步那一发；后续由 Move 钩子跟随
                }

                // 追踪到期 ⇒ 发删除哨兵（Pos = null）
                if (TraceUntil.Count == 0)
                    return;
                List<int> expired = null;
                foreach (var kv in TraceUntil)
                {
                    if (now < kv.Value)
                        continue;
                    (expired ?? (expired = new List<int>())).Add(kv.Key);
                }
                if (expired == null)
                    return;
                foreach (int luisPid in expired)
                {
                    TraceUntil.Remove(luisPid);

                    PosInfo last;
                    if (!TraceMarks.TryGetValue(luisPid, out last))
                        continue;
                    TraceMarks.Remove(luisPid);

                    for (int i = 0; i < room.Players.Count; i++)
                    {
                        var p = room.Players[i];
                        if (p?.PublicInfo == null || p.PublicInfo.PlayerId != luisPid || p.Session == null)
                            continue;
                        try
                        {
                            // 删除：Pos 必须与发出去的那一发**精确同值**（RemoveArrow :13774 按值匹配）
                            p.Session.Send(new S_REMOVE_ARROW
                            {
                                Type = EArrowType.CharacterArrow,
                                Pos = last
                            });
                        }
                        catch { }
                        break;
                    }
                }
            }
        }

        /// <summary>追踪 pin 的号码段（与雷达 PinIdBase、美幸的 90000 段都错开）。</summary>
        private const int TracePinId = 92000;

        // ── ④ 跨局清理 ──
        [HarmonyPatch(typeof(GameRoom), "StartSurvive")]
        internal static class StartHook
        {
            [HarmonyPostfix]
            private static void Postfix() => Clear();
        }

        [HarmonyPatch(typeof(GameRoom), "StartLobby")]
        internal static class LobbyHook
        {
            [HarmonyPostfix]
            private static void Postfix() => Clear();
        }

        private static void Clear()
        {
            // MarkedBy / TraceUntil / NextTraceAt 记的都是 PlayerId 与 SurviveTime；
            // SurviveTime 每局被 ResetSurvival() 设回 420（不是 0），不清会算出负数 ⇒ 永不失效。
            MarkedBy.Clear();
            TraceUntil.Clear();
            NextTraceAt.Clear();
            TraceMarks.Clear();
        }
    }
}
