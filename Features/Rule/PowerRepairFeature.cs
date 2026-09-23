using BepInEx.Configuration;
using HarmonyLib;
using Server.Game;
using HideAndSeek.Core;
using GameDeviceManager = Server.Game.DeviceManager;

namespace HideAndSeek.Features.Rule
{
    /// <summary>
    /// 电力恢复阈值：允许白方"不必修完全部电箱"就恢复供电。
    ///
    /// 原版逻辑（AreaManager.RefreshLight :173490）：
    ///   n &gt;= 2 → 全图黑 + BroadcastFuseArrow 标记断电电箱
    ///   n == 0  → 全亮（case 0）
    ///   n == 1  → 只提醒黑方"还剩一个"，**不恢复**
    /// 所以"默认需要全部修好"是 case 1 不恢复导致的。
    ///
    /// 改法上不在 RefreshLight 里动 Area.IsLight —— 那样灯光只改了服务端字段，
    /// 客户端何时收到 S_AREA_PUBLIC 不受我们控制。改为拦截计数读取：
    /// 当剩余断电数落在阈值内时上报 0，于是原版**完整走 case 0 的全亮 + 音效 + 重派发**，
    /// 一切同步都沿用原版路径。
    ///
    /// 对其它调用点的影响：DisconnetCable 里 `GetDisconnectFuseCount() == 2` 用于
    /// ClearFuseboxSabotage，阈值生效时 n==1 会返回 0，而 n==2 不受影响（阈值 ≤1），故无副作用。
    /// </summary>
    [PatchFeature(
        section: "PowerRepair",
        description: "电力恢复阈值：剩余未修电箱数不超过该值时即恢复供电（0 = 原版，需全部修好）。",
        defaultEnabled: true,
        side: FeatureSide.Host)]
    internal static class PowerRepairFeature
    {
        [ConfigField(1, "剩余未修电箱数 ≤ 此值时恢复供电。0 = 原版行为（必须全部修好）；1 = 修好任意一个即恢复。",
            Min = 0f, Max = 2f)]
        public static ConfigEntry<int> RepairThreshold;

        [HarmonyPatch(typeof(GameDeviceManager), nameof(GameDeviceManager.GetDisconnectFuseCount))]
        internal static class DisconnectCountHook
        {
            [HarmonyPostfix]
            private static void Postfix(ref int __result)
            {
                if (ModeRuntime.Bypass)
                    return;

                int threshold = RepairThreshold?.Value ?? 0;
                if (threshold <= 0)
                    return;                       // 原版行为

                // 落在阈值内即视为"已恢复" → 原版走 case 0（全亮 + 广播）
                if (__result > 0 && __result <= threshold)
                    __result = 0;
            }
        }
    }
}
