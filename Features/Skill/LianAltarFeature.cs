using System.Collections.Generic;
using BepInEx.Configuration;
using HarmonyLib;
using Protocol;
using Server.Game;
using HideAndSeek.Core;
using HideAndSeek.Features.Vision;
using GamePlayer = Server.Game.Player;
using GameDtStage = Server.Game.DeadlyTrickStage;      // AGENTS 坑 #1：同名类，必须显式限定
using GameCabinet = Server.Game.Cabinet;
using GameDeviceManager = Server.Game.DeviceManager;    // AGENTS 坑 #1：同名类，服务端那个才有 Instance

namespace HideAndSeek.Features.Skill
{
    /// <summary>
    /// 莲 · DT 点（藏尸点）相关能力 —— **本次只实现第 ① 项**。
    ///
    /// ① 站在 DT 点范围内超过 <see cref="ArmSeconds"/> 秒时，若此时真的停电（或黑方处于
    ///    捉迷藏的黑灯视野下），就**只给莲本人**下发一份"这里没断电"的假光照，
    ///    让他的客户端放行断电时被禁用的交互 —— 具体说就是**能点蜡烛**。
    ///    离开 DT 点超过 <see cref="ReleaseSeconds"/> 秒后发回真实光照。
    ///
    /// 为什么"假光照"能解禁：客户端所有设备走同一条判据
    /// （`Assembly-CSharp:8098`）：
    ///     `if (State == Survive && !CanUseDarkness && Managers.Game.Darkness) → "DarknessError"`
    /// 而 `Darkness` 只由 `S_AREA_PUBLIC.IsLight` 驱动（客户端 :29592 无条件 `Darkness = !IsLight`）。
    /// ⇒ 单独给他一份 `IsLight = true`，他的 `Darkness` 就变 false，蜡烛类设备放行；
    ///   服务端 `Area.IsLight` 与其它玩家的视野**完全不受影响**。
    ///
    /// "DT 点"在代码里的真身：`Server.Game.DeadlyTrickStage`（`CanHideCorpse => true`，:162413）
    /// 与 `Cabinet`（:162011）—— 即魔法阵/柜子这类能藏尸的设备。坐标取 `DeviceInfo.Pos`。
    ///
    /// ⚠ 本类**不做**"解除"以外的收尾：一旦不满足条件（离开超时 / 不再停电 / 离开生存阶段），
    ///   必须把真实光照发回去，否则莲会永久看得见（假状态残留）。
    /// </summary>
    [PatchFeature("LianAltar",
        "莲（灵魂感知）的全部能力：DT 点假光照（站进藏尸点范围内只给本人下发「这里没断电」）、"
        + "尸体方向预警（有人死亡时只通知莲，指向尸体，可配时长与音效、到期主动撤回）、"
        + "感知死亡后的移速加成。",
        defaultEnabled: false, side: FeatureSide.Host)]
    internal static class LianAltarFeature
    {
        [ConfigField(3f, "站进 DT 点多少秒后开始生效。", Min = 0f, Max = 60f)]
        public static ConfigEntry<float> ArmSeconds;

        [ConfigField(1f, "离开 DT 点多少秒后失效并恢复真实光照。", Min = 0f, Max = 60f)]
        public static ConfigEntry<float> ReleaseSeconds;

        [ConfigField(350f, "DT 点的判定半径（游戏单位）。", Min = 50f, Max = 2000f)]
        public static ConfigEntry<float> DtRadius;

        /// <summary>能力一：站进 DT 点范围内 ⇒ 只给本人下发"这里没断电"的假光照（能点蜡烛）。</summary>
        [ConfigField(false, "【假光照】站进 DT 点范围内时，只给莲本人下发「这里没断电」（放行蜡烛交互）。")]
        public static ConfigEntry<bool> EnableVision;

        /// <summary>能力二：有人死亡 ⇒ 向**莲**（含偷到她技能的）指出尸体方向（CorpseArrow）。</summary>
        [ConfigField(true, "【尸体预警】有人死亡时，只通知莲（含偷到她技能的），向他指出尸体方向。")]
        public static ConfigEntry<bool> EnableTrace;

        /// <summary>
        /// 尸体预警的持续秒数。到期会**主动发删除包**把箭头撤掉
        /// （箭头没有自带寿命，见下方 <see cref="CorpseArrowHook"/> 的说明）。
        /// </summary>
        [ConfigField(20f, "尸体方向预警持续多少秒后消失。", Min = 1f, Max = 120f)]
        public static ConfigEntry<float> TraceSeconds;

        /// <summary>
        /// 尸体预警的音效（**默认 none = 不发声**）。
        ///
        /// 原版这个位置用的是 `WarningSfx` —— 但它同时是"尸体被发现"的全房通报音，
        /// 拿来做"有人死了"的提示会吵且容易与别的信息混淆，所以默认静音、留给你自选。
        /// 建议：`HandBellSfx`（手铃）· `PurpleCandleSfx`（紫蜡烛）· `IgniteSfx`（点燃）。
        /// </summary>
        [ConfigField("none", "尸体预警音效（ESoundType 名）。none = 不发声（默认）。")]
        public static ConfigEntry<string> TraceSfx;

        /// <summary>
        /// 尸体追踪用掉一次之后，莲的技能槽上**长期挂着**的冷却值。
        ///
        /// 需求原话是"长期显示为 -1（如果可行），或 13"。**-1 不可行** ——
        /// 客户端 `SkillCooltime` 的 setter 是 `_skillCooltime = Math.Max(0, value)`
        /// （`Assembly-CSharp:28836`），负数会被夹成 0，看不到 -1。
        /// 所以用备选的 13；并且每秒补发一次，否则它会按客户端本地计时一路减到 0。
        /// </summary>
        [ConfigField(13f, "尸体追踪用过一次后，莲技能槽长期显示的冷却值（0 = 不显示）。", Min = 0f, Max = 999f)]
        public static ConfigEntry<float> UsedCooldown;

        /// <summary>
        /// 尸体追踪警告的持续时间（秒）。需求是 **20 秒**。
        ///
        /// `S_NOTIFY_ARROW` 本身没有"时长"字段（客户端收到就画一个箭头，画完即止），
        /// 所以要"持续 20 秒"必须**每秒重发一次**。这里记录"到期时刻"来做这件事。
        /// </summary>

        /// <summary>莲 PlayerId → 进入 DT 点范围的时刻。用于判"站够 ArmSeconds"。</summary>
        private static readonly Dictionary<int, float> EnteredAt = new Dictionary<int, float>();

        /// <summary>莲 PlayerId → 离开 DT 点范围的时刻。用于判"离开够 ReleaseSeconds"。</summary>
        private static readonly Dictionary<int, float> LeftAt = new Dictionary<int, float>();

        /// <summary>当前正被施加"假光照"的莲 PlayerId 集合。</summary>
        private static readonly HashSet<int> Faked = new HashSet<int>();

        /// <summary>已经用掉一次尸体追踪的莲 PlayerId —— 他们的技能槽要长期挂冷却值。</summary>
        private static readonly HashSet<int> TraceUsed = new HashSet<int>();

        /// <summary>正在被追踪的尸体：尸体位置 + 到期时刻（SurviveTime）。每秒重发箭头用。</summary>
        private static readonly List<(PosInfo pos, float until)> TraceArrows =
            new List<(PosInfo, float)>();

        private static float Now => TimeManager.Instance?.SurviveTime ?? 0f;

        /// <summary>莲：本人是莲，或技能被换成了灵魂感知（Soi 的偷取）。与原版 :176085 同判据。</summary>
        private static bool IsLian(GamePlayer p)
            => p?.SkillComponent?.Data != null && p.SkillComponent.Data.Type == ESkillType.SoulSense;

        /// <summary>"DT 点"= 能藏尸的设备：DeadlyTrickStage（魔法阵那类）与 Cabinet（柜子）。</summary>
        private static bool IsDtDevice(Device d)
            => d is GameDtStage || d is GameCabinet;

        /// <summary>该玩家是否站在某个 DT 点的半径内。</summary>
        private static bool InDtRange(GamePlayer player)
        {
            var pos = player?.PublicInfo?.Pos;
            var devices = GameDeviceManager.Instance?.Objects;
            if (pos == null || devices == null)
                return false;

            float r = DtRadius?.Value ?? 350f;
            float r2 = r * r;

            for (int i = 0; i < devices.Count; i++)
            {
                var d = devices[i];
                if (d == null || !IsDtDevice(d))
                    continue;

                var dp = d.DeviceInfo?.Pos;
                if (dp == null)
                    continue;

                float dx = dp.X - pos.X, dy = dp.Y - pos.Y;
                if (dx * dx + dy * dy <= r2)
                    return true;
            }
            return false;
        }

        /// <summary>撤掉假光照，恢复真实值（无论之前是否施加过，都可安全调用）。</summary>
        private static void Release(GamePlayer player)
        {
            BlackVisionFeature.SendAreaLight(player, player?.CurrentArea?.IsLight ?? true);
        }

        // ── 有人死亡 ⇒ 与**原版路易斯**同款的"尸体追踪警告" ──
        //
        // 原版（:176033 OnDeadMurder）对被路易斯标记过的死者做两件事：
        //     BroadcastSystemSFX(WarningSfx)                          ← 全房警告音
        //     BroadcastAlivePlayers(S_NOTIFY_ARROW{CorpseArrow, Pos})  ← 指向**尸体**的箭头，给所有活人
        // 需求要莲的死亡提示与它一致（"因为莲追踪的是尸体，而不是人"），
        // 所以这里对**任何**死亡都做同样两件事，条件是"场上有一位莲"。
        //
        // 挂 OnDeadMurder 而不是 OnDead：前者是"尸体已经建立"的时刻，
        // 且它的 PublicInfo.Pos 就是尸体位置。
        [HarmonyPatch(typeof(GamePlayer), "OnDeadMurder")]
        internal static class CorpseArrowHook
        {
            [HarmonyPostfix]
            private static void Postfix(GamePlayer __instance)
            {
                if (ModeRuntime.Bypass || __instance?.PublicInfo == null)
                    return;
                if (EnableTrace == null || !EnableTrace.Value)
                    return;                              // 段内开关：尸体预警可以单独关掉

                var room = GameRoom.Instance;
                if (room?.Players == null || room.State != EGameState.Survive)
                    return;

                // 场上得有**活着的**莲（技能持有者，含被偷取的）才触发；她不在就没人需要这个信息
                bool hasLian = false;
                for (int i = 0; i < room.Players.Count; i++)
                {
                    var lp = room.Players[i];
                    if (lp?.PublicInfo != null && lp.IsAlive && IsLian(lp)) { hasLian = true; break; }
                }
                if (!hasLian)
                    return;

                try
                {
                    // 音效可配、**默认不发声** —— 原版这个位置是 WarningSfx，
                    // 但它同时是"尸体被发现"的全房通报音，拿来做死亡提示既吵又易混淆。
                    string traceSfx = TraceSfx?.Value;
                    if (!string.IsNullOrEmpty(traceSfx)
                        && !traceSfx.Equals("none", global::System.StringComparison.OrdinalIgnoreCase)
                        && global::System.Enum.TryParse(traceSfx, true, out ESoundType ts))
                    {
                        room.BroadcastSystemSFX(ts);
                    }
                    // ⚠ 收件人：**只有莲**（含偷到她技能的）—— 不是全场。
                    // 这是"感知死亡"角色的专属情报：她知道有人死了、尸体在哪，别人不知道。
                    //
                    // ⚠ 坐标必须**值拷贝**：客户端 RemoveArrow(:13774) 是按
                    // `(Type, TargetPos.x, TargetPos.y)` **精确匹配**才会删，
                    // 而 TargetPos 是 SetArrow 时那一刻的快照。
                    // 若这里直接引用 `PublicInfo.Pos`，那具尸体之后被搬动时我们记下的"坐标"也跟着变，
                    // 删除就永远匹配不上 ⇒ 箭头留在屏幕上消不掉（实测到的"骷髅头一直不消失"）。
                    var corpsePos = new PosInfo { X = __instance.PublicInfo.Pos.X, Y = __instance.PublicInfo.Pos.Y };
                    var arrow = new S_NOTIFY_ARROW
                    {
                        Type = EArrowType.CorpseArrow,
                        Pos = corpsePos
                    };
                    int sentTo = 0;
                    for (int i = 0; i < room.Players.Count; i++)
                    {
                        var lianP = room.Players[i];
                        if (lianP?.PublicInfo == null || lianP.Session == null || !lianP.IsAlive)
                            continue;
                        if (!IsLian(lianP))
                            continue;                    // 只有莲
                        try { lianP.Session.Send(arrow); sentTo++; }
                        catch { }
                    }
                    // 登记"到期时刻 + 那一发的坐标快照"，供到期时按同值删掉
                    TraceArrows.Add((corpsePos, Now + (TraceSeconds?.Value ?? 20f)));
                    // 这次追踪"用掉"了：所有莲进入"技能槽长期显示冷却值"的状态
                    for (int i = 0; i < room.Players.Count; i++)
                    {
                        var lian = room.Players[i];
                        if (IsLian(lian) && lian.PublicInfo != null)
                            TraceUsed.Add(lian.PublicInfo.PlayerId);
                    }

                    Plugin.Log.LogInfo(
                        $"[HS] LianAltar：死者 #{__instance.PublicInfo.PlayerId} 的尸体追踪警告已广播（莲在场上）。");
                }
                catch (global::System.Exception ex)
                {
                    Plugin.Log.LogWarning($"[HS] LianAltar：尸体追踪警告失败 — {ex.Message}");
                }
            }
        }

        // ── 每秒检查一次：进入/满足条件/离开三段 ──
        [HarmonyPatch(typeof(GameRoom), "SurvivalTick")]
        internal static class TickHook
        {
            [HarmonyPostfix]
            private static void Postfix(GameRoom __instance)
            {
                if (ModeRuntime.Bypass || __instance?.Players == null)
                    return;
                if (__instance.State != EGameState.Survive)
                    return;                          // 只在生存阶段干预；其它阶段交还原版

                // 尸体追踪：需求要"20 秒"的箭头，包本身没有时长 ⇒ 每秒重发一次
                if (TraceArrows.Count > 0)
                {
                    float tn = Now;
                    for (int i = TraceArrows.Count - 1; i >= 0; i--)
                    {
                        var a = TraceArrows[i];
                        if (tn >= a.until)
                        {
                            // 到期：箭头**不会自己消失**（SetArrow 没有时长参数），
                            // 必须主动发 S_REMOVE_ARROW（:42579 → MyPlayer.RemoveArrow）。
                            TraceArrows.RemoveAt(i);
                            try
                            {
                                // 删除包也只能发给莲 —— 否则全房都会收到一个"删除箭头"（虽然他们本来就没有）
                                var rm = new S_REMOVE_ARROW
                                {
                                    Type = EArrowType.CorpseArrow,
                                    Pos = a.pos
                                };
                                for (int k = 0; k < __instance.Players.Count; k++)
                                {
                                    var lp = __instance.Players[k];
                                    if (lp?.PublicInfo == null || lp.Session == null || !IsLian(lp))
                                        continue;
                                    try { lp.Session.Send(rm); } catch { }
                                }
                            }
                            catch { }
                            continue;
                        }
                        // ⚠ 这里**不能重发**：客户端 SetArrow(:13765) 每次都**新建一个箭头对象**并追加进列表，
                        // 而 RemoveArrow(:13774) 只删"坐标完全相同"的那一个 ⇒ 每秒重发会造出一堆箭头，
                        // 到期只能删掉最后那一个，其余永远留在屏幕上（"长舌头"就是这么来的）。
                    }
                }

                // 尸体追踪用过一次 ⇒ 技能槽长期挂着冷却值。
                // 必须**每秒补发**：客户端收到后会按本地计时一路递减（:42319-42321），
                // 只发一次的话几秒后就归零了，"长期显示"就没了。
                if (TraceUsed.Count > 0)
                {
                    float cd = UsedCooldown?.Value ?? 13f;
                    if (cd > 0f)
                    {
                        for (int i = 0; i < __instance.Players.Count; i++)
                        {
                            var lp = __instance.Players[i];
                            if (lp?.PublicInfo == null || lp.Session == null)
                                continue;
                            if (!TraceUsed.Contains(lp.PublicInfo.PlayerId) || !IsLian(lp))
                                continue;
                            try
                            {
                                lp.CanUseSkill = false;
                                lp.Session.Send(new S_COOLTIME_SKILL { Cooltime = (int)cd });
                            }
                            catch { /* 单个失败不影响其它人 */ }
                        }
                    }
                }

                // ⚠ 假光照有自己的开关（EnableVision），守卫**只能放在这一段之前**。
                // 绝不能再往上挪到方法开头 —— 上面的"箭头到期删除"和"技能槽冷却补发"
                // 都不属于假光照：一旦被这个守卫挡住，删除包就永远不会发出，
                // 箭头会一直留在屏幕上（实测到的"骷髅头一直不消失"正是这个原因）。
                if (EnableVision == null || !EnableVision.Value)
                    return;

                float now = Now;
                float arm = ArmSeconds?.Value ?? 3f;
                float release = ReleaseSeconds?.Value ?? 1f;

                for (int i = 0; i < __instance.Players.Count; i++)
                {
                    var p = __instance.Players[i];
                    if (p?.PublicInfo == null || p.Session == null || !IsLian(p))
                        continue;

                    int pid = p.PublicInfo.PlayerId;
                    bool inside = InDtRange(p);

                    if (inside)
                    {
                        LeftAt.Remove(pid);
                        if (!EnteredAt.ContainsKey(pid))
                            EnteredAt[pid] = now;

                        // 站够了 + 确实处在"看不见"的状态（真停电，或被黑灯压着）才需要解禁
                        if (now - EnteredAt[pid] < arm)
                            continue;
                        if (!GameRefs.IsRealBlackout(p) && !Faked.Contains(pid))
                            continue;                // 本来就亮着：什么都不用做

                        if (Faked.Add(pid))
                        {
                            BlackVisionFeature.SendAreaLight(p, true);
                            Plugin.Log.LogInfo($"[HS] LianAltar：莲 #{pid} 在 DT 点内，已下发假光照（可点蜡烛）。");
                        }
                    }
                    else
                    {
                        EnteredAt.Remove(pid);
                        if (!Faked.Contains(pid))
                            continue;                // 没被施加过，不需要收尾

                        if (!LeftAt.ContainsKey(pid))
                            LeftAt[pid] = now;
                        if (now - LeftAt[pid] < release)
                            continue;

                        LeftAt.Remove(pid);
                        Faked.Remove(pid);
                        Release(p);
                        Plugin.Log.LogInfo($"[HS] LianAltar：莲 #{pid} 已离开 DT 点，恢复真实光照。");
                    }
                }
            }
        }

        // ══════════════════════════════════════════════════════════════════════
        // 灵魂感知加速（原 [SoulSense] 段，现并入本段 —— 同源能力归同一段）
        //
        // 原版机制（Assembly-CSharp:176080-176090）：有人死时 `SendDeadNotify` 会给**所有活着的**
        // `ESkillType.SoulSense` 玩家发一个 `S_NOTIFY_DEAD` 包（客户端据此放蜡烛图标）。
        // 我们不改技能本身，只在那条通知上"搭一层"：谁收到了通知，就给他一段加速。
        //
        // ⚠ 速度必须挂在 `BuffComponent.RefreshSpeed` 的 **Postfix**：
        // 原方法会用 `560 * delta` 重算速度，Prefix 里改会被覆盖。
        // ⚠ 幽灵要跳过：`MakeSpectatorGhost` 会置 `State = Hide` 并直接设速度。
        // ══════════════════════════════════════════════════════════════════════

        [ConfigField(30f, "【灵魂感知】每次感知到死亡后，移速加成的持续秒数。", Min = 1f, Max = 300f)]
        public static ConfigEntry<float> SoulBoostSeconds;

        [ConfigField(1.5f, "【灵魂感知】感知死亡后的移速倍率。1.5 = 快 50%。", Min = 1f, Max = 3f)]
        public static ConfigEntry<float> SoulBoostMul;

        /// <summary>PlayerId → 灵魂感知加速的到期时刻（取 SurviveTime）。</summary>
        private static readonly Dictionary<int, float> SoulUntil = new Dictionary<int, float>();

        /// <summary>本局内被加持过的人，用于到期时主动重算速度。</summary>
        private static readonly HashSet<int> SoulBoosted = new HashSet<int>();

        /// <summary>这名玩家此刻是否拥有「灵魂感知」。与原版 :176085 用同一个判据。</summary>
        private static bool HasSoulSense(GamePlayer p)
        {
            try
            {
                return p?.SkillComponent?.Data != null
                    && p.SkillComponent.Data.Type == ESkillType.SoulSense;
            }
            catch { return false; }
        }

        // ── 触发：原版给莲发"有人死了"的那一刻（`SendDeadNotify` 是 private，用字符串定位）──
        [HarmonyPatch(typeof(GamePlayer), "SendDeadNotify")]
        internal static class SoulDeadNotifyHook
        {
            [HarmonyPostfix]
            private static void Postfix()
            {
                Diagnostics.Hit("LianAltar");
                if (ModeRuntime.Bypass)
                    return;

                var room = GameRoom.Instance;
                if (room?.AlivePlayers == null)
                    return;

                float now = Now;
                float secs = SoulBoostSeconds?.Value ?? 30f;

                // 只给**活着**的莲续期（与 SendDeadNotify 的发送条件一致：它也只发给 AlivePlayers）
                for (int i = 0; i < room.AlivePlayers.Count; i++)
                {
                    var p = room.AlivePlayers[i];
                    if (p?.PublicInfo == null || !HasSoulSense(p))
                        continue;

                    // 重复触发 ⇒ **刷新时长，不叠乘**（一波团灭时最多保持"一直有加速"）
                    SoulUntil[p.PublicInfo.PlayerId] = now + secs;
                }
            }
        }

        // ── 加速：与 SpeedBoostFeature 同一挂点（Postfix），乘法叠加 ──
        [HarmonyPatch(typeof(BuffComponent), nameof(BuffComponent.RefreshSpeed))]
        internal static class SoulRefreshSpeedHook
        {
            [HarmonyPostfix]
            private static void Postfix(BuffComponent __instance)
            {
                if (ModeRuntime.Bypass)
                    return;

                float mul = SoulBoostMul?.Value ?? 1.5f;
                if (global::System.Math.Abs(mul - 1f) < 0.001f)
                    return;                       // 1.0 不动，省一次广播

                var player = __instance?.Owner;
                if (player == null || !player.IsAlive || player.State == EPlayerState.Hide)
                    return;                       // 幽灵：原版直接设速度，别掺和

                int pid = player.PublicInfo?.PlayerId ?? 0;
                if (pid == 0 || !SoulUntil.TryGetValue(pid, out float until) || Now >= until)
                    return;                       // 不在加速期内

                player.PrivateInfo.Speed *= mul;
                player.SendChangeSpeed();
            }
        }

        // ── 到期兜底：加速只在 RefreshSpeed 的 Postfix 里生效，而那个方法只在
        //        "速度需要重算"（移动/状态变化）时才被调用 ⇒ 站着不动时到期不会自动恢复。
        //        所以每秒检查"刚刚过期的人"，主动让他重算一次速度。 ──
        [HarmonyPatch(typeof(GameRoom), "SurvivalTick")]
        internal static class SoulTickHook
        {
            [HarmonyPostfix]
            private static void Postfix()
            {
                if (ModeRuntime.Bypass || SoulUntil.Count == 0)
                    return;

                var room = GameRoom.Instance;
                if (room?.Players == null)
                    return;

                float now = Now;
                for (int i = 0; i < room.Players.Count; i++)
                {
                    var p = room.Players[i];
                    if (p?.PublicInfo == null)
                        continue;

                    int pid = p.PublicInfo.PlayerId;
                    if (!SoulUntil.TryGetValue(pid, out float until))
                        continue;
                    if (now < until)
                    {
                        SoulBoosted.Add(pid);
                        continue;                     // 还在加速期内
                    }

                    // 刚过期：清记录 + 主动重算一次速度（否则站着不动会一直挂着加成）
                    SoulUntil.Remove(pid);
                    if (SoulBoosted.Remove(pid) && p.IsAlive && p.State != EPlayerState.Hide)
                    {
                        try { p.BuffComponent?.RefreshSpeed(); }
                        catch (global::System.Exception ex)
                        {
                            Plugin.Log.LogWarning($"[HS] LianAltar：灵魂感知恢复速度失败 — {ex.Message}");
                        }
                    }
                }
            }
        }

        private static void ClearSoulBoost()
        {
            // 记的是 SurviveTime，而它每局被 ResetSurvival() 设回 420（不是从 0）——
            // 不清的话上一局的时刻在新局会算出负数，加速永不过期。
            SoulUntil.Clear();
            SoulBoosted.Clear();
        }

        // ── 跨局清理，并给仍在"假光照"里的人收尾 ──
        [HarmonyPatch(typeof(GameRoom), "StartSurvive")]
        internal static class StartHook
        {
            [HarmonyPostfix]
            private static void Postfix() => Cleanup(restore: false);
        }

        [HarmonyPatch(typeof(GameRoom), "StartLobby")]
        internal static class LobbyHook
        {
            [HarmonyPostfix]
            private static void Postfix() => Cleanup(restore: true);
        }

        private static void Cleanup(bool restore)
        {
            if (restore)
            {
                var room = GameRoom.Instance;
                if (room?.Players != null)
                {
                    for (int i = 0; i < room.Players.Count; i++)
                    {
                        var p = room.Players[i];
                        if (p?.PublicInfo != null && Faked.Contains(p.PublicInfo.PlayerId))
                            Release(p);
                    }
                }
            }

            // 三个表记的都是 PlayerId 与 SurviveTime；SurviveTime 每局被 ResetSurvival() 设回 420，
            // 不清会算出负数 ⇒ 判定永久成立（这一类坑今天已踩过多次）。
            EnteredAt.Clear();
            LeftAt.Clear();
            Faked.Clear();
            TraceUsed.Clear();
            TraceArrows.Clear();
            ClearSoulBoost();       // 灵魂感知加速同理（同段的功能一起清）
        }
    }
}
