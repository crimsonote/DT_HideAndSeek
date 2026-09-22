using System.Reflection;
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
    /// 本功能不重写原方法体（避免 Transpiler 与对 internal 字段的依赖），而是：
    ///   ① 在 ClearMission 执行期间捕获它传给 UpdateRemainTime 的增量；
    ///   ② ClearMission 返回后再补一次 -(1+倍率)×增量。
    /// 净效果 = 减去「倍率 × 原版加时量」。
    ///
    /// MissionManager 在发行程序集中是 internal，且 Harmony 无法把字符串类型名
    /// 解析到 Assembly-CSharp（会误落到 0Harmony 上），故用 HarmonyTargetMethod + AccessTools 反射定位。
    /// 两个钩子各自独立成嵌套类，任一目标找不到时只影响它自己。
    /// </summary>
    [PatchFeature(
        section: "MissionTimePenalty",
        description: "完成任务减少限制时间：原版完成任务会加时，开启后改为扣减等量时间。",
        defaultEnabled: true,
        side: FeatureSide.Host)]
    internal static class MissionTimePenaltyFeature
    {
        [ConfigField(true, "启用任务扣时。")]
        public static ConfigEntry<bool> PenaltyOn;

        [ConfigField(1f, "额外乘数。最终限制时间变化 = (-1) × 原版加时量 × 本乘数；原版加时量已包含大厅设置的时限权重，因此本项只是在其之上再乘一个系数，不替换原始设置。", Min = 0.1f, Max = 5f)]
        public static ConfigEntry<float> Multiplier;

        private static bool _inClearMission;
        private static float _capturedDelta;

        private static MethodBase MissionManagerClearMission()
        {
            var type = AccessTools.TypeByName("Server.Game.MissionManager");
            return type == null ? null : AccessTools.Method(type, "ClearMission");
        }

        /// <summary>包住 ClearMission：进入时置标志，返回后补扣时间。</summary>
        [HarmonyPatch]
        internal static class ClearMissionHook
        {
            [HarmonyTargetMethod]
            private static MethodBase Target() => MissionManagerClearMission();

            [HarmonyPrefix]
            private static void Prefix()
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

            [HarmonyPostfix]
            private static void Postfix()
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

        /// <summary>捕获 ClearMission 内部这一次加时的增量。</summary>
        [HarmonyPatch(typeof(TimeManager), nameof(TimeManager.UpdateRemainTime))]
        internal static class UpdateRemainTimeHook
        {
            [HarmonyPrefix]
            private static void Prefix(ref float delta)
            {
                if (_inClearMission)
                    _capturedDelta = delta;
            }
        }
    }
}
