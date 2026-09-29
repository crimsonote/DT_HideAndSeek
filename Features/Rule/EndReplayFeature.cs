using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using BepInEx.Configuration;
using DummyClient;
using HarmonyLib;
using Protocol;
using Server.Game;
using HideAndSeek.Core;
using HideAndSeek.Features.Weapon;
using GamePlayer = Server.Game.Player;

namespace HideAndSeek.Features.Rule
{
    /// <summary>
    /// 对局结束时播放「真相公开」回放。
    ///
    /// 原版回放是学级裁判里的一个状态（<see cref="ETrialState.Replay"/>）；本模块禁掉了裁判，
    /// 所以它从来没机会播 —— 但磁带一直在录。链路详见 `.tmps/原版回放机制-调查.md`：
    ///
    ///   ① 录：房主在**每次刀杀**后 400ms 向**凶手本人**发 S_RECORD_REPLAY
    ///         （`Server.Game.Player.OnDamaged` :175956），客户端把当前录制缓冲存成一段磁带。
    ///   ② 收：房主发 S_REQUEST_TAPE → 客户端裁剪后回 C_TAPE。
    ///   ③ 广播：`Server.Game.HostPacketHandler.Handle_C_TAPE`（:174890）收到后**原版自动** Broadcast(S_TAPE)。
    ///   ④ 播：`S_TRIAL_STATE{Replay}` → 客户端 `UI_TrialEvent.StartReplay()`（:70368）
    ///         → `RecordManager.Play()`（:31866）。
    ///
    /// 本功能做的四件事：
    ///   · 在关键节点请客户端额外录一段（拿刀 / 最后时段 —— 杀人那一段原版已经在录）；
    ///   · 结算被触发时按阈值决定要不要播，并向各录制者索取磁带；
    ///   · 在磁带广播前按配置的「事件前后秒数」重裁（原版固定前 3 后 5，且只在一次要多段时才裁）；
    ///   · 把房间推进 Trial（客户端只在 Trial 才会创建回放宿主 UI_TrialEvent）后推 Replay，
    ///     等放完（或超时）再继续原本的结算。
    ///
    /// ★ 重裁锚点：客户端 `RecordManager.ReserveSaveTape` 一定会在事件处插入一枚
    ///   `EditShot{NormalTimeEdit}`（:31652）。磁带时间戳用的是各客户端自己的 ClientTime，
    ///   房主无法换算，所以只能靠这枚锚点定位事件。
    ///
    /// ★ 磁带必须**以 SpawnShot 开头**，否则客户端 `BeginTape`（:31905）会整段跳过；
    ///   而按窗口重裁后首帧通常不是 SpawnShot，所以这里合成一枚（时间戳=窗口起点，
    ///   内容=原磁带的 Spawn 信息 —— 客户端只拿它做 ApplySpawn 与镜头跟随，:31925-31927）。
    ///
    /// 假人：假人没有客户端、录不了像（S_RECORD_REPLAY 发过去没有接收方）。但假人**会出现在
    /// 真人录的磁带里**（服务器照常广播它们的移动），所以「假人被杀」的片段是有的；
    /// 只有「假人当黑方」这一种情况拿不到磁带。
    /// </summary>
    [HarmonyPatch]
    [PatchFeature(
        section: "EndReplay",
        description: "对局结束时（刀杀死亡人数超过白方一定比例）在结算前为所有人播放回放：黑方拿刀、每次刀杀、以及最后时段各存活者的行动。",
        defaultEnabled: true,
        side: FeatureSide.Host)]
    internal static class EndReplayFeature
    {
        // ── 配置 ────────────────────────────────────────────────────────
        [ConfigField(true, "对局结束且达到阈值时播放回放。关掉即完全不播（结算照常）。" +
            "本项是热开关：出刀瞬间就会读它来决定要不要请客户端录「拿刀」片段，" +
            "所以对局中途才打开可能缺这一片段（杀人片段不受影响）。")]
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

        [ConfigField(1f, "「最后时段」片段：事件后秒数。录制发生在结算被拦下的那一刻，" +
            "之后场上已冻结，所以「后」实际表现为在静止画面上多留一会儿。", Min = 0f, Max = 30f)]
        public static ConfigEntry<float> EndAfterSec;

        [ConfigField(12, "最多播放几段，取段顺序为 拿刀 → 杀人 → 最后时段。段数越多回放越长。",
            Min = 1f, Max = 40f)]
        public static ConfigEntry<int> MaxClips;

        [ConfigField(2500, "向客户端索取磁带后等多久开始播放（毫秒）。客户端收到请求即回包，给一点余量即可。",
            Min = 200f, Max = 20000f)]
        public static ConfigEntry<int> TapeWaitMs;

        [ConfigField(5f, "超时兜底用的单段估算秒数（秒/段）。" +
            "正常情况下客户端的完成回执会提前结束等待，这个值只在回执丢失时用到。", Min = 1f, Max = 60f)]
        public static ConfigEntry<float> SecondsPerClipEstimate;

        [ConfigField(5000, "客户端审判 UI 建好后，等多久再推 Replay（毫秒）。" +
            "开场文字由 SlidingText 序列在约 3.0 秒后自己 SetActive(false) 隐藏；" +
            "推得太早会 Kill 掉该序列、把文字永久冻在画面上（实测踩过）。5 秒留足余量。",
            Min = 0f, Max = 30000f)]
        public static ConfigEntry<int> TrialIntroWaitMs;

        // ── 片段登记 ────────────────────────────────────────────────────
        private sealed class Clip
        {
            public int Key;          // RecordTime（请求磁带时用的键）
            public int RecorderId;   // 谁录的（谁收到 S_RECORD_REPLAY）
            public string Kind;      // 拿刀 / 杀人 / 最后
            public float Before;
            public float After;
        }

        private static readonly List<Clip> Clips = new List<Clip>();
        /// <summary>按 (录制者, RecordTime) 索引 —— 同一时刻多个玩家各录一段时键会相同，不能只用 key。</summary>
        private static readonly Dictionary<long, Clip> BySlot = new Dictionary<long, Clip>();
        private static int _murderDeaths;
        private static bool _played;
        private static bool _inReplay;
        private static Action _continueSettlement;

        private static long Slot(int playerId, int key) => ((long)playerId << 32) | (uint)key;

        internal static void ResetRound()
        {
            Clips.Clear();
            BySlot.Clear();
            _murderDeaths = 0;
            _played = false;
            _inReplay = false;
            _continueSettlement = null;
        }

        private static bool AddClip(int key, int recorderId, string kind, float before, float after)
        {
            if (key == 0 || recorderId == 0)
                return false;

            long slot = Slot(recorderId, key);
            if (BySlot.ContainsKey(slot))
                return false;

            var clip = new Clip { Key = key, RecorderId = recorderId, Kind = kind, Before = before, After = after };
            Clips.Add(clip);
            BySlot[slot] = clip;
            Plugin.Log.LogInfo($"[HS] EndReplay：登记片段【{kind}】录制者=#{recorderId} key={key} 窗口={before:F1}s前/{after:F1}s后");
            return true;
        }

        /// <summary>一局开始时清空上一局的登记。</summary>
        [HarmonyPatch(typeof(GameRoom), nameof(GameRoom.ChangeGameState), new[] { typeof(EGameState) })]
        internal static class RoundStartHook
        {
            [HarmonyPostfix]
            private static void Postfix(EGameState state)
            {
                if (state == EGameState.Survive)
                    ResetRound();
            }
        }

        // ── ① 杀人：计数 + 记下这一段的键 ───────────────────────────────
        /// <summary>
        /// 挂在服务端 Player.OnDead 之后：`OnDamaged` 在调它之前刚把 `attacker.RecordTime`
        /// 设成击杀时刻、并且已经把 S_RECORD_REPLAY 发给了凶手（:175956-175961）。
        /// ⇒ 这一段**不需要我们再请它录**，只要记下键值。
        ///
        /// 只统计 <see cref="EDeathType.Murder"/>：项圈自爆（黑方胜利时对存活白方执行）
        /// 与审判处决都不算 —— 这正是需求里"不包括项圈自爆"。
        /// </summary>
        [HarmonyPatch(typeof(GamePlayer), nameof(GamePlayer.OnDead))]
        internal static class KillHook
        {
            [HarmonyPostfix]
            private static void Postfix(GamePlayer __instance, GamePlayer black, EDeathType type)
            {
                if (type != EDeathType.Murder)
                    return;

                _murderDeaths++;

                if (black?.PublicInfo == null)
                    return;

                int victim = __instance?.PublicInfo?.PlayerId ?? 0;
                if (black.RecordTime == 0)
                {
                    Plugin.Log.LogWarning("[HS] EndReplay：凶手 RecordTime 为 0，这一段杀人不入回放。");
                    return;
                }

                if (AddClip(black.RecordTime, black.PublicInfo.PlayerId, "杀人",
                        KillBeforeSec?.Value ?? 3f, KillAfterSec?.Value ?? 1f))
                {
                    Plugin.Log.LogInfo($"[HS] EndReplay：本局第 {_murderDeaths} 次刀杀（#{black.PublicInfo.PlayerId} → #{victim}）。");
                }
            }
        }

        // ── ② 拿刀：请凶手客户端额外录一段 ──────────────────────────────
        /// <summary>
        /// 原版只在刀杀时请客户端录，"拿刀"这一段得我们主动请。
        /// `Player.DelayAcquireWeapon`（:176148）是拔刀后的入口（紧接着调 StartWeaponCooltime(5)），
        /// 只在真正拿到武器的人身上跑 ⇒ 拿它当"拿刀时刻"。
        ///
        /// 只在**自提模式**（WeaponGrant.GiveAtStart = false）登记：开局发刀没有跑刀过程，
        /// 录出来只是站着不动的一段。
        /// </summary>
        [HarmonyPatch(typeof(GamePlayer), "DelayAcquireWeapon")]
        internal static class KnifeHook
        {
            [HarmonyPostfix]
            private static void Postfix(GamePlayer __instance)
            {
                if (!IsArmed())
                    return;
                if (WeaponGrantFeature.GiveAtStart != null && WeaponGrantFeature.GiveAtStart.Value)
                    return;

                var room = GameRoom.Instance;
                if (room == null || room.State != EGameState.Survive)
                    return;
                if (__instance?.PublicInfo == null || __instance.Session == null)
                    return;
                if (__instance.Color != EPlayerColor.Black && __instance.Color != EPlayerColor.Dark)
                    return;

                RequestRecord(__instance, TimeManager.Instance.SurviveTime, "拿刀",
                    KnifeBeforeSec?.Value ?? 1f, KnifeAfterSec?.Value ?? 1f);
            }
        }

        /// <summary>
        /// 请某客户端"把当前时刻当成一个新事件录一段"：先发 S_RECORD_REPLAY 让它在
        /// `_killLocalTime` 里登记这个键（客户端 `BuildUploadTape` 靠它定位窗口，:31721）。
        ///
        /// ⚠️ 客户端收到它会执行 `_recordList.Last()`（:31679）—— 只有本局生存阶段在场过、
        /// 真的录过像的客户端才有内容，所以调用方必须先确认对象是真人玩家（有 Session）。
        /// </summary>
        private static bool RequestRecord(GamePlayer player, int key, string kind, float before, float after)
        {
            if (player?.Session == null || player.PublicInfo == null)
                return false;

            player.Session.Send(new S_RECORD_REPLAY { RecordTime = key });
            return AddClip(key, player.PublicInfo.PlayerId, kind, before, after);
        }

        private static bool IsArmed()
        {
            if (ModeRuntime.Bypass)
                return false;
            if (PlayOnEnd != null && !PlayOnEnd.Value)
                return false;
            return Diagnostics.IsLoaded("EndReplay");
        }

        // ── ③ 拦下结算：先播回放 ────────────────────────────────────────
        /// <summary>
        /// 黑方胜利的两条路径最终都会调原版 `GameRoom.GameOver()`（:171389，private）
        /// —— 它做「EndClass 提示 → 判黑方胜 → 存活白方项圈自爆 → 7.5s 后进总结算」。
        /// 在它**之前**插回放：取消这次调用，播完再调一次。
        /// </summary>
        [HarmonyPatch(typeof(GameRoom), "GameOver")]
        internal static class GameOverHook
        {
            [HarmonyPrefix]
            private static bool Prefix(GameRoom __instance)
                => !TryIntercept("黑方胜利(GameOver)", () => CallGameOver(__instance));
        }

        /// <summary>
        /// 白方胜利走的是本模块自己的 `WhiteWinFeature.TriggerWhiteWin`（原版没有这条路径），
        /// 同样在它之前插回放。白胜没有项圈自爆，死亡数在拦下时已是最终值。
        /// </summary>
        [HarmonyPatch(typeof(WhiteWinFeature), nameof(WhiteWinFeature.TriggerWhiteWin))]
        internal static class WhiteWinHook
        {
            [HarmonyPrefix]
            private static bool Prefix(GameRoom room)
                => !TryIntercept("白方胜利(限时归零/任务全清)", () => WhiteWinFeature.TriggerWhiteWin(room));
        }

        private static void CallGameOver(GameRoom room)
        {
            var method = AccessTools.Method(typeof(GameRoom), "GameOver");
            if (method == null)
            {
                Plugin.Log.LogWarning("[HS] EndReplay：找不到 GameOver，回放后无法继续结算。");
                return;
            }

            try
            {
                method.Invoke(room, null);
            }
            catch (Exception ex)
            {
                Plugin.Log.LogWarning($"[HS] EndReplay：回放后调用 GameOver 失败：{ex.Message}");
            }
        }

        /// <summary>
        /// 要不要拦下这次结算？true = 已拦下（结算延后，稍后由 <see cref="FinishReplay"/> 续上）。
        /// 任何异常都吞掉并放行 —— 回放出事绝不能把对局卡在结算之前。
        /// </summary>
        private static bool TryIntercept(string reason, Action continueSettlement)
        {
            try
            {
                // ★ 回放序列进行中：之后进来的每一次结算都必须继续拦下。
                //   BlackWinFeature.TryTrigger 挂在 GameRoom.SurvivalTick 上（每秒跑），
                //   拦下第一次 GameOver 之后它下一 tick 还会再调一次 —— 若这里放行，
                //   真正的结算会在回放还没播完时就跑掉（实测第一次就是这样：
                //   客户端日志里 971 行刚广播 Replay，974 行 TotalResult 就已经发生了）。
                //   结算由 FinishReplay 在回放结束后主动续上。
                if (_inReplay)
                {
                    Plugin.Log.LogInfo($"[HS] EndReplay（{reason}）：回放进行中，继续拦截结算。");
                    return true;
                }

                if (_played || !IsArmed())
                    return false;

                var room = GameRoom.Instance;
                if (room == null || room.State != EGameState.Survive)
                    return false;

                int white = CountWhite(room);
                int need = DeathRatioPercent?.Value ?? 30;
                Plugin.Log.LogInfo($"[HS] EndReplay（{reason}）：刀杀死亡 {_murderDeaths} 人 / 白方 {white} 人，阈值 {need}%。");

                if (_murderDeaths <= 0 || white <= 0)
                    return false;
                if (_murderDeaths * 100 <= white * need)
                {
                    Plugin.Log.LogInfo("[HS] EndReplay：未达阈值，正常结算。");
                    return false;
                }

                _played = true;
                _continueSettlement = continueSettlement;

                var plan = BuildPlan(room);
                if (plan.Count == 0)
                {
                    Plugin.Log.LogWarning("[HS] EndReplay：达到阈值但没有可用片段，正常结算。");
                    _played = false;
                    _continueSettlement = null;
                    return false;
                }

                _inReplay = true;
                Plugin.Log.LogInfo($"[HS] EndReplay：开始回放，共 {plan.Count} 段。");
                RequestTapes(plan);
                return true;
            }
            catch (Exception ex)
            {
                Plugin.Log.LogWarning($"[HS] EndReplay：准备回放失败，正常结算 — {ex.Message}");
                _inReplay = false;
                return false;
            }
        }

        /// <summary>白方 = 非观战且非黑方（含已死亡者）。</summary>
        private static int CountWhite(GameRoom room)
        {
            int n = 0;
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

        /// <summary>
        /// 排出实际要播的片段：拿刀 → 杀人（按发生先后）→ 最后时段。
        /// 最后时段给**每个存活者**各录一段（各自视角），所以要在这一步先请它们录。
        /// </summary>
        private static List<Clip> BuildPlan(GameRoom room)
        {
            int max = MaxClips?.Value ?? 12;
            var plan = new List<Clip>();

            foreach (var c in Clips.Where(c => c.Kind == "拿刀"))
            {
                if (plan.Count >= max) break;
                plan.Add(c);
            }

            foreach (var c in Clips.Where(c => c.Kind == "杀人"))
            {
                if (plan.Count >= max) break;
                plan.Add(c);
            }

            int key = TimeManager.Instance.SurviveTime;
            float before = EndBeforeSec?.Value ?? 3f;
            float after = EndAfterSec?.Value ?? 1f;

            foreach (var p in room.AlivePlayers.ToList())
            {
                if (plan.Count >= max) break;
                if (p?.PublicInfo == null || p.IsSpectator || p.Session == null)
                    continue;

                if (!RequestRecord(p, key, "最后", before, after))
                    continue;

                if (BySlot.TryGetValue(Slot(p.PublicInfo.PlayerId, key), out var clip))
                    plan.Add(clip);
            }

            return plan;
        }

        /// <summary>把每个片段发给它的录制者 —— 客户端只会回自己录的那一段。</summary>
        private static void RequestTapes(List<Clip> plan)
        {
            var room = GameRoom.Instance;
            if (room == null)
                return;

            var byRecorder = new Dictionary<int, List<int>>();
            foreach (var clip in plan)
            {
                if (!byRecorder.TryGetValue(clip.RecorderId, out var list))
                    byRecorder[clip.RecorderId] = list = new List<int>();
                list.Add(clip.Key);
            }

            foreach (var pair in byRecorder)
            {
                int recorderId = pair.Key;
                var player = room.Players.FirstOrDefault(p => p?.PublicInfo != null && p.PublicInfo.PlayerId == recorderId);
                if (player?.Session == null)
                {
                    Plugin.Log.LogWarning($"[HS] EndReplay：录制者 #{recorderId} 已不在房间里，跳过 {pair.Value.Count} 段。");
                    continue;
                }

                var pkt = new S_REQUEST_TAPE { RecordTime = pair.Value[0] };
                pkt.RecordTimes.AddRange(pair.Value);
                player.Session.Send(pkt);
                Plugin.Log.LogInfo($"[HS] EndReplay：向 #{recorderId} 索取 {pair.Value.Count} 段磁带（{string.Join(",", pair.Value)}）。");
            }

            int wait = TapeWaitMs?.Value ?? 2500;
            room.PushAfter(wait, () => BeginReplayScreen(plan));
        }

        // ── ④ 进 Trial → 推 Replay → 等放完 → 续结算 ────────────────────
        private static void BeginReplayScreen(List<Clip> plan)
        {
            try
            {
                var room = GameRoom.Instance;
                if (room == null)
                {
                    FinishReplay("房间已不存在");
                    return;
                }

                // 客户端只有在 EGameState.Trial 才会创建回放宿主 UI_TrialEvent
                // （:38706 → ShowTrialUI :74986）；没有它 S_TRIAL_STATE 会被直接跳过（:43043）。
                //
                // ★★ 这里刻意**只广播状态包给客户端，不动服务端 State**。实测第一次就是这里错了：
                //   走 room.ChangeGameState(Trial) 会把服务端也置成 Trial，于是
                //     (a) GameRoom.TrialTick（:171536，`if (State == Trial)`）开始每秒跑 Trial.Tick()，
                //         原版裁判状态机接管并不断广播 S_TRIAL_STATE{Discuss/VotePhase/VoteResult}，
                //         把我们的 Replay 覆盖掉；因为我们没走 TrialManager.Init，
                //         DiscussionSecond 是 0 ⇒ 讨论瞬间过期，很快就播到"投票结果"；
                //     (b) GameOver() 末尾是 `if (State == EGameState.Survive) PushAfter(7500, …TotalResult)`
                //         （:171403）—— 服务端不在 Survive，结算那一步反而不执行，
                //         最后一整局由原版裁判流程带回了大厅。
                //
                //   只让客户端进 Trial 就同时避开这两条：服务端 State 始终是 Survive，
                //   TrialTick 不跑、GameOver / TriggerWhiteWin 的前置条件也都满足。
                //   客户端在那段期间以为是 Trial —— 这正是原版回放期间的形态，
                //   结算时的 ChangeGameState(TotalResult) 会把它带出去。
                Plugin.Log.LogInfo($"[HS] EndReplay：先让客户端建立回放 UI（{plan.Count} 段）。");

                // ★ 客户端的换状态是**异步**的：Handle_S_CHANGE_GAME_STATE（客户端 :42362）
                //   只调 Managers.UI.StartLoading(pkt.State) 开始淡入淡出过渡，真正落到
                //   ChangeState（即建回放宿主 UI_TrialEvent 的那一段，:38698-38714）要等过渡走完。
                //   客户端日志实测：广播后客户端仍停在旧状态（`Client Survive -> TotalResult`），
                //   所以那一刻 TrialUI 还是 null，S_TRIAL_STATE{Replay} 被原版直接跳过（:43043）。
                //
                //   ChangeState 末尾会 CompleteAndSend()（:38714），所以"等这条回执"就是
                //   "等过渡走完 + 回放宿主 UI 已建好"。给足 8 秒：过渡本身可能被淡入淡出拖长。
                room.WaitCompletePacket(() => WaitTrialIntroThenReplay(plan), room.CompleteWaitCount(), 8000, 1500);
                room.Broadcast(new S_CHANGE_GAME_STATE { State = EGameState.Trial });
            }
            catch (Exception ex)
            {
                Plugin.Log.LogWarning($"[HS] EndReplay：进入回放失败 — {ex.Message}");
                FinishReplay("进入失败");
            }
        }

        /// <summary>
        /// 等审判 UI 的**开场演出**播完，再推 Replay。
        ///
        /// ★ 这是实测踩出来的坑，不看客户端源码根本想不到：
        ///   `StartReplay()`（UI_TrialEvent）第一件事就是调 `ResetSlideVisual()`（:2348），而它是：
        ///
        ///       _slideSequence.Kill();              // 杀掉开场序列
        ///       color.a = 1f; text.color = color;   // 又把文字设成完全不透明
        ///
        ///   而开场 `SlidingText()`（:1570）的收尾是 `slideText.SetActive(false)`（在序列 OnComplete 里）
        ///   —— 序列被 Kill 掉之后这句永远不会执行。于是**开场文字被冻在画面上、且完全不透明**，
        ///   一直压在整段回放上（实机现象：标题卡过后"学级裁判 开庭 / 赌上性命的真相告白"始终可见）。
        ///
        ///   开场时长（源码里的常量）：`SlidingText` 0.5+1+0.5 ≈ 2.0s，随后 `AlertMessage` 0.5+3.5+0.5 ≈ 4.5s，
        ///   合计约 6.5s。默认等 8 秒留余量；等完之后 `ResetSlideVisual` 面对的已是一个空且隐藏的文字元素，
        ///   与原版走到 Replay 时的状态一致。
        /// </summary>
        private static void WaitTrialIntroThenReplay(List<Clip> plan)
        {
            var room = GameRoom.Instance;
            if (room == null)
            {
                FinishReplay("房间已不存在");
                return;
            }

            int wait = TrialIntroWaitMs?.Value ?? 8000;

            // ★ 照原版补上这一包：`StartFirstTalk()` 只在**进入 Discuss 状态**时被调用
            //   （UI_TrialEvent.StartState :1658-1661）。原版流程里 Discuss 是裁判的第一站，
            //   字幕就在那时开演、6.5 秒后由序列自己 SetActive(false) 收掉；
            //   等轮到 Replay（几分钟后）时它早已干净。
            //   我们从 Survive 直接跳到 Replay，从没发过 Discuss ⇒ 字幕序列根本没开始，
            //   而它的元素仍在，StartReplay 的 ResetSlideVisual 把它设成 alpha=1 就冻住了。
            //   ⇒ 主动发一包 Discuss 让字幕**正常开演并正常收尾**，这是原版路径。
            room.Broadcast(new S_TRIAL_STATE { State = ETrialState.Discuss });

            Plugin.Log.LogInfo($"[HS] EndReplay：已发 Discuss 让审判开场字幕正常开演，等 {wait}ms 播完再推 Replay。");
            room.PushAfter(wait, () => BroadcastReplay(plan));
        }

        private static void BroadcastReplay(List<Clip> plan)
        {
            try
            {
                var room = GameRoom.Instance;
                if (room == null)
                {
                    FinishReplay("房间已不存在");
                    return;
                }

                int clips = plan.Count;
                int budget = EstimateBudgetMs(plan);

                // 主机进程里既有服务端也有它自己的客户端，所以能顺手当一次"探针"：
                // 本机客户端若已建好回放宿主 UI，至少说明这次等待是等对了。
                // 远端客户端无法探测，只能靠日志判断。
                try
                {
                    var scene = Managers.UI.SceneUI as UI_GameScene;
                    bool localReady = scene != null && scene.TrialUI != null;
                    Plugin.Log.LogInfo($"[HS] EndReplay：本机客户端回放宿主 UI {(localReady ? "已就绪" : "仍为空")}（远端客户端无法探测）。");
                }
                catch (Exception probeEx)
                {
                    Plugin.Log.LogWarning($"[HS] EndReplay：探测本机回放宿主 UI 失败 — {probeEx.Message}");
                }

                Plugin.Log.LogInfo($"[HS] EndReplay：广播 Replay 状态（{clips} 段，预算 {budget}ms）。");

                // 客户端在 RecordManager.Stop() 里 CompleteAndSend()（:32164）。
                // 用原版同款等待：第一个回执到达后再宽限一小会儿就推进；超时也会强制推进。
                room.WaitCompletePacket(() => FinishReplay("客户端回执/超时"), room.CompleteWaitCount(), budget, 4000);

                room.Broadcast(new S_TRIAL_STATE { State = ETrialState.Replay });
            }
            catch (Exception ex)
            {
                Plugin.Log.LogWarning($"[HS] EndReplay：推送回放失败 — {ex.Message}");
                FinishReplay("推送失败");
            }
        }

        /// <summary>
        /// 回放预算：客户端 `StartReplay` 先播 5 秒"真相公开"标题，再逐段放；
        /// 慢镜期间 `Update` 推进更慢，所以按 1.6 倍留量，再加上段间转场与固定余量。
        /// 这只是**超时兜底** —— 客户端放完会在 `RecordManager.Stop()` 里
        /// `CompleteAndSend()`（:32164），正常情况下我们会提前收到回执。
        /// </summary>
        private static int EstimateBudgetMs(List<Clip> plan)
        {
            float per = SecondsPerClipEstimate?.Value ?? 5f;
            double total = 5000;
            foreach (var c in plan)
                total += Math.Max(c.Before + c.After, per) * 1000.0 * 1.6 + 600;
            total += 8000;
            return (int)Math.Max(15000, Math.Min(120000, total));
        }

        /// <summary>回放收尾：继续原本的结算。只会真正执行一次。</summary>
        private static void FinishReplay(string why)
        {
            if (!_inReplay)
                return;

            _inReplay = false;
            var cont = _continueSettlement;
            _continueSettlement = null;

            Plugin.Log.LogInfo($"[HS] EndReplay：回放结束（{why}），继续结算。");

            try
            {
                cont?.Invoke();
            }
            catch (Exception ex)
            {
                Plugin.Log.LogWarning($"[HS] EndReplay：继续结算失败 — {ex.Message}");
            }
        }

        // ── ⑤ 磁带重裁：在广播前把每段裁到配置的窗口 ────────────────────
        /// <summary>
        /// 客户端侧诊断：游戏的 `UnityEngine.Debug.Log` **不进** BepInEx 的 LogOutput.log
        /// （实测：日志里只有各插件的日志源），所以回放链路上客户端的韩文日志平时看不到。
        /// 这里把两个关键节点用 <see cref="Plugin.Log"/> 记一遍，让下次实测能从一份日志里定位。
        ///
        /// 这些补丁只在本机装了插件时生效 —— 远程客户端本来也不该被要求装。
        /// </summary>
        /// <summary>
        /// 客户端侧诊断：把"回放宿主 UI 到底建没建"直接记下来。
        /// 这一条是针对实测暴露的核心问题 —— 客户端换状态是异步的
        /// （`Handle_S_CHANGE_GAME_STATE` 只调 `StartLoading`，:42386），
        /// 过渡走完才轮到 `ShowTrialUI()`（:38706）。
        /// </summary>
        [HarmonyPatch(typeof(UI_GameScene), nameof(UI_GameScene.ShowTrialUI))]
        internal static class ClientShowTrialUiHook
        {
            [HarmonyPostfix]
            private static void Postfix(UI_GameScene __instance)
            {
                try
                {
                    Plugin.Log.LogInfo($"[HS] EndReplay（客户端）：ShowTrialUI 已执行，回放宿主 UI {( __instance.TrialUI != null ? "建立成功" : "仍然为空")}。");
                }
                catch (Exception ex)
                {
                    Plugin.Log.LogWarning($"[HS] EndReplay（客户端）：诊断日志失败 — {ex.Message}");
                }
            }
        }

        [HarmonyPatch]
        internal static class ClientTrialStateHook
        {
            private static MethodBase Target()
                => AccessTools.Method(AccessTools.TypeByName("PacketHandler"), "Handle_S_TRIAL_STATE");

            [HarmonyPrepare]
            private static bool Prepare()
            {
                bool ok = Target() != null;
                if (!ok)
                    Plugin.Log.LogWarning("[HS] EndReplay：找不到客户端 PacketHandler.Handle_S_TRIAL_STATE，客户端诊断不可用（不影响播放）。");
                return ok;
            }

            [HarmonyTargetMethod]
            private static MethodBase TargetMethod() => Target();

            [HarmonyPostfix]
            private static void Postfix(object __1)
            {
                try
                {
                    var pkt = Traverse.Create(__1).Property("Pkt").GetValue();
                    var state = Traverse.Create(pkt).Property("State").GetValue();
                    var scene = Managers.UI.SceneUI as UI_GameScene;
                    bool ui = scene != null && scene.TrialUI != null;
                    Plugin.Log.LogInfo($"[HS] EndReplay（客户端）：收到 S_TRIAL_STATE = {state}，回放宿主 UI {(ui ? "已就绪" : "尚未建立")}。");
                }
                catch (Exception ex)
                {
                    Plugin.Log.LogWarning($"[HS] EndReplay（客户端）：诊断日志失败 — {ex.Message}");
                }
            }
        }

        /// <summary>客户端侧诊断：`RecordManager.Play()` 是真正开始播的入口（:31866）。</summary>
        [HarmonyPatch(typeof(RecordManager), nameof(RecordManager.Play))]
        internal static class ClientPlayHook
        {
            [HarmonyPrefix]
            private static void Prefix(RecordManager __instance)
            {
                try
                {
                    Plugin.Log.LogInfo($"[HS] EndReplay（客户端）：RecordManager.Play 开始，可用磁带 {__instance.PlayTapeCount} 段。");
                }
                catch (Exception ex)
                {
                    Plugin.Log.LogWarning($"[HS] EndReplay（客户端）：诊断日志失败 — {ex.Message}");
                }
            }
        }

        /// <summary>
        /// 客户端侧诊断：`BeginTape` 是每段的实际入口；它会因为"首帧不是 SpawnShot"
        /// 或"帧数 < 2"而整段跳过（:31905），本模块合成的 SpawnShot 正是为此。
        /// </summary>
        [HarmonyPatch(typeof(RecordManager), "BeginTape")]
        internal static class ClientBeginTapeHook
        {
            [HarmonyPrefix]
            private static void Prefix(RecordManager __instance)
            {
                try
                {
                    Plugin.Log.LogInfo(
                        $"[HS] EndReplay（客户端）：BeginTape 第 {__instance.PlayTapeCount} 段表 / 是否最后一段={(__instance.IsLastTape ? "是" : "否")}。");
                }
                catch (Exception ex)
                {
                    Plugin.Log.LogWarning($"[HS] EndReplay（客户端）：诊断日志失败 — {ex.Message}");
                }
            }
        }

        /// <summary>
        /// 原版 `HostPacketHandler.Handle_C_TAPE`（:174890）收到 C_TAPE 后直接 Broadcast(S_TAPE)。
        /// 这里整段接管：是登记过的片段就按我们的窗口重裁后再广播；否则放行给原版。
        /// </summary>
        [HarmonyPatch(typeof(HostPacketHandler), nameof(HostPacketHandler.Handle_C_TAPE))]
        internal static class TapeHook
        {
            [HarmonyPrefix]
            private static bool Prefix(IPacketSink session, Packet packet)
            {
                try
                {
                    var c = packet?.Pkt as C_TAPE;
                    if (c == null || BySlot.Count == 0)
                        return true;

                    var room = GameRoom.Instance;
                    if (room == null)
                        return true;

                    int recorderId = ResolvePlayerId(room, session);
                    if (recorderId == 0 || !BySlot.TryGetValue(Slot(recorderId, c.RecordTime), out var clip))
                        return true;                // 不是我们的片段 → 交还原版

                    var trimmed = TrimTape(c.SnapShots.ToList(), clip);
                    if (trimmed.Count < 2)
                    {
                        Plugin.Log.LogWarning($"[HS] EndReplay：片段【{clip.Kind}】key={clip.Key} 重裁后不足 2 帧，丢弃。");
                        return false;               // 丢弃：既不广播也不登记
                    }

                    var sendPkt = new S_TAPE
                    {
                        RecordTime = c.RecordTime,
                        TapeCount = TrialManager.Instance?.TapeCount ?? 1
                    };
                    sendPkt.SnapShots.AddRange(trimmed);

                    Plugin.Log.LogInfo(
                        $"[HS] EndReplay：片段【{clip.Kind}】录制者 #{recorderId} 重裁 {c.SnapShots.Count} → {trimmed.Count} 帧" +
                        $"（{clip.Before:F1}s 前 / {clip.After:F1}s 后）。");

                    room.Push(delegate
                    {
                        room.Broadcast(sendPkt);
                        TrialManager.Instance?.SetTape(c.RecordTime, sendPkt.SnapShots.Count);
                    });
                    return false;
                }
                catch (Exception ex)
                {
                    Plugin.Log.LogWarning($"[HS] EndReplay：重裁磁带失败，交还原版 — {ex.Message}");
                    return true;
                }
            }
        }

        /// <summary>包处理器只给到 session，用"哪个玩家的 Session 就是它"反查玩家 id（避免依赖 session 的具体类型）。</summary>
        private static int ResolvePlayerId(GameRoom room, IPacketSink session)
        {
            if (session == null)
                return 0;

            foreach (var p in room.Players)
            {
                if (p?.PublicInfo != null && ReferenceEquals(p.Session, session))
                    return p.PublicInfo.PlayerId;
            }
            return 0;
        }

        /// <summary>
        /// 把一段磁带裁到 [锚点−before, 锚点+after]。锚点 = 第一枚 NormalTimeEdit
        /// （客户端 `ReserveSaveTape` 在事件处必然插入的那一枚，:31652）。
        ///
        /// 找不到锚点或 SpawnShot 时**原样返回**（宁长不丢）：磁带时间戳是各客户端自己的
        /// ClientTime，房主这边没有别的办法定位事件。
        ///
        /// 首帧必须是 SpawnShot（客户端 `BeginTape` :31905 的硬要求），所以合成一枚：
        /// 时间戳取窗口起点，内容沿用原磁带里的 Spawn 信息。
        /// </summary>
        private static List<SnapShot> TrimTape(List<SnapShot> tape, Clip clip)
        {
            // 锚点取**最后一枚** NormalTimeEdit：磁带窗口是 [击杀−3, 击杀+5]（原版 `BuildUploadTape`），
            // 里面通常还含**更早那次击杀**的编辑三元组（Slow/Normal/Glitch），取第一枚会锚错时刻。
            float? anchor = null;
            foreach (var s in tape)
            {
                if (s.Type == ESnapShotType.EditShot && s.Edit != null
                    && s.Edit.Type == EEditShotType.NormalTimeEdit)
                {
                    anchor = s.TimeStamp;
                }
            }

            if (anchor == null)
            {
                Plugin.Log.LogWarning($"[HS] EndReplay：片段【{clip.Kind}】找不到时间锚点，按原样播放。");
                return tape;
            }

            // 录制者是谁：磁带首帧的 Spawn 就是他（客户端 `BeginTape` 也据此决定镜头跟随谁）。
            var firstSpawn = tape.FirstOrDefault(s => s.Type == ESnapShotType.SpawnShot)?.Spawn;
            if (firstSpawn == null)
            {
                Plugin.Log.LogWarning($"[HS] EndReplay：片段【{clip.Kind}】没有 SpawnShot，按原样播放。");
                return tape;
            }

            float start = anchor.Value - clip.Before;
            float end = anchor.Value + clip.After;

            // ★ 合成首帧用的 Spawn 信息必须带**窗口起点那一刻的位置**。
            //   磁带首帧往往是很早的开局 SpawnShot（`RecordAllType` 的 isStartShot，位置＝出生点），
            //   直接沿用会让客户端 `FollowCamera` 把镜头贴到出生点 —— 实测就是这样：
            //   `[Replay] tape 2/4 카메라 즉시 이동 → (4025.34, 4150.52, -50.00)`，
            //   镜头对着空地，看起来"只有标题没有内容"。
            //
            //   好消息是 `RecordAllType` 每秒都会给**所有玩家**各记一枚 SpawnShot（用 ClientTime 基准、
            //   带当时的位置），所以取"录制者自己在窗口起点之前最后一枚"就是我们要的位置。
            int recorderId = firstSpawn.PlayerId;
            var spawn = tape.LastOrDefault(s => s.Type == ESnapShotType.SpawnShot
                                                && s.Spawn != null
                                                && s.Spawn.PlayerId == recorderId
                                                && s.TimeStamp <= start)?.Spawn
                        ?? firstSpawn;

            var result = new List<SnapShot>(tape.Count)
            {
                new SnapShot
                {
                    Type = ESnapShotType.SpawnShot,
                    TimeStamp = start,
                    Spawn = spawn
                }
            };

            foreach (var s in tape)
            {
                if (s.TimeStamp <= start || s.TimeStamp > end)
                    continue;
                if (s.Type == ESnapShotType.EditShot)
                    continue;                       // 窗口已由我们自己定，客户端的慢镜/花屏编辑不再需要
                result.Add(s);
            }

            Plugin.Log.LogInfo(
                $"[HS] EndReplay：片段【{clip.Kind}】锚点={anchor.Value:F2} 窗口=[{start:F2},{end:F2}] " +
                $"磁带跨度=[{tape[0].TimeStamp:F2},{tape[tape.Count - 1].TimeStamp:F2}] 留 {result.Count} 帧 " +
                $"(镜头跟随 #{recorderId}，位置=({spawn.Pos?.X:F0},{spawn.Pos?.Y:F0}))");

            return result;
        }
    }
}
