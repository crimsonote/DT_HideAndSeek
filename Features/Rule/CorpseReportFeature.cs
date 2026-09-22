using System;
using BepInEx.Configuration;
using HarmonyLib;
using Protocol;
using Server.Game;
using HideAndSeek.Core;
using GamePlayer = Server.Game.Player;

namespace HideAndSeek.Features.Rule
{
    /// <summary>
    /// 禁止尸体报告，使对局永不进入调查／学级裁判阶段（捉迷藏没有"开会"环节）。
    ///
    /// 原版有三条通往调查阶段的路径，本功能覆盖前两条，第三条由 WhiteWinFeature 改写：
    ///   ① 首具尸体出现后 50~70 秒自动进入 —— Corpse 构造函数（:168820）
    ///        TimeManager.PushSurvivalJob(WaitDetectiveSecond, EndSurvival)
    ///      拦在 PushSurvivalJob 入口把延迟改成极大值：保留原版代码路径不变
    ///      （不同于直接拦 Corpse.EndSurvival 那样改变状态机），但实际不再触发。
    ///      StateList[5]（客户端显示的"预计发现时刻"）由 Postfix 同步修正。
    ///   ② 玩家走到尸体旁手动报警 —— Corpse.Interact（:168866）
    ///   ③ 限制时间归零且有未发现尸体 —— GameRoom.SurvivalTick（:171256）
    /// </summary>
    [HarmonyPatch]
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
            => action != null && action.Target is Corpse && action.Method.Name == "EndSurvival";

        // ── 路径 ①：自动进入调查的排程时间 ──────────────────────────────
        [HarmonyPatch(typeof(TimeManager), nameof(TimeManager.PushSurvivalJob), new[] { typeof(int), typeof(Action) })]
        [HarmonyPrefix]
        private static void PrefixPushSurvivalJob(ref int secondAfter, Action action)
        {
            if (ModeRuntime.Bypass)
                return;
            if (secondAfter < 50 || secondAfter > 70)   // 原版取值范围 Util.GetRandomNumber(50, 71)
                return;
            if (!IsAutoDetectiveJob(action))
                return;

            secondAfter = DelaySeconds;
        }

        // ── 路径 ① 的显示同步：StateList[5] = 预计发现时刻 ────────────────
        [HarmonyPatch(typeof(Corpse), MethodType.Constructor, new[] { typeof(GamePlayer), typeof(PublicPlayerInfo) })]
        [HarmonyPostfix]
        private static void PostfixCorpseCtor(Corpse __instance)
        {
            if (ModeRuntime.Bypass)
                return;

            var states = __instance?.Info?.StateList;
            if (states == null || states.Count <= 5)
                return;

            states[5] = TimeManager.Instance.SurviveTime + DelaySeconds;
        }

        // ── 路径 ②：手动报警 ────────────────────────────────────────────
        // 用字符串 + 参数类型定位：Corpse 上另有不可访问的 Interact(int) 重载，nameof 会解析到它并报 CS0122
        [HarmonyPatch(typeof(Corpse), "Interact", new[] { typeof(GamePlayer), typeof(Packet) })]
        [HarmonyPrefix]
        private static bool PrefixInteract()
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
