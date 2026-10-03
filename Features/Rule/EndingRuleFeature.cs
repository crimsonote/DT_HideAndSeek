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
    /// 【暂缓项】黑胜的"等待时长"优化（原版 `GameOver` 无条件等 7.5 秒）**本轮不做**：
    ///   2026-10-03 曾试过"黑胜且无人可处决时立即推进"，**结果末幕（最后一次作案）缺画面** ✗ ——
    ///   原因：幕的窗口右端在事件之后（「杀人」幕 = 命中 + `KillAfterSec` 2.2s），
    ///   立刻取带时客户端还没录到"事件之后"那段。⇒ 先回退成**与原版/自爆同一条路**（都等 7.5 秒）。
    ///   将来要改，请走**缝②**：`JobSerializer.PushAfter<T1>(int, Action<T1>, T1)` 的 Prefix，
    ///   判据 `ChangeGameState` + `EGameState.TotalResult`（全程序集唯一命中 `GameOver`），
    ///   **只改 `tickAfter` 数字**，等待值 = "本局所有幕窗口都关闭所需" ⇒ **原版方法体一行不动** ✓
    /// </summary>
    [PatchFeature(
        section: "EndingRule",
        description: "结局裁定：一局只裁定一次（倒计时/通杀/任务多条件同时成立时不再重复结算）。",
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
    }
}
