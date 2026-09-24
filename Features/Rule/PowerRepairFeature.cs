using BepInEx.Configuration;
using HarmonyLib;
using Server.Game;
using HideAndSeek.Core;
using GameDeviceManager = Server.Game.DeviceManager;
using GameFusebox = Server.Game.Fusebox;

namespace HideAndSeek.Features.Rule
{
    /// <summary>
    /// 电力恢复条件：允许白方"不必修完全部电箱"就恢复供电。
    ///
    /// 原版逻辑（AreaManager.RefreshLight :173490）：
    ///   n >= 2 → 全图黑 + BroadcastFuseArrow 标记断电电箱
    ///   n == 0  → 全亮（case 0）
    ///   n == 1  → 只提醒黑方"还剩一个"，**不恢复**
    /// 所以"默认需要全部修好"是 case 1 不恢复导致的。
    ///
    /// 判据用「已修电箱数」而不是「剩余未修数」：
    ///   剩余数的含义会随断电总量漂移（断电 2 个时"剩 1"= 修了 1 个，
    ///   断电 3 个时"剩 1"= 修了 2 个），而"修了 N 个就恢复"在任何总量下都一致。
    ///   已修数 = 本次断电峰值 - 当前剩余，峰值由本类在每次读数时维护。
    ///
    /// ★ 关键：光把计数压成 0 是不够的。
    ///   RefreshLight 的 case 0 只改 Area.IsLight（区域光照，即"屏幕亮"），
    ///   而 Fusebox.IsLight 仍是 false，于是：
    ///     ① 电箱实际仍是"可修"状态；
    ///     ② 更糟的是 Fusebox.ConnetCable 有 `if (!area.IsLight)` 这道门 ——
    ///        第 1 个修完后区域已亮，后续电箱再也修不动（实测"第二个被拦截"）。
    ///   所以必须先把每个电箱真的恢复，再让原版走 case 0。
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

        /// <summary>
        /// 把所有电箱真的恢复成"好的"。
        ///
        /// Fusebox.IsLight 是 `{ get; private set; }`，外部无法直接赋值，只能用反射；
        /// 同时要把 DeviceInfo.StateList[0] 归零（客户端凭它判断是否需要修），
        /// 否则客户端那边仍会显示成"可修"。
        /// </summary>
        private static void ForceRepairAll()
        {
            try
            {
                var dm = GameDeviceManager.Instance;
                if (dm?.Fuseboxes == null)
                    return;

                var setter = AccessTools.PropertySetter(typeof(GameFusebox), "IsLight");
                int fixedCount = 0;

                foreach (var fb in dm.Fuseboxes)
                {
                    if (fb?.DeviceInfo == null)
                        continue;
                    if (fb.IsLight && fb.DeviceInfo.StateList != null
                        && fb.DeviceInfo.StateList.Count > 0 && fb.DeviceInfo.StateList[0] == 0)
                        continue;

                    setter?.Invoke(fb, new object[] { true });
                    if (fb.DeviceInfo.StateList != null && fb.DeviceInfo.StateList.Count > 0)
                        fb.DeviceInfo.StateList[0] = 0;
                    fixedCount++;
                }

                // 清掉"可拆"任务标记，避免地图上留下点不掉的图钉
                try { dm.ClearFuseboxSabotage(); } catch { }

                Plugin.Log.LogInfo($"[HS] PowerRepair：已强制恢复 {fixedCount} 个电箱。");
            }
            catch (global::System.Exception ex)
            {
                Plugin.Log.LogWarning($"[HS] PowerRepair：强制恢复电箱失败 — {ex.Message}");
            }
        }

        [HarmonyPatch(typeof(GameDeviceManager), nameof(GameDeviceManager.GetDisconnectFuseCount))]
        internal static class DisconnectCountHook
        {
            [HarmonyPostfix]
            private static void Postfix(ref int __result)
            {
                Diagnostics.Hit("PowerRepair");

                if (ModeRuntime.Bypass)
                    return;

                // 记录本次断电的总量：读到的最大值就是坏掉的总数
                if (__result > _peak)
                    _peak = __result;

                Plugin.Log.LogInfo($"[HS] PowerRepair：读取断电数 {__result}（峰值 {_peak}）。");

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
                if (repaired < need)
                    return;                       // 还没修够，交给原版

                // 已修够：先把电箱真的修好，再把计数压成 0 让原版走 case 0（全亮 + 音效 + 重派发）
                ForceRepairAll();
                Plugin.Log.LogInfo($"[HS] PowerRepair：已修 {repaired} 个 >= {need}，已恢复全部电箱并上报 0。");
                __result = 0;
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