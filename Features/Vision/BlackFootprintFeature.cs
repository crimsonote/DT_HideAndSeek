using System.Collections.Generic;
using BepInEx.Configuration;
using HarmonyLib;
using HideAndSeek.Core;
using Protocol;
using Server.Game;
using GamePlayer = Server.Game.Player;

namespace HideAndSeek.Features.Vision
{
    /// <summary>
    /// 黑方脚印视野：把「除自己以外所有人」最近 N 秒（默认 1 秒）的脚印位置，
    /// **单发给每一个黑方**，用原版自己的脚印通道画在他自己的屏幕上。
    ///
    /// ══ 原版的"脚印"到底是什么（逐行核对过） ═══════════════════════════════
    /// ① 触发：黑方每刀杀一人，服务端给**凶手自己**挂 3 秒 `EBuffType.Footprint`
    ///    （`Player.OnDamaged` :175962，另一条刀路 :176525），且是
    ///    `AddBuff(..., isBroadcast: false)` ⇒ 这条 buff **只有凶手自己的客户端**知道。
    /// ② 采集：buff 生效期间 `BuffComponent` 的回调 :161503 → `Player.FillFootstep`（:175935）
    ///    → `GameRoom.FillFootstep`（:171692），**每 300 毫秒**往
    ///    `player.FootstepCorpse.Footsteps` 里追加一个 `FootstepInfo{ Pos, Angle }`。
    ///    （夹角随机抖 ±5°，`State` 为 Hide/Sit 时写成 `float.MaxValue`，客户端据此**不画**。）
    /// ③ 显示（自己）：客户端 `MyPlayer.OnAnimEventHandler`（:13437）在
    ///    `PublicInfo.BuffList` 含 `EBuffType.Footprint` 且处于 Idle/Run 时，
    ///    在**自己脚下**实例化一个 `Footstep` ⇒ **黑方本来就看得见自己的血脚印**。
    /// ④ 显示（补画）：`Corpse.Scan`（:168894）在"这具尸体被报过点（`_isReport`）且记过脚印"时，
    ///    把整串脚印打包成 `S_REPLAY_FOOTSTEP`（包号 1104）**只发给扫描者**；
    ///    客户端 `PacketHandler.Handle_S_REPLAY_FOOTSTEP`（:42585）→
    ///    `MapManager.ReplayFootstep`（:29751）用 `footstep_blood.sprite` 在**世界坐标**
    ///    （父节点 `@Maps`，:29768）逐个补画，**每 0.3 秒画一个**（:29776），
    ///    每个点还在它自己的位置响一声 `DropItemSfx`（:29774，位置音，最远 896 单位）。
    ///
    /// ⇒ 所以：游戏**有**脚印系统，但它是「黑方杀人后的血脚印」，
    ///   而**没有任何通道能把别人的脚印实时画到某人的屏幕上** —— 唯一的世界脚印入口
    ///   就是 ④ 那条"批量补画"通道，本功能正是复用它（服务端自己攒点、自己派发）。
    ///
    /// ══ 为什么这样**不会**把玩家本人暴露给黑方（关键） ═════════════════════
    /// `S_REPLAY_FOOTSTEP` 只携带 `FootstepInfo{ Pos, Angle }` —— **纯坐标与朝向**。
    /// 客户端 `MapManager.ReplayFootstep` 全程只做
    /// `Instantiate("Footstep", MapRoot)` + 设 position/rotation/sprite：
    /// **不查 `Managers.Player.GetPlayerCache`、不碰 `SharedPlayers`、
    ///  不产生 `S_SPAWN`/`S_DESPAWN`、不建小地图 pin、不发 `S_NOTIFY_BLACK`。**
    /// 于是黑方客户端上**不会多出任何 Player 对象**，AOI（<see cref="AoiCullingFeature"/>）
    /// 一行都不用动，索敌 / 攻击判定 / 名字牌 / 小地图 pin 也照旧看不见。
    ///
    /// 作对比（为什么**不**用痕迹/箭头那条通道）：`Player.SendTraceTarget`（:175923）发的是
    /// `S_PIN_MOVE`，收端 `Handle_S_PIN_MOVE`（:42242）→ `RefreshComplyRulesPin`，
    /// 而世界箭头那一步 `SetComplyRulesArrow`（:13799）**要求
    /// `Managers.Player.GetPlayerCache(target.ID)` 能查到玩家对象**，查不到直接 return。
    /// 也就是说：要让痕迹通道出世界箭头，就必须先把玩家本人 `AddPlayer` 给黑方
    /// —— 那等于**解封 AOI、暴露玩家本人**，与需求直接冲突。故不采用。
    ///
    /// ⚠ 已知副作用（都是原版回放通道自带的观感，不是本功能引入的 bug）：
    ///   · `ReplayFootstep` 第一句是 `ClearFootstep()`（:29753）——**整批擦掉重画**，
    ///     所以"只保留最近 N 秒"是靠"每秒重发 + 整批擦除"实现的；
    ///   · 一批 N 个点要 `0.3 × N` 秒才画完 ⇒ 人多时会有"分批叠画"，
    ///     用 <see cref="MaxPointsPerPlayer"/> 压点数（默认 1：每人每批只画最新那一个）；
    ///   · 每个点会在它自己的位置响一声 `DropItemSfx`（位置音，896 单位外听不见）。
    /// </summary>
    [PatchFeature(
        section: "BlackFootprint",
        description: "黑方脚印视野：把「除自己以外所有人」最近 N 秒（默认 1 秒）的脚印位置，"
            + "单发给每个黑方，复用原版 S_REPLAY_FOOTSTEP 通道画在他自己的屏幕上。"
            + "只发坐标、不发玩家对象 ⇒ 不解封 AOI、不暴露玩家本人。",
        defaultEnabled: true,
        side: FeatureSide.Host)]
    internal static class BlackFootprintFeature
    {
        /// <summary>
        /// 只显示最近这么多秒内的脚印。需求给的时长就是 **1 秒**；
        /// 房主说过"计划可升级"，所以做成可调项。**0 = 不发**（等于段内热关，已画的黑方会被擦掉）。
        /// </summary>
        [ConfigField(1f, "只显示最近这么多秒内的脚印（需求 = 1 秒）。0 = 不显示（等于热关本功能）。",
            Min = 0f, Max = 10f)]
        public static ConfigEntry<float> WindowSeconds;

        /// <summary>
        /// 同一名玩家两次采样之间的最小间隔（秒）。
        /// 原版自己留脚印的节奏就是 300ms（`GameRoom.FillFootstep` :171715 的 `PushAfter(300, …)`），
        /// 这里照抄同一个节奏，让"我们记的点"与"原版记的点"落在同一时间尺度上。
        /// </summary>
        [ConfigField(0.3f, "同一名玩家两次脚印采样之间的最小间隔（秒）。原版自己留脚印的节奏就是 300ms。",
            Min = 0.05f, Max = 2f)]
        public static ConfigEntry<float> SampleIntervalSeconds;

        /// <summary>
        /// 每名玩家每批最多几个脚印点。
        ///
        /// ⚠ **必须限制**：客户端补画是**每 0.3 秒画一个**（`CoReplayFootstep` :29776），
        /// 一批 N 个点就要 `0.3 × N` 秒才画得完，而我们是每秒派发一批
        /// （下一批的 `ClearFootstep` 会把没画完的整批擦掉）。点数越多，
        /// 每个脚印"冒出来"的时刻就越滞后于它真实的时刻。默认 1 = 每人每批只画最新那一个。
        /// </summary>
        [ConfigField(1, "每名玩家每批最多几个脚印点。⚠ 客户端是每 0.3 秒画一个，点多了整批画不完会显得滞后。",
            Min = 1, Max = 10)]
        public static ConfigEntry<int> MaxPointsPerPlayer;

        /// <summary>一个脚印采样点（我们自己攒的，不是原版 `FootstepCorpse.Footsteps`）。</summary>
        private struct Step
        {
            /// <summary>采样时刻，取 <c>UnityEngine.Time.realtimeSinceStartup</c>（秒，float —— SurviveTime 只有整秒，不够用）。</summary>
            public float At;

            /// <summary>位置快照。**必须是 Clone**，理由见 <see cref="Sample"/>。</summary>
            public PosInfo Pos;

            /// <summary>朝向，已归一化进 [0,360]（客户端只画这个区间内的角度，:29766）。</summary>
            public float Angle;
        }

        /// <summary>PlayerId → 该玩家最近的脚印点（**按时间升序，尾部最新**）。</summary>
        private static readonly Dictionary<int, List<Step>> Recent = new Dictionary<int, List<Step>>();

        /// <summary>PlayerId → 上次采样时刻，只用于按 <see cref="SampleIntervalSeconds"/> 节流。</summary>
        private static readonly Dictionary<int, float> LastSampleAt = new Dictionary<int, float>();

        /// <summary>屏幕上此刻还有我们发的脚印的**接收者** PlayerId。用于"没人动了就发空包擦掉"。</summary>
        private static readonly HashSet<int> Emitting = new HashSet<int>();

        /// <summary>本批点的临时容器（复用，避免每秒都分配）。</summary>
        private static readonly List<Step> Batch = new List<Step>(16);

        /// <summary>剪枝时收集"已经被摘空"的 PlayerId（不能边遍历边删字典）。</summary>
        private static readonly List<int> Expired = new List<int>();

        /// <summary>
        /// "最新的先画" —— 客户端是 0.3 秒画一个，把最新的排在最前，
        /// **最新那一枚脚印立刻出现在正确位置**（信息新鲜度优先），更早的点随后补上。
        /// 用静态字段缓存，避免每秒新建一个委托（本仓库对每秒节奏的分配比较敏感）。
        /// </summary>
        private static readonly global::System.Comparison<Step> NewestFirst
            = (a, b) => b.At.CompareTo(a.At);

        private static float Now => UnityEngine.Time.realtimeSinceStartup;

        /// <summary>
        /// 黑方阵营判据 —— 与 <c>ProximityAlertFeature</c> / <c>BlackWinFeature</c> 一致
        /// （`AoiCullingFeature` 只裁 `Black`，但 `Dark` 同属黑方阵营，这里一并算上）。
        /// </summary>
        private static bool IsBlackSide(GamePlayer player)
            => player != null && (player.Color == EPlayerColor.Black || player.Color == EPlayerColor.Dark);

        /// <summary>
        /// 这个人现在该不该留下脚印。
        ///
        /// 过滤条件照抄 <c>BearMapMarkFeature.IsEligible</c>（同一套"够不够格被看见"的判据）：
        /// `State == Hide` 有**两个**来源，两个都要跳过 ——
        /// 活人躲进柜子（`Cabinet.HideCabinet` :162129）与死亡/幽灵（`MakeSpectatorGhost` :175590）；
        /// `IsSpectator` 要**单独判**（服务端 `Player` 上没有 `IsGhost` 这个属性，幽灵就是 `IsSpectator`）。
        ///
        /// 刻意**不跳过假人**：假人同样是场上的目标，测试环境里更是只有它们可验
        /// （与 <c>BearMapMarkFeature</c> 的取舍一致）。
        /// </summary>
        private static bool LeavesFootprint(GamePlayer player)
        {
            if (player?.PublicInfo == null) return false;
            if (!player.IsAlive) return false;
            if (player.IsSpectator) return false;
            if (player.PublicInfo.IsGhost) return false;
            if (player.State == EPlayerState.Hide || player.State == EPlayerState.Sit) return false;
            return true;
        }

        /// <summary>接收者是否该收到脚印：活着的黑方（幽灵/旁观者/躲柜子时不画），且拿得到会话。</summary>
        private static bool CanReceive(GamePlayer black)
            => LeavesFootprint(black) && black.Session != null;

        /// <summary>
        /// 把方向角折进 [0,360]。客户端 `CoReplayFootstep`（:29766）只在
        /// `0 ≤ Angle ≤ 360` 时才画那一个点，越界点会被**静默丢掉**；
        /// NaN/Infinity 一律当 0（画出来总比整批少一个强）。
        /// 原版 `GameRoom.FillFootstep`（:171698）做的是同一件事。
        /// </summary>
        private static float NormalizeAngle(float angle)
        {
            if (float.IsNaN(angle) || float.IsInfinity(angle)) return 0f;
            while (angle < 0f) angle += 360f;
            while (angle > 360f) angle -= 360f;
            return angle;
        }

        /// <summary>
        /// 采样：玩家每移动一次就（按 <see cref="SampleIntervalSeconds"/> 节流）记一个点。
        /// 站着不动的人不再产生新点，旧点会被 <see cref="Prune"/> 按窗口丢掉
        /// ⇒ 自然就是"停步即无脚印"，不需要额外的"是否在移动"判据。
        /// </summary>
        private static void Sample(GamePlayer player)
        {
            if (!LeavesFootprint(player)) return;

            int pid = player.PublicInfo.PlayerId;
            float now = Now;
            float interval = SampleIntervalSeconds != null ? SampleIntervalSeconds.Value : 0.3f;
            if (LastSampleAt.TryGetValue(pid, out float last) && now - last < interval)
                return;
            LastSampleAt[pid] = now;

            if (!Recent.TryGetValue(pid, out List<Step> list))
            {
                list = new List<Step>(8);
                Recent[pid] = list;
            }

            // ★ 必须 Clone。`PublicInfo.Pos` 是**原地改写**的同一个对象
            //   （`Player.Move` :175885 `PublicInfo.Pos.X = pos.X`），
            //   存引用会让所有历史点跟着一起动 —— 最后每个脚印都会画在"现在的位置"。
            //   原版自己的 `GameRoom.FillFootstep`(:171712) 同样是 `.Clone()`。
            list.Add(new Step
            {
                At = now,
                Pos = player.PublicInfo.Pos.Clone(),
                Angle = NormalizeAngle(player.DirectionAngle)
            });
        }

        /// <summary>
        /// 丢掉窗口之外的点；某个人的点全过期就把他从表里摘掉。
        /// 窗口是**热改**的（每秒都重新读配置），所以每个 tick 都按当前窗口重剪一遍。
        /// </summary>
        private static void Prune(float now, float window)
        {
            if (Recent.Count == 0) return;

            Expired.Clear();
            foreach (KeyValuePair<int, List<Step>> kv in Recent)
            {
                List<Step> list = kv.Value;
                for (int i = list.Count - 1; i >= 0; i--)
                {
                    if (now - list[i].At > window)
                        list.RemoveAt(i);
                }
                if (list.Count == 0)
                    Expired.Add(kv.Key);
            }

            for (int i = 0; i < Expired.Count; i++)
                Recent.Remove(Expired[i]);
        }

        /// <summary>
        /// 每秒一次：给每个黑方组装并**单发**一批
        /// "除他自己以外所有人最近 N 秒的脚印"。
        /// </summary>
        private static void Dispatch(GameRoom room)
        {
            if (room?.Players == null) return;

            float window = WindowSeconds != null ? WindowSeconds.Value : 1f;
            float now = Now;

            // 窗口 ≤ 0 = 热关：把已经画在黑方屏幕上的脚印擦掉，并清账本。
            if (window <= 0f)
            {
                if (Emitting.Count > 0)
                {
                    EraseAll(room);
                    Plugin.Log.LogInfo("[HS] BlackFootprint：窗口被改成 0，已清空所有黑方屏幕上的脚印。");
                }
                Recent.Clear();
                LastSampleAt.Clear();
                return;
            }

            Prune(now, window);

            int maxPerPlayer = MaxPointsPerPlayer != null ? MaxPointsPerPlayer.Value : 1;
            if (maxPerPlayer < 1) maxPerPlayer = 1;

            var all = room.Players;
            for (int i = 0; i < all.Count; i++)
            {
                GamePlayer black = all[i];
                if (!IsBlackSide(black) || black.PublicInfo == null)
                    continue;

                int blackPid = black.PublicInfo.PlayerId;

                // 收件人自己不画（幽灵/旁观/躲柜子）：把上一批擦掉后不再发。
                if (!CanReceive(black))
                {
                    if (Emitting.Remove(blackPid) && black.Session != null)
                        Send(black, null);
                    continue;
                }

                Batch.Clear();
                for (int j = 0; j < all.Count; j++)
                {
                    GamePlayer other = all[j];
                    if (other == null || other == black || other.PublicInfo == null)
                        continue;
                    if (!LeavesFootprint(other))
                        continue;
                    if (!Recent.TryGetValue(other.PublicInfo.PlayerId, out List<Step> list) || list.Count == 0)
                        continue;

                    // 取**最新**的几个（list 尾部最新）。list 已被 Prune 保证全在窗口内。
                    int take = list.Count < maxPerPlayer ? list.Count : maxPerPlayer;
                    for (int k = list.Count - take; k < list.Count; k++)
                        Batch.Add(list[k]);
                }

                if (Batch.Count == 0)
                {
                    // 最近 N 秒没人留脚印（大家都停了 / 都死了）：擦掉上一批并收尾。
                    if (Emitting.Remove(blackPid))
                    {
                        Send(black, null);
                        Plugin.Log.LogInfo(
                            $"[HS] BlackFootprint：黑方 #{blackPid} 已无 {window:F1} 秒内的脚印，已清空他的脚印显示。");
                    }
                    continue;
                }

                Batch.Sort(NewestFirst);
                Send(black, Batch);

                if (Emitting.Add(blackPid))
                {
                    Plugin.Log.LogInfo(
                        $"[HS] BlackFootprint：开始向黑方 #{blackPid} 下发脚印"
                        + $"（窗口 {window:F1}s，本批 {Batch.Count} 点，上限 {maxPerPlayer} 点/人）。");
                }
            }
        }

        /// <summary>
        /// 向某个黑方单发一批脚印。<paramref name="steps"/> 为 null 时发**空包** ——
        /// 客户端 `ReplayFootstep` 第一句就是 `ClearFootstep()`（:29753），
        /// 空包正好用来把屏幕上的脚印擦干净（它会先清列表，然后协程无点可画）。
        /// </summary>
        private static void Send(GamePlayer black, List<Step> steps)
        {
            if (black?.Session == null) return;

            try
            {
                var pkt = new S_REPLAY_FOOTSTEP();
                if (steps != null)
                {
                    for (int i = 0; i < steps.Count; i++)
                    {
                        pkt.Footsteps.Add(new FootstepInfo
                        {
                            Pos = steps[i].Pos,
                            Angle = steps[i].Angle
                        });
                    }
                }

                black.Session.Send(pkt);
            }
            catch (global::System.Exception ex)
            {
                Plugin.Log.LogWarning(
                    $"[HS] BlackFootprint：向黑方 #{black.PublicInfo?.PlayerId} 下发脚印失败 — {ex.Message}");
            }
        }

        /// <summary>把所有还在显示脚印的黑方擦干净（关功能 / 窗口改 0 时用）。</summary>
        private static void EraseAll(GameRoom room)
        {
            var all = room?.Players;
            if (all != null)
            {
                for (int i = 0; i < all.Count; i++)
                {
                    GamePlayer black = all[i];
                    if (black?.PublicInfo == null || black.Session == null)
                        continue;
                    if (!Emitting.Contains(black.PublicInfo.PlayerId))
                        continue;
                    Send(black, null);
                }
            }
            Emitting.Clear();
        }

        // ── ① 采样：玩家每次移动（本仓库已有两处挂同一个方法，都是 Postfix，互不干扰）──
        [HarmonyPatch(typeof(GamePlayer), nameof(GamePlayer.Move), new[] { typeof(PosInfo), typeof(bool) })]
        internal static class MoveHook
        {
            [HarmonyPostfix]
            private static void Postfix(GamePlayer __instance)
            {
                if (ModeRuntime.Bypass)
                    return;                       // 段内门闩：捉迷藏模式没开就什么都不做
                Diagnostics.Hit("BlackFootprint");
                Sample(__instance);
            }
        }

        // ── ② 派发：每秒一次（本仓库统一用 SurvivalTick 做"每秒"节奏）──
        [HarmonyPatch(typeof(GameRoom), "SurvivalTick")]
        internal static class TickHook
        {
            [HarmonyPostfix]
            private static void Postfix(GameRoom __instance)
            {
                if (ModeRuntime.Bypass)
                    return;
                if (__instance == null || __instance.State != EGameState.Survive)
                    return;                       // 只在生存阶段发；大厅/教学关的地图根还没搭好
                Diagnostics.Hit("BlackFootprint");
                Dispatch(__instance);
            }
        }

        // ── ③ 阶段收尾：把 Survive 期间留在黑方屏幕上的脚印擦干净 ──────────
        //
        // 为什么需要它：最后一批脚印是"点了才会一个个画出来"的，Survive 一结束我们就不再发包，
        // 而客户端只会在**探案阶段结束**时才 `MapManager.ClearFootstep()`（`EndDetective` :29243）
        // ⇒ 整个探案阶段都会留着 Survive 最后一秒的那几枚血脚印。
        //
        // 为什么这一刻发空包是安全的：`StartDetective` 并**不**隐藏世界根
        //（只有 `StartTrial` 才 `SetActiveAllWorldObject(false)` :29210），
        // 所以空包能真的落到 `@Maps` 上生效。
        [HarmonyPatch(typeof(GameRoom), "StartDetective")]
        internal static class DetectiveHook
        {
            [HarmonyPostfix]
            private static void Postfix(GameRoom __instance)
            {
                if (ModeRuntime.Bypass)
                    return;
                if (Emitting.Count > 0)
                    EraseAll(__instance);
                Cleanup();
            }
        }

        // ── ④ 跨局清理 ──────────────────────────────────────────────────
        [HarmonyPatch(typeof(GameRoom), "StartSurvive")]
        internal static class StartHook
        {
            [HarmonyPostfix]
            private static void Postfix() => Cleanup();
        }

        [HarmonyPatch(typeof(GameRoom), "StartLobby")]
        internal static class LobbyHook
        {
            [HarmonyPostfix]
            private static void Postfix() => Cleanup();
        }

        /// <summary>
        /// 清账本。**本方法自己刻意不发包**（要擦黑方屏幕上的脚印，
        /// 请像上面的 DetectiveHook 那样在调用它**之前**发空包）：`ReplayFootstep` 用的 `MapRoot` 是
        /// `GameObject.Find("@Maps")`，找不到会**凭空建一个空对象**
        /// （`MapManager.GetRootTransform` :29512）—— 回大厅时地图正在拆，
        /// 这时候发包既没用，还会在那个空对象下留垃圾。
        /// 客户端自己在换场景 / 重载地图时会 `MapManager.ClearFootstep()`
        /// （`EndDetective` :29243、`LoadAllArea` → `ClearSoft` :29627），
        /// 所以这里不需要我们操心屏幕残留。
        ///
        /// 三个表记的都有 `Time.realtimeSinceStartup` 与 PlayerId，不清会跨局串味。
        /// </summary>
        private static void Cleanup()
        {
            Recent.Clear();
            LastSampleAt.Clear();
            Emitting.Clear();
        }
    }
}
