using System;
using System.Collections.Generic;
using BepInEx.Configuration;
using HarmonyLib;
using Protocol;
using Server.Game;
using HideAndSeek.Core;
using GamePlayer = Server.Game.Player;

namespace HideAndSeek.Features.Vision
{
    /// <summary>
    /// 黑方视野裁剪（AOI）：黑方只能"看见"附近一定范围内的玩家。
    ///
    /// 原理是复用原版自己的兴趣区域原语，而不是屏蔽客户端渲染：
    ///   X.SharedPlayers 的语义是"能看到 X 的人"，Service 端
    ///   AddPlayer（:175822）向观察者发 S_SPAWN、RemovePlayer（:175841）发 S_DESPAWN，
    ///   而位置广播 BroadcastToPlayerAndObservers（:176969）只发给 SharedPlayers。
    ///   因此把远处的玩家从黑方的观察列表里移除，黑方客户端上：
    ///   3D 模型消失、平板/HUD 小地图 pin 消失（PlayerManager.Despawn :31312 会主动删 pin），
    ///   且因本地 Managers.Player.Players 里没有该条目，MyPlayer.GetTargetPlayer 也选不中他。
    ///
    /// 两个时机：
    ///   ① 加入 —— Prefix AddPlayer：接收者是黑方且超出 EnterRange 时拒绝（实时，随移动触发）
    ///   ② 剔除 —— Postfix SurvivalTick：每秒清理超出 ExitRange 的（滞回 300 单位，
    ///      远大于位置采样粒度 72.8，避免边界抖动；再叠加 MinVisibleSeconds 兜底）
    ///
    /// 阈值依据：客户端攻击索敌 224、服务端距离校验 672（451584 = 672²）、
    /// 跑速约 728/s（INIT_SPEED 560 × RUN_SPEED_DELTA 1.3）、位置包 10Hz。
    /// </summary>
    [HarmonyPatch]
    [PatchFeature(
        section: "AoiCulling",
        description: "黑方视野裁剪：黑方只能看到附近的玩家（模型与小地图 pin 同时消失），超出范围则完全找不到。",
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

        /// <summary>(黑方 PlayerId, 对方 PlayerId) → 首次可见的 SurviveTime。</summary>
        private static readonly Dictionary<long, int> VisibleSince = new Dictionary<long, int>();

        private static long PairKey(int blackId, int otherId)
            => ((long)blackId << 32) | (uint)otherId;

        private static bool IsBlack(GamePlayer player)
            => player != null && player.Color == EPlayerColor.Black;

        /// <summary>坐标平方距离，避免开方。</summary>
        private static float DistanceSq(GamePlayer a, GamePlayer b)
        {
            var pa = a?.PublicInfo?.Pos;
            var pb = b?.PublicInfo?.Pos;
            if (pa == null || pb == null)
                return float.MaxValue;

            float dx = pa.X - pb.X;
            float dy = pa.Y - pb.Y;
            return dx * dx + dy * dy;
        }

        // ── ① 加入：拒绝把远处的玩家介绍给黑方 ──────────────────────────
        [HarmonyPatch(typeof(GamePlayer), nameof(GamePlayer.AddPlayer))]
        [HarmonyPrefix]
        private static bool PrefixAddPlayer(GamePlayer __instance, GamePlayer player, ref bool __result)
        {
            Diagnostics.Hit("AoiCulling");
            if (ModeRuntime.Bypass)
                return true;
            if (!IsBlack(player))                 // 接收者不是黑方 → 原版行为
                return true;

            float enter = EnterRange?.Value ?? 600f;
            if (DistanceSq(__instance, player) <= enter * enter)
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

        // ── ② 剔除：超出 ExitRange 的从黑方观察列表移除 ─────────────────
        [HarmonyPatch(typeof(GameRoom), "SurvivalTick")]
        [HarmonyPostfix]
        private static void PostfixSurvivalTick(GameRoom __instance)
        {
            if (ModeRuntime.Bypass)
                return;
            if (__instance.State != EGameState.Survive)
                return;

            float exit = ExitRange?.Value ?? 900f;
            float exitSq = exit * exit;
            float minVisible = MinVisibleSeconds?.Value ?? 3f;
            int now = TimeManager.Instance.SurviveTime;

            var alive = __instance.AlivePlayers;
            for (int i = 0; i < alive.Count; i++)
            {
                var black = alive[i];
                if (!IsBlack(black))
                    continue;

                for (int j = 0; j < alive.Count; j++)
                {
                    var other = alive[j];
                    if (other == null || other == black)
                        continue;
                    if (DistanceSq(other, black) <= exitSq)
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
