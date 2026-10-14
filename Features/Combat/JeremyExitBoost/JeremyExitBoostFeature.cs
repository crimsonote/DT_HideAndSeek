using System.Collections.Generic;
using BepInEx.Configuration;
using HarmonyLib;
using Protocol;
using Server.Game;
using HideAndSeek.Core;
using GamePlayer = Server.Game.Player;
using GameCabinet = Server.Game.Cabinet;

namespace HideAndSeek.Features.Combat
{
    /// <summary>
    /// 柜子出柜冲刺：Jeremy **主动**离开柜子后，短时间移速提升。
    ///
    /// 加速原理与 <see cref="SodaBoostFeature"/> 同一套（未上浮到共享层：那边带申领 / 配额 /
    /// 冷却，这边只有"给一次 + 到点收"，规模差得远，强行合并只会把两个都改复杂）：
    ///     速度是客户端本地权威，<c>BuffComponent.RefreshSpeed</c> 是唯一来源
    ///     ⇒ 在它的 Postfix 里乘倍率；施加时主动 RefreshSpeed + SendChangeSpeed 让效果立刻可见。
    ///     <c>SpeedBoostFeature.BlackSpeedMul</c> 是黑方基础倍率，两者叠乘、互不干扰。
    ///
    /// 触发点只认「本人按了出柜」：挂 <c>Cabinet.Interact</c> 的 <c>ExitCabinet</c> 分支
    /// （ACS-0.1.17a:164219）。这样天然排除另外两条也会调 <c>ExitCabinet(player)</c> 的路径 ——
    ///     · 别人开柜子把里面的人踢出来（<c>TryTakeoutPlayerOrCorpse</c> :164238）；
    ///     · 柜子之间的 <c>WarpPlayer</c> 传送（:164243，Jeremy 的看家本领，送了会过强）；
    /// 也排除"在柜子里被打"（<c>OnDamagedEvent</c> 委托，那条路径不进 Interact）。
    ///
    /// 为什么只给 Jeremy：补的是柜子角色的机动性短板 —— 他的柜子传送有 3 秒前摇，
    /// 出柜冲刺给的是"躲藏失败之后的第二口气"，不是正面增益。
    /// </summary>
    [PatchFeature("JeremyExitBoost",
        "柜子出柜冲刺：Jeremy 主动离开柜子后短时间移速提升。",
        defaultEnabled: false, side: FeatureSide.Host)]
    internal static class JeremyExitBoostFeature
    {
        [ConfigField(104, "触发角色 ID。104 = Jeremy（柜子）。", Min = 0f, Max = 999f)]
        public static ConfigEntry<int> CharacterId;

        [ConfigField(5f, "加速持续秒数。", Min = 0.5f, Max = 60f)]
        public static ConfigEntry<float> Seconds;

        [ConfigField(1.6f,
            "移速倍率。1.6 = 160%；与 SpeedBoost.BlackSpeedMul 叠乘。",
            Min = 1f, Max = 3f)]
        public static ConfigEntry<float> SpeedMul;

        /// <summary>PlayerId → 加速到期时刻（TimeManager.SurviveTime 秒）。</summary>
        private static readonly Dictionary<int, float> Active = new Dictionary<int, float>();

        private static float Now => TimeManager.Instance?.SurviveTime ?? 0f;

        // ══ 触发：主动出柜 ═══════════════════════════════════════════════

        [HarmonyPatch(typeof(GameCabinet), nameof(GameCabinet.Interact))]
        internal static class InteractHook
        {
            [HarmonyPostfix]
            private static void Postfix(GamePlayer player, Packet pkt)
            {
                Diagnostics.Hit("JeremyExitBoost");
                if (ModeRuntime.Bypass)
                    return;
                if (player?.PublicInfo == null)
                    return;

                // 只认"本人按了出柜"：pkt 是 C_INTERACT_CABINET 且 Type == ExitCabinet。
                // pkt 可能带着别的包类型进来，as 失败即 null。
                var req = pkt?.Pkt as C_INTERACT_CABINET;
                if (req == null || req.Type != ECabinetInteractType.ExitCabinet)
                    return;

                int want = CharacterId?.Value ?? 104;
                if (want > 0 && player.PublicInfo.CharacterId != want)
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
                    $"[HS] JeremyExitBoost：柜子 #{pid} 主动出柜，{seconds:F1} 秒内移速 ×{SpeedMul?.Value ?? 1.6f:F2}。");
            }
        }

        // ══ 加速生效 ════════════════════════════════════════════════════

        [HarmonyPatch(typeof(BuffComponent), nameof(BuffComponent.RefreshSpeed))]
        internal static class RefreshSpeedHook
        {
            [HarmonyPostfix]
            private static void Postfix(BuffComponent __instance)
            {
                if (ModeRuntime.Bypass)
                    return;

                var player = __instance?.Owner;
                if (player?.PublicInfo == null)
                    return;
                // 幽灵：MakeSpectatorGhost 置 Hide 并直接设速度，别掺和
                if (!player.IsAlive || player.State == EPlayerState.Hide)
                    return;

                // 阶段闸门：只在生存阶段加成。
                // 不能只靠"到期时清理"——那是挂在 GameRoom.SurvivalTick 上的，
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

        // ══ 到期恢复 ════════════════════════════════════════════════════

        [HarmonyPatch(typeof(GameRoom), "SurvivalTick")]
        internal static class TickHook
        {
            [HarmonyPostfix]
            private static void Postfix()
            {
                if (ModeRuntime.Bypass || Active.Count == 0)
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

                    // 此刻该玩家已不在 Active 里，RefreshSpeed 会按原版重算基础速度，
                    // 本类的 Postfix 也不再乘倍率，然后下发给他即可。
                    player.BuffComponent?.RefreshSpeed();
                    player.SendChangeSpeed();
                    Plugin.Log.LogInfo($"[HS] JeremyExitBoost：玩家 #{player.PublicInfo.PlayerId} 的出柜冲刺结束。");
                }
            }
        }

        /// <summary>
        /// 清空记录**并让受影响的人真正恢复基础速度**。
        ///
        /// 光 <c>Active.Clear()</c> 不够：加速是直接写进 <c>player.PrivateInfo.Speed</c> 的，
        /// 清记录不会把那个值改回去 —— 它要等下一次 RefreshSpeed 才重算，而开新局 / 回大厅
        /// 这条路上玩家可能一直不站定 / 起跑，于是会带着加成进新局。
        /// </summary>
        private static void ClearAndRestore()
        {
            if (Active.Count == 0)
                return;

            List<int> affected = new List<int>(Active.Keys);
            Active.Clear();

            var room = GameRoom.Instance;
            if (room?.Players == null)
                return;

            foreach (var player in room.Players)
            {
                if (player?.PublicInfo == null)
                    continue;
                if (!affected.Contains(player.PublicInfo.PlayerId))
                    continue;

                player.BuffComponent?.RefreshSpeed();
                player.SendChangeSpeed();
            }
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
