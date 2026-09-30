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

        /// <summary>
        /// ⚠ 已**移除**"爆炸后把被处决者移出场上"的那枚 `DespawnShot`（原 `BombDespawnDelay = 0.05f`）：
        /// 它会在爆炸后 0.05s 就把对象 `SetActive(false)`，掐断仍在跑的项圈闪烁 DOTween
        /// ⇒ 既不闪、也没有音效（实测：旧版能闪、新版全无，而爆炸帧的生成代码两版一模一样）。
        /// 详见 <see cref="Build"/> 里爆炸循环处的完整说明。
        /// </summary>

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
        /// 用房主缓冲合成一段磁带（**只在客户端磁带没到时兜底**），返回帧数（0＝没有可用样本）。
        ///
        /// ★ 这里有两个**必须分开**的角色 —— 之前把它们合成一个，两个方向都踩了坑：
        ///
        ///   · <paramref name="ghostId"/>：**首帧的玩家**（客户端 `BeginTape` 的硬要求），
        ///     它同时决定**剪影打谁**（`Players[_blackId].ChangeSilhouette(true)`，`_blackId` 就是首帧 id）。
        ///     打在真人身上就是"视角涂黑"，用户明确不要 ⇒ 这里必须是**另一个已死者**，
        ///     标成 `IsGhost = true`：回放期间 `Player.Update` 每帧 `RefreshGhostVisual()` 走
        ///     case 2（幽灵替身与骨架一起关）⇒ 他在画面上不可见，剪影落在他身上等于没落
        ///     ⇒ 真人本色出场。**他只占"剪影槽位"，不承担镜头。**
        ///
        ///   · <paramref name="cameraTargetId"/>：**镜头跟着谁**（`ApplyArea` 里
        ///     `Managers.Game.CameraTarget = GetDevice(area.CameraTargetId)`）。
        ///     必须指向**录制者本人** —— 他本人在哪个房间，镜头就在哪个房间；他走门换房间，
        ///     `AreaShot` 是那一刻录下来的。**原版根本没有"把相机送到别的房间取景"的实现**
        ///     （`ChangeArea`(:29605) 只有换房间 / 设相机目标 / 设黑灯，相机被约束在当前房间边界内），
        ///     自造跨房间机位必然"漂出去又被边界拽回"。
        ///     （观众本人就是录制者时，客户端 `ApplyArea` 会把 `== _myPlayerId` 改写成 0
        ///      ⇒ 镜头跟着 id 0 的本机替身，`ApplyMove` 同步改写 ⇒ 替身沿他的真实路径走 ✓ 原版行为。）
        ///
        ///   · <paramref name="lit"/>：`AreaShot.IsLight` ⇒ `Darkness = !IsLight`（黑方收尾幕要断电视野）。
        ///
        /// ⚠ **录制者必须强制 `IsGhost = false`**：被处决者在服务端已是幽灵（`MakeSpectatorGhost` 置
        /// `IsGhost=true`），照抄采样值会让客户端把他整段隐形 ⇒ 画面没人。客户端磁带里录制者那枚帧
        /// 也是显式清成 `false` 的（`RecordAllType` :31799）⇒ 这里对齐同一语义。
        /// </summary>
        public static int Build(List<SnapShot> into, float from, float to,
            int ghostId, int cameraTargetId, bool lit)
        {
            into.Clear();
            if (Rows.Count == 0 || to <= from)
                return 0;

            var ghost = Pick(ghostId, from) ?? Pick(Rows[0].Id, from) ?? Pick(cameraTargetId, from);
            if (ghost == null)
                return 0;

            var head = ghost.Info.Clone();
            head.State = EPlayerState.Idle;
            head.IsGhost = true;

            into.Add(new SnapShot
            {
                Type = ESnapShotType.SpawnShot,
                TimeStamp = from,
                Spawn = head
            });

            // 相机跟着**录制者本人**，房间取他那一刻所在的房间（不是幽灵的房间）。
            var camRow = Pick(cameraTargetId, from);
            var camShot = MakeArea(from, cameraTargetId, lit, camRow?.RoomId ?? ghost.RoomId);
            if (camShot != null)
                into.Add(camShot);

            var spawned = new HashSet<int> { ghost.Id };
            foreach (var r in Rows)
            {
                if (r.Time < from)
                    continue;
                if (r.Time > to)
                    break;

                if (spawned.Add(r.Id))
                {
                    var info = r.Info.Clone();
                    if (r.Id == cameraTargetId)
                    {
                        // 录制者本色出场（他被处决后服务端标的是幽灵）
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

            int bombAdded = 0;
            var bombIds = new List<int>();
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

                // ★ **不发 `DespawnShot`** —— 这就是"项圈闪烁不见了"的真凶（diff 实测）。
                //
                //   爆炸帧（`EffectShot{DyingVfx}`）只是**启动**一段客户端动画：`ApplyEffect`
                //   → `PlayDying()` → DOTween 序列（`NecklaceWork` 音效 → 1s 停顿 → 4 轮**越来越快**的
                //   项圈闪烁 → `StopDying()`，整段约 2.5s）。它全程持有玩家身上的 `_collar` 子物体。
                //
                //   而 `DespawnShot` → `ApplyDespawn` → `PlayerManager.Despawn(id)` → 对象
                //   `SetActive(false)`。原本这枚帧排在爆炸后 0.05s，于是**动画刚启动就把项圈关掉** ⇒
                //   既不闪、也没有音效（用户实测：旧版能闪、新版全无，而爆炸帧生成代码两版一模一样）。
                //   旧版之所以没事：那一版的 `cameraTargetId` 是一具"幽灵机位"，被处决者压根没被 spawn，
                //   `Despawn` 找不到人 ⇒ no-op ⇒ 项圈对象全程在场。
                //
                //   代价：被处决者会一直站在画面里（不会被自动移走）。这是**可接受的** ——
                //   回放结束本来就会整体清场，而"能看到他被处决前站着的样子"正是这段的意义。

                bombAdded++;
                bombIds.Add(b.DeviceId);
            }

            // 诊断：这一段窗口里到底有几枚爆炸帧、分别属于谁。爆炸帧靠 `DeviceId` 找**活着的
            // 玩家对象**播放 `PlayDying()`（项圈闪烁 + NecklaceWork 音效），对象不在场就静默不出。
            // 下一局日志据此一次分清"没生成"与"生成了但播不出来"，不再靠猜。
            Plugin.Log.LogInfo($"[HS] EndReplayHostTape：兜底段 镜头=#{cameraTargetId} 剪影=#{ghost.Id}"
                + $" 窗口=[{from:F2},{to:F2}] ⇒ 爆炸帧 {bombAdded} 枚"
                + (bombAdded > 0 ? $"（DeviceId=[{string.Join(",", bombIds)}]）" : "（本段不含爆炸时刻）")
                + $"，缓冲内爆炸记录共 {Bombs.Count} 条。");

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
