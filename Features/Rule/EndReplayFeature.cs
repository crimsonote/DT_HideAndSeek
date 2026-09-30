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

        [ConfigField(7.5f, "「自爆」片段（黑方胜利时对存活露娜系白方的项圈自爆）：事件**前**秒数。" +
            "这一段现在是**服务端录制**的（客户端录不了：白方被处决时全是死人，" +
            "`RecordManager.Recording()` 的 `!Managers.Game.IsAlive` 守卫直接 return）⇒ 爆前素材拿得到。" +
            "7.5s ＝ 原版结算从进入自爆流程到爆炸的时长。",
            Min = 0f, Max = 30f)]
        public static ConfigEntry<float> SelfDestructBeforeSec;

        [ConfigField(0.5f, "「自爆」片段：事件**后**秒数。这 0.5s 是镜头切到黑方之后的收尾" +
            "（前 7.5s 为白方多机位巡礼）⇒ 整段约 8s。", Min = 0f, Max = 30f)]
        public static ConfigEntry<float> SelfDestructAfterSec;

        [ConfigField(12, "最多播放几段，取段顺序为 拿刀 → 杀人 → 最后时段。段数越多回放越长。",
            Min = 1f, Max = 40f)]
        public static ConfigEntry<int> MaxClips;

        [ConfigField(2500, "向客户端索取磁带后等多久开始播放（毫秒）。客户端收到请求即回包，给一点余量即可。",
            Min = 200f, Max = 20000f)]
        public static ConfigEntry<int> TapeWaitMs;

        [ConfigField(5f, "超时兜底用的单段估算秒数（秒/段）。" +
            "正常情况下客户端的完成回执会提前结束等待，这个值只在回执丢失时用到。", Min = 1f, Max = 60f)]
        public static ConfigEntry<float> SecondsPerClipEstimate;

        [ConfigField(7000, "收掉加载页并让开场字幕开演后，等多久再推 Replay（毫秒）。" +
            "加载页由 S_FADE_IN 立刻收掉，所以这里只需等**入场演出/开场字幕**自己播完" +
            "（SlidingText 约 3.0s + AlertMessage 约 4.5s）；等太短会把字幕序列 Kill 掉、" +
            "文字被冻在画面上（实测踩过）。",
            Min = 0f, Max = 60000f)]
        public static ConfigEntry<int> TrialIntroWaitMs;

        [ConfigField(true, "【阶段 2】用「隐藏观察者」当回放主视角，去掉画面上的黑白剪影。" +
            "做法：把合成首帧的角色换成一位**已死亡、且这段窗口里没有自己镜头**的玩家，并标成 IsGhost。" +
            "为什么这样就看不见：客户端 `Player.Update` 每帧刷 `RefreshGhostVisual()`，回放期间它走 case 2 " +
            "把**幽灵替身与正常骨架一起关掉**，于是镜头仍锁在他身上、剪影也打在他身上，但画面上什么都没有；" +
            "真实玩家（黑方/受害者/旁观者）全部本色出场。取不到合适的人时自动退回原版主视角（#录制者）。")]
        public static ConfigEntry<bool> ObserverCamera;

        [ConfigField(-1, "隐藏观察者用哪个 id：-1 = 自动（已死亡 + 仍在房间 + 磁带里出现过 + 本段窗口内无自己镜头）；" +
            "0 = 原版的回放临时玩家（房主自己的替身，必然可见、必被剪影）；>0 = 手动指定。" +
            "⚠ 必须是**本局仍在房间里**的 id —— 客户端 `ChangeMyPlayer` 是 `_cache[id]` 直接索引，" +
            "id 不存在会抛 KeyNotFoundException 让整段回放中断。")]
        public static ConfigEntry<int> ObserverPlayerId;

        [ConfigField(true, "把观察者标记成幽灵（IsGhost）。**必须开着** —— 这是让它的骨架被关闭、" +
            "从而让剪影不可见的唯一途径（见上面 ObserverCamera 的说明）。")]
        public static ConfigEntry<bool> ObserverGhost;

        [ConfigField(true, "【阶段 1｜防卡死】每段磁带开头补一轮「全员出场帧」（每个玩家各一枚，时间戳＝窗口起点）。" +
            "原版磁带天生带这一轮：`RecordAllType` 每秒一次、且录音缓冲一定以录制者自己的出场帧开头，" +
            "于是每一轮「我自己 + 全体」是粘在一起的；客户端靠它把**每台机器自己的** 0 号替身装配好。" +
            "我们重裁时把这一轮裁掉了，于是「本机自己的换道具/换状态镜头」可能打在还没装配的替身上" +
            "（`BaseMaterial` 为 null）⇒ `EquipItem` 空引用 ⇒ 回放永久卡死、只能大退（实测踩过）。" +
            "补回这一轮即消除该风险，**观感不变**（补的都是原版磁带里本来就有的帧）。")]
        public static ConfigEntry<bool> RosterFrames;

        [ConfigField(true, "【审判 UI 残留文字】进回放时把字幕行 `SlideLine/SlideText` 上的文字清空。" +
            "背景（实测定位）：那行字是**预制体里烘死的默认值**（官方中文包里没有对应条目），" +
            "原版靠 `StartState(Discuss)` 的开场字幕序列收尾 `slideText.SetActive(false)` 把它收掉；" +
            "我们的回放直接从 Replay 相位进入，那个序列从未跑过，而 `StartReplay()` 里的 `ResetSlideVisual()` " +
            "又会把文字设成完全不透明 ⇒ 它会一直挂在画面正中（中文客户端上显示为韩文「논의 시작」）。" +
            "关掉本项可回到旧行为（残留文字一直显示），用于对照。")]
        public static ConfigEntry<bool> ClearTrialLabels;

        [ConfigField(true, "回放里把黑方的昵称显示成红色，让观众一眼看出「黑刀＝黑幕」。" +
            "做法：推回放前广播一包 `S_NOTIFY_BLACK{ PlayerId = 黑方 }` —— 客户端会把它加进 `KnownBlackIds`" +
            "（:42496），而 `Player.Refresh()` 用 `NameTag.SetNameColor(KnownBlackIds.Contains(PublicInfo.PlayerId))`" +
            "（:17450）决定名字颜色，红色就是 `Color(1, 0.18f, 0.439f)`（:72362）。" +
            "回放里每个玩家都会被 `ApplySpawn → SetInfo → Refresh` 重设一次，所以黑方自然带红名。" +
            "返回大厅时客户端自己会 `KnownBlackIds.Clear()`（:29075），不会带到下一局。")]
        public static ConfigEntry<bool> RevealBlackName;

        [ConfigField(true, "【没收到磁带时】补一段「占位磁带」，让客户端自己走一遍「这段没录到」的转场，" +
            "而不是让这一幕从回放里凭空消失。" +
            "背景：客户端的播放列表就是它收到的磁带集合（`RecordManager._playTapes`）——服务端少发一段，" +
            "那一幕在观感上就**根本不存在**，观众只会觉得「少了一段」，并不知道是没录到。" +
            "两条实测约束决定了占位磁带的样子（均见客户端 `RecordManager`）：" +
            "① `AddPlayTape` 对**空**磁带直接丢弃（日志 `빈 테이프 무시`）⇒ 0 帧等于没发；" +
            "② `BeginTape` 要求 `Count >= 2` 且首帧必须是 `SpawnShot`，不满足会**静默跳过**（连转场都不播）。" +
            "所以占位磁带做成**恰好 2 帧**（`SpawnShot` + 一枚无害的 `NormalTimeEdit`）：客户端会正常" +
            "「开始 → 立刻播完 → `FinishTape()`」⇒ 走一次转场演出（噪声）再进下一段 —— 那正是「一闪而过、表示这段没录到」。" +
            "首帧主视角优先取录制者本人，其次取「已死 + 仍在房间」的人（与隐藏观察者同一套判据）——" +
            "客户端 `ChangeMyPlayer` 用 `_cache[id]` 直接索引，人不在缓存里会抛。")]
        public static ConfigEntry<bool> PlaceholderTape;

        // ── 片段登记 ────────────────────────────────────────────────────
        private sealed class Clip
        {
            public int Key;          // RecordTime（请求磁带时用的键）
            public int RecorderId;   // 谁录的（谁收到 S_RECORD_REPLAY）
            public string Kind;      // 拿刀 / 杀人 / 最后 / 自爆
            public float Before;
            public float After;
            /// <summary>登记这一刻的**房主时钟**（`Managers.Game.ClientTime`）—— 服务端合成磁带时的窗口原点。</summary>
            public float At;
            /// <summary>真的收到磁带了吗 —— 没收到时会在推 Replay 前补一段"占位磁带"（见 <c>SendPlaceholders</c>）。</summary>
            public bool Filled;
        }

        private static readonly List<Clip> Clips = new List<Clip>();
        /// <summary>按 (录制者, RecordTime) 索引 —— 同一时刻多个玩家各录一段时键会相同，不能只用 key。</summary>
        private static readonly Dictionary<long, Clip> BySlot = new Dictionary<long, Clip>();
        private static int _murderDeaths;
        private static bool _played;
        private static bool _inReplay;
        private static Action _continueSettlement;
        /// <summary>当前这一段的"隐藏观察者"id（客户端从 `RecordManager._blackId` 读出）；0＝原版主视角，不处理。</summary>
        private static int _anchorId;

        private static long Slot(int playerId, int key) => ((long)playerId << 32) | (uint)key;

        internal static void ResetRound()
        {
            Clips.Clear();
            BySlot.Clear();
            _murderDeaths = 0;
            _played = false;
            _inReplay = false;
            _continueSettlement = null;
            _anchorId = 0;
            EndReplayHostTape.Clear();
        }

        private static bool AddClip(int key, int recorderId, string kind, float before, float after)
        {
            if (key == 0 || recorderId == 0)
                return false;

            long slot = Slot(recorderId, key);
            if (BySlot.ContainsKey(slot))
                return false;

            var clip = new Clip
            {
                Key = key,
                RecorderId = recorderId,
                Kind = kind,
                Before = before,
                After = after,
                At = Managers.Game.ClientTime
            };
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

        /// <summary>
        /// 【房主侧录制】每帧节流采样一次全体玩家，攒进 <see cref="EndReplayHostTape"/> 的环形缓冲。
        ///
        /// 为什么挂在 `Managers.Update`：它是这台机器的**主更新**（实测崩因堆栈里就是
        /// `Managers.Update → RecordManager.Update`），假人、已死者也都在服务端的
        /// `Players` / `DeadPlayers` 里 ⇒ 采得到；客户端那套 `RecordManager` 对这两类人无能为力。
        ///
        /// 采样本身很便宜（5Hz、每人一次 `PublicInfo.Clone()`），但**只在武装状态且不在回放中**才跑，
        /// 免得在结算/大厅里白采。
        /// </summary>
        [HarmonyPatch(typeof(Managers), "Update")]
        internal static class HostTapeSampleHook
        {
            [HarmonyPostfix]
            private static void Postfix()
            {
                try
                {
                    if (_played || _inReplay || !IsArmed())
                        return;

                    EndReplayHostTape.Sample(Managers.Game.ClientTime);
                }
                catch
                {
                    // 采样失败不该影响对局。
                }
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

        /// <summary>
        /// 是否达到"播放回放"的阈值（刀杀死亡人数超过白方人数的 <see cref="DeathRatioPercent"/>%）。
        /// 单独抽出来是为了让"自爆片段"的请求（<see cref="CollarBombHook"/>）也能用同一判据 ——
        /// 自爆发生在最后，那时死亡数已是最终值；不达阈值就不必白请客户端录一段。
        /// </summary>
        private static bool ThresholdMet(GameRoom room)
        {
            int white = CountWhite(room);
            int need = DeathRatioPercent?.Value ?? 30;
            if (_murderDeaths <= 0 || white <= 0)
                return false;
            return _murderDeaths * 100 > white * need;
        }

        // ── ③ 拦下结算：先播回放 ────────────────────────────────────────
        //
        // ★ 拦截点选在**结算状态切换** `GameRoom.ChangeGameState(EGameState.TotalResult)`，
        //   而不是原来的 `GameOver()` / `WhiteWinFeature.TriggerWhiteWin`。原因（读码 + 需求）：
        //   · `GameOver()` 内部就做完了原版收尾演出 —— `EndClass` 提示、判黑方胜、
        //     **对存活白方 `OnDeadCollarBomb()`（项圈自爆）**，然后才
        //     `PushAfter(7500, ChangeGameState, TotalResult)`（:171400 / :171403）。
        //     也就是说**自爆发生在结算之前**；要在回放里播"自爆"，就必须等它演完再插回放。
        //   · 结算状态切换只有三个调用点（`GameOver` 的推送、`ClearAllMission()`＝任务全清白胜、
        //     `Trial.FinalizeTrialResult()`），**全都走公开的 `ChangeGameState(EGameState)`**
        //     ⇒ 一个钩子全覆盖。
        //   · 此刻客户端的房间状态还是 `Survive`（这条广播被我们拦下了），所以照旧可以把它们推进
        //     Trial、借审判 UI 当回放宿主；回放播完我们再调一次原方法把 TotalResult 放出去。
        private static string _ending = "结算前";
        private static bool _whiteWin;

        /// <summary>只做记录：黑方胜利的收尾演出跑过了（`GameOver` 是 private，用字符串定位）。</summary>
        [HarmonyPatch(typeof(GameRoom), "GameOver")]
        internal static class GameOverNoteHook
        {
            [HarmonyPostfix]
            private static void Postfix()
            {
                _ending = "结算前（黑方胜利）";
                _whiteWin = false;
            }
        }

        /// <summary>只做记录：白方幸存胜利走的是本模块自己的 `WhiteWinFeature.TriggerWhiteWin`。</summary>
        [HarmonyPatch(typeof(WhiteWinFeature), nameof(WhiteWinFeature.TriggerWhiteWin))]
        internal static class WhiteWinNoteHook
        {
            [HarmonyPostfix]
            private static void Postfix()
            {
                _ending = "结算前（白方胜利）";
                _whiteWin = true;
            }
        }

        /// <summary>
        /// ★ 结算的唯一入口（三个调用点都走它）。在这里拦下 `TotalResult`：
        /// 原版收尾（含项圈自爆）已经演完 ⇒ 正好插回放；播完再调原方法放行。
        /// </summary>
        [HarmonyPatch(typeof(GameRoom), nameof(GameRoom.ChangeGameState), new[] { typeof(EGameState) })]
        internal static class TotalResultHook
        {
            [HarmonyPrefix]
            private static bool Prefix(GameRoom __instance, EGameState state)
            {
                if (state != EGameState.TotalResult)
                    return true;

                return !TryIntercept(_ending, () => CallChangeTotalResult(__instance));
            }
        }

        /// <summary>回放结束后继续结算：调用原版 `ChangeGameState(EGameState.TotalResult)`。</summary>
        private static void CallChangeTotalResult(GameRoom room)
        {
            var method = AccessTools.Method(typeof(GameRoom), "ChangeGameState", new[] { typeof(EGameState) });
            if (method == null)
            {
                Plugin.Log.LogWarning("[HS] EndReplay：找不到 GameRoom.ChangeGameState(EGameState)，回放后无法继续结算。");
                return;
            }

            try
            {
                Plugin.Log.LogInfo("[HS] EndReplay：回放结束，继续结算（ChangeGameState → TotalResult）。");
                method.Invoke(room, new object[] { EGameState.TotalResult });
            }
            catch (Exception ex)
            {
                Plugin.Log.LogWarning($"[HS] EndReplay：回放后进入结算失败：{ex.Message}");
            }
        }

        /// <summary>
        /// 【自爆片段】黑方胜利时原版会对**存活的露娜系白方**执行 `OnDeadCollarBomb()`（在 `GameOver()` 里）。
        /// 我们在它执行**之前**给该玩家自己的客户端发 `S_RECORD_REPLAY`，让它把"此刻"登记成一个事件
        /// （客户端 `BuildUploadTape` 靠它定位窗口），于是磁带窗口就以自爆为中心。
        ///
        /// ⚠ 两个已知限制（配置说明里也写了）：
        ///   · **录制者本人的录像在他死亡那一刻就停了**（客户端 `Recording()` 要求 `IsAlive`）
        ///     ⇒ "爆后 N 秒"通常拿不到，实际只有"爆前 + 到爆炸那一刻"；
        ///   · **假人没有客户端** ⇒ 拿不到（`RequestRecord` 会返回 false）。
        /// </summary>
        [HarmonyPatch(typeof(GamePlayer), "OnDeadCollarBomb")]
        internal static class CollarBombHook
        {
            [HarmonyPrefix]
            private static void Prefix(GamePlayer __instance)
            {
                try
                {
                    if (_played || _inReplay || !IsArmed())
                        return;
                    if (__instance?.PublicInfo == null || __instance.Session == null || __instance.IsSpectator)
                        return;

                    var room = GameRoom.Instance;
                    if (room == null || !ThresholdMet(room))
                        return;

                    // ★ 同时喂给房主侧录制器：这一刻的爆炸是"自爆剪辑"的核心素材，而白方此刻
                    //   全是死人 ⇒ 他们的客户端录不了（`Recording()` 的 `!IsAlive` 守卫），
                    //   只能由服务端自己记下时刻与位置，稍后合成磁带。
                    EndReplayHostTape.NoteBomb(
                        Managers.Game.ClientTime, __instance.PublicInfo.PlayerId, __instance.PublicInfo.Pos);

                    // ① 被处决者本人优先（他的客户端会记下"自己眼前"的爆炸 VFX）。
                    // ② 再请**其他活着的真人**各录一段做兜底：假人没有客户端 ⇒ 录不了像
                    //    （实测上一局自爆段的录制者恰好是假人 ⇒ 一段都没拿到，回放里看不到自爆）。
                    //    多个录制者 ⇒ 我们的计划里就会多出几幕"自爆"（不同视角），上限 4 段。
                    int key = TimeManager.Instance.SurviveTime;
                    float before = SelfDestructBeforeSec?.Value ?? 2f;
                    float after = SelfDestructAfterSec?.Value ?? 1f;
                    int asked = 0;

                    if (!__instance.IsDummy
                        && RequestRecord(__instance, key, "自爆", before, after))
                    {
                        asked++;
                    }
                    else
                    {
                        Plugin.Log.LogWarning($"[HS] EndReplay：被处决者 #{__instance.PublicInfo.PlayerId} "
                            + (__instance.IsDummy ? "是假人" : "没有客户端") + "，自爆段改由目击者录。");
                    }

                    foreach (var witness in room.AlivePlayers.ToList())
                    {
                        if (asked >= 4)
                            break;
                        if (witness?.PublicInfo == null || witness.Session == null
                            || witness.IsSpectator || witness.IsDummy)
                            continue;
                        if (witness.PublicInfo.PlayerId == __instance.PublicInfo.PlayerId)
                            continue;
                        if (RequestRecord(witness, key, "自爆", before, after))
                            asked++;
                    }

                    Plugin.Log.LogInfo($"[HS] EndReplay：自爆片段共请 {asked} 个客户端录制（被处决者 + 存活目击者）。");
                }
                catch (Exception ex)
                {
                    Plugin.Log.LogWarning($"[HS] EndReplay：请求自爆片段失败 — {ex.Message}");
                }
            }
        }

        /// <summary>
        /// 回放期间**必须**让服务端留在 `Survive`（只写字段 `_state`，**不发包**）。
        ///
        /// 两条理由（都读过码）：
        ///   · `GameRoom` 的每秒 tick 里有 `case EGameState.Trial: TrialTick();`，而
        ///     `TrialTick`（守卫 `if (State == Trial)`）会跑 `Trial.Tick()` ——
        ///     **原版裁判状态机接管，并不断广播 `S_TRIAL_STATE{Discuss/VotePhase/VoteResult}`**，
        ///     玩家就会看到「开始讨论 / 投票 / 投票结果」那些界面，而且会把我们的 Replay 覆盖掉
        ///     （我们没走 `TrialManager.Init`，DiscussionSecond 是 0 ⇒ 讨论瞬间过期）。
        ///   · `GameOver()` 末尾是 `if (State == EGameState.Survive) PushAfter(7500, …TotalResult)`
        ///     ⇒ 服务端不在 Survive 时，结算那一步反而不执行。
        ///
        /// 正常情况下服务端一直是 Survive（我们只给客户端发状态包，见 `BeginReplayScreen` 的注释）。
        /// 但原版的**侦探阶段是另一条独立链路**：`DetectiveTick` 每秒把自己 `PushAfter` 回来，
        /// 到点就 `ChangeGameState(EGameState.Trial)` —— 它完全可能在我们回放期间把服务端推进 Trial。
        /// 所以每次拦截结算时都兜一次（黑方全敲完那种局面每秒都会来一次），把它按回去。
        ///
        /// 只写字段而不走 `ChangeGameState`：后者的 setter 会附带广播等动作，
        /// 而客户端的 Trial 视图**必须留着**（回放宿主 `UI_TrialEvent` 就挂在它上面）。
        /// </summary>
        private static void EnsureServerSurvive(GameRoom room)
        {
            try
            {
                if (room == null || room.State == EGameState.Survive)
                    return;

                var field = AccessTools.Field(typeof(GameRoom), "_state");
                if (field == null)
                {
                    Plugin.Log.LogWarning("[HS] EndReplay：找不到 GameRoom._state，无法把服务端按回 Survive。");
                    return;
                }

                Plugin.Log.LogWarning(
                    $"[HS] EndReplay：服务端状态是 {room.State}（回放期间必须是 Survive）——原版裁判状态机会接管并广播"
                    + "讨论/投票/投票结果界面，已按回 Survive（只改字段，不发包）。");
                field.SetValue(room, EGameState.Survive);
            }
            catch (Exception ex)
            {
                Plugin.Log.LogWarning($"[HS] EndReplay：把服务端按回 Survive 失败 — {ex.Message}");
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
                    EnsureServerSurvive(GameRoom.Instance);   // ★ 每秒兜一次：原版裁判状态机不能在回放期间接管
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

                if (!ThresholdMet(room))
                {
                    Plugin.Log.LogInfo("[HS] EndReplay：未达阈值，正常结算。");
                    return false;
                }

                _played = true;
                _continueSettlement = continueSettlement;

                // 白方幸存胜利时，把黑方那一段「最后时段」挪到最后一屏（黑方胜利时不调整）。
                bool blackClipLast = _whiteWin || room.ResultType == EResultType.WhiteWin;
                var plan = BuildPlan(room, blackClipLast);
                if (plan.Count == 0)
                {
                    Plugin.Log.LogWarning("[HS] EndReplay：达到阈值但没有可用片段，正常结算。");
                    _played = false;
                    _continueSettlement = null;
                    return false;
                }

                _inReplay = true;
                EnsureServerSurvive(room);                 // ★ 开局先按一次（正常情况下本来就是 Survive，等于空操作）
                NotifyKnownBlack(room);                    // ★ 让黑方在回放里带红名（"已知黑幕"配色）
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
        ///
        /// <paramref name="blackClipLast"/>＝白方幸存胜利时用：把**黑方**那一段「最后时段」压到最后一屏
        /// （白胜的收尾是"黑方被揭穿"，让黑方的视角收尾才顺）。黑方胜利时不调整。
        /// </summary>
        private static List<Clip> BuildPlan(GameRoom room, bool blackClipLast)
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

            int blackId = blackClipLast ? FindBlackId(room) : 0;
            Clip blackClip = null;
            int lastSeq = 0;

            foreach (var p in room.AlivePlayers.ToList())
            {
                if (plan.Count >= max) break;
                if (p?.PublicInfo == null || p.IsSpectator || p.Session == null)
                    continue;

                // 假人没有客户端 ⇒ 发过去也没人录（实测：上一局「最后时段」只有房主那一台回了磁带，
                // 白方全是假人）。与其排一幕永远等不到磁带的片段，不如直接跳过并记一条日志。
                if (p.IsDummy)
                {
                    Plugin.Log.LogInfo($"[HS] EndReplay：跳过「最后时段」#{p.PublicInfo.PlayerId}（假人没有客户端，录不了像）。");
                    continue;
                }

                // ★ 每一幕「最后时段」必须给**各自独立的 key**（基准值 + 序号）。
                //   读码 + 实测：客户端的播放列表 `RecordManager._playTapes` 是
                //   `Dictionary<int, List<SnapShot>>`，**只以 RecordTime 为键** ——
                //   两个不同录制者用同一个 key 时，后到的那段直接 `_playTapes[key] = tape` **覆盖**前一段
                //   ⇒ 观众只会看到其中一幕（另一段凭空消失，日志里连"无效磁带"都不会打）。
                //   而结算时刻 `SurviveTime` 已经停走（整局结束），所有请求拿到的都是同一个值
                //   （实测日志里「最后时段」与「自爆」的 key 都是 570）⇒ 必须自己加序号区分。
                //   序号按本循环顺序 ⇒ 播放顺序＝我们的排片顺序（白胜时黑方那幕排到最后）。
                //   ⚠「自爆」那一组**故意共用**同一个 key：那是同一时刻的多机位，只该占一幕
                //   （客户端保留最后到达的那一段）—— 别"顺手"把它们也拆开，否则同一个爆炸会播 4 遍。
                int clipKey = key + 1 + lastSeq;
                lastSeq++;

                if (!RequestRecord(p, clipKey, "最后", before, after))
                    continue;

                if (!BySlot.TryGetValue(Slot(p.PublicInfo.PlayerId, clipKey), out var clip))
                    continue;

                if (blackClipLast && blackId > 0 && p.PublicInfo.PlayerId == blackId)
                {
                    blackClip = clip;          // 先扣下，等所有"最后时段"都排完再追加 ⇒ 等于挪到最后
                    continue;
                }

                plan.Add(clip);
            }

            // 「自爆」排在「最后时段」之后：它是整局的收尾（黑方胜利时白方被处决）。
            foreach (var c in Clips.Where(c => c.Kind == "自爆"))
            {
                if (plan.Count >= max) break;
                plan.Add(c);
            }

            if (blackClip != null && plan.Count < max)
            {
                plan.Add(blackClip);
                Plugin.Log.LogInfo("[HS] EndReplay：白方胜利 ⇒ 已把黑方的「最后时段」挪到最后一屏。");
            }

            return plan;
        }

        /// <summary>本局黑方的 id（存活表找不到就去死亡表找 —— 白胜时黑方往往已经死了）。</summary>
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

        /// <summary>
        /// 广播 `S_NOTIFY_BLACK`，把黑方标成客户端的"已知黑幕"（`KnownBlackIds`）⇒ 回放里他的昵称是**红色**。
        ///
        /// 依据：客户端 `Handle_S_NOTIFY_BLACK` 把 id 加进 `KnownBlackIds`（:42496），
        /// 而 `Player.Refresh()` 是 `NameTag.SetNameColor(KnownBlackIds.Contains(PublicInfo.PlayerId))`（:17450），
        /// 红色即 `Color(1f, 0.18f, 0.439f)`（:72362）。
        /// 回放里每个玩家都会被 `ApplySpawn → SetInfo → Refresh` 重设一次 ⇒ 黑方自然带红名；
        /// 返回大厅时客户端自己 `KnownBlackIds.Clear()`（:29075），不会影响下一局。
        /// </summary>
        private static void NotifyKnownBlack(GameRoom room)
        {
            if (!(RevealBlackName?.Value ?? true))
                return;

            try
            {
                int blackId = FindBlackId(room);
                if (blackId <= 0)
                {
                    Plugin.Log.LogWarning("[HS] EndReplay：没找到黑方 id，回放里不会有红名。");
                    return;
                }

                room.Broadcast(new S_NOTIFY_BLACK { PlayerId = blackId });
                Plugin.Log.LogInfo($"[HS] EndReplay：已广播 S_NOTIFY_BLACK #{blackId}（回放里黑方昵称显示为红色）。");
            }
            catch (Exception ex)
            {
                Plugin.Log.LogWarning($"[HS] EndReplay：标记黑幕昵称失败 — {ex.Message}");
            }
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

            int wait = TrialIntroWaitMs?.Value ?? 7000;

            // ★★ 关键一包：`S_FADE_IN`（包 id 1004）—— 客户端处理器就一句
            //        public static void Handle_S_FADE_IN(...) { Managers.UI.EndLoading(); }
            //      （客户端 :42390）
            //
            //  进 Trial 时客户端会先显示 `UI_Loading`（那张「学级裁判 开庭」就是它：
            //  `StartLoading(state)` 把标题设成 `<状态名>Loading`），并播放 TrialEffect 入场演出；
            //  加载页**自己不关自己**（类内没有 EndLoading 调用），关闭它的都是外部调用点，
            //  其中 :42393 就是这条 S_FADE_IN。原版靠它淡入，我们之前从没发过 ⇒
            //  加载页只能等 18 秒兜底（`CompleteWatchdog.Arm(Trial => 18f)`），
            //  而我们的回放早就推下去了 —— 整段回放被加载页盖住。
            //
            //  发这一包后加载页立刻收掉，于是不再需要盲等 18 秒；下面的 wait 只剩
            //  "等入场演出/开场字幕自然播完"（否则 StartReplay 的 ResetSlideVisual 会把它冻住）。
            room.Broadcast(new S_FADE_IN());

            // ⚠ 不要补发 `S_TRIAL_STATE{Discuss}`：客户端还有一个"按镜像状态重算"的驱动器
            //   （`_state = TrialMirror.LatestState` 那段），会把 Discuss 当成**真的进入讨论阶段**
            //   ⇒ 讨论计时走完就推进到 VotePhase，玩家会看到「开始讨论 / 投票」页面（实测就是这么冒出来的）。
            //   开场标题由 TrialEffect（FadeOut 的 Trial 分支）自己演，不需要它。

            Plugin.Log.LogInfo($"[HS] EndReplay：已发 S_FADE_IN 收掉加载页，等 {wait}ms 让入场演出播完再推 Replay。");
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

                // ⚠ 曾经这里先补一包 `S_TRIAL_STATE{VoteResult}`（想借 `EndVoteResult()` 收掉
                //   「投票结果」面板）。实测诊断证明**那个面板本来就是关着的**
                //   （`VoteResult[self=False,hier=False]`），真正常显的是字幕行 `SlideLine/SlideText`
                //   （已由 `ClearSlideText` 解决）⇒ 这一包没有收益，反而让客户端闪一下空白投票页 ⇒ 去掉。

                // 没有磁带的片段：补一段"占位磁带"（客户端走一次转场后跳过），
                // 而不是让这一幕从回放里凭空消失。必须赶在 `Replay` 状态包之前 ——
                // 客户端 `UI_TrialEvent.StartReplay → RecordManager.Play()` 会把 `_playTapes` **快照**进 `_session`，
                // 之后再补就进不了这一轮的播放列表了。
                SendPlaceholders(room, plan);

                room.Broadcast(new S_TRIAL_STATE { State = ETrialState.Replay });
            }
            catch (Exception ex)
            {
                Plugin.Log.LogWarning($"[HS] EndReplay：推送回放失败 — {ex.Message}");
                FinishReplay("推送失败");
            }
        }

        /// <summary>
        /// 给"没收到磁带"的片段补一段**占位磁带** —— 让客户端自己走一遍"空片段"的转场，
        /// 而不是被服务端悄悄删掉（用户明确要求：不能直接服务端跳过）。
        ///
        /// 为什么不能发**空**磁带：客户端 `AddPlayTape` 对 0 帧直接丢弃（`빈 테이프 무시`）；
        /// 为什么必须是 **2 帧**：`BeginTape` 的硬要求是 `Count >= 2 && [0].Type == SpawnShot`，
        /// 不满足会打「무효 … 건너뜀」**静默跳过**（连转场都不播）。满足要求时会正常
        /// "开始 → 立刻播完 → `FinishTape()`" ⇒ 走**转场演出**（噪声）再进下一段 ——
        /// 观众看到的就是"闪过一下、表示这段没录到"。
        ///
        /// 时间戳取一个极大的值：`BeginTape` 把 `_currentTime` 置成第 2 帧的时间戳，
        /// `Update` 于是立刻把这 2 帧执行完；`PrepareDevices(eventTime)` 拿它判断
        /// "临时设备是否在该时刻之后才生成"，给大值 ⇒ 不额外藏任何设备（转场一闪，不露馅）。
        ///
        /// 首帧的 Spawn 必须是**每台客户端缓存里都存在**的玩家：客户端 `BeginTape` 里
        /// `ChangeMyPlayer(_cache[id])` 是直接索引，人不在缓存里会抛。
        /// 取不到就整段跳过（宁缺勿崩）。
        /// </summary>
        private static void SendPlaceholders(GameRoom room, List<Clip> plan)
        {
            if (!(PlaceholderTape?.Value ?? true))
                return;

            int sent = 0;
            foreach (var clip in plan)
            {
                // ★ 优先用房主侧录制器**合成真磁带**：假人与已死者这两类客户端根本交不上磁带；
                //   而「自爆」更是无论客户端有没有交都改用服务端（白方被处决那一刻全是死人，
                //   客户端 `Recording()` 的 `!IsAlive` 守卫直接 return ⇒ 白方主视角录不出来）。
                if ((!clip.Filled || NeedsHostTape(clip)) && TrySendHostTape(room, clip))
                {
                    sent++;
                    continue;
                }

                if (clip.Filled)
                    continue;

                var spawn = ResolvePlaceholderSpawn(clip);
                if (spawn == null)
                {
                    Plugin.Log.LogWarning($"[HS] EndReplay：片段【{clip.Kind}】key={clip.Key} 没收到磁带，"
                        + "又找不到可用的占位主视角 ⇒ 这一段整段跳过（观众看到的是「少了一段」）。");
                    continue;
                }

                float t = 1e6f;
                var pkt = new S_TAPE { RecordTime = clip.Key };
                pkt.SnapShots.Add(new SnapShot
                {
                    Type = ESnapShotType.SpawnShot,
                    TimeStamp = t,
                    Spawn = spawn
                });
                pkt.SnapShots.Add(new SnapShot
                {
                    Type = ESnapShotType.EditShot,
                    TimeStamp = t,
                    Edit = new EditSnapShot { Type = EEditShotType.NormalTimeEdit }
                });

                room.Broadcast(pkt);
                sent++;
                Plugin.Log.LogInfo($"[HS] EndReplay：片段【{clip.Kind}】key={clip.Key} 没收到磁带 ⇒ "
                    + $"补 2 帧占位磁带（主视角=#{spawn.PlayerId}），客户端会走一次转场后跳过这一段。");
            }

            if (sent > 0)
                Plugin.Log.LogInfo($"[HS] EndReplay：共补 {sent} 段（服务端合成 / 占位磁带二选一）。");
        }

        /// <summary>
        /// 这些片段**无论客户端有没有交磁带**都改用服务端录制 —— 它们是"合并剪辑"，
        /// 需要跨视角/跨时刻的素材：自爆是**同一刻的多机位**（白方各有各的死法现场），
        /// 白胜结局（待做）是**幸存者巡礼 + 黑方失败**。
        ///
        /// 为什么不直接用客户端那卷：客户端的录像在他死亡那一刻就停了
        /// （`Recording()` 的 `!IsAlive` 守卫），而且它只收到**自己视野内**的特效帧 ⇒
        /// 远处那些爆炸根本不在它的磁带里。
        /// </summary>
        private static bool NeedsHostTape(Clip clip)
            => clip.Kind == "自爆";

        /// <summary>
        /// 用房主侧缓冲（<see cref="EndReplayHostTape"/>）给这一段合成磁带并广播出去。
        ///
        /// 窗口 = **登记那一刻的房主时钟**（`Clip.At`）± 配置的前后秒数。这一段本来就没有客户端磁带，
        /// 也就没有"磁带内锚点"可用；房主缓冲的采样也在同一时钟上，所以两边自洽。
        ///
        /// 镜头与光照现在给的是稳妥默认值（镜头＝黑方、非断电）；P2/P3 的
        /// "按剧本生成 `AreaShot`（逐人快速平移 + 切黑方时转断电视野）"接手后，这里会换成那条序列。
        /// </summary>
        private static bool TrySendHostTape(GameRoom room, Clip clip)
        {
            try
            {
                float from = clip.At - Math.Max(0f, clip.Before);
                float to = clip.At + Math.Max(0f, clip.After);

                int anchorId = FindHostTapeAnchor(room, clip);
                int camId = FindBlackId(room);

                var shots = new List<SnapShot>();
                int n = EndReplayHostTape.Build(shots, from, to, anchorId, camId, true);
                if (n < 2)
                {
                    Plugin.Log.LogWarning($"[HS] EndReplay：片段【{clip.Kind}】key={clip.Key} 想用服务端录制，"
                        + $"但缓冲里可用帧不足（{n} 帧，窗口=[{from:F2},{to:F2}]）⇒ 退回占位磁带。");
                    return false;
                }

                var pkt = new S_TAPE { RecordTime = clip.Key };
                pkt.SnapShots.AddRange(shots);
                room.Broadcast(pkt);
                clip.Filled = true;

                Plugin.Log.LogInfo($"[HS] EndReplay：片段【{clip.Kind}】key={clip.Key} 由**服务端录制**合成 {n} 帧"
                    + $"（窗口=[{from:F2},{to:F2}] 锚点=#{anchorId} 镜头=#{camId} 非断电）⇒ 假人/已死者也能进回放。");
                return true;
            }
            catch (Exception ex)
            {
                Plugin.Log.LogWarning($"[HS] EndReplay：服务端合成磁带失败 — {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// 服务端合成时的"隐形机位"锚点：挑一个**已死 + 仍在房间 + 本机缓存里还在**的人
        /// （与 <see cref="ResolveObserverId"/> 同一套判据 —— 客户端 `ChangeMyPlayer(_cache[id])`
        /// 是直接索引，人不在缓存里会抛）。挑不到就返回 0，让 <see cref="EndReplayHostTape.Build"/>
        /// 退用镜头目标本人当锚点。
        /// </summary>
        private static int FindHostTapeAnchor(GameRoom room, Clip clip)
        {
            try
            {
                foreach (var p in room.DeadPlayers)
                {
                    int id = p?.PublicInfo?.PlayerId ?? 0;
                    if (id <= 0 || id == clip.RecorderId)
                        continue;
                    if (Managers.Player.GetPlayerCache(id) == null)
                        continue;
                    return id;
                }
            }
            catch (Exception ex)
            {
                Plugin.Log.LogWarning($"[HS] EndReplay：挑服务端录制锚点失败 — {ex.Message}");
            }
            return 0;
        }

        /// <summary>
        /// 占位磁带的首帧主视角：优先用**录制者本人**（他录过像 ⇒ 各方缓存里都有他），
        /// 其次用"已死 + 仍在房间 + 本机缓存里还在"的人（与 <see cref="ResolveObserverId"/> 同一套判据）。
        ///
        /// 一律标成 `IsGhost`（跟随 `ObserverGhost` 配置）：这样客户端的 `RefreshGhostVisual()`
        /// 会把幽灵替身与骨架一起关掉 ⇒ 转场那一瞬间不会突然冒出一个人的身体。
        /// </summary>
        private static PublicPlayerInfo ResolvePlaceholderSpawn(Clip clip)
        {
            try
            {
                var room = GameRoom.Instance;
                if (room == null)
                    return null;

                bool ghost = ObserverGhost?.Value ?? true;

                var own = Managers.Player.GetPlayerCache(clip.RecorderId)?.PublicInfo;
                if (own != null)
                    return MakePlaceholderSpawn(own, clip.RecorderId, ghost);

                foreach (var p in room.DeadPlayers)
                {
                    int id = p?.PublicInfo?.PlayerId ?? 0;
                    if (id <= 0 || id == clip.RecorderId)
                        continue;

                    var cached = Managers.Player.GetPlayerCache(id)?.PublicInfo;
                    if (cached == null)
                        continue;

                    Plugin.Log.LogInfo($"[HS] EndReplay：录制者 #{clip.RecorderId} 已不在本机缓存里，"
                        + $"占位磁带改用已死者 #{id} 当主视角。");
                    return MakePlaceholderSpawn(cached, id, ghost);
                }
            }
            catch (Exception ex)
            {
                Plugin.Log.LogWarning($"[HS] EndReplay：准备占位磁带失败 — {ex.Message}");
            }

            return null;
        }

        private static PublicPlayerInfo MakePlaceholderSpawn(PublicPlayerInfo src, int playerId, bool ghost)
        {
            var info = src.Clone();
            info.PlayerId = playerId;
            info.State = EPlayerState.Idle;   // 别让他"躲在柜子里/死亡幽灵"的状态驱动这个看不见的角色
            info.IsGhost = ghost;
            return info;
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
        /// 客户端侧：记录"回放宿主 UI 到底建没建"，并**清掉字幕行里烘死的默认文字**。
        ///
        /// 为什么必须清（实测诊断定位，见 `.tmps`）：
        ///   `UI_TrialEvent` 的字幕行 `SlideLine/SlideText` 在预制体里**烘了一段默认文字** ——
        ///   官方中文包没有对应条目，所以中文客户端上会直接显示成韩文「논의 시작」。
        ///   原版流程会在 `StartState(Discuss)` 里跑开场字幕，序列收尾时 `slideText.SetActive(false)` 把它收掉；
        ///   而我们的回放**从没调用过 `StartState(Discuss)`**（客户端收到的第一个状态就是 Replay），
        ///   `StartReplay()` 里的 `ResetSlideVisual()` 又把它设成完全不透明
        ///   ⇒ 在"进 Trial → 等入场演出 → 推 Replay"这十几秒里，它一直挂在画面正中（实测截图确认）。
        ///
        ///   清成空串之后：等待期间什么都不显示；随后回放自己的标题卡
        ///   （`StartReplay` 的字幕回调写入 `ReplayTrial`＝「公开真相」）照常出现。
        ///
        /// ⚠ 这一步**只能在客户端侧做** —— 服务端没有任何包能隐藏/清空这个字幕元素
        ///   （我已把 `S_TRIAL_STATE{VoteResult}` 之类的相位手段都试过：那只能收掉 `VoteResult` 面板，
        ///   而诊断显示那个面板本来就是关着的）。开关沿用 `ClearTrialLabels`。
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
                    ClearSlideText(__instance.TrialUI);
                }
                catch (Exception ex)
                {
                    Plugin.Log.LogWarning($"[HS] EndReplay（客户端）：诊断日志失败 — {ex.Message}");
                }
            }
        }

        /// <summary>清掉审判 UI 字幕行（`SlideText`）里烘死的默认文字 —— 见 <see cref="ClientShowTrialUiHook"/> 的说明。</summary>
        private static void ClearSlideText(UnityEngine.Component trialUi)
        {
            try
            {
                if (!(ClearTrialLabels?.Value ?? true) || trialUi == null)
                    return;

                var slide = Util.FindChild<UnityEngine.Transform>(trialUi.gameObject, "SlideText", true);
                if (slide == null)
                {
                    Plugin.Log.LogWarning("[HS] EndReplay（客户端）：找不到字幕行 SlideText，无法清掉烘死的默认文字。");
                    return;
                }

                foreach (var c in slide.GetComponents<UnityEngine.Component>())
                {
                    if (c == null)
                        continue;
                    var p = c.GetType().GetProperty("text");
                    if (p == null || p.PropertyType != typeof(string) || !p.CanWrite)
                        continue;
                    p.SetValue(c, "");
                    Plugin.Log.LogInfo("[HS] EndReplay（客户端）：已清空审判 UI 字幕行烘死的默认文字（那行韩文）。");
                    break;
                }
            }
            catch (Exception ex)
            {
                Plugin.Log.LogWarning($"[HS] EndReplay（客户端）：清字幕行失败 — {ex.Message}");
            }
        }

        /// <summary>
        /// 回放开始时把「黑方就是本机玩家」的那条昵称救回来（原版 `BeginSession` 会关掉它），
        /// 并让它的替身(id=0)显示**红色** —— 观众因此一眼看出「黑刀＝黑幕」。
        ///
        /// 注意这不影响"隐藏观察者的昵称"：那条由 <see cref="HideAnchorNameTag"/> 负责，
        /// 两者作用的对象不同（这里是 id=0 的本机替身，那里是锚点观察者）。
        /// </summary>
        [HarmonyPatch]
        internal static class ReplayNameTagHook
        {
            private static MethodBase Target()
                => AccessTools.Method(AccessTools.TypeByName("UI_TrialEvent"), "StartReplay");

            [HarmonyPrepare]
            private static bool Prepare() => Target() != null;

            [HarmonyTargetMethod]
            private static MethodBase TargetMethod() => Target();

            [HarmonyPostfix]
            private static void Postfix(object __instance)
            {
                try
                {
                    var ui = __instance as UnityEngine.Component;
                    if (ui == null)
                        return;

                    // ★ 黑方就是"你自己"时，原版回放会把自己的昵称关掉
                    //   （`RecordManager.BeginSession`：`MyPlayer.NameTag.gameObject.SetActive(false)`）
                    //   ⇒ 你自己的屏幕上永远看不到那个红名。我们刚广播过 `S_NOTIFY_BLACK`，
                    //   所以只在"我确实是被标记的黑幕"时把它重新打开 —— 对白方玩家没有影响。
                    var my = Managers.Player.MyPlayer;
                    if ((RevealBlackName?.Value ?? true) && my?.PublicInfo != null
                        && Managers.Player.KnownBlackIds.Contains(my.PublicInfo.PlayerId))
                    {
                        // ★ 回放里"你"的可视化身是 **id=0 的替身**（`MyPlayer` 本身被 `HidePlayer(true)` 藏起来了），
                        //   所以要把**那个替身**的昵称设成红名 —— 它默认是白的，因为 `Player.Refresh()` 用
                        //   `KnownBlackIds.Contains(PublicInfo.PlayerId)` 决定颜色，而替身的 id 是 0。
                        //   （实测现象：本机玩家头上"第二幕起有白字"，就是它。）
                        var standIn = Managers.Player.GetPlayerCache(0);
                        if (standIn?.NameTag != null)
                        {
                            standIn.NameTag.gameObject.SetActive(true);
                            standIn.NameTag.SetNameColor(true);
                            Plugin.Log.LogInfo("[HS] EndReplay（客户端）：黑方就是本机玩家 ⇒ 已把回放里"
                                + "『你自己』的替身(id=0)昵称设为红名（之前是白字）。");
                        }
                    }

                }
                catch (Exception ex)
                {
                    Plugin.Log.LogWarning($"[HS][诊断] 审判UI文字扫描失败 — {ex.Message}");
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
                        $"[HS] EndReplay（客户端）：BeginTape 开始（可用磁带 {__instance.PlayTapeCount} 段）/ 是否最后一段={(__instance.IsLastTape ? "是" : "否")}。");
                }
                catch (Exception ex)
                {
                    Plugin.Log.LogWarning($"[HS] EndReplay（客户端）：诊断日志失败 — {ex.Message}");
                }
            }

            /// <summary>
            /// 隐藏观察者的昵称必须**每段都补一刀**：`BeginTape` 末尾的 `ChangeSilhouette(true)`
            /// 只是"顺带"把昵称关掉一次（Player.cs:1954），而每段开头都会
            /// `Despawn(全体) → ApplySpawn(首帧)`，那次 `SetInfo → Refresh()` 又会把它打开。
            ///
            /// 锚点 id 就是 `_blackId`（`BeginTape` 里由首帧 id 折出来的那个）—— 我们的首帧是隐藏观察者，
            /// 所以它＝观察者 id；等于 0 表示走的是原版主视角（本机玩家替身），那条路不碰。
            /// </summary>
            [HarmonyPostfix]
            private static void HideObserverNameTag(RecordManager __instance)
            {
                try
                {
                    int anchor = Traverse.Create(__instance).Field("_blackId").GetValue<int>();
                    HideAnchorNameTag(anchor);
                }
                catch (Exception ex)
                {
                    Plugin.Log.LogWarning($"[HS] EndReplay（客户端）：读取锚点失败 — {ex.Message}");
                }
            }
        }

        /// <summary>
        /// 客户端：把"隐藏观察者"的昵称彻底关掉（实测：回放镜头正中浮着一个「假人4」的 ID）。
        ///
        /// 两步都要做，缺一不可（读码 + 实测）：
        ///   · `BeginTape` 末尾对它调 `ChangeSilhouette(true)`，其中 `NameTag.SetActive(false)`（Player.cs:1954）
        ///     —— 但那是**一次性**的，任何 `ToggleEffects(true)` 都会把它打开；
        ///   · 更要命的是 `Player.Refresh()`（Player.cs:2299）末尾有 `_forceHideNameTag = false`
        ///     并重新 `NameTag.SetInfo(DisplayName)` ⇒ 每段 `Despawn → ApplySpawn → SetInfo → Refresh`
        ///     都会让昵称复活。
        ///   ⇒ 用原版自带的**持久**开关 `SetForceHideNameTag(true)`（`ToggleEffects` 会尊重
        ///     `!\_forceHideNameTag`，Player.cs:1713），再由 `AnchorNameTagHook` 在 `Refresh` 之后补一刀。
        /// </summary>
        private static void HideAnchorNameTag(int anchorId)
        {
            _anchorId = anchorId;
            if (anchorId <= 0)
                return;

            try
            {
                var p = Managers.Player.GetPlayerCache(anchorId);
                if (p?.NameTag == null)
                    return;

                p.SetForceHideNameTag(true);
                p.NameTag.gameObject.SetActive(false);
            }
            catch (Exception ex)
            {
                Plugin.Log.LogWarning($"[HS] EndReplay（客户端）：隐藏观察者昵称失败 — {ex.Message}");
            }
        }

        /// <summary>
        /// 客户端：`Player.Refresh()` 会重置 `_forceHideNameTag`（见 <see cref="HideAnchorNameTag"/>），
        /// 所以在它之后补一刀。只在"回放正在播 + 这个玩家就是当前锚点"时才动手，
        /// 绝不影响正常对局里的昵称显示。
        /// </summary>
        [HarmonyPatch(typeof(Player), nameof(Player.Refresh))]
        internal static class AnchorNameTagHook
        {
            [HarmonyPostfix]
            private static void Postfix(Player __instance)
            {
                try
                {
                    if (_anchorId <= 0 || __instance?.PublicInfo == null)
                        return;
                    if (__instance.PublicInfo.PlayerId != _anchorId)
                        return;
                    if (!Managers.Record.IsPlaying)
                        return;
                    if (__instance.NameTag != null)
                        __instance.NameTag.gameObject.SetActive(false);
                }
                catch
                {
                    // 昵称没藏住不该影响回放，静默。
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

                    // ★ "合并剪辑"类片段（自爆）一律改用服务端录制：客户端那卷在他死亡那一刻就停了，
                    //   而且只含自己视野内的特效帧 ⇒ 直接丢弃它，交给 `TrySendHostTape` 合成。
                    if (NeedsHostTape(clip))
                    {
                        Plugin.Log.LogInfo($"[HS] EndReplay：片段【{clip.Kind}】key={clip.Key} 收到客户端磁带"
                            + $"（{c.SnapShots.Count} 帧）但这一类改用服务端录制 ⇒ 丢弃。");
                        return false;
                    }

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
                    // 记下"这一段真的收到了" —— `BroadcastReplay` 靠它决定要不要补占位磁带。
                    clip.Filled = true;
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
                return StripBombCorpseAdds(tape);
            }

            // 录制者是谁：磁带首帧的 Spawn 就是他（客户端 `BeginTape` 也据此决定镜头跟随谁）。
            var firstSpawn = tape.FirstOrDefault(s => s.Type == ESnapShotType.SpawnShot)?.Spawn;
            if (firstSpawn == null)
            {
                Plugin.Log.LogWarning($"[HS] EndReplay：片段【{clip.Kind}】没有 SpawnShot，按原样播放。");
                return StripBombCorpseAdds(tape);
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
            var src = tape.LastOrDefault(s => s.Type == ESnapShotType.SpawnShot
                                              && s.Spawn != null
                                              && s.Spawn.PlayerId == recorderId
                                              && s.TimeStamp <= start)?.Spawn
                      ?? firstSpawn;

            // ★★ 阶段 2：把合成首帧的角色换成"隐藏观察者"。
            //   客户端 `BeginTape` 的顺序（详见 .tmps/回放-隐藏观察者-崩因分析.md）：
            //       ForceSpawnReplayTemp(MyPlayer.Name);   // ← 造 id=0 的替身（半成品）
            //       Despawn(在场所有人);
            //       _blackId = (首帧.PlayerId != _myPlayerId) ? 首帧.PlayerId : 0;
            //       ChangeMyPlayer(_blackId);              // ← `_cache[id]` 直接索引，不存在就抛
            //       ApplySpawn(首帧);                       // ← 唯一会 SetInfo(PublicPlayerInfo) 的地方
            //       Players[_blackId].ChangeSilhouette(true);   // ← 剪影只打给 _blackId
            //   ⇒ 首帧的 id 同时决定"镜头跟谁 / 剪影打谁 / 谁被装配"，只有一个槽位。
            //     把这个槽位让给一位 IsGhost 的已死玩家：镜头仍锁在他身上，但 `Player.Update` 每帧刷
            //     `RefreshGhostVisual()`，回放期间走 case 2 把幽灵渲染与正常骨架一起关掉 ⇒ 画面上没有他，
            //     剪影也就看不见了；真实玩家（黑方/受害者/旁观者）因此全部本色出场。
            var roster = BuildRoster(tape, start);
            roster[recorderId] = src;      // 录制者自己那批帧是 SurvivalTime 基准，上面会漏掉他，这里显式补上

            int obsId = 0;
            bool useTemp = false;
            if (ObserverCamera?.Value ?? true)
            {
                int cfg = ObserverPlayerId?.Value ?? -1;
                if (cfg == 0)
                    useTemp = true;                                   // 显式要求原版临时玩家（id=0）
                else
                    obsId = ResolveObserverId(tape, roster, recorderId, start, end);
            }

            var spawn = src;
            if (obsId > 0)
            {
                // ★ 必须用**观察者自己的**信息（房主同时也是一台客户端，他的对象就在本机缓存里），
                //   只把位置换成"录制者在窗口起点的位置"（镜头必须落在现场）。
                //
                //   为什么不能图省事复用录制者的信息（第一版就是这么写的，实测出事）：
                //   客户端 `Spawn(info) → SetInfo(info)` 里有一句 `RefreshSkeletonCharacter(info.CharacterId)`，
                //   于是**这个人的角色会被改成录制者的角色**，而且改动留在客户端的 PublicInfo 上 ——
                //   实测表现：结算画面里那个被当观察者的假人，变成了黑幕（录制者）的角色。
                //   录像绝不能影响结算，所以改成"把他自己的信息重设一遍"（几乎是无操作）。
                var own = Managers.Player.GetPlayerCache(obsId)?.PublicInfo;
                spawn = own != null ? own.Clone() : src.Clone();
                spawn.PlayerId = obsId;
                if (src.Pos != null)
                    spawn.Pos = src.Pos;               // 镜头落在录制者窗口起点的位置
                spawn.State = EPlayerState.Idle;       // 别让他的状态（躲柜/死亡幽灵）驱动这个看不见的角色
                if (ObserverGhost?.Value ?? true)
                    spawn.IsGhost = true;              // ★ 回放期间 IsGhost ⇒ 幽灵替身与骨架都被关掉 ⇒ 不可见
            }
            else if (useTemp)
            {
                spawn = src.Clone();
                spawn.PlayerId = 0;                // 原版临时玩家（房主自己的替身）
            }

            var result = new List<SnapShot>(tape.Count + roster.Count + 1)
            {
                new SnapShot
                {
                    Type = ESnapShotType.SpawnShot,
                    TimeStamp = start,
                    Spawn = spawn
                }
            };

            // ★ 阶段 1：补一轮"全员出场帧"（每个玩家各一枚，时间戳＝窗口起点）。
            //   客户端 `BeginTape` 把 `_playIndex` 置 1、`_currentTime` 置第 2 帧的时间戳；这里所有补帧的
            //   时间戳都等于 start ⇒ **第一次 `Update` 就会连着执行完它们**。每台机器都会把自己那一枚认领走
            //   （客户端把 `PlayerId == _myPlayerId` 改写成 0）⇒ 先装配好自己的 0 号替身，再开始演动作。
            //   ⚠ 时间戳必须是 ClientTime 基准（＝ start）。**不要**模仿 `RecordAllType` 里
            //     "isStartShot ⇒ SurvivalTime" 的写法：那种帧会被 `_blackId` 规则跳过、或让
            //     `TimeStamp > _currentTime` 直接 break，等于白补甚至卡住播放。
            int rosterAdded = 0;
            if (RosterFrames?.Value ?? true)
            {
                foreach (var kv in roster)
                {
                    if (kv.Key == obsId)
                        continue;   // 观察者是镜头锚点：他若被自己的出场帧重新摆位，镜头就会从现场被拽走
                    result.Add(new SnapShot
                    {
                        Type = ESnapShotType.SpawnShot,
                        TimeStamp = start,
                        Spawn = kv.Value
                    });
                    rosterAdded++;
                }
            }

            int bombDropped = 0;
            foreach (var s in tape)
            {
                if (s.TimeStamp <= start || s.TimeStamp > end)
                    continue;

                // ⚠⚠ 丢掉**项圈自爆尸体**的 `AddShot` —— 这是原版自身的"二次 SetInfo"缺陷，只能绕开。
                //
                //   客户端 `Corpse.SetInfo` 的 bomb 分支第一句就是
                //       GetComponentInChildren<SkeletonAnimation>().gameObject.SetActive(false);
                //   而 Unity 这个 API **默认跳过未激活对象**：实战里这具尸体已经被"加入"包处理过一次、
                //   骨架已被它自己关掉 ⇒ 回放里**再**执行一次必然取到 null ⇒ 空引用。
                //
                //   实测证据链（`Player.log`）：
                //     `at Corpse.SetInfo (Protocol.DeviceInfo) [0x000dd]` ← `RecordManager.ApplyAdd` ← `Update`
                //   用 Mono.Cecil 反查 0xDD：`callvirt Component::get_gameObject()`，前一条正是
                //   `call GetComponentInChildren<Spine.Unity.SkeletonAnimation>()` ⇒ 与推断完全吻合。
                //
                //   罪魁是 `RecordManager.ApplyAdd`：它对 `Corpse` **无条件**调 `SetInfo`
                //       value.gameObject.SetActive(true); if (value is Corpse) value.SetInfo(...);
                //   ⇒ 只要这枚 Add 帧进了磁带，改内容也救不了：把 `StateList[3]` 清零走"普通尸体"分支，
                //     17589 行同样会 `GetComponentInChildren<SkeletonAnimation>()` ⇒ 一样 null。
                //   （`ModifyShot` 那条路是安全的：`DeviceManager.Modify` 只走 `RefreshState`，不碰 `SetInfo`。）
                //
                //   而 `_playIndex++` 在 `switch` **之后** ⇒ 异常让同一枚帧每帧重试 ⇒ 整段回放卡死
                //   （实测 2942 次异常，第 3 幕没有"结束"行，只能等我们的超时兜底）。
                //
                //   ⇒ 唯一稳的做法：**不发这具尸体的 Add**。观感损失很小 —— 爆炸本身是 `EffectShot`
                //     （`S_PLAY_EFFECT{DyingVfx}` → `ApplyEffect`，路径安全）照常播，只是地上不出现焦尸。
                //
                //   判据用**内容**（`DeviceInfo.StateList[3] != 0` 正是 `_isBombCorpse` 的来源）而不是片段种类：
                //   露娜系白方可能**中途**自爆，那种帧会落进「杀人」窗口，按种类过滤会漏。
                if (s.Type == ESnapShotType.AddShot && IsBombCorpseShot(s))
                {
                    bombDropped++;
                    continue;
                }

                // ★ 保留 EditShot（曾经这里一律 continue 裁掉，结果把"揭晓"也裁没了）：
                //   · SlowTimeEdit  插在击杀−0.3s ⇒ 落在窗口内 ⇒ 慢镜 + 最后一段的 ChangeSilhouette(false) 揭晓
                //   · NormalTimeEdit 插在击杀瞬间  ⇒ 落在窗口内 ⇒ 把 TimeScale/镜头复位
                //   · GlitchEdit     插在击杀+1.5s ⇒ 落在窗口外，会被上面的时间判断自然裁掉
                result.Add(s);
            }

            string who = spawn.PlayerId == 0
                ? "回放临时玩家(id=0)"
                : (spawn.PlayerId == recorderId
                    ? "原版 #" + recorderId
                    : "隐藏观察者#" + spawn.PlayerId + (spawn.IsGhost ? "(Ghost)" : ""));
            string bombNote = bombDropped > 0 ? $"（另丢 {bombDropped} 枚自爆尸体帧）" : "";
            Plugin.Log.LogInfo(
                $"[HS] EndReplay：片段【{clip.Kind}】锚点={anchor.Value:F2} 窗口=[{start:F2},{end:F2}] " +
                $"磁带跨度=[{tape[0].TimeStamp:F2},{tape[tape.Count - 1].TimeStamp:F2}] 留 {result.Count} 帧{bombNote} " +
                $"(出场帧 {rosterAdded}) 主视角={who} 位置=({src.Pos?.X:F0},{src.Pos?.Y:F0})");

            return result;
        }

        /// <summary>
        /// 这枚 `AddShot` 是不是「项圈自爆尸体」。
        ///
        /// 判据就是客户端 `Corpse.SetInfo` 里那句 `_isBombCorpse = info.StateList[3] != 0` ——
        /// 磁带里的 `DeviceInfo` 字节与客户端将要执行的完全一样，所以房主能提前判出来。
        ///
        /// 故意用 `var` 而不写类型名：本程序集里 `DeviceInfo` 有**两个同名类型**
        /// （服务端 `Protocol.DeviceInfo` 与客户端 `DeviceBase.Info` 那一套），写名字容易解析错。
        /// </summary>
        private static bool IsBombCorpseShot(SnapShot s)
        {
            var st = s?.Add?.Device?.StateList;
            return st != null && st.Count > 3 && st[3] != 0;
        }

        /// <summary>
        /// 原样播放的兜底路径也要去掉毒帧（见 <see cref="TrimTape"/> 里那段说明）：
        /// 找不到锚点时"宁长不丢"是这个方法的既定策略，但**毒帧不属于"长"，属于"崩"**。
        /// </summary>
        private static List<SnapShot> StripBombCorpseAdds(List<SnapShot> tape)
        {
            var kept = new List<SnapShot>(tape.Count);
            int dropped = 0;
            foreach (var s in tape)
            {
                if (s.Type == ESnapShotType.AddShot && IsBombCorpseShot(s))
                {
                    dropped++;
                    continue;
                }
                kept.Add(s);
            }
            if (dropped > 0)
                Plugin.Log.LogWarning($"[HS] EndReplay：原样播放的磁带里丢了 {dropped} 枚自爆尸体帧。");
            return kept;
        }

        /// <summary>
        /// 【阶段 1】采集"每个玩家在窗口起点前后的样子"，用来合成一轮全员出场帧。
        ///
        /// ⚠ 实测教训（1.4.0 第一版就错在这里）：**客户端交上来的磁带是它自己先裁过的**，
        /// 里面只有 [窗口起点, 窗口终点] 那些帧 —— 根本没有"窗口起点之前"的帧，
        /// 所以"取 start 之前最后一枚"只会捞到录制者自己 ⇒ 日志里 `出场帧 1`，等于空转。
        /// 正确做法：**每位玩家都取"离 start 最近的那一枚"**（可能略晚于 start，最多差一轮点名的间隔），
        /// 反正合成出来的帧时间戳一律盖成 `start`，位置最多"提前不到 1 秒"。
        /// 用"离 start 的距离"挑还顺带避开录制者那批 SurvivalTime 基准的帧（数值差好几百，必然更远）。
        /// </summary>
        private static Dictionary<int, PublicPlayerInfo> BuildRoster(List<SnapShot> tape, float start)
        {
            var roster = new Dictionary<int, PublicPlayerInfo>();
            var dist = new Dictionary<int, float>();
            foreach (var s in tape)
            {
                if (s.Type != ESnapShotType.SpawnShot || s.Spawn == null)
                    continue;
                int id = s.Spawn.PlayerId;
                float d = global::System.Math.Abs(s.TimeStamp - start);
                if (dist.TryGetValue(id, out var prev) && prev <= d)
                    continue;                      // 已有更近的（并列时保留先遇到的那枚，即更早的）
                dist[id] = d;
                roster[id] = s.Spawn;
            }
            return roster;
        }

        /// <summary>一枚镜头"属于哪个玩家" —— 只取会移动/改变这个角色的那几类（用于查他在这段窗口里有没有自己的镜头）。</summary>
        private static int FramePlayerId(SnapShot s)
        {
            switch (s.Type)
            {
                case ESnapShotType.SpawnShot: return s.Spawn?.PlayerId ?? 0;
                case ESnapShotType.DespawnShot: return s.Despawn;
                case ESnapShotType.RespawnShot: return s.Respawn?.PlayerId ?? 0;
                case ESnapShotType.MoveShot: return s.Move?.PlayerId ?? 0;
                case ESnapShotType.StateShot: return s.State?.PlayerId ?? 0;
                default: return 0;
            }
        }

        /// <summary>
        /// 【阶段 2】挑"隐藏观察者"的 id。任何一条不满足就退回 0（＝原版主视角，绝不会因此崩）。
        ///
        /// · **必须在每台客户端的 `_cache` 里** —— `ChangeMyPlayer` 是 `_cache[id]` 直接索引，
        ///   不存在会抛 `KeyNotFoundException`、整段回放被跳过。判据：**本局还在房间里**
        ///   （`room.DeadPlayers`）⇒ 宿主从没给他发过 S_LEAVE_GAME（那一条会 `_cache.Remove` 并 Destroy）。
        /// · **优先"磁带里出现过的人"** —— 说明录制那台客户端的在场表里有他（更强的证据）。
        ///   ⚠ 但**不能把它当必要条件**：磁带是客户端按窗口裁好的，**已死者通常死在窗口之前、根本不在磁带里**；
        ///   第一版把它当必要条件，结果一个候选都没通过、每段都退回原版（实测 `出场帧 1`、`主视角=原版 #1`）。
        ///   取不到"在磁带里"的候选时，退用"已死 + 仍在房间 + 窗口内无镜头"的人。
        /// · **必须已死亡** —— 死人在窗口内不会再有自己的 Move/State 镜头，否则那些镜头会把镜头焦点拽走。
        /// · **不能是录制者自己** —— 否则 `_blackId` 会被折成 0，等于没换。
        /// </summary>
        private static int ResolveObserverId(
            List<SnapShot> tape, Dictionary<int, PublicPlayerInfo> roster, int recorderId, float start, float end)
        {
            int cfg = ObserverPlayerId?.Value ?? -1;
            if (cfg > 0)
            {
                Plugin.Log.LogInfo($"[HS] EndReplay：按配置指定用隐藏观察者 #{cfg}"
                    + (roster.ContainsKey(cfg) ? "。" : "（⚠ 磁带里没出现过这个 id，客户端可能没有他，请留意）。"));
                return cfg;
            }
            if (cfg == 0)
                return 0;

            try
            {
                var room = GameRoom.Instance;
                if (room == null)
                    return 0;

                // 窗口内"有自己镜头"的人：他的镜头会把镜头焦点拽走 / 让看不见的观察者被动起来
                var busy = new HashSet<int>();
                foreach (var s in tape)
                {
                    if (s.TimeStamp <= start || s.TimeStamp > end)
                        continue;
                    int id = FramePlayerId(s);
                    if (id > 0)
                        busy.Add(id);
                }

                var deadIds = new List<string>();
                int loose = 0;
                foreach (var p in room.DeadPlayers)
                {
                    int id = p?.PublicInfo?.PlayerId ?? 0;
                    if (id <= 0 || id == recorderId)
                        continue;
                    deadIds.Add(id.ToString());
                    if (busy.Contains(id))
                        continue;                       // 窗口内还有他自己的镜头 ⇒ 镜头会被他拽走
                    // ★ 交叉验证：房主同时也是一台客户端。宿主本机的 `_cache` 里还有这个对象
                    //   ⇒ "加入"包发过、且没发过"离开"包（那一条才会 `_cache.Remove` + Destroy）
                    //   ⇒ 其他客户端的 `_cache` 里也还有他，`ChangeMyPlayer` 的 `_cache[id]` 不会抛。
                    if (Managers.Player.GetPlayerCache(id) == null)
                    {
                        Plugin.Log.LogWarning($"[HS] EndReplay：已死者 #{id} 在本机客户端缓存里已不存在，跳过（不能当观察者）。");
                        continue;
                    }
                    if (roster.ContainsKey(id))
                    {
                        Plugin.Log.LogInfo($"[HS] EndReplay：挑到隐藏观察者 #{id}（已死亡、仍在房间、窗口内无自己的镜头）。");
                        return id;
                    }
                    if (loose == 0)
                        loose = id;                     // 备选：已死 + 仍在房间，只是不在本段磁带里
                }
                if (loose > 0)
                {
                    // 已死者通常死在窗口之前 ⇒ 磁带里没有他（磁带是客户端按窗口裁好的，实测第一版就是
                    // 因为把"必须在磁带里"当成必要条件，一个候选都没通过、每段都退回原版）。
                    Plugin.Log.LogWarning(
                        $"[HS] EndReplay：本段磁带里没有可用的已死者，改用仍在房间、且本机缓存里还在的已死亡玩家 #{loose} 当观察者。");
                    return loose;
                }

                // 兜底：房间里仍在的人（活人几乎必然在窗口内有镜头，所以这条通常为空）
                foreach (var kv in roster)
                {
                    int id = kv.Key;
                    if (id <= 0 || id == recorderId || busy.Contains(id))
                        continue;
                    if (Managers.Player.GetPlayerCache(id) == null)
                        continue;
                    if (!room.Players.Any(p => p?.PublicInfo != null && p.PublicInfo.PlayerId == id))
                        continue;
                    Plugin.Log.LogWarning($"[HS] EndReplay：没有已死亡的候选，退而用仍在房间的 #{id} 当观察者。");
                    return id;
                }

                Plugin.Log.LogWarning(
                    $"[HS] EndReplay：找不到合适的隐藏观察者，本段按原版主视角播（剪影会打在录制者身上）。" +
                    $"诊断：已死者=[{string.Join(",", deadIds)}] 窗口内有镜头的=[{string.Join(",", busy)}] " +
                    $"磁带里有出场帧的=[{string.Join(",", roster.Keys)}] 录制者=#{recorderId}");
            }
            catch (Exception ex)
            {
                Plugin.Log.LogWarning($"[HS] EndReplay：挑观察者失败，本段按原版主视角播 — {ex.Message}");
            }

            return 0;
        }
    }
}
