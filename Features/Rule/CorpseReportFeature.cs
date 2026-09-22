using System;
using BepInEx.Configuration;
using HarmonyLib;
using Protocol;
using Server.Game;
using HideAndSeek.Core;
using GamePlayer = Server.Game.Player;
// 注意：Assembly-CSharp 里同时存在全局命名空间的 Corpse（base DeviceBase，客户端用）与
// Server.Game.Corpse（base Server.Game.Device，服务端用）。C# 的类型查找中，
// 外层（全局）命名空间优先于 using 引入的命名空间，因此裸写 Corpse 会静默解析到全局那个，
// 编译通过但 Harmony 在运行期找不到目标。必须显式限定。
using GameCorpse = Server.Game.Corpse;

namespace HideAndSeek.Features.Rule
{
    /// <summary>
    /// 禁止尸体报告，使对局永不进入调查／学级裁判阶段（捉迷藏没有"开会"环节）。
    ///
    /// 原版有三条通往调查阶段的路径，本功能覆盖前两条，第三条由 WhiteWinFeature 改写：
    ///   ① 首具尸体出现后 50~70 秒自动进入 —— Server.Game.Corpse 构造函数（:168820）
    ///        TimeManager.PushSurvivalJob(WaitDetectiveSecond, EndSurvival)
    ///      拦在 PushSurvivalJob 入口把延迟改成极大值：保留原版代码路径不变，
    ///      但实际不再触发。StateList[5]（客户端显示的"预计发现时刻"）由另一个钩子同步修正。
    ///   ② 玩家走到尸体旁手动报警 —— Server.Game.Corpse.Interact（:168866）
    ///   ③ 限制时间归零且有未发现尸体 —— GameRoom.SurvivalTick（:171256）
    ///
    /// 三个钩子各自独立成嵌套类：PatchLoader 对嵌套类型逐个 try/catch，
    /// 因此某个目标与游戏版本对不上时，日志能指出是哪一个，而不是整块功能失效。
    /// </summary>
    [PatchFeature(
        section: "CorpseReport",
        description: "禁止尸体报告：不进入调查/裁判阶段。自动调查改为极大延迟，手动报警直接丢弃。",
        defaultEnabled: true,
        side: FeatureSide.Host)]
    internal static class CorpseReportFeature
    {
        [ConfigField(1000000,
            "首具尸体出现后「自动进入调查阶段」的延迟秒数。原版为 50~70 秒；改为极大值可让该路径实际不触发。",
            Min = 71f)]
        public static ConfigEntry<int> AutoDetectiveDelay;

        [ConfigField(true, "禁止玩家对尸体报警（手动开会）。")]
        public static ConfigEntry<bool> BlockManualReport;

        internal static int DelaySeconds => AutoDetectiveDelay?.Value ?? 1000000;

        /// <summary>
        /// 是否为"尸体自动进调查"的那次排程。
        /// Corpse.EndSurvival 是 private，故用「回调目标类型 + 方法名」双重匹配，
        /// 避免误伤 TimeManager 上其它 50~70 秒的生存任务。
        /// </summary>
        private static bool IsAutoDetectiveJob(Action action)
            => action != null && action.Target is GameCorpse && action.Method.Name == "EndSurvival";

        // ── 路径 ①：自动进入调查的排程时间 ──────────────────────────────
        [HarmonyPatch(typeof(TimeManager), nameof(TimeManager.PushSurvivalJob), new[] { typeof(int), typeof(Action) })]
        internal static class PushSurvivalJobHook
        {
            [HarmonyPrefix]
            private static void Prefix(ref int secondAfter, Action action)
            {
                if (ModeRuntime.Bypass)
                    return;
                if (secondAfter < 50 || secondAfter > 70)   // 原版取值范围 Util.GetRandomNumber(50, 71)
                    return;
                if (!IsAutoDetectiveJob(action))
                    return;

                secondAfter = DelaySeconds;
            }
        }

        // ── 路径 ① 的显示同步：StateList[5] = 预计发现时刻 ────────────────
        [HarmonyPatch(typeof(GameCorpse), MethodType.Constructor, new[] { typeof(GamePlayer), typeof(PublicPlayerInfo) })]
        internal static class CorpseCtorHook
        {
            [HarmonyPostfix]
            private static void Postfix(GameCorpse __instance)
            {
                if (ModeRuntime.Bypass)
                    return;

                // 服务端 Device 用 DeviceInfo；客户端的 DeviceBase.Info 是另一套，不要混用
                var states = __instance?.DeviceInfo?.StateList;
                if (states == null || states.Count <= 5)
                    return;

                states[5] = TimeManager.Instance.SurviveTime + DelaySeconds;
            }
        }

        // ── 路径 ②：手动报警 ────────────────────────────────────────────
        [HarmonyPatch(typeof(GameCorpse), "Interact", new[] { typeof(GamePlayer), typeof(Packet) })]
        internal static class InteractHook
        {
            [HarmonyPrefix]
            private static bool Prefix()
            {
                Diagnostics.Hit("CorpseReport");
                if (ModeRuntime.Bypass)
                    return true;
                if (BlockManualReport == null || !BlockManualReport.Value)
                    return true;

                return false;   // 丢弃该次报警
            }
        }
    }
}
