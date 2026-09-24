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
    /// 以太提灯（/lamp）：公共命令，黑白都能向器材柜申领；全场同时最多三盏，合门消耗即归还。
    ///
    /// 玩法：手持提灯在门口**开合两次**即「合门」——
    ///   · 合门后前若干秒「膜未凝」，连合门者自己也不能开；
    ///   · 膜凝之后这扇门只认合门者；
    ///   · 同一扇门被两个人各合过一次，两股以太互咬，**谁都开不了**；
    ///   · 提灯与广域锁（LockDoorFeature 的 /lck）同门时同样视为两把锁 → 谁都开不了，
    ///     且门的剩余锁定时间取两者中较长的那一个。
    ///
    /// 为什么"膜未凝时连自己也不能开"能零成本实现：
    ///   客户端 Door.Interact（:6275）在 StateList[0] == 2 时只播 LockedDoorSfx 就 return，
    ///   **不下发任何包**。所以对合门者也不伪造状态，他就自然开不了；
    ///   膜凝之后再给他补一份"只是关着"的伪造状态（与 LockDoorFeature 对黑方的做法同款），
    ///   他按 E 才会发包，服务端再放行。
    ///
    /// 状态字段沿用原版门：StateList[0] 0=开/1=关/2=锁定，[1]=锁定总时长，[2]=已流逝秒数（TickDoor :162640）。
    /// </summary>
    [PatchFeature(
        section: "KeyLock",
        description: "以太提灯：申领后在门口开合两次可给门上锁（膜凝后只认合门者；多把锁互咬则谁都开不了）。",
        defaultEnabled: true,
        side: FeatureSide.Host)]
    internal static class KeyLockFeature
    {
        [ConfigField(4006, "提灯用的物品 ID。默认借用未启用的 4006 LanternBlue（蓝提灯，Passive，不与任何玩法冲突）。",
            Min = 1f, Max = 9999f)]
        public static ConfigEntry<int> LanternItemId;

        [ConfigField(true, "允许申领提灯。关掉后 /lamp 这个命令直接不存在。")]
        public static ConfigEntry<bool> AllowIssue;

        [ConfigField(3, "全场同时最多几盏提灯。合门消耗视为归还，名额随之释放。", Min = 1f, Max = 20f)]
        public static ConfigEntry<int> MaxLanterns;

        [ConfigField(2, "合门需要开合几次（原版对每个玩家的开门/关门有 1 秒冷却，所以 2 次最快约 2 秒）。",
            Min = 2f, Max = 6f)]
        public static ConfigEntry<int> SealsNeeded;

        [ConfigField(5f, "开合计数的有效窗口（秒）。窗口内凑够次数才合门。", Min = 1f, Max = 30f)]
        public static ConfigEntry<float> SealWindow;

        [ConfigField(20f, "合门后的锁定秒数。若同门已有更长的锁，取较长者。", Min = 1f, Max = 300f)]
        public static ConfigEntry<int> SealSeconds;

        [ConfigField(3f, "「膜未凝」秒数：这段时间里连合门者自己也不能开。", Min = 0f, Max = 30f)]
        public static ConfigEntry<float> SettingSeconds;

        /// <summary>一扇门上的一次合门。</summary>
        private sealed class Seal
        {
            /// <summary>合过这扇门的人。≥2 即两股以太互咬。</summary>
            public readonly HashSet<int> Owners = new HashSet<int>();

            /// <summary>膜凝时刻（SurviveTime 秒）。</summary>
            public float SetAt;

            /// <summary>锁到期时刻（SurviveTime 秒）。</summary>
            public float ExpireAt;
        }

        private static readonly Dictionary<int, Seal> Seals = new Dictionary<int, Seal>();

        /// <summary>已发出、尚未归还的提灯数（场上在外的盏数）。</summary>
        private static int _outstanding;

        /// <summary>"玩家:门" → 窗口内的开合时刻。</summary>
        private static readonly Dictionary<string, List<float>> Swings =
            new Dictionary<string, List<float>>();

        private static MethodInfo _tickDoor;

        private static int LanternId => LanternItemId?.Value ?? 4004;

        private static float Now => TimeManager.Instance?.SurviveTime ?? 0f;

        // ══ 对外：申领 ══════════════════════════════════════════════════

        /// <summary>
        /// 申领一盏提灯。手上已有别的物品时**掉在脚下**（原版 CreateAndInsertInven 会强占手里）。
        /// 返回 false 时 <paramref name="text"/> 说明原因（已按文案表生成）。
        /// </summary>
        internal static bool TryIssue(GamePlayer player, out string text)
        {
            text = null;
            if (player?.PublicInfo == null)
                return false;

            // 注：「开局 N 秒内不能申领」由命令注册表上的 `elapsed>=N` 条件承担（见 CommandFeature），
            // 不在这里判 —— 那样才能在 .cfg 里改，而不是写死在代码里。

            int max = MaxLanterns?.Value ?? 3;
            if (_outstanding >= max)
            {
                text = CommandFeature.Text("LampEmpty");
                return false;
            }

            try
            {
                if (!HideAndSeek.Features.Combat.ItemGrant.Give(player, LanternId, out bool dropped))
                    return false;

                _outstanding++;
                int left = max - _outstanding;

                Plugin.Log.LogInfo(
                    $"[HS] KeyLock：玩家 #{player.PublicInfo.PlayerId} 申领提灯" +
                    (dropped ? "（手上已有物品，已落在脚下）" : "") +
                    $"，场上还有 {left} 盏。");

                // 回执里顺带把玩法讲清楚（三行，正好一条消息）
                text = CommandFeature.Text("LampTaken", "n", left.ToString())
                     + "\n" + CommandFeature.Text("LampHowTo")
                     + "\n" + CommandFeature.Text("LampTaboo");
                return true;
            }
            catch (global::System.Exception ex)
            {
                Plugin.Log.LogWarning($"[HS] KeyLock：发放提灯失败 — {ex.Message}");
                return false;
            }
        }

        // ══ 对外：给 LockDoorFeature 查询 ═══════════════════════════════

        /// <summary>这扇门是否被提灯合过。</summary>
        internal static bool IsSealed(int doorId) => Seals.ContainsKey(doorId);

        /// <summary>这扇门上的提灯锁还剩多少秒（没锁返回 0）。</summary>
        internal static int RemainingSeconds(int doorId)
        {
            Seal seal;
            if (!Seals.TryGetValue(doorId, out seal))
                return 0;

            int left = (int)(seal.ExpireAt - Now);
            return left > 0 ? left : 0;
        }

        /// <summary>
        /// 这扇门上是否"有两把锁"——两股以太互咬，或提灯锁与广域锁叠加。
        /// 按需求：这种情况**谁都开不了**。
        /// </summary>
        private static bool IsTangled(int doorId)
        {
            Seal seal;
            if (Seals.TryGetValue(doorId, out seal) && seal.Owners.Count >= 2)
                return true;

            // 只有真正处于锁定态的广域锁才算"第二把锁"
            return Seals.ContainsKey(doorId) && LockDoorFeature.IsLockingDoor(doorId);
        }

        // ══ 门交互：开合计数 + 合门 + 放行 ══════════════════════════════

        [HarmonyPatch(typeof(GameDoor), "Interact")]
        internal static class DoorInteractHook
        {
            [HarmonyPrefix]
            private static bool Prefix(GameDoor __instance, GamePlayer player)
            {
                Diagnostics.Hit("KeyLock");
                if (ModeRuntime.Bypass)
                    return true;

                var info = __instance?.DeviceInfo;
                if (info?.StateList == null || info.StateList.Count < 3)
                    return true;
                if (player?.PublicInfo == null)
                    return true;

                int doorId = __instance.ID;
                int pid = player.PublicInfo.PlayerId;
                int state = info.StateList[0];

                // ── ① 门是锁定态：判断要不要放行 ──
                if (state == 2)
                {
                    Seal seal;
                    if (!Seals.TryGetValue(doorId, out seal))
                        return true;                 // 不是提灯锁（纯广域锁）→ 交还原版与 LockDoorFeature

                    if (IsTangled(doorId))
                    {
                        Reply(player, CommandFeature.Text("LampTangled"));
                        return false;                // 两把锁 → 谁都不放行
                    }

                    if (Now < seal.SetAt)
                    {
                        Reply(player, CommandFeature.Text("LampNotSet"));
                        return false;                // 膜未凝
                    }

                    if (!seal.Owners.Contains(pid))
                        return false;                // 不是合门者 → 无反应

                    Seals.Remove(doorId);
                    __instance.UnlockDoor();
                    __instance.OpenDoor();
                    Reply(player, CommandFeature.Text("LampOpened"));
                    Plugin.Log.LogInfo($"[HS] KeyLock：合门者 #{pid} 打开了门 #{doorId}。");
                    return false;
                }

                // ── ② 门是开着/关着：手持提灯才算一次"开合" ──
                if (!HasLantern(player))
                    return true;

                if (CountSwing(pid, doorId))
                    TrySeal(__instance, player, doorId, pid);

                return true;                          // 无论是否合门，原版开关门照常发生
            }
        }

        /// <summary>记一次开合；窗口内凑够次数则返回 true。</summary>
        private static bool CountSwing(int pid, int doorId)
        {
            string key = pid + ":" + doorId;
            float now = Now;
            float window = SealWindow?.Value ?? 5f;

            List<float> times;
            if (!Swings.TryGetValue(key, out times))
            {
                times = new List<float>();
                Swings[key] = times;
            }

            times.RemoveAll(t => now - t > window);
            times.Add(now);

            int need = SealsNeeded?.Value ?? 2;
            if (times.Count < need)
                return false;

            times.Clear();
            return true;
        }

        private static void TrySeal(GameDoor door, GamePlayer player, int doorId, int pid)
        {
            try
            {
                var info = door.DeviceInfo;
                int seconds = SealSeconds?.Value ?? 20;
                float now = Now;

                // 门当前的剩余锁定（可能来自更长的广域锁）：按需求取较长者
                int existing = info.StateList[0] == 2 ? info.StateList[1] - info.StateList[2] : 0;

                Seal seal;
                if (!Seals.TryGetValue(doorId, out seal))
                {
                    seal = new Seal();
                    Seals[doorId] = seal;
                }
                seal.Owners.Add(pid);
                seal.SetAt = now + (SettingSeconds?.Value ?? 3f);
                seal.ExpireAt = now + (existing > seconds ? existing : seconds);

                if (existing < seconds)
                {
                    info.StateList[1] = seconds;      // 锁定总时长
                    info.StateList[2] = 0;            // 已流逝清零
                    door.LockDoor();                  // State → 2，并广播

                    // LockDoor 不会自己启动计时，反射跑一次 TickDoor
                    if (_tickDoor == null)
                        _tickDoor = AccessTools.Method(typeof(GameDoor), "TickDoor");
                    _tickDoor?.Invoke(door, null);
                }

                ConsumeLantern(player);

                Plugin.Log.LogInfo(
                    $"[HS] KeyLock：#{pid} 合上了门 #{doorId}（{seconds} 秒" +
                    (existing > seconds ? $"，沿用更长剩余 {existing} 秒" : "") +
                    $"，合门者共 {seal.Owners.Count} 人）。");

                Reply(player, CommandFeature.Text("LampSealed")
                            + "\n" + CommandFeature.Text("LampReturned"));
            }
            catch (global::System.Exception ex)
            {
                Plugin.Log.LogWarning($"[HS] KeyLock：合门失败 — {ex.Message}");
            }
        }

        /// <summary>合门消耗：提灯从手里消失、名额归还。</summary>
        private static void ConsumeLantern(GamePlayer player)
        {
            try
            {
                // 必须走原版 Player.RemoveHand(isForce: true)（:176718）：
                // 它会 S_REMOVE_ITEM 通知客户端、清 HandItemObjectId（并广播 ChangeHandItem）、
                // 再 ItemManager.RemoveItem 从服务端销毁。
                // 自己 RemoveDevice + 手改字段的话，客户端不会收到移除包，会一直显示拿着提灯。
                if (HasLantern(player))
                    player.RemoveHand(isForce: true);

                if (_outstanding > 0)
                    _outstanding--;
            }
            catch (global::System.Exception ex)
            {
                Plugin.Log.LogWarning($"[HS] KeyLock：归还提灯失败 — {ex.Message}");
            }
        }

        // ══ 状态伪造：膜凝之后，合门者才需要看到"只是关着" ══════════════

        [HarmonyPatch(typeof(Device), nameof(Device.BroadcastState))]
        internal static class BroadcastStateHook
        {
            [HarmonyPostfix]
            private static void Postfix(Device __instance)
            {
                Diagnostics.Hit("KeyLock");
                if (ModeRuntime.Bypass)
                    return;
                if (!(__instance is GameDoor door))
                    return;

                Seal seal;
                if (!Seals.TryGetValue(door.ID, out seal))
                    return;

                // 锁已作废 → 清掉记录、停止伪造。三种情况：
                //   1) 门不再是锁定态（StateList[0] != 2）—— TickDoor 自然到期解锁、或合门者自己开了门。
                //      用门状态判断比用时间可靠：TickDoor 由 PushSurvivalJob(1,...) 驱动，
                //      与 SurviveTime 之间可能有约 1 秒的偏差，只比时间会漏掉那一次广播。
                //   2) 锁的时间到了（兜底）。
                //
                // 这一步不能省：Seals 原本只在"合门者自己开门"和"开局/回大厅"时清理，
                // 自然到期不经过任何一处。少了它就会出现 —— 门已经解锁，合门者一开门、
                // 门广播新状态，这里又把伪造的"关着"发回去，于是**门永远打不开**。
                if (door.DeviceInfo.StateList[0] != 2 || Now >= seal.ExpireAt)
                {
                    Seals.Remove(door.ID);
                    return;
                }

                if (IsTangled(door.ID))
                    return;                          // 咬死 → 所有人看真实锁定态，谁都开不了
                if (Now < seal.SetAt)
                    return;                          // 膜未凝 → 连合门者也不给伪造，他自然也开不了

                SendFakeStateToOwners(door, seal);
            }
        }

        private static void SendFakeStateToOwners(GameDoor door, Seal seal)
        {
            var room = GameRoom.Instance;
            if (room?.Players == null || door?.DeviceInfo == null)
                return;

            foreach (var player in room.Players)
            {
                if (player?.Session == null || player.PublicInfo == null)
                    continue;
                if (!seal.Owners.Contains(player.PublicInfo.PlayerId))
                    continue;

                // 每人一份副本：StateList 是共享引用，直接改会污染白方与世界状态
                var fake = door.DeviceInfo.Clone();
                fake.StateList[0] = 1;               // 对合门者而言：门只是关着
                player.Session.Send(new S_MODIFY_DEVICE { Info = fake });
            }
        }

        // ══ 辅助 ════════════════════════════════════════════════════════

        private static bool HasLantern(GamePlayer player)
        {
            var hand = player?.Hand;
            return hand?.Info != null && hand.Info.DataId == LanternId;
        }

        private static void Reply(GamePlayer player, string text)
        {
            if (player?.Session == null || string.IsNullOrEmpty(text))
                return;

            player.Session.Send(new S_CHAT_MESSAGE
            {
                Type = EChatType.NormalChat,
                DeviceId = 0,
                Text = text,
                PlayerId = player.PublicInfo?.PlayerId ?? 0,
                Time = (int)Now,
                IsDead = false
            });
        }

        internal static void ClearAll()
        {
            Seals.Clear();
            Swings.Clear();
            _outstanding = 0;
        }

        [HarmonyPatch(typeof(GameRoom), "StartSurvive")]
        internal static class StartHook
        {
            [HarmonyPostfix]
            private static void Postfix() => ClearAll();
        }

        [HarmonyPatch(typeof(GameRoom), "StartLobby")]
        internal static class LobbyHook
        {
            [HarmonyPostfix]
            private static void Postfix() => ClearAll();
        }
    }
}
