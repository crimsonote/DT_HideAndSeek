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
    /// 白方全图雷达：开启后，存活白方的小地图上会显示**场上所有存活玩家**的位置。
    ///
    /// 通道是协议 S_PIN_MOVE（1018）——服务端已有公开原语可直接用：
    ///     Player.SendTraceTarget(Player target, bool isForce, bool isRemove = false)   (:175923)
    /// 客户端 Handle_S_PIN_MOVE（:42242）→ RefreshComplyRulesPin（:74731）→ TurnComplyRules（:85961），
    /// **全程不看 Player.Color、不看 KnownBlackIds**，贴图恒为 CharData.Type + "_Map_Black.sprite"，
    /// 所以所有人外观同构 —— "不区分阵营"是严格成立的，不是近似。
    ///
    /// 为什么不用其它通道：
    ///   · RefreshPlayerPin 的玩家 pin 被客户端早退挡死（UI_GameScene.LateUpdate :74673
    ///     `if (Color == White) return;`），房主端无法绕过
    ///   · AddPlayer 放开 AOI 只决定"模型是否 spawn"，pin 渲染仍被同一处早退挡住
    ///   · S_NOTIFY_ARROW 是屏幕边缘箭头而非地图 pin，且移除要求坐标精确相等，目标一动就撤不掉
    ///
    /// 两个必须守住的边界：
    ///   1) **只在 Survive 下发**：GetSceneUI&lt;UI_GameScene&gt;() 是 as 转换，审判阶段返回 null → NRE
    ///   2) **阵亡瞬间必须立即撤 pin**：白方 OnDead（:175968）会收到全量 S_NOTIFY_BLACK，
    ///      RefreshBlackPin 命中同 id 的 pin 后会 TurnBlack —— 那就把阵营泄露了
    /// </summary>
    [PatchFeature(
        section: "WhiteRadar",
        description: "白方全图雷达：白方小地图显示所有存活玩家位置（不区分阵营，仅 Survive 阶段）。",
        defaultEnabled: true,
        side: FeatureSide.Host)]
    internal static class WhiteRadarFeature
    {
        // 字段名不能叫 Enabled —— 会与 PatchLoader 生成的段级 Enabled 冲突
        [ConfigField(false, "是否启用白方全图雷达。可用控制台 hs_radar on|off 或规则引擎的 Radar 动作改写。")]
        public static ConfigEntry<bool> RadarOn;

        [ConfigField(0, "自动关闭的秒数（0 = 一直开启）。用于做「短暂的情报窗口」。", Min = 0f, Max = 600f)]
        public static ConfigEntry<int> DurationSeconds;

        [ConfigField(true, "对持有 Kaho 监视（ComplyRules）技能的白方跳过 —— 那套 pin 与技能箭头复用同一 id，会互相破坏。")]
        public static ConfigEntry<bool> SkipComplyRulesHolders;

        [ConfigField(true, "跳过假人（它们不会动，标出来也没意义）。")]
        public static ConfigEntry<bool> SkipDummies;

        private static float _endAt;

        internal static bool IsActive => RadarOn != null && RadarOn.Value;

        /// <summary>开关雷达。开启时按配置计算自动关闭时间。</summary>
        internal static void SetActive(bool on)
        {
            if (RadarOn == null)
                return;

            RadarOn.Value = on;

            int seconds = DurationSeconds?.Value ?? 0;
            _endAt = (on && seconds > 0)
                ? (TimeManager.Instance?.SurviveTime ?? 0f) + seconds
                : 0f;

            if (!on)
                ClearAllPins();

            Plugin.Log.LogInfo(on
                ? $"[HS] WhiteRadar：已开启{(seconds > 0 ? $"（{seconds} 秒后自动关闭）" : "")}。"
                : "[HS] WhiteRadar：已关闭。");
        }

        /// <summary>撤掉所有白方身上的追踪 pin。</summary>
        internal static void ClearAllPins()
        {
            var room = GameRoom.Instance;
            if (room?.Players == null)
                return;

            foreach (var white in room.Players)
            {
                if (white?.PublicInfo == null || white.Color != EPlayerColor.White)
                    continue;

                foreach (var other in room.Players)
                {
                    if (other?.PublicInfo == null)
                        continue;
                    white.SendTraceTarget(other, isForce: false, isRemove: true);
                }
            }
        }

        /// <summary>每秒重推。重推而非一次性建立，是为了自愈（pin 会随 Despawn/迁移丢失）。</summary>
        [HarmonyPatch(typeof(GameRoom), "SurvivalTick")]
        internal static class TickHook
        {
            [HarmonyPostfix]
            private static void Postfix(GameRoom __instance)
            {
                if (ModeRuntime.Bypass || __instance == null)
                    return;
                if (!IsActive)
                    return;

                // 审判/大厅阶段下发会 NRE（见类注释），必须停手并清干净
                if (__instance.State != EGameState.Survive)
                {
                    ClearAllPins();
                    return;
                }

                if (_endAt > 0f && (TimeManager.Instance?.SurviveTime ?? 0f) >= _endAt)
                {
                    SetActive(false);
                    return;
                }

                Filter = __instance;
                Refresh(__instance);
            }

            private static GameRoom Filter;

            private static void Refresh(GameRoom room)
            {
                bool skipKaho = SkipComplyRulesHolders?.Value ?? true;
                bool skipDummy = SkipDummies?.Value ?? true;

                foreach (var white in room.Players)
                {
                    if (white?.PublicInfo == null)
                        continue;
                    if (white.Color != EPlayerColor.White || !white.IsAlive || white.IsSpectator)
                        continue;
                    if (skipKaho && HasComplyRules(white))
                        continue;                        // Kaho 的监视复用同一套 pin，跳过以免互踩

                    foreach (var other in room.Players)
                    {
                        if (other?.PublicInfo == null)
                            continue;

                        // 死人/躲藏/旁观必须**撤销**而不是跳过：
                        // 尤其是阵亡瞬间 —— 他会收到 S_NOTIFY_BLACK，若 pin 还在就会被染成黑点
                        bool visible = other.IsAlive
                            && other.State != EPlayerState.Hide
                            && !other.IsSpectator
                            && !(skipDummy && other.IsDummy);

                        if (visible)
                            white.SendTraceTarget(other, isForce: false);
                        else
                            white.SendTraceTarget(other, isForce: false, isRemove: true);
                    }
                }
            }
        }

        /// <summary>是否持有 Kaho 的监视技能（ComplyRules）。</summary>
        private static bool HasComplyRules(GamePlayer player)
        {
            try
            {
                return player?.SkillComponent?.Data?.Type == ESkillType.ComplyRules;
            }
            catch
            {
                return false;
            }
        }

        // ── 生命周期：离开对局时清干净 ────────────────────────────────
        [HarmonyPatch(typeof(GameRoom), "StartLobby")]
        internal static class LobbyHook
        {
            [HarmonyPostfix]
            private static void Postfix()
            {
                if (IsActive)
                    ClearAllPins();
            }
        }
    }
}
