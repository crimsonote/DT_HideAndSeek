using System;
using System.Reflection;
using HarmonyLib;
using Protocol;
using Server.Game;
using HideAndSeek.Core;
using GamePlayer = Server.Game.Player;

namespace HideAndSeek.Features.Rule
{
    /// <summary>
    /// 【结局裁定】—— 一局之内，结局**只裁定一次**。
    ///
    /// 为什么需要它（实机 bug）：胜负判定散在好几处，各自"判完就执行"——
    ///   · `BlackWinFeature.TryTrigger`（挂 `OnDead` / `RuleBreaker` / 每秒 `SurvivalTick`）
    ///   · `WhiteWinFeature.Prefix`（每秒 `SurvivalTick`：倒计时归零 ⇒ 白胜 / 进度不足 ⇒ 黑胜）
    ///   · `CorpseReportFeature`（尸体报告 ⇒ 走 `WhiteWinFeature.TriggerWhiteWin`）
    /// 于是**多个条件可以同时成立**，而每一次成立都会**立刻执行一遍副作用**
    /// （`ResultType` / `ApplyTeamResults` / `S_ENDING_CAMERA` / `AlertMessage`）。
    /// 实测后果（房主口径）：全敲之后原版要等 7.5 秒才进结算，**若这 7.5 秒内倒计时归零**，
    /// 白胜那条会当场执行 ⇒ 覆盖结果类型、并广播 `S_ENDING_CAMERA{IsEnd=true}` ⇒ **卡掉黑方回放**。
    ///
    /// 做法：**判定与执行分离**。
    ///   · 判定照旧每秒跑（很便宜），但每个"执行入口"先 <see cref="Claim"/> 一次；
    ///   · 只有第一个认领者继续执行，其余**什么都不做**（记一条日志）；
    ///   · 每局 `StartSurvive` 复位。
    ///
    /// 另外处理**黑胜的两类**（这是原版 `GameOver` 的一处浪费）：
    ///   原版 `GameOver` 对 `AlivePlayers` 里**非黑非暗**者逐个点项圈（`OnDeadCollarBomb`），
    ///   然后**无条件** `PushAfter(7500, …TotalResult)`。可是：
    ///     · **A 类**（有人可处决：露娜幸存 / "黑方任务"判黑胜）⇒ 点了项圈 ⇒ 6 秒爆炸演出
    ///       ⇒ 这 7.5 秒**有内容可录** ⇒ 照旧（回放要自爆幕与残留）；
    ///     · **B 类**（黑方直接杀光 ⇒ 一个可处决者都没有）⇒ 循环一次都不跑 ⇒
    ///       自爆幕与"最后时段"幕都**不会生成**（见 `ReplayFeature.RegisterSettlementActs`）
    ///       ⇒ 那 7.5 秒**没有任何演出、也不丢任何画面** ⇒ **立即推进**即可。
    /// </summary>
    [PatchFeature(
        section: "EndingRule",
        description: "结局裁定：一局只裁定一次（倒计时/通杀/任务多条件同时成立时不再重复结算）；" +
            "黑胜且无人可处决时跳过原版 7.5 秒空等，立即进入回放/结算。",
        defaultEnabled: true,
        side: FeatureSide.Host)]
    internal static class EndingRuleFeature
    {
        // ── 裁定状态（一局一份，`StartSurvive` 复位）────────────────────────

        private static bool _decided;
        private static string _kind = "";
        private static float _at;

        /// <summary>本局是否已裁定过结局。</summary>
        internal static bool Decided => _decided;

        /// <summary>已裁定的结局（日志/诊断用）。</summary>
        internal static string Kind => _kind;

        /// <summary>
        /// 认领"由我执行这次结局"。
        /// 返回 <c>true</c> = 本次认领成功（调用方继续执行结算）；
        /// 返回 <c>false</c> = 本局已由别的条件裁定（**调用方必须什么都不做**）。
        /// </summary>
        internal static bool Claim(string kind, string reason)
        {
            if (_decided)
            {
                Plugin.Log.LogInfo($"[HS] 结局已由「{_kind}」裁定（{_at:F1}s）⇒ 忽略「{kind}」（{reason}）。");
                return false;
            }

            _decided = true;
            _kind = kind;
            _at = UnityEngine.Time.realtimeSinceStartup;
            Plugin.Log.LogInfo($"[HS] 结局裁定：**{kind}**（{reason}）。");
            return true;
        }

        /// <summary>
        /// 本次黑胜有没有"可处决者" —— 判据与原版 `GameOver` 的循环完全一致：
        /// `AlivePlayers` 里存在既不是黑方也不是黑幕、且不是旁观者的人。
        /// </summary>
        private static bool HasExecutable(GameRoom room)
        {
            if (room == null)
                return false;

            foreach (var p in room.AlivePlayers)
            {
                if (p?.PublicInfo == null || p.IsSpectator)
                    continue;
                if (p.Color == EPlayerColor.Black || p.Color == EPlayerColor.Dark)
                    continue;
                return true;
            }
            return false;
        }

        /// <summary>每局开始复位裁定状态。</summary>
        [HarmonyPatch(typeof(GameRoom), "StartSurvive")]
        internal static class RoundResetHook
        {
            [HarmonyPostfix]
            private static void Postfix()
            {
                _decided = false;
                _kind = "";
                _at = 0f;
            }
        }

        /// <summary>
        /// 黑胜：**无可处决者**时跳过原版那 7.5 秒（那段时间没有任何演出，也不丢画面）。
        /// 有可处决者（A 类）⇒ 放行原版，一行不改（自爆幕需要那 6 秒）。
        /// </summary>
        [HarmonyPatch(typeof(GameRoom), "GameOver")]
        internal static class FastBlackWinHook
        {
            [HarmonyPrefix]
            private static bool Prefix(GameRoom __instance)
            {
                if (ModeRuntime.Bypass)
                    return true;
                if (HasExecutable(__instance))
                    return true;                       // A 类：交给原版（含项圈自爆与 7.5 秒）

                // B 类：照抄原版 GameOver 的等价收尾，但**去掉那 7.5 秒**。
                //   · 循环体为空（没有可处决者）⇒ 不需要 S_STOP_CONTROL / OnDeadCollarBomb
                //   · `PrimaryWinnerId` 原版取 `MasterMind?.PublicInfo.PlayerId ?? 0`；
                //     本模块自己的 `TriggerBlackWin` 手写分支也不设它 ⇒ 这里同样不设（保持一致）
                //   · `ChangeGameState(TotalResult)` 是**结算/回放的唯一入口**：
                //     回放开 ⇒ 被 `ReplayFeature.SettleHook` 拦下进回放；回放关 ⇒ 直接结算 ✓
                try
                {
                    __instance.AlertMessageAllPlayers(ESystemMessageType.EndClass);
                    __instance.ResultType = EResultType.BlackWin;
                    __instance.ApplyTeamResults();
                    __instance.ChangeGameState(EGameState.TotalResult);

                    Plugin.Log.LogInfo("[HS] 黑胜（无可处决者）：跳过原版 7.5 秒空等，立即推进结算/回放。");
                    return false;
                }
                catch (Exception ex)
                {
                    // 任何异常都退回原版：结算绝不能卡住。
                    Plugin.Log.LogWarning($"[HS] 黑胜快速收尾失败，退回原版 GameOver — {ex.Message}");
                    return true;
                }
            }
        }
    }
}
