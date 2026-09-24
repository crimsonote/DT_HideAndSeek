using System;
using System.Reflection;
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
    /// 原版有四条通往调查阶段的路径。本功能拦住其中三条，手法一致 —— **都拦在"进审判的触发动作"
    /// 的入口**；第四条（限时归零）由 WhiteWinFeature 在判定点接管：
    ///   ① 首具尸体出现后 50~70 秒自动进入 —— Server.Game.Corpse 构造函数（:168824）
    ///        TimeManager.PushSurvivalJob(WaitDetectiveSecond, EndSurvival)
    ///      拦在 PushSurvivalJob 入口把延迟改成极大值：保留原版代码路径不变，但实际不再触发。
    ///      注：StateList[5]（客户端显示的"预计发现时刻"）改不动 —— 尸体状态在构造函数内部
    ///      就已对外同步，Postfix 改的只是服务端之后没人再读的副本，客户端仍显示原版的 50~70 秒。
    ///   ② 玩家走到尸体旁手动报警 —— Server.Game.Corpse.Interact（:168866）
    ///   ③ 任务进度顶满 100% —— MissionManager.ClearMission(:166690) 置 AllClear 后调
    ///        TriggerAllClearEnd(:166868)，由它取一具未发现尸体直接 DiscoverByTimeOver() 进审判。
    ///      这条既不过 PushSurvivalJob 也不过 Corpse.Interact，前两个闸门完全够不着；
    ///      拦法同样是拦入口 —— 丢弃原版这次取尸，改走 ④ 那一套结算。
    ///      必须配套结算而不能只丢弃：此时 AllClear 已为 true，GameRoom.SurvivalTick
    ///      首行 `if (State != Survive || MissionManager.Instance.AllClear) return;` 会直接返回、
    ///      时间不再走 —— 只拦不结算会让对局永久停在 Survive。
    ///   ④ 限制时间归零且有未发现尸体 —— GameRoom.SurvivalTick（:171256），
    ///      由 WhiteWinFeature 的 Prefix 接管判定（本玩法禁止报告，回退原版会进一个推不动的审判）。
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

        internal static int DelaySeconds => AutoDetectiveDelay?.Value ?? 1000000;

        /// <summary>
        /// 是否为"尸体自动进调查"的那次排程。
        /// Corpse.EndSurvival 是 private，故用「回调目标类型 + 方法名」双重匹配，
        /// 避免误伤 TimeManager 上其它 50~70 秒的生存任务。
        /// </summary>
        private static bool IsAutoDetectiveJob(Action action)
            => action != null && action.Target is GameCorpse && action.Method.Name == "EndSurvival";

        // ── 路径 ①：自动进入调查的排程时间 ──────────────────────────────
        // 已知边界（刻意不改，属既存漏洞）：Corpse.OnMigrationResume(:169026) 会在房主迁移后
        // 按 StateList[5] 的剩余时间重排同一个回调，其延迟不一定落在 50~70 内，因此下面的
        // 区间过滤挡不住它 —— 迁移后仍可能自动报告。
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
        //     if (corpse != null) corpse.DiscoverByTimeOver();            // 进审判
        //     else GameRoom.Instance.PushAfter(3000, ClearAllMission);
        // 它既不过 PushSurvivalJob，也不过 Corpse.Interact，前两个闸门完全够不着。
        //
        // 拦法与 ①② 同层：拦它的**入口**，丢弃原版这次取尸；结算复用 ④ 那一套
        // （WhiteWinFeature.TriggerWhiteWin），而不是自己复刻 ClearAllMission。
        //
        // 为什么不只丢弃：此刻 AllClear 已被 CheckAllClear(:166916) 置为 true，
        // GameRoom.SurvivalTick(:171247) 首行会因此直接 return，TimeManager.Tick() 不再被调用，
        // 对局会永久停在 Survive。
        //
        // 为什么拦入口、而不拦下游的 Corpse.DiscoverByTimeOver：后者是 ③ 与 ④ 的公共下游，
        // 拦它会把限时归零那条路径也一起接管（范围反而更大）。
        // TriggerAllClearEnd 只有 2 个调用点 —— ClearMission 的 `if (AllClear)` 分支(:166742)
        // 与 ResumeAllClearAfterMigration(:166885，房主迁移后恢复) —— 两者都以 AllClear == true 为前提，
        // 与限时归零路径完全不相交。
        //
        // MissionManager 是 internal，且 Harmony 无法把字符串类型名解析到 Assembly-CSharp，
        // 故用 HarmonyTargetMethod + AccessTools（与 MissionTimePenaltyFeature 同一写法）。
        [HarmonyPatch]
        internal static class AllClearEndHook
        {
            [HarmonyTargetMethod]
            private static MethodBase Target()
                => AccessTools.Method(AccessTools.TypeByName("Server.Game.MissionManager"),
                                      "TriggerAllClearEnd");

            [HarmonyPrefix]
            private static bool Prefix()
            {
                Diagnostics.Hit("CorpseReport");
                if (ModeRuntime.Bypass)
                    return true;

                GameRoom room = GameRoom.Instance;
                if (room == null)
                    return true;

                Plugin.Log.LogInfo("[HS] CorpseReport：任务已全清 —— 不取尸体进审判，3 秒后按全清结算。");

                // 原版这一支是 PushAfter(3000, ClearAllMission)；TriggerWhiteWin 与
                // ClearAllMission(:166889) 逐行等价，因此直接复用它并保留同样的 3 秒延迟。
                room.PushAfter(3000, delegate
                {
                    GameRoom target = GameRoom.Instance;
                    if (target != null)
                        WhiteWinFeature.TriggerWhiteWin(target);
                });
                return false;
            }
        }
    }
}
