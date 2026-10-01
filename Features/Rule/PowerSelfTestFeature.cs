using System;
using BepInEx.Configuration;
using HarmonyLib;
using Server.Game;
using HideAndSeek.Core;
using GameDeviceManager = Server.Game.DeviceManager;
using GameRoom = Server.Game.GameRoom;

namespace HideAndSeek.Features.Rule
{
    /// <summary>
    /// 「电力系统自检」的两个**结果动作**（流程状态机在 <c>CommandFeature</c> 里，本类只管落地效果）。
    ///
    /// 结果一 —— **电力系统正常** ⇒ **电箱下线 N 秒**（默认 45）：
    ///   口径（房主给的）："如果已经派发电箱，则让电箱消失 45 秒无法破坏；如果电箱还没派发，则额外延后 45 秒"。
    ///   两者用同一条路径实现 —— 一个**抑制窗口**：
    ///     · 已派发（`Fusebox.DeviceInfo.MissionType == -1`）⇒ 先 `ClearFuseboxSabotage()` 立刻收回
    ///       （地图标记消失、`GetDisconnectFuseCount` 归零 ⇒ 期间无法破坏），再把抑制窗口设上；
    ///     · 未派发 ⇒ 只设抑制窗口（原版 `RefreshLight` 的 `case 0` 会在断电归零后
    ///       `PushSurvivalJob(60, StartFuseboxSabotage)`，那一次派发会被抑制窗口挡住并顺延）。
    ///   ⇒ 抑制由 <see cref="SuppressStartHook"/> 实现，**不改原版那个 60 秒常量**。
    ///
    /// 结果二 —— **电力中断** ⇒ **修好所有电箱**：直接复用 <see cref="PowerRepairFeature"/> 的
    ///   既有实现（走原版 `Fusebox.ConnetCable` 路径，不手工复刻它的副作用）。
    /// </summary>
    [PatchFeature(
        section: "PowerSelfTest",
        description: "电力系统自检命令的结果落地：电力正常时让电箱下线一段时间；电力中断时修好所有电箱。",
        defaultEnabled: true,
        side: FeatureSide.Host)]
    internal static class PowerSelfTestFeature
    {
        [ConfigField(45f, "「电力系统正常」时电箱下线/派发延后的秒数。", Min = 0f, Max = 300f)]
        public static ConfigEntry<float> BlackoutSeconds;

        [ConfigField(10f, "自检耗时下限（秒）。真正的等待时长在 [下限, 上限] 之间随机。", Min = 1f, Max = 120f)]
        public static ConfigEntry<float> ActivateMinSec;

        [ConfigField(24f, "自检耗时上限（秒）。", Min = 1f, Max = 300f)]
        public static ConfigEntry<float> ActivateMaxSec;

        [ConfigField(20f, "等待授权码输入的秒数，超时即「操作已过期」。", Min = 5f, Max = 300f)]
        public static ConfigEntry<float> CodeTimeoutSec;

        [ConfigField(5f, "「正在验证操作者权限」的演出时长（秒）。", Min = 0f, Max = 60f)]
        public static ConfigEntry<float> VerifySec;

        [ConfigField(180f, "命令冷却（秒）。**全房共享**。", Min = 0f, Max = 3600f)]
        public static ConfigEntry<float> Cooldown;

        [ConfigField(true,
            "地缘约束：授权码**只能在发起时的那个房间内提交**" +
            "（中途可以离开，只要回来提交即可）；异区玩家敲命令会被告知『正在 X 房间由 Y 操作』。" +
            "关 = 不检查房间，他人一律得到『已被占用』。")]
        public static ConfigEntry<bool> RequireSameArea;

        /// <summary>抑制窗口的截止时刻（房主时钟）。在此之前 `StartFuseboxSabotage` 会被顺延。</summary>
        private static float _suppressUntil = -1f;

        internal static bool Armed => !ModeRuntime.Bypass && Diagnostics.IsLoaded("PowerSelfTest");

        private static float Now()
        {
            try { return Managers.Game.ClientTime; }
            catch { return 0f; }
        }

        /// <summary>把抑制窗口设为"现在 + N 秒"。已在抑制期内则取更晚的那个（不缩短）。</summary>
        private static void Suppress(float seconds)
        {
            float until = Now() + seconds;
            if (until > _suppressUntil)
                _suppressUntil = until;
        }

        /// <summary>
        /// 结果：**电力系统正常** ⇒ 电箱下线 N 秒。
        /// 返回"期间是否收回了已派发的电箱"（供命令端选文案，两套文案房主都给了）。
        /// </summary>
        public static bool ApplyNormal()
        {
            float sec = BlackoutSeconds?.Value ?? 45f;

            if (sec <= 0f)
                return false;                       // 配成 0 = 不生效

            bool dispatched = false;
            try
            {
                var dm = GameDeviceManager.Instance;
                if (dm?.Fuseboxes != null)
                {
                    foreach (var fb in dm.Fuseboxes)
                    {
                        // `MissionType == -1` 是原版 `Fusebox.StartMission` 打上的"已派发"标记
                        // （`ClearSabotage` 只在它等于 -1 时才清理）—— 这是判断"地图上有没有
                        // 待破坏目标"的唯一可靠依据，不要用箭头/任务列表去猜。
                        if (fb?.DeviceInfo != null && fb.DeviceInfo.MissionType == -1)
                        {
                            dispatched = true;
                            break;
                        }
                    }

                    if (dispatched)
                        dm.ClearFuseboxSabotage();
                }
            }
            catch (global::System.Exception ex)
            {
                Plugin.Log.LogWarning($"[HS] 电力自检：收回电箱失败 — {ex.Message}");
            }

            Suppress(sec);
            Plugin.Log.LogInfo($"[HS] 电力自检：电箱下线 {sec:F0} 秒（已派发={dispatched}）。");
            return dispatched;
        }

        /// <summary>结果：**电力中断** ⇒ 修好所有电箱（复用既有实现）。</summary>
        public static int ApplyRestore()
        {
            int broken = 0;
            try { broken = GameDeviceManager.Instance?.GetDisconnectFuseCount() ?? 0; }
            catch { }

            PowerRepairFeature.ForceRepairAll();
            Plugin.Log.LogInfo($"[HS] 电力自检：已恢复供电（原有 {broken} 个断电电箱）。");
            return broken;
        }

        /// <summary>
        /// 抑制窗口内的电箱派发 ⇒ 不执行，改为**顺延到窗口末尾**重排一次。
        ///
        /// 为什么用"重排"而不是"直接丢弃"：派发是原版 `RefreshLight` 的 `case 0` 排的
        /// `PushSurvivalJob(60, StartFuseboxSabotage)` —— 丢弃等于把它永久吃掉，
        /// 这一局就再也不会派发电箱了；重排则只是推迟。
        /// </summary>
        [HarmonyPatch(typeof(GameDeviceManager), "StartFuseboxSabotage")]
        internal static class SuppressStartHook
        {
            [HarmonyPrefix]
            private static bool Prefix(GameDeviceManager __instance)
            {
                if (!Armed || __instance == null)
                    return true;

                float now = Now();
                if (now >= _suppressUntil)
                    return true;                    // 不在抑制期 ⇒ 放行

                // 还在抑制期 ⇒ 顺延到窗口末尾（+50ms 余量，避免同一帧内立刻又撞上）
                int delayMs = (int)((_suppressUntil - now) * 1000f) + 50;
                try
                {
                    GameRoom.Instance?.PushAfter(delayMs, __instance.StartFuseboxSabotage);
                    Plugin.Log.LogInfo($"[HS] 电力自检：电箱派发被顺延 {delayMs / 1000f:F1} 秒。");
                }
                catch (global::System.Exception ex)
                {
                    Plugin.Log.LogWarning($"[HS] 电力自检：顺延派发失败，改为放行 — {ex.Message}");
                    return true;                    // 顺延失败就放行，绝不能让派发凭空消失
                }
                return false;
            }
        }
    }
}
