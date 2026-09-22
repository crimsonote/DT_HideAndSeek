using HarmonyLib;
using Protocol;
using Server.Game;
using HideAndSeek.Core;
using GamePlayer = Server.Game.Player;

namespace HideAndSeek.Features.Vision
{
    /// <summary>
    /// 黑方恒黑灯视野。
    ///
    /// 客户端"黑灯"由 Managers.Game.Darkness 决定，唯一来源是服务端下发的 S_AREA_PUBLIC
    /// （客户端 MapManager.ChangeArea :29575 无条件写 Darkness = !IsLight）。
    /// 因此只需**单独给黑方**发一份 IsLight=false 的包，服务端真实区域光照与其它玩家视野都不受影响。
    ///
    /// ★ 只在生存阶段维持，不做任何"解除"处理：
    ///   原版回大厅时自己会收尾 —— 客户端在 :29070-29112 那段（InitGame / 清 IsGhost /
    ///   刷新玩家）里执行 `Darkness = false`（:29110）。如果我们在那之后还补发黑灯
    ///   （回大厅切区域会触发 Area.SendAreaInfo，从而走到下面的 Postfix），
    ///   大厅就会一直黑着 —— 这正是之前反复出现的现象。
    ///   判据因此只有一句：房间处于 Survive 才黑灯，其它阶段一律不干预。
    ///
    /// 下发时机（都在 Survive 内）：
    ///   ① 转为黑方的瞬间 —— set_Color（:175354）
    ///   ② 进入新区域 —— Area.SendAreaInfo（:173361），否则换房间会重新变亮
    ///   ③ 真实光照变化后 —— Area.set_IsLight（:173326），否则复电时的亮灯包会洗掉黑灯
    ///
    /// 副作用（与真停电一致，属预期）：黑方在黑暗中部分设备无法交互；
    /// 露娜护盾的客户端判定在 Darkness 下失效 —— 由 LunaImmunityFeature 在服务端兜底。
    /// </summary>
    [HarmonyPatch]
    [PatchFeature(
        section: "BlackVision",
        description: "黑方恒黑灯视野：转为黑方后在生存阶段持续保持黑灯（等同真停电的表现）；离开生存阶段不再干预，交还原版收尾。",
        defaultEnabled: true,
        side: FeatureSide.Host)]
    internal static class BlackVisionFeature
    {
        /// <summary>
        /// 是否需要维持黑灯：仅限"该玩家是黑方"且"房间处于生存阶段"。
        /// 生存阶段之外一律不干预 —— 原版会自己把 Darkness 复位。
        /// </summary>
        private static bool ShouldBlackout(GamePlayer player)
        {
            if (ModeRuntime.Bypass || player == null)
                return false;
            if (player.Color != EPlayerColor.Black)
                return false;

            var room = GameRoom.Instance;
            return room != null && room.State == EGameState.Survive;
        }

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
            if (!ShouldBlackout(__instance))
                return;

            SendDarkArea(__instance);
            Plugin.Log.LogInfo($"[HS] BlackVision：玩家 #{__instance.PublicInfo.PlayerId} 转为黑方，已下发黑灯。");
        }

        // ── ② 进入新区域 ────────────────────────────────────────────
        [HarmonyPatch(typeof(Area), nameof(Area.SendAreaInfo))]
        [HarmonyPostfix]
        private static void PostfixSendAreaInfo(Area __instance, GamePlayer player)
        {
            if (!ShouldBlackout(player))
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
                if (ShouldBlackout(player))
                    SendDarkArea(player);
            }
        }
    }
}
