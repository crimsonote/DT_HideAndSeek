using HarmonyLib;
using Protocol;
using Server.Game;
using HideAndSeek.Core;
using GamePlayer = Server.Game.Player;

namespace HideAndSeek.Features.Vision
{
    /// <summary>
    /// 黑方恒黑灯视野（转为黑方的瞬间即刻生效，并持续保持）。
    ///
    /// 客户端"黑灯"由 Managers.Game.Darkness 决定，它只来自服务端下发的 S_AREA_PUBLIC
    /// （客户端 MapManager.ChangeArea 无条件写 Darkness = !IsLight）。因此只需**单独给黑方**
    /// 发一份 IsLight=false 的包：服务端真实区域光照与其它玩家视野都不受影响。
    ///
    /// 下发时机：
    ///   ① 转为黑方的瞬间 —— set_Color（:175354）
    ///   ② 进入新区域 —— Area.SendAreaInfo（:173361），否则换房间会重新变亮
    ///   ③ 真实光照变化后 —— Area.set_IsLight（:173326），否则复电时的亮灯包会洗掉黑灯
    ///
    /// 解除时机（假黑灯是灌进客户端的本地状态，不主动还回去就一直黑着）：
    ///   ④ 离开对局阶段 —— ChangeGameState(EGameState)（:169991）
    ///   ⑤ 结算开始 —— StartTotalResult（:170103），Prefix
    ///   ⑥ 回到大厅 —— StartLobby（:170202），**Prefix**：该方法会逐个 player.Clear()，
    ///      清完之后 CurrentArea 就没了，放 Postfix 补救不了
    ///
    /// 副作用（与真停电一致，属预期）：黑方在黑暗中部分设备无法交互；
    /// 露娜护盾的客户端判定在 Darkness 下失效 —— 由 LunaImmunityFeature 在服务端兜底。
    /// </summary>
    [HarmonyPatch]
    [PatchFeature(
        section: "BlackVision",
        description: "黑方恒黑灯视野：转为黑方后立刻并持续保持黑灯（等同真停电的视野表现），离开对局/结算/回大厅时自动恢复。",
        defaultEnabled: true,
        side: FeatureSide.Host)]
    internal static class BlackVisionFeature
    {
        private static bool IsBlack(GamePlayer player)
            => !ModeRuntime.Bypass && player != null && player.Color == EPlayerColor.Black;

        /// <summary>向该黑方单独下发"你所在区域不亮"。</summary>
        private static void SendDarkArea(GamePlayer player)
        {
            var area = player.CurrentArea;
            if (area == null)
                return;

            player.Session.Send(new S_AREA_PUBLIC
            {
                RoomId = area.Info.RoomId,
                CameraTargetId = player.CameraTargetId,
                IsLight = false
            });
        }

        /// <summary>
        /// 解除黑灯。只有一条路径：直接补一份"亮"的包。
        ///
        /// 不用 Area.SendAreaInfo：这些时机下玩家往往已不在任何区域内（CurrentArea 为 null），
        /// 而大厅/结算阶段本来就没有任何区域广播 —— 那条路在这里永远走不到。
        /// 客户端只要收到 S_AREA_PUBLIC 就会把 Darkness 置回 false，一个包足够。
        /// </summary>
        private static void ClearBlackout(GameRoom room, string reason)
        {
            if (room == null)
                return;

            int count = 0;
            foreach (var player in room.Players)
            {
                if (player?.PublicInfo == null || player.Session == null)
                    continue;

                player.Session.Send(new S_AREA_PUBLIC
                {
                    RoomId = player.CurrentArea?.Info.RoomId ?? 0,
                    CameraTargetId = player.CameraTargetId,
                    IsLight = true
                });
                count++;
            }

            if (count > 0)
                Plugin.Log.LogInfo($"[HS] BlackVision：已解除黑灯（{reason}，{count} 人）。");
        }

        // ── ① 变黑瞬间 ──────────────────────────────────────────────
        [HarmonyPatch(typeof(GamePlayer), "set_Color")]
        [HarmonyPostfix]
        private static void PostfixColor(GamePlayer __instance, EPlayerColor value)
        {
            Diagnostics.Hit("BlackVision");
            if (value != EPlayerColor.Black)
                return;
            if (!IsBlack(__instance))
                return;

            SendDarkArea(__instance);
            Plugin.Log.LogInfo($"[HS] BlackVision：玩家 #{__instance.PublicInfo.PlayerId} 转为黑方，已下发黑灯。");
        }

        // ── ② 进入新区域 ────────────────────────────────────────────
        [HarmonyPatch(typeof(Area), nameof(Area.SendAreaInfo))]
        [HarmonyPostfix]
        private static void PostfixSendAreaInfo(Area __instance, GamePlayer player)
        {
            if (!IsBlack(player))
                return;

            SendDarkArea(player);
        }

        // ── ③ 真实光照变化（复电/停电）后补发 ─────────────────────────
        [HarmonyPatch(typeof(Area), "set_IsLight")]
        [HarmonyPostfix]
        private static void PostfixIsLight(Area __instance)
        {
            if (ModeRuntime.Bypass)
                return;
            if (__instance?.Players == null)
                return;

            foreach (var player in __instance.Players)
            {
                if (IsBlack(player))
                    SendDarkArea(player);
            }
        }

        // ── ④ 离开对局阶段 ──────────────────────────────────────────
        // GameRoom 上有两个 ChangeGameState 重载（public 单参 :169991 / private 双参 :170004），
        // 不指定参数类型会抛 "Ambiguous match"，整个补丁静默挂不上。
        [HarmonyPatch(typeof(GameRoom), nameof(GameRoom.ChangeGameState), new[] { typeof(EGameState) })]
        internal static class GameStateHook
        {
            [HarmonyPostfix]
            private static void Postfix(GameRoom __instance, EGameState state)
            {
                if (ModeRuntime.Bypass)
                    return;
                if (state == EGameState.Survive || state == EGameState.Detective)
                    return;   // 对局内保持黑灯

                ClearBlackout(__instance, $"切至 {state}");
            }
        }

        // ── ⑤ 结算开始前 ────────────────────────────────────────────
        [HarmonyPatch(typeof(GameRoom), "StartTotalResult")]
        internal static class TotalResultHook
        {
            [HarmonyPrefix]
            private static void Prefix(GameRoom __instance)
            {
                if (ModeRuntime.Bypass)
                    return;

                ClearBlackout(__instance, "结算开始");
            }
        }

        // ── ⑥ 回大厅前（必须在 Clear 之前）──────────────────────────
        [HarmonyPatch(typeof(GameRoom), "StartLobby")]
        internal static class LobbyHook
        {
            [HarmonyPrefix]
            private static void Prefix(GameRoom __instance)
            {
                if (ModeRuntime.Bypass)
                    return;

                ClearBlackout(__instance, "回到大厅");
            }
        }
    }
}
