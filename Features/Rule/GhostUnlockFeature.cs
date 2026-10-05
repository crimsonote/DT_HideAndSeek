using System.Collections.Generic;
using BepInEx.Configuration;
using HarmonyLib;
using Protocol;
using Server.Game;
using HideAndSeek.Core;
using GamePlayer = Server.Game.Player;

namespace HideAndSeek.Features.Rule
{
    /// <summary>
    /// **保证「被停控的人一定能拿回操作权」** —— 服务端兜底，不依赖客户端表现。
    ///
    /// <b>为什么要它（对局中途、与结算无关）</b>：
    /// 被刀砍中那一刻，原版会给**受害者**发一发 <c>S_STOP_CONTROL</c>
    /// （<c>Player.OnDamaged</c>，ACS:175952）—— 客户端收到后只做一件事：
    /// <c>Managers.Game.CanControl = false</c>（ACS:42338-42341）。
    /// 而客户端"能不能动 / 能不能开平板"这两条闸门是：
    /// <list type="bullet">
    /// <item>幽灵移动：(State==Survive||Detective) &amp;&amp; <b>CanControl</b> &amp;&amp; _lockControlStack&lt;=0
    /// （ACS:14616）</item>
    /// <item>死人开平板：<b>CanControl</b> &amp;&amp; State != Trial &amp;&amp; 平板键（ACS:29002）</item>
    /// </list>
    /// 于是 <c>CanControl == false</c> 的表现就恰好是"**不能动 + 开不了平板**"。
    ///
    /// <b>而那个开关在一局里只有一条回路能打开</b>：死亡时弹的"你已死亡"提示，那段约 6 秒的
    /// DOTween 的**最后一个回调**才把它置回 true（<c>UI_ClassPopup.ShowDeadMessage</c>，ACS:61050）；
    /// 该提示的 <c>OnDisable</c> 会 <c>_deadSequence.Kill()</c>（ACS:61156），提前关掉就永远不回调。
    /// 服务端把 <c>CanControl</c> 全部置 true 的地方数过一遍，能由包触发的只有"换阶段 / 加载收尾 / 旁观入场"
    /// —— 全都不是对局中途可用的手段。**唯一可行的服务端手段，是让客户端再走一次死亡流程**
    /// （<c>S_DEAD</c> → <c>Dead()</c> → 再弹 6 秒提示 → 回调解锁），这也是本功能做的两件事。
    ///
    /// <b>① 配对保证（零误伤）</b>：原版 <c>Player.OnDead</c> 第一句是 <c>if (!IsAlive) return;</c>
    /// （ACS:175970）—— 被刀砍中后那 400ms 内如果他已经因为**别的死因**变成"不活着"，
    /// 这一发 <c>S_DEAD</c> 就会被跳过。若那个死因自己也没发过 <c>S_DEAD</c>，他的客户端就永远
    /// 不知道自己死了（还被 <c>S_STOP_CONTROL</c> 锁着）。本功能在这种"漏发"发生时补一发。
    /// <b>只在真的漏发时才发</b>（判据：进 <c>OnDead</c> 前就已经不是活着 + 本局从未给他发过 <c>S_DEAD</c>）。
    ///
    /// <b>② 兜底解锁（覆盖"客户端那条 6 秒回路被打断"）</b>：人已经死了、也收到过 <c>S_DEAD</c>，
    /// 但那 6 秒提示被打断（或 <c>Dead()</c> 中途出错）⇒ 客户端没有第二次机会。
    /// 服务端看不见客户端的开关，唯一可观测的间接信号是**移动包**：真被锁住的幽灵一个移动包都发不出来。
    /// 所以：**死后满 N 秒仍没有收到过任何移动包 ⇒ 补发一发 <c>S_DEAD</c>**（只补一次；
    /// 一旦收到移动包就永久放弃这个人）。代价：一个"能动能走却一直站着不动"的幽灵会多看到一次死亡提示。
    ///
    /// <b>本功能只影响服务端发包，不改任何原版逻辑</b>（不拦、不改参数、不写 <c>__result</c>）。
    /// </summary>
    [PatchFeature(
        section: "GhostUnlock",
        description: "服务端保证：被停控的人一定拿得回操作权 —— 死亡通知漏发时补发，死后长时间不能动时补发一次 S_DEAD（让客户端重走死亡流程解锁）。",
        defaultEnabled: true,
        side: FeatureSide.Host)]
    internal static class GhostUnlockFeature
    {
        [ConfigField(true, "启用「拿回操作权」保证（服务端兜底）。")]
        public static ConfigEntry<bool> Enabled;

        [ConfigField(8f,
            "死后多少秒仍**一个移动包都没发过**，就判定为被卡住并补发一次死亡通知（让客户端再走一次 6 秒解锁流程）。\n" +
            "太小会把「站着不动的幽灵」也当成卡住；太大则卡住的人要多等一会。",
            Min = 3f, Max = 60f)]
        public static ConfigEntry<float> UnlockAfterSeconds;

        [ConfigField(true, "补发解锁（上面那条）是否也记一条 WARN 日志，便于事后核对。")]
        public static ConfigEntry<bool> LogUnlock;

        private sealed class Rec
        {
            public int Pid;
            public string Name = "";
            public float DiedAt;              // SurviveTime
            public bool SDeadSent;            // 本局是否给他发过 S_DEAD
            public bool MovedSinceDeath;      // 死后是否发过移动包（= 他能动）
            public bool UnlockResent;         // 兜底解锁是否已经补过
            public bool HitLocked;            // 刚被停控（OnDamaged 打了标记）
        }

        private static readonly Dictionary<int, Rec> Recs = new Dictionary<int, Rec>();

        private static float Now => TimeManager.Instance?.SurviveTime ?? 0f;

        private static Rec Get(int pid)
        {
            Rec r;
            if (Recs.TryGetValue(pid, out r))
                return r;
            r = new Rec { Pid = pid };
            Recs[pid] = r;
            return r;
        }

        private static bool On()
        {
            if (ModeRuntime.Bypass)
                return false;
            return Enabled == null || Enabled.Value;
        }

        // ── ① 被砍中：原版就在这里给受害者发 S_STOP_CONTROL（ACS:175952）──
        [HarmonyPatch(typeof(GamePlayer), nameof(GamePlayer.OnDamaged))]
        internal static class DamagedHook
        {
            [HarmonyPostfix]
            private static void Postfix(GamePlayer __instance)
            {
                if (!On() || __instance?.PublicInfo == null)
                    return;

                var r = Get(__instance.PublicInfo.PlayerId);
                r.Name = __instance.Name ?? "";
                r.HitLocked = true;
            }
        }

        // ── ② 死亡：记录"到底有没有发出 S_DEAD"，并在漏发时补上 ──
        [HarmonyPatch(typeof(GamePlayer), nameof(GamePlayer.OnDead))]
        internal static class DeadHook
        {
            [HarmonyPrefix]
            private static bool Prefix(GamePlayer __instance, out bool __state)
            {
                // 记下"进方法前是不是活着" —— 原版的早退就发生在 !IsAlive 时（ACS:175970）
                __state = __instance != null && __instance.IsAlive;
                return true;
            }

            [HarmonyPostfix]
            private static void Postfix(GamePlayer __instance, bool __state)
            {
                if (!On() || __instance?.PublicInfo == null)
                    return;

                int pid = __instance.PublicInfo.PlayerId;
                var r = Get(pid);
                r.Name = __instance.Name ?? "";

                if (__state)
                {
                    // 方法体正常跑完 ⇒ S_DEAD 已经发出（ACS:175978）
                    r.SDeadSent = true;
                    r.DiedAt = Now;
                    r.MovedSinceDeath = false;
                    r.UnlockResent = false;
                    r.HitLocked = false;
                    return;
                }

                // ★ 方法体早退了：这一发 S_DEAD 没发出去。只有"刚被停控 + 本局从没发过"才需要补，
                //    否则会给人多发一遍死亡通知（那是别的死因已经发过的）。
                if (!r.HitLocked || r.SDeadSent)
                    return;

                r.HitLocked = false;
                try
                {
                    __instance.Session?.Send(new S_DEAD());
                    r.SDeadSent = true;
                    r.DiedAt = Now;
                    r.MovedSinceDeath = false;
                    r.UnlockResent = false;
                    Plugin.Log.LogWarning(
                        $"[HS] GhostUnlock：#{pid}（{r.Name}）的死亡通知被原版跳过（OnDead 早退），"
                        + "已补发 S_DEAD —— 否则他的客户端拿不到解锁用的死亡提示。");
                }
                catch (global::System.Exception ex)
                {
                    Plugin.Log.LogWarning($"[HS] GhostUnlock：补发 S_DEAD 失败 — {ex.Message}");
                }
            }
        }

        // ── ③ 死后还能不能动：服务端唯一能看到的间接信号 ──
        [HarmonyPatch(typeof(GamePlayer), nameof(GamePlayer.Move), new[] { typeof(PosInfo), typeof(bool) })]
        internal static class MoveHook
        {
            [HarmonyPostfix]
            private static void Postfix(GamePlayer __instance)
            {
                if (ModeRuntime.Bypass || __instance?.PublicInfo == null || __instance.IsAlive)
                    return;

                Rec r;
                if (Recs.TryGetValue(__instance.PublicInfo.PlayerId, out r))
                    r.MovedSinceDeath = true;
            }
        }

        // ── ④ 兜底解锁：死后满 N 秒一个移动包都没有 ⇒ 补发一次 S_DEAD ──
        [HarmonyPatch(typeof(GameRoom), "SurvivalTick")]
        internal static class TickHook
        {
            [HarmonyPostfix]
            private static void Postfix()
            {
                if (!On() || Recs.Count == 0)
                    return;

                float grace = UnlockAfterSeconds != null ? UnlockAfterSeconds.Value : 8f;
                if (grace <= 0f)
                    return;

                float now = Now;
                foreach (var kv in Recs)
                {
                    var r = kv.Value;
                    if (!r.SDeadSent || r.MovedSinceDeath || r.UnlockResent)
                        continue;                          // 没死过 / 他能动 / 已经补过

                    float dead = now - r.DiedAt;
                    if (dead < grace)
                        continue;

                    // 找一个仍在场上的 Player 实例（可能已经退房 ⇒ 找不到就放弃，不硬发）
                    var p = Find(r.Pid);
                    if (p?.Session == null)
                    {
                        r.UnlockResent = true;
                        continue;
                    }
                    if (p.IsAlive || p.IsSpectator || p.IsDummy)
                    {
                        r.UnlockResent = true;             // 只有真幽灵需要；活着/旁观/假人一律不碰
                        continue;
                    }

                    r.UnlockResent = true;
                    try
                    {
                        // 让客户端**重走一次死亡流程**：Dead() → 再弹 6 秒提示 → 回调把开关打开
                        p.Session.Send(new S_DEAD());
                        if (LogUnlock == null || LogUnlock.Value)
                        {
                            Plugin.Log.LogWarning(
                                $"[HS] GhostUnlock：#{r.Pid}（{r.Name}）死后 {dead:F0} 秒"
                                + "没有任何移动包（疑似被卡住：不能动 / 开不了平板），"
                                + "已补发一次死亡通知让他重走解锁流程。");
                        }
                    }
                    catch (global::System.Exception ex)
                    {
                        Plugin.Log.LogWarning($"[HS] GhostUnlock：兜底解锁失败 — {ex.Message}");
                    }
                }
            }

            private static GamePlayer Find(int pid)
            {
                var players = GameRoom.Instance?.Players;
                if (players == null)
                    return null;
                for (int i = 0; i < players.Count; i++)
                {
                    if (players[i]?.PublicInfo?.PlayerId == pid)
                        return players[i];
                }
                return null;
            }
        }

        // ── 跨局清理 ────────────────────────────────────────────────
        [HarmonyPatch(typeof(GameRoom), "StartSurvive")]
        internal static class StartHook
        {
            [HarmonyPostfix]
            private static void Postfix() => Recs.Clear();
        }

        [HarmonyPatch(typeof(GameRoom), "StartLobby")]
        internal static class LobbyHook
        {
            [HarmonyPostfix]
            private static void Postfix() => Recs.Clear();
        }
    }
}
