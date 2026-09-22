using BepInEx.Configuration;
using HarmonyLib;
using Protocol;
using Server.Game;
using HideAndSeek.Core;
using GamePlayer = Server.Game.Player;

namespace HideAndSeek.Features.Vision
{
    /// <summary>
    /// 开局灯效：捉迷藏开始时让全场灯光连续亮灭两次，作为"开始了"的信号，
    /// 收尾状态遵循玩法 —— 白方亮、黑方暗（黑方随后由 BlackVisionFeature 维持恒暗）。
    ///
    /// 实现：向每名玩家单独下发 S_AREA_PUBLIC 的时序序列。
    /// 注意"亮"必须以该玩家所在区域的**真实光照** area.IsLight 为基准，
    /// 否则在真停电的对局里会把不该亮的灯点亮。
    ///
    /// 两个触发点（各自可开关）：
    ///   - 有人拿走武器转为黑方 —— ItemManager.InsertWeapon
    ///   - 进入生存阶段 —— GameRoom.StartSurvive（延迟若干毫秒，等客户端就绪）
    /// </summary>
    [PatchFeature(
        section: "StartFlash",
        description: "开局灯效：捉迷藏开始时全场灯光连续亮灭两次（白方终亮、黑方终暗），作为开局信号。",
        defaultEnabled: true,
        side: FeatureSide.Host)]
    internal static class StartFlashFeature
    {
        [ConfigField(true, "有人拿走武器（转为黑方）时播放灯效。")]
        public static ConfigEntry<bool> OnWeaponTaken;

        [ConfigField(true, "进入生存阶段（开局）时播放灯效。")]
        public static ConfigEntry<bool> OnStartSurvive;

        [ConfigField(400, "每次亮/灭的间隔毫秒数。", Min = 50f, Max = 5000f)]
        public static ConfigEntry<int> IntervalMs;

        [ConfigField(2500, "开局触发时的延迟毫秒数（等待客户端进入对局）。", Min = 0f, Max = 30000f)]
        public static ConfigEntry<int> StartDelayMs;

        private static int Interval
        {
            get
            {
                int v = IntervalMs?.Value ?? 400;
                return v < 50 ? 50 : v;
            }
        }

        /// <summary>播放一次：亮 → 灭 → 亮 → 灭 → 定态。</summary>
        internal static void Play(GameRoom room)
        {
            if (ModeRuntime.Bypass || room == null)
                return;

            int step = Interval;
            room.PushAfter(0,         () => SetAll(room, true));
            room.PushAfter(step,      () => SetAll(room, false));
            room.PushAfter(step * 2,  () => SetAll(room, true));
            room.PushAfter(step * 3,  () => SetAll(room, false));
            room.PushAfter(step * 4,  () => SetFinal(room));

            Plugin.Log.LogInfo($"[HS] StartFlash：已播放开局灯效（间隔 {step}ms）。");
        }

        private static void SetAll(GameRoom room, bool light)
        {
            foreach (var player in room.Players)
                SendLight(player, light);
        }

        /// <summary>收尾定态：白方恢复真实光照，黑方保持黑暗。</summary>
        private static void SetFinal(GameRoom room)
        {
            foreach (var player in room.Players)
            {
                if (player?.PublicInfo == null)
                    continue;

                bool light = player.Color != EPlayerColor.Black
                             && player.Color != EPlayerColor.Dark;
                SendLight(player, light);
            }
        }

        private static void SendLight(GamePlayer player, bool light)
        {
            if (player?.PublicInfo == null)
                return;

            var area = player.CurrentArea;
            if (area == null)
                return;

            // "亮"用真实光照作为基准，避免在停电对局里把灯点亮
            bool value = light && area.IsLight;

            player.Session.Send(new S_AREA_PUBLIC
            {
                RoomId = area.Info.RoomId,
                CameraTargetId = player.CameraTargetId,
                IsLight = value
            });
        }

        // ── 触发点 ①：有人拿走武器 ──────────────────────────────────
        [HarmonyPatch(typeof(ItemManager), nameof(ItemManager.InsertWeapon))]
        internal static class WeaponTakenHook
        {
            [HarmonyPostfix]
            private static void Postfix(GamePlayer player)
            {
                if (ModeRuntime.Bypass)
                    return;
                if (OnWeaponTaken == null || !OnWeaponTaken.Value)
                    return;
                if (player == null || player.Color != EPlayerColor.Black)
                    return;

                Play(GameRoom.Instance);
            }
        }

        // ── 触发点 ②：进入生存阶段 ──────────────────────────────────
        [HarmonyPatch(typeof(GameRoom), "StartSurvive")]
        internal static class StartSurviveHook
        {
            [HarmonyPostfix]
            private static void Postfix(GameRoom __instance)
            {
                if (ModeRuntime.Bypass)
                    return;
                if (OnStartSurvive == null || !OnStartSurvive.Value)
                    return;

                int delay = StartDelayMs?.Value ?? 2500;
                __instance.PushAfter(delay < 0 ? 0 : delay, () => Play(__instance));
            }
        }
    }
}
