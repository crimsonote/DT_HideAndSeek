using System.Collections.Generic;
using BepInEx.Configuration;
using HarmonyLib;
using Protocol;
using Server.Game;
using HideAndSeek.Core;
using GamePlayer = Server.Game.Player;

namespace HideAndSeek.Features.Skill
{
    /// <summary>
    /// 莲（灵魂感知）：**每感知到一次死亡，获得一段时间的移速加成**。
    ///
    /// 原版机制（Assembly-CSharp:176080-176090）：有人死时 `SendDeadNotify` 会给**所有活着的**
    /// `ESkillType.SoulSense` 玩家发一个 `S_NOTIFY_DEAD` 包（客户端据此放蜡烛图标）。
    /// 我们不去改技能本身，只在那条通知上"搭一层"：谁收到了通知，就给他一段加速。
    ///
    /// 与其它加速的关系：**相乘叠加**（黑方基础倍率、汽水都各自乘过一次）。
    /// 乘法可交换，所以本补丁可以独立挂 `RefreshSpeed` 的 Postfix，不必并进 SpeedBoostFeature。
    ///
    /// ⚠ 速度必须挂在 `BuffComponent.RefreshSpeed` 的 **Postfix**：
    /// 原方法会用 `560 * delta` 重算速度，Prefix 里改会被覆盖（SpeedBoostFeature 的注释里记着这条）。
    /// ⚠ 幽灵要跳过：`MakeSpectatorGhost` 会置 `State = Hide` 并直接设速度，掺和进去会污染观战速度。
    /// </summary>
    [PatchFeature("SoulSense",
        "莲（灵魂感知）：每感知到一次死亡，获得一段时间（默认 30 秒）的移速加成（默认 +50%）。",
        defaultEnabled: false, side: FeatureSide.Host)]
    internal static class SoulSenseSpeedFeature
    {
        [ConfigField(30f, "感知死亡后的加速时长（秒）。", Min = 1f, Max = 300f)]
        public static ConfigEntry<float> BoostSeconds;

        [ConfigField(1.5f, "感知死亡后的移速倍率。1.5 = 快 50%。", Min = 1f, Max = 3f)]
        public static ConfigEntry<float> SpeedMul;

        /// <summary>PlayerId → 加速到期时刻（取 TimeManager.SurviveTime）。</summary>
        private static readonly Dictionary<int, float> Until = new Dictionary<int, float>();

        private static float Now => TimeManager.Instance?.SurviveTime ?? 0f;

        /// <summary>这名玩家此刻是否拥有「灵魂感知」。与原版 :176085 用同一个判据。</summary>
        private static bool HasSoulSense(GamePlayer p)
        {
            try
            {
                return p?.SkillComponent?.Data != null
                    && p.SkillComponent.Data.Type == ESkillType.SoulSense;
            }
            catch { return false; }
        }

        // ── ① 触发：原版给莲发"有人死了"的那一刻 ──
        // SendDeadNotify 是 Player 的 private 方法（Assembly-CSharp:176080），用字符串定位。
        [HarmonyPatch(typeof(GamePlayer), "SendDeadNotify")]
        internal static class DeadNotifyHook
        {
            [HarmonyPostfix]
            private static void Postfix()
            {
                Diagnostics.Hit("SoulSense");
                if (ModeRuntime.Bypass)
                    return;

                var room = GameRoom.Instance;
                if (room?.AlivePlayers == null)
                    return;

                float now = Now;
                float secs = BoostSeconds?.Value ?? 30f;

                // 只给**活着**的莲续期（与 SendDeadNotify 的发送条件一致：它也只发给 AlivePlayers）
                for (int i = 0; i < room.AlivePlayers.Count; i++)
                {
                    var p = room.AlivePlayers[i];
                    if (p?.PublicInfo == null || !HasSoulSense(p))
                        continue;

                    // 重复触发 ⇒ **刷新时长，不叠乘**（一波团灭时最多保持"一直有加速"，
                    // 而不是把倍率越乘越高）。
                    Until[p.PublicInfo.PlayerId] = now + secs;
                }
            }
        }

        // ── ② 加速：与 SpeedBoostFeature 同一挂点（Postfix），乘法叠加 ──
        [HarmonyPatch(typeof(BuffComponent), nameof(BuffComponent.RefreshSpeed))]
        internal static class RefreshSpeedHook
        {
            [HarmonyPostfix]
            private static void Postfix(BuffComponent __instance)
            {
                if (ModeRuntime.Bypass)
                    return;

                float mul = SpeedMul?.Value ?? 1.5f;
                if (global::System.Math.Abs(mul - 1f) < 0.001f)
                    return;                       // 1.0 不动，省一次广播

                var player = __instance?.Owner;
                if (player == null || !player.IsAlive || player.State == EPlayerState.Hide)
                    return;                       // 幽灵：原版直接设速度，别掺和

                int pid = player.PublicInfo?.PlayerId ?? 0;
                if (pid == 0 || !Until.TryGetValue(pid, out float until) || Now >= until)
                    return;                       // 不在加速期内

                player.PrivateInfo.Speed *= mul;
                player.SendChangeSpeed();
            }
        }

        // ── ③ 到期兜底：加速只在 RefreshSpeed 的 Postfix 里生效，而那个方法
        //        只在"速度需要重算"（移动/状态变化）时才被游戏调用。
        //        ⇒ 到期时若玩家站着不动，Speed 里那个倍率会一直挂着不恢复。
        //        所以每 1 秒检查一次"刚刚过期的人"，主动让他重算一次速度。
        private static readonly HashSet<int> Boosted = new HashSet<int>();

        [HarmonyPatch(typeof(GameRoom), "SurvivalTick")]
        internal static class TickHook
        {
            [HarmonyPostfix]
            private static void Postfix()
            {
                if (ModeRuntime.Bypass || Until.Count == 0)
                    return;

                var room = GameRoom.Instance;
                if (room?.Players == null)
                    return;

                float now = Now;
                for (int i = 0; i < room.Players.Count; i++)
                {
                    var p = room.Players[i];
                    if (p?.PublicInfo == null)
                        continue;

                    int pid = p.PublicInfo.PlayerId;
                    if (!Until.TryGetValue(pid, out float until))
                        continue;
                    if (now < until)
                    {
                        Boosted.Add(pid);
                        continue;                     // 还在加速期内
                    }

                    // 刚过期：清记录 + 主动重算一次速度（否则站着不动会一直挂着加成）
                    Until.Remove(pid);
                    if (Boosted.Remove(pid) && p.IsAlive && p.State != EPlayerState.Hide)
                    {
                        try { p.BuffComponent?.RefreshSpeed(); }
                        catch (global::System.Exception ex)
                        {
                            Plugin.Log.LogWarning($"[HS] SoulSense：恢复速度失败 — {ex.Message}");
                        }
                    }
                }
            }
        }

        // ── ④ 跨局清理（与同类功能的时机一致）──
        [HarmonyPatch(typeof(GameRoom), "StartSurvive")]
        internal static class StartHook
        {
            [HarmonyPostfix]
            private static void Postfix() => Clear();
        }

        [HarmonyPatch(typeof(GameRoom), "StartLobby")]
        internal static class LobbyHook
        {
            [HarmonyPostfix]
            private static void Postfix() => Clear();
        }

        private static void Clear()
        {
            // 到期时刻记的是 SurviveTime，而它每局被 ResetSurvival() 设回 420（不是从 0）——
            // 不清的话上一局的时刻在新局会算出负数，加速永不过期（这一类坑今天已踩过四次）。
            Until.Clear();
            Boosted.Clear();
        }
    }
}
