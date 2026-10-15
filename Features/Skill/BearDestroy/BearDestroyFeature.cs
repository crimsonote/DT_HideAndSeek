using System.Collections.Generic;
using BepInEx.Configuration;
using HarmonyLib;
using Protocol;
using Server.Game;
using HideAndSeek.Core;
using GamePlayer = Server.Game.Player;
using GameSummon = Server.Game.Summon;
using GameDeviceManager = Server.Game.DeviceManager;   // AGENTS 坑 #1：同名类必须显式限定

namespace HideAndSeek.Features.Skill
{
    /// <summary>
    /// 凛的小熊可被破坏：**非主人**的玩家踩到小熊上，等一会儿后开始削它的耐久度，
    /// 耐久归零即视为小熊被破坏（回收）。
    ///
    /// 语义（2026-10-15 房主确认）：
    ///   · **进度持续累计**：打断只结束本次"破坏会话"并收尾进度条，
    ///     小熊的耐久度**不恢复**；下次有人踩上，重新等 2 秒后从当前耐久度继续往下走。
    ///   · 每次会话开始都要先等 <see cref="ApproachMs"/>（默认 2000，只算一次）。
    ///   · 打断条件：破坏者离开小熊 / 死亡 / 小熊被回收（含主人主动收回）。
    ///   · 完成：耐久度 ≤ 0 ⇒ 调 <c>ResetMarionette()</c> 回收小熊。
    ///
    /// 进度条复用 <see cref="ShakerBar"/>（与凛的小熊传送同一套汽水条，
    /// 刻度语义是**剩余耐久度**：满 → 0）。
    ///
    /// 判定细节：
    ///   · "踩在上面" = 玩家与小熊的直线距离 < <see cref="Range"/>（默认 32，约一个身位）。
    ///     刻意**不**做原版那种椭圆 + 视线判定（BearMapMarkFeature 用的是那个）——
    ///     这里要的是"贴身站上去"，最直接的圆距离开销也最低。
    ///   · 只认存活、非幽灵、非主人、非观战者；躲柜子里的（State == Hide）不算。
    ///   · 同一只小熊同时只认可**一个**破坏者（先踩上的算）。
    ///
    /// ⚠ 热开关：本段 <see cref="Enabled"/> 是**段内布尔**（每次读 ConfigEntry.Value），
    ///   不是段开关 —— 段开关只在插件加载时判一次，改它必须重启游戏。
    ///   所以这里 defaultEnabled: true（补丁照常挂载）+ 段内默认 false（功能关着）。
    /// </summary>
    [PatchFeature("BearDestroy",
        "凛的小熊可被破坏：非主人踩到小熊上等 2 秒后开始削耐久（进度持续累计，打断不清零），" +
        "耐久归零即回收小熊。",
        defaultEnabled: true, side: FeatureSide.Host)]
    internal static class BearDestroyFeature
    {
        [ConfigField(false, "启用小熊破坏。**热切换**（不用重启游戏）。")]
        public static ConfigEntry<bool> Enabled;

        [ConfigField(2000f,
            "踩上去后要等多少毫秒才开始削耐久。每次开始破坏都要等这一次，只算一次。",
            Min = 0f, Max = 30000f)]
        public static ConfigEntry<float> ApproachMs;

        [ConfigField(5000f,
            "耐久度从满值削到 0 需要多少毫秒（满耐久状态下）。进度按实时时间折算。",
            Min = 500f, Max = 120000f)]
        public static ConfigEntry<float> DestroyMs;

        [ConfigField(100f,
            "进度条刻度下发的最小间隔（毫秒）。原版进度条没有补间，靠这个节拍连发。",
            Min = 20f, Max = 1000f)]
        public static ConfigEntry<float> StepMs;

        /// <summary>
        /// 耐久度总量（任意正值）。
        ///
        /// 它与"进度条刻度"是**两个量**：1039 汽水的 UI_ShakingSlider 满值硬编码为 24
        /// （ACS-0.1.17a:92250 <c>GetSlider(0).value = value / 24f</c>），
        /// 所以显示侧必须做一次映射 —— <see cref="ToBar"/> 负责把耐久折算成 0..24 的刻度。
        /// 想改"要破坏多久"用 <see cref="DestroyMs"/>；想改"破坏粒度/进度条分辨率"
        /// 才动这个值（它只影响每个刻度代表多少耐久）。
        /// </summary>
        [ConfigField(100f,
            "耐久度总量（不是进度条刻度）。进度条满值固定 24，显示时按比例折算。" +
            "改它等于改「破坏粒度」；要改破坏总时长请用「削完需要多少毫秒」。",
            Min = 1f, Max = 100000f)]
        public static ConfigEntry<float> DurabilityMax;

        [ConfigField(32f,
            "「踩在小熊上」的距离阈值（游戏单位，约一个身位）。小熊位置与玩家位置的直接距离。",
            Min = 4f, Max = 512f)]
        public static ConfigEntry<float> Range;

        [ConfigField(1039,
            "进度条用的物品 ID。1039 = SHAKER_BALL —— 全部物品里唯一带 UI_ShakingSlider 进度条的汽水。",
            Min = 1f, Max = 99999f)]
        public static ConfigEntry<int> BarItemId;

        // ══════════════════ 状态 ══════════════════

        /// <summary>
        /// 小熊 DeviceId → 剩余耐久度。
        /// 这是"进度持续累计"的载体：打断**不清这里**，小熊消失时才清。
        /// </summary>
        private static readonly Dictionary<int, float> Durability = new Dictionary<int, float>();

        /// <summary>
        /// 小熊 DeviceId → 当前破坏会话。
        /// <see cref="ApproachMs"/> 到点后由 Waiting 转 Destroying；打断时移除整条。
        /// </summary>
        private sealed class Session
        {
            public int ByPid;
            public float DueAt;        // 等待结束、开始削耐久的时刻（SurviveTime 秒）
            public float LastPush;     // 上次下发刻度的时刻（节流）
            public int LastValue = -1; // 上次下发的刻度（变了才发包）
            public bool BarOn;         // 进度条是否已挂上
        }

        private static readonly Dictionary<int, Session> Sessions = new Dictionary<int, Session>();

        private static float Now => TimeManager.Instance?.SurviveTime ?? 0f;

        /// <summary>上一次 tick 看到的开关状态，用于检测「开 → 关」的热关闭翻转。</summary>
        private static bool _wasOn;

        private static bool On => Enabled != null && Enabled.Value;
        private static float ApproachSeconds => global::System.Math.Max(0f, (ApproachMs?.Value ?? 2000f) / 1000f);
        private static float DestroySeconds => global::System.Math.Max(0.1f, (DestroyMs?.Value ?? 5000f) / 1000f);
        private static float StepSeconds => global::System.Math.Max(0.02f, (StepMs?.Value ?? 100f) / 1000f);
        private static float MaxDurability => global::System.Math.Max(1f, DurabilityMax?.Value ?? 24f);
        private static float RangeSq => (Range?.Value ?? 32f) * (Range?.Value ?? 32f);

        // ══════════════════ 主流程 ══════════════════

        /// <summary>
        /// 每秒一次（SurvivalTick）：扫全部小熊，维持 / 推进 / 打断破坏会话。
        /// 破坏中的实时进度也在这里折算（节流由 <see cref="StepSeconds"/> 与
        /// LastValue 双重把关，所以 1Hz 的 tick 也能出平滑的条）。
        /// </summary>
        [HarmonyPatch(typeof(GameRoom), "SurvivalTick")]
        internal static class TickHook
        {
            [HarmonyPostfix]
            private static void Postfix()
            {
                if (ModeRuntime.Bypass)
                    return;

                // ── 热关闭收尾 ─────────────────────────────────────────────
                // 开 → 关 的翻转必须立刻收尾：否则关着的时候没有任何代码会跑，
                // 进行中的会话 → 进度条挂在某人屏幕上、手上留着幻觉汽水；
                // 已扣的耐久留在账本里，下次开启时可能被复用 id 的新小熊继承。
                bool on = On;
                if (_wasOn && !on)
                {
                    AbortAll();
                    Durability.Clear();
                    Plugin.Log.LogInfo("[HS] BearDestroy：功能被热关闭，已收尾（会话作废、进度条撤除、耐久清空）。");
                }
                _wasOn = on;

                if (!on)
                    return;

                var room = GameRoom.Instance;
                if (room == null || room.State != EGameState.Survive)
                    return;

                var summons = GameDeviceManager.Instance?.Summons;
                if (summons == null || summons.Count == 0)
                {
                    // 小熊全没了 ⇒ 会话全部作废（耐久账本也清，避免跨局残留）
                    AbortAll();
                    return;
                }

                var seen = new HashSet<int>();
                for (int i = 0; i < summons.Count; i++)
                {
                    var s = summons[i];
                    if (!IsUsable(s))
                        continue;
                    seen.Add(s.DeviceInfo.DeviceId);
                    Step(s);
                }

                // 小熊已消失的那些会话：作废，并且**必须清耐久账本**。
                // 不清的后果：DeviceId 会被复用，新出生的小熊一落地就带着残血。
                List<int> gone = null;
                foreach (var id in Sessions.Keys)
                    if (!seen.Contains(id))
                        (gone ?? (gone ??= new List<int>())).Add(id);
                if (gone != null)
                    foreach (int id in gone)
                        Break(id, keepDurability: false);
            }
        }

        /// <summary>推进一只小熊的破坏会话。</summary>
        private static void Step(GameSummon summon)
        {
            int id = summon.DeviceInfo.DeviceId;
            int ownerPid = OwnerOf(summon);

            // 找"踩在上面"的人：非主人、存活、非幽灵、非观战、不在柜子里
            var stepper = FindStepper(summon, ownerPid);

            Session ses;
            bool has = Sessions.TryGetValue(id, out ses);

            if (stepper == null)
            {
                // 没有人踩 ⇒ 会话结束（进度条收尾，**耐久保留**）
                if (has)
                    Break(id);
                return;
            }

            int pid = stepper.PublicInfo.PlayerId;

            if (has && ses.ByPid != pid)
            {
                // 换人踩上：旧会话作废（进度同样保留），按新的一次会话重新等
                Break(id);
                has = false;
            }

            if (!has)
            {
                ses = new Session { ByPid = pid, DueAt = Now + ApproachSeconds };
                Sessions[id] = ses;
                Plugin.Log.LogInfo($"[HS] BearDestroy：#{pid} 踩上小熊 #{id}，{ApproachMs?.Value ?? 2000f:F0}ms 后开始破坏。");
            }

            if (Now < ses.DueAt)
            {
                // 还在等 2 秒：不挂条、不动耐久（"每次重新开始都要先等 2 秒"）
                if (ses.BarOn)
                {
                    ShakerBar.End(pid);           // 理论上不会到这，防御
                    ses.BarOn = false;
                    ses.LastValue = -1;
                }
                return;
            }

            // ── 开始削耐久（实时折算）────────────────────────────────
            float dur = CurrentDurability(id);
            float elapsed = Now - ses.DueAt;
            float remain = dur - elapsed / DestroySeconds * MaxDurability;

            if (!ses.BarOn)
            {
                if (ShakerBar.Begin(FindPlayer(pid), BarItemIdValue, ToBar(dur)))
                {
                    ses.BarOn = true;
                    ses.LastValue = ToBar(dur);
                    ses.LastPush = Now;
                }
                else
                {
                    return;                        // 人没了 / 发包失败：下秒再试
                }
            }

            if (remain <= 0f)
            {
                Complete(summon, stepper, ownerPid, pid);
                return;
            }

            Durability[id] = remain;

            // 刻度下发：**刻度变了**且距上次下发 >= StepSeconds 才发包。
            // 注意本 tick 只有 1Hz（SurvivalTick），所以实际下发频率至多每秒一次；
            // StepSeconds（默认 100ms）远小于 1 秒，正常情况下不会成为瓶颈，
            // 它只是防止同一秒内重复下发的兜底。
            float now = Now;
            int bar = ToBar(remain);
            if (bar != ses.LastValue && now - ses.LastPush >= StepSeconds)
            {
                ShakerBar.Set(pid, bar);
                ses.LastValue = bar;
                ses.LastPush = now;
            }
        }

        /// <summary>耐久归零：收尾进度条 + 回收小熊。原版回收没有音效，故不发通知（房主 2026-10-15 口径）。</summary>
        private static void Complete(GameSummon summon, GamePlayer stepper, int ownerPid, int pid)
        {
            int id = summon.DeviceInfo.DeviceId;
            Break(id, keepDurability: false);          // 收尾进度条、清会话与耐久

            Plugin.Log.LogInfo($"[HS] BearDestroy：#{pid} 破坏了 #{ownerPid} 的小熊 #{id}，回收。");

            // 走原版回收路径：RemoveSummon + CoolSkill(30) + SkillState = 1
            // 先用主人的 SkillComponent（按 _summonId 定位，与 MioTeleport 同款做法）
            var ownerSc = FindSkillOfSummon(id);
            if (ownerSc != null)
            {
                try { ownerSc.ResetMarionette(); }
                catch (global::System.Exception ex)
                {
                    Plugin.Log.LogWarning($"[HS] BearDestroy：回收小熊失败（{ex.Message}）。");
                }
            }
            else
            {
                // 找不到 SkillComponent（主人掉线等）⇒ 直接从设备列表移除，
                // 否则小熊会永远留着、耐久账本也白记
                try { GameDeviceManager.Instance?.RemoveSummon(summon); }
                catch (global::System.Exception ex)
                {
                    Plugin.Log.LogWarning($"[HS] BearDestroy：移除小熊失败（{ex.Message}）。");
                }
            }
        }

        // ══════════════════ 耐久账本 ══════════════════

        /// <summary>当前耐久度；没记账的小熊视为满耐久。</summary>
        private static float CurrentDurability(int summonId)
        {
            float d;
            return Durability.TryGetValue(summonId, out d) ? d : MaxDurability;
        }

        /// <summary>
        /// 耐久度 → 进度条刻度（0..24，客户端硬上限）。
        ///
        /// 为什么必须映射：<c>UI_ShakingSlider.SetInfo</c> 是 <c>value / 24f</c>
        /// （ACS-0.1.17a:92250），24 是**客户端写死的**。耐久总量
        /// （<see cref="DurabilityMax"/>，可配到 10 万）与显示刻度因此必须是两个量，
        /// 否则耐久一超 24，进度条就越界。
        /// </summary>
        private static int ToBar(float durability)
        {
            float max = MaxDurability;
            if (durability <= 0f || max <= 0f)
                return 0;
            float t = durability >= max ? 1f : durability / max;   // 0..1
            int v = (int)global::System.Math.Round(t * BarMax);
            return v < 0 ? 0 : (v > BarMax ? BarMax : v);
        }

        /// <summary>
        /// 进度条刻度硬上限 = 1039 汽水 UI_ShakingSlider 的满值。
        /// 依据是客户端源码（ACS-0.1.17a:92250 <c>value / 24f</c>），**不是约定俗成**。
        /// </summary>
        private const int BarMax = 24;

        // ══════════════════ 会话收尾 ══════════════════

        /// <summary>
        /// 结束一个会话：收尾进度条 + 移除会话记录。
        /// 默认**保留**耐久（"进度持续累计"）；<paramref name="keepDurability"/> = false
        /// 用于破坏完成（小熊都要没了，账本没必要留）。
        /// </summary>
        private static void Break(int summonId, bool keepDurability = true)
        {
            Session ses;
            if (Sessions.TryGetValue(summonId, out ses))
            {
                if (ses.BarOn)
                    ShakerBar.End(ses.ByPid);       // 撤条 + 把手部对齐回服务端真值
                Sessions.Remove(summonId);
                Plugin.Log.LogInfo(
                    $"[HS] BearDestroy：小熊 #{summonId} 的破坏会话结束（#{ses.ByPid}），" +
                    (keepDurability ? "耐久保留。" : "耐久已清。"));
            }
            if (!keepDurability)
                Durability.Remove(summonId);
        }

        /// <summary>全部作废（小熊全消失 / 阶段切换 / 热关闭）。</summary>
        private static void AbortAll()
        {
            if (Sessions.Count == 0)
                return;
            var ids = new List<int>(Sessions.Keys);
            foreach (int id in ids)
                Break(id);
        }

        [HarmonyPatch(typeof(GameRoom), "StartSurvive")]
        internal static class StartSurviveHook
        {
            [HarmonyPostfix]
            private static void Postfix()
            {
                AbortAll();
                Durability.Clear();       // 新一局的小熊是新的
            }
        }

        [HarmonyPatch(typeof(GameRoom), "StartLobby")]
        internal static class LobbyHook
        {
            [HarmonyPostfix]
            private static void Postfix()
            {
                AbortAll();
                Durability.Clear();
            }
        }

        [HarmonyPatch(typeof(GameRoom), nameof(GameRoom.ChangeGameState), new[] { typeof(EGameState) })]
        internal static class LeaveSurviveHook
        {
            [HarmonyPostfix]
            private static void Postfix(EGameState state)
            {
                if (state == EGameState.Survive)
                    return;
                AbortAll();
                Durability.Clear();
            }
        }

        // ══════════════════ 工具 ══════════════════

        private static int BarItemIdValue => BarItemId != null ? BarItemId.Value : 1039;

        private static bool IsUsable(GameSummon s)
            => s?.DeviceInfo?.StateList != null && s.DeviceInfo.StateList.Count >= 2;

        /// <summary>小熊的主人 PlayerId（<c>StateList[1]</c>，ACS:163271）。</summary>
        private static int OwnerOf(GameSummon s)
        {
            var list = s?.DeviceInfo?.StateList;
            if (list == null || list.Count < 2)
                return 0;
            return list[1];
        }

        /// <summary>
        /// 找此刻"踩在这只小熊上"的玩家。
        /// 判据：直线距离平方 &lt; <see cref="RangeSq"/>，且非主人、存活、非幽灵、
        /// 非观战、不在柜子里。多个同时踩上时取遍历到的第一个（谁先谁算）。
        /// </summary>
        private static GamePlayer FindStepper(GameSummon summon, int ownerPid)
        {
            var players = GameRoom.Instance?.Players;
            if (players == null)
                return null;

            var sp = summon?.DeviceData?.Pos;
            if (sp == null)
                return null;

            float rangeSq = RangeSq;
            for (int i = 0; i < players.Count; i++)
            {
                var p = players[i];
                if (p?.PublicInfo == null)
                    continue;
                if (!p.IsAlive || p.IsSpectator || p.PublicInfo.IsGhost)
                    continue;
                if (p.State == EPlayerState.Hide || p.State == EPlayerState.Sit)
                    continue;
                if (ownerPid > 0 && p.PublicInfo.PlayerId == ownerPid)
                    continue;

                var pp = p.PublicInfo.Pos;
                if (pp == null)
                    continue;

                float dx = pp.X - sp.X;
                float dy = pp.Y - sp.Y;
                if (dx * dx + dy * dy < rangeSq)
                    return p;
            }
            return null;
        }

        private static GamePlayer FindPlayer(int pid)
        {
            var players = GameRoom.Instance?.Players;
            if (players == null)
                return null;
            for (int i = 0; i < players.Count; i++)
            {
                var p = players[i];
                if (p?.PublicInfo != null && p.PublicInfo.PlayerId == pid)
                    return p;
            }
            return null;
        }

        /// <summary>
        /// 按小熊 DeviceId 反查持有它的 <see cref="Server.Game.SkillComponent"/>
        /// （原版的 <c>_summonId</c> 字段就是两者的纽带）。找不到返回 null
        /// —— 主人掉线 / 已进观战时会出现，调用方要能退化处理。
        /// </summary>
        private static global::Server.Game.SkillComponent FindSkillOfSummon(int summonId)
        {
            var players = GameRoom.Instance?.Players;
            if (players == null)
                return null;

            for (int i = 0; i < players.Count; i++)
            {
                var sc = players[i]?.SkillComponent;
                if (sc == null)
                    continue;
                try
                {
                    if (_summonIdRef == null)
                        _summonIdRef = AccessTools.FieldRefAccess<global::Server.Game.SkillComponent, int>("_summonId");
                    if (_summonIdRef(sc) == summonId)
                        return sc;
                }
                catch (global::System.Exception)
                {
                    // 反射失败不刷屏：退化成找不到，调用方直接从设备列表移除
                    return null;
                }
            }
            return null;
        }

        private static AccessTools.FieldRef<global::Server.Game.SkillComponent, int> _summonIdRef;
    }
}
