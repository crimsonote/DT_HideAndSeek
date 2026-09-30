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
    /// 对局结束时播放「真相公开」回放（重写版）。
    ///
    /// 这一版与旧实现的关系：**功能等价、结构重做**。旧实现（`Features/Rule/EndReplayFeature.cs`）
    /// 里那些"同一个概念实现多份"的地方，在这里各自只剩一处：
    ///
    ///   窗口        <see cref="ReplayWindow"/>       —— 传**绝对时间区间**，不再传"相对事件的前后秒数"
    ///   幕表        <see cref="ActTable"/>           —— 每幕的事件时刻与窗口规则集中在此
    ///   出带        <see cref="TapeAssembler"/>      —— 三条素材路径共用同一个出口
    ///   剪影        <see cref="SilhouetteResolver"/> —— 合并旧实现的两套判据
    ///   采样/合成   <see cref="HostRecorder"/> / <see cref="HostSynth"/>
    ///   编排        <see cref="ReplayDirector"/>     —— 显式阶段状态机
    ///
    /// ★【纯服务端】本模块**只 patch 服务端类型**。旧实现里有 8 处补丁打在客户端类型上
    ///   （`RecordManager` / `UI_TrialEvent` / `PacketHandler` / 客户端 `Player` / `UI_GameScene`），
    ///   而它们**只在房主自己的进程里生效** ⇒ 房主看到的画面 ≠ 别人看到的画面，
    ///   而且会掩盖服务端方案的不完善。这一版全部去掉；服务端确实做不到的写进已知限制。
    ///
    /// 配置键名与旧实现**逐一相同**，所以升级用户的 `.cfg` 定制照旧生效（段名也仍是 `EndReplay`）。
    /// </summary>
    [PatchFeature(
        section: "EndReplay",
        description: "对局结束时（刀杀死亡人数超过白方一定比例）在结算前为所有人播放回放：黑方拿刀、每次刀杀、最后时段、以及自爆/白胜巡礼。",
        defaultEnabled: true,
        side: FeatureSide.Host)]
    internal static class ReplayFeature
    {
        // ── 配置（键名与旧实现一致，不要在阶段 6 之后改名）────────────────

        [ConfigField(true, "对局结束且达到阈值时播放回放。关掉即完全不播（结算照常）。" +
            "本项是热开关：出刀瞬间就会读它来决定要不要请客户端录「拿刀」片段，所以对局中途才打开可能缺这一片段。")]
        public static ConfigEntry<bool> PlayOnEnd;

        [ConfigField(30, "触发阈值：本局因刀杀死亡的人数超过白方人数的百分之几才播放回放。" +
            "白方口径 = 非观战且非黑方的玩家（含已死者）；不计项圈自爆与审判处决。0 = 只要有 1 人被杀就播。",
            Min = 0f, Max = 200f)]
        public static ConfigEntry<int> DeathRatioPercent;

        [ConfigField(1f, "「黑方拿刀」片段：事件前秒数。", Min = 0f, Max = 30f)]
        public static ConfigEntry<float> KnifeBeforeSec;

        [ConfigField(1f, "「黑方拿刀」片段：事件后秒数。", Min = 0f, Max = 30f)]
        public static ConfigEntry<float> KnifeAfterSec;

        [ConfigField(3f, "「黑方杀人」片段：事件前秒数。", Min = 0f, Max = 30f)]
        public static ConfigEntry<float> KillBeforeSec;

        [ConfigField(1f, "「黑方杀人」片段：事件后秒数。", Min = 0f, Max = 30f)]
        public static ConfigEntry<float> KillAfterSec;

        [ConfigField(3f, "「最后时段」片段（每个存活者各录一段自己的视角）：事件前秒数。", Min = 0f, Max = 30f)]
        public static ConfigEntry<float> EndBeforeSec;

        [ConfigField(1f, "「最后时段」片段：事件后秒数。录制发生在结算被拦下的那一刻，之后场上已冻结。",
            Min = 0f, Max = 30f)]
        public static ConfigEntry<float> EndAfterSec;

        [ConfigField(0f, "「自爆」各段：**自爆开始之前**额外保留的秒数（0 ＝ 严格从自爆开始那一刻起裁）。" +
            "整幕窗口 = [自爆开始 − 本项, 自爆开始 + 6s + 后秒数]。", Min = 0f, Max = 30f)]
        public static ConfigEntry<float> SelfDestructBeforeSec;

        [ConfigField(0.5f, "「自爆」各段：**爆炸之后**保留的秒数（爆炸 = 自爆开始后 6s，人消失那一刻）。",
            Min = 0f, Max = 30f)]
        public static ConfigEntry<float> SelfDestructAfterSec;

        [ConfigField(3f, "「黑方收尾」幕：镜头落到黑方之后停留的秒数（**另起一幕**）。" +
            "黑胜时窗口以爆炸为中心；白胜时窗口结束于胜负判定那一刻。上限约 7s —— " +
            "原版 `GameOver` 末尾是 `PushAfter(7500, ChangeGameState(TotalResult))`，爆炸后只有这么多素材。",
            Min = 0f, Max = 7f)]
        public static ConfigEntry<float> BlackTailSec;

        [ConfigField(false, "「自爆」瞬间放一次**全屏压暗**（客户端 `BlackOutVfx`）。" +
            "⚠ **默认关**：原版的压暗是「开 → DoActionAfter(2f) → 关」，服务端没有「立刻取消」的接口，" +
            "而下一幕「黑方收尾」是断电视野 ⇒ 两者叠加会完全看不清。", Min = 0f, Max = 0f)]
        public static ConfigEntry<bool> BombBlackout;

        [ConfigField(2f, "「白方各段」：白胜结局里幸存者那段的**判定前**秒数。" +
            "每段都取**同一段时间**（判定前本项 ~ 判定后后项），只是视角不同、依次播放。" +
            "⚠ 总时长 = 幸存者人数 × (本项 + 后项)。", Min = 0f, Max = 30f)]
        public static ConfigEntry<float> TourBeforeSec;

        [ConfigField(0.5f, "「白方各段」：判定后秒数（与上一项一起构成每段的时间段）。", Min = 0f, Max = 30f)]
        public static ConfigEntry<float> TourAfterSec;

        [ConfigField(12, "最多播放几段。段数越多回放越长。", Min = 1f, Max = 40f)]
        public static ConfigEntry<int> MaxClips;

        [ConfigField(2500, "向客户端索取磁带后等多久开始播放（毫秒）。客户端收到请求即回包，给一点余量即可。",
            Min = 200f, Max = 20000f)]
        public static ConfigEntry<int> TapeWaitMs;

        [ConfigField(5f, "超时兜底用的单段估算秒数（秒/段）。正常情况下客户端的完成回执会提前结束等待。",
            Min = 1f, Max = 60f)]
        public static ConfigEntry<float> SecondsPerClipEstimate;

        [ConfigField(7000, "收掉加载页并让开场字幕开演后，等多久再推 Replay（毫秒）。" +
            "加载页由 S_FADE_IN 立刻收掉，所以这里只需等入场演出/开场字幕自己播完；等太短会把字幕序列 Kill 掉、" +
            "文字被冻在画面上（实测踩过）。", Min = 0f, Max = 60000f)]
        public static ConfigEntry<int> TrialIntroWaitMs;

        [ConfigField(true, "回放里把黑方的昵称显示成红色，让观众一眼看出「黑刀＝黑幕」。" +
            "做法：推回放前广播一包 `S_NOTIFY_BLACK{ PlayerId = 黑方 }` —— 这是**服务端广播**，对所有人生效。" +
            "返回大厅时客户端自己会清空，不会带到下一局。")]
        public static ConfigEntry<bool> RevealBlackName;

        [ConfigField(true, "【没收到磁带时】补一段「占位磁带」，让客户端走一遍「这段没录到」的转场，" +
            "而不是让这一幕从回放里凭空消失。（服务端合成的那几幕例外，见代码注释。）")]
        public static ConfigEntry<bool> PlaceholderTape;

        [ConfigField(true, "【排查用】把客户端回传的**真实磁带**落成文本文件（插件目录 tapedump/ 下，" +
            "每局一个子目录、保留最近 8 局）。只写文件，不改游戏状态、不发包 —— 关掉纯粹为了不占磁盘。")]
        public static ConfigEntry<bool> DumpTapes;

        // ── 状态（**一处**便能看清走到哪了）────────────────────────────

        private static readonly List<Act> Acts = new List<Act>();

        /// <summary>受害者"最后一次被击中"的时刻 —— `OnDamaged` 记、`KillHook` 取。</summary>
        private static readonly Dictionary<int, float> LastHitAt = new Dictionary<int, float>();

        private static int _nextKey;
        private static int _murderDeaths;
        private static string _ending = "结算前";

        private static bool Armed
            => !ModeRuntime.Bypass && (PlayOnEnd?.Value ?? true) && Diagnostics.IsLoaded("EndReplay");

        private static float Now()
        {
            try { return Managers.Game.ClientTime; }
            catch { return 0f; }
        }

        private static void Reset()
        {
            Acts.Clear();
            LastHitAt.Clear();
            _nextKey = 0;
            _murderDeaths = 0;
            _ending = "结算前";
            HostRecorder.Clear();
            ReplayDirector.Reset();
        }

        private static Act Add(ActKind kind, int subjectId, int recorderId, ReplayWindow.Span window, string note)
        {
            var act = new Act
            {
                Kind = kind,
                Key = _nextKey++,
                SubjectId = subjectId,
                RecorderId = recorderId,
                Window = window,
                Note = note,
            };
            Acts.Add(act);

            // ⚠ 这里**不**发 `S_RECORD_REPLAY`：key 会在排片时按事件顺序重新分配，
            //   所以统一在"索取之前"发一次即可（见 `ReplayDirector.FetchTapes`）。
            //   客户端只关心"这个 key 有没有被登记过"，与登记的时刻无关。
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

        // ── 钩子（全部 Postfix / 只读，避免与别的补丁抢控制流）──────────

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

        /// <summary>【采样点】`Player.Move` —— 服务端每次收到 `C_MOVE` 后调用它，频率与客户端录制同源。</summary>
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

        /// <summary>1Hz 兜底采样：假人不主动发包，站桩玩家也不发包，这一路保证"每个人至少被记到一次"。</summary>
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

        [HarmonyPatch(typeof(GamePlayer), "DelayAcquireWeapon")]
        internal static class KnifeHook
        {
            [HarmonyPostfix]
            private static void Postfix(GamePlayer __instance)
            {
                if (!Armed)
                    return;
                if (WeaponGrantFeature.GiveAtStart?.Value ?? false)
                    return;
                if (__instance?.PublicInfo == null)
                    return;
                if (__instance.Color != EPlayerColor.Black && __instance.Color != EPlayerColor.Dark)
                    return;

                int id = __instance.PublicInfo.PlayerId;
                Add(ActKind.Knife, id, id,
                    ActTable.Plain(Now(), KnifeBeforeSec?.Value ?? 1f, KnifeAfterSec?.Value ?? 1f), "拿刀");
            }
        }

        /// <summary>
        /// 【事件时刻修正】记下"谁在什么时候被击中"。
        /// `OnDead` 的时刻**不等于**击杀时刻（实测差 1.37 秒），而原版是在 `OnDamaged` 里
        /// 设 `attacker.RecordTime` 并请客户端 `ReserveSaveTape` 的。
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
                float at = LastHitAt.TryGetValue(victim, out var hitAt) ? hitAt : Now();
                LastHitAt.Remove(victim);
                Add(ActKind.Kill, id, id,
                    ActTable.Plain(at, KillBeforeSec?.Value ?? 3f, KillAfterSec?.Value ?? 1f), $"#{id} → #{victim}");
            }
        }

        /// <summary>
        /// 「自爆」—— `OnDeadCollarBomb` 只是**开始**自爆（真正死亡在 6 秒后）。
        /// 它对每个存活白方各调一次 ⇒ 用 <see cref="HasKind"/> 保证只登记一次。
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
                var window = ActTable.SelfDestruct(t0,
                    SelfDestructBeforeSec?.Value ?? 0f, SelfDestructAfterSec?.Value ?? 0.5f);
                int seq = 0;
                foreach (int id in subjects)
                {
                    if (Acts.Count >= (MaxClips?.Value ?? 12))
                        break;
                    var seg = ReplayWindow.Tile(seq, subjects.Count, window, 1.5f);
                    var p = FindPlayer(room, id);
                    Add(ActKind.SelfDestruct, id, p?.Session != null ? id : 0, seg, $"被处决者 #{id}");
                    seq++;
                }

                int blackId = FindBlackId(room);
                if (blackId > 0 && Acts.Count < (MaxClips?.Value ?? 12))
                {
                    float tail = Math.Max(0f, BlackTailSec?.Value ?? 3f);
                    Add(ActKind.BlackTail, blackId, 0,
                        ActTable.BlackTailOnBomb(t0 + ActTable.CollarToDeadSec, tail), "黑方收尾（与爆炸同时）");
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
                float tail = Math.Max(0f, BlackTailSec?.Value ?? 3f);
                var window = ActTable.Plain(Now(),
                    TourBeforeSec?.Value ?? 2f, TourAfterSec?.Value ?? 0.5f);

                foreach (int id in AliveWhites(room))
                {
                    if (Acts.Count >= (MaxClips?.Value ?? 12))
                        break;
                    var p = FindPlayer(room, id);
                    Add(ActKind.Tour, id, p?.Session != null ? id : 0, window, $"幸存者 #{id}");
                }

                if (blackId > 0 && Acts.Count < (MaxClips?.Value ?? 12))
                    Add(ActKind.BlackTail, blackId, 0,
                        ActTable.BlackTailBeforeDecision(Now(), tail), "黑方收尾（白胜）");
            }
        }

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
        /// 【结算的唯一入口】拦下 `TotalResult`，先播回放，播完再放行。
        ///
        /// 为什么拦这里（而不是 `GameOver`）：原版收尾演出（含对存活白方的项圈自爆）都在
        /// `GameOver` 内部跑完，然后才 `PushAfter(7500, ChangeGameState(TotalResult))`
        /// ⇒ 要在回放里播"自爆"，就必须等它演完；而结算状态切换只有三个调用点，
        /// **全都走公开的 `ChangeGameState(EGameState)`** ⇒ 一个钩子全覆盖。
        /// </summary>
        [HarmonyPatch(typeof(GameRoom), nameof(GameRoom.ChangeGameState), new[] { typeof(EGameState) })]
        internal static class SettleHook
        {
            [HarmonyPrefix]
            private static bool Prefix(EGameState state)
            {
                if (state != EGameState.TotalResult)
                    return true;

                return !ReplayDirector.OnSettlementRequested(Acts, _ending, _murderDeaths,
                    CountWhite(GameRoom.Instance), () => ContinueSettlement());
            }
        }

        /// <summary>回放结束后继续结算：调用原版 `ChangeGameState(EGameState.TotalResult)`。</summary>
        private static void ContinueSettlement()
        {
            try
            {
                var room = GameRoom.Instance;
                var m = AccessTools.Method(typeof(GameRoom), "ChangeGameState", new[] { typeof(EGameState) });
                if (room == null || m == null)
                {
                    Plugin.Log.LogWarning("[HS-Replay] 找不到 GameRoom.ChangeGameState(EGameState)，回放后无法继续结算。");
                    return;
                }
                Plugin.Log.LogInfo("[HS-Replay] 回放结束，继续结算。");
                m.Invoke(room, new object[] { EGameState.TotalResult });
            }
            catch (Exception ex)
            {
                Plugin.Log.LogWarning($"[HS-Replay] 回放后进入结算失败：{ex.Message}");
            }
        }

        /// <summary>
        /// 收客户端回传的磁带 —— **只接我们自己登记的 key**，装配后由 `ReplayDirector` 统一广播。
        ///
        /// ⚠ 这个钩子**必须有**：没有它，客户端回的 `C_TAPE` 会落到原版的
        /// `HostPacketHandler.Handle_C_TAPE`，而它直接 `Broadcast(S_TAPE)` ——
        /// 原始**未裁剪**的磁带会进入所有客户端的 `_playTapes`（那就是回放的播放列表）
        /// ⇒ 幕序被打乱、回放被拉长。实测症状：索取 5 段，客户端凭空多出 5 段
        /// （`可用磁带 8 段` vs 我们广播的 5 段）。
        /// </summary>
        [HarmonyPatch(typeof(HostPacketHandler), nameof(HostPacketHandler.Handle_C_TAPE))]
        internal static class TapeHook
        {
            [HarmonyPrefix]
            private static bool Prefix(IPacketSink session, Packet packet)
            {
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

        // ── 结算类幕（事件时刻 = "结算被拦下"这一刻）────────────────────

        internal static void RegisterSettlementActs()
        {
            try
            {
                var room = GameRoom.Instance;
                if (room == null || HasKind(ActKind.Tour) || HasKind(ActKind.SelfDestruct))
                    return;   // 白胜/自爆那两路已经在各自钩子里排过片

                float now = Now();
                int max = MaxClips?.Value ?? 12;
                int seq = 0;
                foreach (int id in AliveWhites(room))
                {
                    if (Acts.Count >= max)
                        break;
                    var p = FindPlayer(room, id);
                    Add(ActKind.Final, id, p?.Session != null ? id : 0,
                        ActTable.Plain(now, EndBeforeSec?.Value ?? 3f, EndAfterSec?.Value ?? 1f), $"存活者 #{id}");
                    seq++;
                }
                if (seq > 0)
                    Plugin.Log.LogInfo($"[HS-Replay] 结算补登记「最后时段」{seq} 段（存活白方各一段、同一段时间）。");
            }
            catch (Exception ex)
            {
                Plugin.Log.LogWarning($"[HS-Replay] 结算补登记失败 — {ex.Message}");
            }
        }

        internal static string Ending => _ending;

        // ── 小工具 ──────────────────────────────────────────────────────

        private static int CountWhite(GameRoom room)
        {
            int n = 0;
            if (room == null)
                return 0;
            foreach (var p in room.Players)
            {
                if (p?.PublicInfo == null || p.IsSpectator)
                    continue;
                if (p.Color == EPlayerColor.Black || p.Color == EPlayerColor.Dark)
                    continue;
                n++;
            }
            return n;
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
                Plugin.Log.LogWarning($"[HS-Replay] 统计存活白方失败 — {ex.Message}");
            }
            return list;
        }

        private static int FindBlackId(GameRoom room)
        {
            if (room == null)
                return 0;
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
            if (room == null || id <= 0)
                return null;
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