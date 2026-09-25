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
        [ConfigField(1059, "命令发放的物品 ID。默认 1059 fish_normal（普通的鱼）—— 蓝/紫/金三种鱼都能用来黏门。",
            Min = 1f, Max = 9999f)]
        public static ConfigEntry<int> LanternItemId;

        [ConfigField(true, "允许申领鱼。关掉后这个命令直接不存在。")]
        public static ConfigEntry<bool> AllowIssue;

        [ConfigField(300f, "配额窗口秒数。默认 300 = 每 5 分钟。", Min = 10f, Max = 3600f)]
        public static ConfigEntry<float> QuotaWindowSeconds;

        [ConfigField(4, "配额窗口内全房最多发几条（只统计命令发放的，钓鱼等原版途径不算）。", Min = 1f, Max = 100f)]
        public static ConfigEntry<int> QuotaMax;

        [ConfigField(150f, "同一个人的两次申领之间要隔多少秒。配额是全房 300 秒 4 条，" +
            "150 秒意味着一个人在窗口内最多拿 2 条（0/150），其余留给别人。",
            Min = 0f, Max = 3600f)]
        public static ConfigEntry<float> FishCooldown;

        [ConfigField(3, "黏门需要开合几次。3 = 开-关-开（每次状态切换算一次；原版每玩家 1 秒冷却，最快约 3 秒）。",
            Min = 2f, Max = 6f)]
        public static ConfigEntry<int> SealsNeeded;

        [ConfigField(6f, "开合计数的有效窗口（秒）。3 次开合最快跨 3 秒，留出反应余量。", Min = 1f, Max = 60f)]
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

        /// <summary>命令发放的鱼（默认 1059 = fish_normal，它的图标是"普通的鱼"）。</summary>
        private static int IssueFishId => LanternItemId?.Value ?? 1059;

        // 三种鱼都能用来黏门：1059 fish_normal / 1060 fish_rare / 1061 fish_gold。
        // 需求是"只发蓝鱼，但紫鱼金鱼具有相同效果" —— 发放固定一种，判定接受三种。
        private const int FishIdFrom = 1059;
        private const int FishIdTo = 1061;

        private static bool IsFish(int dataId) => dataId >= FishIdFrom && dataId <= FishIdTo;

        private static float Now => TimeManager.Instance?.SurviveTime ?? 0f;

        // ══ 对外：申领 ══════════════════════════════════════════════════

        /// <summary>
        /// 申领一条鱼。手上已有别的物品时**掉在脚下**（原版 CreateAndInsertInven 会强占手里）。
        /// 返回 false 时 <paramref name="text"/> 说明原因（已按文案表生成）。
        ///
        /// 配额是**全房**的滚动窗口（默认每 300 秒 4 条），且只统计"命令发放的" ——
        /// 钓鱼等原版途径拿到的鱼不受影响、也不占配额。
        /// </summary>
        internal static bool TryIssue(GamePlayer player, out string text)
        {
            text = null;
            if (player?.PublicInfo == null)
                return false;

            // 注：「开局 N 秒内不能申领」由命令注册表上的 `elapsed>=N` 条件承担（见 CommandFeature）。
            //
            // 注：**配额与冷却已交给命令引擎的「四层空间」执行**（fish 的定义里写着
            // QuotaMax/QuotaWindow/Cooldown/UsesPerPlayer）—— 原先在这里自管，是因为
            // 引擎的 UsesPerPlayer 一个 bool 同时决定「次数按谁算」与「冷却按谁算」，
            // 表达不了「全房配额 + 每人冷却」这种混合归属。
            // 引擎侧的校验顺序是「全房配额 → 每人配额 → 全房冷却 → 每人冷却 → 条件」，
            // 配额本来就在冷却之前，所以"卖空提示优先于冷却提示"这个需求也照旧成立。
            //
            // 因此这里**只负责发放**；被拒的情况根本不会走到这儿（引擎已拦并回执）。

            int pid = player.PublicInfo.PlayerId;
            float now = Now;

            try
            {
                if (!HideAndSeek.Features.Combat.ItemGrant.Give(player, IssueFishId, out bool dropped))
                    return false;

                Plugin.Log.LogInfo(
                    $"[HS] KeyLock：玩家 #{pid} 申领了一条鱼" +
                    (dropped ? "（手上已有物品，已落在脚下）" : "") +
                    $"，now {now:F1}s。");

                text = CommandFeature.Text("FishTaken");
                return true;
            }
            catch (global::System.Exception ex)
            {
                Plugin.Log.LogWarning($"[HS] KeyLock：发放鱼失败 — {ex.Message}");
                return false;
            }
        }

        /// <summary>滚动窗口内的"命令发放"时刻。原版途径（钓鱼等）不进这里。</summary>
        private static readonly List<float> _issuedTimes = new List<float>();

        /// <summary>PlayerId → 上次申领时刻。每人冷却（默认 150 秒）用。</summary>
        private static readonly Dictionary<int, float> _lastIssue = new Dictionary<int, float>();

        /// <summary>把滑出窗口的发放记录丢掉。</summary>
        private static void PruneQuota()
        {
            float window = QuotaWindowSeconds?.Value ?? 300f;
            float now = Now;
            _issuedTimes.RemoveAll(t => now - t > window);
        }

        // ══ 对外：给 LockDoorFeature 查询 ═══════════════════════════════

        /// <summary>这扇门是否被提灯合过。</summary>
        internal static bool IsSealed(int doorId) => Seals.ContainsKey(doorId);

        /// <summary>
        /// 这扇门上的鱼胶锁还剩多少秒（没锁返回 0）。
        ///
        /// 用 Ceiling 而不是直接截断：这里算的是"剩余"，截断会少算不到 1 秒，
        /// 而调用方拿它跟别处的剩余比大小（取较长者），
        /// 少算的这点正好会让 15.9 秒 vs 16 秒这种边界判错方向。
        /// </summary>
        internal static int RemainingSeconds(int doorId)
        {
            Seal seal;
            if (!Seals.TryGetValue(doorId, out seal))
                return 0;

            int left = (int)global::System.Math.Ceiling(seal.ExpireAt - Now);
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

                // 关于"1 秒开合冷却"：**不要去动它**（曾经试过清 _interactCooldownPlayers，已撤）。
                //
                // 原版 Server.Game.Door :162545 用 _interactCooldownPlayers 做门禁，命中就跳过
                // OpenDoor/CloseDoor —— 但它只影响"门真的被开关"，而本 Prefix 在原方法体
                // **之前**执行，所以被冷却忽略的那几次照样会记进计数。
                // 客户端 Door.Interact(:6275) 也没有任何输入冷却：只要门不是锁定态就直接发
                // C_INTERACT_DOOR。
                //
                // ⇒ "短时间连按 E 几次"天然就能凑够次数，冷却保持原样即可。
                //   第一次没锁上，先看日志里"记到第 N/3 次"涨不涨 —— 涨了就是次数没够，
                //   不涨才是别的问题。

                int doorId = __instance.ID;
                int pid = player.PublicInfo.PlayerId;
                int state = info.StateList[0];

                // ── ① 门是锁定态：判断要不要放行 ──
                // 这里一律**静默**：锁门/开门这类动作不该刷一堆文字提示，
                // 玩家按 E 没反应本身就是"打不开"的反馈（与"不是主人"那条一致）。
                if (state == 2)
                {
                    Seal seal;
                    if (!Seals.TryGetValue(doorId, out seal))
                        return true;                 // 不是鱼胶锁（纯广域锁）→ 交还原版与 LockDoorFeature

                    if (IsTangled(doorId))
                        return false;                // 两把锁 → 谁都不放行

                    if (Now < seal.SetAt)
                        return false;                // 胶未干 → 连黏门者也不能开

                    if (!seal.Owners.Contains(pid))
                        return false;                // 不是黏门者 → 无反应

                    Seals.Remove(doorId);
                    __instance.UnlockDoor();
                    __instance.OpenDoor();
                    Plugin.Log.LogInfo($"[HS] KeyLock：黏门者 #{pid} 打开了门 #{doorId}。");
                    return false;
                }

                // ── ② 门是开着/关着：手持鱼才算一次"开合" ──
                if (!HasLantern(player))
                    return true;

                if (CountSwing(pid, doorId))
                    TrySeal(__instance, player, doorId, pid);

                return true;                          // 无论是否黏门，原版开关门照常发生
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

            int need = SealsNeeded?.Value ?? 3;

            // 每次都记一行：不锁的时候，这行日志能直接说明"记到第几次"，
            // 不必再猜是没凑够次数、还是被原版的开合冷却吞掉了。
            // 只在手持鱼时才会走到这里，所以不会吵。
            Plugin.Log.LogInfo(
                $"[HS] KeyLock：玩家 #{pid} 门 #{doorId} 记到第 {times.Count}/{need} 次开合" +
                $"（窗口 {window:F0} 秒，还需 {need - times.Count} 次）。");

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

                // 同一扇门上可能已经有锁（广域锁 /lck，或别人先黏的一次）。按需求：
                // **两把锁重叠时取"剩余时间更长"的那个**，而不是比"初始总长"。
                //
                // 门这一侧的剩余 = StateList[1]（总时长） - StateList[2]（已流逝）；
                // 鱼这一侧还要看自己记的 ExpireAt —— 两侧可能因 TickDoor 的秒级步进而有偏差，
                // 所以跟 LockAround 一样**两边都取**，否则会把更长的旧锁缩短。
                int existing = info.StateList[0] == 2 ? info.StateList[1] - info.StateList[2] : 0;

                Seal seal;
                if (!Seals.TryGetValue(doorId, out seal))
                {
                    seal = new Seal();
                    Seals[doorId] = seal;
                }
                else if (seal.ExpireAt > now)
                {
                    int leftBySeal = (int)global::System.Math.Ceiling(seal.ExpireAt - now);
                    if (leftBySeal > existing)
                        existing = leftBySeal;
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

                // 不给玩家任何文字：黏门不是"命令的报告"，而是用物品做的一个动作。
                // 只有日志留痕（下面这行），玩家侧静默。
                Plugin.Log.LogInfo(
                    $"[HS] KeyLock：#{pid} 黏住了门 #{doorId}（{seconds} 秒" +
                    (existing > seconds ? $"，沿用更长剩余 {existing} 秒" : "") +
                    $"，黏门者共 {seal.Owners.Count} 人）。");
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

        /// <summary>手上是不是拿着一条能用的鱼（三种鱼都算）。</summary>
        private static bool HasLantern(GamePlayer player)
        {
            var hand = player?.Hand;
            return hand?.Info != null && IsFish(hand.Info.DataId);
        }

        private static void Reply(GamePlayer player, string text)
        {
            if (player?.Session == null || string.IsNullOrEmpty(text))
                return;

            // 与汽水那边逐字节相同的实现，已合并到 Core/ChatOut 的 ToPlayer 入口。
            player.Session.Send(ChatOut.ToPlayer(player, text));
        }

        internal static void ClearAll()
        {
            Seals.Clear();
            Swings.Clear();
            _outstanding = 0;

            // 配额与冷却**必须一起清**：它们记的是 TimeManager.SurviveTime，
            // 而那个值每局由 ResetSurvival() 设回 420（不是从 0）。不清的话，上一局记下的时刻（例如 250 秒）
            // 在新局里算出来是负数，RemoveAll 的 `now - t > window` 永远不成立
            // ⇒ 记录永不过期 ⇒ 第二局开局就"已售罄"、"CD 中"，人数再少也拿不到。
            _issuedTimes.Clear();
            _lastIssue.Clear();
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
