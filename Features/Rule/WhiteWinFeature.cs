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
    ///
    /// MinMissionProgress 是白胜的门槛：任务进度不达标时**直接判黑方胜**。
    /// 之所以不回退原版（进 Detective 审判），是因为本玩法禁止尸体报告 ——
    /// 一味回退会得到一个没人能推进的审判阶段。
    /// </summary>
    [HarmonyPatch(typeof(GameRoom), "SurvivalTick")]
    [PatchFeature(
        section: "WhiteWinOnTimeout",
        description: "限制时间归零时判白方胜利（原版为黑方胜利）；可要求最低任务进度。",
        defaultEnabled: true,
        side: FeatureSide.Host)]
    internal static class WhiteWinFeature
    {
        // 字段名不能叫 Enabled —— 会与 PatchLoader 生成的段级 Enabled 配置键冲突
        [ConfigField(true, "限制时间归零时判白方胜利。")]
        public static ConfigEntry<bool> OnTimeout;

        [ConfigField(0, "白方获胜所需的最低任务进度（百分比，0 = 不限制）。" +
            "未达标时时间归零直接判黑方胜 —— 本玩法不报告尸体，故不回退到审判阶段。",
            Min = 0f, Max = 100f)]
        public static ConfigEntry<int> MinMissionProgress;

        // MissionManager 在发行程序集中是 internal，相关字段只能反射读取
        // 注意 AccessTools.PropertyGetter 返回的是 getter 的 MethodInfo
        private static MethodInfo _allClearGetter;
        private static MethodInfo _currentPointGetter;
        private static MethodInfo _goalPointGetter;
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

            // 任务进度门槛：不达标就判黑方胜（本玩法无报告，回退原版会进一个推不动的审判）
            int need = MinMissionProgress?.Value ?? 0;
            if (need > 0)
            {
                int progress = GetMissionProgress();
                if (progress < need)
                {
                    Plugin.Log.LogInfo($"[HS] 时间归零：任务进度 {progress}% < 要求 {need}% → 判黑方胜利。");
                    TriggerBlackWin(__instance);
                    return false;
                }
            }

            Plugin.Log.LogInfo("[HS] 限制时间归零 → 判白方胜利。");
            TriggerWhiteWin(__instance);
            return false;
        }

        /// <summary>初始化反射句柄；失败则熔断，避免每 tick 抛异常。</summary>
        private static bool EnsureReflect()
        {
            if (_reflectFailed)
                return false;
            if (_instanceGetter != null)
                return true;

            var type = AccessTools.TypeByName("Server.Game.MissionManager");
            if (type == null)
            {
                _reflectFailed = true;
                Plugin.Log.LogWarning("[HS] 找不到 Server.Game.MissionManager，任务进度相关判断将跳过。");
                return false;
            }

            _instanceGetter = AccessTools.PropertyGetter(type, "Instance");
            _allClearGetter = AccessTools.PropertyGetter(type, "AllClear");
            _currentPointGetter = AccessTools.PropertyGetter(type, "CurrentPoint");
            _goalPointGetter = AccessTools.PropertyGetter(type, "GoalPoint");
            return true;
        }

        private static bool IsAllClear()
        {
            try
            {
                if (!EnsureReflect())
                    return false;

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

        /// <summary>
        /// 当前任务完成度百分比。
        /// 口径与官方一致（:166385）：CurrentPoint / GoalPoint * 100。
        /// 注意 GoalPoint = 人数 × 15 是**加权点数预算**，所以 50% 不等于「完成一半任务」。
        /// </summary>
        private static int GetMissionProgress()
        {
            try
            {
                if (!EnsureReflect())
                    return 0;

                object instance = _instanceGetter?.Invoke(null, null);
                if (instance == null)
                    return 0;

                float current = (float)(_currentPointGetter?.Invoke(instance, null) ?? 0f);
                float goal = (float)(_goalPointGetter?.Invoke(instance, null) ?? 0f);
                return goal > 0f ? (int)(current / goal * 100f) : 0;
            }
            catch (global::System.Exception ex)
            {
                _reflectFailed = true;
                Plugin.Log.LogWarning($"[HS] 读取任务进度失败：{ex.Message}");
                return 0;
            }
        }

        /// <summary>
        /// 白方胜利结算。内容与 MissionManager.ClearAllMission(:166889) 逐行等价，
        /// 因此也被 CorpseReportFeature 复用 —— 任务进度顶满 100%（路径 ③）时，
        /// 它拦掉原版取尸进审判的那一步之后，走的就是这里。
        ///
        /// 刻意不在这里打日志：触发原因有两个（限时归零 / 任务全清），
        /// 由调用方各自记录，免得日志把原因说错。
        /// </summary>
        internal static void TriggerWhiteWin(GameRoom room)
        {
            room.ResultType = EResultType.WhiteWin;
            room.ApplyTeamResults();

            foreach (GamePlayer player in room.Players)
                player.Session.Send(new S_STOP_CONTROL());

            room.AlertMessageAllPlayers(ESystemMessageType.CompleteClass);
            room.Broadcast(new S_ENDING_CAMERA { IsEnd = true });

            room.PushAfter(13700, delegate
            {
                // 复检：这 13.7 秒里可能已经退房。JobSerializer 的挂起任务不会被退房清理，
                // 不复检就会把新一局也推进结算界面。
                if (room.State != EGameState.Survive)
                    return;

                room.ChangeGameState(EGameState.TotalResult);
                room.Broadcast(new S_ENDING_CAMERA { IsEnd = false });
            });
        }

        /// <summary>
        /// 判黑方胜。优先复用原版 GameOver（:171389，private），
        /// 它会做完 EndClass 提示 → ResultType=BlackWin → 项链自爆 → 7.5s 后进结算，
        /// 比自行复刻更不容易漏步骤。
        /// </summary>
        private static void TriggerBlackWin(GameRoom room)
        {
            var method = AccessTools.Method(typeof(GameRoom), "GameOver");
            if (method != null)
            {
                try
                {
                    method.Invoke(room, null);
                    return;
                }
                catch (global::System.Exception ex)
                {
                    Plugin.Log.LogWarning($"[HS] 调用原版 GameOver 失败，改用手写结算：{ex.Message}");
                }
            }

            room.ResultType = EResultType.BlackWin;
            room.ApplyTeamResults();

            foreach (GamePlayer player in room.Players)
                player.Session.Send(new S_STOP_CONTROL());

            room.Broadcast(new S_ENDING_CAMERA { IsEnd = true });
            room.PushAfter(7500, delegate
            {
                room.ChangeGameState(EGameState.TotalResult);
                room.Broadcast(new S_ENDING_CAMERA { IsEnd = false });
            });

            Plugin.Log.LogInfo("[HS] 时间归零且任务未达标 → 判黑方胜利（手写结算）。");
        }
    }
}
