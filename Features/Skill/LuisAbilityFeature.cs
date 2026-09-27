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
    ///      —— 箭头样式由 `ArrowStyle` 决定（默认 `Character` = 红毛同款的**角色箭头**，
    ///         贴图按目标角色取；`Corpse` = 地图零痕迹的尸体箭头），
    ///         窗口内挂在目标的 `Move` 上持续刷新 —— 两条通道的差别见 `SendTraceArrow`
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

        [ConfigField("Character", "追踪箭头的样式。\n" +
            "Character = 「角色箭头」（与红毛 ComplyRules 同款）：箭头贴图按**目标的角色**显示（如 Luna_Map_Black）；\n" +
            "            代价是平板地图上会多出一个标记（客户端 :42247 无条件刷，房主端避不开）。\n" +
            "Corpse    = 「尸体箭头」（原版尸体通报那种箭头）：只走 UI_Arrow，**地图与平板零痕迹**。")]
        public static ConfigEntry<string> ArrowStyle;

        /// <summary>被标记的目标 PlayerId → 标记他的路易斯 PlayerId。</summary>
        private static readonly Dictionary<int, int> MarkedBy = new Dictionary<int, int>();

        /// <summary>路易斯 PlayerId → 追踪到期时刻（SurviveTime）。</summary>
        private static readonly Dictionary<int, float> TraceUntil = new Dictionary<int, float>();

        /// <summary>路易斯 PlayerId → 下次可以再发一次追踪的时刻。</summary>
        private static readonly Dictionary<int, float> NextTraceAt = new Dictionary<int, float>();

        /// <summary>
        /// 路易斯 PlayerId → （被指向的目标 PlayerId, 最后一次发出的位置）。
        ///
        /// 两个都要记：撤销时 `Corpse` 样式要按 `(Type, Pos)` 精确匹配删（`:13779`），
        /// `Character` 样式要把目标 PlayerId 填回 `S_PIN_MOVE.Type` 才认得出删哪个。
        /// 位置必须 `Clone()` —— `PublicInfo.Pos` 会随玩家移动被原地改写，存引用会删不掉。
        /// </summary>
        private static readonly Dictionary<int, (int targetPid, PosInfo pos)> TraceMarks =
            new Dictionary<int, (int, PosInfo)>();

        /// <summary>是否用「角色箭头」（红毛同款）。默认 Character。</summary>
        private static bool UseCharacterArrow()
            => !string.Equals(ArrowStyle?.Value, "Corpse", global::System.StringComparison.OrdinalIgnoreCase);

        /// <summary>
        /// 「CD 冻结中」的路易斯 PlayerId 集合。
        ///
        /// 需求：标记一个人之后 CD 显示成 30 但**不走**，直到目标死亡才开始走。
        /// 机制依据（客户端）：
        /// ```
        /// :42319  if (Managers.Game.SkillCooltime > 0) SkillCooltime--;   ← 客户端**每秒自己减 1**
        /// :42775  Handle_S_COOLTIME_SKILL ⇒ Managers.Game.SkillCooltime = pkt.Cooltime;  ← 包是**直接赋值**
        /// ```
        /// ⇒ 冻结 = **每秒把 30 重发一遍**（客户端减掉的那 1 被重置回去）；
        ///   目标死亡时把这个 pid 移出集合 ⇒ 停止重发 ⇒ 客户端自然从 30 开始每秒递减。
        /// </summary>
        private static readonly HashSet<int> FrozenCd = new HashSet<int>();

        /// <summary>
        /// 「本帧刚标记完、等着接管随后那次 `CoolSkill`」的路易斯 PlayerId（-1 = 无）。
        ///
        /// 时序依据：`UseActiveSkill`(:177213) 里 `UseTelekinesis`(:177240) 先跑，
        /// `if (num > 0) CoolSkill(num)`(:177248) 后跑 ——
        /// 所以 `UseTelekinesis` 的 Postfix 天然早于 `CoolSkill`，可以在这里留个记号，
        /// 让 `CoolSkill` 的 Prefix 知道"这一次是路易斯标记触发的"，从而接管它。
        /// </summary>
        private static int _pendingFreezePid = -1;


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

                int ownerPid = owner.PublicInfo.PlayerId;
                MarkedBy[targetId] = ownerPid;
                TraceUntil.Remove(ownerPid);
                NextTraceAt[ownerPid] = Now;   // 用技能后马上就能看到第一次追踪

                // 只有**真的标记上了**才接管 CD：原版 UseTelekinesis(:177576) 只在目标存活时
                // 才设置 `Owner.SkillState = targetId * -1`，用它判断成功与否。
                if (owner.SkillState < 0 && owner.SkillState * -1 == targetId)
                    _pendingFreezePid = ownerPid;     // 等本帧稍后的 CoolSkill(5) 被我们的 Prefix 接管
            }
        }

        // ── ①b 接管原版的 CoolSkill：把 Telekinesis 那 5 秒 CD 换成「冻结的 30 秒」 ──
        //
        // 原版：`UseActiveSkill`(:177213) 里 Telekinesis 分支给 `num = 5`（:177239），
        //       随后统一 `if (num > 0) CoolSkill(num)`（:177248）。
        //       `CoolSkill`(:177253) 会发 S_COOLTIME_SKILL 并挂一个"到点恢复可用"的作业。
        //
        // 我们要的语义：技能一用（标记成功）⇒ CD **显示 30 但不动**，直到目标死亡才开始走。
        // ⇒ 所以**必须拦截掉原版这次 CoolSkill**（否则它那个 5 秒作业会在 5 秒后把技能解锁，
        //   而且它发的值也会盖掉我们显示的 30），改成由我们在 `FrozenCd` 里冻结，
        //   等目标死亡（DeadHook）再发一次 30 并挂上解锁作业。
        [HarmonyPatch(typeof(SkillComponent), "CoolSkill")]
        internal static class CoolSkillHook
        {
            [HarmonyPrefix]
            private static bool Prefix(SkillComponent __instance)
            {
                int pending = _pendingFreezePid;
                if (pending < 0)
                    return true;                      // 不是我们接管的，走原版
                _pendingFreezePid = -1;                // 一次性记号，无论成败都清掉

                if (ModeRuntime.Bypass)
                    return true;

                var owner = __instance?.Owner;
                if (owner?.PublicInfo == null || owner.PublicInfo.PlayerId != pending)
                    return true;
                if (!IsLuis(owner))
                    return true;

                float recharge = RechargeSeconds?.Value ?? 30f;
                if (recharge <= 0f)
                    return true;                      // 配成 0 ⇒ 不干预 CD

                try
                {
                    owner.CanUseSkill = false;
                    owner.Session?.Send(new S_COOLTIME_SKILL { Cooltime = (int)recharge });
                    FrozenCd.Add(pending);
                    Plugin.Log.LogInfo(
                        $"[HS] LuisAbility：路易斯 #{pending} 标记成功，CD 冻结在 {recharge:F0} 秒（等目标死亡后开始走）。");
                }
                catch (global::System.Exception ex)
                {
                    Plugin.Log.LogWarning($"[HS] LuisAbility：冻结 CD 失败 — {ex.Message}");
                }

                return false;                         // 跳过原版 CoolSkill（不挂它那个 5 秒解锁作业）
            }
        }

        // ── ①c 冻结维持：每秒把 CD 值重发一遍，让客户端减掉的那 1 被重置回去 ──
        //
        // 客户端每秒 `SkillCooltime--`（:42319），而收包是直接赋值（:42775）——
        // 所以每秒重发就等于"冻结"。⚠ 两边都是 1Hz 且互不同步，最坏会闪一帧 29，
        // 下一 tick 就回到 30（人眼基本看不出）。
        [HarmonyPatch(typeof(GameRoom), "SurvivalTick")]
        internal static class FreezeHook
        {
            [HarmonyPostfix]
            private static void Postfix(GameRoom __instance)
            {
                if (ModeRuntime.Bypass || FrozenCd.Count == 0 || __instance?.Players == null)
                    return;
                if (__instance.State != EGameState.Survive)
                    return;

                float recharge = RechargeSeconds?.Value ?? 30f;
                if (recharge <= 0f)
                    return;

                List<int> stale = null;
                foreach (int pid in FrozenCd)
                {
                    GamePlayer owner = null;
                    for (int i = 0; i < __instance.Players.Count; i++)
                    {
                        var p = __instance.Players[i];
                        if (p?.PublicInfo != null && p.PublicInfo.PlayerId == pid) { owner = p; break; }
                    }

                    // 兜底解冻：本人不在了 / 已死 / 他记的那个目标已经不在标记表里
                    // （目标离线等异常情况下 OnDead 不会触发，不兜底会永久冻结）
                    if (owner?.Session == null || !owner.IsAlive || !HasLiveMarkOf(pid))
                    {
                        (stale ?? (stale = new List<int>())).Add(pid);
                        continue;
                    }

                    try { owner.Session.Send(new S_COOLTIME_SKILL { Cooltime = (int)recharge }); }
                    catch { }
                }

                if (stale == null)
                    return;
                foreach (int pid in stale)
                {
                    FrozenCd.Remove(pid);
                    Plugin.Log.LogInfo($"[HS] LuisAbility：路易斯 #{pid} 的 CD 冻结解除（标记已结束）。");
                }
            }

            /// <summary>该路易斯是否还持有至少一个"标记且目标仍活着"的记录。</summary>
            private static bool HasLiveMarkOf(int luisPid)
            {
                foreach (var kv in MarkedBy)
                    if (kv.Value == luisPid)
                        return true;
                return false;
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

                // 路易斯的技能转入周转 —— 也就是"目标死了，冻结的 CD 从这一刻开始走"
                float recharge = RechargeSeconds?.Value ?? 30f;
                if (recharge > 0f)
                {
                    // 先解冻：FreezeHook 每秒重发 30 的动作到此为止，客户端才会真的开始递减
                    bool wasFrozen = FrozenCd.Remove(luisPid);

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
                                Plugin.Log.LogInfo(
                                    $"[HS] LuisAbility：路易斯 #{luisPid} 的目标已死，CD 开始走 {recharge:F0} 秒"
                                    + (wasFrozen ? "（冻结已解除）。" : "。"));
                            }
                            catch (global::System.Exception ex)
                            {
                                Plugin.Log.LogWarning($"[HS] LuisAbility：技能周转失败 — {ex.Message}");
                            }
                            break;
                        }
                    }
                }
                else
                {
                    FrozenCd.Remove(luisPid);   // 不干预 CD 时也要清掉冻结记录，免得它一直重发
                }

                Plugin.Log.LogInfo(
                    $"[HS] LuisAbility：标记目标 #{deadPid} 已死亡，凶手 #{killer?.PublicInfo?.PlayerId ?? 0}"
                    + (killerIsLuis ? "（是路易斯自己，豁免）" : "，已施加时停与力竭") + "。");
            }
        }

        /// <summary>
        /// 给路易斯发一次"指向目标"的箭头。样式由 `ArrowStyle` 决定，两条通道的客户端链路完全不同：
        ///
        /// **Character（默认，红毛 ComplyRules 同款）** —— `S_PIN_MOVE{ Type = 目标的真实 PlayerId }`
        /// ```
        /// :42242 Handle_S_PIN_MOVE → :42246 UI_GameScene.RefreshComplyRulesPin
        ///   → :74747 SetComplyRulesArrow(pin)
        ///     → :13801 GetPlayerCache(target.ID)（**ID 必须是真实 PlayerId**，否则直接 return）
        ///       → :89667 贴图 = `{该角色}_Map_Black.sprite` ⇒ **箭头显示的是目标的角色**
        /// ```
        /// · 重复发**不需要**先撤：`SetComplyRulesArrow` 会 `Find` 复用同一个 `UI_Arrow`（:13807）
        /// · ★ **首包必须 `IsForce = true`**（`first` 变量）—— 客户端 :74749 分两条路：
        ///   `true` → `SetLocalPosition` 直接定位；`false` → `SetTargetPosition`，:86096 是 **0.1 秒
        ///   DOLocalMove 补间**。控件刚由 `CreatePin` 建出来时，它的位置与目标位置差着，
        ///   用 `false` 会先**从原地飘一下**才到位；之后窗口内的刷新才该用 `false` 平滑跟随。
        ///   原版红毛同此：标记那一刻 `isForce: true`（:177398），之后移动才 `false`（:175918）；
        ///   美幸那边也是这个做法（`MiyukiScanFeature.SendPinTracked` 用 HashSet.Add 判首包）。
        /// · ⚠ 副作用：同一个包也刷平板（`:42247` 是无条件的那一行），**打开平板时会多一个标记**
        ///   —— 客户端没有按 ID 分岔的逻辑，房主端避不开；不打开平板则看不到。
        /// · 撤销：`S_PIN_MOVE{ Type = 同一个 PlayerId, Pos = (0,0) }` ⇒ `DeletePin` + `RemoveComplyRulesArrow`
        ///   （删除走的是 `pos == Vector2.zero` 那条分支，在 `isForce` 判断**之前**就 return 了，故 `IsForce` 无意义）
        ///
        /// **Corpse** —— `S_NOTIFY_ARROW{ Type = CorpseArrow }`
        /// ```
        /// :42569 Handle_S_NOTIFY_ARROW → :13765 MyPlayer.SetArrow → UI_Arrow 通用分支
        ///   （:89719 每帧摆到"自己 + 朝目标 × 200"处并旋转朝目标）
        /// ```
        /// · ✅ 地图与平板**零痕迹**（平板副作用 `NoteCorpseArrow` 只在 Detective 阶段，:42573）
        /// · ⚠ 贴图只能是尸体/电箱/军械库那三种之一（`:89629` 的 switch），**拿不到角色贴图**
        /// · ⚠ 必须"先撤旧、再发新"：`SetArrow`(:13765) 每次调用都 `MakeWorldSpaceUI` **新建**一个
        ///   `UI_Arrow` 且 `Arrows.Add`，**没有去重** ⇒ 3 秒窗口内 10Hz 重发会堆出几百个箭头对象
        /// </summary>
        private static void SendTraceArrow(GamePlayer luis, GamePlayer target)
        {
            if (luis?.Session == null || luis.PublicInfo == null || target?.PublicInfo?.Pos == null)
                return;

            int luisPid = luis.PublicInfo.PlayerId;
            int targetPid = target.PublicInfo.PlayerId;
            try
            {
                // 「首包不补间」——与美幸那边同一个做法（`MiyukiScanFeature.SendPinTracked`）：
                // 客户端 RefreshComplyRulesPin 收到 pin 后，:74749 分两条路：
                //   IsForce=true  → SetLocalPosition 直接定位（新控件不会"从原地滑入"）
                //   IsForce=false → SetTargetPosition，:86096 是 0.1 秒 DOLocalMove 补间
                // 控件刚建出来时它的位置与目标位置差着，用 false 会先飘一下 ⇒ 首包必须 true。
                // 原版红毛也是这么做的：标记那一刻 `isForce: true`（:177398），之后移动才 false（:175918）。
                bool first = !TraceMarks.TryGetValue(luisPid, out var prevMark)
                             || prevMark.targetPid != targetPid;

                if (UseCharacterArrow())
                {
                    // 角色箭头：反复发同一个 (Type, Pos) 不会叠加，客户端会复用那一个 UI_Arrow
                    luis.Session.Send(new S_PIN_MOVE
                    {
                        Type = targetPid,
                        Pos = target.PublicInfo.Pos,
                        IsForce = first
                    });
                }
                else
                {
                    // 尸体箭头：先撤上一发（必须用上一发的**原值**精确匹配，:13779 是按值删的）
                    PosInfo prev = first ? null : prevMark.pos;
                    if (prev != null)
                    {
                        luis.Session.Send(new S_REMOVE_ARROW
                        {
                            Type = EArrowType.CorpseArrow,
                            Pos = prev
                        });
                    }

                    luis.Session.Send(new S_NOTIFY_ARROW
                    {
                        Type = EArrowType.CorpseArrow,
                        Pos = target.PublicInfo.Pos
                    });
                }

                // 记下"指向谁 + 这一发的位置"。位置必须克隆：PublicInfo.Pos 会被原地改写
                TraceMarks[luisPid] = (targetPid, target.PublicInfo.Pos.Clone());
            }
            catch (global::System.Exception ex)
            {
                Plugin.Log.LogWarning($"[HS] LuisAbility：追踪发包失败 — {ex.Message}");
            }
        }

        /// <summary>撤销某人身上的追踪箭头（按当前的 `ArrowStyle` 走对应的删除方式）。</summary>
        private static void ClearTraceArrow(GamePlayer target, int markTargetPid, PosInfo markPos)
        {
            if (target?.Session == null)
                return;
            try
            {
                if (UseCharacterArrow())
                {
                    // 删除哨兵：Pos = (0,0) ⇒ 客户端 DeletePin + RemoveComplyRulesArrow
                    target.Session.Send(new S_PIN_MOVE
                    {
                        Type = markTargetPid,
                        Pos = new PosInfo(),
                        IsForce = false
                    });
                }
                else if (markPos != null)
                {
                    target.Session.Send(new S_REMOVE_ARROW
                    {
                        Type = EArrowType.CorpseArrow,
                        Pos = markPos
                    });
                }
            }
            catch { }
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
                        // 注意不只是"停止重发" —— 箭头是死数据（或 pin），必须主动撤掉。
                        if (TraceMarks.TryGetValue(luisPid, out var deadMark))
                        {
                            TraceMarks.Remove(luisPid);
                            TraceUntil.Remove(luisPid);
                            ClearTraceArrow(luis, deadMark.targetPid, deadMark.pos);
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

                    // 发包细节见 SendTraceArrow：按 ArrowStyle 走 S_PIN_MOVE（角色箭头）或 S_NOTIFY_ARROW（尸体箭头），
                    SendTraceArrow(luis, target);   // 起步那一发；后续由 Move 钩子跟随
                }

                // 追踪到期 ⇒ 撤掉箭头（按 ArrowStyle 走对应的删除方式，见 ClearTraceArrow）
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

                    if (!TraceMarks.TryGetValue(luisPid, out var lastMark))
                        continue;                     // 没有发过，没什么可撤
                    TraceMarks.Remove(luisPid);

                    for (int i = 0; i < room.Players.Count; i++)
                    {
                        var p = room.Players[i];
                        if (p?.PublicInfo == null || p.PublicInfo.PlayerId != luisPid || p.Session == null)
                            continue;
                        ClearTraceArrow(p, lastMark.targetPid, lastMark.pos);
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
            FrozenCd.Clear();          // CD 冻结表同理：不清会把上局的"冻结"带进新局
            _pendingFreezePid = -1;    // 一次性记号，跨局也清掉
        }
    }
}
