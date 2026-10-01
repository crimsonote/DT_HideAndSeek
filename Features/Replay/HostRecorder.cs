using System;
using System.Collections.Generic;
using Protocol;
using Server.Game;
using GamePlayer = Server.Game.Player;

namespace HideAndSeek.Features.Replay
{
    /// <summary>
    /// 【房主侧采样器】—— 服务端视角的玩家轨迹缓冲。
    ///
    /// 为什么需要它：真人客户端回传的磁带是首选素材（见 docs/回放-规格.md §4·S4），
    /// 但有两类人**永远交不上磁带**：
    ///   · **假人**没有客户端，收不到 `S_RECORD_REPLAY`，`RecordManager` 根本跑不起来；
    ///   · 客户端 `Recording()` 的守卫是 `IsAlive &amp;&amp; !IsSpectator &amp;&amp; State == Survive`，
    ///     死者**完全不录** —— 而"自爆幕"要的正是白方被处决那一刻的视角。
    /// ⇒ 这两类只能由房主侧产出。
    ///
    /// ★★ **与旧实现唯一的、也是最重要的区别：采样点。**
    ///   旧实现挂在 `Managers.Update` 上、按 `SampleInterval = 0.2f` 定时采样（死板 5Hz）。
    ///   本类改挂在 <see cref="NoteMove"/> —— 它由**服务端每次收到 `C_MOVE` 后调用**，
    ///   所以采样频率 = 客户端发包频率：站桩约 10Hz、转向立即补发、鼠标跟随可达 ~50Hz。
    ///   ⇒ 服务端合成的密度**与客户端磁带同源**，而不是"5Hz 位置 + 名义上的插值"。
    ///
    ///   ⚠ 但**位置滞后消不掉**：服务端记的是收到 `C_MOVE` 之后设的权威位置，
    ///     而客户端画面是本地先行的 ⇒ 恒定落后约一个 RTT。这正是"延迟刀"观感的来源。
    ///     采样再密也补不回那一跳 —— 如实写进已知限制，不做"看起来更平滑"的假修补。
    ///
    /// 本类**只负责采样与查询**（纯数据），不产帧 —— 合成在 HostSynth（阶段 3b）。
    /// </summary>
    internal static class HostRecorder
    {
        /// <summary>
        /// 环形缓冲时长 —— **必须覆盖整局**。
        ///
        /// 为什么不是"只需要覆盖结算类的那几秒"（曾经这么以为，设成 60 秒，那是错的）：
        ///   回放是在**结算时**才组装的，而一幕的事件的发生时刻可能远早于此
        ///   —— 实测那一局：第一幕的事件在 `ClientTime = 27.5`，结算在 116.5，**相隔 89 秒**。
        ///   60 秒的缓冲早已把那一段裁光 ⇒ `At(id, 窗口起点)` 走 `AtOrBefore` 的兜底返回 `rows[0]`
        ///   —— 而那是**该玩家死之后**的帧（`IsGhost = true`）
        ///   ⇒ `SilhouetteResolver` 把它判成"起点即幽灵" ⇒ **受害者被选为剪影**
        ///   ⇒ 涂黑 + 身体隐藏 ⇒ 玩家看到"砍死虚空、然后凭空冒出尸体"。
        ///
        /// 曾经以为"早期事件由客户端的 9 秒快照兜底、房主采样只是备用"，所以 60 秒够 ——
        /// **那个假设不成立**：客户端录制会漏人（实测日志：`参与者 #4 不在磁带里 ⇒ 由服务端补进画面`），
        /// 此时该玩家的位置**只能**取自房主采样。
        ///
        /// ⚠ 历史上把这里设成 300 秒确实引发过严重卡顿，但**根因不在这个值**，而是
        ///   `NoteMove` 里的触发条件写成了 `|| _rowCount > MaxRows` —— 帧数一旦顶到上限，
        ///   该条件恒真 ⇒ `Trim` **每帧**遍历几万帧 ⇒ 主线程一帧 5.7 秒（Dissonance 报 frame skip）。
        ///   现在触发只按时间且 1 秒节流，"超上限"由 `Trim` 内部按数量兜底处理 ⇒ 可以放心放大窗口。
        /// </summary>
        private const float KeepSeconds = 400f;

        /// <summary>
        /// 硬上限。超过就**按数量**裁掉最老的（不能只靠时间 —— 帧率高于预期时会空转）。
        /// 取值依据：`KeepSeconds` 400 × 9 帧/秒/人 × 6 人 ≈ 21600，留一点余量。
        /// 内存 ≈ 24000 × ~200B ≈ 4.6 MB。
        /// </summary>
        private const int MaxRows = 24000;

        /// <summary>一次采样 —— 某个玩家在某个时刻的样子。</summary>
        private sealed class Row
        {
            public float Time;
            public int Id;
            public PublicPlayerInfo Info;   // 已 Clone，调用方拿到的一定是新实例
            public float Velocity;
            /// <summary>这一帧时他所在房间（`Player.CurrentArea`）。相机只能在自己房间边界内活动，
            /// 所以每换一个机位对象就必须把房间一起换掉，否则镜头会被夹住。</summary>
            public int RoomId;
            /// <summary>这一帧时他那个房间的灯是否亮着 —— 用于让"黑方收尾幕"的断电视野**跟随真实光照**，
            /// 而不是像旧实现那样硬编码（旧实现在亮着的房间也会断电）。</summary>
            public bool IsLight;
        }

        private sealed class Bomb
        {
            public float Time;
            public int DeviceId;
            public PosInfo Pos;
        }

        /// <summary>
        /// **一幕的采样快照** —— 独立于主缓冲存储，这才是"按需裁剪"的正解。
        ///
        /// 客户端早就在这么做：收到 `S_RECORD_REPLAY` 后 9 秒，它把当时的录制缓冲
        /// **拍成快照并长期保存**，之后缓冲怎么滚都不影响它（`ReserveSaveTape`）。
        /// 服务端缺的正是这一层 —— 采样只住在主缓冲里，于是**主缓冲的裁剪能把"结算时要用的幕"剪掉**。
        /// 实测：把 `MaxRows` 调小后，"按数量裁"那个分支不尊重保护名单，
        /// 直接丢掉了最早那几幕的帧 ⇒ 自爆幕消失、剪影候选全空 ⇒ 降级到主角。
        ///
        /// 分工：
        ///   · 主缓冲（`ById`）—— 保留策略照旧，负责"最近一段"的查询（`At`/`Range`/`Roster`）；
        ///   · 快照（本类）—— 事件发生时开始累积 `[事件−before, 事件+after]`，`To` 之后**封存**，
        ///     此后不再更新、**也不受主缓冲裁剪影响**。结算时从这里取。
        ///
        /// ⚠ **第一步只积累，不改任何现有查询路径** —— `Trim`/`Range`/`At` 全不动，
        ///   所以现有行为一字不变（最坏只是多占一点内存）。验证积累正常后，再让 `Range` 读它。
        /// </summary>
        private sealed class Snap
        {
            public int Key;
            public float From;
            public float To;
            /// <summary>`To` 之后置位 ⇒ 不再接收新帧（这一幕的素材已齐）。</summary>
            public bool Sealed;
            /// <summary>按玩家分组 —— 与主缓冲同构，便于日后直接替换查询。</summary>
            public readonly Dictionary<int, List<Row>> Rows = new Dictionary<int, List<Row>>();
            public int Count;
        }

        private static readonly List<Snap> _snaps = new List<Snap>();

        /// <summary>
        /// 事件发生时登记一份快照（窗口此刻已确定）。由 `ReplayFeature.AddWithKey` 调用。
        /// 同一 key 重复登记时忽略（幂等）。
        /// </summary>
        public static void BeginSnap(int key, float from, float to)
        {
            if (to <= from)
                return;
            for (int i = 0; i < _snaps.Count; i++)
            {
                if (_snaps[i].Key == key)
                    return;
            }

            var snap = new Snap { Key = key, From = from, To = to };

            // ★★ **回填"事件之前"那几秒** —— 这一步不能省。
            //    登记发生在**事件当时**，而窗口起点是 `事件 − before` ⇒ 那一段**已经过去了**，
            //    快照若从此刻才开始收帧，就永远缺"事件前"的素材。
            //    实测（`before` 越大缺得越多）：窗口 4s、`before=3` 的杀人幕只收到 15 帧
            //    （应约 144），而 `before=0` 的自爆幕反而是好的 ⇒ 症状正是"第一幕没有出刀动画、
            //    没有人死掉"（那些帧都在缺掉的那一段里）。
            //    主缓冲此刻仍保有那段（`KeepSeconds` 内），直接把它灌进来即可。
            float now = Now();
            foreach (var kv in ById)
            {
                var rows = kv.Value;
                int n = rows.Count;
                if (n == 0)
                    continue;
                List<Row> dst = null;
                for (int i = 0; i < n; i++)
                {
                    var r = rows[i];
                    if (r.Time < from)
                        continue;
                    if (r.Time > now)
                        break;
                    if (dst == null)
                    {
                        if (!snap.Rows.TryGetValue(kv.Key, out dst))
                            snap.Rows[kv.Key] = dst = new List<Row>(64);
                    }
                    dst.Add(r);
                    snap.Count++;
                }
            }

            _snaps.Add(snap);
        }

        /// <summary>快照积累情况的诊断行（写日志用）。</summary>
        public static string SnapStats()
        {
            if (_snaps.Count == 0)
                return "快照 0 份";
            var sb = new global::System.Text.StringBuilder();
            sb.Append("快照 ").Append(_snaps.Count).Append(" 份：");
            for (int i = 0; i < _snaps.Count; i++)
            {
                var s = _snaps[i];
                if (i > 0)
                    sb.Append(" ｜ ");
                sb.Append('#').Append(s.Key).Append(' ')
                  .Append(s.Sealed ? "已封存" : "累积中")
                  .Append(' ').Append(s.Count).Append(" 帧")
                  .Append('/').Append(s.Rows.Count).Append(" 人");
            }
            return sb.ToString();
        }

        private static readonly Dictionary<int, List<Row>> ById = new Dictionary<int, List<Row>>();
        private static readonly List<Bomb> Bombs = new List<Bomb>();
        private static int _rowCount;
        private static float _lastTrim = -999f;
        private static float _newestTime = -999f;

        public static void Clear()
        {
            ById.Clear();
            Bombs.Clear();
            _snaps.Clear();
            _rowCount = 0;
            _lastTrim = -999f;
            _newestTime = -999f;
        }

        /// <summary>
        /// 采一帧。**调用点应该是服务端的 `Player.Move`**（每次收到 `C_MOVE` 之后执行）——
        /// 这样频率与客户端录制同源，而不是像旧实现那样定时 5Hz。
        ///
        /// ★ **不做状态去重**（详见方法内注释）：`C_MOVE` 每次记一帧、心博每秒再补一帧。
        ///   曾经的"状态没变就只把时间往后推"会让**不动的人**在缓冲里只剩一帧、且时间戳被推到最新，
        ///   于是 `Range(from,to)` 按区间筛时什么都取不到 —— 那正是"合成幕一闪而过"的根因。
        /// </summary>
        public static void NoteMove(GamePlayer p)
        {
            try
            {
                var info = p?.PublicInfo;
                if (info == null || info.PlayerId <= 0)
                    return;

                float now = Now();
                var row = new Row
                {
                    Time = now,
                    Id = info.PlayerId,
                    Info = info.Clone(),
                    Velocity = p.Velocity,
                    RoomId = RoomOf(p),
                    IsLight = LightOf(p),
                };

                if (!ById.TryGetValue(row.Id, out var list))
                    ById[row.Id] = list = new List<Row>(64);

                // ★ **不做"状态去重"** —— 每次都新增一帧，也就是"定时录帧"，与客户端 `RecordAllType` 同构。
                //
                //   曾经的写法是"连续两次状态完全一样就只把 last.Time 往后推、不新增帧"，
                //   理由是"位置是状态、查询只需 ≤ 目标时刻的最近一帧、还能省内存"。
                //   **那个理由只对 `At()` 成立，对 `Range()` 是致命的**：
                //     `last.Time = now` 把一帧的时间戳推到了"最新"，而它的语义变成了
                //     "这个状态持续到的最新时刻" ⇒ 于是 `Range(from,to)` 按 `Time ∈ [from,to]`
                //     线性筛时，`now > to` 会直接 `break`，**那一段唯一的一枚素材被跳过**。
                //   实测症状（用户那一局）：自爆三幕 `samples` 全为空 ⇒ 装配出的帧只剩窗口起点那几枚
                //   ⇒ 段时长趋近 0 ⇒ "一闪而过"。
                //   而**不动的人（假人、自爆中的人、站桩玩家）恰恰只有那一帧** ⇒ 必空。
                //
                //   定时录帧后帧密度 = 与客户端同源：`C_MOVE` 每 tick 一枚（≈8/秒/人）+ 心博 1/秒/人。
                //   容量：最坏 6 人 × 9/秒 × `KeepSeconds`(60) ≈ 3240 帧，远低于 `MaxRows`(12000)。
                list.Add(row);
                _rowCount++;
                _newestTime = now;

                // ★ **同时分流进"覆盖当前时刻的活跃快照"** —— 这一幕的素材从此有了一份独立副本，
                //   主缓冲之后怎么裁都动不到它（`Row` 只读，`At()` 返回前会 Clone，共享实例安全）。
                for (int i = 0; i < _snaps.Count; i++)
                {
                    var s = _snaps[i];
                    if (s.Sealed || now < s.From || now > s.To)
                        continue;
                    if (!s.Rows.TryGetValue(row.Id, out var sl))
                        s.Rows[row.Id] = sl = new List<Row>(64);
                    sl.Add(row);
                    s.Count++;
                }

                // ★★ 触发条件**必须只按时间**，而且要节流。
                //    曾经写成 `|| _rowCount > MaxRows`：帧数一旦顶到上限，这个条件就**恒真**
                //    ⇒ `Trim` 每一帧都跑 ⇒ 每帧遍历几万帧 ⇒ 主线程一帧 5.7 秒
                //    （实测 Dissonance 报 "frame skip, Delta Time:5.697155"，随后游戏被强杀）。
                //    "超上限"由 `Trim` 内部按数量兜底处理，不需要在这里每帧检查。
                if (now - _lastTrim >= 1f)
                {
                    _lastTrim = now;
                    Trim(now);
                }
            }
            catch
            {
                // 采样失败绝不该影响对局。
            }
        }

        /// <summary>
        /// 给当前所有玩家各记一帧（不管他们有没有移动）—— 由 `Managers.Update` 上的心博按 **1Hz** 调用。
        ///
        /// ★ 它**不是"兜底"，而是"不动的人唯一的素材来源"**：
        ///   主采样点在 `Player.Move`（服务端每次收到 `C_MOVE` 后调用），频率与客户端同源；
        ///   但**假人不发包、站桩玩家不发包、自爆/巡礼期间大家都不动** ——
        ///   这些情形下全靠这一路按秒计时录帧，否则那一段在缓冲里就是空白。
        ///   ⇒ 少说话就会重演"合成幕一闪而过"（实测：自爆三幕 `samples` 全空）。
        /// </summary>
        public static void SampleAll()
        {
            try
            {
                var room = GameRoom.Instance;
                if (room == null)
                    return;
                foreach (var p in room.Players)
                    NoteMove(p);
                foreach (var p in room.DeadPlayers)
                    NoteMove(p);
            }
            catch
            {
                // 兜底采样失败不该影响对局。
            }
        }

        /// <summary>记一枚项圈自爆（`OnDeadCollarBomb` 的补丁调用）。爆炸的"开始时刻"与位置只有服务端知道。</summary>
        public static void NoteBomb(float now, int deviceId, PosInfo pos)
        {
            try
            {
                Bombs.Add(new Bomb { Time = now, DeviceId = deviceId, Pos = pos?.Clone() });
            }
            catch
            {
                // 记不下这一枚不该影响对局。
            }
        }

        // ── 查询 ────────────────────────────────────────────────────────

        /// <summary>这个人在缓冲里有没有采样（没有 = 他不在房间里，不能当机位）。</summary>
        public static bool HasRows(int id) => id > 0 && ById.TryGetValue(id, out var l) && l.Count > 0;

        /// <summary>缓冲里出现过的所有 id（用于"全员出场帧"的名单）。</summary>
        public static List<int> Ids()
        {
            var ids = new List<int>(ById.Count);
            foreach (var kv in ById)
            {
                if (kv.Value.Count > 0)
                    ids.Add(kv.Key);
            }
            return ids;
        }

        /// <summary>
        /// 某人在 <paramref name="t"/> 时刻（或之前最近一刻）的样子。
        /// 返回的是**新实例**（调用方可以随意改，不会污染缓冲）。
        ///
        /// ⚠ **该时刻之前没有任何采样时返回 null**（不再退用"最早的一帧"）。
        ///   旧理由是"他早就站在那儿了，比'他不在场'更接近事实"—— 听起来合理，但它混淆了两件事：
        ///   ① "那时他已在场，只是没动" 与 ② "那时他还没有任何数据"（缓冲被裁过、或他刚进场）。
        ///   而退用最早的一帧更严重的问题是**拿未来冒充过去**：实测见过返回的帧属于
        ///   **该玩家死亡之后**（`IsGhost = true`）⇒ 上层据此判他"起点即幽灵"
        ///   ⇒ 本幕受害者被选为剪影 ⇒ 涂黑 + 隐形 ⇒ "砍死虚空、凭空冒出尸体"。
        ///   ⇒ 宁可返回 null，让"取不到"显式暴露。
        /// </summary>
        public static PublicPlayerInfo At(int id, float t, out float velocity)
            => At(id, t, out velocity, out _);

        /// <summary>
        /// 取"≤ t 的最近一帧"。
        /// <paramref name="atTime"/> 回传**那一帧自己的时间戳** —— 排障用：
        /// 它正常应当 ≤ t；若日志里看到它明显大于 t，就说明当时缓冲覆盖不到该时刻
        /// （历史上正是靠这一点定位到"缓冲只有 60 秒而事件在 90 秒前"）。
        /// </summary>
        public static PublicPlayerInfo At(int id, float t, out float velocity, out float atTime)
        {
            velocity = 0f;
            atTime = -1f;
            if (!ById.TryGetValue(id, out var list) || list.Count == 0)
                return null;

            var row = AtOrBefore(list, t);
            if (row == null)
                return null;

            velocity = row.Velocity;
            atTime = row.Time;
            return row.Info.Clone();
        }

        public static PublicPlayerInfo At(int id, float t) => At(id, t, out _);

        /// <summary>某人某时刻所在的房间号；取不到返回 0。</summary>
        public static int RoomAt(int id, float t)
        {
            if (!ById.TryGetValue(id, out var list) || list.Count == 0)
                return 0;
            return AtOrBefore(list, t)?.RoomId ?? 0;
        }

        /// <summary>某人某时刻所在房间的灯是否亮着。取不到时返回 true（宁可亮着，也不要凭空断电）。</summary>
        public static bool LightAt(int id, float t)
        {
            if (!ById.TryGetValue(id, out var list) || list.Count == 0)
                return true;
            return AtOrBefore(list, t)?.IsLight ?? true;
        }

        /// <summary>
        /// 在 `[from, to]` 期间，`a` 与 `b` 是否**曾经**相距 &lt; `range`
        /// （逐采样点比对 —— **进过范围就算**，不是只看某一刻）。
        ///
        /// ★ 为什么必须扫全程：幕的窗口是 `[事件 − 3, 事件 + 1]`，而**事件在窗口末尾** ——
        ///   凶手是**走到**受害者身边才动手的。若只判窗口起点那一刻的距离，拿到的恰恰是
        ///   "他还没走过去"的时候（实测算出 2101 &gt; 900 ⇒ 受害者被判"不在画面里"
        ///   ⇒ 剪影落到他身上 ⇒ 涂黑 + 隐形 ⇒ 玩家看到"砍死虚空、凭空冒出尸体"）。
        ///
        /// 以 `a`（镜头/主角）的采样时刻为基准，`b` 用 `AtOrBefore` 取同一时刻的位置 ——
        /// 这样"站着不动的人"（不产生新采样）也能参与比较，不必给他插值。
        /// </summary>
        public static bool EverWithin(int a, int b, float from, float to, float range)
        {
            if (a <= 0 || b <= 0)
                return false;
            if (!ById.TryGetValue(a, out var ra) || !ById.TryGetValue(b, out var rb))
                return false;
            if (ra.Count == 0 || rb.Count == 0)
                return false;

            float r2 = range * range;
            for (int i = 0; i < ra.Count; i++)
            {
                var x = ra[i];
                if (x.Time < from)
                    continue;
                if (x.Time > to)
                    break;

                var pa = x.Info?.Pos;
                var pb = AtOrBefore(rb, x.Time)?.Info?.Pos;
                if (pa == null || pb == null)
                    continue;

                float dx = pa.X - pb.X;
                float dy = pa.Y - pb.Y;
                if (dx * dx + dy * dy <= r2)
                    return true;
            }
            return false;
        }

        /// <summary>
        /// 全员在 <paramref name="t"/> 时刻的样子 —— **"全员出场帧"的数据源**。
        ///
        /// ★ 必须是这里（房主侧采样），不能"从客户端磁带里捞"：
        ///   客户端 `Player.Move` 只把移动包发给 `Session`(自己) / `BroadcastToDeadPlayers` /
        ///   `BroadcastToPlayerAndObservers`(AOI 内的活人) —— 所以活人录的磁带**只有 AOI 内的人**。
        ///   旧实现从磁带里捞 SpawnShot，实测只捞到 1 枚，等于空转。
        /// </summary>
        public static List<PublicPlayerInfo> Roster(float t)
        {
            var list = new List<PublicPlayerInfo>(ById.Count);
            foreach (var kv in ById)
            {
                if (kv.Value.Count == 0)
                    continue;
                var info = AtOrBefore(kv.Value, t)?.Info;
                if (info != null)
                    list.Add(info.Clone());
            }
            return list;
        }

        /// <summary>一枚采样（查询结果的只读视图）。</summary>
        internal struct Sample
        {
            public float Time;
            public int Id;
            /// <summary>⚠ 指向缓冲内部的实例，**只读、不要改**（要改就自己 Clone）。</summary>
            public PublicPlayerInfo Info;
            public float Velocity;
            public int RoomId;
            public bool IsLight;
        }

        /// <summary>
        /// 取 [from, to] 区间内**每人**的采样，按时间升序 —— 合成世界帧（`HostSynth`）的数据源。
        ///
        /// 采样侧是**定时录帧**（`NoteMove` 不做状态去重）：`C_MOVE` 每 tick 一枚 + 心博 1/秒一枚。
        /// 而窗口最短的是巡礼（`TourBeforeSec` 2 + `TourAfterSec` 0.5 = 2.5s）
        /// ⇒ **任何窗口内都至少应有 2 枚帧**。
        ///
        /// ⇒ 所以这里**不做任何兜底**：取不到就是采样机制坏了，
        ///   而那种情况会由装配日志里的 `⚠帧跨度只有…` 当场暴露。
        ///   补一枚"锚帧"能让画面看起来正常，却会把这个信号盖掉 —— 那正是要避免的。
        /// </summary>
        /// <summary>
        /// 找一份**覆盖 `[from, to]`** 的服务端采样快照。
        ///
        /// ⚠ 别与"客户端磁带"混：客户端也有快照（`ReserveSaveTape` 拍的 `List<SnapShot>`），
        ///   但那个走 `S_TAPE` 由 `ReplayDirector.OnTape` 处理，**优先级高于本函数**。
        ///   这里只服务"服务端合成"那一路的数据源。
        /// </summary>
        private static Snap FindSnap(float from, float to)
        {
            for (int i = 0; i < _snaps.Count; i++)
            {
                var s = _snaps[i];
                if (s.From <= from + 0.001f && s.To >= to - 0.001f)
                    return s;
            }
            return null;
        }

        /// <summary>
        /// 取 `[from, to]` 区间内每人的采样 —— **服务端合成**（`HostSynth`）的数据源。
        ///
        /// ★ 数据源优先级（**只在这一路内**，与"客户端磁带优先"无关）：
        ///   ① **服务端采样快照**（`Snap`）—— 独立副本，主缓冲怎么裁都动不到它 ⇒ 首选；
        ///   ② 主缓冲（`ById`）—— 快照未覆盖时退回（兼容"没登记到"的情况）。
        ///   而**客户端磁带**在更上层就已经定胜负：客户端回了磁带就走 `OnTape`，
        ///   压根不会调用到这里（见 `ReplayDirector` 的 `TrySynth`）。
        ///
        /// 为什么要有 ①：主缓冲的裁剪曾把"结算时要用的幕"剪掉（`MaxRows` 调小后
        /// "按数量裁"分支不尊重保护名单 ⇒ 最早那几幕的帧全丢 ⇒ 自爆幕消失、剪影候选空）。
        /// 快照把素材从主缓冲里**解耦**出来，那类事故就不可能再发生。
        /// </summary>
        public static List<Sample> Range(float from, float to)
        {
            var list = new List<Sample>(256);
            var snap = FindSnap(from, to);
            var source = snap != null ? snap.Rows : ById;

            foreach (var kv in source)
            {
                var rows = kv.Value;
                int n = rows.Count;
                for (int i = 0; i < n; i++)
                {
                    var r = rows[i];
                    if (r.Time < from)
                        continue;
                    if (r.Time > to)
                        break;
                    list.Add(ToSample(r));
                }
            }

            list.Sort((a, b) => a.Time.CompareTo(b.Time));
            return list;
        }

        private static Sample ToSample(Row r) => new Sample
        {
            Time = r.Time,
            Id = r.Id,
            Info = r.Info,
            Velocity = r.Velocity,
            RoomId = r.RoomId,
            IsLight = r.IsLight,
        };

        /// <summary>缓冲里记下的自爆（时刻 / 谁 / 在哪）——合成"自爆幕"时要用。</summary>
        public static List<(float Time, int DeviceId, PosInfo Pos)> BombList()
        {
            var list = new List<(float, int, PosInfo)>(Bombs.Count);
            foreach (var b in Bombs)
                list.Add((b.Time, b.DeviceId, b.Pos));
            return list;
        }

        /// <summary>缓冲覆盖的时间范围（用于日志排查）。</summary>
        public static bool Covered(out float from, out float to)
        {
            from = float.MaxValue;
            to = float.MinValue;
            foreach (var kv in ById)
            {
                var l = kv.Value;
                if (l.Count == 0)
                    continue;
                if (l[0].Time < from) from = l[0].Time;
                if (l[l.Count - 1].Time > to) to = l[l.Count - 1].Time;
            }
            return to >= from;
        }

        /// <summary>一行诊断（"缓冲 N 人 / M 帧 / 覆盖 [a,b] / 自爆 K 枚"）。</summary>
        public static string Stats()
        {
            string range = Covered(out float a, out float b) ? $"[{a:F1},{b:F1}]" : "(空)";
            return $"采样 {ById.Count} 人 / {_rowCount} 帧 / 覆盖 {range} / 自爆 {Bombs.Count} 枚";
        }

        // ── 内部 ────────────────────────────────────────────────────────

        private static float Now()
        {
            try { return Managers.Game.ClientTime; }
            catch { return 0f; }
        }

        private static int RoomOf(GamePlayer p)
        {
            try { return p?.CurrentArea?.Info?.RoomId ?? 0; }
            catch { return 0; }
        }

        private static bool LightOf(GamePlayer p)
        {
            try { return p?.CurrentArea?.IsLight ?? true; }
            catch { return true; }
        }

        /// <summary>
        /// 找"最后一个 Time &lt;= t"的行（二分）；**没有就返回 null**。
        ///
        /// ★ 返回 null 的语义是"这个时刻还没有任何数据"，**不是**"拿最早的一帧凑数"。
        ///   曾经的写法是"都比 t 晚就退用最早的（`rows[0]`）"—— 那等于把**未来**的状态
        ///   当成 t 时刻的状态，而调用方完全无从察觉。实测后果：窗口起点早于某人的所有帧时
        ///   （缓冲被裁过、或他刚进场），`At()` 返回的是他**死亡之后**的那一帧（`IsGhost = true`），
        ///   于是 `SilhouetteResolver` 把他判成"起点即幽灵" ⇒ **本幕的受害者被选为剪影**
        ///   ⇒ 涂黑 + 身体隐藏 ⇒ 玩家看到"砍死虚空、然后凭空冒出尸体"。
        ///
        /// ⇒ 宁可返回 null 让上层显式处理（`VisibleInfos` 跳过该人、`BuildHead` 退用主角、
        ///   `Roster` 不收他），也不要拿错误的数据把"取不到"盖住。
        /// </summary>
        private static Row AtOrBefore(List<Row> rows, float t)
        {
            int lo = 0, hi = rows.Count - 1, best = -1;
            while (lo <= hi)
            {
                int mid = (lo + hi) / 2;
                if (rows[mid].Time <= t)
                {
                    best = mid;
                    lo = mid + 1;
                }
                else
                {
                    hi = mid - 1;
                }
            }
            return best >= 0 ? rows[best] : null;
        }

        /// <summary>
        /// 裁剪缓冲。**只在节流后调用**（见 `NoteMove`）—— 它遍历所有玩家的所有帧。
        ///
        /// 两级裁剪：
        ///   ① 按时间：丢掉早于 `now - KeepSeconds` 的（常规路径）；
        ///   ② 按数量：时间裁不掉、但总量超过 `MaxRows` 时，每人保留等额的一份
        ///      （防住"帧率远高于预期"时缓冲无限增长 —— 只按时间会永远裁不掉）。
        /// </summary>
        private static void Trim(float now)
        {
            // ★ **封存已过窗口的快照**：`To` 之后这一幕的素材就齐了，不再接收新帧。
            //   （本步只加这一句，裁剪逻辑一个字不改 —— 快照不参与裁剪，只独立积累。）
            for (int i = 0; i < _snaps.Count; i++)
            {
                if (!_snaps[i].Sealed && now > _snaps[i].To)
                    _snaps[i].Sealed = true;
            }

            float keepFrom = now - KeepSeconds;
            int perPlayerCap = Math.Max(64, MaxRows / Math.Max(1, ById.Count));

            foreach (var kv in ById)
            {
                var l = kv.Value;
                int drop = 0;
                while (drop < l.Count - 1 && l[drop].Time < keepFrom)
                    drop++;

                if (drop == 0 && _rowCount > MaxRows && l.Count > perPlayerCap)
                    drop = l.Count - perPlayerCap;

                if (drop > 0)
                {
                    l.RemoveRange(0, drop);
                    _rowCount -= drop;
                }
            }
        }
    }
}
