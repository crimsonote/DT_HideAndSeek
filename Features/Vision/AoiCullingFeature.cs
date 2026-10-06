using System;
using System.Collections.Generic;
using BepInEx.Configuration;
using HarmonyLib;
using Protocol;
using Server.Game;
using HideAndSeek.Core;
using UnityEngine;
using HideAndSeek.Features.Combat;
using GamePlayer = Server.Game.Player;
using GameSkill = Server.Game.SkillComponent;

namespace HideAndSeek.Features.Vision
{
    /// <summary>
    /// 黑方视野裁剪（AOI）：黑方只能"看见"附近一定范围内的玩家。
    ///
    /// 原理是复用原版自己的兴趣区域原语：X.SharedPlayers 的语义是"能看到 X 的人"，
    /// 服务端 AddPlayer（:175822）向观察者发 S_SPAWN、RemovePlayer（:175841）发 S_DESPAWN，
    /// 位置广播 BroadcastToPlayerAndObservers（:176969）只发给 SharedPlayers。
    /// 因此把远处的玩家从黑方的观察列表移除，黑方客户端上模型、小地图 pin、
    /// 以及 MyPlayer.GetTargetPlayer 的索敌会同时消失。
    ///
    /// ★ 必须双向维护（这是最容易漏的一点）：
    ///   原版让"某人重新可见"的唯一途径是**被观察者自己移动**
    ///   （Player.Move :175883 → AreaManager.SearchAndUpdatePlayer :173427 → AddPlayer）。
    ///   站着不动的目标一旦被剔除，就再没有任何路径被加回来 —— 客户端上那个
    ///   Player 对象已 despawn，走到跟前也看不见。所以这里必须自己补 AddPlayer：
    ///     · 黑方移动时立即校正（否则只靠每秒一次的 tick，靠近后会"慢一拍"）
    ///     · SurvivalTick 每秒兜底
    ///   进入用 EnterRange、退出用 ExitRange，构成滞回；AddPlayer 幂等，重复调用无害。
    ///
    /// ★ 该被剔除的不止"太远的人"：死者 / 幽灵 / 躲进柜子的活人 / 旁观者一律不该进黑方视野，
    ///   判据统一在 <see cref="InvisibleReason"/>（本类唯一的"谁能被看见"入口）。
    ///
    /// ★ 技能感知豁免（SkillAware）—— 两类技能需要"看得见目标"才能在客户端选中：
    ///   - TimeStop（Seol）：SkillData.IsTarget=true、Range=3.0×224=672。客户端
    ///     GetSkillTarget（:31443）只遍历本地 Players，672 内无人则 CanUseSkillCondition=false，
    ///     按键毫无反应。故阈值放宽到 672。
    ///   - Marionette（Rin 的小熊）：探测是**纯客户端本地判定**
    ///     （Summon.DetectNearbyPlayer :7290，以召唤物坐标为圆心遍历本地 Players），
    ///     而召唤物固定在自己脚下、不随人移动。故圆心改为召唤物坐标，
    ///     并按它自己的探测半径 448 取阈值（不用 900，那会让可见范围翻倍）。
    /// </summary>
    [HarmonyPatch]
    [PatchFeature(
        section: "AoiCulling",
        description: "黑方视野裁剪：黑方只能看到附近的玩家（模型与小地图 pin 同时消失）。对时停、小熊等依赖真实目标信息的技能自动豁免。",
        defaultEnabled: true,
        side: FeatureSide.Host)]
    internal static class AoiCullingFeature
    {
        [ConfigField(700f, "进入可见范围的距离。建议不小于客户端攻击距离 224；调大能让靠近时更早被识别。",
            Min = 200f, Max = 5000f)]
        public static ConfigEntry<float> EnterRange;

        [ConfigField(900f, "离开可见范围的距离。须明显大于进入距离（滞回），否则边界会抖动。",
            Min = 200f, Max = 5000f)]
        public static ConfigEntry<float> ExitRange;

        [ConfigField(true,
            "隔墙裁剪：视线被墙挡住时，即使距离够近也不可见（原版视野本来就带遮挡，这里是恢复它）。" +
            "关掉即恢复成「只看距离」。")]
        public static ConfigEntry<bool> BlockByWalls;

        [ConfigField(true,
            "视野升级到 1 级后解除隔墙限制（穿墙视野，作为成长奖励）。")]
        public static ConfigEntry<bool> WallsUnlockByVision;

        [ConfigField(224f,
            "贴脸豁免半径：距离小于它时不判遮挡（224 = 一格）。" +
            "没有它会出现「两人贴墙分站两侧、走到脸上却完全看不见」，见 .tmps/AOI-墙壁遮挡-调查.md §8。",
            Min = 0f, Max = 1000f)]
        public static ConfigEntry<float> WallGraceRange;

        /// <summary>遮挡层（4096 = "Block"，与原版视野判定同一个 mask）。</summary>
        private const int BlockLayerMask = 4096;

        [ConfigField(3f, "最短可见秒数：进入视野后至少保持这么久不被剔除。", Min = 0f, Max = 60f)]
        public static ConfigEntry<float> MinVisibleSeconds;

        [ConfigField(true, "技能感知：持时停（放宽到 672）或放出小熊（圆心改为召唤物）时自动豁免裁剪，避免技能静默失效。")]
        public static ConfigEntry<bool> SkillAware;

        [ConfigField(true,
            "小熊豁免：小熊的『视野切换』与『附近探测』都由客户端遍历本地玩家完成，" +
            "不豁免则两者在 AOI 下一起失效。代价是黑方小地图会为召唤物附近（半径见下）的人生成 pin —— " +
            "原版该技能只给 HUD 提示、不给位置。")]
        public static ConfigEntry<bool> MarionetteExempt;

        [ConfigField(448f, "小熊豁免的进入半径（以召唤物为圆心），对应其客户端探测圈。",
            Min = 100f, Max = 2000f)]
        public static ConfigEntry<float> MarionetteEnterRange;

        [ConfigField(648f, "小熊豁免的退出半径（滞回 200，采样粒度约 72.8）。",
            Min = 100f, Max = 2000f)]
        public static ConfigEntry<float> MarionetteExitRange;

        /// <summary>TimeStop 的客户端索敌半径 = SkillData.Range(3.0) × 224（:31451 的换算）。</summary>
        private const float TimeStopSearchRange = 672f;

        /// <summary>(黑方 PlayerId, 对方 PlayerId) → 首次可见的 SurviveTime。</summary>
        private static readonly Dictionary<long, int> VisibleSince = new Dictionary<long, int>();

        /// <summary>
        /// (黑方 PlayerId, 被藏者 PlayerId) → 本局是否已经为这一对打过"藏起来"的日志。
        ///
        /// 用途：实机确认"谁被藏了、因为哪条判据" —— 这是本修复**唯一**能在日志里看见的正向证据
        /// （修复生效时本类什么都不做，所以没有别的输出）。不用它就会每秒每对刷一行。
        /// 每局在 <c>StartSurvive</c> 清空。
        /// </summary>
        private static readonly HashSet<long> HiddenLogged = new HashSet<long>();

        // SkillComponent._summonId 是私有字段，反射读取（失败则整体降级为不使用召唤物圆心）
        private static AccessTools.FieldRef<GameSkill, int> _summonIdRef;
        private static bool _summonIdFailed;

        private static bool SkillAwareOn => SkillAware == null || SkillAware.Value;

        private static long PairKey(int blackId, int otherId)
            => ((long)blackId << 32) | (uint)otherId;

        private static bool IsBlack(GamePlayer player)
            => player != null && player.Color == EPlayerColor.Black;

        /// <summary>
        /// 「这个人此刻不该出现在任何活人（含黑方）的视野里」
        /// —— **本类唯一的"幽灵 / 躲藏 / 旁观"判据，别在别处再写一遍。**
        ///
        /// 返回原因（写日志用）；该被看见时返回 <c>null</c>。
        ///
        /// ★ **为什么不能只看 `State == Hide`** —— 这正是"黑方砍完正在操作对讲机的人，幽灵依旧可见"的直接原因：
        ///   死亡时**若该客户端正处于 `EPlayerState.Interact`（操作发信机 / 对讲机）**，
        ///   服务端的 `PublicInfo.State` 会被改回 `Idle`，链条（行号 = .tmps/decomp/Assembly-CSharp.decompiled.cs）：
        ///   ① 服务端 <c>Player.OnDead</c>（:175968）：<c>IsAlive = false</c>（:175974）→
        ///      <c>Session.Send(new S_DEAD())</c>（:175978）→ 末尾 <c>ExitPlayer()</c>（:176004）置
        ///      <c>State = Hide</c> + <c>PublicInfo.IsGhost = true</c>（:176098-176099）。
        ///   ② 客户端收 <c>S_DEAD</c> → <c>GameManagerEX.Dead()</c>（:28956）→ <c>CancelAllInteract()</c>（:28969）→
        ///      <c>CloseInteractUI()</c>（:28982-28988）：`MyPlayer.State == Interact` 时发
        ///      <c>C_MODIFY_PLAYER{ChangePlayerState = Idle}</c>（:13248-13257）。
        ///   ③ 服务端 <c>Player.ModifyPlayer</c> 的守卫是 <c>IsAlive &amp;&amp; (State == Hide || State == Sit)</c>（:176785）
        ///      —— **死人整条守卫被跳过** ⇒ <c>PublicInfo.State = Idle</c> 被无条件写入（:176801）并广播（:176803）。
        ///   ⇒ 这个死人既不是 <c>Hide</c>、<c>IsSpectator</c> 也是 false，旧判据（<c>Hide || IsSpectator</c>）全盘放行。
        ///
        /// ★ 但**原版在死亡那一刻其实已经清理干净了**：
        ///   <c>ExitPlayer()</c>（:176004）末尾会调 <c>GameRoom.EnterGhostVisibility(this)</c>（:176116），
        ///   它给所有人发 <c>S_DESPAWN</c>（:169860-169863）并把双方从 <c>SharedPlayers</c> 互删（:169866-169873）。
        ///   ⇒ **幽灵是被本类自己重新加回去的**（<c>RevealNearby</c> 与每秒 tick 那两处主动 <c>AddPlayer</c>）。
        ///   修判据 = 不再加回去，而不是"再补一次清理"。
        ///
        /// ★ 判据与另外两处"够不够格被看见"保持一致，别再各写各的：
        ///   <c>BlackFootprintFeature.LeavesFootprint</c>（Features/Vision/BlackFootprintFeature.cs:154-162）、
        ///   <c>BearMapMarkFeature.IsEligible</c>（Features/Skill/BearMapMarkFeature.cs:101-111）。
        ///
        /// ⚠ <c>PublicInfo.IsGhost</c> **不是** <c>Player.IsGhost</c>：服务端 <c>Player</c> 上**没有** <c>IsGhost</c>
        ///   属性（只有 <c>IsAlive</c> / <c>IsDummy</c> / <c>IsSpectator</c>，:175304-175308），幽灵标志在
        ///   <c>PublicInfo</c>（<c>Protocol.PublicPlayerInfo</c>，字段表 :148473；<c>Player.PublicInfo</c> 声明在 :175284）上。
        ///
        /// ⚠ 刻意**不跳过假人**（<c>IsDummy</c>）：假人是场上的靶子，活着就该被看见
        ///   （与 BlackFootprint / BearMapMark 的取舍一致）。已死的假人当然落进 <c>!IsAlive</c> 这一支，
        ///   而原版 <c>AttackPlayer</c>（:171790-171793）本来就不让砍死人 ⇒ 无影响。
        /// </summary>
        internal static string InvisibleReason(GamePlayer p)
        {
            if (p?.PublicInfo == null)
                return null;

            if (!p.IsAlive)
                return "已死（Player.IsAlive = false）";
            if (p.PublicInfo.IsGhost)
                return "幽灵标志（PublicInfo.IsGhost = true）";
            if (p.State == EPlayerState.Hide)
                return "躲藏 / 幽灵（State = Hide）";
            if (p.IsSpectator)
                return "旁观者（Player.IsSpectator = true）";
            return null;
        }

        private static ESkillType? SkillTypeOf(GamePlayer player)
            => player?.SkillComponent?.Data?.Type;

        /// <summary>坐标平方距离，避免开方。</summary>
        private static float DistanceSq(GamePlayer a, float cx, float cy)
        {
            var pa = a?.PublicInfo?.Pos;
            if (pa == null)
                return float.MaxValue;

            float dx = pa.X - cx;
            float dy = pa.Y - cy;
            return dx * dx + dy * dy;
        }

        private static int GetSummonId(GameSkill component)
        {
            if (component == null || _summonIdFailed)
                return 0;

            try
            {
                if (_summonIdRef == null)
                    _summonIdRef = AccessTools.FieldRefAccess<GameSkill, int>("_summonId");

                return _summonIdRef(component);
            }
            catch (Exception ex)
            {
                _summonIdFailed = true;
                Plugin.Log.LogWarning($"[HS] AoiCulling：无法读取 SkillComponent._summonId，小熊豁免将不生效（{ex.Message}）。");
                return 0;
            }
        }

        /// <summary>黑方若已放出小熊，返回召唤物坐标作为裁剪圆心。</summary>
        private static bool TryMiniCenter(GamePlayer black, out float cx, out float cy)
        {
            cx = 0f;
            cy = 0f;

            if (!SkillAwareOn)
                return false;
            if (MarionetteExempt != null && !MarionetteExempt.Value)
                return false;
            if (SkillTypeOf(black) != ESkillType.Marionette)
                return false;

            int summonId = GetSummonId(black.SkillComponent);
            if (summonId == 0)
                return false;   // 召唤物还没放出来

            // Assembly-CSharp 同时存在全局 DeviceManager 与 Server.Game.DeviceManager，须显式限定
            var summon = Server.Game.DeviceManager.Instance?.GetSummon(summonId);
            var spos = summon?.DeviceInfo?.Pos;
            if (spos == null)
                return false;

            cx = spos.X;
            cy = spos.Y;
            return true;
        }

        private static void OwnCenter(GamePlayer black, out float cx, out float cy)
        {
            var pos = black?.PublicInfo?.Pos;
            cx = pos?.X ?? 0f;
            cy = pos?.Y ?? 0f;
        }

        /// <summary>
        /// 这次"看得见"会不会被墙挡住 —— **只用于"进入"方向**；
        /// 剔除仍只看距离（退出宽松），否则会出现"因遮挡被移除、下一秒又被加回"的抖动。
        ///
        /// 三层短路，任一满足即视为没被挡住：
        ///   ① 隔墙裁剪关掉了 ⇒ 恢复"只看距离"的原行为
        ///   ② 视野已升级到 1 级且允许解锁 ⇒ 穿墙视野
        ///   ③ 贴脸豁免：距离 ≤ WallGraceRange（默认 224 = 一格）
        ///      —— 没有它就会出现"贴墙分站两侧、走到脸上却看不见"（这是几何必然）
        ///
        /// 射线从**圆心**发出：普通情况圆心 = 黑方自己；小熊豁免时圆心 = 召唤物坐标，
        /// 于是判定自然变成"小熊能不能探测到那个人"，与客户端的本地判定一致。
        /// </summary>
        private static bool WallBlocks(GamePlayer black, GamePlayer other, float cx, float cy, float dSq)
        {
            if (BlockByWalls == null || !BlockByWalls.Value)
                return false;

            if (WallsUnlockByVision != null && WallsUnlockByVision.Value
                && KillUpgradeFeature.LevelOf(KillUpgradeFeature.DirVision) >= 1)
                return false;

            float grace = WallGraceRange != null ? WallGraceRange.Value : 224f;
            if (grace > 0f && dSq <= grace * grace)
                return false;

            var target = other?.PublicInfo?.Pos;
            if (target == null) return false;

            Vector2 from = new Vector2(cx, cy);
            Vector2 to = new Vector2(target.X, target.Y);
            Vector2 dir = to - from;
            float mag = dir.magnitude;
            if (mag <= 0.01f) return false;          // 重叠，视为可见

            return Physics2D.Raycast(from, dir, mag, BlockLayerMask).collider != null;
        }

        /// <summary>一组裁剪范围：圆心 + 进入/退出半径。</summary>
        private struct ClipRange
        {
            public float Cx;
            public float Cy;
            public float Enter;
            public float Exit;
        }

        /// <summary>本次要用的范围列表（复用，避免每 tick 分配）。</summary>
        private static readonly List<ClipRange> _ranges = new List<ClipRange>(2);

        /// <summary>
        /// 收集该黑方本次的**全部**裁剪范围 —— **并集**语义：
        ///   ① 玩家本体：以自己为圆心，EnterRange / ExitRange（TimeStop 豁免照旧放大）
        ///   ② 若已放出小熊：**追加**一组「召唤物圆心 + MarionetteEnterRange / ExitRange」
        ///
        /// ⚠ 这里是"替换 → 并集"的修复点。16504fd 把原来的
        ///     <c>range = Math.Max(baseRange, MarionetteDetectRadius)</c>
        ///   改成了 <c>range = MarionetteEnterRange</c>，于是**放出小熊后玩家本体视野被削到 448**。
        ///   而小熊是放在地上不动的召唤物：玩家一走开，判定就只剩"离小熊近不近"，
        ///   连自己身边的人也看不见（哪怕贴脸）。两个圆心必须各判一次。
        /// </summary>
        private static void ResolveRanges(GamePlayer black)
        {
            _ranges.Clear();

            float enter = EnterRange?.Value ?? 750f;
            float exit = ExitRange?.Value ?? 1100f;
            if (SkillAwareOn && SkillTypeOf(black) == ESkillType.TimeStop)
            {
                enter = Math.Max(enter, TimeStopSearchRange);
                exit = Math.Max(exit, TimeStopSearchRange);
            }

            OwnCenter(black, out float ox, out float oy);
            _ranges.Add(new ClipRange { Cx = ox, Cy = oy, Enter = enter, Exit = exit });

            if (TryMiniCenter(black, out float mx, out float my))
            {
                _ranges.Add(new ClipRange
                {
                    Cx = mx,
                    Cy = my,
                    Enter = MarionetteEnterRange?.Value ?? 448f,
                    Exit = MarionetteExitRange?.Value ?? 648f
                });
            }
        }

        /// <summary>是否落在任一"进入"范围内；命中时回传那一组（圆心供遮挡射线用）与它的平方距离。</summary>
        private static bool InAnyEnter(GamePlayer other, out ClipRange hit, out float dSq)
        {
            for (int i = 0; i < _ranges.Count; i++)
            {
                ClipRange r = _ranges[i];
                float d = DistanceSq(other, r.Cx, r.Cy);
                if (d <= r.Enter * r.Enter)
                {
                    hit = r;
                    dSq = d;
                    return true;
                }
            }

            hit = default;
            dSq = 0f;
            return false;
        }

        /// <summary>
        /// 是否落在任一"退出"范围内（滞回用），命中时回传**最近**的那一组。
        /// 只看距离、不做遮挡判定（退出宽松，避免"因遮挡移除 → 下一秒又加回"）。
        /// </summary>
        private static bool InAnyExit(GamePlayer other, out ClipRange hit, out float dSq)
        {
            bool any = false;
            hit = default;
            dSq = float.MaxValue;

            for (int i = 0; i < _ranges.Count; i++)
            {
                ClipRange r = _ranges[i];
                float d = DistanceSq(other, r.Cx, r.Cy);
                if (d <= r.Exit * r.Exit && (!any || d < dSq))
                {
                    any = true;
                    hit = r;
                    dSq = d;
                }
            }

            return any;
        }

        /// <summary>
        /// 为这一对「（黑方, 不该被看见的人）」打一次日志 —— **每局每对只打一次**，否则每秒每对刷一行。
        ///
        /// 这是本次修复**唯一**能在实机日志里看见的正向证据：修复生效时本类的作用是"什么都不做"
        /// （不再把幽灵加回黑方视野），所以不会像"剔除超范围的人"那样天然有日志。
        /// 想看成没生效：黑方砍死一个正在操作对讲机的人之后，搜这行 ——
        /// <c>[HS] AoiCulling：黑方 #N 不显示 #M —— 已死（Player.IsAlive = false）</c>。
        /// </summary>
        private static void LogHiddenOnce(GamePlayer black, GamePlayer other, string why)
        {
            if (black?.PublicInfo == null || other?.PublicInfo == null)
                return;
            if (why == null || !HiddenLogged.Add(PairKey(black.PublicInfo.PlayerId, other.PublicInfo.PlayerId)))
                return;

            Plugin.Log.LogInfo(
                $"[HS] AoiCulling：黑方 #{black.PublicInfo.PlayerId} 不显示 #{other.PublicInfo.PlayerId} —— {why}");
        }

        /// <summary>
        /// 对黑方附近的人主动确保可见。黑方移动时立即调用，
        /// 避免只靠每秒一次的 tick 而产生"靠近后慢一拍"的观感。
        /// </summary>
        private static void RevealNearby(GameRoom room, GamePlayer black)
        {
            if (room == null || black == null)
                return;

            ResolveRanges(black);

            var all = room.Players;
            for (int i = 0; i < all.Count; i++)
            {
                var other = all[i];
                if (other == null || other == black)
                    continue;
                // 不该被看见的人（死者 / 幽灵 / 躲进柜子的活人 / 旁观者）一个都不补 —— 判据见 InvisibleReason。
                // ⚠ **只看 `State == Hide` 是不够的**：死亡时若该客户端正在操作发信机 / 对讲机，
                //   服务端的 `PublicInfo.State` 会被改回 `Idle`（链条见 InvisibleReason 的注释），
                //   于是 Hide 这个标记消失，这里的主动补 AddPlayer 就会把幽灵塞回黑方视野。
                //   原版在死亡那一刻已用 `ExitPlayer → EnterGhostVisibility`（:176116）把他 despawn 过，
                //   所以这里"不把他加回来"就是全部该做的事。
                string why = InvisibleReason(other);
                if (why != null)
                {
                    LogHiddenOnce(black, other, why);
                    continue;
                }
                // 命中任一范围（自身 ∪ 小熊）且没被墙挡住 ⇒ 确保可见
                if (InAnyEnter(other, out ClipRange hit, out float dEnter)
                    && !WallBlocks(black, other, hit.Cx, hit.Cy, dEnter))
                    other.AddPlayer(black);      // 幂等：不在列表才真正发送 S_SPAWN
            }
        }

        // ── ① 加入：拒绝把远处的玩家介绍给黑方 ──────────────────────────
        [HarmonyPatch(typeof(GamePlayer), nameof(GamePlayer.AddPlayer))]
        [HarmonyPrefix]
        private static bool PrefixAddPlayer(GamePlayer __instance, GamePlayer player, ref bool __result)
        {
            if (ModeRuntime.Bypass)
                return true;
            if (!IsBlack(player))                 // 接收者不是黑方 → 原版行为
                return true;

            // 美幸扫描期：暂时解除 AOI，让黑方看到全图（1 秒后由扫描功能收回）
            if (MiyukiScanFeature.IsUnlocking(player))
                return true;

            // ★★ **被介绍的人自己**若是死者 / 幽灵 / 躲藏者 / 旁观者，绝不允许进入黑方的视野。
            //
            //   `X.AddPlayer(Y)` 的语义是"把 X 介绍给 Y"（原版 `AddPlayer` :175822 会给 Y 发 S_SPAWN），
            //   原版 `SearchAndUpdatePlayer`（:173427-173440，由 `Player.Move` :175895 调用）
            //   与本类两处主动补加、以及 `MiyukiScanFeature` 的解封补加，**全都走这个出口**
            //   ⇒ 判据落在这里最可靠。
            //   ⚠ **必须是 Prefix**：Postfix 跑的时候 S_SPAWN 已经发出去了。
            //
            //   ⚠ 为什么"幽灵会重新出现"：原版在死亡那一刻**已经处理干净** ——
            //     `OnDead`（:175968）→ `ExitPlayer()`（:176004）→ `GameRoom.EnterGhostVisibility(this)`（:176116）
            //     会给所有人发 `S_DESPAWN`（:169860-169863）并把双方从 `SharedPlayers` 互删（:169866-169873）。
            //     ⇒ 幽灵是**被本类自己重新加回去的**（`RevealNearby` 与每秒 tick 的两处 `AddPlayer`）：
            //       旧判据只看 `State == Hide`，而"死时正在操作发信机"那条路把 `PublicInfo.State` 改回了 `Idle`。
            //
            //   （勘误：早前注释把 `GameRoom.RestoreGhostVisibility`（:169896）当成本钩子拦的路径 ——
            //     它其实是**给死者自己**直发 `S_SPAWN` / `S_DESPAWN`（让幽灵看得见全场），
            //     **完全不经过 `AddPlayer`**，与本 Prefix 无关。）
            string why = InvisibleReason(__instance);
            if (why != null)
            {
                __result = false;
                Plugin.Log.LogInfo(
                    $"[HS] AoiCulling：拒绝把 #{__instance.PublicInfo?.PlayerId} 介绍给黑方 "
                    + $"#{player.PublicInfo?.PlayerId} —— {why}");
                return false;
            }

            Diagnostics.Hit("AoiCulling");

            ResolveRanges(player);

            // 命中任一范围（自身 ∪ 小熊）且没被墙挡住 ⇒ 允许
            if (InAnyEnter(__instance, out ClipRange hit, out float dEnter)
                && !WallBlocks(player, __instance, hit.Cx, hit.Cy, dEnter))
                return true;

            __result = false;
            return false;
        }

        /// <summary>记录首次可见时间，供 MinVisibleSeconds 使用。</summary>
        [HarmonyPatch(typeof(GamePlayer), nameof(GamePlayer.AddPlayer))]
        [HarmonyPostfix]
        private static void PostfixAddPlayer(GamePlayer __instance, GamePlayer player, bool __result)
        {
            if (!__result || ModeRuntime.Bypass)
                return;
            if (!IsBlack(player))
                return;

            VisibleSince[PairKey(player.PublicInfo.PlayerId, __instance.PublicInfo.PlayerId)]
                = TimeManager.Instance.SurviveTime;
        }

        /// <summary>
        /// 变幽灵的**那一刻**就从所有黑方视野里撤掉。
        ///
        /// 为什么还要这一个：`MakeSpectatorGhost`（:175590，"中途以旁观者进场"那条路）**不会**走
        /// `ExitPlayer → EnterGhostVisibility`，所以它没有原版那次即时 despawn，必须自己补。
        /// （死亡那条路原版已经在 `ExitPlayer()`（:176116）里 despawn 过了，本类的职责只是
        ///   **不要再把他加回来** —— 见 InvisibleReason 与 PrefixAddPlayer。）
        /// </summary>
        [HarmonyPatch(typeof(GamePlayer), "MakeSpectatorGhost")]
        internal static class GhostRemoveHook
        {
            [HarmonyPostfix]
            private static void Postfix(GamePlayer __instance)
            {
                if (ModeRuntime.Bypass || __instance?.PublicInfo == null)
                    return;

                var room = GameRoom.Instance;
                if (room == null)
                    return;

                var all = room.Players;
                for (int i = 0; i < all.Count; i++)
                {
                    var black = all[i];
                    if (black == null || black == __instance || !IsBlack(black))
                        continue;
                    __instance.RemovePlayer(black);      // 幂等：不在列表就不发 S_DESPAWN
                }
            }
        }

        // ── ② 黑方移动：立即校正可见性（消除"慢一拍"）────────────────────
        [HarmonyPatch(typeof(GamePlayer), nameof(GamePlayer.Move), new[] { typeof(PosInfo), typeof(bool) })]
        internal static class MoveHook
        {
            [HarmonyPostfix]
            private static void Postfix(GamePlayer __instance)
            {
                if (ModeRuntime.Bypass)
                    return;
                if (!IsBlack(__instance))
                    return;

                var room = GameRoom.Instance;
                if (room == null || room.State != EGameState.Survive)
                    return;

                RevealNearby(room, __instance);
            }
        }

        // ── ③ 每秒兜底：进入的保证可见、超出的剔除 ──────────────────────
        [HarmonyPatch(typeof(GameRoom), "SurvivalTick")]
        [HarmonyPostfix]
        private static void PostfixSurvivalTick(GameRoom __instance)
        {
            if (ModeRuntime.Bypass)
                return;
            if (__instance.State != EGameState.Survive)
                return;

            float minVisible = MinVisibleSeconds?.Value ?? 3f;
            int now = TimeManager.Instance.SurviveTime;

            // 用 Players 而非 AlivePlayers：假人可能不在 AlivePlayers 里，
            // 那会让下面的恢复逻辑连机会都没有。
            var all = __instance.Players;
            for (int i = 0; i < all.Count; i++)
            {
                var black = all[i];
                if (!IsBlack(black))
                    continue;

                // 美幸扫描解封期：本轮不做任何裁剪（既不剔除也不补加），
                // 由 MiyukiScanFeature 自己在解封结束时把超范围的人收回。
                // 之前只在 PrefixAddPlayer 加后门是不够的 —— 剔除走的是这里的循环。
                if (MiyukiScanFeature.IsUnlocking(black))
                    continue;
                ResolveRanges(black);

                for (int j = 0; j < all.Count; j++)
                {
                    var other = all[j];
                    if (other == null || other == black)
                        continue;

                    // 不该被看见的人（死者 / 幽灵 / 躲进柜子的活人 / 旁观者）必须主动从观察列表里撤掉。
                    // ⚠ 原版只在"变成幽灵那一刻"撤过一次（`ExitPlayer` → `EnterGhostVisibility` :176116），
                    //   之后本类的主动 `AddPlayer`（上面那段与 `RevealNearby`）会把他塞回来，
                    //   所以这里必须自己判、自己撤 —— 判据见 InvisibleReason。
                    //   （只看 `State == Hide` 会漏掉"死时正在操作发信机"的人：他的 State 被改回了 Idle。）
                    string why = InvisibleReason(other);
                    if (why != null)
                    {
                        // 幂等：不在列表就不发 S_DESPAWN。返回 true ⇒ 他确实溜进过观察列表，单独记一行。
                        if (other.RemovePlayer(black))
                        {
                            Plugin.Log.LogInfo(
                                $"[HS] AoiCulling：黑方 #{black.PublicInfo.PlayerId} 视野里撤下 "
                                + $"#{other.PublicInfo.PlayerId} —— {why}（他此前溜进了 SharedPlayers）");
                        }
                        LogHiddenOnce(black, other, why);
                        continue;
                    }

                    // ① 进入：命中任一范围（自身 ∪ 小熊）且没被墙挡住 ⇒ 确保可见
                    if (InAnyEnter(other, out ClipRange hitEnter, out float dEnter))
                    {
                        if (!WallBlocks(black, other, hitEnter.Cx, hitEnter.Cy, dEnter))
                            other.AddPlayer(black);
                        continue;
                    }

                    // ② 滞回：命中任一"退出"范围 ⇒ 维持现状（只看距离）
                    if (!InAnyExit(other, out ClipRange hitExit, out float dExit))
                    {
                        // ③ 全部范围都超出 ⇒ 剔除（受最短可见保护）
                        long keyFar = PairKey(black.PublicInfo.PlayerId, other.PublicInfo.PlayerId);
                        if (VisibleSince.TryGetValue(keyFar, out int sinceFar) && minVisible > 0f
                            && now - sinceFar < minVisible)
                            continue;             // 还在最短可见保护期内

                        Plugin.Log.LogInfo(
                            $"[HS] AoiCulling：黑方 #{black.PublicInfo.PlayerId} 剔除 #{other.PublicInfo.PlayerId}" +
                            "（超出全部范围）");
                        other.RemovePlayer(black);
                        VisibleSince.Remove(keyFar);
                        continue;
                    }

                    // 滞回区间内：★ 遮挡**同样生效** —— 走到墙后就该看不见。
                    //   否则会出现"在开阔处被看到一次之后，躲进墙后仍然可见"。
                    //   防抖交给上面的 MinVisibleSeconds：刚看到就进墙后，至少保留 minVisible 秒。
                    if (!WallBlocks(black, other, hitExit.Cx, hitExit.Cy, dExit))
                        continue;                 // 看得见 → 维持现状

                    long key = PairKey(black.PublicInfo.PlayerId, other.PublicInfo.PlayerId);
                    if (VisibleSince.TryGetValue(key, out int since) && minVisible > 0f
                        && now - since < minVisible)
                        continue;                 // 还在最短可见保护期内

                    Plugin.Log.LogInfo(
                        $"[HS] AoiCulling：黑方 #{black.PublicInfo.PlayerId} 剔除 #{other.PublicInfo.PlayerId}" +
                        $"（被墙挡住，距离 {Math.Sqrt(dExit):F0}，圆心 {hitExit.Cx:F0},{hitExit.Cy:F0}）");
                    other.RemovePlayer(black);
                    VisibleSince.Remove(key);
                    continue;
                }
            }
        }

        /// <summary>
        /// 新对局开始时清空可见时间记录。
        /// 否则上一局的残留会在新局里被误判：新局 SurviveTime 由 <c>TimeManager.ResetSurvival()</c>
        /// 设回 **420**（不是从 0 重新计时），旧值一旦大于新局的 now，now - since 就是负数、
        /// 恒小于最短可见时间，那批玩家将永远不被剔除。
        /// </summary>
        [HarmonyPatch(typeof(GameRoom), "StartSurvive")]
        [HarmonyPostfix]
        private static void PostfixStartSurvive()
        {
            VisibleSince.Clear();
            HiddenLogged.Clear();           // 上局的"已打过日志"不能带进新局，否则关键那行不再出现
        }
    }
}
