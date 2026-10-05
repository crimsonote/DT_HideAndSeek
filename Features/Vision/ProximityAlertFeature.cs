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
    /// ★ 提醒范围 = **这条音效实际能被听到的范围**，而不是"我们打算把包发给谁"。
    ///
    ///   旧实现：判据是「白方到黑方的距离 ≤ 视野 × RangeMul」（默认 900 × 1.4 = 1260），
    ///   而发给客户端的包里 `MaxDistance` 只有「视野」（默认 900）—— 于是 900~1260 之间的白方
    ///   **收到了"这里有个音源"的包，却什么也听不到**：把"通知了客户端"当成了"提醒"。
    ///
    ///   现在：先算出本次心跳的**音效可听范围**（= 会写进 `S_PLAY_WORLD_SFX.MaxDistance` 的那个值），
    ///   再按 `提醒范围 = min(音效范围, MaxRange)` 决定发给谁，并把这个范围**原样写进包** ——
    ///   收到包的白方必然在音效可听范围内，"提醒"因此才成立（范围与可听范围是同一个数）。
    ///
    ///   原版证据（`Assembly-CSharp.decompiled.cs`）：
    ///     :172111  `SendWorldSFX(Player, ESoundType, PosInfo, float distance = 896f)`
    ///              → :172116 `_play_world_sfx_pkt.MaxDistance = distance` ⇒ **音效可听范围就是这个值**
    ///     :36459/:36480 客户端 `Managers.Sound.PlayWorld(key, pos, maxDistance)` → `audioSource.maxDistance = maxDistance`
    ///     :43248/:43254 `Handle_S_PLAY_WORLD_SFX` 用包里的 `MaxDistance` 播 —— 超出该距离音量归零（听不见）
    ///     :130908/:130961 `S_PLAY_WORLD_SFX` 的第 4 个字段就是 `MaxDistance`
    ///
    ///   区分两类"音源"包（都是 1115 号 `S_PLAY_WORLD_SFX`，差别只在**收件人怎么选**）：
    ///     :172111 `SendWorldSFX(Player, …)`          定向给一个人 —— **本功能用它**（只给范围内的白方）
    ///     :172137 `BroadcastWorldSFX(…, PosInfo, distance)` 按 `distance` 过滤收件人（:172143-172146）
    ///              ⇒ 它"谁收到"与"谁听得见"天然一致；而定向发送把两者拆开了，
    ///                所以我们必须自己按**音效可听范围**过滤，不能用别的距离。
    ///
    /// 三条约束（需求明确）：
    ///   1. **不发给黑方自己** —— 用 `SendWorldSFX(Player, …)` 定向，而不是广播
    ///   2. **听不到的人不发** —— 超出提醒范围就完全不管（包里的 `MaxDistance` 与它同值）
    ///   3. **站着不动也要响** —— 节拍挂在 1 Hz 的 `SurvivalTick` 上，不依赖任何人的移动
    ///
    /// "没有声音就没有提醒"（明确不发心跳的两种情形）：
    ///   · 主拍音效留空或写 `none` ⇒ 整段不发（回声/背景音一起跳过，也不记账）
    ///   · 解析出的提醒范围 ≤ 0（例如上限被设成 0）⇒ 整段不发
    ///
    /// 节拍怎么表达"缓急"：`SurvivalTick` 只有 1 Hz，所以用"每个 tick 发几拍"来做：
    ///   外围（远）：每 2 个 tick 发 1 拍 ⇒ 0.5 Hz
    ///   内圈（中）：每个 tick 发 1 拍    ⇒ 1 Hz
    ///   贴近（近）：每个 tick 发 2 拍    ⇒ 2 Hz（第二拍用 PushAfter 排到 500ms 后）
    ///
    /// 客户端那一侧（`Managers.Sound.PlayWorld` :36459）还会据距离决定 `spatialBlend`
    /// （≤448 时变 2D ⇒ 方位感消失；>448 时 3D ⇒ 能听出左右）。
    /// </summary>
    [PatchFeature("ProximityAlert",
        "黑方接近时给白方播放心跳式预警（主拍+回声，节奏随距离变快；只发给音效实际覆盖得到的白方，黑方自己不受影响）。",
        defaultEnabled: true, side: FeatureSide.Host)]
    internal static class ProximityAlertFeature
    {
        /// <summary>
        /// 原版 `S_PLAY_WORLD_SFX` 的 `MaxDistance` 缺省值 = 原版默认的世界音效可听范围。
        /// 出处：服务端 `GameRoom.SendWorldSFX`(:172111) / `BroadcastWorldSFX`(:172137) 的
        /// `distance = 896f`；客户端 `Managers.Sound.PlayWorld`(:36459) 的 `maxDistance = 896f`。
        /// 用途：算不出音效范围时退回它（"拿不到就按原版默认算"）。
        /// </summary>
        private const float VanillaSfxRange = 896f;

        /// <summary>
        /// 提醒范围硬上限的**默认值** = 黑方升到 2 级视野后的地图视野范围（世界单位，固定值）。
        ///
        /// 出处（全是**默认值**链，逐行可查；单位 = 世界单位，224 = 一格）：
        ///   · `Features/Vision/AoiCullingFeature.cs:54`  ExitRange 默认 **900**（黑方地图视野基准）
        ///   · `Features/Combat/KillUpgradeFeature.cs:50` VisionBonusPerLevel 默认 **0.5**（"+50% / 级"）
        ///   · `Features/Combat/KillUpgradeFeature.cs:321-323`  `k = 1 + 0.5 × 等级`，直接乘在 ExitRange 上
        ///   ⇒ 2 级 = 900 × (1 + 0.5×2) = 900 × 2 = **1800**
        ///
        /// 各档（同一基准 900）：未升级 900（4.0 格）/ 1 级 1350（6.0 格）/
        /// 2 级 **1800**（8.0 格）/ 3 级 2250（10.0 格）。
        /// ⚠ 若房主改过 `AoiCulling.ExitRange` 或 `KillUpgrade.VisionBonusPerLevel`，
        ///   本默认值**不跟着变**（需求要的就是"固定值"）—— 需要时请直接改 MaxRange。
        /// </summary>
        private const float BlackVisionLevel2Range = 1800f;

        [ConfigField(true, "启用接近预警。")]
        public static ConfigEntry<bool> AlertEnabled;

        [ConfigField(1.4f, "预警外圈倍率：以 AoiCulling.ExitRange（黑方地图视野）为基准，乘这个倍数就是最外层的感知范围"
            + "（它同时就是心跳音效的可听范围，再受 MaxRange 上限封顶）。",
            Min = 1f, Max = 3f)]
        public static ConfigEntry<float> RangeMul;

        /// <summary>
        /// 提醒范围的**硬上限**：提醒范围 = min(音效可听范围, 本上限)，音效也按这个范围发。
        /// 默认取「黑方 2 级视野」是有意的 —— 既覆盖"能看见黑方走过来"的距离，
        /// 又不会随视野升到 3 级（2250）把心跳铺得整个地图都是。
        /// </summary>
        [ConfigField(BlackVisionLevel2Range,
            "提醒范围硬上限（世界单位）。提醒范围 = min(音效可听范围, 本上限)；"
            + "默认 1800 = 黑方升到 2 级视野后的地图视野范围（AoiCulling.ExitRange 默认 900 × (1 + 0.5×2)）。"
            + "0 = 静音（不发心跳，等于关掉预警）；负数 = 不设上限（跟随 视野 × RangeMul）。",
            Min = -1f, Max = 5000f)]
        public static ConfigEntry<float> MaxRange;

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

        /// <summary>上一 tick 实际用的提醒范围，仅用于"范围变了就写一条日志"（配置热改时才有变化）。</summary>
        private static float _lastRange = float.NaN;

        /// <summary>上一次写过的说明，用来给每 tick 都会算出来的日志去重（同一条只写一次）。</summary>
        private static string _lastNote;

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

                // ① 不会播放声音 ⇒ 不产生提醒（旧版在这里仍会继续走判据、发回声/背景音、记账）
                if (IsSilent(SignalSfx?.Value))
                {
                    Note("主拍音效为空或 none ⇒ 不会播放声音，不产生提醒。");
                    return;
                }

                // 基准范围 = 黑方地图视野（热更新：每 tick 现读）
                float sight = AoiCullingFeature.ExitRange?.Value ?? 900f;
                if (!(sight > 0f))
                {
                    // 视野配置非正数时"音效范围"无从谈起 —— 退回原版默认，别把预警静默搞死
                    Note($"黑方视野为 {sight:F0}（≤0），音效范围退回原版默认 {VanillaSfxRange:F0}。");
                    sight = VanillaSfxRange;
                }

                // ② 音效可听范围（会原样写进包的 MaxDistance）；拿不到就退回原版默认 896
                float sfxRange = sight * (RangeMul?.Value ?? 1.4f);
                if (!(sfxRange > 0f) || float.IsNaN(sfxRange) || float.IsInfinity(sfxRange))
                {
                    Note($"音效范围算不出来（视野 {sight:F0} × 外圈倍率）⇒ 退回原版默认 {VanillaSfxRange:F0}。");
                    sfxRange = VanillaSfxRange;
                }

                // ③ 提醒范围 = min(音效可听范围, 上限)。0 = 静音；负数 = 不设上限。
                float cap = MaxRange?.Value ?? BlackVisionLevel2Range;
                float alertRange = cap == 0f
                    ? 0f
                    : (cap < 0f ? sfxRange : global::System.Math.Min(sfxRange, cap));

                if (!(alertRange > 0f))
                {
                    Note("提醒范围上限为 0 ⇒ 范围为 0，不产生提醒。");
                    return;
                }
                if (cap > 0f && cap < sfxRange)
                {
                    Note($"音效范围 {sfxRange:F0} 超过上限 {cap:F0}，已封顶为 {alertRange:F0}"
                        + "（包里的 MaxDistance 同步收窄，收包者一定听得见）。");
                }
                if (float.IsNaN(_lastRange) || global::System.Math.Abs(alertRange - _lastRange) > 0.5f)
                {
                    _lastRange = alertRange;
                    Plugin.Log.LogInfo(
                        $"[HS] ProximityAlert：提醒范围 = {alertRange:F0}（音效可听范围 {sfxRange:F0}，"
                        + $"上限 {(cap < 0f ? "不限" : cap.ToString("F0"))}，黑方视野 {sight:F0}）。");
                }

                _tick++;

                float alert2 = alertRange * alertRange;

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

                    // 约束 2：**音效听不到的地方不发** —— 判据就是这个音效的可听范围
                    //（不是"我们打算把包发给谁"：定向发送时两者本来可以不一致）
                    if (bestPos == null || best > alert2)
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
                            Beat(__instance, p, bestPos, alertRange);
                        else
                        {
                            // 第二拍排到 500ms 后 —— 1 Hz 的 tick 靠这个做出"2 Hz"
                            var who = p;
                            var at = bestPos;
                            float d = alertRange;
                            __instance.PushAfter(500, delegate { Beat(__instance, who, at, d); });
                        }
                    }
                }
            }
        }

        /// <summary>名字为空或 `none` = 这一发不会播放声音（`SendOne` 里用的是同一个判据）。</summary>
        private static bool IsSilent(string sfxName)
            => string.IsNullOrEmpty(sfxName)
               || sfxName.Equals("none", global::System.StringComparison.OrdinalIgnoreCase);

        /// <summary>关键节点日志：同一条只写一次（本方法在每 tick 的路径上，不能无条件写）。</summary>
        private static void Note(string message)
        {
            if (message == _lastNote)
                return;
            _lastNote = message;
            Plugin.Log.LogInfo("[HS] ProximityAlert：" + message);
        }

        /// <summary>
        /// 发一次心跳（主拍 + 延迟回声 + 偶尔背景音），全部只发给该白方。
        /// <paramref name="maxDistance"/> 既是"谁能听到"的判据，也是写进包的 `MaxDistance`。
        /// </summary>
        private static void Beat(GameRoom room, GamePlayer target, PosInfo sourcePos, float maxDistance)
        {
            if (target?.Session == null || sourcePos == null)
                return;

            _beats++;

            SendOne(target, SignalSfx?.Value, sourcePos, maxDistance);

            string echo = EchoSfx?.Value;
            if (!IsSilent(echo))
            {
                var who = target;
                var at = sourcePos;
                float d = maxDistance;
                float delay = EchoDelayMs?.Value ?? 180f;
                // 回声推迟一点，形成"咚…哒"的双拍
                room.PushAfter((int)delay, delegate { SendOne(who, echo, at, d); });
            }

            int every = BgEveryBeats?.Value ?? 0;
            string bg = BgSfx?.Value;
            if (every > 0 && !IsSilent(bg) && _beats % every == 0)
            {
                SendOne(target, bg, sourcePos, maxDistance);
            }
        }

        /// <summary>
        /// 把某个音效**只**发给一个人（带位置 ⇒ 客户端会算左右方位与距离衰减）。
        /// <paramref name="maxDistance"/> 写进包 ⇒ 客户端 `AudioSource.maxDistance` ⇒ 就是可听范围。
        /// </summary>
        private static void SendOne(GamePlayer target, string sfxName, PosInfo pos, float maxDistance)
        {
            if (IsSilent(sfxName) || target?.Session == null)
                return;
            if (!global::System.Enum.TryParse(sfxName, true, out ESoundType sfx))
            {
                Plugin.Log.LogWarning($"[HS] ProximityAlert：未知音效名 {sfxName}");
                return;
            }
            try
            {
                GameRoom.Instance.SendWorldSFX(target, sfx, pos, maxDistance);
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
                _lastRange = float.NaN;
                _lastNote = null;
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
                _lastRange = float.NaN;
                _lastNote = null;
            }
        }
    }
}
