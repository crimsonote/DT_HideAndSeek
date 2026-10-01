using System.Collections.Generic;
using Protocol;

namespace HideAndSeek.Features.Replay
{
    /// <summary>
    /// 【服务端合成器】—— 把 <see cref="HostRecorder"/> 的采样铺成"世界帧"。
    ///
    /// 它在整条链路上的位置：**素材来源之一**（见 docs/回放-规格.md §4）。
    ///   · 只有"客户端磁带拿不到"时才用它：假人当主角、主角已死、主角已退出房间。
    ///   · 它**不产**首帧、全员出场帧、`NormalTimeEdit` —— 那些是 <see cref="TapeAssembler"/> 的职责。
    ///     分工必须清楚，否则同一个人会被发两枚 `SpawnShot`。
    ///   · 它**绝不产** `AddShot` —— 那是会让客户端整段卡死的毒帧（规格 §1·C8）。
    ///
    /// ★ 与旧实现（`EndReplayHostTape.Build`）的实质改进：
    ///   1. 数据来自 `HostRecorder`，采样与客户端发包同频（旧的死板 5Hz）；
    ///   2. **主角换房间时会补发 `AreaShot`** —— 旧实现整幕只发一枚，主角中途换房间后
    ///      相机会"漂出去又被房间边界拽回"（原版没有跨房间机位）；
    ///   3. 光照不再硬编码：`dark = false` 时**跟随采样到的真实光照**（旧实现的 `IsLight` 采了从未被读）。
    ///
    /// 已知限制（如实保留，不做假修补）：服务端记的是收到 `C_MOVE` 之后的权威位置，
    /// 而客户端画面是本地先行的 ⇒ **恒定落后约一个 RTT**。这正是"延迟刀"观感的来源。
    /// </summary>
    internal static class HostSynth
    {
        /// <summary>「自爆开始 → 真正死亡」的秒数 = `OnDeadCollarBomb` 里 `PushAfter(6000, OnDead)`。</summary>
        public const float CollarToDeadSec = 6f;

        /// <summary>
        /// 把 <paramref name="window"/> 内的世界铺成一串帧（**不含首帧/全员出场帧/时间编辑**）。
        /// </summary>
        /// <param name="window">时间区间（房主时钟）。</param>
        /// <param name="subjectId">镜头要拍谁 —— 他会在哪个房间，相机就在哪个房间。</param>
        /// <param name="dark">true = 断电视野（"黑方收尾幕"是刻意的艺术选择）；false = 跟随真实光照。</param>
        /// <param name="bombBlackout">自爆时要不要补一枚全屏压暗（原版的压暗**无法立刻取消**，会持续 2 秒，
        /// 与下一幕的断电视野叠加会完全看不清 ⇒ 默认关）。</param>
        public static List<SnapShot> Frames(ReplayWindow.Span window, int subjectId, bool dark, bool bombBlackout)
        {
            var shots = new List<SnapShot>(256);
            if (!window.IsValid || subjectId <= 0)
                return shots;

            var samples = HostRecorder.Range(window.From, window.To);

            // ── 窗口起点的"全员出场帧" ──────────────────────────────────
            // 它有两个作用：
            //   ① 让每台客户端把自己 id 0 的替身装配好（缺了 ⇒ 换道具镜头打在空引用上 ⇒ 整段卡死）；
            //   ② 它就是"谁在画面里"的**唯一依据** —— `TapeAssembler` 从这个列表判断要补谁的帧，
            //      `SilhouetteResolver` 也从它判断剪影该落在谁身上。不做任何 AOI/房间的近似推断。
            // 服务端这条路上没有 AOI 信息，所以按"有采样的所有人"给（保守：宁可多装配，不可漏）。
            foreach (var info in HostRecorder.Roster(window.From))
            {
                if (info == null)
                    continue;
                shots.Add(new SnapShot
                {
                    Type = ESnapShotType.SpawnShot,
                    TimeStamp = window.From,
                    Spawn = info.Clone(),
                });
            }

            // ── 相机 ────────────────────────────────────────────────────
            // 原版只有 `ChangeArea` 一条路能改相机目标，而且 `RoomId` 必须合法
            // （客户端 `RoomDic[RoomId]` 直接索引，0 会抛 `KeyNotFoundException`）。
            int room = RoomOrFallback(subjectId, window.From, samples);
            AddArea(shots, window.From, subjectId, LightFor(subjectId, window.From, dark), room);
            int lastRoom = room;

            // ── 世界 ────────────────────────────────────────────────────
            var spawned = new HashSet<int>();
            foreach (var s in samples)
            {
                // 主角换房间 ⇒ 补一枚 `AreaShot`，否则相机会被夹在旧房间里
                if (s.Id == subjectId && s.RoomId > 0 && s.RoomId != lastRoom)
                {
                    lastRoom = s.RoomId;
                    AddArea(shots, s.Time, subjectId, LightFor(subjectId, s.Time, dark), s.RoomId);
                }

                if (spawned.Add(s.Id))
                {
                    var info = s.Info.Clone();
                    ApplyVisibility(info, s, subjectId);
                    shots.Add(new SnapShot
                    {
                        Type = ESnapShotType.SpawnShot,
                        TimeStamp = s.Time,
                        Spawn = info,
                    });
                }
                else
                {
                    shots.Add(new SnapShot
                    {
                        Type = ESnapShotType.MoveShot,
                        TimeStamp = s.Time,
                        Move = new MoveSnapShot
                        {
                            PlayerId = s.Id,
                            Pos = s.Info.Pos?.Clone(),
                            LookLeft = s.Info.LookLeft,
                            Velocity = s.Velocity,
                            // ★ 服务端只有一个采样点、没有"瞬移"语义 ⇒ 一律 IsMove = true。
                            //   客户端 `UpdateMove` 会朝目标点按速度走过去；IsMove = false 会**直接吸附**
                            //   （旧实现里有几处误用，观感是"凭空穿墙"）。
                            IsMove = true,
                        },
                    });
                }
            }

            // ── 自爆 ────────────────────────────────────────────────────
            foreach (var b in HostRecorder.BombList())
            {
                // ① 触发帧：`OnDeadCollarBomb` 只是"开始自爆" —— 客户端 `PlayDying()` 会演
                //    1.0 秒哑期 + 4 轮越来越快的闪烁（共 5.875s），然后 `StopDying`。
                //    **只需触发一次**：它是纯客户端 DOTween（目标不绑 GameObject），
                //    段切换的 despawn/重新 spawn 不会打断它。
                if (b.Time >= window.From && b.Time < window.To)
                {
                    shots.Add(new SnapShot
                    {
                        Type = ESnapShotType.EffectShot,
                        TimeStamp = b.Time,
                        Effect = new EffectSnapShot
                        {
                            Type = EEffectType.DyingVfx,
                            DeviceId = b.DeviceId,
                            Pos = b.Pos?.Clone(),
                        },
                    });
                }

                // ② 消失帧：真正的死亡在 `b.Time + 6s`（`CreateBombCorpse` + 广播 `S_DESPAWN`）。
                //    回放里复现"人没了"只有这条安全路 —— 尸体 `AddShot` 会触发 `Corpse.SetInfo`
                //    二次调用崩溃（规格 §1·C8），绝不能用。
                //    ⚠ 它与触发帧**通常不在同一段**，所以必须独立判断窗口。
                //    ⚠ 也不能放在闪烁期间（会同时掐掉闪烁与音效）——6 秒正好是闪烁结束点。
                float goneAt = b.Time + CollarToDeadSec;
                if (goneAt >= window.From && goneAt < window.To)
                {
                    shots.Add(new SnapShot
                    {
                        Type = ESnapShotType.DespawnShot,
                        TimeStamp = goneAt,
                        // ⚠ 必须填他的 id：客户端会 `SetActive(false)` 掉这个玩家。
                        //   忘了填（= 0）会去移走"本机替身"，画面直接坏掉。
                        Despawn = b.DeviceId,
                    });
                }

                // ③ 全屏压暗（可选）：原版的压暗是「开 → DoActionAfter(2f) → 关」，服务端**没有**
                //    "立刻取消"的接口 ⇒ 会持续满 2 秒，叠到下一幕的断电视野上就完全看不清 ⇒ 默认关。
                if (bombBlackout && goneAt + 0.1f >= window.From && goneAt + 0.1f < window.To)
                {
                    shots.Add(new SnapShot
                    {
                        Type = ESnapShotType.EffectShot,
                        TimeStamp = goneAt + 0.1f,
                        Effect = new EffectSnapShot
                        {
                            Type = EEffectType.BlackOutVfx,
                            DeviceId = b.DeviceId,
                            Pos = b.Pos?.Clone(),
                        },
                    });
                }
            }

            // 稳定排序：同时间戳保持插入顺序（客户端 `BeginTape` 硬要求首帧是 SpawnShot，
            // 而首帧由 `TapeAssembler` 负责；这里只需要保证自己内部不逆序）。

            // ⚠ 用 `OrderBy` 而不是 `List<T>.Sort` —— 后者不稳定，而原版磁带里**同一时间戳的帧顺序是有意义的**
            //   （实测：刀架在同一时刻先"还在"后"被取走"，排反了就看到"刀没被拿走"）。
            shots = global::System.Linq.Enumerable.ToList(
                global::System.Linq.Enumerable.OrderBy(shots, delegate (SnapShot s) { return s.TimeStamp; }));
            return shots;
        }

        // ── 小工具 ──────────────────────────────────────────────────────

        /// <summary>
        /// 这个人在这一帧该不该被看见。
        ///
        /// 客户端的规则是：`IsGhost = true` 的角色，回放期间 `RefreshGhostVisual()` 走 case 2
        /// 把幽灵替身与骨架**一起关掉** ⇒ 画面上什么都没有。
        /// 所以：
        ///   · **主角**必须本色出场（他是这一段要拍的人）；
        ///   · **正在自爆、还没消失的人**也必须可见 —— 否则"在闪却看不见"
        ///     （服务端在自爆那一刻就把他们标成 `IsGhost` 了）；
        ///   · 其余人照抄采样：服务端认为他不可见，回放里就该不可见。
        /// </summary>
        private static void ApplyVisibility(PublicPlayerInfo info, HostRecorder.Sample s, int subjectId)
        {
            bool isSubject = s.Id == subjectId;
            bool exploding = false;
            foreach (var b in HostRecorder.BombList())
            {
                if (b.DeviceId == s.Id && s.Time <= b.Time + CollarToDeadSec)
                {
                    exploding = true;
                    break;
                }
            }

            if (isSubject || exploding)
            {
                info.IsGhost = false;
                info.State = EPlayerState.Idle;
            }
        }

        private static bool LightFor(int id, float t, bool dark)
            => dark ? false : HostRecorder.LightAt(id, t);

        private static int RoomOrFallback(int subjectId, float t, List<HostRecorder.Sample> samples)
        {
            int room = HostRecorder.RoomAt(subjectId, t);
            if (room > 0)
                return room;

            foreach (var s in samples)
            {
                if (s.RoomId > 0)
                    return s.RoomId;
            }
            return 0;
        }

        private static void AddArea(List<SnapShot> shots, float t, int cameraTargetId, bool lit, int roomId)
        {
            if (roomId <= 0)
                return;   // 没有合法房间就不发：宁可让客户端保持默认相机，也不要抛 KeyNotFound

            shots.Add(new SnapShot
            {
                Type = ESnapShotType.AreaShot,
                TimeStamp = t,
                Area = new AreaSnapShot
                {
                    AreaInfo = new AreaInfo { RoomId = roomId },
                    CameraTargetId = cameraTargetId,
                    IsLight = lit,
                },
            });
        }

    }
}
