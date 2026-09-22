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
    /// ★ 技能感知豁免（SkillAware）—— 两类技能需要"看得见目标"才能在客户端选中，
    ///   而 AOI 会让它们静默失效：
    ///
    ///   - TimeStop（Seol）：SkillData.IsTarget=true、Range=3.0×224=672。
    ///     客户端 UpdateSkillTargetPlayer → GetSkillTarget（:31443）只遍历本地 Players，
    ///     672 内若无人被生成则 CanUseSkillCondition=false → UseSkill 直接 return，
    ///     表现为"按键毫无反应"。故把阈值放宽到 672（与关掉 AOI 的原版逐位等价）。
    ///
    ///   - Marionette（Rin 的小熊）：探测是**纯客户端本地判定**
    ///     （Summon.DetectNearbyPlayer :7290，以召唤物坐标为圆心遍历本地 Players），
    ///     而召唤物固定在自己脚下、不随人移动。本人走远后熊周围的人被裁掉，
    ///     于是"熊贴着人也报无人在附近"。故圆心改为召唤物坐标，
    ///     并**按它自己的探测半径 448 取阈值**（不是沿用 900 —— 那会让熊周围的
    ///     可见范围比该技能应有的信息多一倍）。
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

        /// <summary>
        /// 黑方若已放出小熊，返回召唤物坐标作为裁剪圆心。
        /// 返回 false 时调用方应使用黑方自己的坐标。
        /// </summary>
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

        /// <summary>黑方自己的坐标，作为默认圆心。</summary>
        private static void OwnCenter(GamePlayer black, out float cx, out float cy)
        {
            var pos = black?.PublicInfo?.Pos;
            cx = pos?.X ?? 0f;
            cy = pos?.Y ?? 0f;
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

            float range = EnterRange?.Value ?? 600f;

            if (SkillAwareOn && SkillTypeOf(player) == ESkillType.TimeStop)
                range = Math.Max(range, TimeStopSearchRange);

            if (TryMiniCenter(player, out float cx, out float cy))
                range = MarionetteEnterRange?.Value ?? 448f;   // 熊模式：用它自己的探测圈
            else
                OwnCenter(player, out cx, out cy);

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

            float minVisible = MinVisibleSeconds?.Value ?? 3f;
            int now = TimeManager.Instance.SurviveTime;

            // 用 Players 而非 AlivePlayers：假人可能不在 AlivePlayers 里，
            // 那会让下面的恢复逻辑看不到它。
            var all = __instance.Players;
            for (int i = 0; i < all.Count; i++)
            {
                var black = all[i];
                if (!IsBlack(black))
                    continue;

                float range = ExitRange?.Value ?? 900f;

                if (SkillAwareOn && SkillTypeOf(black) == ESkillType.TimeStop)
                    range = Math.Max(range, TimeStopSearchRange);

                if (TryMiniCenter(black, out float cx, out float cy))
                    range = MarionetteExitRange?.Value ?? 648f;   // 熊模式
                else
                    OwnCenter(black, out cx, out cy);

                float rangeSq = range * range;

                for (int j = 0; j < all.Count; j++)
                {
                    var other = all[j];
                    if (other == null || other == black)
                        continue;

                    float dSq = DistanceSq(other, cx, cy);

                    if (dSq <= rangeSq)
                    {
                        // 在范围内 → 主动确保可见。
                        //
                        // 关键：剔除是单向动作，而原版的"重新可见"依赖**被观察者自己移动**
                        // （AddPlayer 由 AreaManager.SearchAndUpdatePlayer 在被观察者 Move 时触发）。
                        // 假人站着不动，所以一旦被剔除就永久消失 —— 这正是
                        // "白方时能看到、变黑后走到它面前也看不到"的原因。
                        // AddPlayer 幂等（已在列表则立即返回 false），每秒调用开销可忽略。
                        other.AddPlayer(black);
                        continue;
                    }

                    long key = PairKey(black.PublicInfo.PlayerId, other.PublicInfo.PlayerId);
                    if (VisibleSince.TryGetValue(key, out int since) && minVisible > 0f
                        && now - since < minVisible)
                        continue;                 // 还在最短可见保护期内

                    // 记录实际距离：用于区分"被裁剪剔除"与"被黑灯遮住"两种看不见
                    Plugin.Log.LogInfo(
                        $"[HS] AoiCulling：黑方 #{black.PublicInfo.PlayerId} 剔除 #{other.PublicInfo.PlayerId}" +
                        $"（距离 {Math.Sqrt(dSq):F0} > 阈值 {range:F0}，圆心 {cx:F0},{cy:F0}）");

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
