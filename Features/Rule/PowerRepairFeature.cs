using BepInEx.Configuration;
using HarmonyLib;
using Server.Game;
using HideAndSeek.Core;
using GameDeviceManager = Server.Game.DeviceManager;

namespace HideAndSeek.Features.Rule
{
    /// <summary>
    /// 电力恢复条件：允许白方"不必修完全部电箱"就恢复供电。
    ///
    /// 原版逻辑（AreaManager.RefreshLight :173490）：
    ///   n &gt;= 2 → 全图黑 + BroadcastFuseArrow 标记断电电箱
    ///   n == 0  → 全亮（case 0）
    ///   n == 1  → 只提醒黑方"还剩一个"，**不恢复**
    /// 所以"默认需要全部修好"是 case 1 不恢复导致的。
    ///
    /// 改法上不在 RefreshLight 里动 Area.IsLight —— 那样灯光只改了服务端字段，
    /// 客户端何时收到 S_AREA_PUBLIC 不受我们控制。改为拦截计数读取：
    /// 满足条件时上报 0，于是原版**完整走 case 0 的全亮 + 音效 + 重派发**，
    /// 一切同步都沿用原版路径。
    ///
    /// 判据用「**已修电箱数**」而不是「剩余未修数」：
    ///   剩余数的含义会随断电总量漂移（断电 2 个时"剩 1"= 修了 1 个，
    ///   断电 3 个时"剩 1"= 修了 2 个），而"修了 N 个就恢复"在任何总量下都一致。
    ///   已修数 = 本次断电峰值 - 当前剩余，峰值由本类在每次读数时维护。
    ///
    /// 对其它调用点的影响：DisconnetCable 里 `GetDisconnectFuseCount() == 2` 用于
    /// ClearFuseboxSabotage；本功能只在满足条件时把结果压成 0，且条件成立意味着
    /// 白方已修够，此时清不清任务标记都不影响供电，故无副作用。
    /// </summary>
    [PatchFeature(
        section: "PowerRepair",
        description: "电力恢复条件：已修电箱数达到该值即恢复供电（0 = 原版，需全部修好）。",
        defaultEnabled: true,
        side: FeatureSide.Host)]
    internal static class PowerRepairFeature
    {
        [ConfigField(1, "已修复的电箱数达到此值即恢复供电。0 = 原版行为（必须全部修好）；1 = 修好任意一个即恢复。",
            Min = 0f, Max = 10f)]
        public static ConfigEntry<int> RepairCount;

        /// <summary>本次断电观察到的最大断电数，即"总共坏了几个"。</summary>
        private static int _peak;

        [HarmonyPatch(typeof(GameDeviceManager), nameof(GameDeviceManager.GetDisconnectFuseCount))]
        internal static class DisconnectCountHook
        {
            [HarmonyPostfix]
            private static void Postfix(ref int __result)
            {
                if (ModeRuntime.Bypass)
                    return;

                // 记录本次断电的总量：读到的最大值就是坏掉的总数
                if (__result > _peak)
                    _peak = __result;

                // 全部修好时归零，为下一次断电重新计数
                if (__result == 0)
                {
                    _peak = 0;
                    return;                       // 原版自己就会走 case 0
                }

                int need = RepairCount?.Value ?? 0;
                if (need <= 0)
                    return;                       // 原版行为

                int repaired = _peak - __result;
                if (repaired >= need)
                    __result = 0;                 // 已修够 → 原版走 case 0（全亮 + 广播）
            }
        }

        // ── 生命周期：每局重置峰值 ──────────────────────────────────
        [HarmonyPatch(typeof(GameRoom), "StartSurvive")]
        internal static class StartHook
        {
            [HarmonyPostfix]
            private static void Postfix() => _peak = 0;
        }

        [HarmonyPatch(typeof(GameRoom), "StartLobby")]
        internal static class LobbyHook
        {
            [HarmonyPostfix]
            private static void Postfix() => _peak = 0;
        }
    }
}
