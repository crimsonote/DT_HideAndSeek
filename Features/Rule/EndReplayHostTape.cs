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

        private sealed class Row
        {
            public float Time;
            public int Id;
            public PublicPlayerInfo Info;
            public float Velocity;
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
        /// <paramref name="anchorId"/>：首帧的"主视角/锚点" —— 负责**装配与剪影**（必须是 `SpawnShot`，
        /// 这是客户端 `BeginTape` 的硬要求），**不控制镜头**；
        /// <paramref name="cameraTargetId"/>：真正决定镜头的 `AreaShot.CameraTargetId`
        /// （`MapManager.ChangeArea` 里 `Managers.Game.CameraTarget = GetDevice(area.CameraTargetId)` 那一行）；
        /// <paramref name="lit"/>：`AreaShot.IsLight` ⇒ `Managers.Game.Darkness = !IsLight`
        /// （白方主视角要"非断电视野"，切到黑方时改 false）。
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

            int cam = cameraTargetId > 0 ? cameraTargetId : anchor.Id;

            var head = anchor.Info.Clone();
            head.State = EPlayerState.Idle;
            head.IsGhost = true;

            into.Add(new SnapShot
            {
                Type = ESnapShotType.SpawnShot,
                TimeStamp = from,
                Spawn = head
            });

            var camShot = MakeArea(from, cam, lit);
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

        /// <summary>镜头/光照帧：`AreaInfo` 必须来自真实区域（`ChangeArea` 会查 `RoomDic[RoomId]`，非法就抛）。</summary>
        private static SnapShot MakeArea(float t, int cameraTargetId, bool lit)
        {
            try
            {
                var cur = Managers.Map.CurrentArea;
                if (cur == null)
                    return null;

                var area = cur.Clone();
                area.CameraTargetId = cameraTargetId;
                area.IsLight = lit;

                return new SnapShot
                {
                    Type = ESnapShotType.AreaShot,
                    TimeStamp = t,
                    Area = area
                };
            }
            catch (Exception ex)
            {
                Plugin.Log.LogWarning($"[HS] EndReplay：合成镜头帧失败 — {ex.Message}");
                return null;
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
                    Velocity = p.Velocity
                });
            }
        }
    }
}
