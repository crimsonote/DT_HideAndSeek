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
        /// 「自爆开始 → 真正死亡（人消失）」的秒数 = 原版 `OnDeadCollarBomb` 里
        /// `PushAfter(6000, OnDead)` 的 6000ms（`:176023`）。与 `EndReplayFeature.CollarToDeadSec` 同义。
        ///
        /// ★ 这里曾写成 `0.05f`（"爆炸后 0.05s 把人移走"），**完全错的**：
        ///   `PlayDying()` 的项圈闪烁总长 **5.875s**（含**前 1.0s 哑期**：`_collar` 亮起但
        ///   `intensity=0` + 只响 `NecklaceWork`，`:17215-17224`），声音还挂在玩家自己的
        ///   `AudioSource` 上（`Sound.PlayWorld(key, obj)` `:36498`）⇒ 0.05s 就把对象 `SetActive(false)`
        ///   会**同时掐掉闪烁与音效**。真正的"爆炸"（人消失 + 生成焦尸）在 **t+6**（`:175990-175997`
        ///   的 `CreateBombCorpse` + `S_DESPAWN`）⇒ 消失帧必须排在 `b.Time + 6f`。
        ///
        /// ⚠ 另一条误判也要纠正：曾把"看不到闪烁"归因于这枚 `DespawnShot`（`ff9b6a8` 干脆删掉它），
        ///   但实测那一版**仍然看不到闪烁** ⇒ 不是它。真因见 <see cref="Build"/> 里爆炸循环处的说明。
        /// </summary>
        private const float BombDespawnDelay = 6f;

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
        /// ★★ **术语先钉死（我反复搞混的两组概念）**
        ///
        ///   · **录制者（recorder）**：谁**录**了这卷磁带（收到 `S_RECORD_REPLAY`、回 `C_TAPE`）。
        ///     只有**真人**才可能是录制者。
        ///   · **被拍摄者（subject）**：我们想让**镜头拍**的那个人（本段的"主角"）。
        ///
        ///     客户端磁带路径：录制者 **就是** 被拍摄者本人（他录自己的视角）⇒ 两者重合。
        ///     服务端兜底路径（假人）：**根本没有录制者**（假人没有客户端，磁带是本类合成的），
        ///     只有被拍摄者 —— 调用方传进来的 `cameraTargetId` 就是他（**不是**录制者）。
        ///
        /// ★ 这里有两个**必须分开**的角色 —— 之前把它们合成一个，两个方向都踩了坑：
        ///
        ///   · <paramref name="ghostId"/>：**首帧的玩家**（客户端 `BeginTape` 的硬要求），
        ///     它同时决定**剪影打谁**（`Players[_blackId].ChangeSilhouette(true)`，`_blackId` 就是首帧 id）。
        ///     打在真人身上就是"视角涂黑"，用户明确不要 ⇒ 这里必须是**另一个已死者**，
        ///     标成 `IsGhost = true`：回放期间 `Player.Update` 每帧 `RefreshGhostVisual()` 走
        ///     case 2（幽灵替身与骨架一起关）⇒ 他在画面上不可见，剪影落在他身上等于没落
        ///     ⇒ 真人本色出场。**他只占"剪影槽位"，不承担镜头，且必须与镜头目标不是同一人。**
        ///
        ///   · <paramref name="cameraTargetId"/>：**镜头跟着谁**（`ApplyArea` 里
        ///     `Managers.Game.CameraTarget = GetDevice(area.CameraTargetId)`）
        ///     ⇒ 必须是**被拍摄者本人**（真人磁带那一路就是原磁带里的录制者）。
        ///     他在哪个房间镜头就在哪个房间；他走门换房间，`AreaShot` 是那一刻录下来的。
        ///     **原版根本没有"把相机送到别的房间取景"的实现**
        ///     （`ChangeArea`(:29605) 只有换房间 / 设相机目标 / 设黑灯，相机被约束在当前房间边界内），
        ///     自造跨房间机位必然"漂出去又被边界拽回"。
        ///     （观众本人就是被拍摄者时，客户端 `ApplyArea` 会把 `== _myPlayerId` 改写成 0
        ///      ⇒ 镜头跟着 id 0 的本机替身，`ApplyMove` 同步改写 ⇒ 替身沿他的真实路径走 ✓ 原版行为。）
        ///
        ///   · <paramref name="lit"/>：`AreaShot.IsLight` ⇒ `Darkness = !IsLight`（黑方收尾幕要断电视野）。
        ///
        /// ⚠ **被拍摄者必须强制 `IsGhost = false`**：被处决者在服务端已是幽灵（`MakeSpectatorGhost` 置
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

            // 相机跟着**被拍摄者**（不是剪影替身、也不是"录制者"——服务端兜底这条路没有录制者），
            // 房间取他那一刻所在的房间。
            var camRow = Pick(cameraTargetId, from);
            var camShot = MakeArea(from, cameraTargetId, lit, camRow?.RoomId ?? ghost.RoomId);
            if (camShot != null)
                into.Add(camShot);

            // ★ 本幕"该被看见的人"（**带时间上限**，不能一刀切整幕）：
            //
            //   · 被拍摄者本人：整段本色出场（他就是这段的主角）。
            //   · 正在自爆的人（`Bombs` 的 DeviceId）：**只在他"消失"之前**本色出场
            //     （`r.Time <= b.Time + BombDespawnDelay`，即 t0+6 之前）。
            //
            //   为什么自爆者也要可见：闪烁靠 `EffectShot{DyingVfx}` 触发后，是一段**纯客户端 DOTween**
            //   （`DOTween.To` 的目标是 lambda、不绑 GameObject ⇒ 段切换的 despawn/重新 spawn
            //   **不会中断**它，唯一终止者是 5.875s 处的 `StopDying`）。但对象若被标成幽灵，
            //   就"在闪却看不见"。而服务端在自爆那一刻起会把他们逐步标成 `IsGhost=true`，
            //   采样值照抄就会让后面几段的人隐形 ⇒ 强制本色出场。
            //
            //   ⚠ **必须带上限**：否则到了「黑方收尾」那一段（t0+6.5 之后），他们会被重新 spawn 成
            //   活人 ⇒ 看起来"炸完又站起来"。
            var visibleUntil = new Dictionary<int, float>();
            foreach (var b in Bombs)
                visibleUntil[b.DeviceId] = b.Time + BombDespawnDelay;

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
                    bool show = r.Id == cameraTargetId
                                || (visibleUntil.TryGetValue(r.Id, out float until) && r.Time <= until);
                    if (show)
                    {
                        // 本色出场（他们被处决后服务端标的是幽灵；但在"消失"之前这一段里还在闪）
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
            int goneAdded = 0;
            var bombIds = new List<int>();
            foreach (var b in Bombs)
            {
                // ① **触发帧**：只要"自爆开始"落在本段窗口内就发。
                //    它必须落在**自爆幕的第一段**：`PlayDying()` 前 1.0s 是"有声无光"的哑期
                //    （`_collar` 亮起但 intensity=0 + 只响 `NecklaceWork`，`:17215-17224`），
                //    之后才逐轮闪起来（总长 5.875s）。如果它落在最后一段且那段只剩 0.5s，
                //    整段都在哑期里就被切走 ⇒ **只听得见声音、画面什么都没有**（实测症状）。
                if (b.Time >= from && b.Time <= to)
                {
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
                    bombAdded++;
                    bombIds.Add(b.DeviceId);
                }

                // ② **消失帧**：真正死亡在 `b.Time + 6s`（`PushDown`→`OnDead`：`CreateBombCorpse`
                //    + 广播 `S_DESPAWN`，`:175990-175997`）—— 回放里复现"人没了"只有这条安全路
                //    （尸体 `AddShot` 会触发 `Corpse.SetInfo` 二次调用崩溃，绝不能用）。
                //    ⚠ 它落在**与触发帧不同的一段**（通常是最末段），所以必须**独立判断窗口**，
                //      不能用上面那个 `b.Time ∈ [from,to]` 的条件（否则永远发不出去）。
                float goneAt = b.Time + BombDespawnDelay;
                if (goneAt >= from && goneAt <= to)
                {
                    into.Add(new SnapShot
                    {
                        Type = ESnapShotType.DespawnShot,
                        TimeStamp = goneAt,
                        Despawn = b.DeviceId
                    });
                    goneAdded++;
                }
            }

            // 诊断：本段触发了几次闪烁、移走了几个人（触发一次即全程闪 5.875s，跨段）。
            Plugin.Log.LogInfo($"[HS] EndReplayHostTape：兜底段 镜头=#{cameraTargetId} 剪影=#{ghost.Id}"
                + $" 窗口=[{from:F2},{to:F2}] ⇒ 触发闪烁 {bombAdded} 枚"
                + (bombAdded > 0 ? $"（DeviceId=[{string.Join(",", bombIds)}]）" : "（沿用前段已触发的闪烁）")
                + $"，本段移走 {goneAdded} 人，本色出场上限 {visibleUntil.Count} 人，"
                + $"缓冲内自爆记录 {Bombs.Count} 条。");

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
