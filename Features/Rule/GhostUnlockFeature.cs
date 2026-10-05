using System.Collections.Generic;
using BepInEx.Configuration;
using HarmonyLib;
using Protocol;
using Server.Game;
using HideAndSeek.Core;
using HideAndSeek.Features.Replay;          // 回放是否在放（ReplayDirector.Current）
using GamePlayer = Server.Game.Player;

namespace HideAndSeek.Features.Rule
{
    /// <summary>
    /// **保证「被刀砍中的人一定拿得回操作权」** —— 服务端兜底，只管对局中途，不涉及结算/回放。
    ///
    /// <b>为什么是"停控"这一个开关</b>（判据全部来自反编译客户端）：
    /// 被刀砍中那一刻，原版会给**受害者**发一发 <c>S_STOP_CONTROL</c>（<c>Player.OnDamaged</c>，ACS:175952）；
    /// 客户端收到只做一件事：<c>Managers.Game.CanControl = false</c>（ACS:42338-42341）。而两条闸门是：
    /// <list type="bullet">
    /// <item>幽灵移动：(State==Survive||Detective) &amp;&amp; <b>CanControl</b> &amp;&amp; _lockControlStack&lt;=0（ACS:14616）</item>
    /// <item>死人开平板：<b>CanControl</b> &amp;&amp; State != Trial &amp;&amp; 平板键（ACS:29002）</item>
    /// </list>
    /// 后者不看 <c>_lockControlStack</c> ⇒ 两个症状同时出现只可能是 <c>CanControl == false</c>。
    /// 而它在一局里**只有一条回路**能打开：死亡时那条约 6 秒的"你已死亡"提示的最后一个回调
    /// （<c>UI_ClassPopup.ShowDeadMessage</c>，ACS:61050）；该提示 <c>OnDisable</c> 会
    /// <c>_deadSequence.Kill()</c>（ACS:61156）。服务端把 <c>CanControl</c> 置 true 的地方数过一遍（共 8 处），
    /// 能由包触发的只有"换阶段 / 加载收尾 / 旁观入场"，都对局中途用不了。
    /// ⇒ **唯一可行的服务端手段是让客户端再走一次死亡流程**（<c>S_DEAD</c> → <c>Dead()</c> → 再弹 6 秒 → 回调解锁）。
    ///
    /// <b>① 配对保证（零误伤）</b>：原版 <c>OnDead</c> 第一句 <c>if (!IsAlive) return;</c>（ACS:175970）——
    /// 被砍中后那 400ms 里如果他已经因为别的死因变成"不活着"，这一发 <c>S_DEAD</c> 就被跳过；
    /// 若那个死因自己也没发过，他的客户端就永远不知道自己死了。**只在真的漏发时补发**
    /// （判据：进 <c>OnDead</c> 前就已是"不活着" + 刚被停控 + 本局从未给他发过 <c>S_DEAD</c>）。
    ///
    /// <b>② 兜底解锁</b>：人已死、也收到过 <c>S_DEAD</c>，但那 6 秒提示被打断（或 <c>Dead()</c> 中途出错）
    /// ⇒ 客户端没有第二次机会。服务端看不见客户端的开关，只能靠间接信号：
    /// <b>死者死后位置从未变化过</b> ⇒ 判定被卡住 ⇒ 补发一次 <c>S_DEAD</c>。
    ///
    /// ⚠ 判据踩过的坑（都写在这里，免得后来人重犯）：
    /// <list type="bullet">
    /// <item><b>不能用"有没有收到移动包"</b>：客户端死人分支里 <c>UpdateMovePacket()</c> 是**无条件**调用的
    /// （ACS:14615 + 14865），被锁住的幽灵**照旧每 0.1 秒发一发 `C_MOVE`** ⇒ 那个信号区分不出来。
    /// 能区分的只有**位置有没有真的变过**。</item>
    /// <item><b>必须自己查阶段</b>：<c>SurvivalTick</c> 在非 <c>Survive</c> 阶段会早退，
    /// 但本类挂在它的 **Postfix** 上照样会跑 ⇒ 结算/大厅期间必须自己拦掉。</item>
    /// <item><b>回放期间服务端 <c>State</c> 仍是 <c>Survive</c></b>（<c>ReplayDirector</c> 刻意只给客户端发
    /// <c>S_CHANGE_GAME_STATE{Trial}</c>、不动服务端 State），而客户端全在 Trial 里、谁都动不了
    /// ⇒ 只看 <c>State</c> 挡不住，必须额外问 <c>ReplayDirector.Current</c>。</item>
    /// <item><b>只对"被刀砍死"的人做</b>：判据是"死前刚被武器命中过"（那时原版发过停控）。
    /// 自爆（项圈）没有这一发停控，也就**不会**被本功能补发。</item>
    /// </list>
    ///
    /// <b>本功能只多发"客户端本该收到的那一发"，不拦原版、不改参数、不写 `__result`。</b>
    /// </summary>
    [PatchFeature(
        section: "GhostUnlock",
        description: "服务端保证：被刀砍中的人一定拿得回操作权 —— 死亡通知漏发时补发；被刀砍死且死后位置一直没变（疑似卡住）时补发一次 S_DEAD。只管对局中途，不碰结算与回放。",
        defaultEnabled: true,
        side: FeatureSide.Host)]
    internal static class GhostUnlockFeature
    {
        [ConfigField(true, "启用「拿回操作权」保证（服务端兜底）。")]
        public static ConfigEntry<bool> Enabled;

        [ConfigField(10f,
            "**被刀砍死**后多少秒位置仍一点没变，就判定为卡住并补发一次死亡通知（让客户端再走一次 6 秒解锁流程）。\n" +
            "0 = 关闭这条兜底（只保留上面那条「死亡通知漏发时补发」）。\n" +
            "太小会把「死后一直站着不动」的幽灵也算进来；太大则真卡住的人要多等一会。",
            Min = 0f, Max = 60f)]
        public static ConfigEntry<float> UnlockAfterSeconds;

        [ConfigField(true, "补发时记一条 WARN 日志，便于事后核对（关掉则只在诊断面板里看到触发次数）。")]
        public static ConfigEntry<bool> LogUnlock;

        private sealed class Rec
        {
            public int Pid;
            public string Name = "";
            public float HitAt = -1f;         // 最近一次被武器命中的时刻（SurviveTime）；<0 = 没有
            public float DiedAt;              // 死亡时刻（SurviveTime）
            public bool DiedByWeapon;         // 死前刚被武器命中过（那时原版发了停控）
            public bool SDeadSent;            // 本局是否给他发过 S_DEAD
            public bool ReallyMoved;          // 死后位置变过（= 他能动）
            public bool UnlockResent;         // 兜底解锁是否已经补过
            public bool HitLocked;            // 刚被停控（OnDamaged 打了标记，供"漏发补发"用）
            public PosInfo LastPos;           // 死亡后的参考坐标
        }

        private static readonly Dictionary<int, Rec> Recs = new Dictionary<int, Rec>();

        private static float Now => TimeManager.Instance?.SurviveTime ?? 0f;

        private static Rec Get(int pid)
        {
            Rec r;
            if (Recs.TryGetValue(pid, out r))
                return r;
            r = new Rec { Pid = pid, HitAt = -1f };
            Recs[pid] = r;
            return r;
        }

        private static bool On()
        {
            if (ModeRuntime.Bypass)
                return false;
            return Enabled == null || Enabled.Value;
        }

        /// <summary>回放是否正在占着客户端（占着时全员都不能动，绝不能把"没动"当成卡住）。</summary>
        private static bool ReplayBusy()
        {
            var p = ReplayDirector.Current;
            return p != ReplayDirector.Phase.Idle && p != ReplayDirector.Phase.Done;
        }

        // ── ① 被武器命中：原版就在这里给受害者发 S_STOP_CONTROL（ACS:175952）──
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
                r.HitAt = Now;
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

                // 死前 2 秒内被武器命中过 ⇒ 原版给他发过停控（本功能只对这一类人做兜底解锁）
                bool byWeapon = r.HitAt >= 0f && (Now - r.HitAt) <= 2f;

                if (__state)
                {
                    // 方法体正常跑完 ⇒ S_DEAD 已经发出（ACS:175978）
                    r.SDeadSent = true;
                    r.DiedAt = Now;
                    r.DiedByWeapon = byWeapon;
                    r.ReallyMoved = false;
                    r.UnlockResent = false;
                    r.LastPos = __instance.PublicInfo.Pos?.Clone();
                    r.HitLocked = false;
                    return;
                }

                // ★ 方法体早退了：这一发 S_DEAD 没发出去。只有"刚被停控 + 本局从没发过"才补，
                //    否则会给别的死因已经通知过的人多发一遍死亡通知。
                if (!r.HitLocked || r.SDeadSent)
                    return;

                r.HitLocked = false;
                try
                {
                    __instance.Session?.Send(new S_DEAD());
                    r.SDeadSent = true;
                    r.DiedAt = Now;
                    r.DiedByWeapon = byWeapon;
                    r.ReallyMoved = false;
                    r.UnlockResent = false;
                    r.LastPos = __instance.PublicInfo.Pos?.Clone();
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

        // ── ③ 死后位置有没有真的变过（**不是**"有没有收到包"）──
        //
        // 客户端死人分支里 `UpdateMovePacket()` 是无条件调用的（ACS:14615 + 14865），
        // 被锁住的幽灵照旧每 0.1 秒发一发 `C_MOVE`（速度与位移都是 0）
        // ⇒ 只能看**坐标有没有变**。
        [HarmonyPatch(typeof(GamePlayer), nameof(GamePlayer.Move), new[] { typeof(PosInfo), typeof(bool) })]
        internal static class MoveHook
        {
            [HarmonyPostfix]
            private static void Postfix(GamePlayer __instance)
            {
                if (ModeRuntime.Bypass || __instance?.PublicInfo?.Pos == null || __instance.IsAlive)
                    return;

                Rec r;
                if (!Recs.TryGetValue(__instance.PublicInfo.PlayerId, out r) || r.ReallyMoved)
                    return;

                var now = __instance.PublicInfo.Pos;
                if (r.LastPos == null)
                {
                    r.LastPos = now.Clone();
                    return;
                }

                float dx = now.X - r.LastPos.X;
                float dy = now.Y - r.LastPos.Y;
                if (dx * dx + dy * dy > 1f)          // 1 个游戏单位以上才算"真的动了"
                {
                    r.ReallyMoved = true;
                    r.LastPos = now.Clone();
                }
            }
        }

        // ── ④ 兜底解锁：被刀砍死 + 位置一直没变 ⇒ 补发一次 S_DEAD ──
        [HarmonyPatch(typeof(GameRoom), "SurvivalTick")]
        internal static class TickHook
        {
            [HarmonyPostfix]
            private static void Postfix()
            {
                if (!On() || Recs.Count == 0)
                    return;

                float grace = UnlockAfterSeconds != null ? UnlockAfterSeconds.Value : 10f;
                if (grace <= 0f)
                    return;                                   // 0 = 关掉这条兜底

                var room = GameRoom.Instance;
                if (room == null)
                    return;

                // ⚠ SurvivalTick 在非 Survive 阶段会早退，但 Postfix 照样跑 ⇒ 自己拦
                if (room.State != EGameState.Survive && room.State != EGameState.Detective)
                    return;

                // ⚠ 回放期间服务端 State 仍是 Survive，而客户端全在 Trial 里不动 ⇒ 必须单独拦
                if (ReplayBusy())
                    return;

                float now = Now;
                foreach (var kv in Recs)
                {
                    var r = kv.Value;
                    if (!r.SDeadSent || !r.DiedByWeapon || r.ReallyMoved || r.UnlockResent)
                        continue;   // 没死过 / 不是被刀砍死 / 他能动 / 已经补过

                    float dead = now - r.DiedAt;
                    if (dead < grace)
                        continue;

                    var p = Find(r.Pid);
                    if (p?.Session == null)
                    {
                        r.UnlockResent = true;
                        continue;
                    }
                    if (p.IsAlive || p.IsSpectator || p.IsDummy)
                    {
                        r.UnlockResent = true;                // 只有真幽灵需要
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
                                $"[HS] GhostUnlock：#{r.Pid}（{r.Name}）被刀砍死后 {dead:F0} 秒"
                                + "位置一点没变（疑似卡住：不能动 / 开不了平板），"
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
