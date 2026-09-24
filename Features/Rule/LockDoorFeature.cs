using System.Collections.Generic;
using System.Reflection;
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
    /// 锁门：锁住半径内所有门。白方看到的是**原生锁定**（音效/进度条/按 E 无反应），
    /// 黑方看到的是**只是关着的门**，于是他能正常按 E —— 服务端收到包后替他秒解并开门。
    ///
    /// 为什么要伪造状态：
    ///   客户端 Door.Interact（:6275-6286）在 StateList[0] == 2 时只播 LockedDoorSfx 就 return，
    ///   **不下发任何包**。所以真实锁定态下黑方根本无法"按 E 秒解"。
    ///   而 Device.BroadcastState（:163469）发的是完整 DeviceInfo，客户端 Modify 会覆盖本地状态 ——
    ///   于是可以对该门**逐人**发送不同状态：白方收真实的 2，黑方收伪造的 1。
    ///
    /// 副作用（有意保留）：TickDoor 每秒广播锁定进度，黑方每收到一次就会闪一下锁定视觉。
    ///   这被当作"门确实被锁过"的提示接受，且不影响他按键。
    ///
    /// 原版锁门的操作者是 Dark（Door.HandleEvent :162567 硬性要求），而本模块的 NoMasterMind
    /// 取消了黑幕分配 —— 这个命令正好补上缺口，故判定用 Black。
    /// </summary>
    [PatchFeature(
        section: "LockDoor",
        description: "锁门命令：白方看到原生锁定，黑方看到只是关着（按 E 可秒解）。",
        defaultEnabled: true,
        side: FeatureSide.Host)]
    internal static class LockDoorFeature
    {
        [ConfigField(0.3f, "锁定半径 = 地图短边 × 此比例。0.25 ≈ 1/4 地图，0.33 ≈ 1/3。", Min = 0.05f, Max = 1f)]
        public static ConfigEntry<float> RadiusRatio;

        [ConfigField(10, "锁定持续秒数（写入门自身的锁定总时长，由原版 TickDoor 计时解锁）。", Min = 1f, Max = 300f)]
        public static ConfigEntry<int> LockSeconds;

        /// <summary>当前处于锁定的门 ID。仅用于判断"要不要给黑方伪造状态"。</summary>
        private static readonly HashSet<int> LockedDoors = new HashSet<int>();

        private static MethodInfo _tickDoor;

        internal static int LockedCount => LockedDoors.Count;

        /// <summary>锁定半径（地图短边 × 比例）。取不到地图数据时退化为 1000。</summary>
        internal static float GetRadius()
        {
            var map = Managers.Data?.MapData;
            if (map?.MapSize == null)
                return 1000f;

            float w = map.MapSize.X * 224f;          // 网格边长 224（AreaManager 同款常量）
            float h = map.MapSize.Y * 224f;
            float shortSide = w < h ? w : h;

            return shortSide * (RadiusRatio?.Value ?? 0.3f);
        }

        /// <summary>锁住以 center 为圆心、radius 内的所有门，返回锁住的数量。</summary>
        internal static int LockAround(PosInfo center, float radius, GamePlayer byPlayer)
        {
            if (center == null)
                return 0;

            var manager = GameDeviceManager.Instance;
            if (manager?.Objects == null)
                return 0;

            int seconds = LockSeconds?.Value ?? 10;
            float r2 = radius * radius;
            int count = 0;

            foreach (var device in manager.Objects)
            {
                if (!(device is GameDoor door))
                    continue;

                var info = door.DeviceInfo;
                if (info?.StateList == null || info.StateList.Count < 3)
                    continue;

                var pos = info.Pos;
                if (pos == null)
                    continue;

                float dx = pos.X - center.X;
                float dy = pos.Y - center.Y;
                if (dx * dx + dy * dy > r2)
                    continue;

                // 先登记再 LockDoor：LockDoor 会触发 BroadcastState，
                // 我们的 Postfix 靠 LockedDoors 判断是否要给黑方伪造状态
                LockedDoors.Add(door.ID);

                info.StateList[1] = seconds;         // 锁定总时长
                info.StateList[2] = 0;               // 已锁定进度归零
                door.LockDoor();                     // State = 2（白方看到原生锁定）

                // LockDoor 不会启动计时，反射跑一次 TickDoor；它会自续 PushSurvivalJob
                if (_tickDoor == null)
                    _tickDoor = AccessTools.Method(typeof(GameDoor), "TickDoor");
                _tickDoor?.Invoke(door, null);

                count++;
            }

            if (count == 0)
                return 0;

            Plugin.Log.LogInfo(
                $"[HS] LockDoor：黑方 #{byPlayer?.PublicInfo?.PlayerId} 锁住 {count} 扇门（半径 {radius:F0}，{seconds} 秒）。");
            return count;
        }

        /// <summary>清空锁定登记（开局与进入侦探阶段调用）。门自身的 State 由原版 TickDoor 收尾。</summary>
        internal static void ClearAll()
        {
            if (LockedDoors.Count > 0)
                LockedDoors.Clear();
        }

        /// <summary>给所有黑方补发一份"只是关着"的 DeviceInfo，覆盖掉他们刚收到的真实锁定态。</summary>
        private static void SendFakeStateToBlack(GameDoor door)
        {
            var room = GameRoom.Instance;
            if (room?.Players == null || door?.DeviceInfo == null)
                return;

            S_MODIFY_DEVICE packet = null;

            foreach (var player in room.Players)
            {
                if (player?.Session == null || player.Color != EPlayerColor.Black)
                    continue;

                // 每人一份副本：StateList 是共享引用，直接改会污染白方与世界状态
                var fake = door.DeviceInfo.Clone();
                fake.StateList[0] = 1;               // 对黑方而言：门只是关着

                packet = new S_MODIFY_DEVICE { Info = fake };
                player.Session.Send(packet);
            }
        }

        // ── ① 每次门状态广播后，向黑方补一份伪造状态 ──────────────────
        [HarmonyPatch(typeof(Device), nameof(Device.BroadcastState))]
        internal static class BroadcastStateHook
        {
            [HarmonyPostfix]
            private static void Postfix(Device __instance)
            {
                if (ModeRuntime.Bypass)
                    return;
                if (!(__instance is GameDoor door) || !LockedDoors.Contains(door.ID))
                    return;

                SendFakeStateToBlack(door);
            }
        }

        // ── ② 门真解锁时同步撤登记，免得继续伪造"关着" ────────────────
        [HarmonyPatch(typeof(GameDoor), nameof(GameDoor.UnlockDoor))]
        internal static class UnlockHook
        {
            [HarmonyPostfix]
            private static void Postfix(GameDoor __instance)
            {
                if (__instance != null)
                    LockedDoors.Remove(__instance.ID);
            }
        }

        // ── ③ 黑方按 E：解锁并开门（真实状态是 2，原版 Interact 会什么都不做）──
        [HarmonyPatch(typeof(GameDoor), "Interact")]
        internal static class DoorInteractHook
        {
            [HarmonyPrefix]
            private static bool Prefix(GameDoor __instance, GamePlayer player)
            {
                Diagnostics.Hit("LockDoor");
                if (ModeRuntime.Bypass)
                    return true;
                if (__instance?.DeviceInfo?.StateList == null
                    || __instance.DeviceInfo.StateList.Count == 0)
                    return true;
                if (__instance.DeviceInfo.StateList[0] != 2)
                    return true;                          // 不是锁定态 → 原版开关门
                if (player == null || player.Color != EPlayerColor.Black)
                    return true;                          // 白方根本发不出这个包，走到这也不会是白方

                LockedDoors.Remove(__instance.ID);
                __instance.UnlockDoor();                  // State 2 → 1（同时广播，黑方此时可见真实状态也无妨）
                __instance.OpenDoor();                    // State 1 → 0：门开了

                Plugin.Log.LogInfo(
                    $"[HS] LockDoor：黑方 #{player.PublicInfo?.PlayerId} 秒解并打开了门 #{__instance.ID}。");
                return false;
            }
        }

        // ── ④ 生命周期 ────────────────────────────────────────────────
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
