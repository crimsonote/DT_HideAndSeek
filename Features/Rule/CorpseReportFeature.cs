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
    /// 原版有四条通往调查阶段的路径（① 与 ② 又各有"主机迁移后重排"这一变体），
    /// 本功能覆盖 ①②③，④ 由 WhiteWinFeature 改写：
    ///   ① 首具尸体出现后 50~70 秒自动进入 —— Server.Game.Corpse 构造函数（:168820）
    ///        TimeManager.PushSurvivalJob(WaitDetectiveSecond, EndSurvival)
    ///      拦在 PushSurvivalJob 入口把延迟改成极大值：保留原版代码路径不变，但实际不再触发。
    ///      同一入口也覆盖 Corpse.OnMigrationResume(:169012) 的迁移后重排。
    ///      注：StateList[5]（客户端显示的"预计发现时刻"）改不动 —— 尸体状态在构造函数内部
    ///      就已对外同步，Postfix 改的只是服务端之后没人再读的副本，客户端仍显示原版的 50~70 秒。
    ///   ② 玩家走到尸体旁手动报警 —— Server.Game.Corpse.Interact（:168866）
    ///   ③ 任务进度顶满 100% —— MissionManager.ClearMission(:166741) → TriggerAllClearEnd(:166868)
    ///        → GameRoom.FindOldestUndiscoveredCorpse() → corpse.DiscoverByTimeOver() 直接进审判。
    ///      这条完全绕过 ①②（不经过 PushSurvivalJob，也不经过 Corpse.Interact），
    ///      拦法是让"找可报告尸体"这个查询在**全清路径**上返回 null：原版随即走它自己的
    ///      「无尸体」分支 PushAfter(3000, ClearAllMission) → 正常按全清结算。
    ///   ④ 限制时间归零且有未发现尸体 —— GameRoom.SurvivalTick（:171256）
    ///
    /// 各钩子独立成嵌套类：PatchLoader 对嵌套类型逐个 try/catch，
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

        [ConfigField(true,
            "把尸体上的『报告』改写成『搬起尸体』。" +
            "开：白方也能拖尸，且尸体不再抢占交互位（捉迷藏里尸体本就不该是举报按钮）；" +
            "关：只丢弃报告，尸体仍会挡住它旁边的交互目标。")]
        public static ConfigEntry<bool> ConvertToCarry;

        [ConfigField(true,
            "任务进度顶满 100% 时也不进入调查阶段。" +
            "原版 MissionManager.TriggerAllClearEnd 会取一具未发现尸体直接进审判，绕过本功能的其它闸门；" +
            "开启后该查询在**全清路径**上返回 null，原版随即走它自己的「无尸体」分支 —— " +
            "3 秒后按全清结算（白方胜利）。限时归零那条路径不受影响，仍由 WhiteWinOnTimeout 决定胜负。")]
        public static ConfigEntry<bool> BlockAutoDetectiveOnAllClear;

        internal static int DelaySeconds => AutoDetectiveDelay?.Value ?? 1000000;

        /// <summary>
        /// 是否为"尸体自动进调查"的那次排程。
        /// Corpse.EndSurvival 是 private，故用「回调目标类型 + 方法名」双重匹配 ——
        /// 这个组合本身已足够精确（全工程只有 Corpse 构造函数与 Corpse.OnMigrationResume
        /// 两处排程它），不必再叠加延迟秒数区间。
        /// </summary>
        private static bool IsAutoDetectiveJob(Action action)
            => action != null && action.Target is GameCorpse && action.Method.Name == "EndSurvival";

        // ── 路径 ①：自动进入调查的排程时间 ──────────────────────────────
        // 判据只用「回调目标是尸体 + 方法是 EndSurvival」，不按延迟秒数过滤。
        // 早先还额外要求 secondAfter ∈ [50,70]，那是因为只考虑了 Corpse 构造函数里
        // Util.GetRandomNumber(50, 71) 那一处；但同一个方法还有第二个排程点 ——
        // Corpse.OnMigrationResume(:169012) 会在房主迁移后按 StateList[5] 的剩余时间重排，
        // 那个值可以是任意数（余量 >70 时就被旧的区间过滤放过去了 → 迁移后照样自动报告）。
        // IsAutoDetectiveJob 本身已经精确到"就是那个委托"，区间过滤纯属冗余。
        [HarmonyPatch(typeof(TimeManager), nameof(TimeManager.PushSurvivalJob), new[] { typeof(int), typeof(Action) })]
        internal static class PushSurvivalJobHook
        {
            [HarmonyPrefix]
            private static void Prefix(ref int secondAfter, Action action)
            {
                if (ModeRuntime.Bypass)
                    return;
                if (!IsAutoDetectiveJob(action))
                    return;

                secondAfter = DelaySeconds;
            }
        }

        // 注：这里曾有一个 CorpseCtorHook，试图把 StateList[5]（客户端显示的"预计发现时刻"）
        // 也改成极大值，但实测无效 —— 尸体状态在构造函数内部就已对外同步，
        // Postfix 改的只是服务端之后没人再读的副本，客户端仍显示原版的 50~70 秒。
        // "到时间不报告"由上面的 PushSurvivalJob 拦截保证，与本钩子无关，
        // 因此删除它 —— 留一个无效补丁只会误导后来的人。

        // ── 路径 ②：手动报警 ────────────────────────────────────────────
        // 报告与搬运是两条独立包路径：E 键 → C_INTERACT_CORPSE → 这里；
        // Q 键 → C_CARRY_CORPSE → DeviceManager.CarryCorpse。所以拦这里只挡报告，不碰搬运。
        //
        // 但客户端对尸体有"绝对交互优先权"（SearchInteractDevice :27333 连距离都不比就抢占），
        // 且尸体提示文本不读 DiscoverdDone/_isReport —— 报告被丢弃后提示仍在、尸体继续抢占，
        // 观感就是"按 E 没反应，还挡住了旁边的设备"。
        //
        // 因此这里不只丢弃报告，而是把它改写成"搬起尸体"：
        //   · StartCarry 内部 RemoveDevice(this) → 客户端尸体消失 → 抢占自然解除
        //   · 白方本没有搬运入口（客户端三重闸门只放 Black/Dark），这里一并放开
        //   · State / HandItemId 都由 S_MODIFY_PLAYER 下发，客户端能正确渲染拖尸外观
        //   · 顺序敏感：必须先 State=Carry 再 HandItemId，否则客户端走 EquipItem 分支、不显拖尸
        [HarmonyPatch(typeof(GameCorpse), "Interact", new[] { typeof(GamePlayer), typeof(Packet) })]
        internal static class InteractHook
        {
            [HarmonyPrefix]
            private static bool Prefix(GameCorpse __instance, GamePlayer player)
            {
                Diagnostics.Hit("CorpseReport");
                if (ModeRuntime.Bypass)
                    return true;
                if (BlockManualReport == null || !BlockManualReport.Value)
                    return true;

                if (ConvertToCarry != null && ConvertToCarry.Value)
                    TryCarryInstead(__instance, player);

                return false;   // 永不报告
            }

            /// <summary>把一次"报告"改写成"搬起尸体"。</summary>
            private static void TryCarryInstead(GameCorpse corpse, GamePlayer player)
            {
                try
                {
                    var room = GameRoom.Instance;
                    if (corpse == null || player == null || room == null)
                        return;
                    if (room.State != EGameState.Survive || !player.IsAlive)
                        return;
                    if (corpse.IsHidden || corpse.IsCarried || corpse.IsBombCorpse || corpse.DiscoverdDone)
                        return;
                    if (player.CarryingCorpseId != -1)
                        return;                                  // 手上已经有尸体了

                    ItemManager.Instance.DropItem(player);       // 复刻 CarryCorpse :164292，仅去掉颜色条件
                    corpse.StartCarry(player.PublicInfo.PlayerId);
                    player.CarryingCorpseId = corpse.ID;
                    player.State = EPlayerState.Carry;           // 先状态：内部广播 ChangePlayerState=12
                    player.PublicInfo.HandItemId = corpse.ID;
                    player.BroadcastModifyPlayer(EModifyPlayerEvent.ChangeHandItem, corpse.ID);   // 再手持物
                }
                catch (global::System.Exception ex)
                {
                    Plugin.Log.LogWarning($"[HS] CorpseReport：改写为搬运失败 — {ex.Message}");
                }
            }
        }

        // ── 路径 ③：任务进度顶满 100% ────────────────────────────────────
        // 原版 TriggerAllClearEnd(:166868) 是本玩法唯一会"凭空"进审判的入口：
        //     Corpse corpse = FindOldestUndiscoveredCorpse();
        //     if (corpse != null) corpse.DiscoverByTimeOver();   // 进审判
        //     else GameRoom.Instance.PushAfter(3000, ClearAllMission);
        // 它既不过 PushSurvivalJob，也不过 Corpse.Interact，前两个闸门完全够不着。
        //
        // 这里不去改 TriggerAllClearEnd 本身（private，且照抄那条分支等于重写原版），
        // 而是让原版**自己的查询**在全清时返回 null —— 后续两条分支都是原版代码，
        // 我们一点没碰。GameRoom.SurvivalTick(:171247) 在 AllClear 为真时直接 return，
        // 所以限时归零那条路径走到这个查询时 AllClear 必为 false，天然不受影响。
        [HarmonyPatch(typeof(GameRoom), nameof(GameRoom.FindOldestUndiscoveredCorpse))]
        internal static class FindCorpseHook
        {
            [HarmonyPrefix]
            private static bool Prefix(ref GameCorpse __result)
            {
                Diagnostics.Hit("CorpseReport");
                if (ModeRuntime.Bypass)
                    return true;
                if (BlockAutoDetectiveOnAllClear == null || !BlockAutoDetectiveOnAllClear.Value)
                    return true;
                if (!MissionBridge.AllClear)
                    return true;                       // 非全清路径（限时归零）—— 交还原版

                __result = null;
                Plugin.Log.LogInfo(
                    "[HS] CorpseReport：任务已全清 —— 不取尸体进审判，改走原版「无尸体」分支（3 秒后按全清结算）。");
                return false;
            }
        }
    }
}
