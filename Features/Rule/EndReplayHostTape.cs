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
