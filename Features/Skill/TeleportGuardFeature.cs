using BepInEx.Configuration;
using HarmonyLib;
using Protocol;
using Server.Game;
using HideAndSeek.Core;
using GamePlayer = Server.Game.Player;
using GameRoom = Server.Game.GameRoom;
using GameSkill = Server.Game.SkillComponent;

namespace HideAndSeek.Features.Skill
{
    /// <summary>
    /// 黑洞（Noel 的 Teleport）防滥用。
    ///
    /// 原版行为（:177439 UseTeleport）：
    ///   选「离自己最远的存活玩家」，2 秒前摇后**精确落到他当前位置**
    ///   （唯二例外：目标在玩 Nintendo / 躲在柜子里，此时落到设备规定的点）。
    ///   选人走服务端全量 AlivePlayers，AOI 完全管不到 ⇒ 黑方可以把它当"最远的人在哪"的雷达，
    ///   并且直接撞到人身上 —— 捉迷藏玩法会因此失效。
    ///
    /// 本功能两件事：
    ///   L3 落点改写：Prefix SkillComponent.TryGetSafeLandingPos（:177479，private）。
    ///       原版恰好用它做落点修正（`if (!TryGetSafeLandingPos(...)) destPos = 目标当前位置`，:177452），
    ///       而 destPos 是在 2 秒 PushAfter **之前**算好的，所以改这一处就改了最终落点，
    ///       不需要碰协程闭包。落点取 StartPosList 中离目标最近的出生点（结构性、保证可站）。
    ///   L4 特效泄露：原版会 `BroadcastWorldVFX(BlackHoleVfx, 目标id, 目标坐标, 1792)`
    ///       （:177445-177446），而客户端 PlayBlackHoleEffect（:27152）在本地找不到该玩家时
    ///       **回落到包里的真实坐标** —— 目标是"模型与 pin 都被 AOI 隐藏"，却有一个黑洞特效
    ///       精确画在他脚下。所以对非施法者那一发予以拦截。
    /// </summary>
    [PatchFeature(
        section: "TeleportGuard",
        description: "黑洞防滥用：把传送落点改为目标附近的出生点（不再精准落到人身上），并拦掉暴露目标真实坐标的黑洞特效。",
        defaultEnabled: true,
        side: FeatureSide.Host)]
    internal static class TeleportGuardFeature
    {
        [ConfigField(true, "改写传送落点：改为 StartPosList 中离目标最近的出生点。关闭则保持原版（精确落到目标身上）。")]
        public static ConfigEntry<bool> RewriteLanding;

        [ConfigField(true, "拦截暴露目标真实坐标的黑洞特效（仅拦发给非施法者的那一发；关闭则原版表现）。")]
        public static ConfigEntry<bool> BlockVfxLeak;

        /// <summary>本次施法的施法者 ID，用于区分"自己脚下那一发"与"目标身上那一发"。</summary>
        private static int _casterId;

        // ── L3：落点改写 ────────────────────────────────────────────
        [HarmonyPatch(typeof(GameSkill), "TryGetSafeLandingPos")]
        internal static class LandingHook
        {
            [HarmonyPrefix]
            private static bool Prefix(GamePlayer target, out PosInfo landingPos, ref bool __result)
            {
                landingPos = null;

                if (ModeRuntime.Bypass)
                    return true;
                if (RewriteLanding == null || !RewriteLanding.Value)
                    return true;

                var spawn = NearestSpawnTo(target);
                if (spawn == null)
                    return true;   // 拿不到出生点表，交还原版

                landingPos = spawn;
                __result = true;   // 必须 true：false 会让原版回退成"精确落到目标当前位置"
                Plugin.Log.LogInfo(
                    $"[HS] TeleportGuard：落点改写为出生点 ({spawn.X:F0},{spawn.Y:F0})（目标 #{target?.PublicInfo?.PlayerId}）。");
                return false;
            }
        }

        /// <summary>StartPosList 中离目标最近的出生点。</summary>
        private static PosInfo NearestSpawnTo(GamePlayer target)
        {
            var starts = Managers.Data?.MapData?.StartPosList;
            var tp = target?.PublicInfo?.Pos;
            if (starts == null || starts.Count == 0 || tp == null)
                return null;

            PosInfo best = null;
            float bestSq = float.MaxValue;

            foreach (var s in starts)
            {
                if (s == null)
                    continue;

                float dx = s.X - tp.X;
                float dy = s.Y - tp.Y;
                float d = dx * dx + dy * dy;
                if (d < bestSq)
                {
                    bestSq = d;
                    best = s;
                }
            }

            return best?.Clone();
        }

        // ── L4：黑洞特效坐标泄露 ─────────────────────────────────────
        [HarmonyPatch(typeof(GameRoom), nameof(GameRoom.BroadcastWorldVFX))]
        internal static class VfxHook
        {
            [HarmonyPrefix]
            private static bool Prefix(EEffectType type, int deviceId)
            {
                if (ModeRuntime.Bypass)
                    return true;
                if (BlockVfxLeak == null || !BlockVfxLeak.Value)
                    return true;
                if (type != EEffectType.BlackHoleVfx)
                    return true;                 // 只关心黑洞，别误伤 FlashVfx/DyingVfx 等一大家族
                if (deviceId == _casterId)
                    return true;                 // 保留施法者脚下那一发

                return false;                    // 拦掉"画在目标身上"的那一发
            }
        }

        /// <summary>记录本次施法者，供 VfxHook 区分两发特效。</summary>
        [HarmonyPatch(typeof(GameSkill), "UseTeleport")]
        internal static class UseTeleportHook
        {
            [HarmonyPrefix]
            private static void Prefix(GameSkill __instance)
            {
                _casterId = __instance?.Owner?.PublicInfo?.PlayerId ?? 0;
                Diagnostics.Hit("TeleportGuard");
            }
        }
    }
}
