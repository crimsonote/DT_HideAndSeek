using HarmonyLib;
using Server.Game;
using HideAndSeek.Core;

namespace HideAndSeek.Features.System
{
    /// <summary>
    /// 驱动模式开关监视：每次生存 tick 检查 HS_Mode.Enabled 是否由开变关，
    /// 变化时执行一次回滚（见 <see cref="ModeWatchdog"/>）。
    ///
    /// 单独成一个功能而不并入 AOI，是因为它必须**始终挂载** ——
    /// 若依附于某个可被禁用的功能，该功能关闭时回滚逻辑也会一并消失。
    /// </summary>
    [HarmonyPatch(typeof(GameRoom), "SurvivalTick")]
    [PatchFeature(
        section: "ModeWatchdog",
        description: "模式开关监视：关闭捉迷藏模式时自动回滚黑方可见性与光照，避免残留半关闭状态。",
        defaultEnabled: true,
        side: FeatureSide.Host)]
    internal static class ModeWatchdogFeature
    {
        [HarmonyPostfix]
        private static void Postfix()
        {
            Diagnostics.Hit("ModeWatchdog");
            ModeWatchdog.Tick();
        }
    }
}
