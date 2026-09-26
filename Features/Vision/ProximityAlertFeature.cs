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
    /// 黑方接近预警（心跳）。
    ///
    /// 语义：黑方离某位白方足够近时，**只给那位白方**放一组"心跳"——
    /// 主拍（咚）+ 短促回声（哒），节奏随距离变快。白方据此判断"有人在这片区域"，
    /// 而且因为用的是**世界音**，他会听出**大致方向**（左右耳强弱）。
    ///
    /// 三条约束（需求明确）：
    ///   1. **不发给黑方自己** —— 用 `SendWorldSFX(Player, …)` 定向，而不是广播
    ///   2. **不发给视野外的人** —— 距离超过基准就干脆不发（同时 `distance` 参数也会让客户端听不见）
    ///   3. **站着不动也要响** —— 节拍挂在 1 Hz 的 `SurvivalTick` 上，不依赖任何人的移动
    ///
    /// 节拍怎么表达"缓急"：`SurvivalTick` 只有 1 Hz，所以用"每个 tick 发几拍"来做：
    ///   外围（远）：每 2 个 tick 发 1 拍 ⇒ 0.5 Hz
    ///   内圈（中）：每个 tick 发 1 拍    ⇒ 1 Hz
    ///   贴近（近）：每个 tick 发 2 拍    ⇒ 2 Hz（第二拍用 PushAfter 排到 500ms 后）
    ///
    /// 音效入口（服务端，`Assembly-CSharp`）：
    ///   :172111  SendWorldSFX(Player, ESoundType, PosInfo, distance)  ← **本功能用它**（定向 + 位置 + 衰减）
    ///   :172137  BroadcastWorldSFX(ESoundType, PosInfo, distance)     ← 广播，会连黑方一起听到，不用
    /// 客户端那一侧（`Managers.Sound.PlayWorld` :36459）会据距离决定 `spatialBlend`
    /// （≤448 时变 2D ⇒ 方位感消失；>448 时 3D ⇒ 能听出左右）。
    /// </summary>
    [PatchFeature("ProximityAlert",
        "黑方接近时给白方播放心跳式预警（主拍+回声，节奏随距离变快；只发给范围内的白方，黑方自己不受影响）。",
        defaultEnabled: true, side: FeatureSide.Host)]
    internal static class ProximityAlertFeature
    {
        [ConfigField(true, "启用接近预警。")]
        public static ConfigEntry<bool> AlertEnabled;

        [ConfigField(1.4f, "预警外圈倍率：以 AoiCulling.ExitRange（黑方地图视野）为基准，乘这个倍数就是最外层的感知范围。",
            Min = 1f, Max = 3f)]
        public static ConfigEntry<float> RangeMul;

        [ConfigField("IgniteSfx", "主拍音效（咚）。ESoundType 名。")]
        public static ConfigEntry<string> SignalSfx;

        [ConfigField("PurpleCandleSfx", "回声音效（哒）；留空则不发声。ESoundType 名。")]
        public static ConfigEntry<string> EchoSfx;

        [ConfigField(180f, "回声相对主拍的延迟（毫秒）。", Min = 40f, Max = 500f)]
        public static ConfigEntry<float> EchoDelayMs;

        [ConfigField("ScanningSfx", "背景音（每 N 拍垫一次，营造氛围）；留空则不放。ESoundType 名。")]
        public static ConfigEntry<string> BgSfx;

        [ConfigField(4, "背景音每隔几拍放一次（0 = 关闭背景）。", Min = 0f, Max = 16f)]
        public static ConfigEntry<int> BgEveryBeats;

        /// <summary>每个 tick（1 秒）的序号，用来做"每 2 tick 一拍"的低频档。</summary>
        private static int _tick;

        /// <summary>累计发过多少拍，用来决定背景音什么时候垫一次。</summary>
        private static int _beats;

        [HarmonyPatch(typeof(GameRoom), "SurvivalTick")]
        internal static class TickHook
        {
            [HarmonyPostfix]
            private static void Postfix(GameRoom __instance)
            {
                if (ModeRuntime.Bypass || __instance?.Players == null)
                    return;
                if (AlertEnabled == null || !AlertEnabled.Value)
                    return;
                if (__instance.State != EGameState.Survive)
                    return;

                _tick++;

                // 基准范围 = 黑方地图视野（热更新：每 tick 现读）
                float sight = AoiCullingFeature.ExitRange?.Value ?? 900f;
                float outer = sight * (RangeMul?.Value ?? 1.4f);
                float outer2 = outer * outer;

                // 先收集黑方位置（可能有多个黑方）
                List<PosInfo> blacks = null;
                for (int i = 0; i < __instance.Players.Count; i++)
                {
                    var b = __instance.Players[i];
                    if (b?.PublicInfo == null || !b.IsAlive)
                        continue;
                    if (b.Color != EPlayerColor.Black && b.Color != EPlayerColor.Dark)
                        continue;
                    (blacks ?? (blacks = new List<PosInfo>())).Add(b.PublicInfo.Pos);
                }
                if (blacks == null)
                    return;                                  // 场上没黑方 ⇒ 不需要预警

                for (int i = 0; i < __instance.Players.Count; i++)
                {
                    var p = __instance.Players[i];
                    if (p?.PublicInfo == null || p.Session == null || !p.IsAlive)
                        continue;
                    // 约束 1：**不发给黑方自己**（只给白方）
                    if (p.Color != EPlayerColor.White)
                        continue;

                    var pp = p.PublicInfo.Pos;
                    if (pp == null)
                        continue;

                    // 找最近的黑方
                    float best = float.MaxValue;
                    PosInfo bestPos = null;
                    for (int k = 0; k < blacks.Count; k++)
                    {
                        var bp = blacks[k];
                        if (bp == null)
                            continue;
                        float dx = bp.X - pp.X, dy = bp.Y - pp.Y;
                        float d2 = dx * dx + dy * dy;
                        if (d2 < best)
                        {
                            best = d2;
                            bestPos = bp;
                        }
                    }

                    // 约束 2：**视野外不发**（超过外圈倍率就完全不管）
                    if (bestPos == null || best > outer2)
                        continue;

                    float dist = (float)global::System.Math.Sqrt(best);

                    // 分三档决定"这个 tick 发几拍"
                    int beatsThisTick;
                    if (dist > sight)
                        beatsThisTick = (_tick % 2 == 0) ? 1 : 0;   // 外围：每 2 tick 一拍（0.5Hz）
                    else if (dist > sight * 0.4f)
                        beatsThisTick = 1;                          // 内圈：每 tick 一拍（1Hz）
                    else
                        beatsThisTick = 2;                          // 贴近：每 tick 两拍（2Hz）

                    for (int b = 0; b < beatsThisTick; b++)
                    {
                        if (b == 0)
                            Beat(__instance, p, bestPos, sight);
                        else
                        {
                            // 第二拍排到 500ms 后 —— 1 Hz 的 tick 靠这个做出"2 Hz"
                            var who = p;
                            var at = bestPos;
                            float d = sight;
                            __instance.PushAfter(500, delegate { Beat(__instance, who, at, d); });
                        }
                    }
                }
            }
        }

        /// <summary>发一次心跳（主拍 + 延迟回声 + 偶尔背景音），全部只发给该白方。</summary>
        private static void Beat(GameRoom room, GamePlayer target, PosInfo sourcePos, float distance)
        {
            if (target?.Session == null || sourcePos == null)
                return;

            _beats++;

            SendOne(target, SignalSfx?.Value, sourcePos, distance);

            string echo = EchoSfx?.Value;
            if (!string.IsNullOrEmpty(echo) && !echo.Equals("none", global::System.StringComparison.OrdinalIgnoreCase))
            {
                var who = target;
                var at = sourcePos;
                float d = distance;
                float delay = EchoDelayMs?.Value ?? 180f;
                // 回声推迟一点，形成"咚…哒"的双拍
                room.PushAfter((int)delay, delegate { SendOne(who, echo, at, d); });
            }

            int every = BgEveryBeats?.Value ?? 0;
            string bg = BgSfx?.Value;
            if (every > 0 && !string.IsNullOrEmpty(bg)
                && !bg.Equals("none", global::System.StringComparison.OrdinalIgnoreCase)
                && _beats % every == 0)
            {
                SendOne(target, bg, sourcePos, distance);
            }
        }

        /// <summary>把某个音效**只**发给一个人（带位置 ⇒ 客户端会算左右方位与距离衰减）。</summary>
        private static void SendOne(GamePlayer target, string sfxName, PosInfo pos, float distance)
        {
            if (string.IsNullOrEmpty(sfxName) || target?.Session == null)
                return;
            if (!global::System.Enum.TryParse(sfxName, true, out ESoundType sfx))
            {
                Plugin.Log.LogWarning($"[HS] ProximityAlert：未知音效名 {sfxName}");
                return;
            }
            try
            {
                GameRoom.Instance.SendWorldSFX(target, sfx, pos, distance);
            }
            catch (global::System.Exception ex)
            {
                Plugin.Log.LogWarning($"[HS] ProximityAlert：发包失败 — {ex.Message}");
            }
        }

        // ── 跨局清理 ──
        [HarmonyPatch(typeof(GameRoom), "StartSurvive")]
        internal static class StartHook
        {
            [HarmonyPostfix]
            private static void Postfix()
            {
                _tick = 0;
                _beats = 0;
            }
        }

        [HarmonyPatch(typeof(GameRoom), "StartLobby")]
        internal static class LobbyHook
        {
            [HarmonyPostfix]
            private static void Postfix()
            {
                _tick = 0;
                _beats = 0;
            }
        }
    }
}
