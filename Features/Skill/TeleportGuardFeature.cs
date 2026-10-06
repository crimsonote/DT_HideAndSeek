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

        [ConfigField(true, "把黑洞特效改写到实际落点（就近出生点），用于提示附近的人它要来了；关闭则保留原版的坐标泄露。")]
        public static ConfigEntry<bool> BlockVfxLeak;

        [ConfigField(false,
            "是否区分阵营。关闭（默认）＝黑白方行为一致，落点都会被改写 —— " +
            "不会因「只有黑方被改写」而让玩家从落点差异推断出谁是黑方；" +
            "开启后只限制黑方、白方完全保持原版。")]
        public static ConfigEntry<bool> OnlyForBlack;

        /// <summary>本次施法的施法者 ID，用于区分"自己脚下那一发"与"目标身上那一发"。</summary>
        private static int _casterId;

        /// <summary>本次施法者是否为黑方。</summary>
        private static bool _casterIsBlack;

        /// <summary>本次黑洞的实际落点（由 LandingHook 写入），供 VfxHook 画特效。</summary>
        private static PosInfo _pendingLanding;

        /// <summary>
        /// _pendingLanding 的登记时刻。它是静态字段、只写不清，
        /// 若不设失效窗口，之后任何一次非施法者的 BlackHoleVfx 广播
        /// 都会被改写到**很久以前那次**的落点上（黑洞技能以外的场景也会被污染）。
        /// </summary>
        private static float _pendingAt = -999f;

        /// <summary>
        /// 抑制计数：TeleportCommandFeature 在自己播落点特效前后增减，
        /// 避免那一发被 VfxHook 误判为"黑洞技能的特效"而改写到旧落点。
        /// </summary>
        internal static int Suppress;

        private const float PendingTtl = 5f;

        /// <summary>
        /// 清空本功能全部跨局静态状态。**必须在回大厅 / 开局时调用。**
        ///
        /// 为什么必须清：<c>TimeManager.ResetSurvival()</c> 每局把 SurviveTime 设回
        /// <b>420</b>（不是从 0 重新计时）。上一局登记的 <c>_pendingAt</c> 可能比新局的 now
        /// 还大，于是 <c>now - _pendingAt</c> 为负、旧落点会一直"没过期"，
        /// <b>新局第一次黑洞特效会被改写到上一局的落点</b>。
        ///
        /// 这是双保险的第一道（显式清理）；第二道在 <see cref="VfxHook"/> 的过期判据里
        /// （要求时间戳必须落在过去），即使这里漏清也不会把负差当成"还没过期"。
        /// </summary>
        private static void ResetState()
        {
            _pendingLanding = null;
            _pendingAt = -999f;
            _casterId = 0;
            _casterIsBlack = false;
            Suppress = 0;
        }

        // 清理是基础能力、不该受配置影响，所以两个钩子都放在本类里。
        // ⚠ 不能挪到默认关闭的段：PatchLoader 对未启用的段会跳过整类 PatchAll，
        // 清理钩子会形同不存在（AGENTS「已踩过的坑」第 8 条）。
        [HarmonyPatch(typeof(GameRoom), "StartLobby")]
        internal static class StartLobbyResetHook
        {
            [HarmonyPostfix]
            private static void Postfix() => ResetState();
        }

        [HarmonyPatch(typeof(GameRoom), "StartSurvive")]
        internal static class StartSurviveResetHook
        {
            [HarmonyPostfix]
            private static void Postfix() => ResetState();
        }

        // ── L3：落点改写 ────────────────────────────────────────────
        [HarmonyPatch(typeof(GameSkill), "TryGetSafeLandingPos")]
        internal static class LandingHook
        {
            [HarmonyPrefix]
            private static bool Prefix(GameSkill __instance, GamePlayer target, out PosInfo landingPos, ref bool __result)
            {
                landingPos = null;

                if (ModeRuntime.Bypass)
                    return true;
                if (RewriteLanding == null || !RewriteLanding.Value)
                    return true;
                // 只限制黑方：白方用黑洞属于正常玩法，落点不应被改写
                if (OnlyForBlack != null && OnlyForBlack.Value
                    && __instance?.Owner?.Color != EPlayerColor.Black)
                    return true;

                var spawn = NearestSpawnTo(target);
                if (spawn == null)
                    return true;   // 拿不到出生点表，交还原版

                landingPos = spawn;
                _pendingLanding = spawn;      // 供 VfxHook 把特效画在真正的落点上
            _pendingAt = TimeManager.Instance?.SurviveTime ?? 0f;
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

        // ── L4：把黑洞特效画在真正的落点上 ───────────────────────────
        /// <summary>
        /// 原版会为目标发一发 BlackHoleVfx（deviceId = 目标、pos = 目标实时坐标），
        /// 那既暴露被裁剪目标的真实位置，又会让客户端优先按"玩家当前位置"渲染。
        ///
        /// 这里**不拦截** —— 拦掉就没人知道黑洞要来了。改为把它改写到实际落点：
        ///   deviceId 置 0 → 客户端走 else 分支，改用包里的坐标（PlayBlackHoleEffect :27152）
        ///   pos 换成本次算出的出生点
        /// 效果是"在黑洞真正要落下的地方闪一下"，用来提示附近的人它要过来了。
        /// </summary>
        [HarmonyPatch(typeof(GameRoom), nameof(GameRoom.BroadcastWorldVFX))]
        internal static class VfxHook
        {
            [HarmonyPrefix]
            private static void Prefix(EEffectType type, ref int deviceId, ref PosInfo pos)
            {
                if (ModeRuntime.Bypass || _pendingLanding == null)
                    return;

                // ⚠ 抑制计数（Suppress）由**别的功能**在自己播黑洞特效的前后增减：
                //    TeleportCommandFeature.cs:270 与 MioTeleportFeature（澪传送）。
                //    这个字段此前只被写、**没有任何地方读** —— 抑制是空转的，
                //    于是"我们自己发的那发特效"照样会被下面改写到上一次技能的旧落点。
                //    判据必须放在最前面：它与 _pendingLanding 的过期窗口无关，
                //    而是"这一发是我自己发的，别碰"。
                if (Suppress > 0)
                    return;

                    // 过期即作废：该字段只写不清，没有窗口会一直影响后续所有黑洞广播。
                    // 判据必须要求时间戳落在**过去**（now >= _pendingAt）：SurviveTime 每局被
                    // ResetSurvival() 设回 420（不是从 0），跨局时 now - _pendingAt 会是负数，
                    // 只判 `> PendingTtl` 会把负差当成"还没过期"。
                    float now = TimeManager.Instance?.SurviveTime ?? 0f;
                    if (now < _pendingAt || now - _pendingAt > PendingTtl)
                    {
                        _pendingLanding = null;
                        return;
                    }
                if (BlockVfxLeak == null || !BlockVfxLeak.Value)
                    return;
                if (type != EEffectType.BlackHoleVfx)
                    return;                      // 只关心黑洞，别误伤 FlashVfx/DyingVfx 等一大家族
                if (deviceId == _casterId)
                    return;                      // 施法者脚下那一发保持原样
                if (OnlyForBlack != null && OnlyForBlack.Value && !_casterIsBlack)
                    return;                      // 白方用黑洞 → 特效也保持原版

                deviceId = 0;                    // 让客户端走 effect.Pos 分支，而不是按玩家位置渲染
                pos = _pendingLanding;           // 画在实际落点（就近出生点）
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
                _casterIsBlack = __instance?.Owner?.Color == EPlayerColor.Black;
                Diagnostics.Hit("TeleportGuard");
            }
        }
    }
}
