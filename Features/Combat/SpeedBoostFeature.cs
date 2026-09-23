using BepInEx.Configuration;
using HarmonyLib;
using Protocol;
using Server.Game;
using HideAndSeek.Core;

namespace HideAndSeek.Features.Combat
{
    /// <summary>
    /// 黑方移动速度倍率。
    ///
    /// 速度是**客户端本地权威**：服务端 HandleMove（:171719）只校验坐标合法性，
    /// 不做速度/位移距离校验，所以放大速度不会导致回拉。
    /// 下行靠 S_MODIFY_MY_PLAYER(ChangeSpeed)（SendChangeSpeed :176936，只发自己）。
    ///
    /// 必须挂在 BuffComponent.RefreshSpeed（:161475）的 **Postfix** ——
    /// 该方法是速度的唯一来源，Player.StartState（:176986）在站定/起跑时都会重新调用它，
    /// 在 Prefix 里改会被原方法从 560*delta 重算覆盖。
    ///
    /// 排除幽灵：MakeSpectatorGhost（:175598）与 ExitPlayer（:176114）直接设
    /// Speed = GHOST_SPEED(840)，不经 RefreshSpeed；若在别处连乘会污染观战速度。
    /// </summary>
    [PatchFeature(
        section: "SpeedBoost",
        description: "黑方移动速度倍率（1.0 = 原版）。",
        defaultEnabled: true,
        side: FeatureSide.Host)]
    internal static class SpeedBoostFeature
    {
        [ConfigField(1.0f, "黑方移动速度倍率。1.0 = 原版；1.2 = 快 20%。", Min = 0.5f, Max = 3f)]
        public static ConfigEntry<float> BlackSpeedMul;

        internal static float Mul => BlackSpeedMul?.Value ?? 1f;

        [HarmonyPatch(typeof(BuffComponent), nameof(BuffComponent.RefreshSpeed))]
        internal static class RefreshSpeedHook
        {
            [HarmonyPostfix]
            private static void Postfix(BuffComponent __instance)
            {
                if (ModeRuntime.Bypass)
                    return;

                float mul = Mul;
                // 只在"确实是默认值"时放行。原先写成 mul <= 0f 也 return，
// 导致有人把 BlackSpeedMul 设为 0 想减速时会静默失效（时停第一版就踩了这个坑）。
            if (global::System.Math.Abs(mul - 1f) < 0.001f)
                    return;                       // 1.0 不动，省一次广播

                var player = __instance?.Owner;
                if (player == null || !player.IsAlive || player.State == EPlayerState.Hide)
                    return;                       // 幽灵：MakeSpectatorGhost 置 Hide 并直接设速度，别掺和
                if (player.Color != EPlayerColor.Black)
                    return;

                player.PrivateInfo.Speed *= mul;
                player.SendChangeSpeed();
            }
        }
    }
}
