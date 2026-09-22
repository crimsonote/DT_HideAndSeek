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
    /// 原理是复用原版自己的兴趣区域原语，而不是屏蔽客户端渲染：
    ///   X.SharedPlayers 的语义是"能看到 X 的人"，服务端
    ///   AddPlayer（:175822）向观察者发 S_SPAWN、RemovePlayer（:175841）发 S_DESPAWN，
    ///   而位置广播 BroadcastToPlayerAndObservers（:176969）只发给 SharedPlayers。
    ///   因此把远处的玩家从黑方的观察列表里移除，黑方客户端上：3D 模型消失、
    ///   小地图 pin 消失、MyPlayer.GetTargetPlayer 也选不中他。
    ///
    /// 两个时机：
    ///   ① 加入 —— Prefix AddPlayer：接收者是黑方且超出阈值时拒绝（随移动触发）
    ///   ② 剔除 —— Postfix SurvivalTick：每秒清理超出阈值的（滞回避免抖动）
    ///
    /// 阈值依据：客户端攻击索敌 224、服务端距离校验 672（451584 = 672²）、
    /// 跑速约 728/s、位置包 10Hz（采样粒度 72.8，故滞回必须远大于它）。
    ///
    /// ★ 技能感知豁免（见 SkillAware）：
    ///   某些技能需要"看得见目标"才能在客户端选中，而 AOI 会让它们静默失效：
    ///   - TimeStop（Seol）：SkillData.IsTarget=true、Range=3.0×224=672。
    ///     客户端 UpdateSkillTargetPlayer → GetSkillTarget 只遍历本地 Players，
    ///     672 内若无人被生成则 CanUseSkillCondition=false，按键毫无反应。
    ///   - Marionette（Rin 的小熊）：探测是**纯客户端本地判定**（Summon.DetectNearbyPlayer
    ///     以召唤物坐标为圆心遍历本地 Players），而召唤物固定在自己脚下不随人移动。
    ///     本人走远后熊周围的人被裁掉，于是"熊贴着人也报无人在附近"。
    ///   处理方式：持 TimeStop 时把阈值放宽到其索敌半径；持 Marionette 且召唤物已放出时，
    ///   把裁剪圆心改成召唤物坐标（并按其探测半径）。
    /// </summary>
    [HarmonyPatch]
    [PatchFeature(
        section: "AoiCulling",
        description: "黑方视野裁剪：黑方只能看到附近的玩家（模型与小地图 pin 同时消失）。对时停、小熊等依赖真实目标信息的技能自动豁免。",
        defaultEnabled: true,
        side: FeatureSide.Host)]
    internal static class AoiCullingFeature
    {
        [ConfigField(600f, "进入可见范围的距离。建议不小于客户端攻击距离 224，且略小于服务端校验距离 672。",
            Min = 200f, Max = 5000f)]
        public static ConfigEntry<float> EnterRange;

        [ConfigField(900f, "离开可见范围的距离。必须明显大于进入距离（滞回），否则边界会抖动。",
            Min = 200f, Max = 5000f)]
        public static ConfigEntry<float> ExitRange;

        [ConfigField(3f, "最短可见秒数：进入视野后至少保持这么久不被剔除。", Min = 0f, Max = 60f)]
        public static ConfigEntry<float> MinVisibleSeconds;

        [ConfigField(true, "技能感知：持时停（放宽到 672）或放出小熊（圆心改为召唤物）时自动豁免裁剪，避免技能静默失效。")]
        public static ConfigEntry<bool> SkillAware;

        /// <summary>TimeStop 的客户端索敌半径 = SkillData.Range(3.0) × 224（:31451 的换算）。</summary>
        private const float TimeStopSearchRange = 672f;

        /// <summary>小熊的客户端探测半径（Summon.DetectNearbyPlayer 的椭圆按 448/410.67，这里取保守圆）。</summary>
        private const float MarionetteDetectRadius = 448f;

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

        /// <summary>
        /// 计算某黑方的裁剪圆心与半径。
        /// 默认是黑方自己；持 Marionette 且召唤物已放出时改为召唤物坐标及其探测半径。
        /// </summary>
        private static void ResolveCenterAndRange(
            GamePlayer black, float baseRange, out float cx, out float cy, out float range)
        {
            var pos = black?.PublicInfo?.Pos;
            cx = pos?.X ?? 0f;
            cy = pos?.Y ?? 0f;
            range = baseRange;

            if (!SkillAwareOn)
                return;

            if (SkillTypeOf(black) != ESkillType.Marionette)
                return;

            int summonId = GetSummonId(black.SkillComponent);
            if (summonId == 0)
                return;   // 召唤物还没放出来

            // 注意：Assembly-CSharp 同时存在全局 DeviceManager 与 Server.Game.DeviceManager，须显式限定
            var summon = Server.Game.DeviceManager.Instance?.GetSummon(summonId);
            var spos = summon?.DeviceInfo?.Pos;
            if (spos == null)
                return;

            cx = spos.X;
            cy = spos.Y;
            range = Math.Max(baseRange, MarionetteDetectRadius);
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

            Diagnostics.Hit("AoiCulling");

            float enter = EnterRange?.Value ?? 600f;

            // 时停：客户端索敌半径比 AOI 更远，必须放宽否则技能点不出来
            if (SkillAwareOn && SkillTypeOf(player) == ESkillType.TimeStop)
                enter = Math.Max(enter, TimeStopSearchRange);

            ResolveCenterAndRange(player, enter, out float cx, out float cy, out float range);

            if (DistanceSq(__instance, cx, cy) <= range * range)
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

        // ── ② 剔除：超出阈值的从黑方观察列表移除 ─────────────────────────
        [HarmonyPatch(typeof(GameRoom), "SurvivalTick")]
        [HarmonyPostfix]
        private static void PostfixSurvivalTick(GameRoom __instance)
        {
            if (ModeRuntime.Bypass)
                return;
            if (__instance.State != EGameState.Survive)
                return;

            float exit = ExitRange?.Value ?? 900f;
            float minVisible = MinVisibleSeconds?.Value ?? 3f;
            int now = TimeManager.Instance.SurviveTime;

            var alive = __instance.AlivePlayers;
            for (int i = 0; i < alive.Count; i++)
            {
                var black = alive[i];
                if (!IsBlack(black))
                    continue;

                float baseExit = exit;
                if (SkillAwareOn && SkillTypeOf(black) == ESkillType.TimeStop)
                    baseExit = Math.Max(baseExit, TimeStopSearchRange);

                ResolveCenterAndRange(black, baseExit, out float cx, out float cy, out float range);
                float exitSq = range * range;

                for (int j = 0; j < alive.Count; j++)
                {
                    var other = alive[j];
                    if (other == null || other == black)
                        continue;
                    if (DistanceSq(other, cx, cy) <= exitSq)
                        continue;

                    long key = PairKey(black.PublicInfo.PlayerId, other.PublicInfo.PlayerId);
                    if (VisibleSince.TryGetValue(key, out int since) && minVisible > 0f
                        && now - since < minVisible)
                        continue;                 // 还在最短可见保护期内

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
