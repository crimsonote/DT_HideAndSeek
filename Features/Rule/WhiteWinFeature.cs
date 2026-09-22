using System.Reflection;
using BepInEx.Configuration;
using HarmonyLib;
using Protocol;
using Server.Game;
using HideAndSeek.Core;
using GamePlayer = Server.Game.Player;

namespace HideAndSeek.Features.Rule
{
    /// <summary>
    /// 限制时间归零 → 白方胜利（原版为黑方胜利）。
    ///
    /// 原版 GameRoom.SurvivalTick（:171245）：
    ///     if (TimeManager.Instance.RemainTime == 0f)
    ///     {
    ///         Corpse corpse = FindOldestUndiscoveredCorpse();
    ///         if (corpse != null) corpse.DiscoverByTimeOver();   // 进调查
    ///         else GameOver();                                   // 黑方胜
    ///         return;
    ///     }
    /// 「撑到时间结束」是捉迷藏里白方的胜利条件，因此在该分支改为白方结算。
    /// 结算流程等价于 MissionManager.ClearAllMission（:166889），全部使用公开 API 复刻，
    /// 避免依赖 internal 类型。
    /// </summary>
    [HarmonyPatch(typeof(GameRoom), "SurvivalTick")]
    [PatchFeature(
        section: "WhiteWinOnTimeout",
        description: "限制时间归零时判白方胜利（原版为黑方胜利）。",
        defaultEnabled: true,
        side: FeatureSide.Host)]
    internal static class WhiteWinFeature
    {
        // 字段名不能叫 Enabled —— 会与 PatchLoader 生成的段级 Enabled 配置键冲突
        [ConfigField(true, "限制时间归零时判白方胜利。")]
        public static ConfigEntry<bool> OnTimeout;

        // MissionManager 在发行程序集中是 internal，AllClear 只能反射读取
        // 注意 AccessTools.PropertyGetter 返回的是 getter 的 MethodInfo
        private static MethodInfo _allClearGetter;
        private static MethodInfo _instanceGetter;
        private static bool _reflectFailed;

        [HarmonyPrefix]
        private static bool Prefix(GameRoom __instance)
        {
            Diagnostics.Hit("WhiteWinOnTimeout");
            if (ModeRuntime.Bypass)
                return true;
            if (OnTimeout == null || !OnTimeout.Value)
                return true;

            // 与原版一致的前置条件
            if (__instance.State != EGameState.Survive || IsAllClear())
                return true;
            if (TimeManager.Instance.Paused)
                return true;                      // 交给原版重排 tick
            if (TimeManager.Instance.RemainTime != 0f)
                return true;                      // 未归零 → 原版正常 tick

            TriggerWhiteWin(__instance);
            return false;
        }

        private static bool IsAllClear()
        {
            if (_reflectFailed)
                return false;

            try
            {
                if (_allClearGetter == null)
                {
                    var type = AccessTools.TypeByName("Server.Game.MissionManager");
                    if (type == null)
                    {
                        _reflectFailed = true;
                        Plugin.Log.LogWarning("[HS] 找不到 Server.Game.MissionManager，跳过任务全清检查。");
                        return false;
                    }

                    _instanceGetter = AccessTools.PropertyGetter(type, "Instance");
                    _allClearGetter = AccessTools.PropertyGetter(type, "AllClear");
                }

                object instance = _instanceGetter?.Invoke(null, null);
                return instance != null && (bool)(_allClearGetter?.Invoke(instance, null) ?? false);
            }
            catch (global::System.Exception ex)
            {
                _reflectFailed = true;
                Plugin.Log.LogWarning($"[HS] 读取 MissionManager.AllClear 失败：{ex.Message}");
                return false;
            }
        }

        private static void TriggerWhiteWin(GameRoom room)
        {
            room.ResultType = EResultType.WhiteWin;
            room.ApplyTeamResults();

            foreach (GamePlayer player in room.Players)
                player.Session.Send(new S_STOP_CONTROL());

            room.AlertMessageAllPlayers(ESystemMessageType.CompleteClass);
            room.Broadcast(new S_ENDING_CAMERA { IsEnd = true });

            room.PushAfter(13700, delegate
            {
                room.ChangeGameState(EGameState.TotalResult);
                room.Broadcast(new S_ENDING_CAMERA { IsEnd = false });
            });

            Plugin.Log.LogInfo("[HS] 限制时间归零 → 判白方胜利。");
        }
    }
}
