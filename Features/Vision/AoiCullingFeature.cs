using System;
using System.Collections.Generic;
using BepInEx.Configuration;
using HarmonyLib;
using Protocol;
using Server.Game;
using HideAndSeek.Core;
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
        [ConfigField(750f, "进入可见范围的距离。建议不小于客户端攻击距离 224；调大能让靠近时更早被识别。",
            Min = 200f, Max = 5000f)]
        public static ConfigEntry<float> EnterRange;

        [ConfigField(1100f, "离开可见范围的距离。须明显大于进入距离（滞回），否则边界会抖动。",
            Min = 200f, Max = 5000f)]
        public static ConfigEntry<float> ExitRange;

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

        // SkillComponent._summonId 是私有字段，反射读取（失败则整体降级为不使用召唤物圆心）
        private static AccessTools.FieldRef<GameSkill, int> _summonIdRef;
        private static bool _summonIdFailed;

        private static bool SkillAwareOn => SkillAware == null || SkillAware.Value;

        private static long PairKey(int blackId, int otherId)
            => ((long)blackId << 32) | (uint)otherId;

        private static bool IsBlack(GamePlayer player)
            => player != null && player.Color == EPlayerColor.Black;

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

        /// <summary>解出某黑方本次应使用的圆心与进入/退出半径。</summary>
        private static void Resolve(GamePlayer black,
            out float cx, out float cy, out float enter, out float exit)
        {
            enter = EnterRange?.Value ?? 750f;
            exit = ExitRange?.Value ?? 1100f;

            if (SkillAwareOn && SkillTypeOf(black) == ESkillType.TimeStop)
            {
                enter = Math.Max(enter, TimeStopSearchRange);
                exit = Math.Max(exit, TimeStopSearchRange);
            }

            if (TryMiniCenter(black, out cx, out cy))
            {
                enter = MarionetteEnterRange?.Value ?? 448f;
                exit = MarionetteExitRange?.Value ?? 648f;
            }
            else
            {
                OwnCenter(black, out cx, out cy);
            }
        }

        /// <summary>
        /// 对黑方附近的人主动确保可见。黑方移动时立即调用，
        /// 避免只靠每秒一次的 tick 而产生"靠近后慢一拍"的观感。
        /// </summary>
        private static void RevealNearby(GameRoom room, GamePlayer black)
        {
            if (room == null || black == null)
                return;

            Resolve(black, out float cx, out float cy, out float enter, out _);
            float enterSq = enter * enter;

            var all = room.Players;
            for (int i = 0; i < all.Count; i++)
            {
                var other = all[i];
                if (other == null || other == black)
                    continue;
                // 跳过 Hide 状态：死亡玩家会被 MakeSpectatorGhost（:175590）置为
                // State=Hide + IsGhost=true，原版 SearchAndUpdatePlayer 同样跳过他们。
                // 少了这一条，主动补 AddPlayer 就会把死人的幽灵塞给黑方。
                if (other.State == EPlayerState.Hide)
                    continue;
                if (DistanceSq(other, cx, cy) <= enterSq)
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

            Diagnostics.Hit("AoiCulling");

            Resolve(player, out float cx, out float cy, out float enter, out _);

            if (DistanceSq(__instance, cx, cy) <= enter * enter)
                return true;                      // 在范围内，允许

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
                Resolve(black, out float cx, out float cy, out float enter, out float exit);
                float enterSq = enter * enter;
                float exitSq = exit * exit;

                for (int j = 0; j < all.Count; j++)
                {
                    var other = all[j];
                    if (other == null || other == black)
                        continue;

                    // Hide 状态（死亡幽灵 / 躲藏者）不该出现在任何观察列表里。
                    // 它可能从别处进过 SharedPlayers，所以这里要主动移除。
                    if (other.State == EPlayerState.Hide)
                    {
                        other.RemovePlayer(black);
                        continue;
                    }

                    float dSq = DistanceSq(other, cx, cy);

                    if (dSq <= enterSq)
                    {
                        // 进入范围 → 确保可见（补回可能被剔除掉的目标）
                        other.AddPlayer(black);
                        continue;
                    }

                    if (dSq <= exitSq)
                        continue;                 // 滞回区间 → 维持现状，不增不减

                    long key = PairKey(black.PublicInfo.PlayerId, other.PublicInfo.PlayerId);
                    if (VisibleSince.TryGetValue(key, out int since) && minVisible > 0f
                        && now - since < minVisible)
                        continue;                 // 还在最短可见保护期内

                    Plugin.Log.LogInfo(
                        $"[HS] AoiCulling：黑方 #{black.PublicInfo.PlayerId} 剔除 #{other.PublicInfo.PlayerId}" +
                        $"（距离 {Math.Sqrt(dSq):F0} > 阈值 {exit:F0}，圆心 {cx:F0},{cy:F0}）");

                    other.RemovePlayer(black);    // 幂等：不在 SharedPlayers 时直接返回 false
                    VisibleSince.Remove(key);
                }
            }
        }

        /// <summary>
        /// 新对局开始时清空可见时间记录。
        /// 否则上一局的残留会在新局里被误判：新局 SurviveTime 从 0 重新计时，
        /// now - since 变成负数，恒小于最短可见时间，那批玩家将永远不被剔除。
        /// </summary>
        [HarmonyPatch(typeof(GameRoom), "StartSurvive")]
        [HarmonyPostfix]
        private static void PostfixStartSurvive()
        {
            VisibleSince.Clear();
        }
    }
}
