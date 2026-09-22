using BepInEx.Configuration;
using HarmonyLib;
using Protocol;
using Server.Game;
using HideAndSeek.Core;
using GamePlayer = Server.Game.Player;

namespace HideAndSeek.Features.Vision
{
    /// <summary>
    /// 捉迷藏开始的灯效信号 —— 模拟"接触不良的坏灯"：亮灭不定、间隔随机，最后抽搐一下再落定。
    /// 收尾状态遵循玩法 —— 白方亮、黑方暗（黑方随后由 BlackVisionFeature 维持恒暗）。
    ///
    /// 实现：向每名玩家单独下发 S_AREA_PUBLIC 的时序序列（PushAfter 编排）。
    /// 闪灯阶段强制亮/灭（force=true）—— 否则在真停电的对局里信号根本看不见，
    /// 而那恰恰是最需要提示"开始了"的场合；收尾定态则回到真实光照。
    ///
    /// 触发点（默认只由"有人拿刀"触发，开局不闪）：
    ///   - 有人拿走武器转为黑方 —— ItemManager.InsertWeapon
    ///   - 进入生存阶段 —— GameRoom.StartSurvive（默认关闭）
    /// </summary>
    [PatchFeature(
        section: "StartFlash",
        description: "捉迷藏开始灯效：坏灯式闪烁（白方终亮、黑方终暗）。默认由「有人拿刀」触发，可用 hs_flash 开关。",
        defaultEnabled: true,
        side: FeatureSide.Host)]
    internal static class StartFlashFeature
    {
        [ConfigField(true,
            "是否播放闪烁。关闭后不再闪，改为直接进入定态（白方亮、黑方暗）——" +
            "玩法所需的光照结果不受影响。可用命令 hs_flash on|off 运行时切换。")]
        public static ConfigEntry<bool> FlashEnabled;

        [ConfigField(true, "有人拿走武器（转为黑方）时播放 —— 这就是「捉迷藏开始」的时刻。")]
        public static ConfigEntry<bool> OnWeaponTaken;

        [ConfigField(false, "进入生存阶段（开局）时也播放。默认关闭：开局就闪太吵，改由拿刀触发。")]
        public static ConfigEntry<bool> OnStartSurvive;

        [ConfigField(12, "闪烁次数（模拟坏灯抽搐）。", Min = 2f, Max = 60f)]
        public static ConfigEntry<int> FlickerCount;

        [ConfigField(25, "闪烁的最小间隔毫秒数。", Min = 10f, Max = 1000f)]
        public static ConfigEntry<int> FlickerMinMs;

        [ConfigField(160, "闪烁的最大间隔毫秒数。与最小值拉开距离才有「坏掉」的随机感。", Min = 10f, Max = 3000f)]
        public static ConfigEntry<int> FlickerMaxMs;

        [ConfigField(220, "闪烁结束后到落定之间的毫秒数。", Min = 0f, Max = 3000f)]
        public static ConfigEntry<int> SettleDelayMs;

        [ConfigField(2500, "开局触发时的延迟毫秒数（等待客户端进入对局）。", Min = 0f, Max = 30000f)]
        public static ConfigEntry<int> StartDelayMs;

        private static int Clamp(int v, int lo, int hi) => v < lo ? lo : (v > hi ? hi : v);

        /// <summary>
        /// 播放一次坏灯式闪烁；若闪烁被关闭则直接进入定态。
        /// 关闭闪烁 ≠ 什么都不做：仍然要落到"白方亮、黑方暗"，那是玩法本身需要的光照结果。
        /// </summary>
        internal static void Play(GameRoom room)
        {
            if (ModeRuntime.Bypass || room == null)
                return;

            if (FlashEnabled == null || !FlashEnabled.Value)
            {
                SetFinal(room);
                Plugin.Log.LogInfo("[HS] StartFlash：闪烁已关闭，直接进入定态（白方亮 / 黑方暗）。");
                return;
            }

            int count = Clamp(FlickerCount?.Value ?? 12, 2, 60);
            int lo = Clamp(FlickerMinMs?.Value ?? 25, 10, 1000);
            int hi = Clamp(FlickerMaxMs?.Value ?? 160, 10, 3000);
            if (hi < lo)
                hi = lo;

            int settle = Clamp(SettleDelayMs?.Value ?? 220, 0, 3000);

            // 坏灯感的关键：亮灭不定 + 间隔随机
            var rng = new global::System.Random();
            int at = 0;
            for (int i = 0; i < count; i++)
            {
                bool on = rng.Next(2) == 0;
                int when = at;
                room.PushAfter(when, () => SetAll(room, on));
                at += rng.Next(lo, hi + 1);
            }

            room.PushAfter(at, () => SetAll(room, false));          // 最后一下：灭
            room.PushAfter(at + settle, () => SetFinal(room));      // 落定

            Plugin.Log.LogInfo(
                $"[HS] StartFlash：已播放坏灯式闪烁（{count} 次，间隔 {lo}~{hi}ms，历时约 {at}ms）。");
        }

        private static void SetAll(GameRoom room, bool light)
        {
            foreach (var player in room.Players)
                SendLight(player, light, force: true);
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
                SendLight(player, light, force: false);
            }
        }

        /// <summary>
        /// force=true 时强制亮/灭 —— 闪灯阶段必须这样，否则在真停电的对局里
        /// 信号根本看不见（那正是最需要提示"开始了"的场合）。
        /// force=false 时"亮"以区域真实光照为准 —— 收尾定态不该把停电的灯点亮。
        /// </summary>
        private static void SendLight(GamePlayer player, bool light, bool force)
        {
            if (player?.PublicInfo == null)
                return;

            var area = player.CurrentArea;
            if (area == null)
                return;

            bool value = force ? light : (light && area.IsLight);

            player.Session.Send(new S_AREA_PUBLIC
            {
                RoomId = area.Info.RoomId,
                CameraTargetId = player.CameraTargetId,
                IsLight = value
            });
        }

        // ── 触发点 ①：有人拿走武器（默认）──────────────────────────
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

        // ── 触发点 ②：进入生存阶段（默认关闭）────────────────────────
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
