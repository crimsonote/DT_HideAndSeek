using System;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using Protocol;
using Server.Game;
using GamePlayer = Server.Game.Player;

namespace HideAndSeek.Features.Replay
{
    /// <summary>
    /// 【编排】—— 从"结算被请求"到"回放放完、继续结算"的完整流程。
    ///
    /// 与旧实现的对应关系（逐个替换掉的东西）：
    ///   `TrimTape` + `ComposeHostTape`          →  <see cref="TapeAssembler.Assemble"/>（唯一出口）
    ///   `FindGhostStandIn` + `ResolveObserverId`→  <see cref="SilhouetteResolver"/>
    ///   `EndReplayHostTape.Build`               →  <see cref="HostSynth"/>
    ///   `SendPlaceholders` 里两套兜底           →  一套（合成不成再占位）
    ///   散落的静态字段（`_played`/`_inReplay`/`_continueSettlement`…）→ <see cref="Phase"/>
    ///
    /// 流程（每一步都能在日志里看到）：
    ///   ① 结算被请求 ⇒ 拦下（返回 true），启动
    ///   ② 逐幕发 `S_RECORD_REPLAY`（登记时已发）+ `S_REQUEST_TAPE`
    ///   ③ 等 TapeWaitMs；期间客户端回 `C_TAPE` ⇒ 逐段装配 + 广播
    ///   ④ 让客户端进 Trial（**只广播，不改服务端 State**），等它过渡完
    ///   ⑤ 发 `S_FADE_IN` 收掉加载页，等入场演出播完
    ///   ⑥ 给没收到磁带的幕补"合成/占位"，然后推 `S_TRIAL_STATE{Replay}`
    ///   ⑦ 等客户端回执（或超时）⇒ 继续原本的结算
    /// </summary>
    internal static class ReplayDirector
    {
        /// <summary>当前阶段 —— **一处**就能看清流程走到哪了。</summary>
        internal enum Phase
        {
            Idle = 0,
            /// <summary>已决定要播，正在索要素材。</summary>
            Preparing = 1,
            /// <summary>素材已发完，正在等客户端进回放画面。</summary>
            Screen = 2,
            /// <summary>回放正在播（等客户端回执）。</summary>
            Playing = 3,
            /// <summary>已收尾。</summary>
            Done = 4,
        }

        internal static Phase Current { get; private set; } = Phase.Idle;

        private sealed class Planned
        {
            public Act Act;
            public List<SnapShot> Tape;
            public bool Broadcast;
        }

        private static readonly Dictionary<long, Planned> Pending = new Dictionary<long, Planned>();
        private static readonly List<Planned> All = new List<Planned>();
        private static Action _continueSettlement;
        private static GameRoom _room;
        private static int _sent, _got, _synth, _placeholder;

        private static long Slot(int recorderId, int key) => ((long)recorderId << 32) | (uint)key;

        public static void Reset()
        {
            Current = Phase.Idle;
            Pending.Clear();
            All.Clear();
            _continueSettlement = null;
            _room = null;
            _sent = _got = _synth = _placeholder = 0;
        }

        // ── ① 拦截结算 ──────────────────────────────────────────────────

        /// <summary>
        /// 结算被请求时调用。返回 <c>true</c> = 已拦下（结算延后，稍后由本类续上）。
        /// 任何异常都吞掉并放行 —— 回放出事绝不能把对局卡在结算之前。
        /// </summary>
        public static bool OnSettlementRequested(List<Act> acts, string ending,
            int murderDeaths, int whiteCount, Action continueSettlement)
        {
            try
            {
                // ★ 回放序列进行中：之后进来的每一次结算都必须继续拦下。
                //   `BlackWinFeature.TryTrigger` 挂在每秒的 `SurvivalTick` 上，拦下第一次之后
                //   它下一 tick 还会再调一次；若这里放行，真正的结算会在回放没播完时跑掉。
                // ★ 回放序列进行中：之后进来的每一次结算都必须继续拦下（`BlackWinFeature.TryTrigger`
                //   挂在每秒的 SurvivalTick 上，拦下第一次之后还会再来）。
                //   ⚠ **但 `Done` 必须放行**：`Finish()` 已经调过 `ContinueSettlement()`，
                //   而它内部正是调 `ChangeGameState(TotalResult)`；若这里还拦，结算就永远不会发生
                //   —— 实测症状：日志里刷"继续拦截结算"，游戏卡在回放结束之后，只能大退。
                if (Current == Phase.Preparing || Current == Phase.Screen || Current == Phase.Playing)
                {
                    Plugin.Log.LogInfo($"[HS-Replay]（{ending}）回放进行中（{Current}），继续拦截结算。");
                    EnsureServerSurvive();
                    return true;
                }
                if (Current == Phase.Done)
                    return false;

                var room = GameRoom.Instance;
                if (room == null || room.State != EGameState.Survive)
                    return false;

                int need = ReplayFeature.DeathRatioPercent?.Value ?? 30;
                Plugin.Log.LogInfo($"[HS-Replay]（{ending}）刀杀死亡 {murderDeaths} 人 / 白方 {whiteCount} 人，阈值 {need}%。");
                if (murderDeaths <= 0 || whiteCount <= 0 || murderDeaths * 100 <= whiteCount * need)
                {
                    Plugin.Log.LogInfo("[HS-Replay] 未达阈值，正常结算。");
                    return false;
                }

                _room = room;
                _continueSettlement = continueSettlement;

                // 结算类的幕（"最后时段"）在这里补登记 —— 它们的事件时刻就是"结算被拦下"这一刻。
                ReplayFeature.RegisterSettlementActs();

                All.Clear();
                foreach (var a in acts)
                    All.Add(new Planned { Act = a });

                // ★ 排片顺序**就是**播放顺序（客户端 `_playTapes` 是字典，按 key 排序播）。
                //
                // ★★ 这里**只按 key 排序，绝不重分配 key** ——
                //   key 是在**事件发生那一刻**分配并发给客户端的（见 `ReplayFeature.HitHook` / `Add`），
                //   客户端靠它在 9 秒后把"事件周围的录制缓冲"拍成**持久快照**。
                //   一旦在这里改 key，客户端手里那把就失效了，索取只会拿回"当前 14 秒缓冲"
                //   —— 早期事件早已滚出缓冲区。实测症状："3 次刀杀只播了 1 次"。
                //
                //   而 key 既然按事件时刻单调递增，播放顺序**自然**就是事件顺序，
                //   所以不再需要按 (幕类型, 事件时刻) 重排。
                All.Sort(delegate (Planned x, Planned y) { return x.Act.Key.CompareTo(y.Act.Key); });

                int max = ReplayFeature.MaxClips?.Value ?? 12;
                if (All.Count > max)
                {
                    Plugin.Log.LogWarning($"[HS-Replay] 排片 {All.Count} 幕超过 MaxClips={max}，截断到 {max} 幕。");
                    All.RemoveRange(max, All.Count - max);
                }
                if (All.Count == 0)
                {
                    Plugin.Log.LogInfo("[HS-Replay] 达到阈值但没有可用片段，正常结算。");
                    return false;
                }

                Current = Phase.Preparing;
                EnsureServerSurvive();
                NotifyKnownBlack(room);

                Plugin.Log.LogInfo($"[HS-Replay] 开始回放，共 {All.Count} 幕：{DescribePlan()}");

                // ② 索要素材（每 key 单发 —— 一次塞多个 key 会让客户端按它自己的硬编码窗口裁，裁出空带）
                FetchTapes(room);

                // ③ 等一会儿再进回放画面（客户端收到请求即回包，给一点余量）
                int wait = ReplayFeature.TapeWaitMs?.Value ?? 2500;
                room.PushAfter(wait, BeginReplayScreen);
                return true;
            }
            catch (Exception ex)
            {
                Plugin.Log.LogWarning($"[HS-Replay] 准备回放失败，正常结算 — {ex.Message}");
                Current = Phase.Idle;
                return false;
            }
        }

        private static string DescribePlan()
        {
            var parts = new List<string>(All.Count);
            foreach (var p in All)
                parts.Add($"{ActTable.Name(p.Act.Kind)}{p.Act.Key}");
            return string.Join(" → ", parts);
        }

        // ── ② 索要素材 ──────────────────────────────────────────────────

        private static void FetchTapes(GameRoom room)
        {
            Pending.Clear();
            _sent = 0;
            foreach (var p in All)
            {
                var act = p.Act;
                var player = FindPlayer(room, act.RecorderId);
                if (act.RecorderId <= 0 || player?.Session == null)
                    continue;   // 假人/已退出：没有客户端可问，稍后走服务端合成

                // ⚠ **不要在这里补发 `S_RECORD_REPLAY`**。
                //   它必须在**事件发生时就发出去**，客户端才会在 9 秒后把"事件周围的缓冲"拍成
                //   持久快照（见 `ReplayFeature.Add` / `HitHook`）。拖到索取前发，客户端只会回
                //   "当前 14 秒缓冲" ⇒ 早期事件（几十秒前的那次刀杀）根本不在里面。
                Pending[Slot(act.RecorderId, act.Key)] = p;
                player.Session.Send(new S_REQUEST_TAPE { RecordTime = act.Key });
                _sent++;
            }
            Plugin.Log.LogInfo($"[HS-Replay] 已向客户端逐个索取 {_sent} 段磁带"
                + $"（每 key 单发 ⇒ 客户端回原始缓冲、由我们按绝对窗口裁）；"
                + $"其余 {All.Count - _sent} 幕没有客户端可用 ⇒ 走服务端合成。");
        }

        /// <summary>
        /// 收到一段客户端磁带（由 <see cref="ReplayFeature"/> 的 `TapeHook` 调用）。
        /// 返回 true = 这段是我们登记的、已处理（调用方不要交还原版）。
        /// </summary>
        public static bool OnTape(int recorderId, int key, List<SnapShot> raw)
        {
            long slot = Slot(recorderId, key);
            if (!Pending.TryGetValue(slot, out var plan))
                return false;

            Pending.Remove(slot);
            _got++;

            try
            {
                var act = plan.Act;

                // 诊断：**首帧位置到底取自哪一帧** —— 它来自房主侧采样（`HostRecorder.At`），
                // 而那是"≤ 窗口起点的最近一帧"，可能会兜底到很早的帧。打出来才能判定
                // "首帧位置为什么与磁带里的事实不符"（实测：拿刀幕首帧 (8149,3563)，
                // 而磁带里那一刻他在 (8378,4925)，差 1316 单位 ⇒ 角色会猛地闪一下）。
                DumpHeadPos(act, raw);

                // 诊断：原始磁带里到底有**谁**的 SpawnShot、各多少枚、时间戳范围。
                // 用来区分两种"画面里没人"：① 磁带里真的没有别人（录制侧就没录到）；
                // ② 磁带里有、但被后面的某一层过滤掉了。
                DumpSpawnCensus(raw, act);

                // "谁在画面里" —— 直接读**客户端真实磁带**里的 SpawnShot（那就是 AOI 的真实结果）。
                var visibleIds = SceneIds(act, raw);
                // 位置取自房主侧采样：录制者自己的帧是 SurvivalTime 基准，从磁带取会拿到几秒前的位置。
                var visibleInfos = VisibleInfos(visibleIds, act.Window.From);
                var sil = SilhouetteResolver.Resolve(act.SubjectId, visibleIds, act.Window.From);
                act.SilhouetteId = sil.Id;

                var head = BuildHead(act, sil.Id);
                var tape = TapeAssembler.Assemble(act, raw, head, visibleInfos, out var rep);

                if (tape == null)
                {
                    Plugin.Log.LogWarning($"[HS-Replay] {rep.Line()}");
                }
                else
                {
                    plan.Tape = tape;
                    Plugin.Log.LogInfo($"[HS-Replay] {rep.Line()} 密度={rep.FramesOut / Math.Max(0.01f, act.Window.Length):F1}帧/秒"
                        + $"（客户端给了 {raw.Count} 帧）");
                    if (ReplayFeature.DumpTapes?.Value ?? false)
                        TapeDump.Save(recorderId, key, ActTable.Name(act.Kind),
                            act.Window.From, 0f, act.Window.Length, raw, tape, sil.ToString());
                }
                return true;
            }
            catch (Exception ex)
            {
                Plugin.Log.LogWarning($"[HS-Replay] 装配磁带失败 — {ex.Message}");
                return true;
            }
        }

        // ── ④ 让客户端进回放画面 ────────────────────────────────────────

        private static void BeginReplayScreen()
        {
            try
            {
                var room = GameRoom.Instance;
                if (room == null)
                {
                    Finish("房间已不存在");
                    return;
                }
                _room = room;

                // ★ 刻意**只广播状态包给客户端，不动服务端 State**：
                //   走 `room.ChangeGameState(Trial)` 会把服务端也置成 Trial，于是
                //     (a) `GameRoom.TrialTick`（`if (State == Trial)`）开始每秒跑 `Trial.Tick()`，
                //         原版裁判状态机接管并不断广播讨论/投票界面，把我们的 Replay 覆盖掉；
                //     (b) `GameOver()` 末尾是 `if (State == Survive) PushAfter(7500, …TotalResult)`
                //         ⇒ 服务端不在 Survive，结算那一步反而不执行。
                //   只让客户端进 Trial 就同时避开这两条。
                Plugin.Log.LogInfo($"[HS-Replay] 先让客户端建立回放 UI（{All.Count} 幕）。");
                room.WaitCompletePacket(WaitIntroThenReplay, room.CompleteWaitCount(), 8000, 1500);
                room.Broadcast(new S_CHANGE_GAME_STATE { State = EGameState.Trial });
            }
            catch (Exception ex)
            {
                Plugin.Log.LogWarning($"[HS-Replay] 进入回放失败 — {ex.Message}");
                Finish("进入失败");
            }
        }

        /// <summary>
        /// 等审判 UI 的**开场演出**播完再推 Replay。
        ///
        /// ★ 不看客户端源码想不到：`StartReplay()` 第一件事是 `ResetSlideVisual()`，它是
        ///   `_slideSequence.Kill()` + 把文字设成完全不透明；而开场 `SlidingText()` 的收尾
        ///   `slideText.SetActive(false)` 挂在序列 OnComplete 上 ⇒ 序列被 Kill 之后永不执行
        ///   ⇒ 开场文字被冻在画面上压在整段回放之上。
        ///   开场时长（源码常量）：`SlidingText` 0.5+1+0.5 ≈ 2.0s，随后 `AlertMessage` 0.5+3.5+0.5 ≈ 4.5s，
        ///   合计约 6.5s ⇒ 默认等 7 秒。
        ///
        /// 另外必须发 `S_FADE_IN`：客户端进 Trial 会先显示 `UI_Loading`，而它**自己不关自己**，
        /// 关闭它的都是外部调用点，其中一条就是这个包；不发就要等 18 秒的兜底 watchdog，
        /// 而那时回放早被盖住了。
        /// </summary>
        private static void WaitIntroThenReplay()
        {
            var room = GameRoom.Instance;
            if (room == null)
            {
                Finish("房间已不存在");
                return;
            }
            _room = room;

            room.Broadcast(new S_FADE_IN());

            int wait = ReplayFeature.TrialIntroWaitMs?.Value ?? 7000;
            Plugin.Log.LogInfo($"[HS-Replay] 已发 S_FADE_IN 收掉加载页，等 {wait}ms 让入场演出播完再推 Replay。");
            room.PushAfter(wait, BroadcastReplay);
        }

        // ── ⑥⑦ 推 Replay 并等它放完 ────────────────────────────────────

        private static void BroadcastReplay()
        {
            try
            {
                var room = GameRoom.Instance;
                if (room == null)
                {
                    Finish("房间已不存在");
                    return;
                }
                _room = room;
                Current = Phase.Playing;

                FillMissing(room);

                int budget = EstimateBudgetMs();
                Plugin.Log.LogInfo($"[HS-Replay] 广播 Replay 状态：已有磁带 {_got} 段 / 服务端合成 {_synth} 段 / "
                    + $"占位 {_placeholder} 段，预算 {budget}ms。");

                // 客户端放完会在 `RecordManager.Stop()` 里 `CompleteAndSend()`；第一个回执到达后再宽限一会儿。
                room.WaitCompletePacket(() => Finish("客户端回执/超时"), room.CompleteWaitCount(), budget, 4000);
                room.Broadcast(new S_TRIAL_STATE { State = ETrialState.Replay });
            }
            catch (Exception ex)
            {
                Plugin.Log.LogWarning($"[HS-Replay] 推送回放失败 — {ex.Message}");
                Finish("推送失败");
            }
        }

        /// <summary>
        /// 给"没等到客户端磁带"的幕补素材。
        ///
        /// ⚠ 必须赶在 `Replay` 状态包**之前** —— 客户端 `StartReplay → RecordManager.Play()`
        ///   会把 `_playTapes` **快照**进 `_session`，之后再补就进不了这一轮的播放列表。
        ///
        /// 顺序：**优先服务端合成**（有真实采样 ⇒ 画面是真的），合不上再补 2 帧占位磁带
        /// （让客户端走一次"这段没录到"的转场，而不是让这一幕凭空消失）。
        /// </summary>
        private static void FillMissing(GameRoom room)
        {
            foreach (var plan in All)
            {
                if (plan.Tape != null)
                    continue;

                var act = plan.Act;
                if (TrySynth(act, out var tape))
                {
                    plan.Tape = tape;
                    _synth++;
                    continue;
                }

                if (!(ReplayFeature.PlaceholderTape?.Value ?? true))
                    continue;

                var head = BuildHead(act, PickPlaceholderId(act));
                var ph = TapeAssembler.Placeholder(act.Key, head);
                if (ph == null)
                {
                    Plugin.Log.LogWarning($"[HS-Replay] 【{ActTable.Name(act.Kind)}#{act.Key}】没素材、又找不到占位主视角 ⇒ "
                        + "这一段整段跳过（观众看到的是「少了一段」）。");
                    continue;
                }
                plan.Tape = ph;
                _placeholder++;
                Plugin.Log.LogInfo($"[HS-Replay] 【{ActTable.Name(act.Kind)}#{act.Key}】合不上 ⇒ 补 2 帧占位磁带"
                    + $"（主视角=#{head.PlayerId}），客户端会走一次转场后跳过这一段。");
            }

            // ★ **统一在这里广播**（而不是边补边发）：
            //   · 顺序 = 幕序，与客户端按 key 排序播放的结果一致；
            //   · 全部在 `S_TRIAL_STATE{Replay}` 之前 —— 客户端 `StartReplay → RecordManager.Play()`
            //     会把 `_playTapes` **快照**进 `_session`，之后再补就进不了这一轮的播放列表。
            int sent = 0;
            foreach (var plan in All)
            {
                if (plan.Tape == null)
                    continue;
                Broadcast(room, plan.Act.Key, plan.Tape);
                sent++;
            }
            Plugin.Log.LogInfo($"[HS-Replay] 已广播 {sent} 段磁带（客户端回传 {_got} / 服务端合成 {_synth} / 占位 {_placeholder}）。");
        }

        private static bool TrySynth(Act act, out List<SnapShot> tape)
        {
            tape = null;
            try
            {
                // 服务端这条路上没有 AOI 信息 ⇒ "谁在画面里"由我们自己铺的帧决定
                // （`HostSynth` 会在窗口起点给每个有采样的人各发一枚）。
                bool dark = act.Kind == ActKind.BlackTail;
                var frames = HostSynth.Frames(act.Window, act.SubjectId, dark, ReplayFeature.BombBlackout?.Value ?? false);
                if (frames.Count == 0)
                {
                    Plugin.Log.LogInfo($"[HS-Replay] 【{ActTable.Name(act.Kind)}#{act.Key}】"
                        + $"缓冲里没有可用帧（窗口={act.Window}）⇒ 不合成。");
                    return false;
                }

                var visibleIds = SceneIds(act, frames);
                var visibleInfos = VisibleInfos(visibleIds, act.Window.From);
                var sil = SilhouetteResolver.Resolve(act.SubjectId, visibleIds, act.Window.From);
                act.SilhouetteId = sil.Id;

                var head = BuildHead(act, sil.Id);
                if (head == null)
                {
                    Plugin.Log.LogInfo($"[HS-Replay] 【{ActTable.Name(act.Kind)}#{act.Key}】拿不到首帧信息 ⇒ 不合成。");
                    return false;
                }

                tape = TapeAssembler.Assemble(act, frames, head, visibleInfos, out var rep);
                if (tape == null)
                {
                    Plugin.Log.LogInfo($"[HS-Replay] {rep.Line()}");
                    return false;
                }
                Plugin.Log.LogInfo($"[HS-Replay] {rep.Line()}"
                    + $" 密度={rep.FramesOut / Math.Max(0.01f, act.Window.Length):F1}帧/秒（服务端合成）");
                return true;
            }
            catch (Exception ex)
            {
                Plugin.Log.LogWarning($"[HS-Replay] 服务端合成失败 — {ex.Message}");
                return false;
            }
        }

        private static void Broadcast(GameRoom room, int key, List<SnapShot> tape)
        {
            var pkt = new S_TAPE { RecordTime = key, TapeCount = TrialManager.Instance?.TapeCount ?? 1 };
            pkt.SnapShots.AddRange(tape);
            room.Push(delegate
            {
                room.Broadcast(pkt);
                TrialManager.Instance?.SetTape(key, tape.Count);
            });
        }

        // ── ⑦ 收尾 ──────────────────────────────────────────────────────

        private static void Finish(string why)
        {
            if (Current == Phase.Done)
                return;
            Current = Phase.Done;

            var cont = _continueSettlement;
            _continueSettlement = null;

            Plugin.Log.LogInfo($"[HS-Replay] 回放结束（{why}）：客户端磁带 {_got}/{_sent} 段，"
                + $"服务端合成 {_synth} 段，占位 {_placeholder} 段，继续结算。");

            try
            {
                cont?.Invoke();
            }
            catch (Exception ex)
            {
                Plugin.Log.LogWarning($"[HS-Replay] 继续结算失败 — {ex.Message}");
            }
        }

        // ── 工具 ────────────────────────────────────────────────────────

        /// <summary>
        /// 回放期间**必须**让服务端留在 `Survive`（只写字段 `_state`，**不发包**）。
        /// 两条理由：`TrialTick` 会接管状态机并广播讨论/投票界面；而 `GameOver` 末尾的
        /// `if (State == Survive) PushAfter(7500, …TotalResult)` 要求它在 Survive。
        /// 客户端的 Trial 视图必须留着（回放宿主 `UI_TrialEvent` 挂在它上面）⇒ 所以只改字段。
        /// </summary>
        private static void EnsureServerSurvive()
        {
            try
            {
                var room = GameRoom.Instance;
                if (room == null || room.State == EGameState.Survive)
                    return;

                var field = AccessTools.Field(typeof(GameRoom), "_state");
                if (field == null)
                {
                    Plugin.Log.LogWarning("[HS-Replay] 找不到 GameRoom._state，无法把服务端按回 Survive。");
                    return;
                }
                Plugin.Log.LogWarning($"[HS-Replay] 服务端状态是 {room.State}（回放期间必须是 Survive）——"
                    + "原版裁判状态机会接管并广播讨论/投票界面，已按回 Survive（只改字段，不发包）。");
                field.SetValue(room, EGameState.Survive);
            }
            catch (Exception ex)
            {
                Plugin.Log.LogWarning($"[HS-Replay] 把服务端按回 Survive 失败 — {ex.Message}");
            }
        }

        /// <summary>广播 `S_NOTIFY_BLACK` 把黑方标成客户端的"已知黑幕" ⇒ 昵称显示为红色。</summary>
        private static void NotifyKnownBlack(GameRoom room)
        {
            if (!(ReplayFeature.RevealBlackName?.Value ?? true))
                return;
            try
            {
                int blackId = FindBlackId(room);
                if (blackId <= 0)
                {
                    Plugin.Log.LogWarning("[HS-Replay] 没找到黑方 id，回放里不会有红名。");
                    return;
                }
                room.Broadcast(new S_NOTIFY_BLACK { PlayerId = blackId });
                Plugin.Log.LogInfo($"[HS-Replay] 已广播 S_NOTIFY_BLACK #{blackId}（回放里黑方昵称显示为红色）。");
            }
            catch (Exception ex)
            {
                Plugin.Log.LogWarning($"[HS-Replay] 标记黑幕昵称失败 — {ex.Message}");
            }
        }

        /// <summary>
        /// 诊断：**首帧位置取自哪一帧**。
        ///
        /// 首帧位置来自 `HostRecorder.At(subjectId, 窗口起点)`，即"房主侧采样里 ≤ 起点的那一帧"。
        /// 而 `AtOrBefore` 在"起点早于所有帧"时会**兜底返回最早的一帧** ⇒ 那时位置可以差出很远。
        /// 这条日志把"取到的帧自己的时间戳"和"磁带里同一时刻主角在哪"并列，
        /// 一眼就能看出是**采样太旧**、**兜底触发**，还是**两侧数据本身不一致**。
        /// </summary>
        private static void DumpHeadPos(Act act, List<SnapShot> frames)
        {
            try
            {
                var info = HostRecorder.At(act.SubjectId, act.Window.From, out _, out float atTime);
                if (info?.Pos == null)
                {
                    Plugin.Log.LogWarning($"[HS-Replay/诊断] 【{ActTable.Name(act.Kind)}#{act.Key}】"
                        + $"主角 #{act.SubjectId} 在房主采样里取不到位置（At({act.Window.From:F2}) = null）");
                    return;
                }

                float dx = 0f, dy = 0f;
                string tapeSide = "；磁带里找不到该 id 的帧";
                var nearest = NearestFrame(frames, act.SubjectId, act.Window.From);
                if (nearest != null && nearest.Spawn != null && nearest.Spawn.Pos != null)
                {
                    dx = nearest.Spawn.Pos.X - info.Pos.X;
                    dy = nearest.Spawn.Pos.Y - info.Pos.Y;
                    tapeSide = $"；磁带里最近的 #{act.SubjectId} 帧 t={nearest.TimeStamp:F2}"
                        + $" pos=({nearest.Spawn.Pos.X:F0},{nearest.Spawn.Pos.Y:F0})";
                }

                Plugin.Log.LogInfo($"[HS-Replay/诊断] 【{ActTable.Name(act.Kind)}#{act.Key}】"
                    + $"首帧位置 = 房主采样 ({info.Pos.X:F0},{info.Pos.Y:F0})，取自 **t={atTime:F2}**"
                    + $"（窗口起点 {act.Window.From:F2}，相差 {act.Window.From - atTime:F2}s）"
                    + tapeSide
                    + $"；两侧相距 {Math.Sqrt(dx * dx + dy * dy):F0} 单位");
            }
            catch (Exception ex)
            {
                Plugin.Log.LogWarning($"[HS-Replay/诊断] 首帧位置诊断失败 — {ex.Message}");
            }
        }

        /// <summary>磁带里离 `t` 最近的一枚该 id 的出场帧（诊断对照用）。</summary>
        private static SnapShot NearestFrame(List<SnapShot> frames, int id, float t)
        {
            SnapShot best = null;
            float bestD = float.MaxValue;
            if (frames == null)
                return null;
            foreach (var s in frames)
            {
                if (s?.Type != ESnapShotType.SpawnShot || s.Spawn == null || s.Spawn.PlayerId != id)
                    continue;
                float d = Math.Abs(s.TimeStamp - t);
                if (d < bestD)
                {
                    bestD = d;
                    best = s;
                }
            }
            return best;
        }

        /// <summary>
        /// 诊断：原始磁带里每个人有多少枚 `SpawnShot`、时间戳范围如何。
        ///
        /// 为什么需要它：用户反馈"第 2 刀之后受害者不在画面里（像对空气挥刀）"，而 roster 只有 1 人。
        /// 这条日志能区分三种原因：
        ///   ① 磁带里**根本没有别人**的 SpawnShot ⇒ 录制侧就没录到（AOI/despawn）；
        ///   ② 有，但**都在窗口之外**（`VisibleIn` 取"离窗口起点最近"的那一枚）；
        ///   ③ 有且在窗口内 ⇒ 那就是下游某层把它过滤掉了。
        /// </summary>
        private static void DumpSpawnCensus(List<SnapShot> frames, Act act)
        {
            try
            {
                if (frames == null)
                    return;
                var stat = new Dictionary<int, int>();
                var lo = new Dictionary<int, float>();
                var hi = new Dictionary<int, float>();
                foreach (var s in frames)
                {
                    if (s?.Type != ESnapShotType.SpawnShot || s.Spawn == null)
                        continue;
                    int id = s.Spawn.PlayerId;
                    if (id <= 0)
                        continue;
                    stat[id] = stat.TryGetValue(id, out int c) ? c + 1 : 1;
                    float t = s.TimeStamp;
                    if (!lo.TryGetValue(id, out float a) || t < a) lo[id] = t;
                    if (!hi.TryGetValue(id, out float b) || t > b) hi[id] = t;
                }

                var sb = new global::System.Text.StringBuilder();
                foreach (var kv in stat)
                {
                    if (sb.Length > 0)
                        sb.Append(" | ");
                    sb.Append('#').Append(kv.Key).Append('×').Append(kv.Value)
                      .Append('[').Append(lo[kv.Key].ToString("F1")).Append(',')
                      .Append(hi[kv.Key].ToString("F1")).Append(']')
                      .Append(kv.Key == act.SubjectId ? "(主角)" : "");
                }
                Plugin.Log.LogInfo($"[HS-Replay] 【{ActTable.Name(act.Kind)}#{act.Key}】"
                    + $"原始 {frames.Count} 帧里的 SpawnShot：{(sb.Length == 0 ? "（一枚都没有）" : sb.ToString())}"
                    + $" ｜ 窗口={act.Window} 主角=#{act.SubjectId}");
            }
            catch (Exception ex)
            {
                Plugin.Log.LogWarning($"[HS-Replay] SpawnShot 普查失败 — {ex.Message}");
            }
        }

        /// <summary>
        /// 本幕"**会出现在画面里的人**" = 磁带里出现过的人 ∪ **事件参与者**。
        ///
        /// ★ 为什么要并上参与者：`VisibleIn` 读的是"录制者当时看得到的人"，而实测
        ///   第 2 次刀杀之后它掉到 1~2 人 ⇒ 画面里只剩凶手 ⇒
        ///   用户看到"对着空气挥刀，然后冒出一具尸体"。
        ///   **受害者本来就该出现在画面里**，不该依赖"录制者有没有录到他"；
        ///   而位置一律取房主侧采样，所以即使磁带里完全没有他也能摆对位置。
        ///
        /// ⚠ 这个并集同时用于**补帧**和**剪影决策**：剪影必须落在"不会本色出场的人"身上，
        ///   所以参与者（会出场）也要排除在剪影候选之外。
        /// </summary>
        private static List<int> SceneIds(Act act, List<SnapShot> frames)
        {
            var ids = new List<int>(ActTable.VisibleIn(frames, act.Window.From, 0));

            // ★ **自我报告**：如果这一幕需要"另补"参与者，就说明**录制侧没录到他们** ——
            //   那是另一个 bug，必须显式喊出来，不能让这层兜底悄悄掩盖它。
            //   （`VisibleIn` 是扫描**全部**帧的，所以它漏人只可能是"磁带里真的没有"。）
            var added = new List<int>();
            if (act.Subjects != null)
            {
                foreach (int id in act.Subjects)
                {
                    if (id > 0 && !ids.Contains(id))
                    {
                        ids.Add(id);
                        added.Add(id);
                    }
                }
            }

            if (added.Count > 0)
            {
                Plugin.Log.LogWarning(
                    $"[HS-Replay] 【{ActTable.Name(act.Kind)}#{act.Key}】⚠ 磁带里只录到 {ids.Count - added.Count} 人"
                    + $"（{string.Join(",", ids.Where(i => !added.Contains(i)).Select(i => "#" + i))}），"
                    + $"参与者 {string.Join(",", added.Select(i => "#" + i))} **不在磁带里** ⇒ 由服务端补进画面。"
                    + "这属于「录制侧漏录」，需要单独查（见 tapedump 的原始磁带 + SpawnShot 普查）。");
            }

            return ids;
        }

        /// <summary>把"画面里的人"的 id 换成本幕**窗口起点时的样子**（位置取自房主侧采样）。</summary>
        private static List<PublicPlayerInfo> VisibleInfos(List<int> ids, float windowStart)
        {
            var list = new List<PublicPlayerInfo>(ids.Count);
            foreach (int id in ids)
            {
                var info = HostRecorder.At(id, windowStart);
                if (info != null)
                    list.Add(info);
            }
            return list;
        }

        /// <summary>
        /// 首帧的玩家信息：**剪影槽位本人** + **主角在窗口起点的位置**。
        ///
        /// 用剪影槽位自己的信息、而不是复用主角的：客户端 `Spawn → SetInfo` 里有
        /// `RefreshSkeletonCharacter(CharacterId)`，会把**这个人的角色改成主角的角色**，
        /// 而改动留在客户端的 `PublicInfo` 上 —— 录像绝不能影响结算画面（实测踩过）。
        ///
        /// 位置必须来自房主侧采样（与窗口同一时间轴）。旧实现是"从客户端磁带里捞最近一枚
        /// SpawnShot"，而录制者那些帧全是 `SurvivalTime` 基准 ⇒ `TimeStamp <= 窗口起点` 不成立
        /// ⇒ 退到磁带第一帧 = 十几秒前的位置 ⇒ 角色朝错误方向匀速漂移（实测"每一帧都是坏的飘的"）。
        /// </summary>
        private static PublicPlayerInfo BuildHead(Act act, int silhouetteId)
        {
            var subject = HostRecorder.At(act.SubjectId, act.Window.From);
            var own = HostRecorder.At(silhouetteId, act.Window.From) ?? subject;
            if (own == null)
                return null;

            var head = own.Clone();
            head.PlayerId = silhouetteId;
            head.State = EPlayerState.Idle;
            head.IsGhost = true;

            if (subject?.Pos != null)
                head.Pos = subject.Pos.Clone();
            return head;
        }

        /// <summary>占位磁带的主视角：优先录制者本人，其次任意一个还能用的已死者。</summary>
        private static int PickPlaceholderId(Act act)
        {
            if (act.SilhouetteId > 0)
                return act.SilhouetteId;
            if (HostRecorder.HasRows(act.RecorderId))
                return act.RecorderId;
            var sil = SilhouetteResolver.Resolve(act.SubjectId, new List<int>(), act.Window.From);
            act.SilhouetteId = sil.Id;
            return sil.Id;
        }

        /// <summary>回放预算：客户端 `StartReplay` 先播标题，再逐段放；慢镜期间推进更慢，按 1.6 倍留量。</summary>
        private static int EstimateBudgetMs()
        {
            float per = ReplayFeature.SecondsPerClipEstimate?.Value ?? 5f;
            double total = 5000;
            foreach (var p in All)
                total += Math.Max(p.Act.Window.Length, per) * 1000.0 * 1.6 + 600;
            total += 8000;
            return (int)Math.Max(15000, Math.Min(120000, total));
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