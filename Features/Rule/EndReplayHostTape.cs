using System;
using System.Collections.Generic;
using Protocol;
using Server.Game;
using GamePlayer = Server.Game.Player;

namespace HideAndSeek.Features.Rule
{
    /// <summary>
    /// 房主侧「服务端录制器」—— 给"客户端交不上磁带"的片段提供素材，并合成磁带。
    ///
    /// 为什么需要它（两条都是读码得来，实测也各踩过一次）：
    ///   · **假人没有客户端** ⇒ 收不到 `S_RECORD_REPLAY`，`RecordManager` 根本跑不起来 ⇒ 永远交不上磁带
    ///     （实测：自爆段的录制者恰好是假人 ⇒ 一段都没拿到，回放里看不到自爆）；
    ///   · **已死玩家也录不了** —— 客户端 `RecordManager.Recording()` 的守卫是
    ///     `MyPlayer == null || !Managers.Game.IsAlive || IsSpectator || State != Survive → return`，
    ///     而"自爆剪辑"要的正是白方被处决那一刻的视角（那时他们全是死人）
    ///     ⇒ **白方主视角只能由服务端产出**，这条需求本身就否掉了客户端录制。
    ///
    /// 采什么：房主手里就是权威数据 —— 每个玩家的 `PublicInfo`（位置/朝向/手持/角色/生死）
    /// 与 `Velocity`，正是 `Server.Game.Player.Move` 组 `_pkt_move` 用的那几项；
    /// 项圈自爆的时刻与位置也是服务端自己调 `OnDeadCollarBomb` 时知道的。
    ///
    /// ⚠ 本类**只产出安全帧**：`SpawnShot` / `MoveShot` / `AreaShot` / `EffectShot` / `DespawnShot`。
    /// **绝不产出 `AddShot`** —— 那正是回放卡死（客户端 `Corpse.SetInfo` 二次调用空引用）的毒源，
    /// 服务端合成的磁带因此天生免疫。
    /// </summary>
    internal static class EndReplayHostTape
    {
        /// <summary>采样间隔：白方在结算那一刻是站着的，5Hz 足够；镜头平移由我们自己的机位帧补足。</summary>
        private const float SampleInterval = 0.2f;

        /// <summary>环形缓冲时长（自爆窗口是 8s，留 45s 是为了"最后时段"等多种请求）。</summary>
        private const float KeepSeconds = 45f;

        /// <summary>爆炸后多久把被处决者从场上拿掉（他们死后是幽灵，留在场上会"站着不动"）。</summary>
        private const float BombDespawnDelay = 0.05f;

        /// <summary>移动帧的插值频率：采样只有 5Hz，镜头要跟人走就必须插值到更密，否则一顿一顿。</summary>
        private const float MoveHz = 20f;

        private sealed class Row
        {
            public float Time;
            public int Id;
            public PublicPlayerInfo Info;
            public float Velocity;
            /// <summary>这一帧时该玩家所在房间（服务端 `Player.CurrentArea`）—— 相机只能在自己的房间边界内活动，
            /// 所以每换一个机位对象就必须把房间一起换掉，否则镜头会被夹住（实测"移动一点点就光速变回来"）。</summary>
            public int RoomId;
            public bool IsLight;
        }

        private sealed class Bomb
        {
            public float Time;
            public int DeviceId;
            public PosInfo Pos;
        }

        private static readonly List<Row> Rows = new List<Row>();
        private static readonly List<Bomb> Bombs = new List<Bomb>();
        private static float _lastSample = -999f;

        public static void Clear()
        {
            Rows.Clear();
            Bombs.Clear();
            _lastSample = -999f;
        }

        /// <summary>
        /// 采样一次（由 `Managers.Update` 的补丁调用，内部按 <see cref="SampleInterval"/> 节流）。
        /// 时间基准用 `Managers.Game.ClientTime` —— 与客户端 `Recording()` 给普通帧打的时间戳同源，
        /// 这样我们合成的帧才能和客户端磁带落在同一个坐标系里。
        /// </summary>
        public static void Sample(float now)
        {
            if (now - _lastSample < SampleInterval)
                return;
            _lastSample = now;

            var room = GameRoom.Instance;
            if (room == null)
                return;

            AddRows(room.Players, now);
            AddRows(room.DeadPlayers, now);

            float keepFrom = now - KeepSeconds;
            if (Rows.Count > 0 && Rows[0].Time < keepFrom)
                Rows.RemoveAll(r => r.Time < keepFrom);
            if (Bombs.Count > 0 && Bombs[0].Time < keepFrom)
                Bombs.RemoveAll(b => b.Time < keepFrom);
        }

        /// <summary>记一枚项圈自爆（`GamePlayer.OnDeadCollarBomb` 的补丁调用）。</summary>
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

        /// <summary>
        /// 用房主缓冲合成一段磁带，写进 <paramref name="into"/>，返回帧数（0＝缓冲里没有可用样本，
        /// 调用方应退回占位磁带）。
        ///
        /// <paramref name="anchorId"/>：**隐形机位**的玩家 id —— 它同时充当首帧的 `SpawnShot`
        /// （客户端 `BeginTape` 的硬要求）与 `AreaShot.CameraTargetId`（镜头跟它走）；
        /// <paramref name="cameraTargetId"/>：机位挑不到时的**兜底锚点**（正常不生效）；
        /// <paramref name="lit"/>：`AreaShot.IsLight` ⇒ `Managers.Game.Darkness = !IsLight`
        /// （白方主视角要"非断电视野"，切到黑方时改 false）。
        ///
        /// ⚠ 镜头目标**绝不能指真人**：客户端 `ApplyArea`(:731) 会把 `CameraTargetId == _myPlayerId`
        /// 改写成 0，而 id 0 的替身是 `ForceSpawnReplayTemp` 造出来的半成品、停在 (-10,616)
        /// ⇒ 观众看到的是定格空白（实测踩过）。机位 id 由调用方按"无客户端实体优先、
        /// 不在画面里的死者次之"挑（见 `EndReplayFeature.FindHostTapeAnchors`）。
        ///
        /// 锚点会被标成 `IsGhost = true`：回放期间 `Player.Update` 每帧 `RefreshGhostVisual()` 会走
        /// "幽灵与骨架一起关掉"的分支 ⇒ 这个机位在画面上是**不可见**的（与隐藏观察者同一套做法）。
        /// </summary>
        public static int Build(List<SnapShot> into, float from, float to,
            int anchorId, int cameraTargetId, bool lit)
        {
            into.Clear();
            if (Rows.Count == 0 || to <= from)
                return 0;

            var anchor = Pick(anchorId, from) ?? Pick(cameraTargetId, from) ?? Pick(Rows[0].Id, from);
            if (anchor == null)
                return 0;

            var head = anchor.Info.Clone();
            head.State = EPlayerState.Idle;
            head.IsGhost = true;

            into.Add(new SnapShot
            {
                Type = ESnapShotType.SpawnShot,
                TimeStamp = from,
                Spawn = head
            });

            var camShot = MakeArea(from, anchor.Id, lit, anchor.RoomId);
            if (camShot != null)
                into.Add(camShot);

            var spawned = new HashSet<int> { anchor.Id };
            foreach (var r in Rows)
            {
                if (r.Time < from)
                    continue;
                if (r.Time > to)
                    break;

                if (spawned.Add(r.Id))
                {
                    into.Add(new SnapShot
                    {
                        Type = ESnapShotType.SpawnShot,
                        TimeStamp = r.Time,
                        Spawn = r.Info.Clone()
                    });
                }
                else
                {
                    into.Add(new SnapShot
                    {
                        Type = ESnapShotType.MoveShot,
                        TimeStamp = r.Time,
                        Move = new MoveSnapShot
                        {
                            PlayerId = r.Id,
                            Pos = r.Info.Pos?.Clone(),
                            LookLeft = r.Info.LookLeft,
                            Velocity = r.Velocity,
                            IsMove = true
                        }
                    });
                }
            }

            foreach (var b in Bombs)
            {
                if (b.Time < from || b.Time > to)
                    continue;

                into.Add(new SnapShot
                {
                    Type = ESnapShotType.EffectShot,
                    TimeStamp = b.Time,
                    Effect = new EffectSnapShot
                    {
                        Type = EEffectType.DyingVfx,
                        DeviceId = b.DeviceId,
                        Pos = b.Pos?.Clone()
                    }
                });

                into.Add(new SnapShot
                {
                    Type = ESnapShotType.DespawnShot,
                    TimeStamp = b.Time + BombDespawnDelay,
                    Despawn = b.DeviceId
                });
            }

            return into.Count;
        }

        /// <summary>
        /// 镜头/光照帧。**房间号来自服务端**（`Server.Game.Player.CurrentArea`），不再克隆房主客户端的
        /// `Managers.Map.CurrentArea` —— 后者是"房主自己所在的那个房间"，会把每一幕都摆到房主那边去
        /// （实测：白方巡礼整段停在房主所在的那个房间不动）。
        ///
        /// `AreaInfo.RoomId` 必须真实存在于 `RoomDic`，否则 `ChangeArea` 查 `RoomDic[RoomId]` 会抛。
        /// </summary>
        private static SnapShot MakeArea(float t, int cameraTargetId, bool lit, int roomId)
        {
            try
            {
                int room = roomId;
                if (room <= 0)
                    room = FirstRoom();
                if (room <= 0)
                    room = Managers.Map.CurrentArea?.AreaInfo?.RoomId ?? 0;
                if (room <= 0)
                    return null;

                return new SnapShot
                {
                    Type = ESnapShotType.AreaShot,
                    TimeStamp = t,
                    Area = new AreaSnapShot
                    {
                        AreaInfo = new AreaInfo { RoomId = room },
                        CameraTargetId = cameraTargetId,
                        IsLight = lit
                    }
                };
            }
            catch (Exception ex)
            {
                Plugin.Log.LogWarning($"[HS] EndReplay：合成镜头帧失败 — {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// 缓冲里第一个**合法**房间号 —— 只在调用方给不出房间时兜底。
        /// 以前这里的兜底是"问房主客户端你在哪"（`Managers.Map.CurrentArea`），而那正是
        /// "白方巡礼整段停在房主所在那间"的旧 bug 来源 ⇒ 现在先问服务端自己的采样。
        /// </summary>
        private static int FirstRoom()
        {
            foreach (var r in Rows)
            {
                if (r.RoomId > 0)
                    return r.RoomId;
            }
            return 0;
        }

        /// <summary>这个玩家在缓冲里有没有采样 —— 没有说明他不在房间里，不能当机位。</summary>
        public static bool HasRows(int id)
        {
            if (id <= 0)
                return false;
            foreach (var r in Rows)
            {
                if (r.Id == id)
                    return true;
            }
            return false;
        }

        private static int RoomOf(GamePlayer p)
        {
            try
            {
                return p?.CurrentArea?.Info?.RoomId ?? 0;
            }
            catch
            {
                return 0;
            }
        }

        private static bool IsLightOf(GamePlayer p)
        {
            try
            {
                return p?.CurrentArea?.IsLight ?? true;
            }
            catch
            {
                return true;
            }
        }

        /// <summary>取"离 <paramref name="t"/> 最近的、且不晚于 t 的"样本；没有更早的就退用最早的。</summary>
        private static Row Pick(int id, float t)
        {
            Row best = null;
            float bestScore = float.MaxValue;
            foreach (var r in Rows)
            {
                if (id > 0 && r.Id != id)
                    continue;

                // "晚于 t"的样本位置已经跑过头了 ⇒ 加大惩罚，优先取该时刻之前的
                float score = Math.Abs(r.Time - t) + (r.Time <= t ? 0f : 1000f);
                if (score < bestScore)
                {
                    bestScore = score;
                    best = r;
                }
            }
            return best;
        }

        // ── 运镜合成（P2/P3）────────────────────────────────────────────
        /// <summary>
        /// 合成「白方巡礼」：机位（隐形幽灵）沿**贪心最近未拍摄**的顺序在白方之间走，
        /// 站间是**直线高速平移**（不是瞬移 —— 平移期间机位角色自己在动，镜头自然跟过去），
        /// 全程 `IsLight = true`（非断电视野）。
        ///
        /// 每站停留 `(窗口 − (N−1)×平移) ÷ N` 秒 ⇒ **人越多切得越急**。
        /// 镜头目标就是这具机位自己（一枚 `AreaShot` 钉住即可），所以整个巡礼只有一次"切镜头"。
        /// </summary>
        public static int BuildWhiteTour(List<SnapShot> into, float from, float to,
            int anchorId, List<int> subjects, float panSec)
        {
            into.Clear();
            if (Rows.Count == 0 || to <= from || subjects == null || subjects.Count == 0)
                return 0;

            var anchor = Pick(anchorId, from) ?? Pick(subjects[0], from);
            if (anchor == null)
                return 0;

            var stops = OrderTour(subjects, from);
            if (stops.Count == 0)
                return 0;

            var head = anchor.Info.Clone();
            head.State = EPlayerState.Idle;
            head.IsGhost = true;

            into.Add(new SnapShot { Type = ESnapShotType.SpawnShot, TimeStamp = from, Spawn = head });

            var area = MakeArea(from, anchor.Id, true, Pick(stops[0], from)?.RoomId ?? anchor.RoomId);
            if (area != null)
                into.Add(area);

            EmitWorld(into, from, to, anchor.Id, null, new HashSet<int>(subjects));
            EmitTour(into, from, to, anchor.Id, stops, panSec, anchor.RoomId);
            return into.Count;
        }

        /// <summary>
        /// 合成「黑方收尾」：镜头切到黑方、`IsLight = false`（**断电视野**），跟着他走 N 秒。
        /// 镜头目标的移动会按 <see cref="MoveHz"/> 插值（采样只有 5Hz，不插值镜头会顿）。
        /// </summary>
        public static int BuildBlackAct(List<SnapShot> into, float from, float to, int anchorId, int blackId)
        {
            into.Clear();
            if (Rows.Count == 0 || to <= from)
                return 0;

            var anchor = Pick(anchorId, from) ?? Pick(blackId, from) ?? Pick(Rows[0].Id, from);
            if (anchor == null)
                return 0;

            // ★ 镜头目标永远是**机位幽灵自己**，绝不指任何真实玩家：
            //   客户端 `ApplyArea` 会把"本机玩家"改写成 id=0，而 id=0 的替身是
            //   `ForceSpawnReplayTemp` 造出来的半成品、从来没被摆过位 —— 实测它的 transform 停在
            //   (-10, 616)，镜头跟过去就是一片空白（正是"损坏镜头"）。
            //   机位的 id 取自"已死的人"，真人客户端不可能等于它 ⇒ 不会被改写。
            var blackRow = Pick(blackId, from);
            var head = anchor.Info.Clone();
            head.State = EPlayerState.Idle;
            head.IsGhost = true;

            into.Add(new SnapShot { Type = ESnapShotType.SpawnShot, TimeStamp = from, Spawn = head });

            var area = MakeArea(from, anchor.Id, false, blackRow?.RoomId ?? anchor.RoomId);
            if (area != null)
                into.Add(area);

            var visible = new HashSet<int>();
            if (blackId > 0)
                visible.Add(blackId);

            EmitWorld(into, from, to, anchor.Id, null, visible);
            // 机位沿"黑方走过的那条路"移动 ⇒ 镜头跟着黑方，而不是跟着被藏起来的本机对象
            EmitInterpolated(into, from, to, anchor.Id, blackId);
            return into.Count;
        }

        /// <summary>
        /// 把世界里的玩家铺成一串帧：第一次见到发 `SpawnShot`（带完整信息），之后发 `MoveShot`。
        /// <paramref name="omitAnchor"/> 是首帧锚点（已由 head 帧装配好）；
        /// <paramref name="omit"/> 里的玩家整个跳过（例如由我们自己的机位路径/插值驱动的那个）。
        /// </summary>
        private static void EmitWorld(List<SnapShot> into, float from, float to, int omitAnchor,
            HashSet<int> omit, HashSet<int> forceVisible)
        {
            var seen = new HashSet<int> { omitAnchor };
            if (omit != null)
            {
                foreach (var id in omit)
                    seen.Add(id);
            }

            foreach (var r in Rows)
            {
                if (r.Time < from)
                    continue;
                if (r.Time > to)
                    break;
                if (omit != null && omit.Contains(r.Id))
                    continue;

                if (seen.Add(r.Id))
                {
                    var info = r.Info.Clone();
                    if (forceVisible != null && forceVisible.Contains(r.Id))
                    {
                        // ★ 服务端在项圈自爆那一刻就把白方标成 `IsGhost`，而客户端在回放期间
                        //   `RefreshGhostVisual()` 会把幽灵的骨架与幽灵替身**一起关掉** ⇒
                        //   整幕"和哑剧似的没有任何人"。回放里要看的是他们**被处决前站着的样子**，
                        //   所以这里显式改回可见 + Idle。
                        info.IsGhost = false;
                        info.State = EPlayerState.Idle;
                    }

                    into.Add(new SnapShot
                    {
                        Type = ESnapShotType.SpawnShot,
                        TimeStamp = r.Time,
                        Spawn = info
                    });
                }
                else
                {
                    into.Add(new SnapShot
                    {
                        Type = ESnapShotType.MoveShot,
                        TimeStamp = r.Time,
                        Move = new MoveSnapShot
                        {
                            PlayerId = r.Id,
                            Pos = r.Info.Pos?.Clone(),
                            LookLeft = r.Info.LookLeft,
                            Velocity = r.Velocity,
                            IsMove = true
                        }
                    });
                }
            }
        }

        /// <summary>
        /// 贪心「最近且还没拍过」：从当前机位位置出发，每次挑离得最近、尚未到访的人。
        /// （点名要的路线 —— 也是"直线飞过去"最自然的顺序。）
        /// </summary>
        private static List<int> OrderTour(List<int> subjects, float at)
        {
            var cand = new List<int>();
            foreach (var id in subjects)
            {
                if (id <= 0 || cand.Contains(id))
                    continue;
                if (Pick(id, at) == null)
                    continue;
                cand.Add(id);
            }

            var order = new List<int>();
            PosInfo cur = null;
            while (cand.Count > 0)
            {
                int bestIdx = 0;
                float bestD = float.MaxValue;
                for (int i = 0; i < cand.Count; i++)
                {
                    float d = Dist(cur, Pick(cand[i], at)?.Info?.Pos);
                    if (d < bestD)
                    {
                        bestD = d;
                        bestIdx = i;
                    }
                }

                int chosen = cand[bestIdx];
                cand.RemoveAt(bestIdx);
                order.Add(chosen);
                cur = Pick(chosen, at)?.Info?.Pos;
            }
            return order;
        }

        /// <summary>巡礼机位的时间表：每站停留 + 站间直线平移；窗口不够时压缩平移，保证每站都有停留。</summary>
        private static void EmitTour(List<SnapShot> into, float from, float to, int dollyId, List<int> stops,
            float panSec, int fallbackRoom)
        {
            float span = to - from;
            int n = stops.Count;
            float pan = Math.Max(0.05f, panSec);
            float dwell = (span - (n - 1) * pan) / n;
            if (dwell < 0.1f)
            {
                pan = Math.Max(0.02f, span / n * 0.25f);
                dwell = Math.Max(0.02f, (span - (n - 1) * pan) / n);
            }

            var pos = Pick(stops[0], from)?.Info?.Pos;
            float t = from;

            if (pos != null)
                EmitPath(into, dollyId, from, from, pos, null);        // 开场先把机位摆到第一个目标

            for (int i = 0; i < n; i++)
            {
                if (pos != null)
                {
                    // ★ 每到一站就换一次房间：相机的活动范围是**当前房间的边界**，
                    //   不换房间的话镜头刚出边界就被夹回来（实测"往其他位置移动，但移动一点点就光速变回来"）。
                    var stopArea = MakeArea(t, dollyId, true, Pick(stops[i], from)?.RoomId ?? fallbackRoom);
                    if (stopArea != null)
                        into.Add(stopArea);

                    EmitPath(into, dollyId, t, t + dwell, pos, null);  // 停留（原地）
                    t += dwell;
                }

                if (i + 1 >= n)
                    break;

                var next = Pick(stops[i + 1], from)?.Info?.Pos;
                if (next == null)
                    continue;

                EmitPath(into, dollyId, t, t + pan, pos, next);        // 直线高速平移
                t += pan;
                pos = next;
            }
        }

        /// <summary>
        /// 给"镜头相关的那个人"补移动帧：从 <paramref name="a"/> 到 <paramref name="b"/> 按 20Hz 插值。
        /// <paramref name="b"/> 为 null ＝ 原地停留（只发一枚定位帧）。
        /// </summary>
        private static void EmitPath(List<SnapShot> into, int id, float t0, float t1, PosInfo a, PosInfo b)
        {
            if (a == null)
                return;

            if (b == null)
            {
                into.Add(MoveAt(id, t0, a));
                return;
            }

            int steps = Math.Max(1, (int)Math.Round((t1 - t0) * MoveHz));
            for (int i = 1; i <= steps; i++)
            {
                float k = i / (float)steps;
                into.Add(MoveAt(id, t0 + (t1 - t0) * k, new PosInfo
                {
                    X = a.X + (b.X - a.X) * k,
                    Y = a.Y + (b.Y - a.Y) * k
                }));
            }
        }

        /// <summary>
        /// 机位移动帧的 `Velocity`。**这一项以前写 `0f`，是"哑剧 + 镜头漂移被拽回"的真凶。**
        ///
        /// 客户端 `Player.UpdateMove()`（`FixedUpdate` 调用，:16892）是这样逼近 `TargetPos` 的：
        ///     num = Velocity * DeltaTime;
        ///     if (Velocity == 0 &amp;&amp; transform.position != target)
        ///         num = Mathf.Max(_lastVelocity, 201.59999f) * DeltaTime;   // ← 速度被钉在 201.6/秒
        ///     if (dist² &lt; num²) transform.position = target;                  // 够近才瞬移到位
        ///     else               transform.position += dir * num;             // 否则一帧挪一点
        ///
        /// 而我们的机位帧要求它**一瞬间**跨越几千单位（开场从锚点位置跳到首站、站间 0.2s 直线平移、
        /// `EmitInterpolated` 沿黑方路径飞），201.6/秒根本走不到 ⇒ 机位（＝`AreaShot.CameraTargetId`，
        /// 镜头就是跟着它）**整幕都在半路上**：观众看到画面往别处"漂"，而每站那枚 `AreaShot` 又把
        /// 镜头**立即**摆回房间内（客户端日志 `카메라 즉시 이동 → (x,y)` 就是它）⇒ 实测观感正是
        /// "画面时不时往其他方向漂、然后被拽回原点"；同时镜头**从没到过演员那里** ⇒ 整幕像哑剧。
        ///
        /// 给一个"每物理帧都判定为够近"的值 ⇒ 客户端**逐帧精确贴合**我们 20Hz 的插值点，
        /// 于是平移就是我们画的直线、停留就是真的停住。
        /// ⚠ 只对**机位**这么干：`Velocity` 在客户端 `Player` 里只有 `UpdateMove` 读它
        /// （`SetRigidBodyVelocity` 用的是 `Rigidbody.linearVelocity`，另一回事，已核对），
        /// 所以调大没有任何其它副作用；真实演员仍用采样到的真实速度。
        /// </summary>
        private const float HostMoveVelocity = 1000000f;

        private static SnapShot MoveAt(int id, float t, PosInfo pos)
        {
            return new SnapShot
            {
                Type = ESnapShotType.MoveShot,
                TimeStamp = t,
                Move = new MoveSnapShot
                {
                    PlayerId = id,
                    Pos = pos,
                    LookLeft = false,
                    Velocity = HostMoveVelocity,
                    IsMove = true
                }
            };
        }

        /// <summary>把某个玩家在窗口内的样本插值到 <see cref="MoveHz"/>，但**用另一个 id 发出来**
        /// （镜头不指真实玩家，而是让机位沿那个人走过的路走）。</summary>
        private static void EmitInterpolated(List<SnapShot> into, float from, float to, int emitId, int srcId)
        {
            var pts = new List<Row>();
            foreach (var r in Rows)
            {
                if (r.Id != srcId)
                    continue;
                if (r.Time < from - 1f)
                    continue;
                if (r.Time > to + 1f)
                    break;
                pts.Add(r);
            }

            for (int i = 0; i + 1 < pts.Count; i++)
            {
                float t0 = Math.Max(from, pts[i].Time);
                float t1 = Math.Min(to, pts[i + 1].Time);
                if (t1 <= t0)
                    continue;
                EmitPath(into, emitId, t0, t1, pts[i].Info.Pos, pts[i + 1].Info.Pos);
            }
        }

        private static float Dist(PosInfo a, PosInfo b)
        {
            if (a == null || b == null)
                return float.MaxValue;
            float dx = a.X - b.X;
            float dy = a.Y - b.Y;
            return dx * dx + dy * dy;
        }

        private static void AddRows(List<GamePlayer> players, float now)
        {
            if (players == null)
                return;

            foreach (var p in players)
            {
                var info = p?.PublicInfo;
                if (info == null)
                    continue;

                Rows.Add(new Row
                {
                    Time = now,
                    Id = info.PlayerId,
                    Info = info.Clone(),
                    Velocity = p.Velocity,
                    RoomId = RoomOf(p),
                    IsLight = IsLightOf(p)
                });
            }
        }
    }
}
