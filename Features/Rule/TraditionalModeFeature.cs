using System.Collections.Generic;
using System.Reflection;
using BepInEx.Configuration;
using Google.Protobuf;
using HarmonyLib;
using Protocol;
using Server.Game;
using HideAndSeek.Core;
using HideAndSeek.Game;
using GamePlayer = Server.Game.Player;

namespace HideAndSeek.Features.Rule
{
    /// <summary>
    /// 传统模式（雕像式捉迷藏）。
    ///
    /// 规则（房主口径）：
    ///   ① 黑方**第一次**拿到刀 ⇒ 立刻冻结黑方 N 秒（默认 30），并把倒计时剩余时间设为 N 秒。
    ///      这一帧是「白方的躲藏准备时间」：黑方在数数，白方可以自由躲。
    ///   ② 第一段倒计时**不真正结束**：在剩余 1 秒时就切换到第二段（避免归 0 触发客户端问题），
    ///      把倒计时重置为「初始限制时间」，并冻结**全体白方**直到这段倒计时结束。
    ///      第二段是「黑方的寻找时间」：白方不能动，黑方搜索。
    ///   ③ 第二段归零后走原版/本仓既有的胜负判定（其他判定不变）。
    ///   ④ 本局只重置一次；黑方之后掉刀再捡不再触发。
    ///   ⑤ 任务完成、黑方击杀都**不再改变倒计时**（任务仍推进进度，只是不加/减时间）。
    ///   ⑥ 传统模式开启时，游戏内指令系统与「额外的技能修改」全部关闭，
    ///      唯一例外是黑洞（Noel 的 Teleport，`TeleportGuardFeature`）。
    ///
    /// 实现要点：
    ///   · 状态在 <c>StartSurvive</c> 一次性锁存（设置页只在大厅可改），
    ///     所以不存在「局内热切换」导致的活动状态清理问题；关总开关 <c>HS_Mode</c> 仍即时生效。
    ///   · 白方冻结用 `EBuffType.Stop`（只锁操作，不 `CancelAllInteract`）——
    ///     躲进柜子的白方不会被弹出来；黑方计数用 `EBuffType.TheWorld`（灰屏时停观感）。
    ///   · 白方的 Stop 用「超长时长 + 到点手动 RemoveBuffForce」实现「直到倒计时结束」：
    ///     若倒计时被快照恢复等外部原因延长，冻结仍在；归零时由 Postfix 统一撤销。
    ///   · 第一段在 `RemainTime &lt;= 1` 时切换；`SurvivalTick` 是自续约的，
    ///     所以 Prefix 里**只改 RemainTime、返回 true**，绝不 `return false` 阻断循环。
    ///     并钉 `Priority.First` 抢在 `WhiteWinFeature`（默认优先级）之前。
    /// </summary>
    [PatchFeature(
        section: "TraditionalMode",
        description: "传统模式：黑方拿刀后等待 N 秒（冻结黑方、倒计时设为 N），随后把倒计时重置为初始限制并冻结白方至本轮结束；只重置一次，任务与击杀不再改变倒计时。",
        defaultEnabled: true,
        side: FeatureSide.Host)]
    internal static class TraditionalModeFeature
    {
        // ══ 配置 ══════════════════════════════════════════════════════

        [ConfigField(false,
            "启用传统模式：黑方拿刀后等待 N 秒（冻结黑方、倒计时设为 N），" +
            "随后把倒计时重置为初始限制并冻结白方至本轮结束。只重置一次；任务与击杀不再改变倒计时。\n" +
            "默认关闭。只在大厅设置页改动，开局锁存（下一局生效）；关总开关 HS_Mode 仍即时生效。")]
        public static ConfigEntry<bool> EnabledMode;

        [ConfigField(30,
            "黑方拿刀后等待/冻结秒数（独立设置，不受传统模式开关影响；传统模式关闭时不生效）。",
            Min = 5f, Max = 60f)]
        public static ConfigEntry<int> CountSeconds;

        [ConfigField(false,
            "倒计时剩余 30 秒时提前结束本局（实际在剩余 31 秒归零，避开客户端最后 30 秒的项圈警报闪烁）。" +
            "只在本模式生效；会少打约 30 秒。默认关闭。")]
        public static ConfigEntry<bool> EndAtThirty;

        // ══ 状态 ══════════════════════════════════════════════════════

        private enum Phase
        {
            None = 0,
            Countdown = 1,
            Search = 2
        }

        private static Phase _phase = Phase.None;

        /// <summary>本局是否启用传统模式。StartSurvive 时从配置锁存。</summary>
        private static bool _latched;

        /// <summary>本局是否已经触发过（只触发第一次拿刀）。</summary>
        private static bool _triggered;

        /// <summary>开局时的剩余倒计时 = 初始限制时间（StartSurvive 时捕获）。</summary>
        private static float _initialRemain;

        /// <summary>正在计数的黑方。到期要撤 TheWorld，所以留对象引用。</summary>
        private static GamePlayer _black;

        /// <summary>等待期间发给白方的“指向黑方”箭头目标位置；结束时要按同位置撤销。</summary>
        private static PosInfo _arrowPos;

        /// <summary>是否已经给白方发过指向黑方的箭头。</summary>
        private static bool _arrowSent;

        /// <summary>本模式施加了 Stop 的白方；归零/换阶段时逐个撤销。</summary>
        private static readonly List<GamePlayer> FrozenWhites = new List<GamePlayer>();

        /// <summary>冻结白方的 PlayerId。断线重连后 Player 对象会换，门闩按 ID 判才不失效。</summary>
        private static readonly HashSet<int> FrozenWhiteIds = new HashSet<int>();

        /// <summary>ClearMission 的执行深度（>0 = 当前这次加时来自任务完成）。</summary>
        private static int _missionDepth;

        /// <summary>
        /// 白方 Stop 的额外缓冲秒数。真实撤销由倒计时归零/阶段切换主动做，
        /// 这个余量只保证「即使撤销失败也不会提前解冻」。
        /// </summary>
        private const int FreezeBufferSeconds = 600;

        // ══ 对外门闩 ══════════════════════════════════════════════════

        /// <summary>本局传统模式是否生效（总开关关掉时立即失效）。</summary>
        internal static bool ActiveThisRound => !ModeRuntime.Bypass && _latched;

        /// <summary>
        /// 第二段「黑方寻找」是否已经开始。白方已被 Stop 冻结。
        /// 物品伪装用它做兼容：冻结期间不新进入伪装，但已经完成的伪装必须保留。
        /// </summary>
        internal static bool IsFrozenPhase => ActiveThisRound && _phase == Phase.Search;

        /// <summary>指令系统是否关闭。</summary>
        internal static bool BanCommands => ActiveThisRound;

        /// <summary>额外的技能修改是否关闭（黑洞除外）。</summary>
        internal static bool BanSkillExtras => ActiveThisRound;

        /// <summary>任务/击杀是否不再改变倒计时。</summary>
        internal static bool SuppressTimeChanges => ActiveThisRound;

        /// <summary>
        /// 传统模式要拼进**原版消息开头**的那一行。
        /// 不能单独发一条：客户端的 SecretChat 气泡同一时刻只留一条，后发的会把前一条顶掉。
        /// </summary>
        internal static string KnifeNotice(GamePlayer black)
        {
            if (!ActiveThisRound || black?.PublicInfo == null)
                return null;
            if (black.Color != EPlayerColor.Black && black.Color != EPlayerColor.Dark)
                return null;

            int secs = CountSeconds?.Value ?? 30;
            return "传统模式：黑方在「" + RoomDisplay.AreaNameOf(black)
                   + "」计数 " + secs + " 秒。";
        }

        /// <summary>开局发刀模式：在开局提示里找当前黑方，拼同一行。</summary>
        internal static string AutoStartNotice(GameRoom room)
        {
            if (!ActiveThisRound || room?.Players == null)
                return null;

            foreach (var p in room.Players)
            {
                if (p?.PublicInfo == null)
                    continue;
                if (p.Color != EPlayerColor.Black && p.Color != EPlayerColor.Dark)
                    continue;
                return KnifeNotice(p);
            }
            return null;
        }

        // ══ 生命周期 ══════════════════════════════════════════════════

        [HarmonyPatch(typeof(GameRoom), "StartSurvive")]
        internal static class StartHook
        {
            [HarmonyPostfix]
            private static void Postfix(GameRoom __instance)
            {
                // 先把上一局可能残留的 Stop/TheWorld 清掉，再锁存本局设置。
                CleanupBuffs();
                _phase = Phase.None;
                _triggered = false;
                _latched = EnabledMode != null && EnabledMode.Value;

                var tm = TimeManager.Instance;
                // 权威口径 = 大厅难度里的「初始限制时间」（InitSecond）；
                // ResetSurvival 也是把它与 TotalTime 取小后写进 RemainTime。
                float init = __instance != null ? __instance.DifficultPreset.InitSecond : 0f;
                if (tm != null && tm.TotalTime > 0f)
                    init = global::System.Math.Min(init, tm.TotalTime);
                if (init <= 1f)
                    init = tm?.RemainTime ?? 180f;

                _initialRemain = global::System.Math.Max(5f, init);

                if (ActiveThisRound)
                {
                    Plugin.Log.LogInfo(
                        $"[HS] TraditionalMode：本局启用传统模式（计数 {CountSeconds?.Value ?? 30} 秒，"
                        + $"初始限制 {_initialRemain:F0} 秒）。");
                }
            }
        }

        [HarmonyPatch(typeof(GameRoom), "StartLobby")]
        internal static class LobbyHook
        {
            [HarmonyPostfix]
            private static void Postfix()
            {
                _latched = false;
                _phase = Phase.None;
                _triggered = false;
                _initialRemain = 0f;
                CleanupBuffs();
            }
        }

        [HarmonyPatch(typeof(GameRoom), nameof(GameRoom.ChangeGameState), new[] { typeof(EGameState) })]
        internal static class StateHook
        {
            [HarmonyPostfix]
            private static void Postfix(EGameState state)
            {
                if (state == EGameState.Survive)
                    return;

                // 离开生存阶段：撤销所有由本模式施加的 Buff，避免带到结算/大厅。
                _phase = Phase.None;
                CleanupBuffs();
            }
        }

        // ══ 触发：黑方第一次拿到刀 ═══════════════════════════════════

        [HarmonyPatch(typeof(ItemManager), nameof(ItemManager.InsertWeapon))]
        internal static class KnifeTakenHook
        {
            [HarmonyPostfix]
            private static void Postfix(GamePlayer player, Server.Game.Item item)
            {
                Diagnostics.Hit("TraditionalMode");
                if (ModeRuntime.Bypass || !ActiveThisRound)
                    return;
                if (player?.PublicInfo == null)
                    return;

                // 只有这次插入真的生效才算「获得刀」：已有刀时 InsertWeapon 整段跳过，
                // 但 Postfix 照样会跑。
                if (!ReferenceEquals(player.Weapon, item))
                    return;

                if (player.Color != EPlayerColor.Black && player.Color != EPlayerColor.Dark)
                    return;

                var room = GameRoom.Instance;
                if (room?.Players == null || room.State != EGameState.Survive)
                    return;

                if (_triggered)
                    return;                      // 本局只触发一次

                StartCountdown(room, player);
            }
        }

        private static void StartCountdown(GameRoom room, GamePlayer black)
        {
            var tm = TimeManager.Instance;
            if (tm == null)
                return;

            int secs = global::System.Math.Max(1, CountSeconds?.Value ?? 30);
            float target = global::System.Math.Min(secs, tm.TotalTime > 0f ? tm.TotalTime : secs);

            _triggered = true;
            _phase = Phase.Countdown;
            _black = black;
            NotifyBlackArrow(room);

            // 倒计时直接设为 N 秒（UpdateRemainTime 会 clamp 到 [0, TotalTime] 并广播）。
            tm.UpdateRemainTime(target - tm.RemainTime);

            // 黑方：时停观感（灰屏 + 动画冻结 + 锁操作）。先清掉同类型残留，保证时长精确。
            black.BuffComponent?.RemoveBuffForce(EBuffType.TheWorld);
            black.BuffComponent?.AddBuff(EBuffType.TheWorld, secs * 1000);

            try
            {
                room.BroadcastWorldSFX(ESoundType.TheWorldSfx, black.PublicInfo.Pos, 896f);
            }
            catch (global::System.Exception ex)
            {
                Plugin.Log.LogWarning($"[HS] TraditionalMode：计数音效广播失败 — {ex.Message}");
            }

            Plugin.Log.LogInfo(
                $"[HS] TraditionalMode：黑方 #{black.PublicInfo.PlayerId}（{black.Name}）拿到刀 ⇒ "
                + $"冻结 {secs} 秒、倒计时设为 {target:F0} 秒（白方躲藏准备）。");
        }

        /// <summary>
        /// 等待期间给所有白方发一个指向黑方的世界箭头。
        /// 黑方被 TheWorld 冻结，位置不会变，所以只需发一次；结束时按同一坐标撤销。
        /// `CharacterArrow` 在客户端是通用角色箭头，UI_Arrow 会围绕目标点旋转。
        /// </summary>
        private static void NotifyBlackArrow(GameRoom room)
        {
            if (room?.Players == null || _black?.PublicInfo == null)
                return;

            ClearBlackArrow();
            _arrowPos = _black.PublicInfo.Pos.Clone();
            _arrowSent = true;

            foreach (var player in room.Players)
            {
                if (player?.Session == null || player.PublicInfo == null)
                    continue;
                if (!player.IsAlive || player.IsSpectator)
                    continue;
                if (player.Color == EPlayerColor.Black || player.Color == EPlayerColor.Dark)
                    continue;

                player.Session.Send(new S_NOTIFY_ARROW
                {
                    Type = EArrowType.CharacterArrow,
                    Pos = _arrowPos.Clone()
                });
            }
        }

        private static void ClearBlackArrow()
        {
            if (!_arrowSent || _arrowPos == null)
                return;

            _arrowSent = false;
            var room = GameRoom.Instance;
            PosInfo pos = _arrowPos;
            _arrowPos = null;

            if (room?.Players == null)
                return;

            foreach (var player in room.Players)
            {
                if (player?.Session == null || player.PublicInfo == null)
                    continue;
                if (player.Color == EPlayerColor.Black || player.Color == EPlayerColor.Dark)
                    continue;

                player.Session.Send(new S_REMOVE_ARROW
                {
                    Type = EArrowType.CharacterArrow,
                    Pos = pos
                });
            }
        }

        // ══ 冻结期：白方的主动技能一律不生效（黑洞由黑方保留）═════════

        /// <summary>
        /// 白方被 Stop 冻结后，服务端仍可能收到已经在队列里的 C_USE_SKILL；
        /// 原版 UseActiveSkill 不检查 _lockControlStack，只靠客户端锁 ⇒ 必须在这里补一道。
        /// 黑方差分：黑方不受此门闩限制，黑洞（Noel）照旧可用。
        /// </summary>
        [HarmonyPatch(typeof(SkillComponent), "UseActiveSkill")]
        internal static class FrozenSkillHook
        {
            [HarmonyPrefix]
            private static bool Prefix(SkillComponent __instance)
            {
                if (ModeRuntime.Bypass || !ActiveThisRound || _phase != Phase.Search)
                    return true;

                var owner = __instance?.Owner;
                if (owner?.PublicInfo == null)
                    return true;
                if (owner.Color == EPlayerColor.Black || owner.Color == EPlayerColor.Dark)
                    return true;                 // 黑洞保留：黑方技能不受限
                if (!FrozenWhiteIds.Contains(owner.PublicInfo.PlayerId))
                    return true;

                Plugin.Log.LogInfo(
                    $"[HS] TraditionalMode：白方 #{owner.PublicInfo.PlayerId} 在冻结期尝试使用技能，已拦截。");
                return false;
            }
        }

        // ══ 第一段结束：切到第二段（重置倒计时 + 冻结白方）═══════════

        [HarmonyPatch(typeof(GameRoom), "SurvivalTick")]
        internal static class TickHook
        {
            /// <summary>
            /// 在剩余 1 秒时切换，保证第一段**永远不归 0**（房主口径：防客户端问题）。
            /// 只改 RemainTime、返回 true，让原方法继续 tick 并自续约。
            /// </summary>
            [HarmonyPrefix]
            [HarmonyPriority(Priority.First)]
            private static void Prefix(GameRoom __instance)
            {
                if (ModeRuntime.Bypass || !ActiveThisRound)
                    return;

                var tm = TimeManager.Instance;
                if (__instance == null || tm == null)
                    return;
                if (__instance.State != EGameState.Survive || tm.Paused)
                    return;
                if (WhiteWinFeature.IsAllClear())
                    return;                      // 任务已全清，交给结局流程

                // 可选项：第二段剩余 30 秒时提前结束。
                // 在 31 秒归零，客户端根本不会收到 30 秒的 S_TIME ⇒ 不会进入最后 30 秒的
                // 项圈警报闪烁；随后 WhiteWinFeature 按正常规则裁定（不自己结算）。
                if (_phase == Phase.Search
                    && EndAtThirty != null && EndAtThirty.Value
                    && tm.RemainTime > 0f && tm.RemainTime <= 31f)
                {
                    tm.UpdateRemainTime(-tm.RemainTime);
                    return;
                }

                if (_phase != Phase.Countdown)
                    return;
                if (tm.RemainTime > 1f)
                    return;                      // 第一段还没到切换点

                EnterSearch(__instance, tm);
            }

            /// <summary>第二段归零 / 状态失效时撤销白方冻结。</summary>
            [HarmonyPostfix]
            private static void Postfix(GameRoom __instance)
            {
                if (!ActiveThisRound)
                {
                    // HS_Mode 被关掉（或本局未锁存）—— 立即回滚，别把玩家冻在大厅里。
                    if (_phase != Phase.None || FrozenWhites.Count > 0 || _black != null)
                    {
                        _phase = Phase.None;
                        CleanupBuffs();
                    }
                    return;
                }

                if (__instance == null || __instance.State != EGameState.Survive)
                {
                    if (_phase != Phase.None || FrozenWhites.Count > 0 || _black != null)
                    {
                        _phase = Phase.None;
                        CleanupBuffs();
                    }
                    return;
                }

                if (_phase == Phase.Countdown && (_black == null || !_black.IsAlive))
                {
                    _phase = Phase.None;
                    CleanupBuffs();
                    return;
                }

                if (_phase != Phase.Search)
                    return;

                var tm = TimeManager.Instance;
                if (tm == null)
                {
                    CleanupBuffs();
                    _phase = Phase.None;
                    return;
                }

                // 第二段结束：正常判胜负由 WhiteWinFeature / 原版处理。
                // 只在「已经裁定」或「已离开 Survive」后解冻 —— 若在归零当帧就解冻，
                // 会让人在判定前多动约 1 秒（原版要等下一个 SurvivalTick 才判 0）。
                if (tm.RemainTime <= 0f
                    && (EndingRuleFeature.Decided || __instance.State != EGameState.Survive))
                {
                    CleanupBuffs();
                    _phase = Phase.None;
                }
            }
        }

        private static void EnterSearch(GameRoom room, TimeManager tm)
        {
            ClearBlackArrow();
            _phase = Phase.Search;

            float target = _initialRemain > 1f
                ? _initialRemain
                : (tm.TotalTime > 0f ? tm.TotalTime : 120f);
            target = global::System.Math.Max(5f, target);

            // 把倒计时重置为初始限制。当前是 1 秒，所以这里通常是 +（target-1）。
            // 原方法随后会 Tick 一次（-1 秒），这是既有 1Hz 节拍的正常损耗。
            tm.UpdateRemainTime(target - tm.RemainTime);

            // 黑方的计数时停到此结束（即使 TheWorld 还剩不到 1 秒也一并清掉）。
            _black?.BuffComponent?.RemoveBuffForce(EBuffType.TheWorld);

            FreezeWhites(room, target);

            Plugin.Log.LogInfo(
                $"[HS] TraditionalMode：计数结束 ⇒ 倒计时重置为 {target:F0} 秒，"
                + $"已冻结 {FrozenWhites.Count} 名白方至本轮结束（黑方开始寻找）。");
        }

        private static void FreezeWhites(GameRoom room, float targetRemain)
        {
            FrozenWhites.Clear();
            FrozenWhiteIds.Clear();
            if (room?.Players == null)
                return;

            int durationMs = (int)(global::System.Math.Min(3600f, targetRemain + FreezeBufferSeconds) * 1000f);

            foreach (var player in room.Players)
            {
                if (player?.PublicInfo == null || player.Session == null)
                    continue;
                if (!player.IsAlive || player.IsSpectator)
                    continue;
                if (player.Color == EPlayerColor.Black || player.Color == EPlayerColor.Dark)
                    continue;

                try
                {
                    // ⚠ 必须先取消已经起手的黑洞传送：原版 UseTeleport 会先给自己挂 Stop、
                    // 2 秒后 Job 里 RemoveBuffForce(Stop) + Move(dest)。不取消的话，白方在
                    // 冻结前起手的传送会在冻结期间完成，并把我们的 Stop 一起删掉。
                    player.SkillComponent?.CancelTeleport();

                    // 再清同类型残留，保证我们这次的长时长真的生效。
                    player.BuffComponent?.RemoveBuffForce(EBuffType.Stop);
                    player.BuffComponent?.AddBuff(EBuffType.Stop, durationMs);
                    FrozenWhites.Add(player);
                    FrozenWhiteIds.Add(player.PublicInfo.PlayerId);
                }
                catch (global::System.Exception ex)
                {
                    Plugin.Log.LogWarning(
                        $"[HS] TraditionalMode：冻结 #{player.PublicInfo.PlayerId} 失败 — {ex.Message}");
                }
            }
        }

        private static void CleanupBuffs()
        {
            ClearBlackArrow();

            for (int i = 0; i < FrozenWhites.Count; i++)
            {
                try { FrozenWhites[i]?.BuffComponent?.RemoveBuffForce(EBuffType.Stop); }
                catch { /* 撤销失败不能影响换局 */ }
            }
            FrozenWhites.Clear();
            FrozenWhiteIds.Clear();

            try { _black?.BuffComponent?.RemoveBuffForce(EBuffType.TheWorld); }
            catch { /* 同上 */ }

            _black = null;
        }

        // ══ 任务 / 击杀不再改变倒计时 ═════════════════════════════════

        /// <summary>
        /// 包住 MissionManager.ClearMission：进入时记深度，返回后复位。
        /// MissionManager 在本程序集外是 internal，只能反射定位。
        /// </summary>
        [HarmonyPatch]
        internal static class MissionClearHook
        {
            [HarmonyTargetMethod]
            private static MethodBase Target()
            {
                var type = AccessTools.TypeByName("Server.Game.MissionManager");
                return type == null ? null : AccessTools.Method(type, "ClearMission");
            }

            [HarmonyPrefix]
            private static void Prefix()
            {
                if (SuppressTimeChanges)
                    _missionDepth++;
            }

            [HarmonyPostfix]
            private static void Postfix()
            {
                if (_missionDepth > 0)
                    _missionDepth--;
            }
        }

        /// <summary>
        /// 任务完成时原版会 UpdateRemainTime(+N)。把这一次的增量清零。
        /// 钉 Priority.First：必须在 MissionTimePenaltyFeature 的捕获钩子之前，
        /// 否则它会先捕获原值、再在 Postfix 里补扣。
        /// </summary>
        [HarmonyPatch(typeof(TimeManager), nameof(TimeManager.UpdateRemainTime))]
        internal static class RemainTimeFreezeHook
        {
            [HarmonyPrefix]
            [HarmonyPriority(Priority.First)]
            private static void Prefix(ref float delta)
            {
                if (!SuppressTimeChanges || _missionDepth <= 0)
                    return;

                delta = 0f;
            }
        }

        /// <summary>
        /// 客户端会按 S_MISSION_CLEAR.AddTime 显示「+N SEC」浮字；
        /// 时间已不改，包里的 AddTime 也一并清零，避免界面与真实倒计时不一致。
        /// </summary>
        [HarmonyPatch(typeof(GameRoom), "Broadcast", new[] { typeof(IMessage), typeof(int) })]
        internal static class MissionClearPacketHook
        {
            [HarmonyPrefix]
            private static void Prefix(IMessage packet)
            {
                if (!SuppressTimeChanges || !(packet is S_MISSION_CLEAR clear))
                    return;

                clear.AddTime = 0;
            }
        }
    }
}
