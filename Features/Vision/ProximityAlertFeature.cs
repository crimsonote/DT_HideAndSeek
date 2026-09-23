using System.Collections.Generic;
using BepInEx.Configuration;
using HideAndSeek.Core;
using HarmonyLib;
using Protocol;
using Server.Game;
using UnityEngine;
using GamePlayer = Server.Game.Player;

namespace HideAndSeek.Features.Vision
{
    /// <summary>
    /// 黑方接近时给白方一个听觉提示（心跳感）。
    ///
    /// 为什么需要：AOI 裁剪让白方完全看不到黑方（内圈 700），而地图很大，
    /// 最后一名白方被"找不到就输"的焦虑和"突然被砍"的挫败同时夹击。
    /// 给一个纯听觉的接近预警，既不暴露黑方的方向与距离，又让白方有机会反应。
    ///
    /// 实现可行性：完全在服务端 —— SendSystemSFX(ESoundType, Player) 只发给该玩家，
    /// 不需要任何客户端改动（对比"屏幕心跳 UI"就必须改客户端，做不到）。
    ///
    /// 节流：每个白方独立计时，避免站在黑方旁边时音效刷屏。
    /// </summary>
    [PatchFeature("ProximityAlert", "黑方靠近时给白方播放警示音（默认关闭，需要时再开）。",
        defaultEnabled: false, side: FeatureSide.Host)]
    internal static class ProximityAlertFeature
    {
        [ConfigField(false, "启用接近预警。默认关闭。")]
        public static ConfigEntry<bool> AlertEnabled;

        [ConfigField(300f, "触发距离。黑方进入该距离后，向其附近的白方播放警示音。",
            Min = 0f, Max = 2000f)]
        public static ConfigEntry<float> AlertRange;

        [ConfigField(3f, "同一名白方两次预警之间的最短间隔（秒），防止刷屏。",
            Min = 0.5f, Max = 60f)]
        public static ConfigEntry<float> AlertIntervalSeconds;

        [ConfigField("none", "预警音效（ESoundType 名）。none = 不播放。可选 WarningSfx / HandBellSfx / ElevatorSfx。")]
        public static ConfigEntry<string> AlertSfx;

        /// <summary>每名白方上一次收到预警的时间。</summary>
        private static readonly Dictionary<int, float> LastAlertAt = new Dictionary<int, float>();

        [HarmonyPatch(typeof(GameRoom), "SurvivalTick")]
        internal static class TickHook
        {
            [HarmonyPostfix]
            private static void Postfix(GameRoom __instance)
            {
                if (ModeRuntime.Bypass || __instance == null)
                    return;
                if (AlertEnabled == null || !AlertEnabled.Value)
                    return;
                if (__instance.State != EGameState.Survive)
                    return;

                float now = TimeManager.Instance?.SurviveTime ?? 0f;
                float range = AlertRange?.Value ?? 300f;
                float interval = AlertIntervalSeconds?.Value ?? 3f;

                // none / 空 → 不播放；名字写错才回退到 WarningSfx（并留下日志便于排查）
                string sfxName = AlertSfx?.Value;
                bool silent = string.IsNullOrEmpty(sfxName)
                    || sfxName.Equals("none", global::System.StringComparison.OrdinalIgnoreCase);
                if (silent)
                    return;

                ESoundType sfx;
                if (!global::System.Enum.TryParse(sfxName, true, out sfx))
                {
                    Plugin.Log.LogWarning($"[HS] ProximityAlert：未知音效名 {sfxName}，本次回退 WarningSfx。");
                    sfx = ESoundType.WarningSfx;
                }

                var players = __instance.Players;

                // 先收集存活的黑方，避免在双层循环里重复判断
                var blacks = new List<GamePlayer>();
                for (int i = 0; i < players.Count; i++)
                {
                    var p = players[i];
                    if (p?.PublicInfo == null || !p.IsAlive || p.IsSpectator)
                        continue;
                    if (p.Color == EPlayerColor.Black)
                        blacks.Add(p);
                }

                if (blacks.Count == 0)
                    return;

                float rangeSq = range * range;

                for (int i = 0; i < players.Count; i++)
                {
                    var white = players[i];
                    if (white?.PublicInfo == null || !white.IsAlive || white.IsSpectator)
                        continue;
                    if (white.Color != EPlayerColor.White)
                        continue;

                    int pid = white.PublicInfo.PlayerId;

                    // 是否已有一名黑方进入范围内
                    bool near = false;
                    for (int j = 0; j < blacks.Count; j++)
                    {
                        float d = Util.CalculateDistanceSquared(white.PublicInfo.Pos, blacks[j].PublicInfo.Pos);
                        if (d <= rangeSq)
                        {
                            near = true;
                            break;
                        }
                    }

                    if (!near)
                    {
                        LastAlertAt.Remove(pid);       // 脱离后允许下次进入立刻提示
                        continue;
                    }

                    if (LastAlertAt.TryGetValue(pid, out float last) && now - last < interval)
                        continue;

                    LastAlertAt[pid] = now;
                    try
                    {
                        __instance.SendSystemSFX(sfx, white);
                        Plugin.Log.LogInfo($"[HS] ProximityAlert：警告 #{pid}（黑方已进入 {range:F0}）。");
                    }
                    catch (global::System.Exception ex)
                    {
                        Plugin.Log.LogWarning($"[HS] ProximityAlert：播放失败 — {ex.Message}");
                    }
                }
            }
        }

        [HarmonyPatch(typeof(GameRoom), "StartLobby")]
        internal static class LobbyHook
        {
            [HarmonyPostfix]
            private static void Postfix() => LastAlertAt.Clear();
        }

        [HarmonyPatch(typeof(GameRoom), "StartDetective")]
        internal static class DetectiveHook
        {
            [HarmonyPostfix]
            private static void Postfix() => LastAlertAt.Clear();
        }
    }
}
