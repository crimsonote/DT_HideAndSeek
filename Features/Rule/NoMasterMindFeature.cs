using HarmonyLib;
using Protocol;
using HideAndSeek.Core;
using GamePlayer = Server.Game.Player;

namespace HideAndSeek.Features.Rule
{
    /// <summary>
    /// 开局不分配黑幕（Dark）。
    ///
    /// 原版 GameRoom.StartPick（:171011）在全员变白后随机挑一人：
    ///     Players[random].Color = EPlayerColor.Dark;
    ///     SetMasterMind(Players[random]);
    /// 本功能拦截 SetMasterMind —— MasterMind 的唯一赋值入口（主机迁移恢复 :173720 亦经此处），
    /// 把刚涂成 Dark 的玩家改回 White，并拒绝记录 MasterMind。
    /// 改 Color 会走 set_Color，正常下发 S_MODIFY_PLAYER，客户端表现与"从未变黑"一致。
    /// </summary>
    [HarmonyPatch(typeof(Server.Game.GameRoom), nameof(Server.Game.GameRoom.SetMasterMind))]
    [PatchFeature(
        section: "NoMasterMind",
        description: "开局不分配黑幕（Dark）：跳过原版随机指定，改由玩家自行跑刀转黑方。",
        defaultEnabled: true,
        side: FeatureSide.Host)]
    internal static class NoMasterMindFeature
    {
        [HarmonyPrefix]
        private static bool Prefix(GamePlayer player)
        {
            Diagnostics.Hit("NoMasterMind");
            if (ModeRuntime.Bypass)
                return true;

            if (player != null && player.Color == EPlayerColor.Dark)
                player.Color = EPlayerColor.White;

            Plugin.Log.LogInfo("[HS] NoMasterMind：已跳过黑幕分配，该玩家保持白方。");
            return false;
        }
    }
}
