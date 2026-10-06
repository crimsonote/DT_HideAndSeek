using System;
using System.Collections.Generic;
using BepInEx.Configuration;
using DummyClient;      // IPacketSink（拦 C_DROP_ITEM 用；照 ReplayFeature / CommandFeature 的写法）
using HarmonyLib;
using Protocol;
using Server;          // ObjectUtils（CreateItem）在这里，不在 Server.Game
using Server.Game;
using HideAndSeek.Core;
using GameDeviceManager = Server.Game.DeviceManager;   // AGENTS 坑 #1：同名类必须显式限定
using GamePlayer = Server.Game.Player;
using GameRoom = Server.Game.GameRoom;
using GameSkill = Server.Game.SkillComponent;
using GameSummon = Server.Game.Summon;

namespace HideAndSeek.Features.Skill
{
    /// <summary>
    /// 澪 · 定点传送 —— 把「小熊」技能的第二阶段从**切换视野**改造成**传送到小熊处**。
    ///
    /// 原版链路（行号 = .tmps/decomp/Assembly-CSharp.decompiled.cs）：
    ///   按技能 → UseActiveSkill(:177213) → UseMarioNette(:177279)，按 Owner.SkillState 分三态：
    ///     SkillState == 1 ⇒ SummonMarionette(:177299)  放熊（脚下），SkillState = 2
    ///     SkillState == 2 ⇒ PossessMarionette(:177325) 切视野（State = Possess + CameraTargetId = 熊 id），SkillState = 3
    ///     SkillState == 3 ⇒ UnpossessMarionette(:177341) 退附身
    ///   而"切视野"其实**不是本地行为**：服务端只写 CameraTargetId(:175242)，
    ///   相机由区域广播 S_AREA_PUBLIC{ CameraTargetId }(:173335/:173363 → 客户端 :29586/:29609) 驱动。
    ///   ⇒ **拦掉 PossessMarionette 就等于取消了切视野**，不需要任何客户端补丁。
    ///
    /// 本功能的改造（全部走服务端包，对所有玩家生效）：
    ///   ① 按技能 = 把主人 `Move(熊坐标, force: true)`（:175883；force 会把同步升级为 S_RESPAWN 全服广播）
    ///      + 黑洞特效 + 1 秒操作锁（默认 `EBuffType.Stop`：只锁操作、**不丢手上的物品**；
    ///      `Stun` 会经客户端 CancelAllInteract(:14906) → DropHand(:4754) 把汽水丢到地上，故默认不用）。
    ///   ② 每次传送后 `CoolSkill(25)`（:177253，private ⇒ 反射调用）：
    ///      它一次做完「图标灰化 + 下发数字 + **杀掉旧的解锁计时器**」。最后一项是关键 ——
    ///      自己重发 S_COOLTIME_SKILL 不会取消 `_cooltimeJob`，25 秒后技能会提前解锁。
    ///   ③ 每只熊限 N 次；用尽即主动 `ResetMarionette()`(:177357) 回收小熊。
    ///   ④ 小熊寿命 60 秒 → 可配（默认 300）；改写 `PushSurvivalJob(60, ResetMarionette)`(:177304)。
    ///   ⑤ 小熊消失的三条路（回收 / 超时 / 用尽）**都汇到 `ResetMarionette()`**，
    ///      所以它的 Postfix 一处即可覆盖：清账本 + `CoolSkill(60)` 换成"放置 CD"。
    ///      原版那里已经 CoolSkill(30)，我们再调一次会把那枚 30 秒任务 Kill 掉并替换。
    ///   ⑥ 传送瞬间，**在施法者自己的屏幕上**临时"手持汽水"（默认 1039 = SHAKER_BALL，
    ///      **唯一**带进度条的汽水），进度条从 24 降到 16（下降 1/3 槽位），放完就还原。
    ///      ⚠ 这是**纯本机幻觉**（房主 2026-10-06 口径）：换汽水只是为了显示进度条而做的变通，
    ///      不是玩法改动 ⇒ 相关包**只发给本人**，服务端权威手部（`Player.Hand` /
    ///      `HandItemObjectId` / `PublicInfo.HandItemId`）全程不动，**别人看到的他还是原来的样子**；
    ///      进度条窗口内还会忽略他的「丢弃物品」与「拾取物品」操作（既不许把幻觉汽水弄丢，
    ///      也不许把真实原物品丢出去；被打断时立刻重申持有，**进度条要一路放到收尾**）。
    ///      ⚠ 进度条必须靠"客户端真的持有 1039"（客户端 Inventory.InsertHand → StartHand → DataId==1039
    ///      才 ShowItemUI&lt;UI_ShakingSlider&gt;），所以仍然要发 `S_ADD_ITEM` —— 但收件人只有他一个。
    ///      ⚠ 原版 SetInfo(:91069) 是**直接赋值**、没有补间 ⇒ "下降动画"只能由服务端按节拍连发递减值。
    ///
    /// 跨局：`ResetSurvival()` 每局把 `SurviveTime` 设回 420（:178650）⇒
    /// 用 `SurviveTime` 记的时刻在新局会算成负数（永不过期）。本功能不按时刻记账（只记"用了几次"），
    /// 但**手部换物**必须跨局收尾，否则会把汽水带进下一局 ⇒ 三个清理钩子（StartSurvive / StartLobby /
    /// ChangeGameState != Survive）都在本类里。
    /// </summary>
    [PatchFeature(
        section: "MioTeleport",
        description: "澪：放小熊后按技能＝传送到小熊处（不再切视野），每只熊可传 N 次、每次带传送 CD；" +
                     "小熊消失后换成放置 CD。传送附带黑洞特效、1 秒操作锁，" +
                     "并在施法者自己屏幕上临时显示汽水进度条（降 1/3，只他本人可见）。",
        defaultEnabled: false,
        side: FeatureSide.Host)]
    internal static class MioTeleportFeature
    {
        // ══════════════════ 配置 ══════════════════

        [ConfigField(300,
            "小熊寿命（秒）。原版 60。小熊到点会自动消失（并进入放置 CD）。",
            Min = 10f, Max = 3600f)]
        public static ConfigEntry<int> BearLifetimeSec;

        [ConfigField(25,
            "每次传送后的技能冷却（秒）。",
            Min = 0f, Max = 600f)]
        public static ConfigEntry<int> TeleportCdSec;

        [ConfigField(3,
            "每只小熊允许的传送次数。用尽即立刻回收小熊，并进入放置 CD。",
            Min = 1f, Max = 50f)]
        public static ConfigEntry<int> MaxTeleports;

        [ConfigField(60,
            "小熊消失后的技能冷却（秒）＝下一次放置小熊前要等多久。",
            Min = 0f, Max = 600f)]
        public static ConfigEntry<int> RecallCdSec;

        [ConfigField(1000,
            "传送后锁操作的毫秒数。用 EBuffType.Stop（只锁操作，不会丢下手上的物品）。",
            Min = 0f, Max = 10000f)]
        public static ConfigEntry<int> LockMs;

        [ConfigField(false,
            "锁操作改用 EBuffType.Stun（带眩晕动画）。" +
            "⚠ Stun 会让客户端 CancelAllInteract → DropHand，把刚换上的汽水（以及原本手上的东西）丢到地上，" +
            "与「临时换汽水」直接冲突；只在确认这套表现可接受时才打开。")]
        public static ConfigEntry<bool> UseStunInsteadOfStop;

        [ConfigField("TeleportVfx",
            "传送特效类型（填 EEffectType 的名字）。默认 TeleportVfx = 原版**普通传送门**（拉杆）用的那一款；" +
            "想换黑洞就填 BlackHoleVfx（它还能按包内坐标渲染，AOI 剔除时不会丢）。")]
        public static ConfigEntry<string> VfxType;

        [ConfigField("TeleportSfx",
            "传送音效（填 ESoundType 的名字；填 none 关闭）。默认 TeleportSfx = 原版传送门同款。")]
        public static ConfigEntry<string> SfxType;

        [ConfigField(896f,
            "传送特效/音效的可见（可听）半径。原版黑洞技能用的是 1792。",
            Min = 100f, Max = 5000f)]
        public static ConfigEntry<float> VfxRange;

        [ConfigField(true, "传送时把手上物品临时换成汽水，并播放进度条下降。")]
        public static ConfigEntry<bool> SwapToSoda;

        [ConfigField(1039,
            "汽水物品 ID。1039 = SHAKER_BALL —— 全部物品里**唯一**带 UI_ShakingSlider 进度条的汽水。",
            Min = 1f, Max = 99999f)]
        public static ConfigEntry<int> SodaItemId;

        [ConfigField(24,
            "进度条满值（槽位刻度；条 = 值/满值）。" +
            "条反映的是「剩余使用次数」：剩余次数 ÷ 总次数 × 满值。" +
            "3 次上限时依次是 24 → 16 → 8 → 0。",
            Min = 1f, Max = 24f)]
        public static ConfigEntry<int> SodaMaxValue;

        [ConfigField(80,
            "进度条每一步的间隔（毫秒）。原版进度条没有补间，平滑下降靠服务端按这个节拍连发递减值。",
            Min = 20f, Max = 1000f)]
        public static ConfigEntry<int> SodaStepMs;

        [ConfigField(500,
            "进度条降到终点后，停留多久再还原原物品（毫秒）。",
            Min = 0f, Max = 5000f)]
        public static ConfigEntry<int> SodaHoldMs;

        // 配置在 PatchLoader.Bind 之前是 null（与其它功能一致，全部用 ?? 兜底）
        private static int BearLifetimeValue => BearLifetimeSec != null ? BearLifetimeSec.Value : 300;
        private static int TeleportCdValue => TeleportCdSec != null ? TeleportCdSec.Value : 25;
        private static int MaxTeleportsValue => MaxTeleports != null ? MaxTeleports.Value : 3;
        private static int RecallCdValue => RecallCdSec != null ? RecallCdSec.Value : 60;
        private static int LockMsValue => LockMs != null ? LockMs.Value : 1000;
        private static bool UseStunValue => UseStunInsteadOfStop != null && UseStunInsteadOfStop.Value;
        private static float VfxRangeValue => VfxRange != null ? VfxRange.Value : 896f;

        /// <summary>传送特效类型；填了不认识的名字时回退 TeleportVfx（普通传送门那款）。</summary>
        private static EEffectType VfxTypeValue
        {
            get
            {
                string raw = VfxType != null ? VfxType.Value : null;
                EEffectType parsed;
                if (!string.IsNullOrEmpty(raw)
                    && global::System.Enum.TryParse(raw, true, out parsed))
                    return parsed;
                return EEffectType.TeleportVfx;
            }
        }

        /// <summary>传送音效；返回 false 表示不播（空值或填了 none）。</summary>
        private static bool TrySfxType(out ESoundType type)
        {
            type = ESoundType.TeleportSfx;
            string raw = SfxType != null ? SfxType.Value : null;
            if (string.IsNullOrEmpty(raw)
                || raw.Equals("none", global::System.StringComparison.OrdinalIgnoreCase))
                return false;
            return global::System.Enum.TryParse(raw, true, out type);
        }
        private static bool SwapToSodaValue => SwapToSoda == null || SwapToSoda.Value;
        private static int SodaItemIdValue => SodaItemId != null ? SodaItemId.Value : 1039;
        private static int SodaMax => SodaMaxValue != null ? SodaMaxValue.Value : 24;
        private static int SodaStepMsValue => SodaStepMs != null ? Math.Max(20, SodaStepMs.Value) : 80;
        private static int SodaHoldMsValue => SodaHoldMs != null ? Math.Max(0, SodaHoldMs.Value) : 500;

        // ══════════════════ 跨局状态 ══════════════════
        //
        // 只记"这只熊已经用掉几次传送"。刻意**不**按 SurviveTime 记时刻：
        // ResetSurvival() 每局把 SurviveTime 设回 420（不是 0），按时刻记账会算出负数 ⇒ 永不过期
        // （SodaBoostFeature 已经踩过同一个坑）。

        /// <summary>PlayerId → 当前这只小熊已经用掉的传送次数。</summary>
        private static readonly Dictionary<int, int> Teleports = new Dictionary<int, int>();

        /// <summary>
        /// PlayerId → 正在他屏幕上"假装持有"的那件汽水（<c>ItemInfo</c>，只在换物窗口内有值）。
        ///
        /// ⚠ 这套换物是**纯本机幻觉**（房主 2026-10-06 补充口径）：
        /// 它只是为了在**施法者自己**的屏幕上显示一个进度条，不是玩法改动。
        /// 所以服务端权威的 <c>Player.Hand</c> / <c>HandItemObjectId</c> / <c>PublicInfo.HandItemId</c>
        /// **全程不动** —— 别人看到的他还是原来的样子；进度条放完只还原他自己的本地视图。
        /// 也正因为服务端没动过，这份记录里**不需要**存档原物品：
        /// 还原时直接读 <c>Player.Hand</c>（它仍然是权威真值）即可。
        /// </summary>
        private static readonly Dictionary<int, ItemInfo> SodaSwaps = new Dictionary<int, ItemInfo>();

        // SkillComponent._summonId 是私有字段，反射读取（照 Features/Vision/AoiCullingFeature.cs:129-147）
        private static AccessTools.FieldRef<GameSkill, int> _summonIdRef;
        private static bool _summonIdFailed;

        // ══════════════════ 工具 ══════════════════

        private static GamePlayer FindPlayer(int pid)
        {
            var players = GameRoom.Instance != null ? GameRoom.Instance.Players : null;
            if (players == null)
                return null;

            for (int i = 0; i < players.Count; i++)
            {
                var p = players[i];
                if (p != null && p.PublicInfo != null && p.PublicInfo.PlayerId == pid)
                    return p;
            }
            return null;
        }

        private static bool IsMarionette(GameSkill sc)
            => sc != null && sc.Data != null && sc.Data.Type == ESkillType.Marionette && sc.Owner != null;

        private static int GetSummonId(GameSkill sc)
        {
            if (sc == null || _summonIdFailed)
                return 0;

            try
            {
                if (_summonIdRef == null)
                    _summonIdRef = AccessTools.FieldRefAccess<GameSkill, int>("_summonId");

                return _summonIdRef(sc);
            }
            catch (Exception ex)
            {
                _summonIdFailed = true;
                Plugin.Log.LogWarning($"[HS] MioTeleport：读不到 SkillComponent._summonId，小熊查找降级为按主人扫描（{ex.Message}）。");
                return 0;
            }
        }

        /// <summary>
        /// 找这位技能持有者的小熊。
        /// 首选 `_summonId`（原版就是这么找的，:177331）；拿不到或已失效时，
        /// 退化为「扫全部召唤物、找 StateList[1] == 主人 PlayerId」（StateList[1] 是主人在 :163271 写入的）。
        /// ⚠ 不要判 StateList[0]（它恒为 0，看 BearMapMarkFeature:124-130 的踩坑记录）。
        /// </summary>
        private static GameSummon FindBear(GameSkill sc)
        {
            var dm = GameDeviceManager.Instance;
            if (dm == null || sc == null)
                return null;

            int summonId = GetSummonId(sc);
            if (summonId != 0)
            {
                var byId = dm.GetSummon(summonId);
                if (byId != null)
                    return byId;
            }

            int ownerId = sc.Owner != null && sc.Owner.PublicInfo != null ? sc.Owner.PublicInfo.PlayerId : 0;
            if (ownerId == 0)
                return null;

            var all = dm.Summons;
            if (all == null)
                return null;

            for (int i = 0; i < all.Count; i++)
            {
                var s = all[i];
                var list = s != null && s.DeviceInfo != null ? s.DeviceInfo.StateList : null;
                if (list != null && list.Count >= 2 && list[1] == ownerId)
                    return s;
            }
            return null;
        }

        /// <summary>
        /// 调 <c>SkillComponent.CoolSkill(int)</c>（private，:177253）。
        ///
        /// 必须走它、不能自己发包：它内部会 <c>_cooltimeJob?.Kill()</c>（:177260）——
        /// 少了这一步，上一枚 CD 计时器到点仍会把 <c>CanUseSkill</c> 置回 true
        /// （观感＝"数字显示 60，但 25 秒后技能就亮了"）。
        /// 找不到方法时降级为"自己发包 + 记一条 WARNING"（数字对、但可能被旧计时器提前解锁）。
        /// </summary>
        private static void CoolSkill(GameSkill sc, int seconds)
        {
            if (sc == null || seconds <= 0)
                return;

            try
            {
                if (_coolSkillMethod == null)
                    _coolSkillMethod = AccessTools.Method(typeof(GameSkill), "CoolSkill");

                if (_coolSkillMethod != null)
                {
                    _coolSkillMethod.Invoke(sc, new object[] { seconds });
                    return;
                }
            }
            catch (Exception ex)
            {
                Plugin.Log.LogWarning($"[HS] MioTeleport：调用 CoolSkill 失败，降级为直接下发 CD（{ex.Message}）。");
            }

            // 降级：至少把指示切过去（⚠ 不会取消旧的解锁计时器）
            try
            {
                sc.Owner.CanUseSkill = false;
                sc.Owner.Session?.Send(new S_COOLTIME_SKILL { Cooltime = seconds });
            }
            catch (Exception ex)
            {
                Plugin.Log.LogWarning($"[HS] MioTeleport：下发 CD 也失败（{ex.Message}）。");
            }
        }

        // ⚠ 必须写 global:: —— 本程序集存在 HideAndSeek.Features.System，
        //    裸写 System.Reflection.MethodInfo 会被它截胡（AGENTS「已踩过的坑」第 2 条）。
        private static global::System.Reflection.MethodInfo _coolSkillMethod;

        // ══════════════════ ① 小熊寿命：60 → 可配 ══════════════════

        /// <summary>
        /// 改写"小熊寿命"那一次排程。
        ///
        /// 判据 = 「回调目标是 SkillComponent + 方法名 ResetMarionette」，与
        /// `CorpseReportFeature`（认 `action.Target is Corpse && name == "EndSurvival"`）**互斥**，
        /// 所以两者都标 `[HarmonyPriority(Priority.Last)]` 也不会互相覆盖。
        ///
        /// 上游 DT_Tools 的 CorpseWaitFeature 也 Prefix 同一个方法、并在尸体构造期内无条件改写秒数；
        /// 本钩子同样是"改参数后继续走原方法"（void），按 AGENTS 第 9 条必须**最后**跑，
        /// 才能保证捉迷藏模式的规则压过上游。模式关闭（ModeRuntime.Bypass）时第一句就返回。
        /// </summary>
        [HarmonyPatch(typeof(TimeManager), nameof(TimeManager.PushSurvivalJob), new[] { typeof(int), typeof(Action) })]
        internal static class BearLifetimeHook
        {
            [HarmonyPrefix]
            [HarmonyPriority(Priority.Last)]
            private static void Prefix(ref int secondAfter, Action action)
            {
                if (ModeRuntime.Bypass)
                    return;

                var sc = action != null ? action.Target as GameSkill : null;
                if (sc == null)
                    return;
                if (action.Method.Name != "ResetMarionette")
                    return;
                if (!IsMarionette(sc))
                    return;

                int want = BearLifetimeValue;
                if (secondAfter == want)
                    return;

                Plugin.Log.LogInfo($"[HS] MioTeleport：小熊 #{sc.Owner.PublicInfo?.PlayerId} 寿命 {secondAfter}s → {want}s。");
                secondAfter = want;
            }
        }

        // ══════════════════ ② 按技能 = 传送到小熊处（取代切视野）══════════════════

        /// <summary>
        /// 拦 `PossessMarionette`（private，:177325）—— 原版在这里把 `State = Possess` +
        /// `CameraTargetId = 熊 id`（相机随包切走）。返回 false ⇒ 相机不切，人直接搬过去。
        ///
        /// 顺序刻意是「**先搬人，后上锁**」：
        /// 锁（Stop/Stun）是客户端 `_lockControlStack++`（:14905/:14923），1 秒内不能移动/交互；
        /// 但传送本身是服务端 Move，不受客户端锁影响 —— 反过来做会让玩家觉得"卡在原地一秒才到"。
        /// </summary>
        [HarmonyPatch(typeof(GameSkill), "PossessMarionette")]
        internal static class TeleportHook
        {
            [HarmonyPrefix]
            private static bool Prefix(GameSkill __instance)
            {
                if (ModeRuntime.Bypass)
                    return true;
                if (!IsMarionette(__instance))
                    return true;

                var owner = __instance.Owner;
                if (owner == null || owner.PublicInfo == null)
                    return true;

                // 原版 PossessMarionette 的守卫是 `Data.Type == Marionette && Owner.SkillState == 2`。
                // 我们只在同一条件下接管，其余情况一律交还原版（例如热开时玩家正处在 SkillState == 3）。
                if (owner.SkillState != 2)
                    return true;

                var bear = FindBear(__instance);
                var bearPos = bear != null && bear.DeviceInfo != null ? bear.DeviceInfo.Pos : null;
                if (bearPos == null)
                {
                    Plugin.Log.LogWarning($"[HS] MioTeleport：#{owner.PublicInfo.PlayerId} 按了技能但找不到小熊，交还原版（会切成切视野）。");
                    return true;
                }

                int pid = owner.PublicInfo.PlayerId;
                Diagnostics.Hit("MioTeleport");

                // 落点兜底：Move 全程无校验（:175883-175921）。小熊本来放在玩家脚下（:177305），
                // 但玩家可能把熊留在原处、而那个点已不可站（柜子/机器旁），所以照 TeleportCommand 的做法校验一次。
                var dest = bearPos.Clone();
                try
                {
                    var areas = AreaManager.Instance;
                    if (areas != null && !areas.ValidPosition(dest))
                        dest = areas.ClampToMapBounds(dest);
                }
                catch (Exception ex)
                {
                    Plugin.Log.LogWarning($"[HS] MioTeleport：落点校验失败，改用原始熊坐标（{ex.Message}）。");
                }

                var room = GameRoom.Instance;

                // ── ① 先搬人（服务端权威 + S_RESPAWN 全服广播）──────────────
                owner.Move(dest, force: true);

                // ── ② 传送特效 + 音效（类型可配，默认＝普通传送门那款）──────
                // deviceId 传**澪自己的 id**：这两类特效都是"按该实体播"、不是按包内坐标播
                //   （TeleportVfx → PlayCommonEffect(:26958 default)，BlackHoleVfx → PlayBlackHoleEffect(:27152)），
                //   所以必须**先 Move 再发包**，否则特效会留在传送前的位置。
                //   pos 仍一并填对：黑洞那款在客户端找不到该玩家时会回落到包内坐标。
                // ⚠ 默认 TeleportVfx 与**原版普通传送门**（拉杆 InteractTeleport :162965）同款，
                //   观感统一；且它不是 BlackHoleVfx ⇒ 不会被 TeleportGuardFeature.VfxHook 改写。
                //   ⚠ TeleportVfx/PlayCommonEffect **不用包内坐标**：deviceId 传 0 只会打一条
                //     Log.Assert、什么都看不见 —— 所以这里必须传真实玩家 id。
                try
                {
                    if (room != null)
                    {
                        TeleportGuardFeature.Suppress++;      // 只在 vfx 是 BlackHoleVfx 时才起作用，留着无害
                        try
                        {
                            room.BroadcastWorldVFX(VfxTypeValue, pid, dest, VfxRangeValue);
                        }
                        finally
                        {
                            TeleportGuardFeature.Suppress--;
                        }

                        ESoundType sfx;
                        if (TrySfxType(out sfx))
                            room.BroadcastWorldSFX(sfx, dest, VfxRangeValue);
                    }
                }
                catch (Exception ex)
                {
                    Plugin.Log.LogWarning($"[HS] MioTeleport：传送特效/音效失败（{ex.Message}）。");
                }

                // ── ③ 锁操作（默认 Stop：只锁操作、不丢物品）──────────────
                if (LockMsValue > 0)
                {
                    try
                    {
                        owner.BuffComponent?.AddBuff(UseStunValue ? EBuffType.Stun : EBuffType.Stop, LockMsValue);
                    }
                    catch (Exception ex)
                    {
                        Plugin.Log.LogWarning($"[HS] MioTeleport：锁操作失败（{ex.Message}）。");
                    }
                }

                // ── ④ 计数 —— 进度条按「剩余次数」画，必须先算出来 ────────
                int used = UsedTeleports(pid) + 1;
                Teleports[pid] = used;

                // ── ⑤ 临时换汽水 + 进度条（刻度 = 剩余次数比例）──────────
                if (SwapToSodaValue)
                    StartSodaSwap(owner, room, used);

                if (used >= MaxTeleportsValue)
                {
                    Plugin.Log.LogInfo($"[HS] MioTeleport：#{pid} 第 {used} 次传送 —— 次数用尽，回收小熊。");
                    // 主动走回收那条路：它会 RemoveSummon(:177365) + CoolSkill(30) + SkillState = 1，
                    // 我们的 Postfix 再把 CD 换成"放置 CD"。此时 SkillState == 2，
                    // 内部的 UnpossessMarionette 判 SkillState == 3 ⇒ no-op，安全。
                    try { __instance.ResetMarionette(); }
                    catch (Exception ex) { Plugin.Log.LogWarning($"[HS] MioTeleport：回收小熊失败（{ex.Message}）。"); }
                }
                else
                {
                    Plugin.Log.LogInfo($"[HS] MioTeleport：#{pid} 第 {used}/{MaxTeleportsValue} 次传送 → ({dest.X:F0},{dest.Y:F0})。");
                    CoolSkill(__instance, TeleportCdValue);
                }

                // 不再走原版：相机不切、SkillState 停在 2（下次按技能 = 再传一次）
                return false;
            }
        }

        private static int UsedTeleports(int pid)
        {
            int n;
            return Teleports.TryGetValue(pid, out n) ? n : 0;
        }

        // ══════════════════ ③ 小熊消失 ⇒ 清账本 + 换成放置 CD ══════════════════

        /// <summary>
        /// 小熊的三条消失路（走近回收 / 超时 / 次数用尽）**都汇到 `ResetMarionette()`**（:177357）。
        /// 所以这一个 Postfix 就能覆盖全部：
        ///   · 清掉"用了几次"的账本（下一只熊重新数）
        ///   · 把 CD 换成放置 CD（默认 60 秒）
        ///
        /// 原版方法体末尾已经 `CoolSkill(30)`（:177368），我们再调一次会把那枚 30 秒任务
        /// `Kill()` 掉并替换成 60 —— **不需要也不该去改那个 30 字面量**
        /// （`CoolSkill` 是全局共用的，改字面量会波及其它技能）。
        ///
        /// ⚠ 原版有守卫 `Owner.SkillState != 1`，守卫没过时它什么都不做；
        /// 那种情况下我们也不该记 CD，故用 `__state` 把"这次真的会执行"带过来。
        /// </summary>
        [HarmonyPatch(typeof(GameSkill), nameof(GameSkill.ResetMarionette))]
        internal static class RecallHook
        {
            [HarmonyPrefix]
            private static void Prefix(GameSkill __instance, out bool __state)
            {
                __state = false;
                if (ModeRuntime.Bypass || __instance == null || __instance.Owner == null)
                    return;
                if (!IsMarionette(__instance))
                    return;

                __state = __instance.Owner.SkillState != 1;   // 与原版守卫一致
            }

            [HarmonyPostfix]
            private static void Postfix(GameSkill __instance, bool __state)
            {
                if (!__state)
                    return;

                int pid = __instance.Owner.PublicInfo != null ? __instance.Owner.PublicInfo.PlayerId : 0;
                if (pid != 0)
                    Teleports.Remove(pid);

                CoolSkill(__instance, RecallCdValue);
                Plugin.Log.LogInfo($"[HS] MioTeleport：小熊消失（#{pid}）→ 技能 CD 换成 {RecallCdValue}s。");
            }
        }

        // ══════════════════ ④ 临时换汽水 + 进度条（**纯本机幻觉**）══════════════════

        /// <summary>
        /// 让**施法者自己**的屏幕上出现"手持汽水 + 进度条"，并从 <c>SodaValueFrom</c> 降到 <c>SodaValueTo</c>；
        /// 进度条放完就把本地手部还原成原物品。
        ///
        /// ⚠ 口径（房主 2026-10-06 补充）：换汽水**只是为显示进度条而做的变通**，不是玩法改动。
        /// 因此这里**只发给他本人**、**完全不动服务端权威状态**：
        ///   · 不写 <c>Player.Hand</c> —— 服务端仍认为他拿着原物品
        ///     （顺带好处：原版 `Player.Tick` 的"手持汽水跑动就累加值"不会来干扰我们的进度条）
        ///   · 不写 <c>HandItemObjectId</c> —— 它内部是 `BroadcastModifyPlayer(ChangeHandItem)`
        ///     （:175550），会**广播给所有人**；不写它，别人看到的他还是原来的样子 ✓
        ///   · 临时汽水也不放进 <c>ItemManager.Items</c> —— 服务端数据结构零污染
        ///
        /// 客户端为什么会出进度条（这条是硬约束）：
        ///   S_ADD_ITEM(:42716) 若物品不是 Weapon ⇒ <c>Inventory.InsertHand(item)</c>(:42741)
        ///   → <c>StartHand</c>(:4774) → `if (item.DataId == 1039)` ⇒ ShowItemUI&lt;UI_ShakingSlider&gt; + SetInfo(:4782)。
        ///   所以包的收件人必须**只有他本人**（S_ADD_ITEM 本来就是单发给他）。
        ///   反过来说：光改 `PublicInfo.HandItemId` 只会让所有人看到角色手上拿着汽水、而他自己的屏幕上**没有条**。
        /// </summary>
        private static void StartSodaSwap(GamePlayer player, GameRoom room, int used)
        {
            if (player == null || player.PublicInfo == null || player.Session == null)
                return;

            int pid = player.PublicInfo.PlayerId;
            if (SodaSwaps.ContainsKey(pid))
                return;   // 上一次还没还原（25 秒 CD 下不该发生），不叠加

            ItemInfo soda;
            try
            {
                soda = new ItemInfo
                {
                    DataId = SodaItemIdValue,
                    ObjectId = ObjectUtils.GenerateNewId(),   // 全局唯一，避免与真实物品撞 id
                    Value = SodaValueFor(MaxTeleportsValue - used + 1, MaxTeleportsValue)
                };

                SodaSwaps[pid] = soda;

                // 让**他本人的客户端**真的持有这件汽水 —— 进度条出现的唯一前提。
                player.Session.Send(new S_ADD_ITEM { Item = soda });
            }
            catch (Exception ex)
            {
                Plugin.Log.LogWarning($"[HS] MioTeleport：换汽水失败（{ex.Message}），跳过本机进度条。");
                SodaSwaps.Remove(pid);
                return;
            }

            // 条 = 「剩余次数 ÷ 总次数」× 满值。房主口径（2026-10-06）：它反映**还剩几次**，
            // 而不是每次固定掉一段 —— 3 次上限时：第 1 次 24→16、第 2 次 16→8、第 3 次 8→0。
            int total = MaxTeleportsValue;
            int from = SodaValueFor(total - used + 1, total);
            int to = SodaValueFor(total - used, total);
            int step = SodaStepMsValue;
            int steps = Math.Abs(to - from);

            if (step > 0 && steps > 0 && room != null)
            {
                // 原版进度条没有补间（SetInfo 直接赋值，:91071）⇒ 平滑下降只能自己打节拍。
                // 每一步都自校验：PushAfter **不随阶段清空**，回调可能在结算/大厅里才跑到。
                for (int i = 1; i <= steps; i++)
                {
                    int value = from + (to - from) * i / steps;
                    room.PushAfter(step * i, delegate
                    {
                        var p = FindPlayer(pid);
                        ItemInfo cur;
                        if (p == null || !SodaSwaps.TryGetValue(pid, out cur))
                            return;
                        if (!p.IsAlive || room.State != EGameState.Survive)
                        {
                            FinishSodaSwap(pid);   // 中途失效 ⇒ 立刻收尾，别把他的本地手部留成汽水
                            return;
                        }

                        try
                        {
                            // 记住当前刻度：客户端若被打断，ResendSoda 用这个值重建，
                            // 条才不会跳回起点。
                            cur.Value = value;

                            // ⚠ 不能走 `Item.Value` 的 setter：它内部读的是**服务端 Hand**
                            //   （`SendChangeItemValue` :176956），而我们的汽水只是本机幻觉、
                            //   服务端 Hand 根本没变 ⇒ 那样会发出**原物品**的值。
                            //   直接构造 ChangeItemValue 单发给本人：客户端 :13881 → Inventory.ChangeValue
                            //   → Hand.ChangeValue + `if (Hand.DataId == 1039)` ⇒ _shaker.SetInfo。
                            p.Session.Send(new S_MODIFY_MY_PLAYER
                            {
                                Type = EModifyMyPlayerEvent.ChangeItemValue,
                                Value = value
                            });
                        }
                        catch (Exception ex)
                        {
                            Plugin.Log.LogWarning($"[HS] MioTeleport：进度条下发失败（{ex.Message}）。");
                        }
                    });
                }
            }

            int finishAt = step * steps + SodaHoldMsValue;
            if (finishAt < 0)
                finishAt = 0;

            if (room != null)
            {
                room.PushAfter(finishAt, delegate { FinishSodaSwap(pid); });
            }
            else
            {
                FinishSodaSwap(pid);
            }

            Plugin.Log.LogInfo($"[HS] MioTeleport：#{pid} 本机临时换成汽水 {SodaItemIdValue}（{from} → {to}，{finishAt}ms 后还原）。");
        }

        /// <summary>
        /// 进度条刻度 = 「剩余次数 ÷ 总次数」映射到 0..<see cref="SodaMax"/>。
        /// 剩余为 0（次数用尽）或次数非法时返回 0。
        /// </summary>
        private static int SodaValueFor(int remaining, int total)
        {
            if (total <= 0 || remaining <= 0)
                return 0;
            if (remaining >= total)
                return SodaMax;
            return (int)global::System.Math.Round(SodaMax * (double)remaining / total);
        }

        /// <summary>
        /// 还原**他本人的本地手部**：撤掉汽水 → 把服务端权威的当前手部重新发给他。
        ///
        /// 服务端权威（`Player.Hand` / `HandItemObjectId` / `PublicInfo.HandItemId`）全程没动过，
        /// 所以这里**直接读 `player.Hand`** 作为真值 —— 不需要存档，也就不会出现
        /// "存档与窗口内发生的其它改动打架"的问题（例如他在这 1 秒里捡了别的东西）。
        /// 幂等，且任何异常都不向外抛 —— 它是收尾路径。
        /// </summary>
        private static void FinishSodaSwap(int pid)
        {
            ItemInfo soda;
            if (!SodaSwaps.TryGetValue(pid, out soda))
                return;
            SodaSwaps.Remove(pid);

            var player = FindPlayer(pid);
            if (player == null || player.Session == null)
                return;   // 人已经不在（离开/迁移中）⇒ 客户端随之销毁，无需还原

            try
            {
                // ① 撤掉汽水：客户端 Handle_S_REMOVE_ITEM(:42746) 按 ObjectId 匹配 ⇒ InsertHand(null)
                //    → EndHand 关掉进度条 UI
                if (soda != null)
                    player.Session.Send(new S_REMOVE_ITEM { ObjectId = soda.ObjectId });

                // ② 把他的本地手部对齐回服务端真值（原本空手就不发）
                var hand = player.Hand;
                if (hand != null && hand.Info != null)
                    player.Session.Send(new S_ADD_ITEM { Item = hand.Info });
            }
            catch (Exception ex)
            {
                Plugin.Log.LogWarning($"[HS] MioTeleport：还原本机手部失败（{ex.Message}）。");
            }
        }

        // ══════════════════ ⑤ 进度条期间：不许被换掉、不许被丢 ══════════════════

        /// <summary>
        /// 再把「你手上正拿着这瓶汽水」告诉客户端一次 —— **进度条被打断后的恢复**。
        ///
        /// 什么时候需要：客户端可能在本地先清过手部再发包（最典型的是 `DropHand`(:4754)：
        /// 它先 `InsertHand(null)` 才把 `C_DROP_ITEM` 发出来 —— 哪怕那个包被我们拦下，
        /// 它自己屏幕上的进度条已经没了）。补发时用的 `ItemInfo.Value` 是**当前刻度**
        /// （每步节拍都会更新它），所以条不会跳回起点，进度看起来是连续的。
        /// </summary>
        private static void ResendSoda(GamePlayer player, ItemInfo soda)
        {
            if (player == null || player.Session == null || soda == null)
                return;

            try
            {
                player.Session.Send(new S_ADD_ITEM { Item = soda });
                player.Session.Send(new S_MODIFY_MY_PLAYER
                {
                    Type = EModifyMyPlayerEvent.ChangeItemValue,
                    Value = soda.Value
                });
            }
            catch (Exception ex)
            {
                Plugin.Log.LogWarning($"[HS] MioTeleport：重申持有汽水失败（{ex.Message}）。");
            }
        }

        /// <summary>发这个包的玩家是否正处在"进度条窗口"里；不是就返回 null（调用方直接放行）。</summary>
        private static GamePlayer InSodaWindow(IPacketSink session)
        {
            if (SodaSwaps.Count == 0 || ModeRuntime.Bypass)
                return null;

            var peer = session as HostPeerSession;
            var player = peer != null ? peer.Player : null;
            if (player == null || player.PublicInfo == null)
                return null;

            return SodaSwaps.ContainsKey(player.PublicInfo.PlayerId) ? player : null;
        }

        /// <summary>
        /// 进度条窗口内忽略「丢下手中的物品」。
        ///
        /// 拦 `HostPacketHandler.Handle_C_DROP_ITEM`(:174867，客户端主动丢弃的**唯一**入口)，
        /// 而**不是** `ItemManager.DropItem`(:172739)：后者还被原版内部流程调用
        /// （`InsertInven`(:172713) 捡新物品前会先丢旧的），在那一层拦会让"捡东西"半途而废。
        ///
        /// 注：幻觉汽水本来就落不到世界上（服务端 `Player.Hand` 全程没动过），
        /// 这一拦防的是"玩家在窗口内把他**真实的**原物品丢出去"。
        /// 拦下的同时会 `ResendSoda` —— 因为客户端已经先把自己的手部清空了，
        /// 不重申一次，进度条就在这一帧断掉。
        /// </summary>
        [HarmonyPatch(typeof(HostPacketHandler), "Handle_C_DROP_ITEM")]
        internal static class BlockDropHook
        {
            [HarmonyPrefix]
            private static bool Prefix(IPacketSink session)
            {
                var player = InSodaWindow(session);
                if (player == null)
                    return true;

                ItemInfo soda;
                SodaSwaps.TryGetValue(player.PublicInfo.PlayerId, out soda);

                Plugin.Log.LogInfo($"[HS] MioTeleport：#{player.PublicInfo.PlayerId} 在进度条期间尝试丢弃物品，已忽略并重申持有。");
                ResendSoda(player, soda);
                return false;
            }
        }

        /// <summary>
        /// 进度条窗口内也忽略「拾取物品」—— 拾取走 `ItemManager.AcquireItem`(:172788) →
        /// `InsertInven`(:172713)，会**换掉**他手上的东西（并先把旧的丢掉），进度条随即断掉。
        /// 窗口只有 1 秒出头、这段时间本来还锁着操作，代价可以忽略。
        /// </summary>
        [HarmonyPatch(typeof(HostPacketHandler), "Handle_C_ACQUIRE_ITEM")]
        internal static class BlockAcquireHook
        {
            [HarmonyPrefix]
            private static bool Prefix(IPacketSink session)
            {
                var player = InSodaWindow(session);
                if (player == null)
                    return true;

                Plugin.Log.LogInfo($"[HS] MioTeleport：#{player.PublicInfo.PlayerId} 在进度条期间尝试拾取物品，已忽略。");
                return false;
            }
        }

        // ══════════════════ ⑥ 跨局清理 ══════════════════
        //
        // 必须清：`SurviveTime` 每局被 ResetSurvival() 设回 420（:178650），
        // 而换汽水是**持久副作用**（手部 + 物品列表）—— 不收尾就会把汽水带进下一局。

        private static void ClearAll()
        {
            Teleports.Clear();

            if (SodaSwaps.Count > 0)
            {
                var pids = new List<int>(SodaSwaps.Keys);
                for (int i = 0; i < pids.Count; i++)
                    FinishSodaSwap(pids[i]);
            }
        }

        [HarmonyPatch(typeof(GameRoom), "StartSurvive")]
        internal static class StartSurviveHook
        {
            [HarmonyPostfix]
            private static void Postfix() => ClearAll();
        }

        [HarmonyPatch(typeof(GameRoom), "StartLobby")]
        internal static class StartLobbyHook
        {
            [HarmonyPostfix]
            private static void Postfix() => ClearAll();
        }

        [HarmonyPatch(typeof(GameRoom), nameof(GameRoom.ChangeGameState), new[] { typeof(EGameState) })]
        internal static class LeaveSurviveHook
        {
            [HarmonyPostfix]
            private static void Postfix(EGameState state)
            {
                if (state == EGameState.Survive)
                    return;
                ClearAll();
            }
        }
    }
}
