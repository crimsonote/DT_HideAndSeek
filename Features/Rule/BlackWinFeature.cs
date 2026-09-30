using HarmonyLib;
using Protocol;
using Server.Game;
using HideAndSeek.Core;
using GamePlayer = Server.Game.Player;

namespace HideAndSeek.Features.Rule
{
    /// <summary>
    /// 非露娜系白方全部淘汰 → 黑方胜利。
    ///
    /// 判定挂在服务端 Player.OnDead（:175968）之后：出刀到真正死亡之间有 400ms 延迟
    /// （OnDamaged :175953 的 PushAfter），若挂在 UseWeapon 上会误判，必须挂在这里。
    ///
    /// ★ **只钩 OnDead 是不够的**（实机 bug）：还有一条"非露娜白方消失"的路径**没有任何人死亡** ——
    ///   场上白方只剩露娜 + 小偷（Soi）时，小偷偷到露娜能力（RuleBreaker 把技能换成 Catastrophe）
    ///   之后，按露娜系判据他自己也成了露娜系 ⇒ 黑方胜利条件**当场**成立，
    ///   但这条路径不产生任何 OnDead 事件 ⇒ 判定根本不跑；
    ///   而此刻小偷也有露娜护盾（LunaImmunity 按技能判据放行）⇒ 黑方砍不死他 ⇒ 一直拖到白方赢。
    ///   ⇒ 所以补两个触发点：技能被偷的那一刻（立即）+ SurvivalTick（每秒兜底）。
    ///
    /// 判据用 <see cref="GameRefs.IsLunaSide"/>（**含**"被偷到露娜技能的人"）—— 这是**对的**：
    ///   小偷既然拿到了露娜的能力，就该与露娜一同被留下，黑方杀光其他人才算赢。
    ///   （反过来说，不能用"本人是露娜"这种纯角色判据 —— 那样永远等不到这一刻。）
    ///
    /// 结算直接复用原版 GameRoom.GameOver()（:171389），它已包含需求所要的完整演出：
    ///     EndClass 提示 → 判黑方胜 → 对存活白方 S_STOP_CONTROL + OnDeadCollarBomb（项圈自爆）
    ///     → 7.5 秒后进入总结算
    ///
    /// 注：不分配黑幕时 MasterMind 为 null，GameOver 内部的 PrimaryWinnerId 会是 0
    /// —— 语义上"没有单一主赢家"，可接受；不额外修正，避免重复触发 ApplyTeamResults。
    /// </summary>
    [HarmonyPatch(typeof(GamePlayer), nameof(GamePlayer.OnDead))]
    [PatchFeature(
        section: "BlackWin",
        description: "非露娜系白方全部淘汰时判黑方胜利（含「小偷偷到露娜能力」这种无人死亡的路径）。",
        defaultEnabled: true,
        side: FeatureSide.Host)]
    internal static class BlackWinFeature
    {
        [HarmonyPostfix]
        private static void Postfix(GamePlayer __instance) => TryTrigger();

        /// <summary>
        /// 技能被换的那一刻也要检查 —— 小偷偷到露娜能力后，露娜系集合**当场**变化，
        /// 而这条路径上没有任何人死亡（OnDead 钩子不会跑）。
        /// </summary>
        [HarmonyPatch(typeof(SkillComponent), "UseRuleBreaker")]
        internal static class RuleBreakerHook
        {
            [HarmonyPostfix]
            private static void Postfix() => TryTrigger();
        }

        /// <summary>
        /// 每秒兜底：任何"露娜系集合变化但无人死亡"的路径都能被兜住
        /// （例如将来又有别的机制改技能）。成本极低：只是遍历存活玩家。
        /// </summary>
        [HarmonyPatch(typeof(GameRoom), "SurvivalTick")]
        internal static class TickHook
        {
            [HarmonyPostfix]
            private static void Postfix() => TryTrigger();
        }

        /// <summary>满足条件就结算；三个触发点共用。</summary>
        private static void TryTrigger()
        {
            Diagnostics.Hit("BlackWin");
            if (ModeRuntime.Bypass)
                return;

            var room = GameRoom.Instance;
            if (room == null || room.State != EGameState.Survive)
                return;
            if (room.ResultType == EResultType.BlackWin)
                return;                            // 已经结算过，避免重复触发

            // ★★ **已经判白胜了就必须退出** —— 否则会把白胜覆盖成黑胜、并把存活白方全部项圈自爆。
            //
            //   为什么会在同一 tick 撞上：本钩子挂在 `GameRoom.SurvivalTick` 的 **Postfix**，
            //   而 `WhiteWinFeature` 判白胜是 `SurvivalTick` 的 **Prefix 返回 false**
            //   —— `return false` 只跳过**原方法体**，Harmony **仍会执行 Postfix**。
            //   于是时间归零那一刻：Prefix 先设 `ResultType = WhiteWin` 并返回，
            //   Postfix（本方法）紧接着跑；若此时判据为真（`OnlyLunaSideAlive` 只要求
            //   "不存在普通白方"，**空集合也算真**），就会再调一次 `GameOver()`
            //   ⇒ `EndClass` 提示 + `ResultType = BlackWin` + 存活白方 `OnDeadCollarBomb()`
            //   ⇒ 玩家看到"白胜却冒出黑方的【特别课程结束】"、白方还被炸。
            if (room.ResultType == EResultType.WhiteWin)
                return;

            if (!OnlyLunaSideAlive(room))
                return;

            Plugin.Log.LogInfo("[HS] BlackWin：非露娜系白方已全部淘汰 → 判黑方胜利。");
            room.GameOver();
        }

        /// <summary>
        /// 是否已不存在"非露娜系白方"的存活者。
        ///
        /// 判据只有一条：活人里还有没有既不是黑方、又不是露娜系的人。
        /// ★ "露娜系"含**被 RuleBreaker 偷到露娜技能的人**（<see cref="GameRefs.IsLunaSide"/>）——
        ///   小偷偷到露娜能力后他自己就不再是"普通白方"，条件当场成立。
        ///
        /// 不额外要求"至少有一名露娜系存活" —— 露娜系全灭同样满足黑方胜利条件
        /// （黑方自己一定活着，否则走不到这里）。
        /// 假人（IsDummy）同样计入白方存活，否则拿它们当靶子时条件永远不成立。
        /// </summary>
        private static bool OnlyLunaSideAlive(GameRoom room)
        {
            foreach (var player in room.AlivePlayers)
            {
                if (player?.PublicInfo == null)
                    continue;
                if (player.IsSpectator)
                    continue;
                if (player.Color == EPlayerColor.Black || player.Color == EPlayerColor.Dark)
                    continue;                      // 黑方自己不计入白方存活

                if (!GameRefs.IsLunaSide(player))
                    return false;                  // 仍有普通白方存活 → 条件未达成
            }

            return true;
        }
    }
}
