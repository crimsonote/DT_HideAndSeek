﻿using BepInEx.Configuration;
using HarmonyLib;
using Protocol;
using Server.Game;
using HideAndSeek.Core;
using GamePlayer = Server.Game.Player;

namespace HideAndSeek.Features.Combat
{
    /// <summary>
    /// 死亡流程兜底：原版 <c>Player.OnDead</c> 半路抛异常时，补完被跳过的尾部，杜绝「半死」。
    ///
    /// 病因（2026-10-15 实机定位：Player.log 76455-76464 完整堆栈）：
    ///   原版 OnDead（ACS:175968-176005）是一条**没有 try/catch 的直线**：
    ///     IsAlive=false → DeadPlayers.Insert → Session.Send(S_DEAD)（受害者本机就此黑屏）→
    ///     通报黑方 → BroadcastFinalSnapshot → switch{ Murder → OnDeadMurder(建尸体+击杀记账) } →
    ///     **ExitPlayer()**。
    ///   异常只被最外层的 JobTimer.Flush（ACS:173246-253）接住：它只 Debug.LogError 后继续，
    ///   **已半应用的状态没人收拾** ⇒ S_DEAD 已发（白方觉得自己被杀死）、尸体没建（没有尸体）、
    ///   ExitPlayer 没跑（人还在 AlivePlayers ⇒ 系统按活人结算；没进幽灵可见性 ⇒ 其他幽灵看到活体；
    ///   击杀不给学分/加时、回放不计数）。这就是线上那次事故的全部症状。
    ///
    /// 方案：Finalizer 兜底 —— 确认「死到一半」（IsAlive==false 且已在 DeadPlayers）后，
    /// 补完尾部：直接调原版 public 的 <c>ExitPlayer()</c>（:176092，含 AlivePlayers.Remove +
    /// EnterGhostVisibility + 换速广播），吞掉原异常并落日志。健康死亡路径零改动
    /// （__exception == null 立即返回）。尸体缺失由姊妹功能 [AreaGuard] 从源头兜住。
    /// </summary>
    [PatchFeature(
        section: "DeathGuard",
        description: "死亡流程兜底：原版 OnDead 是一条无 try/catch 的直线，尸体创建等步骤一旦抛异常就会被拦腰截断（S_DEAD 已发、人却还在 AlivePlayers、无尸体、不给学分——即「半死」）。本功能只在异常路径上补完 ExitPlayer 尾部并吞掉异常，健康死亡零改动。",
        defaultEnabled: true,
        side: FeatureSide.Host)]
    internal static class DeathGuardFeature
    {
        /// <summary>运行期热开关。段开关 [DeathGuard].Enabled 决定补丁挂不挂（改它要重启游戏）。</summary>
        [ConfigField(true, "运行期热开关（不需要重启游戏）：关掉即完全恢复原版行为（半死照旧发生，只留 JobTimer 的一行日志）。段开关 [DeathGuard].Enabled 只决定补丁挂不挂。")]
        public static ConfigEntry<bool> Active;

        /// <summary>
        /// OnDead 全程 public 且无重载（IL 已复核），用 nameof 定位（与 ReplayFeature.KillHook 同款写法）。
        /// 本模块已有两个 OnDead 钩子（GhostPhaseRefresh 的 Prefix、ReplayFeature 的 Postfix）——
        /// Finalizer 与它们不冲突：异常发生时 Postfix 本来就不会跑，Finalizer 照常执行。
        /// </summary>
        [HarmonyPatch(typeof(GamePlayer), nameof(GamePlayer.OnDead))]
        internal static class OnDeadHook
        {
            [HarmonyFinalizer]
            private static global::System.Exception Finalizer(GamePlayer __instance, GamePlayer black, EDeathType type, global::System.Exception __exception)
            {
                Diagnostics.Hit("DeathGuard");
                if (__exception == null) return null;                  // 健康死亡：不插手
                if (ModeRuntime.Bypass) return __exception;            // 模式关闭 = 纯原版
                if (Active == null || !Active.Value) return __exception;

                var room = GameRoom.Instance;
                if (__instance == null || room == null) return __exception;
                if (__instance.IsAlive) return __exception;            // 没到置死那一步（异常更早抛出），不接管
                if (room.DeadPlayers == null || !room.DeadPlayers.Contains(__instance))
                    return __exception;                                // 死讯未登记，不接管

                try
                {
                    Plugin.Log.LogWarning(
                        $"[HS] DeathGuard：OnDead 半路抛异常，补完死亡尾部 — pid={__instance.PublicInfo?.PlayerId} " +
                        $"name={__instance.Name} type={type}，异常={__exception}");
                    if (room.AlivePlayers != null && room.AlivePlayers.Contains(__instance))
                        __instance.ExitPlayer();                       // public：AlivePlayers.Remove + 幽灵可见性
                }
                catch (global::System.Exception ex)
                {
                    Plugin.Log.LogWarning($"[HS] DeathGuard：补尾自身失败 — {ex}");
                }
                return null;                                           // 半死已修复，吞掉原异常
            }
        }
    }
}
