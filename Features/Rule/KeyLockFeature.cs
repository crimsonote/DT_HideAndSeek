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

        /// <summary>
        /// 是否让上锁进度条"频闪"（在「当前进度」与「0」之间每秒交替）。
        /// **默认关** —— 这不是原始需求，只是实验；开着会让门头那条一闪一闪。
        /// </summary>
        [ConfigField(false, "上锁进度条是否频闪（每秒在进度与 0 之间交替）。默认关。")]
        public static ConfigEntry<bool> BlinkGauge;

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

        /// <summary>
        /// 门的「短保护」到期时刻（门 ID → 时刻）。
        ///
        /// 拿鱼的人每对门按一次 E 就把这里刷新成 now + 1 秒 —— 保护期内**其他人**开不了这扇门，
        /// 但要一直护住就得一直按，直到门真正被锁上。这样既不会让"什么都不做就白锁 6 秒"
        /// 破坏平衡，也给了黏门者一个"护住正在上锁的门"的手段。
        /// </summary>
        private static readonly Dictionary<int, float> GuardUntil = new Dictionary<int, float>();

        /// <summary>保护时长（秒）。可调，别太长 —— 太长就退化成"整段锁死"了。</summary>
        [ConfigField(1f, "拿鱼对门按 E 后，该门不被他人打开的时长（秒）。每按一次刷新。",
            Min = 0.2f, Max = 6f)]
        public static ConfigEntry<float> GuardSeconds;

        /// <summary>这扇门的黏门者是不是他（保护不该拦住黏门者自己）。</summary>
        private static bool IsSealOwner(int doorId, int pid)
        {
            Seal s;
            return Seals.TryGetValue(doorId, out s) && s.Owners != null && s.Owners.Contains(pid);
        }

        /// <summary>已发出、尚未归还的提灯数（场上在外的盏数）。</summary>
        private static int _outstanding;

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

                // ── ⓪ 短保护：拿鱼的人按 E ⇒ 这扇门在 ~1 秒内不被**其他人**打开 ──
                //
                // 为什么不整段锁死：上锁流程是「先合门 → 等 SealWindow 秒 → 才进入锁定态」，
                // 若在"等"的整段里谁都开不了，等于什么都不做就白锁 6 秒，从平衡上太激。
                // 现在的规则与玩家操作对齐：**每按一次 E 刷新 1 秒保护**，要一直护住就得一直按，
                // 直到门真正被锁上。
                //
                // 保护期内**谁被放行**：黏门者（他要连按凑开合次数）+ 手里拿着鱼的人
                // （保护就是他们按出来的，不是他们的话一按 E 会先被自己刚设的保护拦掉）。
                bool holding = HasLantern(player);

                // ── 惰性过期：保护期到期而**没有下一次有效操作** ⇒ 保护失效 + 进度清零 ──
                // 顺手在按 E 时判定，省一个每秒钩子。到期后门立刻恢复"任何人可正常打开"。
                Attempt att;
                if (Attempts.TryGetValue(doorId, out att) && Now >= att.GuardUntil)
                {
                    Attempts.Remove(doorId);
                    GuardUntil.Remove(doorId);
                    ResetGauge(__instance);
                    Plugin.Log.LogInfo(
                        $"[HS] KeyLock：门 #{doorId} 保护期到期，上锁进度已清零（原发起者 #{att.OwnerPid}）。");
                }

                // 保护期内该门**不受其他人影响**：别人（无论是否拿着鱼）开不了、也推进不了。
                // 只有发起者本人、以及已经黏住这扇门的人放行。
                float guardLeft;
                if (GuardUntil.TryGetValue(doorId, out guardLeft) && Now < guardLeft)
                {
                    bool isOwner = Attempts.TryGetValue(doorId, out att) && att.OwnerPid == pid;
                    if (!isOwner && !IsSealOwner(doorId, pid))
                        return false;
                }

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

                // ── ② 门是开着/关着：只有"发起者本人 + 当时手持鱼"才算一次**有效操作** ──
                //
                // 规格：忽略无关操作 —— 其他人拿鱼按 E、以及原发起者**丢鱼之后**按 E，
                // 都不算"下一次有效操作"（后者连这里都到不了：`holding` 已为 false）。
                if (!holding)
                    return true;

                Attempt cur;
                if (Attempts.TryGetValue(doorId, out cur) && cur.OwnerPid != pid)
                    return true;                      // 这扇门的锁归别人 ⇒ 本次按键完全无效

                if (CountSwing(__instance, pid, doorId))
                    TrySeal(__instance, player, doorId, pid);
                else
                    GuardUntil[doorId] = Now + (GuardSeconds?.Value ?? 1f);   // 有效操作 ⇒ 刷新保护
                Diagnostics.Hit("KeyLockGuard");

                return true;                          // 无论是否黏门，原版开关门照常发生
            }
        }

        /// <summary>
        /// 一次"锁门尝试" —— **每扇门至多一条**，是本功能的唯一真相源。
        ///
        /// 门的 `State == 2` 与进度条槽位都只是由它派生出来的表现，由本模型写、由本模型撤。
        /// 规则（用户规格）：
        ///   · 谁先开始，这扇门的锁就归谁；别人拿鱼按 E **完全无效**
        ///   · 发起者本人持鱼按 E 才算"有效操作"：刷保护期 + 计数
        ///   · 保护期内门不受其他人影响（也开不了）
        ///   · 保护期到期而**没有下一次有效操作** ⇒ 保护失效 + 进度清零，门恢复可被任何人打开
        ///   · 前 `SealsNeeded` 次不显示进度条；之后每次额外有效 E ⇒ 条**流失**；
        ///     近空/全空 ⇒ 交回 `TrySeal`（= 原版锁门）
        /// </summary>
        private sealed class Attempt
        {
            /// <summary>发起者 PlayerId（门锁归他）。</summary>
            public int OwnerPid;

            /// <summary>有效敲击数（只数发起者本人持鱼的 E）。</summary>
            public int Taps;

            /// <summary>保护期到期时刻（每次有效敲击刷新）。</summary>
            public float GuardUntil;
        }

        /// <summary>门 ID → 那次锁门尝试。</summary>
        private static readonly Dictionary<int, Attempt> Attempts = new Dictionary<int, Attempt>();

        /// <summary>第几声"额外 E"把进度条走空（走空即上锁）。</summary>
        [ConfigField(3, "前 SealsNeeded 次不显示进度条；此后每次额外按 E 让进度条流失，流失这么多下后上锁。",
            Min = 1f, Max = 10f)]
        public static ConfigEntry<int> DrainTaps;

        /// <summary>
        /// 正在充能的门：门 ID → (门对象, 当前格数, 总量)。
        ///
        /// 用途只有一个 —— **频闪**：需求要"锁门进度条在推进中闪"，也就是在
        /// 「当前进度」与「0」之间交替。客户端 `Update` 会不会把值自己爬一格（:90076）
        /// 还没实机验证过，所以这里不依赖它：**每秒交替重发两个值**，
        /// 无论客户端自不自走，看上去都是"亮一下、灭一下"。
        /// </summary>
        private static readonly Dictionary<int, (GameDoor door, int cur, int total)> Charging =
            new Dictionary<int, (GameDoor, int, int)>();

        /// <summary>频闪相位：true = 这次发"0"，false = 发"当前进度"。</summary>
        private static bool _blinkOff;

        /// <summary>
        /// 把"上锁进度"推到门自己的设备进度条上。
        ///
        /// 客户端 `Door.RefreshState`(:6134) 会执行
        /// `UI_DeviceCasting.SetInfo(StateList[2], StateList[1])`(:90065) ——
        /// 也就是拿 `StateList[2]` 当**当前值**、`StateList[1]` 当**总量**，
        /// 画在门的 `Casting` 子节点上（世界空间，就在门上方）。所以只要写这两个格子再广播即可。
        ///
        /// 说明两点：
        ///   · 门原本用这两个格子表示"黑方正在破坏门"的进度；我们在**上锁的这几秒**占用它，
        ///     这是需求要的"实验性门锁进度条"。
        ///   · 配色是客户端写死的紫红（`Door.RefreshState` 传 `isSabotage: true`，:6132），
        ///     房主端改不了 —— 已记在 `.tmps\锁门进度条-备忘录.md`。
        /// </summary>
        private static void PushGauge(GameDoor door, int cur, int total)
        {
            if (door == null || total <= 0)
                return;

            var list = door.DeviceInfo?.StateList;
            if (list == null || list.Count < 3)
                return;                                 // 不是带进度条的门

            // 门**已经正式锁定**（StateList[0] == 2）时不要写槽。
            //
            // 注意这条护栏**不阻止任何上锁动作**：它只跳过"画进度条"。
            // 没锁上的门照样能被锁 —— 另一条鱼再黏一次、或 /lock 广域锁，都不受影响
            // （那时 StateList[0] != 2，TrySeal 的 existing 恒为 0，按正常秒数上锁）。
            //
            // 它防的只有这一个场景：门已锁住、而有人还拿鱼对它敲 E。
            // 若不跳过，StateList[2]（此刻是**门那把锁已流逝的秒数**）会被我们改写成
            // "还差几下"这种小数字 ⇒ 之后 TrySeal 读到 existing = StateList[1] - StateList[2]
            // 被**抬高**，于是 seal.ExpireAt 记成比真实更长的锁（我们这边误以为门锁得更久）。
            if (list[0] == 2)
                return;

            // 登记/注销"正在充能的门"，供每秒频闪使用
            if (cur > 0 && cur < total)
                Charging[door.ID] = (door, cur, total);
            else
                Charging.Remove(door.ID);

            try
            {
                list[1] = total;
                list[2] = cur;
                door.BroadcastState();
            }
            catch (global::System.Exception ex)
            {
                Plugin.Log.LogWarning($"[HS] KeyLock：进度条推送失败 — {ex.Message}");
            }
        }

        /// <summary>
        /// 把门头进度条**还原成"没有进度"的自然状态**。
        ///
        /// 客户端 `UI_DeviceCasting.SetInfo(cur, total)`（:90065-90071）的可见规则是
        /// `gameObject.SetActive(cur < total)` —— 所以写 (0,0) 就等于"不显示"。
        /// 必须显式还原：这两格是**设备自身状态**，每次 `BroadcastState` 都会重画一遍
        /// （空手开门也会触发一次）⇒ 不还原的话，半途停手留下的 2/3 会一直挂在门上，
        /// 之后任意一次状态广播都会把它重新画出来。
        /// </summary>
        private static void ResetGauge(GameDoor door)
        {
            if (door?.DeviceInfo?.StateList == null)
                return;
            var list = door.DeviceInfo.StateList;
            if (list.Count < 3 || list[0] == 2)
                return;                                 // 不是带条的门 / 门已被正式锁定（那是它自己的值）
            try
            {
                list[1] = 0;
                list[2] = 0;
                door.BroadcastState();
            }
            catch { /* 单个门失败不影响其它 */ }
        }

        /// <summary>
        /// 记一次**有效**敲击（调用点已保证：发起者本人 + 当时手持鱼）。
        /// 返回 true 表示"进度已空，该上锁了"（交给 <see cref="TrySeal"/> = 原版锁门）。
        ///
        /// 不做"N 秒内连敲 N 下"的时间门槛：原版那 1 秒开合冷却只拦"门真的被开关"，
        /// 而本 Prefix 在原方法体之前执行；客户端门交互也没有输入冷却（:6275）
        /// ⇒ 每一次按 E 都会走到这里。
        /// </summary>
        private static bool CountSwing(GameDoor door, int pid, int doorId)
        {
            float now = Now;
            int need = SealsNeeded?.Value ?? 3;        // 前 need 次：什么都不显示
            int drainTotal = DrainTaps?.Value ?? 3;    // 之后每次额外 E 让条流失，流失满即上锁
            float guard = GuardSeconds?.Value ?? 1f;

            Attempt a;
            if (!Attempts.TryGetValue(doorId, out a))
            {
                // 谁先开始，这扇门的锁就归谁；此后别人按 E 一律无效（判定在调用点）
                a = new Attempt { OwnerPid = pid, Taps = 1, GuardUntil = now + guard };
                Attempts[doorId] = a;
                ResetGauge(door);                      // 前几次不显示进度条
                Plugin.Log.LogInfo(
                    $"[HS] KeyLock：玩家 #{pid} 开始对门 #{doorId} 上锁（1/{need}，未显示进度条）。");
                return false;
            }

            // 有效操作：刷新保护期 + 计数
            a.GuardUntil = now + guard;
            a.Taps++;

            if (a.Taps <= need)
            {
                Plugin.Log.LogInfo(
                    $"[HS] KeyLock：玩家 #{pid} 门 #{doorId} 敲击 {a.Taps}/{need}（未显示进度条）。");
                return false;
            }

            // 第 need 次之后的每一次额外 E：进度条出现并**逐次流失**
            int extra = a.Taps - need;
            int left = drainTotal - extra;
            if (left < 0)
                left = 0;

            PushGauge(door, left, drainTotal);
            Plugin.Log.LogInfo(
                $"[HS] KeyLock：玩家 #{pid} 门 #{doorId} 上锁条 {left}/{drainTotal}（第 {extra} 次额外敲击）。");

            if (left > 0)
                return false;

            Attempts.Remove(doorId);                   // 走空 ⇒ 交还原版锁门
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

        /// <summary>
        /// 每秒把"正在充能的门"的进度条在「当前进度」与「0」之间交替一下 —— 即需求要的频闪。
        /// `SurviveTime` 是 int 秒（1 Hz），正好天然是 1 Hz 的闪烁节拍。
        /// </summary>
        [HarmonyPatch(typeof(GameRoom), "SurvivalTick")]
        internal static class BlinkHook
        {
            [HarmonyPostfix]
            private static void Postfix()
            {
                if (ModeRuntime.Bypass || Charging.Count == 0)
                    return;
                if (BlinkGauge == null || !BlinkGauge.Value)
                    return;                          // 默认关：不闪

                _blinkOff = !_blinkOff;
                var room = GameRoom.Instance;
                List<int> stale = null;

                foreach (var kv in Charging)
                {
                    var door = kv.Value.door;
                    // 门没了 / 房间不在生存阶段 ⇒ 撤掉登记（否则会一直闪）
                    if (door == null || room?.State != EGameState.Survive)
                    {
                        (stale ?? (stale = new List<int>())).Add(kv.Key);
                        continue;
                    }

                    var list = door.DeviceInfo?.StateList;
                    if (list == null || list.Count < 3)
                        continue;

                    try
                    {
                        list[1] = kv.Value.total;
                        list[2] = _blinkOff ? 0 : kv.Value.cur;   // 交替：有时发 0，有时发真实进度
                        door.BroadcastState();
                    }
                    catch { /* 单次失败忽略 */ }
                }

                if (stale != null)
                {
                    foreach (int id in stale)
                        Charging.Remove(id);
                }
            }
        }

        internal static void ClearAll()
        {
            Seals.Clear();
            _outstanding = 0;
            GuardUntil.Clear();     // 短保护也记的是 SurviveTime
            Attempts.Clear();       // 锁门尝试（唯一真相源）不能跨局
            Charging.Clear();       // 频闪登记同理 —— 不清会跨局残留（下一局开局门就被"保护"住）

            // 配额与冷却**必须一起清**：它们记的是 TimeManager.SurviveTime，
            // 而那个值每局由 ResetSurvival() 设回 420（不是从 0）。不清的话，上一局记下的时刻（例如 250 秒）
            // 在新局里算出来是负数，RemoveAll 的 `now - t > window` 永远不成立
            // ⇒ 记录永不过期 ⇒ 第二局开局就"已售罄"、"CD 中"，人数再少也拿不到。
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
