using BepInEx.Configuration;
using HarmonyLib;
using Server.Game;
using HideAndSeek.Core;

namespace HideAndSeek.Features.Rule
{
    /// <summary>
    /// 完成任务反而**减少**限制时间（原版为增加）。
    ///
    /// 原版 MissionManager.ClearMission（:166690）末尾：
    ///     num2 = |missionData.Point| * (15 / PublicRemainPlayerCount) * TimeLimitIncreaseWeight;
    ///     TimeManager.Instance.UpdateRemainTime(num2);                       // 加时
    ///     Broadcast(new S_MISSION_CLEAR { ..., AddTime = (int)num2 });
    ///
    /// 这里不重写原方法体（避免 Transpiler 与对 internal 字段的依赖），而是：
    ///   ① 在 ClearMission 执行期间捕获它传给 UpdateRemainTime 的增量；
    ///   ② ClearMission 返回后再补一次 -(1+倍率)×增量。
    /// 净效果 = 减去「倍率 × 原版加时量」，默认倍率 1 即"完成任务扣掉等量时间"。
    ///
    /// 注：MissionManager 在发行程序集中是 internal，故用类型名字符串挂补丁。
    /// </summary>
    [HarmonyPatch]
    [PatchFeature(
        section: "MissionTimePenalty",
        description: "完成任务减少限制时间：原版完成任务会加时，开启后改为扣减等量时间。",
        defaultEnabled: true,
        side: FeatureSide.Host)]
    internal static class MissionTimePenaltyFeature
    {
        [ConfigField(true, "启用任务扣时。")]
        public static ConfigEntry<bool> PenaltyOn;

        [ConfigField(1f, "扣时倍率。1 = 与原版加时等量相抵（净效果为扣减等量时间）。", Min = 0.1f, Max = 5f)]
        public static ConfigEntry<float> Multiplier;

        private static bool _inClearMission;
        private static float _capturedDelta;

        // ── 捕获 ClearMission 内部的加时量 ──────────────────────────────
        [HarmonyPatch(typeof(TimeManager), nameof(TimeManager.UpdateRemainTime))]
        [HarmonyPrefix]
        private static void PrefixUpdateRemainTime(ref float delta)
        {
            if (_inClearMission)
                _capturedDelta = delta;
        }

        // ── 包住 ClearMission ──────────────────────────────────────────
        [HarmonyPatch("Server.Game.MissionManager", "ClearMission")]
        [HarmonyPrefix]
        private static void PrefixClearMission()
        {
            Diagnostics.Hit("MissionTimePenalty");
            _inClearMission = false;
            _capturedDelta = 0f;

            if (ModeRuntime.Bypass)
                return;
            if (PenaltyOn == null || !PenaltyOn.Value)
                return;

            _inClearMission = true;
        }

        [HarmonyPatch("Server.Game.MissionManager", "ClearMission")]
        [HarmonyPostfix]
        private static void PostfixClearMission()
        {
            _inClearMission = false;

            if (_capturedDelta == 0f)
                return;

            float multiplier = Multiplier?.Value ?? 1f;
            if (multiplier <= 0f)
            {
                _capturedDelta = 0f;
                return;
            }

            // 原版已 +delta，这里补 -(1+倍率)*delta → 净效果为 -倍率*delta
            TimeManager.Instance.UpdateRemainTime(-_capturedDelta * (1f + multiplier));
            Plugin.Log.LogInfo(
                $"[HS] MissionTimePenalty：完成任务，限制时间净减少 {_capturedDelta * multiplier:F1} 秒。");

            _capturedDelta = 0f;
        }
    }
}
