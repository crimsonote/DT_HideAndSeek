using System;
using System.Collections.Generic;
using System.Linq;
using BepInEx.Configuration;
using DummyClient;
using HarmonyLib;
using Protocol;
using Server.Game;
using HideAndSeek.Core;
using HideAndSeek.Features.Rule;
using HideAndSeek.Features.Weapon;
using GamePlayer = Server.Game.Player;

namespace HideAndSeek.Features.Replay
{
    /// <summary>
    /// 回放重写版 —— 当前处于**影子模式**：只计算并打印"如果换我，这一幕会怎么排"，
    /// **不发包、不改游戏状态、不播回放**。同一局里现有实现（<c>[EndReplay]</c>）照常工作，
    /// 于是日志里会并列出现两套决策，可以直接对照。
    ///
    /// 为什么先做影子模式：验证一次改动需要打一局，而"打一局"的成本不该反复花在用户身上。
    /// 影子模式让**你照常玩**，决策差异在日志里自己显形。
    ///
    /// 接入约定（重要）：
    ///   · 全部钩子用 **Postfix** —— 现有实现的 `TotalResultHook` 是 Prefix 且会 `return false`，
    ///     而 Harmony 里"某个 Prefix 返回 false"会让**后面的 Prefix 不再执行**（Postfix 仍会执行）。
    ///     用 Postfix 才能保证影子逻辑一定跑得到。
    ///   · 配置项**暂时借用**现有实现的 <c>[EndReplay]</c> 那些值（阶段 6 会把它们搬过来），
    ///     所以现在不需要新增任何配置键。
    /// </summary>
    [PatchFeature(
        section: "ReplayDraft",
        description: "回放重写版（当前：影子模式）。只计算并打印幕表，不发包、不播回放 —— 用于与现有实现【EndReplay】对照。",
        defaultEnabled: true,
        side: FeatureSide.Host)]
    internal static class ReplayFeature
    {
        [ConfigField(true, "影子模式：只计算与记录，不发包、不播回放。用于与现有实现对照。")]
        public static ConfigEntry<bool> ShadowOnly;

        [ConfigField(false, "【验证用·默认关】真的向客户端索取真实磁带并装配 —— 但**不广播、不播放**，"
            + "只把装配结果打进日志并落盘到 tapedump/。"
            + "它验证的是整套重写里唯一没法用合成数据覆盖的一环：**真实客户端磁带**能不能被新装配器正确处理"
            + "（旧实现所有窗口错误都出在「拿真实磁带 + 用锚点算窗口」这一步）。"
            + "⚠ 开着它会让每个有客户端的录制者多回一轮磁带；装完后关掉。")]
        public static ConfigEntry<bool> FetchRealTapes;

        // ── 状态（**一个**地方）────────────────────────────────────────
        private static readonly List<Act> Acts = new List<Act>();

        /// <summary>受害者"最后一次被击中"的时刻。`OnDamaged` 记、`KillHook` 取 ——
        /// 因为 `OnDead` 的时刻**不等于**击杀时刻（实测晚 1.4~1.8 秒）。</summary>
        private static readonly Dictionary<int, float> LastHitAt = new Dictionary<int, float>();
        private static int _nextKey;
        private static int _murderDeaths;
        private static string _ending = "结算前";
        private static bool _reported;

        private static bool Armed
            => !ModeRuntime.Bypass && (ShadowOnly?.Value ?? true) && Diagnostics.IsLoaded("ReplayDraft");

        private static float Now()
        {
            try { return Managers.Game.ClientTime; }
            catch { return 0f; }
        }

        private static int NextKey() => _nextKey++;

        private static Act Add(ActKind kind, int subjectId, int recorderId, ReplayWindow.Span window, string note)
        {
            var act = new Act
            {
                Kind = kind,
                Key = NextKey(),
                SubjectId = subjectId,
                RecorderId = recorderId,
                Window = window,
                Note = note,
            };
            Acts.Add(act);

            // 【真索带模式】请录制者在**此刻**把当前时刻登记成一个可裁事件：
            //  客户端 `ReserveSaveTape` 会把它记进 `_killLocalTime[key]` 并插一枚 `NormalTimeEdit`。
            // 没有这一步，客户端 `BuildUploadTape` 查不到这个 key 会回**空磁带**（实测踩过）。
            if (FetchRealTapes?.Value ?? false)
            {
                var p = FindPlayer(GameRoom.Instance, act.RecorderId);
                if (p?.Session != null)
                    p.Session.Send(new S_RECORD_REPLAY { RecordTime = act.Key });
            }

            return act;
        }

        private static bool HasKind(ActKind kind)
        {
            foreach (var a in Acts)
            {
                if (a.Kind == kind)
                    return true;
            }
            return false;
        }

        private static void Reset()
        {
            Acts.Clear();
            _nextKey = 0;
            _murderDeaths = 0;
            _ending = "结算前";
            _reported = false;
            HostRecorder.Clear();
            ReplayDirector.ReportFetch();   // 上一局的真实磁带汇总（如果有）
            LastHitAt.Clear();
            ReplayDirector.Reset();
        }

        // 借用现有实现的配置值（阶段 6 搬过来就独立了）
        private static float KnifeBefore => EndReplayFeature.KnifeBeforeSec?.Value ?? 1f;
        private static float KnifeAfter => EndReplayFeature.KnifeAfterSec?.Value ?? 1f;
        private static float KillBefore => EndReplayFeature.KillBeforeSec?.Value ?? 3f;
        private static float KillAfter => EndReplayFeature.KillAfterSec?.Value ?? 1f;
        private static float EndBefore => EndReplayFeature.EndBeforeSec?.Value ?? 3f;
        private static float EndAfter => EndReplayFeature.EndAfterSec?.Value ?? 1f;
        private static float TourBefore => EndReplayFeature.TourBeforeSec?.Value ?? 2f;
        private static float TourAfter => EndReplayFeature.TourAfterSec?.Value ?? 0.5f;
        private static float SelfBefore => EndReplayFeature.SelfDestructBeforeSec?.Value ?? 0f;
        private static float SelfAfter => EndReplayFeature.SelfDestructAfterSec?.Value ?? 0.5f;
        private static float BlackTail => Math.Max(0f, EndReplayFeature.BlackTailSec?.Value ?? 3f);
        private static int MaxClips => EndReplayFeature.MaxClips?.Value ?? 12;

        // ── 钩子 ────────────────────────────────────────────────────────

        /// <summary>一局开始：清状态、开采样。</summary>
        [HarmonyPatch(typeof(GameRoom), nameof(GameRoom.ChangeGameState), new[] { typeof(EGameState) })]
        internal static class RoundStartHook
        {
            [HarmonyPostfix]
            private static void Postfix(EGameState state)
            {
                if (state == EGameState.Survive)
                    Reset();
            }
        }

        /// <summary>
        /// 【采样点】`Player.Move` —— 服务端每次收到 `C_MOVE` 后调用它。
        /// 用它而不是定时 5Hz，是为了让服务端合成的密度与客户端录制**同源**。
        /// </summary>
        [HarmonyPatch(typeof(GamePlayer), "Move", new[] { typeof(PosInfo), typeof(bool) })]
        internal static class SampleHook
        {
            [HarmonyPostfix]
            private static void Postfix(GamePlayer __instance)
            {
                if (!Armed)
                    return;
                HostRecorder.NoteMove(__instance);
            }
        }

        /// <summary>
        /// 【兜底采样】1Hz 把当前所有玩家各记一帧。
        ///
        /// 为什么需要它：主采样点挂在 `Player.Move`（服务端每次收到 `C_MOVE` 后调用），
        /// 频率与客户端同源 —— 但**假人不主动发包**，站桩的玩家也不发包，
        /// 于是"从未移动过的人"可能一帧都没有（实测单人测试时约 1.1 帧/秒/人）。
        /// 这一路只保证"每个人至少被记到一次"，不去追高频。
        /// </summary>
        [HarmonyPatch(typeof(Managers), "Update")]
        internal static class HeartbeatHook
        {
            private static float _last;

            [HarmonyPostfix]
            private static void Postfix()
            {
                if (!Armed)
                    return;
                float now = Now();
                if (now - _last < 1f)
                    return;
                _last = now;
                HostRecorder.SampleAll();
            }
        }

        /// <summary>「拿刀」—— `DelayAcquireWeapon` 是拔刀后的入口，只在真正拿到武器的人身上跑。</summary>
        [HarmonyPatch(typeof(GamePlayer), "DelayAcquireWeapon")]
        internal static class KnifeHook
        {
            [HarmonyPostfix]
            private static void Postfix(GamePlayer __instance)
            {
                if (!Armed)
                    return;
                // 只在自提模式登记：开局发刀没有"跑刀"过程，录出来只是站着不动。
                if (WeaponGrantFeature.GiveAtStart?.Value ?? false)
                    return;
                if (__instance?.PublicInfo == null || __instance.Session == null)
                    return;
                if (__instance.Color != EPlayerColor.Black && __instance.Color != EPlayerColor.Dark)
                    return;

                int id = __instance.PublicInfo.PlayerId;
                Add(ActKind.Knife, id, id, ActTable.Plain(Now(), KnifeBefore, KnifeAfter), "拿刀");
            }
        }

        /// <summary>
        /// 【事件时刻修正】记下"谁在什么时候被击中"。
        ///
        /// 为什么必须有它：`OnDead` 的时刻**不等于**击杀时刻。原版是在 `OnDamaged` 里
        /// 设 `attacker.RecordTime` 并请客户端 `ReserveSaveTape` 的，而 `OnDead` 可能被推迟 ——
        /// 实测某次刀杀 `OnDead` 在 `t=60.49`，而客户端为它插的 `NormalTimeEdit` 在 `t=59.12`，
        /// 相差 **1.37 秒**。用 `OnDead` 的时刻算窗口，整幕会往后偏。
        /// </summary>
        [HarmonyPatch(typeof(GamePlayer), "OnDamaged")]
        internal static class HitHook
        {
            [HarmonyPostfix]
            private static void Postfix(GamePlayer __instance, GamePlayer attacker)
            {
                if (!Armed)
                    return;
                int victim = __instance?.PublicInfo?.PlayerId ?? 0;
                if (victim <= 0 || attacker?.PublicInfo == null)
                    return;
                LastHitAt[victim] = Now();
            }
        }

        /// <summary>「杀人」—— 原版在 `OnDamaged` 里已经请凶手录过一段，我们只是登记。</summary>
        [HarmonyPatch(typeof(GamePlayer), nameof(GamePlayer.OnDead))]
        internal static class KillHook
        {
            [HarmonyPostfix]
            private static void Postfix(GamePlayer __instance, GamePlayer black, EDeathType type)
            {
                if (!Armed)
                    return;
                if (type != EDeathType.Murder)
                    return;                    // 项圈自爆与审判处决都不算

                _murderDeaths++;

                if (black?.PublicInfo == null)
                    return;

                int victim = __instance?.PublicInfo?.PlayerId ?? 0;
                int id = black.PublicInfo.PlayerId;
                // ★ 事件时刻必须取"被击中的那一刻"，不能取 OnDead 的时刻：
                //   原版是在 `OnDamaged` 里设 `attacker.RecordTime` 并请客户端 `ReserveSaveTape` 的，
                //   而 `OnDead` 可能被推迟 —— 实测某次刀杀 OnDead 在 t=60.49、客户端插的
                //   NormalTimeEdit 在 t=59.12，差 1.37 秒 ⇒ 用 OnDead 会让整幕往后偏。
                float at = LastHitAt.TryGetValue(victim, out var hitAt) ? hitAt : Now();
                LastHitAt.Remove(victim);
                Add(ActKind.Kill, id, id, ActTable.Plain(at, KillBefore, KillAfter), $"#{id} → #{victim}");
            }
        }

        /// <summary>
        /// 「自爆」—— `OnDeadCollarBomb` 只是**开始**自爆（真正死亡在 6 秒后）。
        /// 窗口取 <c>[t0 − 额外前秒, t0 + 6 + 后秒]</c>，即从"自爆开始"那一刻起算。
        /// ⚠ 它对每个存活白方各调一次 ⇒ 必须用 <see cref="HasKind"/> 保证只登记一次。
        /// </summary>
        [HarmonyPatch(typeof(GamePlayer), "OnDeadCollarBomb")]
        internal static class CollarBombHook
        {
            [HarmonyPostfix]
            private static void Postfix(GamePlayer __instance)
            {
                if (!Armed)
                    return;
                if (__instance?.PublicInfo == null || __instance.IsSpectator)
                    return;

                float t0 = Now();
                HostRecorder.NoteBomb(t0, __instance.PublicInfo.PlayerId, __instance.PublicInfo.Pos);

                if (HasKind(ActKind.SelfDestruct))
                    return;

                var room = GameRoom.Instance;
                if (room == null)
                    return;

                var subjects = AliveWhites(room);
                var window = ActTable.SelfDestruct(t0, SelfBefore, SelfAfter);
                int seq = 0;
                foreach (int id in subjects)
                {
                    if (Acts.Count >= MaxClips)
                        break;
                    // 平铺：第一段独占哑期（见 ReplayWindow.Tile 的说明）
                    var seg = ReplayWindow.Tile(seq, subjects.Count, window, 1.5f);
                    var p = FindPlayer(room, id);
                    Add(ActKind.SelfDestruct, id, p?.Session != null ? id : 0, seg, $"被处决者 #{id}");
                    seq++;
                }

                int blackId = FindBlackId(room);
                if (blackId > 0 && Acts.Count < MaxClips)
                {
                    // 黑方收尾：与爆炸同时
                    Add(ActKind.BlackTail, blackId, 0,
                        ActTable.BlackTailOnBomb(t0 + ActTable.CollarToDeadSec, BlackTail), "黑方收尾");
                }
            }
        }

        /// <summary>「巡礼」—— 白胜：每个幸存者各一段，**同一段时间**、视角不同、依次播。</summary>
        [HarmonyPatch(typeof(WhiteWinFeature), nameof(WhiteWinFeature.TriggerWhiteWin))]
        internal static class WhiteWinHook
        {
            [HarmonyPostfix]
            private static void Postfix()
            {
                if (!Armed)
                    return;
                _ending = "白方胜利";

                if (HasKind(ActKind.Tour))
                    return;

                var room = GameRoom.Instance;
                if (room == null)
                    return;

                int blackId = FindBlackId(room);
                var subjects = AliveWhites(room);
                var window = ActTable.Plain(Now(), TourBefore, TourAfter);
                foreach (int id in subjects)
                {
                    if (Acts.Count >= MaxClips)
                        break;
                    var p = FindPlayer(room, id);
                    Add(ActKind.Tour, id, p?.Session != null ? id : 0, window, $"幸存者 #{id}");
                }

                if (blackId > 0 && Acts.Count < MaxClips)
                {
                    Add(ActKind.BlackTail, blackId, 0,
                        ActTable.BlackTailBeforeDecision(Now(), BlackTail), "黑方收尾（白胜）");
                }
            }
        }

        /// <summary>只做记录：黑方胜利的收尾演出跑过了。</summary>
        [HarmonyPatch(typeof(GameRoom), "GameOver")]
        internal static class BlackWinHook
        {
            [HarmonyPostfix]
            private static void Postfix()
            {
                _ending = "黑方胜利";
            }
        }

        /// <summary>
        /// 【结算】用 **Postfix**：现有实现的 `TotalResultHook` 是 Prefix 且会拦下这一次调用
        /// （`return false`），而 Harmony 在 Prefix 返回 false 时**仍会执行 Postfix** ⇒ 影子逻辑一定跑得到。
        /// 现有实现稍后会自己再调一次 `ChangeGameState(TotalResult)`，那次它的 Prefix 放行，
        /// 于是这里会被调用第二次 —— 用 <see cref="_reported"/> 去重。
        /// </summary>
        [HarmonyPatch(typeof(GameRoom), nameof(GameRoom.ChangeGameState), new[] { typeof(EGameState) })]
        internal static class SettleHook
        {
            [HarmonyPostfix]
            private static void Postfix(EGameState state)
            {
                if (state != EGameState.TotalResult)
                    return;
                if (!Armed || _reported)
                    return;

                // ★ 去重必须放在入口：现有实现拦下第一次之后，会在回放结束时**自己再调一次**
                //   `ChangeGameState(TotalResult)`（那次它的 Prefix 放行）⇒ 这里会被调用第二次，
                //   于是整块影子输出会重复打印一遍。
                _reported = true;

                // 结算时补登记"结算类"的幕（它们的事件时刻就是"结算被拦下"这一刻）。
                RegisterSettlementActs();

                Report();   // 概览：阈值 / 采样
                // 影子：逐幕做一次完整装配并打印结果（不发包、不广播、不播放）。
                ReplayDirector.ShadowAssemble(Acts, EndReplayFeature.BombBlackout?.Value ?? false);

                // 【真索带】真的向客户端要一遍 —— 验证"真实客户端磁带 → 装配"这一环。
                // 收上来的包由下面的 TapeHook 拦住，绝不会被广播出去污染别人的回放。
                if (FetchRealTapes?.Value ?? false)
                    ReplayDirector.FetchTapes(Acts);
            }
        }

        /// <summary>
        /// 结算时补登记"结算类"的幕 —— 它们的事件时刻都是**结算被拦下**这一刻。
        ///
        /// 规则（避免同一件事登记两遍）：
        ///   · 已经有「自爆」幕（黑胜处决）⇒ 不再排「最后时段」：那一刻白方全死了，没有存活着可拍；
        ///   · 已经有「巡礼」幕（白胜）⇒ 同上，`TriggerWhiteWin` 已经排过；
        ///   · 否则 ⇒ 为**还活着的白方**各排一段「最后时段」（纯刀杀造成的黑胜局就是这种）；
        ///   · 「黑方收尾」若还没有就补一段 —— 黑胜时与爆炸同时，没有自爆就落在结算前。
        /// </summary>
        private static void RegisterSettlementActs()
        {
            try
            {
                var room = GameRoom.Instance;
                if (room == null)
                    return;

                int blackId = FindBlackId(room);
                float now = Now();

                if (!HasKind(ActKind.SelfDestruct) && !HasKind(ActKind.Tour))
                {
                    int seq = 0;
                    foreach (int id in AliveWhites(room))
                    {
                        if (Acts.Count >= MaxClips)
                            break;
                        var p = FindPlayer(room, id);
                        Add(ActKind.Final, id, p?.Session != null ? id : 0,
                            ActTable.Plain(now, EndBefore, EndAfter), $"存活者 #{id}");
                        seq++;
                    }
                    if (seq > 0)
                        Plugin.Log.LogInfo($"[HS-Shadow] 结算补登记「最后时段」{seq} 段（存活白方各一段、同一段时间）。");
                }

                if (!HasKind(ActKind.BlackTail) && blackId > 0 && Acts.Count < MaxClips)
                {
                    // 黑方收尾的时刻：有自爆就跟着爆炸（自爆开始 + 6s），否则落在结算前。
                    float anchor = now;
                    foreach (var a in Acts)
                    {
                        if (a.Kind == ActKind.SelfDestruct)
                        {
                            anchor = a.Window.From + ActTable.CollarToDeadSec;
                            break;
                        }
                    }
                    Add(ActKind.BlackTail, blackId, 0,
                        ActTable.BlackTailOnBomb(anchor, BlackTail), "黑方收尾（结算补登记）");
                }
            }
            catch (Exception ex)
            {
                Plugin.Log.LogWarning($"[HS-Shadow] 结算补登记失败 — {ex.Message}");
            }
        }

        /// <summary>
        /// 收客户端回传的真实磁带 —— **只接我们自己的 key**。
        ///
        /// ⚠ 为什么必须拦在这里，而不是放着不管：
        ///   现有实现的 `TapeHook` 对"不是它登记的 key"一律 `return true` **交还原版**，
        ///   而原版 `HostPacketHandler.Handle_C_TAPE` 会直接 `Broadcast(S_TAPE)` ——
        ///   于是我们索取回来的原始磁带会**广播给所有客户端**、进入它们的 `_playTapes`
        ///   （那就是回放的播放列表）⇒ 用户看到的回放里会凭空多出几段原始录像。
        ///
        /// 两个 Prefix 并存不会打架：Harmony 只在"某个 Prefix 返回 false"时跳过其余 Prefix，
        /// 而本类只在**自己的 key** 上 return false，其余一律 return true 放行给旧实现。
        /// `Priority.First` 是为了先判"是不是我们的 key"。
        /// </summary>
        [HarmonyPatch(typeof(HostPacketHandler), nameof(HostPacketHandler.Handle_C_TAPE))]
        internal static class TapeHook
        {
            [HarmonyPrefix]
            [HarmonyPriority(Priority.First)]
            private static bool Prefix(IPacketSink session, Packet packet)
            {
                if (!(FetchRealTapes?.Value ?? false))
                    return true;
                if (!Armed)
                    return true;

                var c = packet?.Pkt as C_TAPE;
                if (c == null)
                    return true;

                int recorderId = ResolveRecorder(session);
                if (recorderId <= 0)
                    return true;

                return !ReplayDirector.OnTape(recorderId, c.RecordTime, c.SnapShots.ToList());
            }

            /// <summary>包处理器只给到 session，用"哪个玩家的 Session 就是它"反查 id。</summary>
            private static int ResolveRecorder(IPacketSink session)
            {
                var room = GameRoom.Instance;
                if (room == null || session == null)
                    return 0;
                foreach (var p in room.Players)
                {
                    if (p?.PublicInfo != null && ReferenceEquals(p.Session, session))
                        return p.PublicInfo.PlayerId;
                }
                return 0;
            }
        }

        // ── 输出 ────────────────────────────────────────────────────────

        private static void Report()
        {
            try
            {
                int white = 0;
                var room = GameRoom.Instance;
                if (room != null)
                {
                    foreach (var p in room.Players)
                    {
                        if (p?.PublicInfo == null || p.IsSpectator)
                            continue;
                        if (p.Color == EPlayerColor.Black || p.Color == EPlayerColor.Dark)
                            continue;
                        white++;
                    }
                }
                int need = EndReplayFeature.DeathRatioPercent?.Value ?? 30;
                bool armed = _murderDeaths > 0 && white > 0 && _murderDeaths * 100 > white * need;

                Plugin.Log.LogInfo($"[HS-Shadow] ════ 影子幕表（{_ending}）：{Acts.Count} 幕，"
                    + $"刀杀 {_murderDeaths}/{white} 人（阈值 {need}% ⇒ {(armed ? "会播" : "不会播")}）════");
                Plugin.Log.LogInfo($"[HS-Shadow] 采样：{HostRecorder.Stats()}");

                // 单幕详情（含剪影与帧数）由 ReplayDirector 的装配报告给出，这里只列幕序。
                foreach (var a in Acts)
                    Plugin.Log.LogInfo($"[HS-Shadow]   幕 {a.Key,-3} {ActTable.Name(a.Kind),-4} 窗口={a.Window} "
                        + $"主角=#{a.SubjectId,-2} 录制者=#{a.RecorderId} "
                        + $"来源={(CanRecord(a.RecorderId) ? "客户端磁带" : (a.RecorderId > 0 ? "服务端合成（该录制者无客户端）" : "服务端合成"))}  {a.Note}");

                if (Acts.Count == 0)
                    Plugin.Log.LogInfo("[HS-Shadow]   （这一幕是空的）");
            }
            catch (Exception ex)
            {
                Plugin.Log.LogWarning($"[HS-Shadow] 打印幕表失败 — {ex.Message}");
            }
        }

        // ── 小工具 ──────────────────────────────────────────────────────

        /// <summary>
        /// 这个人能不能**真的**提供磁带 —— 只有有客户端（`Session` 非空）的玩家才能。
        /// 假人没有客户端，`S_RECORD_REPLAY` 发过去没人接 ⇒ 只能走服务端合成。
        /// （旧口径只看 `RecorderId &gt; 0`，会把假人也标成"客户端优先"，误导排查。）
        /// </summary>
        private static bool CanRecord(int id)
        {
            if (id <= 0)
                return false;
            var p = FindPlayer(GameRoom.Instance, id);
            return p?.Session != null;
        }

        private static List<int> AliveWhites(GameRoom room)
        {
            var list = new List<int>();
            try
            {
                int blackId = FindBlackId(room);
                foreach (var p in room.AlivePlayers)
                {
                    int id = p?.PublicInfo?.PlayerId ?? 0;
                    if (id <= 0 || id == blackId || p.IsSpectator)
                        continue;
                    list.Add(id);
                }
            }
            catch (Exception ex)
            {
                Plugin.Log.LogWarning($"[HS-Shadow] 统计存活白方失败 — {ex.Message}");
            }
            return list;
        }

        private static int FindBlackId(GameRoom room)
        {
            foreach (var p in room.Players)
            {
                if (p?.PublicInfo != null && (p.Color == EPlayerColor.Black || p.Color == EPlayerColor.Dark))
                    return p.PublicInfo.PlayerId;
            }
            foreach (var p in room.DeadPlayers)
            {
                if (p?.PublicInfo != null && (p.Color == EPlayerColor.Black || p.Color == EPlayerColor.Dark))
                    return p.PublicInfo.PlayerId;
            }
            return 0;
        }

        private static GamePlayer FindPlayer(GameRoom room, int id)
        {
            foreach (var p in room.Players)
            {
                if (p?.PublicInfo != null && p.PublicInfo.PlayerId == id)
                    return p;
            }
            foreach (var p in room.DeadPlayers)
            {
                if (p?.PublicInfo != null && p.PublicInfo.PlayerId == id)
                    return p;
            }
            return null;
        }
    }
}
