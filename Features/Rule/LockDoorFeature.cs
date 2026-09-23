using System.Collections.Generic;
using BepInEx.Configuration;
using HarmonyLib;
using Protocol;
using Server.Game;
using HideAndSeek.Core;
using GameDeviceManager = Server.Game.DeviceManager;
using GameDoor = Server.Game.Door;
using GamePlayer = Server.Game.Player;

namespace HideAndSeek.Features.Rule
{
    /// <summary>
    /// 锁门：锁住以某点为圆心、给定半径内的所有门；黑方按 E 可秒解。
    ///
    /// 为什么不用原生锁定态（State = 2）：
    ///   客户端 Door.Interact（:6275-6286）在 StateList[0] == 2 时只播 LockedDoorSfx 就 return，
    ///   **不下发任何包**；特殊键路径同样被封（:6318-6321 返回空 → :8116 不发包）。
    ///   于是原生锁定期间客户端根本不给服务端发包，"黑方按 E 秒解"无法实现。
    ///
    /// 因此改用服务端隐形锁：线上状态始终保持 0/1，让客户端照常发 C_INTERACT_DOOR，
    /// 闸门放在服务端的 Door.Interact Prefix：
    ///   · 锁中 + 黑方按 E → 解除并放行（门随即打开）
    ///   · 锁中 + 其他人按 E → 吞掉（门保持关闭）
    /// 代价是失去原生 locked 视觉与进度条，用 LockedDoorSfx 音效补偿。
    ///
    /// 原版锁门的操作者是 Dark（Door.HandleEvent :162567 硬性要求），
    /// 而本模块的 NoMasterMind 取消了黑幕分配 —— 这个命令正好补上缺口，故判定用 Black。
    /// </summary>
    [PatchFeature(
        section: "LockDoor",
        description: "锁门命令：锁定半径内所有门，黑方按 E 秒解（服务端隐形锁）。",
        defaultEnabled: true,
        side: FeatureSide.Host)]
    internal static class LockDoorFeature
    {
        [ConfigField(0.3f, "锁定半径 = 地图短边 × 此比例。0.25 ≈ 1/4 地图，0.33 ≈ 1/3。", Min = 0.05f, Max = 1f)]
        public static ConfigEntry<float> RadiusRatio;

        [ConfigField(10, "自定义锁的持续秒数（到点自动解锁）。", Min = 1f, Max = 300f)]
        public static ConfigEntry<int> LockSeconds;

        /// <summary>当前处于自定义锁的门 ID。</summary>
        private static readonly HashSet<int> LockedDoors = new HashSet<int>();

        internal static int LockedCount => LockedDoors.Count;

        /// <summary>按配置算出锁定半径（地图短边 × 比例）。取不到地图数据时退化为 1000。</summary>
        internal static float GetRadius()
        {
            var map = Managers.Data?.MapData;
            if (map?.MapSize == null)
                return 1000f;

            float w = map.MapSize.X * 224f;          // 网格边长 224（AreaManager 同款常量）
            float h = map.MapSize.Y * 224f;
            float shortSide = w < h ? w : h;

            float ratio = RadiusRatio?.Value ?? 0.3f;
            return shortSide * ratio;
        }

        /// <summary>锁住以 center 为圆心、radius 为半径内的所有门，返回锁住的数量。</summary>
        internal static int LockAround(PosInfo center, float radius, GamePlayer byPlayer)
        {
            if (center == null)
                return 0;

            var manager = GameDeviceManager.Instance;
            if (manager?.Objects == null)
                return 0;

            float r2 = radius * radius;
            int count = 0;

            foreach (var device in manager.Objects)
            {
                if (!(device is GameDoor door))
                    continue;                        // Objects 里还有电箱/尸体等其它设备

                var info = door.DeviceInfo;
                if (info?.StateList == null || info.StateList.Count < 3)
                    continue;                        // Door 构造保证 3 项；防御异常数据

                var pos = info.Pos;
                if (pos == null)
                    continue;

                float dx = pos.X - center.X;
                float dy = pos.Y - center.Y;
                if (dx * dx + dy * dy > r2)
                    continue;

                door.CloseDoor();                    // State = 1：客户端 collider 生效，挡住所有人
                LockedDoors.Add(door.ID);
                count++;
            }

            if (count == 0)
                return 0;

            int seconds = LockSeconds?.Value ?? 10;

            // PushSurvivalJob 会被 ClearSurvivalJob 在阶段切换时整体清空，
            // 所以这里只需在开局/进侦探阶段显式 ClearAll，不必自己记定时器。
            TimeManager.Instance.PushSurvivalJob(seconds, delegate
            {
                if (LockedDoors.Count > 0)
                    Plugin.Log.LogInfo($"[HS] LockDoor：{seconds} 秒到，解锁 {LockedDoors.Count} 扇门。");
                LockedDoors.Clear();
            });

            Plugin.Log.LogInfo(
                $"[HS] LockDoor：黑方 #{byPlayer?.PublicInfo?.PlayerId} 锁住 {count} 扇门（半径 {radius:F0}，{seconds} 秒）。");
            return count;
        }

        /// <summary>清空自定义锁（开局与进入侦探阶段调用）。</summary>
        internal static void ClearAll()
        {
            if (LockedDoors.Count > 0)
                LockedDoors.Clear();
        }

        // ── 按 E 的闸门：锁中的门只放黑方通过 ────────────────────────
        [HarmonyPatch(typeof(GameDoor), "Interact")]
        internal static class DoorInteractHook
        {
            [HarmonyPrefix]
            private static bool Prefix(GameDoor __instance, GamePlayer player)
            {
                if (ModeRuntime.Bypass)
                    return true;
                if (__instance == null || !LockedDoors.Contains(__instance.ID))
                    return true;                     // 未被自定义锁 → 原版开关门

                // 锁中 + 黑方按 E → 秒解并放行（门随即打开）
                if (player != null && player.Color == EPlayerColor.Black)
                {
                    LockedDoors.Remove(__instance.ID);
                    Plugin.Log.LogInfo(
                        $"[HS] LockDoor：黑方 #{player.PublicInfo?.PlayerId} 秒解了门 #{__instance.ID}。");
                    return true;
                }

                return false;                        // 其他人：静默无视，门保持关闭
            }
        }

        // ── 生命周期 ────────────────────────────────────────────────
        [HarmonyPatch(typeof(GameRoom), "StartSurvive")]
        internal static class StartHook
        {
            [HarmonyPostfix]
            private static void Postfix() => ClearAll();
        }

        [HarmonyPatch(typeof(GameRoom), "StartDetective")]
        internal static class DetectiveHook
        {
            [HarmonyPostfix]
            private static void Postfix() => ClearAll();
        }
    }
}
