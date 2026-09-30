using System;
using System.Collections.Generic;
using System.Linq;
using Protocol;
using Server.Game;
using GamePlayer = Server.Game.Player;

namespace HideAndSeek.Features.Replay
{
    /// <summary>
    /// 【编排】—— 从"决定要播"到"放完继续结算"。
    ///
    /// 当前只实现**影子装配**：对每一幕走"服务端合成 → 统一装配 → 打印结果"，
    /// **不发包、不广播、不播放**。它的作用是让"装配出来的磁带长什么样"在实机日志里可见，
    /// 而不需要用户按测试步骤去撞。
    ///
    /// ★★ 为什么影子期**绝不能发 `S_REQUEST_TAPE`**（这条踩过一次就会毁掉用户的回放）：
    ///   客户端收到索取请求会回 `C_TAPE`，而**现有实现的 `TapeHook` 只认它自己登记的 key**
    ///   ⇒ 我们的响应会被判成"不是我的片段"、**交还原版** ⇒
    ///   原版 `HostPacketHandler.Handle_C_TAPE` 会直接 `Broadcast(S_TAPE)` ——
    ///   **把原始磁带广播给所有客户端** ⇒ 污染它们的 `_playTapes`（而回放的播放列表就是它）
    ///   ⇒ 用户看到的回放会多出几段莫名其妙的原始录像。
    ///   ⇒ 所以：影子期只用**服务端采样**验证装配；真实客户端磁带的装配用 `TapeDump` 的文件**离线**验证。
    ///     只有在"正式替换"那一步（旧实现已经不在了）才发索取。
    ///
    /// 与现有实现的对应关系（逐个替换掉的东西）：
    ///   `TrimTape` + `ComposeHostTape`  →  <see cref="TapeAssembler.Assemble"/>（唯一出口）
    ///   `FindGhostStandIn` + `ResolveObserverId`  →  <see cref="SilhouetteResolver"/>
    ///   `EndReplayHostTape.Build`  →  <see cref="HostSynth"/>
    ///   散落的静态字段（`_played`/`_inReplay`/`_continueSettlement`…）  →  本类的显式阶段
    /// </summary>
    internal static class ReplayDirector
    {
        /// <summary>当前阶段（**一处**就能看清流程走到哪了 —— 规格 P2）。</summary>
        internal enum Phase
        {
            Idle = 0,
            /// <summary>已经决定要播，正在准备素材。</summary>
            Preparing = 1,
            /// <summary>已经进入回放画面（阶段 6 才会用到）。</summary>
            Playing = 2,
            /// <summary>回放结束，续结算。</summary>
            Done = 3,
        }

        internal static Phase Current { get; private set; } = Phase.Idle;

        internal static void Reset() => Current = Phase.Idle;

        /// <summary>
        /// 【影子】逐幕做一次完整装配并打印结果。
        ///
        /// 它验证的是<b>装配正确性</b>：帧数、首帧是不是 `SpawnShot`、时间戳是否非递减、
        /// 有没有补 `NormalTimeEdit`、剪影挑到了谁、roster 覆盖几个人。
        /// 数据来自房主侧采样（与窗口同一时间轴），所以窗口/首帧位置这两层旧 bug 在这里不可能出现。
        /// </summary>
        public static void ShadowAssemble(List<Act> acts, bool bombBlackout)
        {
            if (acts == null || acts.Count == 0)
            {
                Plugin.Log.LogInfo("[HS-Shadow] 幕表为空，没有可装配的段。");
                return;
            }

            int ok = 0, fail = 0;
            foreach (var act in acts)
            {
                try
                {
                    if (AssembleOne(act, bombBlackout))
                        ok++;
                    else
                        fail++;
                }
                catch (Exception ex)
                {
                    fail++;
                    Plugin.Log.LogWarning($"[HS-Shadow] 【{ActTable.Name(act.Kind)}#{act.Key}】装配抛异常 — {ex.Message}");
                }
            }

            Plugin.Log.LogInfo($"[HS-Shadow] 影子装配完成：成功 {ok} 幕 / 失败 {fail} 幕"
                + $"（不发包、不广播、不播放；真实客户端磁带的装配由 tapedump 离线验证）");
        }

        private static bool AssembleOne(Act act, bool bombBlackout)
        {
            // ① 世界帧（服务端合成）。
            //    它的开头就是"窗口起点的全员出场帧" —— 那一轮同时承担两个作用：
            //    让每台客户端装配好自己的 id 0 替身，以及作为"谁在画面里"的**唯一依据**。
            //    黑方收尾幕用断电视野，这是**刻意的艺术选择**，所以写在这里而不是让合成器猜。
            bool dark = act.Kind == ActKind.BlackTail;
            var frames = HostSynth.Frames(act.Window, act.SubjectId, dark, bombBlackout);

            // ② "谁在画面里" —— **直接从帧里读**，不做任何 AOI / 房间的近似推断。
            var visibleIds = ActTable.VisibleIn(frames, act.Window.From, 0);
            //    但**位置**必须取自房主侧采样：录制者自己的帧是 SurvivalTime 基准（每轮第一枚），
            //    从磁带里取会拿到几秒前的位置，客户端会先把角色摆在那儿再被后续帧拉回来 ⇒ 抖动。
            var visibleInfos = VisibleInfos(visibleIds, act.Window.From);

            // ③ 剪影槽位（唯一决策点）：必须落在"不会出现在画面里"的人身上。
            var sil = SilhouetteResolver.Resolve(act.SubjectId, visibleIds);
            act.SilhouetteId = sil.Id;

            // ④ 首帧信息：用**剪影槽位自己的**信息，只把位置换成"主角在窗口起点的位置"。
            //    为什么不能省事复用主角的信息：客户端 `Spawn → SetInfo` 里有一句
            //    `RefreshSkeletonCharacter(CharacterId)`，会**把这个人的角色改成主角的角色**，
            //    而且改动留在客户端的 PublicInfo 上 —— 录像绝不能影响结算画面（实测踩过）。
            var head = BuildHead(act, sil.Id);
            if (head == null)
            {
                Plugin.Log.LogWarning($"[HS-Shadow] 【{ActTable.Name(act.Kind)}#{act.Key}】"
                    + $"拿不到首帧信息（剪影=#{sil.Id}，采样={HostRecorder.Stats()}）⇒ 该幕无法装配。");
                return false;
            }

            // ⑤ 装配合规磁带（唯一出口）
            var tape = TapeAssembler.Assemble(act, frames, head, visibleInfos, out var rep);

            float density = rep.FramesOut / Math.Max(0.01f, act.Window.Length);
            if (tape == null)
            {
                Plugin.Log.LogWarning($"[HS-Shadow] {rep.Line()}");
                return false;
            }

            Plugin.Log.LogInfo($"[HS-Shadow] {rep.Line()} 密度={density:F1}帧/秒");
            return true;
        }

        /// <summary>
        /// 首帧的玩家信息：<b>剪影槽位本人</b> + <b>主角在窗口起点的位置</b>。
        ///
        /// 位置必须来自房主侧采样（与窗口同一时间轴）。现有实现是"从客户端磁带里捞最近的一枚
        /// SpawnShot"，而录制者的那些帧全是 `SurvivalTime` 基准（数值几百）
        /// ⇒ `TimeStamp &lt;= 窗口起点`（几十）永远不成立 ⇒ 它会退到磁带第一帧
        /// = **十几秒前的位置** ⇒ 与窗内帧相差上千单位 ⇒ 角色朝错误方向匀速漂移
        /// （实测"每一帧都是坏的飘的"就是这么来的）。这里从接口上就没有这个可能。
        /// </summary>
        // ── 真索带（验证用：真的向客户端要，但不广播、不播放）────────────────

        /// <summary>待收的幕：`(录制者, key)` → Act。客户端回包时按这个找回对应的一幕。</summary>
        private static readonly Dictionary<long, Act> Pending = new Dictionary<long, Act>();

        private static long Slot(int recorderId, int key) => ((long)recorderId << 32) | (uint)key;

        /// <summary>本局有几段真的向客户端索取了（用于收尾汇总）。</summary>
        private static int _fetched, _assembled, _failed;

        /// <summary>
        /// 【验证用】真的向客户端索取真实磁带 —— 但**不广播、不播放**，只装配 + 落盘。
        ///
        /// 为什么必须先做这一步：它验证的是"**真实客户端磁带**能不能被新装配器正确处理"，
        /// 而这是整套重写里唯一没法用合成数据覆盖的部分（影子装配用的是房主侧采样）。
        /// 旧实现所有的窗口错误都出在"拿真实磁带 + 用锚点算窗口"这一步上，所以这一环必须真跑。
        ///
        /// ⚠ 收上来的 `C_TAPE` **必须由我们拦下来**（见 `ReplayFeature.TapeHook`）：
        ///   否则现有实现的 TapeHook 会把它判成"不是我的片段"交还原版 ⇒
        ///   原版 `Handle_C_TAPE` 直接 `Broadcast(S_TAPE)` ⇒ 污染所有客户端的 `_playTapes`
        ///   ⇒ 用户看到的回放会多出几段莫名其妙的原始录像。
        /// </summary>
        public static void FetchTapes(List<Act> acts)
        {
            Pending.Clear();
            _fetched = _assembled = _failed = 0;

            var room = GameRoom.Instance;
            if (room == null || acts == null)
            {
                Plugin.Log.LogInfo("[HS-Shadow/真实] 房间不存在，跳过索取。");
                return;
            }

            foreach (var act in acts)
            {
                // 只有**有客户端**的录制者才问得到东西；假人没有 Session，旧实现也是直接跳过。
                var p = FindPlayer(room, act.RecorderId);
                if (act.RecorderId <= 0 || p?.Session == null)
                    continue;

                Pending[Slot(act.RecorderId, act.Key)] = act;
                p.Session.Send(new S_REQUEST_TAPE { RecordTime = act.Key });
                _fetched++;
            }

            Plugin.Log.LogInfo($"[HS-Shadow/真实] 已向客户端逐个索取 {_fetched} 段真实磁带"
                + $"（每 key 单发 ⇒ 客户端回原始缓冲，由我们裁；其余 {acts.Count - _fetched} 幕没有客户端可用）");
        }

        /// <summary>
        /// 收到一段真实客户端磁带（由 `ReplayFeature.TapeHook` 调用）。
        /// 返回 true = 这段是我们登记的，已处理（调用方不要交还原版）。
        /// </summary>
        public static bool OnTape(int recorderId, int key, List<SnapShot> raw)
        {
            long slot = Slot(recorderId, key);
            if (!Pending.TryGetValue(slot, out var act))
                return false;

            Pending.Remove(slot);
            Current = Phase.Preparing;

            try
            {
                // "谁在画面里" —— 直接读**客户端真实磁带**里的 SpawnShot（那就是 AOI 的真实结果）。
                var visibleIds = ActTable.VisibleIn(raw, act.Window.From, 0);
                // 位置取自房主侧采样（理由同 AssembleOne）。
                var visibleInfos = VisibleInfos(visibleIds, act.Window.From);
                var sil = SilhouetteResolver.Resolve(act.SubjectId, visibleIds);
                act.SilhouetteId = sil.Id;

                var head = BuildHead(act, sil.Id);
                var tape = TapeAssembler.Assemble(act, raw, head, visibleInfos, out var rep);
                float density = rep.FramesOut / Math.Max(0.01f, act.Window.Length);

                if (tape == null)
                {
                    _failed++;
                    Plugin.Log.LogWarning($"[HS-Shadow/真实] {rep.Line()}");
                }
                else
                {
                    _assembled++;
                    Plugin.Log.LogInfo($"[HS-Shadow/真实] {rep.Line()} 密度={density:F1}帧/秒"
                        + $"，客户端给了 {raw.Count} 帧");
                }

                // 落盘对照（复用 TapeDump 的目录；这里 at/before/after 传的是**新定义的绝对区间**，
                // 所以头部那个"窗口"数值就是规范窗口 —— 与旧实现那几行并列时一眼能看出差别）。
                TapeDump.Save(recorderId, key, "新装配-" + ActTable.Name(act.Kind),
                    act.Window.From, 0f, act.Window.Length, raw, tape, sil.ToString());

                return true;
            }
            catch (Exception ex)
            {
                _failed++;
                Plugin.Log.LogWarning($"[HS-Shadow/真实] 装配抛异常 — {ex.Message}");
                return true;
            }
        }

        /// <summary>收尾汇总（结算后调用）。</summary>
        public static void ReportFetch()
        {
            if (_fetched == 0 && _assembled == 0 && _failed == 0)
                return;
            Plugin.Log.LogInfo($"[HS-Shadow/真实] 真实磁带汇总：索取 {_fetched} 段，"
                + $"装配成功 {_assembled} 段，失败 {_failed} 段"
                + $"（落盘在插件目录 tapedump/ 下，与旧实现的 dump 并列可比）");
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

        private static PublicPlayerInfo BuildHead(Act act, int silhouetteId)
        {
            var subject = HostRecorder.At(act.SubjectId, act.Window.From);
            var own = HostRecorder.At(silhouetteId, act.Window.From) ?? subject;
            if (own == null)
                return null;

            var head = own.Clone();
            head.PlayerId = silhouetteId;
            head.State = EPlayerState.Idle;   // 别让"躲柜 / 死亡幽灵"这类状态驱动这个看不见的角色
            head.IsGhost = true;              // 幽灵 ⇒ 客户端 RefreshGhostVisual 走 case 2 把他关掉

            // 镜头必须落在主角所在的现场：把位置换成主角在窗口起点的位置。
            // ⚠ 必须 Clone —— 直接用会让首帧和采样缓冲共享同一个 PosInfo 对象。
            if (subject?.Pos != null)
                head.Pos = subject.Pos.Clone();

            return head;
        }
    }
}
