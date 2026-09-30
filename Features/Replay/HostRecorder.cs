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
        /// <summary>环形缓冲时长。自爆窗口 6.5s、最后时段 3s 都远小于它；留 45s 是为了"结算类片段"也能取到历史。</summary>
        private const float KeepSeconds = 45f;

        /// <summary>超过这个帧数就强制裁剪（防住"人多 + 高刷"时的无限增长）。</summary>
        private const int MaxRows = 40000;

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

        private static readonly Dictionary<int, List<Row>> ById = new Dictionary<int, List<Row>>();
        private static readonly List<Bomb> Bombs = new List<Bomb>();
        private static int _rowCount;
        private static float _lastTrim = -999f;
        private static float _newestTime = -999f;

        public static void Clear()
        {
            ById.Clear();
            Bombs.Clear();
            _rowCount = 0;
            _lastTrim = -999f;
            _newestTime = -999f;
        }

        /// <summary>
        /// 采一帧。**调用点应该是服务端的 `Player.Move`**（每次收到 `C_MOVE` 之后执行）——
        /// 这样频率与客户端录制同源，而不是像旧实现那样定时 5Hz。
        ///
        /// 为什么不需要"定时兜底采样"：位置是**状态**而不是事件 ——
        /// 站着不动的人，他"最后一次移动"那一帧记录的位置**仍然是对的**；
        /// 查询用"≤ 目标时刻的最近一帧"即可（<see cref="At"/>）。
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

                // 位置是状态：连续两次完全一样就没必要再存一帧（省内存，也省查询时的扫描）
                if (list.Count > 0)
                {
                    var last = list[list.Count - 1];
                    if (SameState(last, row))
                    {
                        last.Time = now;         // 只把时间往后推
                        _newestTime = now;
                        return;
                    }
                }

                list.Add(row);
                _rowCount++;
                _newestTime = now;

                if (now - _lastTrim > 5f || _rowCount > MaxRows)
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
        /// 【兜底】把当前所有玩家各记一帧（不管他们有没有移动）。
        ///
        /// 主采样点挂在 `Player.Move` 上（服务端每次收到 `C_MOVE` 后调用）—— 那保证了
        /// "有移动就有帧"，而且频率与客户端录制同源。但**假人不主动发包**、站桩玩家也不发包，
        /// 所以"从未移动过的人"可能一帧都没有。这一路只负责补这个洞，不追高频。
        /// （`NoteMove` 内部有"状态没变就只把时间往后推"的去重，所以它不会撑大缓冲。）
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
        /// 注意保留策略：该时刻之前没有任何采样时，返回他**最早**的一帧（而不是 null）——
        /// 因为"他早就站在那儿了"比"他不在场"更接近事实。
        /// </summary>
        public static PublicPlayerInfo At(int id, float t, out float velocity)
        {
            velocity = 0f;
            if (!ById.TryGetValue(id, out var list) || list.Count == 0)
                return null;

            var row = AtOrBefore(list, t);
            if (row == null)
                return null;

            velocity = row.Velocity;
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
        /// 窗口内的**全部采样**，按时间升序 —— 合成世界帧（`HostSynth`）的数据源。
        ///
        /// ⚠ 它只返回"窗口内**真的发生过**的移动"，**不含**"谁站在那里"的起点信息。
        ///   起点由调用方用 <see cref="Roster"/>(窗口起点) 铺 —— 那是 `TapeAssembler` 的"全员出场帧"。
        ///   这个分工必须清楚，否则同一个人会被发两枚 SpawnShot。
        /// </summary>
        public static List<Sample> Range(float from, float to)
        {
            var list = new List<Sample>(256);
            foreach (var kv in ById)
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

                    list.Add(new Sample
                    {
                        Time = r.Time,
                        Id = r.Id,
                        Info = r.Info,
                        Velocity = r.Velocity,
                        RoomId = r.RoomId,
                        IsLight = r.IsLight,
                    });
                }
            }
            list.Sort((a, b) => a.Time.CompareTo(b.Time));
            return list;
        }

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

        /// <summary>两帧是否"看起来一样"（位置/朝向/速度/房间/光照都没变）—— 一样就不必再存。</summary>
        private static bool SameState(Row a, Row b)
        {
            if (a.RoomId != b.RoomId || a.IsLight != b.IsLight)
                return false;
            if (Math.Abs(a.Velocity - b.Velocity) > 0.01f)
                return false;
            if (a.Info.LookLeft != b.Info.LookLeft)
                return false;
            var pa = a.Info.Pos;
            var pb = b.Info.Pos;
            if (pa == null || pb == null)
                return false;
            return Math.Abs(pa.X - pb.X) < 0.01f && Math.Abs(pa.Y - pb.Y) < 0.01f;
        }

        /// <summary>找"最后一个 Time &lt;= t"的行（二分）；都比 t 晚就退用最早的。</summary>
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
            return best >= 0 ? rows[best] : rows[0];
        }

        private static void Trim(float now)
        {
            float keepFrom = now - KeepSeconds;
            foreach (var kv in ById)
            {
                var l = kv.Value;
                int drop = 0;
                while (drop < l.Count - 1 && l[drop].Time < keepFrom)
                    drop++;
                if (drop > 0)
                {
                    l.RemoveRange(0, drop);
                    _rowCount -= drop;
                }
            }
        }
    }
}
