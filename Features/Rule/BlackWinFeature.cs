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

        /// <summary>存活的白方（排除观战/占位/黑方）是否只剩露娜系，且至少存在一名。</summary>
        private static bool OnlyLunaSideAlive(GameRoom room)
        {
            bool anyWhite = false;

            foreach (var player in room.AlivePlayers)
            {
                if (player == null || player.PublicInfo == null)
                    continue;
                if (player.IsSpectator)
                    continue;
                // 注意：假人（IsDummy）也算白方存活。它们是被 ConvertToDummy 标记的靶子，
                // 若不计数，"非露娜系白方全部淘汰"永远不成立，黑胜条件失效。
                if (player.Color == EPlayerColor.Black || player.Color == EPlayerColor.Dark)
                    continue;                      // 黑方不计入白方存活

                anyWhite = true;
                if (!GameRefs.IsLunaSide(player))
                    return false;                  // 仍有普通白方存活 → 条件未达成
            }

            return anyWhite;
        }
    }
}
