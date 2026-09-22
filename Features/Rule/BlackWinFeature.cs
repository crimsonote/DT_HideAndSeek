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
        description: "非露娜系白方全部淘汰时判黑方胜利（复用原版 GameOver：幸存者项圈自爆 + 7.5 秒结算）。",
        defaultEnabled: true,
        side: FeatureSide.Host)]
    internal static class BlackWinFeature
    {
        [HarmonyPostfix]
        private static void Postfix(GamePlayer __instance)
        {
            Diagnostics.Hit("BlackWin");
            if (ModeRuntime.Bypass)
                return;

            var room = GameRoom.Instance;
            if (room == null || room.State != EGameState.Survive)
                return;
            if (room.ResultType == EResultType.BlackWin)
                return;                            // 已经结算过，避免重复触发

            if (!OnlyLunaSideAlive(room))
                return;

            Plugin.Log.LogInfo("[HS] BlackWin：非露娜系白方已全部淘汰 → 判黑方胜利。");
            room.GameOver();
        }

        /// <summary>
        /// 是否已不存在"非露娜系白方"的存活者。
        ///
        /// 判据只有一条：活人里还有没有既不是黑方、又不是露娜系的人。
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
