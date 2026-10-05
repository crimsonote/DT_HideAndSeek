using System.Collections.Generic;
using BepInEx.Configuration;
using HarmonyLib;
using Protocol;
using Server.Game;
using HideAndSeek.Core;
using HideAndSeek.Features.Replay;          // ReplayDirector.Current（回放是否在放）
using GameChatDevice = Server.Game.ChatDevice;
using GamePlayer = Server.Game.Player;

namespace HideAndSeek.Features.Rule
{
    /// <summary>
    /// 【**临时补丁** —— 官方修好客户端之后应整段作为冗余代码移除】
    ///
    /// <b>治的是什么</b>：在**生存阶段**里「**操作发信机**（`ChatDevice` / `UI_ChatDevicePopup`）时被刀杀死」
    /// 的人，会**整段 Survive 拿不回操作权**（不能动、也开不了平板），直到下一次换阶段（回大厅）才恢复。
    ///
    /// <b>为什么是原版的客户端缺陷</b>（判据全部来自反编译 + 新程序集 IL，见 `.tmps/幽灵卡死-独立复核.md` §1/§8）：
    /// <list type="bullet">
    /// <item>被刀命中那一刻服务端发 <c>S_STOP_CONTROL</c>（<c>Player.OnDamaged</c>，:175952），
    /// 客户端只做一件事：<c>Managers.Game.CanControl = false</c>（PacketHandler，:42338-42341）。</item>
    /// <item>一局之内能把它重新置 true 的路，**在 Survive 内只有一条**：死亡时那条约 6.0 秒的
    /// 「你已死亡」提示的**最后一个 DOTween 回调**（<c>UI_ClassPopup.&lt;ShowDeadMessage&gt;b__10_0</c>，:61050）。</item>
    /// <item>而 <c>UI_ClassPopup.OnDisable</c> / <c>OnDestroy</c>（:61156-61166）会把那条序列
    /// <c>Kill(false)</c> —— **complete=false ⇒ 回调永不执行 ⇒ 解锁永久丢失**。这是客户端自己的洞。</item>
    /// <item>原版还有一条后路：**下一次换阶段**时 <c>S_CHANGE_GAME_STATE → StartLoading → S_FADE_IN →
    /// EndLoading</c> 的回调里会 <c>CanControl = true</c>（<c>UIManager.&lt;EndLoading&gt;b__31_0</c>，:38652）。
    /// 但本玩法**禁用了报告尸体** ⇒ 侦探/审判阶段不再发生 ⇒ 那条「下一次换阶段」的后路被大幅拉远
    /// （只剩结算/回大厅），于是症状表现为「一直卡到本局结束」。</item>
    /// <item>另一个独立缺陷：<c>MyPlayer.PlayDying()</c> 也会 <c>CanControl = false</c>（:15012）
    /// 且全程序没有任何解锁配它 —— 那属另一条链，本类不处理。</item>
    /// </list>
    ///
    /// <b>本类是干什么的</b>：服务端**只对那一个人**补一次「当前阶段」的刷新，让他重走
    /// <c>StartLoading → 回执 → S_FADE_IN → EndLoading</c> 这条**原版每次换阶段都在走**的路，
    /// 由 :38652 那个回调把 <c>CanControl</c> 置回 true。**服务端真实状态不变**（仍是 <c>Survive</c>）。
    ///
    /// <b>触发判据（确定性，不靠猜客户端行为）</b>：
    /// <list type="number">
    /// <item><c>Server.Game.ChatDevice.OnUserDamaged</c>（:162331，**private**）—— 它挂在
    /// <c>player.OnDamagedEvent</c> 上（<c>ChatDevice.Enter</c>，:162315-162316），而被调用的位置正是
    /// <c>Player.OnDamaged</c> 那发 400ms 回调里、**<c>OnDead</c> 的前一行**（:175955 / :175961）。
    /// ⇒ 「此人在操作发信机时被打」这一瞬间，服务端是**确定知道**的。</item>
    /// <item><c>OnDead</c> 的 <b>Prefix</b> 里读 <c>PublicInfo.State == EPlayerState.Interact</c>（=5）——
    /// 客户端开界面时会发 <c>C_MODIFY_PLAYER{ChangePlayerState=5}</c>，服务端
    /// <c>Player.ModifyPlayer</c> 的该分支**无条件写入**（:176801；那条 <c>IsAlive &amp;&amp; (Hide||Sit)</c>
    /// 的守卫只挡 Hide/Sit，不挡 Interact）。
    /// ⚠ **必须在 Prefix 读**：<c>OnDead</c> 末尾的 <c>ExitPlayer()</c> 会把 <c>State</c> 改成 <c>Hide</c>（:176098）。</item>
    /// </list>
    ///
    /// <b>怎么做（顺序不能反）</b>：
    /// <code>
    /// p.Session.Send(new S_CHANGE_GAME_STATE { State = room.State });   // 只发那一个人，不广播
    /// // 等他的 C_COMPLETE_PACKET（带超时）
    /// p.Session.Send(new S_FADE_IN());
    /// </code>
    /// 反过来会被客户端 <c>UIManager.EndLoading</c> 开头那句 <c>if (_loadingUI == null) return;</c>（:38646）
    /// 直接吃掉。写法与 <c>ReplayDirector</c> 里已验证的那套同源（玩法分支 <c>Features/Replay/ReplayDirector.cs</c>）。
    ///
    /// <b>明确不用的做法</b>：
    /// <list type="bullet">
    /// <item>✗ <c>room.ChangeGameState(room.State)</c> —— 方法体开头就是 <c>if (State == state) return;</c>，**空转**。</item>
    /// <item>✗ <c>S_MIGRATION_COMPLETE</c> 当解锁包 —— 它的 handler 会调
    /// <c>Managers.Network.NotifyMigrationComplete</c>（:43066），有网络层副作用。</item>
    /// <item>✗ 无条件广播 —— 会打扰全场；这里只 <c>Session.Send</c> 给那一个人。</item>
    /// </list>
    ///
    /// <b>两个当初的未知项怎么兜的</b>：
    /// <list type="number">
    /// <item>「客户端收到**同状态**的 <c>S_CHANGE_GAME_STATE</c> 会不会自己忽略」——**不需要它不忽略**：
    /// 客户端 <c>Handle_S_CHANGE_GAME_STATE</c> 不判重，直接 <c>StartLoading(state)</c>；而
    /// <c>GameManagerEX.State</c> 的 setter 自带 <c>_state != value</c> 守卫（:28716），所以
    /// 「设成同一个 Survive」是**无副作用**的。⇒ 整条路照走，回执照发，最后 <c>EndLoading</c> 解锁。</item>
    /// <item>「服务端没有等待者时收到那发 <c>C_COMPLETE_PACKET</c> 有没有害」——**已核实为无害**：
    /// <c>GameRoom.CompletePacket</c>（:169808 → 基类 :173031）整段体就是 <c>if (_completeAction != null) {…}</c>，
    /// 没有等待者时**一个字都不做**。所以本类**不注册**、也不碰那套等待者集合，只挂一个纯观察 Postfix。</item>
    /// </list>
    /// 等回执仍**带超时**；超时（或客户端走了 <c>IsCatchingUp</c> 的瞬时解锁路径）时**仍然补发 <c>S_FADE_IN</c>** ——
    /// 只 WARN 不补的话，客户端会停在 <c>_loadingUI != null</c> 的加载态，**比不修更糟**。
    ///
    /// <b>移除条件</b>（满足任意一条即可整段删掉）：
    /// <list type="bullet">
    /// <item>官方修掉客户端 <c>UI_ClassPopup.OnDisable</c> / <c>OnDestroy</c> 的 <c>Kill(false)</c>
    /// （例如改成不再取消那个回调、或把解锁做成幂等）；</item>
    /// <item>或者官方让「拿回操作权」不再只依赖那一条 DOTween 回调（例如服务端给一发能直接置 <c>CanControl</c> 的包）；</item>
    /// <item>或者本玩法的换阶段后路恢复（例如重新允许报告尸体 / 恢复到侦探阶段）。</item>
    /// </list>
    /// </summary>
    [PatchFeature(
        section: "GhostPhaseRefresh",
        description: "【临时补丁】操作发信机时被刀杀死的人会整段 Survive 拿不回操作权（原版客户端 UI_ClassPopup.OnDisable 会 Kill 掉唯一的解锁回调）。"
            + "这里只在服务端对那一个人定向补一次「当前阶段」刷新，走原版换阶段的解锁路。官方修复后应作为冗余代码移除。",
        defaultEnabled: true,
        side: FeatureSide.Host)]
    internal static class GhostPhaseRefreshFeature
    {
        private const string Sec = "GhostPhaseRefresh";

        /// <summary>备用判据的时间窗（毫秒）：只认「刚被打」那一刻在发信机上。</summary>
        private const int DamagedRecencyMs = 1000;

        /// <summary>补完 S_FADE_IN 之后，隔多久回看坐标有没有变（用来在实机日志里确认真的恢复了）。</summary>
        private const int VerifyAfterMs = 6000;

        [ConfigField(true, "【临时补丁】启用「操作发信机时被刀杀死 ⇒ 定向补一次当前阶段刷新」。关掉即完全恢复原版行为。")]
        public static ConfigEntry<bool> RefreshEnabled;

        [ConfigField(8f, "死亡后多少秒补这次刷新（秒）。必须大于死亡提示的 6.0 秒 —— 太早会和原版那 6 秒提示叠在一起。",
            Min = 6f, Max = 60f)]
        public static ConfigEntry<float> AfterSeconds;

        [ConfigField(2500, "等客户端 C_COMPLETE_PACKET 回执的超时（毫秒）。超时也补发 S_FADE_IN，避免把客户端留在加载态。",
            Min = 200f, Max = 15000f)]
        public static ConfigEntry<int> AckTimeoutMs;

        [ConfigField(false, "额外保守闸门：只有「死后坐标一点没变」才补（默认关）。" +
            "开了会漏判 —— 服务端只要对这个死者写过一次坐标（复活/ErrorPos/迁移），这条闸门就永久失效。")]
        public static ConfigEntry<bool> RequireStillInPlace;

        [ConfigField(true, "把每次判定的细节（候选/闸门/发出/回执/超时/事后坐标）写进日志，便于实机确认生效。")]
        public static ConfigEntry<bool> VerboseLog;

        private sealed class Rec
        {
            public int Pid;
            public string Name = "";

            /// <summary>死者的引用：同 pid 换了 Player 实例（退房重进）时据此放弃，避免误伤新人。</summary>
            public GamePlayer Owner;

            /// <summary>最近一次「在发信机上被打」的时刻（Environment.TickCount，毫秒）；0 = 没有。</summary>
            public int DamagedOnChatAtMs;

            public bool DiedInteract;        // OnDead Prefix 看到 State == Interact（主判据）
            public bool DiedRecentDamaged;   // 备用判据命中
            public PosInfo PosAtDeath;       // 仅 RequireStillInPlace 用

            public bool Fired;               // 已经发过 S_CHANGE_GAME_STATE
            public bool AckReceived;         // 收到过他的 C_COMPLETE
            public bool FadeInSent;          // 已经发过 S_FADE_IN（终态）
        }

        private static readonly Dictionary<int, Rec> Recs = new Dictionary<int, Rec>();

        private static int NowMs => global::System.Environment.TickCount;

        private static bool On()
        {
            if (ModeRuntime.Bypass)
                return false;
            return RefreshEnabled == null || RefreshEnabled.Value;
        }

        private static void Log(string msg)
        {
            if (VerboseLog == null || VerboseLog.Value)
                Plugin.Log.LogInfo("[HS] GhostPhaseRefresh： " + msg);
        }

        private static Rec Get(int pid, GamePlayer p)
        {
            Rec r;
            if (!Recs.TryGetValue(pid, out r))
            {
                r = new Rec { Pid = pid };
                Recs[pid] = r;
            }
            if (p != null)
            {
                r.Owner = p;
                if (!string.IsNullOrEmpty(p.Name))
                    r.Name = p.Name;
            }
            return r;
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

        private static bool MovedSinceDeath(Rec r, GamePlayer p)
        {
            if (r.PosAtDeath == null || p?.PublicInfo?.Pos == null)
                return false;                       // 拿不到基准就不拦（宁可补，不要漏）
            float dx = p.PublicInfo.Pos.X - r.PosAtDeath.X;
            float dy = p.PublicInfo.Pos.Y - r.PosAtDeath.Y;
            return dx * dx + dy * dy > 1f;
        }

        // ── ① 「在发信机上被打」—— 服务端确定性信号（:162331，private；被调于 :175955，早 OnDead 一行）──
        [HarmonyPatch(typeof(GameChatDevice), "OnUserDamaged")]
        internal static class ChatDeviceDamagedHook
        {
            [HarmonyPostfix]
            private static void Postfix(GameChatDevice __instance, GamePlayer player)
            {
                if (ModeRuntime.Bypass || player?.PublicInfo == null)
                    return;

                Get(player.PublicInfo.PlayerId, player).DamagedOnChatAtMs = NowMs;
                Diagnostics.Hit(Sec);
            }
        }

        // ── ② 死亡：定候选（⚠ Prefix，不能是 Postfix —— 末尾 ExitPlayer() 会把 State 改成 Hide）──
        [HarmonyPatch(typeof(GamePlayer), nameof(GamePlayer.OnDead))]
        internal static class DeadHook
        {
            [HarmonyPrefix]
            private static bool Prefix(GamePlayer __instance, EDeathType type)
            {
                // 一律放行原版；本钩子只登记候选
                if (!On() || __instance?.PublicInfo == null)
                    return true;

                var room = GameRoom.Instance;
                if (room == null || room.State != EGameState.Survive)
                    return true;
                if (type != EDeathType.Murder)
                    return true;                       // 只认刀杀（含 DT 处刑）；自爆/审判处决不碰
                if (ReplayDirector.Current != ReplayDirector.Phase.Idle)
                    return true;                       // 回放期间服务端 State 仍是 Survive，必须单独拦

                int pid = __instance.PublicInfo.PlayerId;
                var r = Get(pid, __instance);

                bool interact = __instance.PublicInfo.State == EPlayerState.Interact;   // 主判据（:176801 写入）
                bool recent = r.DamagedOnChatAtMs != 0
                    && (NowMs - r.DamagedOnChatAtMs) <= DamagedRecencyMs;              // 备用判据（:162331 打点）
                if (!interact && !recent)
                    return true;

                r.DiedInteract = interact;
                r.DiedRecentDamaged = recent;
                r.PosAtDeath = __instance.PublicInfo.Pos?.Clone();
                r.Fired = false;
                r.AckReceived = false;
                r.FadeInSent = false;
                Diagnostics.Hit(Sec);

                int delay = (int)((AfterSeconds != null ? AfterSeconds.Value : 8f) * 1000f);
                Log($"#{pid}（{r.Name}）死时在操作发信机（State=Interact:{interact} 近期被打:{recent}）"
                    + $" ⇒ {delay}ms 后定向补一次当前阶段刷新。");
                room.PushAfter(delay, () => Fire(pid));
                return true;
            }
        }

        // ── ③ 回执：纯观察，不注册也不碰原版的等待者集合（:173031 无等待者时是空操作）──
        [HarmonyPatch(typeof(GameRoom), "CompletePacket", new[] { typeof(int) })]
        internal static class CompleteAckHook
        {
            [HarmonyPostfix]
            private static void Postfix(int playerId) => OnAck(playerId);
        }

        // ── ④ 换局清理 ────────────────────────────────────────────────
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

        // ── 主流程（都跑在服务端主线程上，由 JobTimer 墙钟调度）──────────

        private static void Fire(int pid)
        {
            Rec r;
            if (!Recs.TryGetValue(pid, out r) || r.Fired)
                return;
            if (!On())
            {
                Log($"#{pid} 跳过：功能已关（或模式关闭）。");
                return;
            }

            var room = GameRoom.Instance;
            if (room == null || room.State != EGameState.Survive)
            {
                Log($"#{pid} 跳过：房间不在生存阶段（{room?.State}）。");
                return;
            }
            if (room.IsTransitioning || room.IsMigrating)
            {
                Log($"#{pid} 跳过：房间正在换阶段/迁移。");
                return;
            }
            if (ReplayDirector.Current != ReplayDirector.Phase.Idle)
            {
                Log($"#{pid} 跳过：回放进行中（{ReplayDirector.Current}）。");
                return;
            }

            var p = Find(pid);
            if (p?.Session == null || !ReferenceEquals(p, r.Owner))
            {
                Log($"#{pid} 跳过：玩家已不在房间（或换了实例）。");
                return;
            }
            if (p.IsAlive || p.IsSpectator || p.IsDummy)
            {
                Log($"#{pid} 跳过：他活着/是旁观/是假人。");
                return;
            }
            if ((RequireStillInPlace != null && RequireStillInPlace.Value) && MovedSinceDeath(r, p))
            {
                Log($"#{pid} 跳过：死后坐标变过（开了保守闸门）。");
                return;
            }

            r.Fired = true;
            try
            {
                // ★ 只发那一个人。服务端真实状态不变 —— 这里不动 room.State，也不走 ChangeGameState。
                p.Session.Send(new S_CHANGE_GAME_STATE { State = room.State });
                int timeout = AckTimeoutMs != null ? AckTimeoutMs.Value : 2500;
                Plugin.Log.LogWarning($"[HS] GhostPhaseRefresh（临时补丁）：#{pid}（{r.Name}）死时在操作发信机，"
                    + $"已定向补发 S_CHANGE_GAME_STATE{{{room.State}}}，等回执（超时 {timeout}ms）后补 S_FADE_IN。");
                room.PushAfter(timeout, () => OnAckTimeout(pid));
            }
            catch (global::System.Exception ex)
            {
                Plugin.Log.LogWarning($"[HS] GhostPhaseRefresh：定向补发失败 — {ex.Message}");
            }
        }

        private static void OnAck(int pid)
        {
            Rec r;
            if (!Recs.TryGetValue(pid, out r) || !r.Fired || r.FadeInSent || r.AckReceived)
                return;
            r.AckReceived = true;
            SendFadeIn(pid, "已收到客户端回执");
        }

        private static void OnAckTimeout(int pid)
        {
            Rec r;
            if (!Recs.TryGetValue(pid, out r) || !r.Fired || r.FadeInSent)
                return;
            // ⚠ 超时也必须补发：不补的话客户端会停在 _loadingUI != null 的加载态，比不修更糟。
            //    这发 C_COMPLETE 对房间是无害空操作（GameRoom.CompletePacket 无等待者时不做事），
            //    所以超时并不代表"客户端没进加载态"——也可能它走了 IsCatchingUp 的同步解锁路径。
            Plugin.Log.LogWarning($"[HS] GhostPhaseRefresh（临时补丁）：#{pid} 等 C_COMPLETE 回执超时 —— "
                + "仍补发 S_FADE_IN（否则客户端会停在加载态）。");
            SendFadeIn(pid, "回执超时兜底");
        }

        private static void SendFadeIn(int pid, string why)
        {
            Rec r;
            if (!Recs.TryGetValue(pid, out r) || r.FadeInSent)
                return;
            r.FadeInSent = true;

            // ★ 只在那个人"确实处于我们发起的那次加载"时才补发：
            //   若此刻房间已在真正的换阶段 / 回放刚插进来，那次转换**自己会发** S_FADE_IN，
            //   我们抢先发反而会把它的加载页提前收掉。⇒ 让给原版，我们什么都不做。
            var room = GameRoom.Instance;
            if (room == null || room.State != EGameState.Survive
                || room.IsTransitioning || room.IsMigrating
                || ReplayDirector.Current != ReplayDirector.Phase.Idle)
            {
                Plugin.Log.LogInfo($"[HS] GhostPhaseRefresh（临时补丁）：#{pid} 不补 S_FADE_IN —— "
                    + "房间已在真实换阶段/回放中，那次转换自己会收掉加载页。");
                return;
            }

            var p = Find(pid);
            if (p?.Session == null || !ReferenceEquals(p, r.Owner))
            {
                Log($"#{pid} 不再在场，放弃补 S_FADE_IN。");
                return;
            }
            try
            {
                p.Session.Send(new S_FADE_IN());
                Plugin.Log.LogInfo($"[HS] GhostPhaseRefresh（临时补丁）：#{pid}（{r.Name}）{why} ⇒ 已补发 S_FADE_IN"
                    + "（客户端 EndLoading 回调会把 CanControl 置 true）。");
                ScheduleVerify(pid);
            }
            catch (global::System.Exception ex)
            {
                Plugin.Log.LogWarning($"[HS] GhostPhaseRefresh：补发 S_FADE_IN 失败 — {ex.Message}");
            }
        }

        private static void ScheduleVerify(int pid)
        {
            // 事后回看：他"有没有真的能动"。服务端看不到 CanControl，但看得到坐标有没有变 —— 这足以在实机日志里确认生效。
            GameRoom.Instance?.PushAfter(VerifyAfterMs, () => Verify(pid));
        }

        private static void Verify(int pid)
        {
            Rec r;
            if (!Recs.TryGetValue(pid, out r))
                return;
            var p = Find(pid);
            if (p?.PublicInfo?.Pos == null)
                return;
            float dx = p.PublicInfo.Pos.X - (r.PosAtDeath?.X ?? p.PublicInfo.Pos.X);
            float dy = p.PublicInfo.Pos.Y - (r.PosAtDeath?.Y ?? p.PublicInfo.Pos.Y);
            bool moved = dx * dx + dy * dy > 1f;
            // 这条**不**受 VerboseLog 管：它是"实机上怎么确认这次补刷新真的生效"的唯一直接证据。
            Plugin.Log.LogInfo($"[HS] GhostPhaseRefresh（临时补丁）：#{pid}（{r.Name}）补刷新后 {VerifyAfterMs}ms："
                + $"坐标{(moved ? "已变化（他拿回操作权并动了）" : "仍未变化（可能没动、也可能仍卡 —— 用 P1 探针确认 CanControl）")}。");
        }
    }
}
