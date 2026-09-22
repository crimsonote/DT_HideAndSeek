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
    /// 客户端"黑灯"由 Managers.Game.Darkness 决定，而它只来自服务端下发的 S_AREA_PUBLIC
    /// （客户端 MapManager.ChangeArea 无条件写 Darkness = !IsLight）。因此只需**单独给黑方**
    /// 发一份 IsLight=false 的包：服务端的真实区域光照、其它玩家的视野都不受影响。
    ///
    /// 四个必须覆盖的时机：
    ///   ① 转为黑方的瞬间 —— set_Color（:175354）
    ///   ② 进入新区域 —— Area.SendAreaInfo（:173361），否则换房间会重新变亮（大厅也属于这种情况）
    ///   ③ 真实光照变化后 —— Area.set_IsLight（:173326），否则真停电恢复时
    ///      向区域内玩家重播的亮灯包会把黑灯一起洗掉
    ///   ④ **离开对局阶段** —— ChangeGameState（:169991）：假黑灯是灌进客户端的本地状态，
    ///      不主动还回去的话，回到大厅仍会一直黑着（已实测复现）
    ///
    /// 副作用（与真停电一致，属预期）：黑方在黑暗中部分设备无法交互；
    /// 露娜护盾的客户端判定在 Darkness 下失效 —— 该问题由 LunaImmunityFeature 在服务端兜底。
    /// </summary>
    [HarmonyPatch]
    [PatchFeature(
        section: "BlackVision",
        description: "黑方恒黑灯视野：转为黑方后立刻并持续保持黑灯（等同真停电的视野表现），离开对局时自动恢复。",
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

        // ── ④ 离开对局阶段：把真实光照还回去 ──────────────────────────
        [HarmonyPatch(typeof(GameRoom), nameof(GameRoom.ChangeGameState))]
        internal static class GameStateHook
        {
            [HarmonyPostfix]
            private static void Postfix(GameRoom __instance, EGameState state)
            {
                if (ModeRuntime.Bypass)
                    return;
                if (state == EGameState.Survive || state == EGameState.Detective)
                    return;   // 对局内保持黑灯

                int restored = 0;
                foreach (var player in __instance.Players)
                {
                    if (player?.PublicInfo == null)
                        continue;

                    var area = player.CurrentArea;
                    if (area == null)
                        continue;

                    // 重播真实光照：客户端会据此把 Darkness 改回与 IsLight 一致
                    area.SendAreaInfo(player);
                    restored++;
                }

                if (restored > 0)
                    Plugin.Log.LogInfo($"[HS] BlackVision：已恢复真实光照（切至 {state}，{restored} 人）。");
            }
        }
    }
}
