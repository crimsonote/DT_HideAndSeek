using System;
using System.Collections.Generic;
using System.Linq;
using BepInEx.Configuration;
using DummyClient;
using Google.Protobuf;
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

        [ConfigField(2f, "「黑方拿刀」片段：事件后秒数。", Min = 0f, Max = 30f)]
        public static ConfigEntry<float> KnifeAfterSec;

        [ConfigField(2f, "「黑方杀人」片段：事件前秒数。", Min = 0f, Max = 30f)]
        public static ConfigEntry<float> KillBeforeSec;

        [ConfigField(2.2f, "「黑方杀人」片段：事件后秒数。", Min = 0f, Max = 30f)]
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
            "而下一幕「黑方收尾」是断电视野 ⇒ 两者叠加会完全看不清。")]
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

        [ConfigField(0, "收掉加载页后，等多久再把客户端切进 Replay 相位（毫秒）。默认 0 ＝ 立刻切。" +
            "⚠ 这里**不要**再等'开场字幕'：那段字幕由 `Discuss` 相位的 `StartFirstTalk()` 启动，" +
            "而回放从头到尾**不发 Discuss** ⇒ 它根本不会播。而客户端被拉进 Trial 后、" +
            "收到第一个 `S_TRIAL_STATE` 之前的这段时间，`UI_TrialEvent` 是没有相位的" +
            "（界面文字在 `Init()` 里就设好了，只有相位才驱动切换）⇒ 等得越久，" +
            "画面上越久停留在一张'没按相位初始化'的界面（实测表现为：标题写着『投票结果』、" +
            "中间却是另一条烘死的韩文）。默认值曾是 7000，理由是'避免 Kill 掉开场字幕'" +
            "—— 那个理由基于对 `StartReplay()` 的错误假设（它只做 `WaitCompletePacket`，没有 ResetSlideVisual）。",
            Min = 0f, Max = 60000f)]
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

        /// <summary>
        /// 受害者的"被击记录"：**事件时刻** + 那一刻就分配给它的 **key**。
        ///
        /// ★ 为什么 key 必须在这里分配（而不是等结算时排片）：
        ///   客户端收到 `S_RECORD_REPLAY{key}` 后会在 **9 秒后**把"当时的录制缓冲"拍成快照存进
        ///   `RecordingTape[key]`，而那份快照覆盖 `[T+9-14, T+9]` ⇒ 事件的窗口 `[T-3, T+1]` 完整落在里面。
        ///   ⇒ 之后无论多久（结算是几分钟后）再索取，都能拿回**事件周围的素材**。
        ///   ⚠ 反过来做（结算时才发 `S_RECORD_REPLAY`）只会拿到"当前 14 秒缓冲"
        ///     ⇒ 早期事件早就滚出去了 —— 实测症状："3 次刀杀只播了 1 次"。
        /// </summary>
        private sealed class Hit
        {
            public float Time;
            public int Key;
            /// <summary>受害者 —— 他会作为"事件参与者"被强制放进本幕的画面名单。</summary>
            public int Victim;
        }

        private static readonly Dictionary<int, Hit> PendingHit = new Dictionary<int, Hit>();

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
            PendingHit.Clear();
            _nextKey = 0;
            _murderDeaths = 0;
            _ending = "结算前";
            HostRecorder.Clear();
            ReplayDirector.Reset();
        }

        /// <summary>用**指定的 key** 登记一幕（key 已在事件发生时分配并告知客户端）。</summary>
        private static Act AddWithKey(int key, ActKind kind, int subjectId, int recorderId,
            ReplayWindow.Span window, string note)
        {
            var act = new Act
            {
                Kind = kind,
                Key = key,
                SubjectId = subjectId,
                RecorderId = recorderId,
                Window = window,
                Note = note,
            };
            Acts.Add(act);

            // ★ **事件发生时开一份采样快照**（客户端 `ReserveSaveTape` 的服务端对应物）。
            //   窗口此刻已确定，快照从这里开始累积 `[事件−before, 事件+after]`，`To` 之后封存，
            //   此后**不受主缓冲裁剪影响** —— 结算时（可能几分钟后）仍拿得到这一幕的素材。
            //   ⚠ 本步只积累，`Range`/`Trim` 尚未改用它，所以现有行为不变。
            HostRecorder.BeginSnap(key, window.From, window.To);

            return act;
        }

        /// <summary>
        /// 登记一幕，并在**此刻**（= 事件时刻）把新 key 告知录制者客户端。
        ///
        /// ★ 时机是这套机制的关键：客户端 `ReserveSaveTape` 会在 9 秒后把"当时的录制缓冲"
        ///   拍成快照并**长期保存**，所以结算时（可能已是几分钟后）索取仍能拿回事件周围的素材。
        ///   如果拖到索取前才发，只会拿到"当前 14 秒缓冲" ⇒ 早期事件已经滚出缓冲。
        /// </summary>
        /// <summary>
        /// 把"新 key"告知**录制者客户端** —— 客户端靠它在 9 秒后把"事件周围的缓冲"
        /// 拍成**持久快照**（`ReserveSaveTape`），结算时（可能几分钟后）索取才拿得回素材。
        ///
        /// ★ **只有这一处发**。曾经散在两处（`HitHook` 与 `Add`），于是诊断只加进了一处、
        ///   另一处发失败时静默跳过 —— 排查时"日志里什么都没有"反而把人引偏。
        ///   现在两处都调这里，成功/失败都留痕，便于和客户端的
        ///   `S_REQUEST_TAPE: no tape for RecordTime=N. Sending empty tape.` 对账。
        /// </summary>
        private static void NotifyRecorder(int key, int recorderId, string what)
        {
            var p = FindPlayer(GameRoom.Instance, recorderId);
            if (p?.Session != null)
            {
                p.Session.Send(new S_RECORD_REPLAY { RecordTime = key });
                Plugin.Log.LogInfo($"[HS-Replay/诊断] key={key} 已发 S_RECORD_REPLAY → 录制者 #{recorderId}（{what}）");
                return;
            }

            Plugin.Log.LogWarning($"[HS-Replay/诊断] ⚠ key={key} **未能发出 S_RECORD_REPLAY**"
                + $"（{what}，recorderId=#{recorderId}："
                + (p == null ? "找不到该玩家" : "他的 Session 为空（已掉线/假人/已退出）")
                + "）⇒ 客户端不会有这一幕的快照，结算时只能走服务端合成。");
        }

        private static Act Add(ActKind kind, int subjectId, int recorderId, ReplayWindow.Span window, string note)
        {
            int key = _nextKey++;
            var act = AddWithKey(key, kind, subjectId, recorderId, window, note);

            NotifyRecorder(key, recorderId, $"{ActTable.Name(kind)} 窗口={window}");

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
                if (state != EGameState.Survive)
                    return;

                Reset();

                // 每局开一个新的 dump 目录（只写文件，零行为影响）。
                // ⚠ 这一句在阶段 6 重写本文件时漏掉过一次 —— 症状是"新一局没有 dump 文件"，
                //   而因为没有日志，看不出是漏了调用还是没收到磁带。
                if (DumpTapes?.Value ?? false)
                    TapeDump.BeginRound();
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


        /// <summary>刀的物品 id（`ItemInfo.DataId`，客户端到处用它判断"手里是不是刀"）。</summary>
        private const int KnifeItemId = 4001;

        /// <summary>
        /// 「拿刀」幕 —— 事件时刻取自 `DelayAcquireWeapon`。
        ///
        /// ★ **挂点是正确的，之前换掉它是错的**（2026-10 实测推翻）：
        ///   从那一局的 dump 算出"主角与刀架的实际距离"：
        ///       刀架（Armory，deviceId=10082）= (8366, 4964)
        ///       窗口起点 t=70.113 时主角 = (8471, 4839) ⇒ 距刀架 **163**
        ///       t=70.328 = (8378, 4898) ⇒ 距 **67**；t=70.405 起 = (8378, 4925) ⇒ 距 **41**（停在刀架旁）
        ///   ⇒ 窗口**确实**覆盖了"走到刀架拿刀"的全过程 ⇒ 事件时刻没问题。
        ///
        ///   而当时真正坏掉的是**首帧位置**：房主采样给的是 (8149, 3563)，**距刀架 1418**
        ///   ⇒ 角色被放在 1418 单位外，再由 `MoveShot` 直线匀速拽到刀架旁 ⇒ 用户看到的"很飘"。
        ///   ⇒ 修的是位置来源（见 `TapeAnchorPos`），**不是这个挂点**。
        ///
        /// `DelayAcquireWeapon` 在 `Player.Color` 的 setter 里 `value == Black` 分支被调
        /// （实现只是 `StartWeaponCooltime(5)`），时机是"黑方身份公开"前后 ——
        /// 而实测它落在"主角已经站在刀架旁"之后约 0.7 秒 ⇒ 作为"拿刀"事件时刻足够近。
        /// </summary>
        [HarmonyPatch(typeof(GamePlayer), "DelayAcquireWeapon")]
        internal static class KnifeHook
        {
            [HarmonyPostfix]
            private static void Postfix(GamePlayer __instance)
            {
                if (!Armed)
                    return;
                if (WeaponGrantFeature.GiveAtStart?.Value ?? false)
                    return;                       // 开局直接给刀 ⇒ 没有"跑刀"过程 ⇒ 这一幕没内容
                if (__instance?.PublicInfo == null)
                    return;
                if (__instance.Color != EPlayerColor.Black && __instance.Color != EPlayerColor.Dark)
                    return;

                int id = __instance.PublicInfo.PlayerId;
                Add(ActKind.Knife, id, id,
                    ActTable.Plain(Now(), KnifeBeforeSec?.Value ?? 1f, KnifeAfterSec?.Value ?? 2f), "拿刀");
            }
        }


        /// <summary>探针日志：把两个候选挂点的时间戳并列打印，便于对比。</summary>
        private static void Probe(string where, int playerId, int value)
        {
            try
            {
                float t = Now();
                Plugin.Log.LogInfo($"[HS-Replay/探针] 拿刀候选 {where} → t={t:F2} 玩家=#{playerId}"
                    + (value != 0 ? $" value={value}" : "")
                    + "（本局生存时间；两行对比即知哪个是真正走到刀架那一刻）");
            }
            catch
            {
                // 探针不该影响任何东西
            }
        }

        /// <summary>
        /// 【事件时刻 + key 分配】`OnDamaged` 就是击杀发生的那一刻（原版也是在这里设
        /// `attacker.RecordTime` 并请客户端 `ReserveSaveTape` 的；而 `OnDead` 实测晚 1.37 秒）。
        ///
        /// ⇒ 在这里做三件事：记时刻、分配 key、**立刻把 key 告知凶手客户端**。
        ///   第三件是关键：客户端 9 秒后会把当时的缓冲拍成**持久快照**，
        ///   结算时（几分钟后）索取仍能拿回事件周围的素材。
        /// </summary>
        /// <summary>
        /// 登记"一次杀人事件的时刻与 key"，并**立刻把 key 告知录制者客户端**。
        ///
        /// ★ 为什么要有这个共用方法：**刀杀有两条路径**，只有一条经过 `OnDamaged`：
        ///   · 普通刀杀：`UseWeapon` → `OnDamaged`（其内部再调 `OnDead`）—— 由 `HitHook` 走这里；
        ///   · **DT 刀杀**：`ExecuteCabinet/Pond/OccultTrick` → `StartDeadlyTrickCommon` → `OnDead`
        ///     —— **完全不经过 `OnDamaged`**，所以 `HitHook` 对它无效。
        ///   实测症状：一局里 2 幕杀人**全是 DT** ⇒ `HitHook` 一次都没跑 ⇒ 客户端手里只有
        ///   原版自己发的那份快照（key = `SurviveTime`），而我们索取时用的是 `_nextKey`
        ///   ⇒ `no tape for RecordTime=0` ⇒ 全部降级成服务端合成。
        ///
        /// ⇒ **key 一律由 mod 分配**，不用原版的 `RecordTime` —— 这样"每个事件点由 mod 登记、
        ///   结算时用同一个 key 索取"是唯一规则，不依赖原版实现细节（原版也会发它自己那份，
        ///   多存一份无害）。这也是"独立性与解释性"的取舍：宁可多一份快照，也不要两套 key 混用。
        /// </summary>
        private static void NoteHit(int victim, int attackerId, string what)
        {
            if (victim <= 0 || attackerId <= 0)
                return;
            if (PendingHit.ContainsKey(victim))
                return;                       // 同一受害者只记第一次（多段伤害会重复触发）

            int key = _nextKey++;
            PendingHit[victim] = new Hit { Time = Now(), Key = key, Victim = victim };
            NotifyRecorder(key, attackerId, what);
        }

        /// <summary>
        /// 「普通刀杀」—— `UseWeapon` → `OnDamaged`。它内部会调 `OnDead`，所以这一刻就是事件时刻。
        /// </summary>
        [HarmonyPatch(typeof(GamePlayer), "OnDamaged")]
        internal static class HitHook
        {
            [HarmonyPostfix]
            private static void Postfix(GamePlayer __instance, GamePlayer attacker)
            {
                if (!Armed)
                    return;
                if (attacker?.PublicInfo == null)
                    return;
                int victim = __instance?.PublicInfo?.PlayerId ?? 0;
                NoteHit(victim, attacker.PublicInfo.PlayerId,
                    $"普通刀杀 #{attacker.PublicInfo.PlayerId} → #{victim}");
            }
        }

        /// <summary>
        /// 「**DT 刀杀**」—— 它**不经过 `OnDamaged`**，必须单独补登记点。
        ///
        /// 服务端入口是 `Player.UseDeadlyTrick(C_DEADLY_TRICK)`（:176314）：它构造
        /// `S_DEADLY_TRICK` 广播、并走 `StartDeadlyTrickCommon` ⇒ 约 1.8 秒后 `OnDead`。
        /// 而 `OnDead` 正是 `KillHook` 登记幕的地方 ⇒ 只要在这里把 key 备好，
        /// `KillHook` 就能取到 mod 自己那个（否则它会走 `else` 分支另分配一个，
        /// 而客户端手里没有那个 key ⇒ 该幕拿不到磁带）。
        ///
        /// ⚠ **拖尸**（`UseCorpseDeadlyTrick`）不在这里登记：那条路径上人**早就死了**、
        ///   `OnDead` 不会再触发 ⇒ 分配出去的 key 没人消费，只会让 `PendingHit` 留垃圾。
        ///   拖尸的"帧"由 `BroadcastHook` 记 `S_DEADLY_TRICK` 负责，与"幕"无关。
        /// </summary>
        [HarmonyPatch(typeof(GamePlayer), "UseDeadlyTrick", new[] { typeof(C_DEADLY_TRICK) })]
        internal static class DeadlyTrickRecordHook
        {
            [HarmonyPostfix]
            private static void Postfix(GamePlayer __instance, C_DEADLY_TRICK pkt)
            {
                if (!Armed || pkt == null || __instance?.PublicInfo == null)
                    return;
                NoteHit(pkt.TargetId, __instance.PublicInfo.PlayerId,
                    $"DT 刀杀 #{__instance.PublicInfo.PlayerId} → #{pkt.TargetId}");
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

                // ★ 复用 `HitHook` 在**事件时刻**分配并发给客户端的那个 key ——
                //   既保证播放顺序反映事件顺序（不再依赖"登记顺序"，`OnDead` 有延迟），
                //   也保证客户端手里那把 key 与我这里一致（9 秒快照才会落地）。
                int key;
                float at;
                if (PendingHit.TryGetValue(victim, out var hit))
                {
                    key = hit.Key;
                    at = hit.Time;
                    PendingHit.Remove(victim);
                }
                else
                {
                    key = _nextKey++;
                    at = Now();
                }

                var act = AddWithKey(key, ActKind.Kill, id, id,
                    ActTable.Plain(at, KillBeforeSec?.Value ?? 2f, KillAfterSec?.Value ?? 2.2f), $"#{id} → #{victim}");

                // ★ 受害者是**事件参与者**：他要被强制放进本幕的"画面名单"。
                //   roster 只来自磁带里出现过的人，而磁带录的是"录制者当时看得到的人" ——
                //   实测第 2 次刀杀之后 roster 掉到 1~2 人，画面里只剩凶手（对空气挥刀）。
                //   参与者不该依赖这个，位置照样取房主侧采样。
                if (victim > 0)
                    act.Subjects = new List<int> { victim };
            }
        }

        /// <summary>
        /// 记录**服务端广播的两类帧素材** —— 它们都是"客户端磁带里那条帧"的服务端对应物：
        ///
        ///   · `S_PLAY_EFFECT`  ⇒ 客户端录成 `EffectShot`（**出刀闪光**等）
        ///       服务端发它的是 `GameRoom.BroadcastWorldVFX` / `SendVFX`；
        ///       客户端 `Handle_S_PLAY_EFFECT` 只在 `Pos != null` 时录。
        ///   · `S_DEADLY_TRICK` ⇒ 客户端录成 `DeadlyTrickShot`（**DT：藏尸**）
        ///       服务端发它的是 `Player.UseDeadlyTrick`（杀人类）/ `UseCorpseDeadlyTrick`（拖尸类）。
        ///
        /// ★ 为什么拦 `Broadcast` 而不是各自的发送点：
        ///   ① 它只有一个重载（`IMessage`），拦点唯一、不会漏；
        ///   ② 拿到的是**服务端真正发出去的包** —— 拖尸那条路径里 `TargetId` 会被换成 `corpse.ID`
        ///      并带上 `IsCorpse = true`，只有在这里才拿得到客户端真正录下的那份。
        ///
        /// ⚠ `Broadcast` 是通用方法、调用频繁，所以钩子第一件事就是按类型快速判断（`is` 为 O(1)）。
        /// </summary>
        [HarmonyPatch(typeof(GameRoom), "Broadcast", new[] { typeof(IMessage), typeof(int) })]
        internal static class BroadcastHook
        {
            [HarmonyPostfix]
            private static void Postfix(IMessage packet)
            {
                if (!Armed || packet == null)
                    return;

                if (packet is S_PLAY_EFFECT fx)
                {
                    // 与客户端 `Handle_S_PLAY_EFFECT` 的录制条件一致：`Pos == null` 时它不录，我们也不记。
                    HostRecorder.NoteEffect(fx.Type, fx.DeviceId, fx.Pos);
                    return;
                }

                if (packet is S_DEADLY_TRICK dt)
                {
                    HostRecorder.NoteTrick(dt.TrickType, dt.AttackerId, dt.TargetId, dt.DeviceId, dt.IsCorpse);
                }
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
