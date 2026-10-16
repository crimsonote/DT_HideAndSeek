using System.Collections.Generic;
using BepInEx.Configuration;
using HarmonyLib;
using Protocol;
using Server.Game;
using HideAndSeek.Core;
using GamePlayer = Server.Game.Player;
using GameCabinet = Server.Game.Cabinet;
using GameDeviceManager = Server.Game.DeviceManager;
using GameSkill = Server.Game.SkillComponent;

namespace HideAndSeek.Features.Combat
{
    /// <summary>
    /// 柜子（Jeremy）能力包 —— 三项改动并入同一个配置段，一个总开关 + 三个子开关。
    ///
    /// ① 处决自爆 <c>[JeremyBurst]</c>：当且仅当「死者是 Jeremy」且「DT 处决建筑是柜子」时，
    ///    在死亡点引发一次类矿山自爆：爆炸音效 + <c>EBuffType.Stun</c> 晕眩半径内所有人。
    ///    两个判据都取原版自己写下的标记，不自己记状态：
    ///      · 处决建筑 —— <see cref="GamePlayer.DeathCabinet"/>。它由 DT 柜子处决
    ///        <c>ExecuteCabinetTrick</c>（ACS-0.1.17a:178906）写入；池塘 / 神秘处决写的
    ///        是另外两组字段（<c>DeathPondStageId</c> :178962 / <c>DeathOccultStageId</c>），
    ///        所以「DeathCabinet 非空」恰好等价于「这次 DT 走的是柜子」。
    ///      · 角色 —— <c>PublicInfo.CharacterId == 104</c>（Jeremy；Noel 是黑洞传送
    ///        <c>UseTeleport</c>，别混）。
    ///    挂 <c>OnDeadMurder</c> 的 **Prefix**：原版在该方法体会读并**当场清空**
    ///    <c>DeathCabinet</c>（:178375-178378），Postfix 读不到。
    ///    打断分析：DT 柜子处决链路 ExecuteCabinetTrick → StartDeadlyTrickCommon
    ///    → PushAfter(1767ms) → OnDead → OnDeadMurder，到达钩子时演出已放完、尸体已建立；
    ///    凶手的挥刀早在 <c>OnDamaged</c> 的 400ms 回调里一次性结算完且原版只给它发包与
    ///    Footprint debuff，没有任何状态锁。本钩子只发两个广播包，不改任何玩家状态。
    ///    <c>Stun</c> 只是入 BuffList + 客户端播眩晕特效，不重置状态机 —— 原版矿山自爆
    ///    （Mineral.HandleEvent 失败分支 :168094）就是这么用的。
    ///
    /// ② 出柜冲刺 <c>[JeremyExitBoost]</c>：Jeremy **主动**离开柜子后短时间移速提升。
    ///    加速原理与 <c>SodaBoostFeature</c> 同一套（未上浮：那边带申领 / 配额，
    ///    这边只给一次，规模差得远）：速度是客户端本地权威，
    ///    <c>BuffComponent.RefreshSpeed</c> 是唯一来源 ⇒ 在它的 Postfix 里乘倍率。
    ///    触发点挂 <c>Cabinet.Interact</c> 的 <c>ExitCabinet</c> 分支（:164219），
    ///    只认「本人按了出柜」，天然排除另外两条也会调 <c>ExitCabinet(player)</c> 的路径 ——
    ///    别人开柜把里面的人踢出来（:164238）与柜子间 <c>WarpPlayer</c>（:164243）。
    ///
    /// ③ 柜子传送前摇 <c>[JeremyWarp]</c>（新增）：Jeremy 的柜子转移是**被动技能**
    ///    <c>ESkillType.MindControl</c>（服务端 :179471 挂 <c>EBuffType.QuantumLeap</c>
    ///    3600 秒常驻；<c>UseSkill</c> :179564 对它绕过 <c>CanUseSkill</c> 与冷却，
    ///    直接调 <c>UseMindControl</c> :179575），链路是：
    ///        躲进柜子 → 客户端 MyPlayer.StartState(Hide)（:10206）见 IsQuantumLeap
    ///        → UI_GameScene.SetBlackPanel(true)（:76530）
    ///            → 黑屏淡入 **3 秒**（DOFade(1f, 3f)，:76536）
    ///            → AppendCallback 里**客户端自动发 C_USE_SKILL**（:76538）
    ///        → 服务端 UseMindControl → GetPairCabinet 找同色柜（:166207）→ WarpPlayer
    ///    本功能把这段"等待"搬到服务端手里，可缩短也可延长：
    ///      · <c>HideCabinet</c> Postfix 记账 + 按 <c>WarpDelayMs</c> 排传送；
    ///      · <c>UseSkill</c> Prefix 在屏蔽窗口内拦掉客户端那次自动发包（否则服务端传完后
    ///        客户端 3 秒后的发包会把人再送一格 —— 连续传送）；
    ///      · 传送完成后主动写 <c>SkillState = 0</c>（其 setter :177769 会
    ///        SendChangeSkillState ⇒ 客户端 ChangeSkillState :76622 → SetBlackPanel(false)
    ///        把黑屏 Tween Kill 掉）—— 这就是"提前结束黑屏"的关键，纯服务端即可。
    ///    ⚠ 延时的第四条路：这条链的 3 秒不在任何 PushAfter / WaitForSeconds 里，
    ///      而在客户端 UI 的 **DOTween 时长 + AppendCallback** 里（且发包藏在回调里）。
    ///      查"某功能有没有延时"时必须连 UI 层的 Tween 一起看。
    /// </summary>
    [PatchFeature("Jeremy",
        "柜子（Jeremy）能力包：处决自爆 + 主动出柜冲刺 + 柜子传送前摇可调（可缩短/延长）。",
        defaultEnabled: true, side: FeatureSide.Host)]
    internal static class JeremyFeature
    {
        // ══════════════════ 通用 ══════════════════

        [ConfigField(0,
            "额外限定角色 ID（0 = 不限制）。能力判据只看 EBuffType.QuantumLeap —— " +
            "任何拿到柜子能力的角色都生效（含被小偷角色窃取），需要限定回原角色时填 104。",
            Min = 0f, Max = 999f)]
        public static ConfigEntry<int> CharacterId;

        // ══════════════════ ① 处决自爆 ══════════════════

        [ConfigField(true, "处决自爆开关。关掉只影响自爆，出柜冲刺与传送前摇不受影响。")]
        public static ConfigEntry<bool> BurstEnabled;

        [ConfigField(5000f,
            "自爆晕眩毫秒数。原版矿山自爆为 3000；5~6 秒是「够黑方刀一个人、不够清场」的量级，10 秒起进团灭窗口。",
            Min = 0f, Max = 10000f)]
        public static ConfigEntry<float> StunMs;

        [ConfigField(256f, "自爆半径（游戏单位）。原版矿山自爆为 224。", Min = 32f, Max = 1024f)]
        public static ConfigEntry<float> Radius;

        [ConfigField(true, "是否播放爆炸音效（传播半径 672，与原版一致）。")]
        public static ConfigEntry<bool> PlaySfx;

        // ══════════════════ ② 出柜冲刺 ══════════════════

        [ConfigField(true, "出柜冲刺开关。")]
        public static ConfigEntry<bool> ExitBoostEnabled;

        [ConfigField(5f, "出柜冲刺持续秒数。", Min = 0.5f, Max = 60f)]
        public static ConfigEntry<float> Seconds;

        [ConfigField(1.6f,
            "出柜冲刺移速倍率。1.6 = 160%；与 SpeedBoost.BlackSpeedMul 叠乘。",
            Min = 1f, Max = 3f)]
        public static ConfigEntry<float> SpeedMul;

        // ══════════════════ ③ 柜子传送前摇 ══════════════════

        [ConfigField(true, "柜子传送前摇调整开关。")]
        public static ConfigEntry<bool> WarpEnabled;

        [ConfigField(3000f,
            "躲柜后到转移至同色柜子的毫秒数。3000 = 与原版一致（客户端黑屏 3 秒）；" +
            "调小即缩短（0 = 躲柜即走），调大即延长。",
            Min = 0f, Max = 15000f)]
        public static ConfigEntry<float> WarpDelayMs;

        // ══════════════════ 状态 ══════════════════

        /// <summary>PlayerId → 出柜冲刺到期时刻（TimeManager.SurviveTime 秒）。</summary>
        private static readonly Dictionary<int, float> Active = new Dictionary<int, float>();

        /// <summary>PlayerId → 柜子转移的「应传时刻」（SurviveTime 秒）。</summary>
        private static readonly Dictionary<int, float> WarpDue = new Dictionary<int, float>();

        /// <summary>PlayerId → 转移应从哪个柜子出发（防止把玩家从别的柜子送走）。</summary>
        private static readonly Dictionary<int, GameCabinet> WarpFrom = new Dictionary<int, GameCabinet>();

        /// <summary>
        /// PlayerId → 屏蔽客户端自动发包的截止时刻（SurviveTime 秒）。
        /// 客户端在躲柜后固定 3 秒发一次 <c>C_USE_SKILL</c>（黑屏 AppendCallback），
        /// 窗口覆盖 max(3000, WarpDelayMs) 再加余量，保证那次发包一定被拦。
        /// </summary>
        private static readonly Dictionary<int, float> WarpSuppress = new Dictionary<int, float>();

        private static float Now => TimeManager.Instance?.SurviveTime ?? 0f;

        /// <summary>
        /// 是否"拥有柜子能力"。
        ///
        /// 判据是**能力**而非角色：柜子的传送是被动技能 <c>ESkillType.MindControl</c>，
        /// 服务端 :179471 给持有者挂 <c>EBuffType.QuantumLeap</c>（3600 秒常驻）——
        /// 这个 buff 才是"谁有柜子能力"的唯一标识，小偷角色窃取到能力时同样带着它，
        /// 三项改动（自爆 / 出柜冲刺 / 传送前摇）都应跟着能力走。
        /// <c>CharacterId</c> 只作额外的白名单限制（0 = 不限制）。
        /// </summary>
        private static bool HasCabinetPower(GamePlayer p)
        {
            if (p?.PublicInfo == null)
                return false;
            if (p.BuffComponent == null || !p.BuffComponent.HasBuff(EBuffType.QuantumLeap))
                return false;
            int want = CharacterId?.Value ?? 0;
            return want <= 0 || p.PublicInfo.CharacterId == want;
        }

        // ══════════════════ ① 处决自爆 ══════════════════

        /// <summary>
        /// 钉 <c>Priority.First</c>：DT_Tools 新版也 hook 了 <c>OnDeadMurder</c> 的
        /// Prefix（check-upstream-overlap 实测），按 AGENTS.md 第 9 条必须显式定序。
        /// 取向往「最先结算」：本钩子纯读标记 + 发广播包，与上游互不读写；
        /// 而清空 <c>DeathCabinet</c> 的只有**原版方法体**，所以无论上游那个 Prefix
        /// 是否 return false，我们都能读到标记。
        /// </summary>
        [HarmonyPatch(typeof(GamePlayer), "OnDeadMurder")]
        [HarmonyPriority(Priority.First)]
        internal static class BurstHook
        {
            [HarmonyPrefix]
            private static void Prefix(GamePlayer __instance)
            {
                Diagnostics.Hit("Jeremy");
                if (ModeRuntime.Bypass)
                    return;
                if (!(BurstEnabled?.Value ?? true))
                    return;
                if (__instance?.PublicInfo == null)
                    return;

                // 判据一：处决建筑是柜子（原版写下的标记，这一刻还未被清空）
                if (__instance.DeathCabinet == null)
                    return;

                // 判据二：死者是 Jeremy
                if (!HasCabinetPower(__instance))
                    return;

                var room = GameRoom.Instance;
                if (room == null)
                    return;

                var pos = __instance.PublicInfo.Pos;
                if (pos == null)
                    return;

                float ms = StunMs?.Value ?? 5000f;
                float radius = Radius?.Value ?? 256f;

                if (PlaySfx?.Value ?? true)
                    room.BroadcastWorldSFX(ESoundType.ExplosionSfx, pos, 672f);

                // 无差别 AOE：不分阵营，落在半径内的 AlivePlayers 都吃（与矿山自爆同款语义）。
                // 柜子本人此刻 IsAlive 已是 false（OnDead 第二行设的 :175974），不会吃到自己的爆炸。
                if (ms > 0f && radius > 0f)
                    room.BroadcastBuff(EBuffType.Stun, (int)ms, pos, radius);

                Plugin.Log.LogInfo(
                    $"[HS] Jeremy：柜子 #{__instance.PublicInfo.PlayerId} 死于柜子处决，" +
                    $"爆炸晕眩 {ms:F0}ms / r={radius:F0}。");
            }
        }

        // ══════════════════ ② 出柜冲刺 ══════════════════

        [HarmonyPatch(typeof(GameCabinet), nameof(GameCabinet.Interact))]
        internal static class ExitInteractHook
        {
            [HarmonyPostfix]
            private static void Postfix(GamePlayer player, Packet pkt)
            {
                Diagnostics.Hit("Jeremy");
                if (ModeRuntime.Bypass)
                    return;
                if (!(ExitBoostEnabled?.Value ?? true))
                    return;
                if (player?.PublicInfo == null)
                    return;

                // 只认「本人按了出柜」：pkt 是 C_INTERACT_CABINET 且 Type == ExitCabinet。
                // pkt 可能带着别的包类型进来，as 失败即 null。
                var req = pkt?.Pkt as C_INTERACT_CABINET;
                if (req == null || req.Type != ECabinetInteractType.ExitCabinet)
                    return;

                if (!HasCabinetPower(player))
                    return;
                if (!player.IsAlive)
                    return;

                float seconds = Seconds?.Value ?? 5f;
                int pid = player.PublicInfo.PlayerId;
                Active[pid] = Now + seconds;

                // 立刻重算并下发：否则要等下一次 RefreshSpeed（站定 / 起跑）才看得到效果
                player.BuffComponent?.RefreshSpeed();
                player.SendChangeSpeed();

                Plugin.Log.LogInfo(
                    $"[HS] Jeremy：柜子 #{pid} 主动出柜，{seconds:F1} 秒内移速 ×{SpeedMul?.Value ?? 1.6f:F2}。");
            }
        }

        [HarmonyPatch(typeof(BuffComponent), nameof(BuffComponent.RefreshSpeed))]
        internal static class RefreshSpeedHook
        {
            [HarmonyPostfix]
            private static void Postfix(BuffComponent __instance)
            {
                if (ModeRuntime.Bypass || !(ExitBoostEnabled?.Value ?? true))
                    return;

                var player = __instance?.Owner;
                if (player?.PublicInfo == null)
                    return;
                // 幽灵：MakeSpectatorGhost 置 Hide 并直接设速度，别掺和
                if (!player.IsAlive || player.State == EPlayerState.Hide)
                    return;

                // 阶段闸门：只在生存阶段加成。
                // 不能只靠「到期时清理」—— 那是挂在 GameRoom.SurvivalTick 上的，
                // 对局一结束 SurvivalTick 自己就不跑了，残留会让加成挂到结算阶段。
                var room = GameRoom.Instance;
                if (room == null || room.State != EGameState.Survive)
                    return;

                float expireAt;
                if (!Active.TryGetValue(player.PublicInfo.PlayerId, out expireAt) || Now >= expireAt)
                    return;

                player.PrivateInfo.Speed *= SpeedMul?.Value ?? 1.6f;
                player.SendChangeSpeed();
            }
        }

        [HarmonyPatch(typeof(GameRoom), "SurvivalTick")]
        internal static class TickHook
        {
            [HarmonyPostfix]
            private static void Postfix()
            {
                if (ModeRuntime.Bypass)
                    return;
                ExpireActive();
                RunDueWarp();
            }
        }

        // ══════════════════ ③ 柜子传送前摇 ══════════════════

        /// <summary>
        /// 躲柜即记账。原版 <c>HideCabinet</c> 是 private，用字符串定位（AGENTS.md）。
        /// 它只有一个调用点（<c>Cabinet.Interact</c> 的 HideCabinet 分支 :164215），
        /// 所以「躲柜子」与「本钩子」一一对应。
        /// </summary>
        [HarmonyPatch(typeof(GameCabinet), "HideCabinet")]
        internal static class HideCabinetHook
        {
            [HarmonyPostfix]
            private static void Postfix(GameCabinet __instance, GamePlayer player)
            {
                Diagnostics.Hit("Jeremy");
                if (ModeRuntime.Bypass)
                    return;
                if (!(WarpEnabled?.Value ?? true))
                    return;
                if (player?.PublicInfo == null || !player.IsAlive)
                    return;

                // 只有带柜子转移被动（QuantumLeap）的才管；判据统一走能力而非角色
                if (!HasCabinetPower(player))
                    return;

                var room = GameRoom.Instance;
                if (room == null || room.State != EGameState.Survive)
                    return;

                float delayMs = WarpDelayMs?.Value ?? 3000f;
                float now = Now;
                int pid = player.PublicInfo.PlayerId;

                // 记账（覆盖上一次未完成的：玩家可能躲一下又出柜再躲）
                WarpFrom[pid] = __instance;
                WarpDue[pid] = now + delayMs / 1000f;

                // 屏蔽窗口覆盖客户端那次固定 3 秒后的自动发包；延长时窗口要更长，
                // 否则客户端会在我们的计时器之前把人送走。
                float suppress = now + (global::System.Math.Max(3000f, delayMs) + 500f) / 1000f;
                WarpSuppress[pid] = suppress;

                if (delayMs <= 0f)
                {
                    // 0 = 当帧传送，不走队列（状态已经校验过）
                    TryWarp(pid);
                    return;
                }

                GameRoom.Instance.PushAfter((int)delayMs, () => TryWarp(pid));
            }
        }

        /// <summary>
        /// 拦掉客户端黑屏结束时的自动 <c>C_USE_SKILL</c>（UI_GameScene:76538）。
        ///
        /// 为什么必须拦：服务端按 <c>WarpDelayMs</c> 提前传完后，客户端 3 秒后仍会发一次包，
        /// 原版 <c>UseMindControl</c> 那时又会 <c>GetCabinet</c> 找到**新**柜子、再送一格
        /// ⇒ 连续传送。放行只会发生在玩家真正主动按技能时（提前触发，语义上也该放行）。
        /// </summary>
        [HarmonyPatch(typeof(GameSkill), "UseSkill")]
        internal static class UseSkillHook
        {
            // 与 DT_Tools 2.0 的同点 Prefix 共存（见 AGENTS §9）：模式开启时本模块先判自己的压制窗口，
            // 模式关闭时第一句 Bypass 直接让路。
            [HarmonyPriority(Priority.First)]
            [HarmonyPrefix]
            private static bool Prefix(GameSkill __instance)
            {
                if (ModeRuntime.Bypass)
                    return true;
                if (!(WarpEnabled?.Value ?? true))
                    return true;
                if (__instance?.Data == null || __instance.Data.Type != ESkillType.MindControl)
                    return true;

                var owner = __instance.Owner;
                if (owner?.PublicInfo == null)
                    return true;

                float until;
                if (WarpSuppress.TryGetValue(owner.PublicInfo.PlayerId, out until) && Now < until)
                {
                    Diagnostics.Hit("Jeremy");
                    Plugin.Log.LogInfo(
                        $"[HS] Jeremy：拦掉 #{owner.PublicInfo.PlayerId} 的客户端自动发包（服务端已接管传送时机）。");
                    return false;
                }
                return true;
            }
        }

        /// <summary>
        /// 执行一次柜子转移（服务端计时器 / 躲柜当帧 两条路都汇到这里）。
        ///
        /// 三重自校验（<c>PushAfter</c> 的回调**不随阶段清空**，可能到结算/大厅才跑）：
        ///   ① 阶段与存活；② 时刻真的到了（防旧记账被覆盖后提前触发）；
        ///   ③ 人还在原来那个柜子里（出柜了或已换柜就放弃）。
        /// </summary>
        private static void TryWarp(int pid)
        {
            try
            {
                var room = GameRoom.Instance;
                if (room == null || room.State != EGameState.Survive)
                    return;

                float due;
                GameCabinet from;
                if (!WarpDue.TryGetValue(pid, out due) || !WarpFrom.TryGetValue(pid, out from))
                    return;
                if (Now < due)
                    return;                          // 时刻未到（旧记账）
                if (from == null)
                    return;

                var dm = GameDeviceManager.Instance;
                if (dm == null)
                    return;

                // 人必须还藏在「原柜」里 —— 出柜 / 已被别的原因送走都算放弃
                var current = dm.GetCabinet(pid);
                if (current == null || !global::System.Object.ReferenceEquals(current, from))
                {
                    WarpDue.Remove(pid);
                    WarpFrom.Remove(pid);
                    return;
                }

                var to = dm.GetPairCabinet(from);
                if (to == null)
                {
                    Plugin.Log.LogInfo($"[HS] Jeremy：#{pid} 找不到同色配对柜，放弃本次转移。");
                    WarpDue.Remove(pid);
                    WarpFrom.Remove(pid);
                    return;
                }

                var player = from.HidePlayer;
                WarpDue.Remove(pid);
                WarpFrom.Remove(pid);

                from.WarpPlayer(to);

                // 提前关掉客户端的黑屏：SkillState 的 setter（:177769）会
                // SendChangeSkillState ⇒ 客户端 ChangeSkillState（:76622）
                // → SetBlackPanel(false)（:76542 Kill 掉黑屏 Tween）。
                // 纯服务端即可，不需要任何客户端改动。
                if (player?.PublicInfo != null && player.IsAlive)
                {
                    try
                    {
                        player.SkillState = 0;
                    }
                    catch (global::System.Exception ex)
                    {
                        Plugin.Log.LogWarning($"[HS] Jeremy：写 SkillState 失败 — {ex.Message}");
                    }
                }

                Plugin.Log.LogInfo($"[HS] Jeremy：柜子 #{pid} 转移至同色柜 {to.ID}。");
            }
            catch (global::System.Exception ex)
            {
                Plugin.Log.LogWarning($"[HS] Jeremy：柜子转移异常 — {ex.Message}");
            }
        }

        /// <summary>到期恢复出柜冲刺（SurvivalTick 每秒一次）。</summary>
        private static void ExpireActive()
        {
            if (Active.Count == 0)
                return;

            float now = Now;
            List<int> expired = null;
            foreach (var kv in Active)
            {
                if (now >= kv.Value)
                    (expired ?? (expired = new List<int>())).Add(kv.Key);
            }
            if (expired == null)
                return;

            foreach (int pid in expired)
                Active.Remove(pid);

            var room = GameRoom.Instance;
            if (room?.Players == null)
                return;

            foreach (var player in room.Players)
            {
                if (player?.PublicInfo == null)
                    continue;
                if (!expired.Contains(player.PublicInfo.PlayerId))
                    continue;

                player.BuffComponent?.RefreshSpeed();
                player.SendChangeSpeed();
                Plugin.Log.LogInfo($"[HS] Jeremy：玩家 #{player.PublicInfo.PlayerId} 的出柜冲刺结束。");
            }
        }

        /// <summary>
        /// 到点就传送（SurvivalTick 每秒一次的兜底）。
        /// 正常路径由 <c>PushAfter(WarpDelayMs)</c> 触发；这条兜底防的是
        /// 「PushAfter 回调被阶段切换吞掉」的极端情况。
        /// </summary>
        private static void RunDueWarp()
        {
            if (WarpDue.Count == 0)
                return;
            var pids = new List<int>(WarpDue.Keys);
            foreach (int pid in pids)
            {
                float due;
                if (WarpDue.TryGetValue(pid, out due) && Now >= due)
                    TryWarp(pid);
            }
        }

        /// <summary>
        /// 清空全部记录**并让受影响的人真正恢复基础速度**。
        /// 光 <c>Active.Clear()</c> 不够：加速是直接写进 <c>player.PrivateInfo.Speed</c> 的，
        /// 清记录不会把那个值改回去 —— 它要等下一次 RefreshSpeed 才重算，
        /// 而开新局 / 回大厅这条路上玩家可能一直不站定 / 起跑，于是会带着加成进新局。
        /// </summary>
        private static void ClearAndRestore()
        {
            Active.Clear();
            WarpDue.Clear();
            WarpFrom.Clear();
            WarpSuppress.Clear();
        }

        [HarmonyPatch(typeof(GameRoom), "StartSurvive")]
        internal static class StartHook
        {
            [HarmonyPostfix]
            private static void Postfix() => ClearAndRestore();
        }

        [HarmonyPatch(typeof(GameRoom), "StartLobby")]
        internal static class LobbyHook
        {
            [HarmonyPostfix]
            private static void Postfix() => ClearAndRestore();
        }

        /// <summary>一离开生存阶段就清 —— 结算阶段还带着加速的窗口也被关掉。</summary>
        [HarmonyPatch(typeof(GameRoom), nameof(GameRoom.ChangeGameState), new[] { typeof(EGameState) })]
        internal static class LeaveSurviveHook
        {
            [HarmonyPostfix]
            private static void Postfix(EGameState state)
            {
                if (state == EGameState.Survive)
                    return;
                ClearAndRestore();
            }
        }
    }
}
