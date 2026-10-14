using System.Collections.Generic;
using System.Linq;
using BepInEx.Configuration;
using HarmonyLib;
using DummyClient;          // SteamP2PSession（读每玩家 RTT 用；照 ReplayFeature / CommandFeature 的写法）
using Protocol;
using Server.Game;
using HideAndSeek.Core;
using UnityEngine;
using GamePlayer = Server.Game.Player;

namespace HideAndSeek.Features.Combat
{
    /// <summary>
    /// 延迟刀缓解：服务端回退判定（backwards reconciliation，Overwatch/Valorant/CoD/CS 同款思路）。
    ///
    /// 病因（源码事实，行号 = .tmps/decomp/Assembly-CSharp.decompiled.cs，0.1.14b，未用新版 IL 复核）：
    ///   客户端出刀闸门 = 距离 ≤224 + Raycast(mask 12288) 无遮挡，判的是**受害者过期+外推位**（ACS:15072-15099）；
    ///   服务端 UseWeapon = 距离 ≤672，判的是**服务端当前位**，且**完全不看视线**（ACS:176263-176312）。
    ///   ⇒ 「没看到人就死了」的主因不是 1格vs3格，而是无视线 + 双方画面各滞后约一个 RTT。
    ///
    /// 方案：出刀时把**目标回退**到杀手屏幕上所见样本（Δ = 50ms + 杀手 RTT，推导见设计文档 §3.2）、
    /// **攻击者**按最近一次移动样本的 age 外推，然后用回退位判距离（默认 336）与视线（mask 12288，贴脸豁免）。
    ///   只回退目标 —— 双方回退会在黑冲刺时按 v_K×Δ 虚增距离、系统性吞合法冲刺刀（设计文档 §3.2(3)）；
    ///   攻击者外推跨度 = age —— up_K 在样本与点击时刻两边抵消，外推到点击时刻反而欠推 v×up_K（§3.2(2)）。
    ///
    /// 规格全文：.tmps/延迟刀-回退判定-设计.md（不进 git）。本文只实现其 §3.1/§4/§8。
    ///
    /// 开关分两级（房主最易踩的点）：
    ///   · 段开关 [KillLagComp].Enabled —— 插件加载时决定补丁**挂不挂**，改它要重启游戏；
    ///   · <see cref="Active"/> —— 运行期热开关，决定挂着的补丁**干不干活**，局内随时切、立即生效。
    ///   Move 采样钩子**不受 Active 控制**（常录）：否则关闭再开会因历史空窗而吞一刀。
    /// </summary>
    [PatchFeature(
        section: "KillLagComp",
        description: "延迟刀缓解：服务端回退判定——把目标回退到杀手出刀瞬间所见的位置，再校验距离与视线；缓解「没看到人就死了」且不增加黑方吞刀。",
        defaultEnabled: true,
        side: FeatureSide.Host)]
    internal static class KillLagCompFeature
    {
        // ─────────────────────────── 配置 ───────────────────────────

        /// <summary>运行期总开关（热开关，不需要重启）。段开关只管挂载，见类注释。</summary>
        [ConfigField(true, "运行期总开关（热开关，不需要重启游戏）：关掉即完全恢复原版出刀判定。段开关 [KillLagComp].Enabled 只决定补丁挂不挂（改它要重启游戏）。")]
        public static ConfigEntry<bool> Active;

        /// <summary>
        /// 回退量模式。KillerRttFull = DeltaIntervalMs + 杀手 RTT（推导值）；
        /// Fixed = DeltaFixedMs；BothRtt = DeltaIntervalMs + (杀手RTT+目标RTT)/2（A/B 对照组）。
        /// </summary>
        [ConfigField("KillerRttFull", "回退量模式：KillerRttFull = 间隔毫秒 + 杀手 RTT（默认，推导值）；Fixed = 固定兜底毫秒；BothRtt = 间隔毫秒 + (杀手RTT+目标RTT)/2（A/B 对照组）。")]
        public static ConfigEntry<string> DeltaMode;

        /// <summary>回退量的固定部分 ≈ C_MOVE 半个发包间隔（客户端 10Hz 发包，ACS:14873-74）。</summary>
        [ConfigField(50f, "回退量的固定部分（毫秒）≈ 客户端 C_MOVE 半个发包间隔（10Hz，ACS:14873-74）。", Min = 0f, Max = 200f)]
        public static ConfigEntry<float> DeltaIntervalMs;

        [ConfigField(60f, "回退量下限（毫秒）。", Min = 60f, Max = 600f)]
        public static ConfigEntry<float> DeltaMinMs;

        /// <summary>上限默认 350：只影响 RTT>250ms 的杀手；调大更护黑，调小更护白（设计文档 §5）。</summary>
        [ConfigField(350f, "回退量上限（毫秒）。只影响 RTT>250ms 的杀手；调大更护黑（高延迟杀手补偿更足），调小更护白（「幽灵距离」更短）。", Min = 60f, Max = 600f)]
        public static ConfigEntry<float> DeltaMaxMs;

        /// <summary>拿不到 RTT（假人/Steam 状态异常）时的固定回退量。</summary>
        [ConfigField(150f, "拿不到 RTT（假人 / Steam 状态异常）时的固定回退量（毫秒）。", Min = 0f, Max = 500f)]
        public static ConfigEntry<float> DeltaFixedMs;

        /// <summary>回退位距离上限。原版服务端 672（3 格）且不看视线；336 = 客户端闸门 224 + 112 估计误差余量。</summary>
        [ConfigField(336f, "回退位距离上限（世界单位，224 = 一格）。原版服务端为 672（3 格）且不看视线；336 = 客户端闸门 224 + 112 估计误差余量。", Min = 224f, Max = 672f)]
        public static ConfigEntry<float> MaxRange;

        /// <summary>回退位视线检查。掩码 12288 = 层12(Block 4096)+层13，与客户端索敌同款（ACS:15090-15091）；AOI 那个 4096 是另一套语义，勿混用。</summary>
        [ConfigField(true, "回退位视线检查（掩码 12288，与客户端索敌 GetTargetPlayer 同款，ACS:15090-15091；AOI 的 4096 是另一套语义，勿混用）。")]
        public static ConfigEntry<bool> LosCheck;

        /// <summary>贴脸豁免：距离小于它不判遮挡（防「贴墙分站两侧、走到脸上却砍不着」，同 AoiCullingFeature.WallGraceRange 思路）。</summary>
        [ConfigField(224f, "贴脸豁免半径：距离小于它不判遮挡（防「贴墙分站两侧、走到脸上却砍不着」）。", Min = 0f, Max = 672f)]
        public static ConfigEntry<float> LosGrace;

        /// <summary>吞刀时给攻击者播失败音效，让黑知道「被规则吞了」而不是卡刀。</summary>
        [ConfigField(true, "吞刀时给攻击者播放失败音效（ESoundType.FailedSfx），让黑知道「被规则吞了」而不是「卡刀」。")]
        public static ConfigEntry<bool> RejectFeedback;

        /// <summary>
        /// 出刀通过时向除攻击者外的每个会话补发一条攻击者权威坐标的 S_MOVE。
        /// ⚠ 绝不能群发（GameRoom.Broadcast）：攻击者本人是位置权威（客户端本地先行），
        /// 给他发会把他拉回旧位；且客户端每收一条 S_MOVE 都会录进回放磁带（ACS:42224-42240），
        /// 而那一幕的 recorder 正是攻击者（S_RECORD_REPLAY 只发他，ACS:175957-960）⇒ 群发会让回放里凶手「眨眼」。
        /// </summary>
        [ConfigField(false, "出刀通过时向除攻击者外的每个会话补发一条攻击者权威坐标的 S_MOVE（缓解「间距错觉」）。默认关；绝不能群发，原因见设计文档 §3.4。")]
        public static ConfigEntry<bool> KillPosSync;

        /// <summary>受击时刻就在攻击者位置播世界音（原版要等 400ms 后的死亡回调，ACS:175953-964）。</summary>
        [ConfigField(false, "受击时刻就在攻击者位置播放世界音（原版要等 400ms 后的死亡回调，ACS:175953-964），让受害者立刻获得方向感。默认关。")]
        public static ConfigEntry<bool> HitSfxEarly;

        /// <summary>DT 扩展。「被害者在 DT zone 内」是客户端 BoxCollider2D 概念（ACS:7687-7742），服务端无法复刻 ⇒ DT 只能做到 3/4，默认关。</summary>
        [ConfigField(false, "对致命诡计（DT，UseDeadlyTrick ACS:176314）也应用回退判定。默认关：「被害者在 DT zone 内」这一维服务端无法复刻（zone 是客户端 BoxCollider2D，ACS:7687-7742）。")]
        public static ConfigEntry<bool> ApplyToDeadlyTrick;

        /// <summary>A/B 依据：逐条拒绝日志带 dx/dy、回退量与样本状态。关掉只留 KillBlocker 的单行摘要。</summary>
        [ConfigField(true, "逐条拒绝日志（A/B 调参依据，带 dx/dy、回退量、模式与样本状态）。关掉则只留 KillBlocker 的单行摘要。")]
        public static ConfigEntry<bool> LogDetails;

        // ─────────────────────────── 常量 ───────────────────────────

        /// <summary>LOS 掩码：层 12（"Block"，4096）+ 层 13（8192）= 12288，客户端出刀闸门同款。</summary>
        private const int LosMask = 12288;

        /// <summary>攻击者外推的最大跨度（毫秒）。超过即认为输入陈旧（掉包/封锁窗口），不再外推。</summary>
        private const float MaxExtrapolateMs = 150f;

        /// <summary>历史保留时长（毫秒）。只需覆盖回退量上限 + 余量。</summary>
        private const float KeepMs = 1500f;

        /// <summary>每玩家最多样本数（10Hz × 1.5s ≈ 15，留一倍余量）。</summary>
        private const int MaxSamples = 64;

        /// <summary>ping 读取失败的告警只打一次（避免每次出刀都刷）。</summary>
        private static bool _pingWarned;

        private static bool ActiveOn => Active == null || Active.Value;

        // ─────────────────────────── 钩子 ───────────────────────────

        /// <summary>
        /// 移动采样：服务端每次收到 C_MOVE 后 <c>Player.Move</c>（ACS:175883）被调用，
        /// 这正是「广播给观察者的那一刻」⇒ 样本 = 杀手屏幕上所见位置的权威来源。
        /// <para>
        /// ★ 常录不判断：不受 <see cref="Active"/> 控制（设计文档 §4）——
        /// 否则关闭再开会因历史空窗而吞一刀；内存有界（每玩家 ≤64 样本）。
        /// </para>
        /// </summary>
        [HarmonyPatch(typeof(GamePlayer), nameof(GamePlayer.Move), new[] { typeof(PosInfo), typeof(bool) })]
        internal static class MoveHook
        {
            [HarmonyPostfix]
            private static void Postfix(GamePlayer __instance, PosInfo pos, bool force)
            {
                History.Note(__instance, pos, force);
            }
        }

        /// <summary>
        /// 出刀判定入口。与 LunaImmunity / FirstDeathProtect 同方法（UseWeapon）：
        /// 三者判据正交，Harmony 2.2+ 下所有 Prefix 都会跑、return false 只跳原方法体；
        /// 显式 <c>Priority.Last</c> 让免疫/保护先判（日志顺序稳定），双重收刀幂等无害
        /// （HolsterWeaponAfterSwing 的 PushAfter(1000) 回调自带条件，ACS:176252-176261）。
        /// </summary>
        [HarmonyPatch(typeof(GamePlayer), nameof(GamePlayer.UseWeapon))]
        [HarmonyPriority(Priority.Last)]
        internal static class WeaponHook
        {
            [HarmonyPrefix]
            private static bool Prefix(GamePlayer __instance, int targetId)
            {
                if (ModeRuntime.Bypass || !ActiveOn) return true;   // 模式总开关关 / 本功能热开关关 → 完全让给原版
                return Judge(__instance, targetId, "KillLagComp", posSync: true);
            }
        }

        /// <summary>
        /// DT 扩展（默认关）。与刀共用同一套判定；zone 维度不查（服务端无数据）。
        /// 扛尸分支（CarryingCorpseId != -1）不杀人，直接放过。
        /// </summary>
        [HarmonyPatch(typeof(GamePlayer), nameof(GamePlayer.UseDeadlyTrick), new[] { typeof(C_DEADLY_TRICK) })]
        [HarmonyPriority(Priority.Last)]
        internal static class DeadlyTrickHook
        {
            [HarmonyPrefix]
            private static bool Prefix(GamePlayer __instance, C_DEADLY_TRICK pkt)
            {
                if (ModeRuntime.Bypass || !ActiveOn) return true;
                if (ApplyToDeadlyTrick == null || !ApplyToDeadlyTrick.Value) return true;
                if (__instance.CarryingCorpseId != -1) return true;    // 扛尸分支：不杀人（ACS:176432-176467）
                // DT 自带 S_DEADLY_TRICK 广播 + 黑方摆位，不需要位置补正
                return Judge(__instance, pkt.TargetId, "KillLagComp/DT", posSync: false);
            }
        }

        /// <summary>每局清空历史（Player 对象整房存活，跨局样本没有意义）。</summary>
        [HarmonyPatch(typeof(GameRoom), "StartSurvive")]
        internal static class StartHook
        {
            [HarmonyPostfix]
            private static void Postfix()
            {
                History.Clear();
            }
        }

        // ─────────────────────────── 判定 ───────────────────────────

        /// <summary>
        /// 核心判定。返回 true = 放行（交原版继续）；false = 已替原版拒绝（收刀+音效+日志已做）。
        /// </summary>
        private static bool Judge(GamePlayer attacker, int targetId, string tag, bool posSync)
        {
            var room = GameRoom.Instance;
            if (room == null || room.State != EGameState.Survive) return true;
            if (attacker == null || !attacker.IsAlive) return true;

            var target = room.Players?.FirstOrDefault(p => p.PublicInfo.PlayerId == targetId);
            if (target == null) return true;              // 目标查不到等其余校验，交原版

            Diagnostics.Hit("KillLagComp");

            float now = Time.realtimeSinceStartup;
            float maxRange = MaxRange?.Value ?? 336f;
            bool los = LosCheck == null || LosCheck.Value;
            float grace = LosGrace?.Value ?? 224f;

            // 攻击者：最新移动样本 + age 外推（无样本 → 当前 PublicInfo.Pos、不外推）
            if (!History.Attacker(attacker.PublicInfo.PlayerId, now, out float kx, out float ky))
            {
                kx = attacker.PublicInfo.Pos.X;
                ky = attacker.PublicInfo.Pos.Y;
            }

            string mode = (DeltaMode?.Value ?? "KillerRttFull").Trim();
            float deltaMs = ComputeDelta(attacker, target, mode);

            // 目标：回退到 now − Δ；无样本 / 跨断裂 → 用当前位并记进日志
            bool degraded = !History.Rewind(target.PublicInfo.PlayerId, now, deltaMs, out float tx, out float ty);
            if (degraded)
            {
                tx = target.PublicInfo.Pos.X;
                ty = target.PublicInfo.Pos.Y;
            }

            float dx = kx - tx;
            float dy = ky - ty;
            float d2 = dx * dx + dy * dy;
            string why = null;

            if (d2 > maxRange * maxRange)
            {
                float d = Mathf.Sqrt(d2);
                why = $"[{mode}] 距离越界 d={d:F0}≈{d / 224f:F1}格 > 上限 {maxRange:F0} dx={dx:F0} dy={dy:F0}";
            }
            else if (los && d2 > grace * grace)
            {
                Vector2 from = new Vector2(kx, ky);
                Vector2 dir = new Vector2(tx - kx, ty - ky);
                float mag = dir.magnitude;
                if (mag > 0.01f && Physics2D.Raycast(from, dir, mag, LosMask).collider != null)
                    why = $"[{mode}] 视线遮挡 d={mag:F0} dx={dx:F0} dy={dy:F0}";
            }

            if (why == null)
            {
                if (posSync) OnAccept(attacker);
                return true;
            }

            if (LogDetails == null || LogDetails.Value)
                why += $"（Δ={deltaMs:F0}ms，目标样本{(degraded ? "缺失→用当前位" : "ok")}）";

            KillBlocker.Reject(attacker, target, tag, RejectFeedback == null || RejectFeedback.Value, why);
            return false;
        }

        /// <summary>接受一刀后的观感补正（D 组，均默认关）。</summary>
        private static void OnAccept(GamePlayer attacker)
        {
            if (KillPosSync != null && KillPosSync.Value)
            {
                try
                {
                    // 只逐会话发给除攻击者以外的人 —— 原因见配置项注释与设计文档 §3.4
                    var pkt = new S_MOVE
                    {
                        PlayerId = attacker.PublicInfo.PlayerId,
                        Pos = attacker.PublicInfo.Pos.Clone(),
                        LookLeft = attacker.PublicInfo.LookLeft,
                        Velocity = attacker.Velocity,
                        IsMove = attacker.IsMove,
                    };
                    var room = GameRoom.Instance;
                    if (room?.Players != null)
                    {
                        for (int i = 0; i < room.Players.Count; i++)
                        {
                            var p = room.Players[i];
                            if (p == null || p == attacker || p.Session == null) continue;
                            p.Session.Send(pkt);
                        }
                    }
                }
                catch (global::System.Exception ex)
                {
                    Plugin.Log.LogWarning($"[HS] KillLagComp：位置补正发送失败 — {ex.Message}");
                }
            }

            if (HitSfxEarly != null && HitSfxEarly.Value)
            {
                try
                {
                    // 距离 896 = 原版世界音效默认可听范围（ACS:172111）
                    GameRoom.Instance?.BroadcastWorldSFX(ESoundType.HitKnifeSfx, attacker.PublicInfo.Pos, 896f);
                }
                catch (global::System.Exception ex)
                {
                    Plugin.Log.LogWarning($"[HS] KillLagComp：命中音效播放失败 — {ex.Message}");
                }
            }
        }

        /// <summary>
        /// 回退量 Δ_k = clamp(Interval + ping, Min, Max)。推导（设计文档 §3.2(1)）：
        /// 杀手屏上目标样本的广播时刻 ≈ t₁ − down_K − w，回退截止 = up_K + down_K + w = **ping_K + w** ——
        /// 是**全 RTT**（不是半 RTT），且 ping_V 数学上抵消（匹配的是样本本身）。
        /// </summary>
        private static float ComputeDelta(GamePlayer attacker, GamePlayer target, string mode)
        {
            float interval = DeltaIntervalMs?.Value ?? 50f;
            float min = DeltaMinMs?.Value ?? 60f;
            float max = DeltaMaxMs?.Value ?? 350f;
            float fixedMs = DeltaFixedMs?.Value ?? 150f;

            float raw;
            if (global::System.String.Equals(mode, "Fixed", global::System.StringComparison.OrdinalIgnoreCase))
            {
                raw = fixedMs;
            }
            else
            {
                int pk = PingOf(attacker);
                if (pk <= 0)
                {
                    raw = fixedMs;                       // 拿不到杀手 RTT → 兜底
                }
                else if (global::System.String.Equals(mode, "BothRtt", global::System.StringComparison.OrdinalIgnoreCase))
                {
                    int pv = PingOf(target);
                    raw = interval + (pv > 0 ? (pk + pv) * 0.5f : pk);
                }
                else
                {
                    raw = interval + pk;                 // KillerRttFull（默认，也是推导值）
                }
            }

            if (raw < min) raw = min;
            if (raw > max) raw = max;
            return raw;
        }

        /// <summary>
        /// 每玩家实时 RTT（全公开 API 链，零反射；ACS:160491/172513/175210/160564-587）：
        /// Player.Session(HostPeerSession) → .Underlying(IPacketSink，运行期即 SteamP2PSession)
        /// → TryGetUplinkStatus(out _, out _, out pingMs) = GetConnectionRealTimeStatus().m_nPing。
        /// 必须在 Unity 主线程调用（UseWeapon 本来就是）。
        /// </summary>
        private static int PingOf(GamePlayer p)
        {
            try
            {
                if (p?.Session?.Underlying is SteamP2PSession p2p
                    && p2p.TryGetUplinkStatus(out _, out _, out int ping))
                    return ping;
            }
            catch (global::System.Exception ex)
            {
                if (!_pingWarned)
                {
                    _pingWarned = true;
                    Plugin.Log.LogWarning($"[HS] KillLagComp：读取 RTT 失败（此后只告警一次）— {ex.Message}");
                }
            }
            return -1;
        }

        // ─────────────────────────── 位置历史 ───────────────────────────

        /// <summary>
        /// 每玩家一小段位置历史（环形保留 ~1.5s）。时钟用 <c>Time.realtimeSinceStartup</c>
        /// （进程内单调）—— **不能用 SurviveTime**（每局回 420，事实索引有同款事故）。
        /// </summary>
        internal static class History
        {
            private struct Sample
            {
                public float T;      // realtimeSinceStartup
                public float X, Y;
                public float DirX, DirY;   // 与上一【非 force】样本的位移方向（归一化；0 = 不外推）
                public float Speed;        // 采样时的 Player.Velocity
                public bool Force;         // 服务端强制位移（传送/复活/DT 摆位）⇒ 断裂点
            }

            private sealed class Track
            {
                public readonly List<Sample> Samples = new List<Sample>(16);
                public bool HasPrev;
                public float PrevX, PrevY;
            }

            private static readonly Dictionary<int, Track> Tracks = new Dictionary<int, Track>();

            public static void Clear() => Tracks.Clear();

            /// <summary>记一个样本。id ≤ 0 的丢弃（未绑定会话，与 HostRecorder.NoteMove 同判据）。</summary>
            public static void Note(GamePlayer p, PosInfo pos, bool force)
            {
                if (p?.PublicInfo == null || pos == null) return;
                int id = p.PublicInfo.PlayerId;
                if (id <= 0) return;

                float now = Time.realtimeSinceStartup;
                if (!Tracks.TryGetValue(id, out var t))
                {
                    t = new Track();
                    Tracks[id] = t;
                }

                var s = new Sample { T = now, X = pos.X, Y = pos.Y, Force = force };
                if (!force)
                {
                    s.Speed = p.Velocity;
                    if (t.HasPrev)
                    {
                        float dx = s.X - t.PrevX;
                        float dy = s.Y - t.PrevY;
                        float m = Mathf.Sqrt(dx * dx + dy * dy);
                        if (m > 0.01f) { s.DirX = dx / m; s.DirY = dy / m; }
                    }
                    t.PrevX = s.X;
                    t.PrevY = s.Y;
                    t.HasPrev = true;
                }
                else
                {
                    // force 样本：Dir/Speed 保持 0（传送后不外推）；HasPrev 复位，
                    // 否则下一个样本会跨断裂点算出错误方向
                    t.HasPrev = false;
                }

                t.Samples.Add(s);
                while (t.Samples.Count > MaxSamples) t.Samples.RemoveAt(0);
                float cutoff = now - KeepMs;
                while (t.Samples.Count > 1 && t.Samples[0].T < cutoff) t.Samples.RemoveAt(0);
            }

            /// <summary>
            /// 攻击者新鲜位：最新样本 + 按 age 外推（跨度 = T − 最后样本到达时刻，设计文档 §3.2(2)）。
            /// age &gt; 150ms 视为输入陈旧（掉包 / MoveLock 封锁窗口），不再外推。
            /// </summary>
            public static bool Attacker(int id, float now, out float x, out float y)
            {
                x = 0f;
                y = 0f;
                if (!Tracks.TryGetValue(id, out var t) || t.Samples.Count == 0) return false;

                var s = t.Samples[t.Samples.Count - 1];
                x = s.X;
                y = s.Y;

                float age = now - s.T;
                if (age <= 0f || age > MaxExtrapolateMs) return true;   // 不补（含时钟倒退的防御）
                if (s.Speed > 0.01f && (s.DirX != 0f || s.DirY != 0f))
                {
                    float d = s.Speed * age / 1000f;
                    x += s.DirX * d;
                    y += s.DirY * d;
                }
                return true;
            }

            /// <summary>
            /// 目标回退位：t ≤ now − deltaMs 的最新样本，且**不跨断裂点**
            /// （候选与 now 之间若有 force 样本 ⇒ 传送后杀手看到的是新位置，旧样本作废）。
            /// </summary>
            public static bool Rewind(int id, float now, float deltaMs, out float x, out float y)
            {
                x = 0f;
                y = 0f;
                if (!Tracks.TryGetValue(id, out var t) || t.Samples.Count == 0) return false;

                float cutoff = now - deltaMs;
                var list = t.Samples;
                bool seenBreak = false;
                for (int i = list.Count - 1; i >= 0; i--)
                {
                    var s = list[i];
                    if (s.T > cutoff)
                    {
                        if (s.Force) seenBreak = true;    // 截止时刻之后的断裂：旧样本作废
                        continue;
                    }
                    if (seenBreak) return false;          // 候选与 now 之间有断裂 → 放弃回退
                    x = s.X;
                    y = s.Y;
                    return true;
                }
                return false;                             // 全部样本都比截止时刻新（Δ 比历史长）
            }
        }
    }
}
