using System;
using BepInEx.Configuration;
using HarmonyLib;
using HideAndSeek.Core;

namespace HideAndSeek.Features.Dev
{
    /// <summary>
    /// 单人／少人开局，用于本地验证本模块（正式对局不需要）。
    ///
    /// 唯一的硬门槛在房主侧：GameRoom.HandleStart（:170990）
    ///     if (State == Lobby && Host == player && Players.Count >= Define.LOBBY_MIN_PLAYER && CheckAllPlayerReady())
    /// 而客户端完全不拦 —— CanStartGame()（:75970）只被 RefreshStartButtonColor()（:75985）用于改按钮颜色，
    /// 点击处理 OnClickStartButton（:76001）与 F5（:78703）都没有任何校验，
    /// 全项目 interactable 也没有一处在开始按钮上。
    /// 因此只需改写 Define.LOBBY_MIN_PLAYER（:93760，正式服为 5），不存在客户端阻塞点。
    ///
    /// 注意：
    ///   - 不要用 IsPlaytestApp 达到同一目的 —— 它会把支付/库存请求打到 pay-dev 后端；
    ///   - 若同时装了 DTSoloPlay（它 patch 同一个 getter），两个 Prefix 的返回顺序未定义会互相覆盖，
    ///     一次只启用一个。
    /// </summary>
    [HarmonyPatch(typeof(Define), nameof(Define.LOBBY_MIN_PLAYER), MethodType.Getter)]
    [PatchFeature(
        section: "SoloPlay",
        description: "单人/少人开局（测试向）：放开开始游戏所需的最少玩家数，正式对局无需开启。",
        defaultEnabled: false,
        side: FeatureSide.Host)]
    internal static class SoloPlayFeature
    {
        [ConfigField(1, "开始游戏所需的最少玩家数。原版正式服为 5；设为 1 即可单人开局。", Min = 1f, Max = 10f)]
        public static ConfigEntry<int> MinPlayers;

        [HarmonyPrefix]
        // 上游 DT_Tools 的 LobbyMinPlayersFeature 也 Prefix 同一个 getter，同样是"写 __result 后返回 false"。
        // 固定本模块先跑：捉迷藏模式开着时由本模块决定最少人数；模式关闭时返回 true，
        // 上游那条照常生效。否则两者谁先挂上谁生效，结果不可预期。
        [HarmonyPriority(Priority.First)]
        private static bool Prefix(ref int __result)
        {
            Diagnostics.Hit("SoloPlay");
            if (ModeRuntime.Bypass)
                return true;

            int value = MinPlayers?.Value ?? 1;
            __result = value < 1 ? 1 : value;
            return false;
        }
    }
}
