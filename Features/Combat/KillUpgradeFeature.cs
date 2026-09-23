using System.Collections.Generic;
using BepInEx.Configuration;
using HarmonyLib;
using Protocol;
using Server.Game;
using HideAndSeek.Core;
using HideAndSeek.Features.Vision;
using HideAndSeek.Features.Rule;
using GamePlayer = Server.Game.Player;

namespace HideAndSeek.Features.Combat
{
    /// <summary>
    /// 黑学分：黑方每杀一名白方获得学分，累积后可换取三项永久强化。
    ///
    /// 发放规则（可配）：
    ///   每股学分 = 池子总量 ÷ **白方人数**（分母去掉黑方自己）
    ///   即 3 人 2 白时每杀一人得 1/2 池子；7 人 6 白时得 1/6 池子。
    ///   池子总量固定，因此**杀光全部白方恰好发完整个池子**。
    ///
    /// 花费规则：
    ///   3 个方向 × 每项 3 级 = 9 级硬上限；每级固定成本 ≈ 池子的 17%
    ///   （基准：8 人 7 白、杀掉 6 人 → 6/7 池子 ≈ 够点 5 级）。
    ///   "5 级"是预算结果而非上限 —— 杀得少就点得少。
    ///
    /// 三项效果都通过**改写既有功能的配置项**生效，因此天然可热调：
    ///   vision → AoiCullingFeature.EnterRange / ExitRange（× 1.5 每级）
    ///   speed  → SpeedBoostFeature.BlackSpeedMul（+ 0.1 每级）
    ///   task   → WhiteWinFeature.MinMissionProgress（+ 10 每级）
    ///
    /// 每次升级都会：系统告示（全房可见）+ 写入发信机聊天记录（任何人可回溯）。
    /// </summary>
    [PatchFeature(
        section: "KillUpgrade",
        description: "黑学分：击杀获得学分，可换取视野/移速/任务门槛三项强化（每项最多 3 级）。",
        defaultEnabled: true,
        side: FeatureSide.Host)]
    internal static class KillUpgradeFeature
    {
        [ConfigField(100f, "学分池总量。杀光全部白方恰好发完。", Min = 10f, Max = 10000f)]
        public static ConfigEntry<float> PoolTotal;

        [ConfigField(18f, "每级消耗（池子的百分比）。默认 18% → 6/7 池子约够点 5 级。",
            Min = 1f, Max = 100f)]
        public static ConfigEntry<float> CostPercentPerLevel;

        [ConfigField(3, "每个方向的最大等级。", Min = 0f, Max = 9f)]
        public static ConfigEntry<int> MaxLevelPerItem;

        [ConfigField(0.5f, "【视野】每级扩大比例。0.5 = 每级 +50%（连升三级 ≈ 3.4 倍）。",
            Min = 0f, Max = 5f)]
        public static ConfigEntry<float> VisionBonusPerLevel;

        [ConfigField(0.1f, "【移速】每级增加量（直接加到 SpeedBoost.BlackSpeedMul）。",
            Min = 0f, Max = 2f)]
        public static ConfigEntry<float> SpeedBonusPerLevel;

        [ConfigField(10f, "【任务门槛】每级增加的任务进度要求（加到 WhiteWinOnTimeout.MinMissionProgress）。",
            Min = 0f, Max = 200f)]
        public static ConfigEntry<float> TaskBonusPerLevel;

        [ConfigField(true, "每次升级向全场发系统告示（同时进入发信机记录）。")]
        public static ConfigEntry<bool> AnnounceUpgrade;

        /// <summary>方向索引：0=视野 1=移速 2=任务门槛。</summary>
        internal const int DirVision = 0;
        internal const int DirSpeed = 1;
        internal const int DirTask = 2;
        internal const int DirCount = 3;

        private static float _credits;
        private static readonly int[] Levels = new int[DirCount];

        /// <summary>已被我们改写过的基础值快照，用于"先还原再套用"避免叠加。</summary>
        private static float _baseEnter = -1f;
        private static float _baseExit = -1f;
        private static float _baseSpeed = -1f;
        private static float _baseTask = -1f;

        internal static float Credits => _credits;

        internal static int LevelOf(int dir)
            => dir >= 0 && dir < DirCount ? Levels[dir] : 0;

        // ── 击杀发放 ────────────────────────────────────────────────
        [HarmonyPatch(typeof(GamePlayer), "OnDeadMurder")]
        internal static class KillHook
        {
            [HarmonyPostfix]
            private static void Postfix()
            {
                Diagnostics.Hit("KillUpgrade");
                if (ModeRuntime.Bypass)
                    return;

                var room = GameRoom.Instance;
                if (room == null || room.State != EGameState.Survive)
                    return;

                int whites = 0;
                foreach (var p in room.Players)
                {
                    if (p?.PublicInfo == null || !p.IsAlive)
                        continue;
                    if (p.Color == EPlayerColor.White)
                        whites++;
                }

                // 刚被杀的那个可能已被置为 Hide，所以白方分母取"当前存活白方数 + 1"，
                // 这样在 2 白局面下每杀一人得 1/2、6 白局面下得 1/6，与开局口径一致。
                int divisor = whites + 1;
                if (divisor <= 0)
                    divisor = 1;

                float share = (PoolTotal?.Value ?? 100f) / divisor;
                _credits += share;

                Plugin.Log.LogInfo(
                    $"[HS] KillUpgrade：击杀获得 {share:F1} 学分（池子 {PoolTotal?.Value:F0} ÷ {divisor}），当前 {_credits:F1}。");

                Announce(room, $"黑方获得 {share:F1} 学分（现有 {_credits:F1}）。");
            }
        }

        // ── 升级 ────────────────────────────────────────────────────
        internal static bool TryUpgrade(GameRoom room, int dir, out string message)
        {
            message = null;

            int maxLevel = MaxLevelPerItem?.Value ?? 3;
            if (dir < 0 || dir >= DirCount)
            {
                message = "未知升级方向（vision / speed / task）";
                return false;
            }
            if (Levels[dir] >= maxLevel)
            {
                message = $"{DirName(dir)} 已达到上限 {maxLevel} 级";
                return false;
            }

            float cost = (PoolTotal?.Value ?? 100f) * (CostPercentPerLevel?.Value ?? 18f) / 100f;
            if (_credits < cost)
            {
                message = $"学分不足（需要 {cost:F1}，现有 {_credits:F1}）";
                return false;
            }

            _credits -= cost;
            Levels[dir]++;
            ApplyUpgrades();

            message = $"{DirName(dir)} 升至 {Levels[dir]} 级（消耗 {cost:F1}，剩余 {_credits:F1}）";
            if (AnnounceUpgrade == null || AnnounceUpgrade.Value)
                Announce(room, "黑方强化：" + message);
            Plugin.Log.LogInfo($"[HS] KillUpgrade：{message}");
            return true;
        }

        internal static string DirName(int dir)
        {
            switch (dir)
            {
                case DirVision: return "视野追踪范围";
                case DirSpeed: return "移动速度";
                case DirTask: return "白方任务门槛";
                default: return "?";
            }
        }

        internal static string Status()
        {
            float cost = (PoolTotal?.Value ?? 100f) * (CostPercentPerLevel?.Value ?? 18f) / 100f;
            return $"黑学分 {_credits:F1}（每级消耗 {cost:F1}）\n" +
                   $"视野 {Levels[DirVision]}/{MaxLevelPerItem?.Value}｜" +
                   $"移速 {Levels[DirSpeed]}/{MaxLevelPerItem?.Value}｜" +
                   $"任务 {Levels[DirTask]}/{MaxLevelPerItem?.Value}";
        }

        /// <summary>把三项等级套用到既有功能的配置项上（先还原基础值，避免叠加）。</summary>
        private static void ApplyUpgrades()
        {
            // 视野：扩大 AOI 的进入/退出半径
            if (AoiCullingFeature.EnterRange != null && AoiCullingFeature.ExitRange != null)
            {
                if (_baseEnter < 0f)
                {
                    _baseEnter = AoiCullingFeature.EnterRange.Value;
                    _baseExit = AoiCullingFeature.ExitRange.Value;
                }
                float k = 1f + (VisionBonusPerLevel?.Value ?? 0.5f) * Levels[DirVision];
                AoiCullingFeature.EnterRange.Value = _baseEnter * k;
                AoiCullingFeature.ExitRange.Value = _baseExit * k;
            }

            // 移速：直接加到黑方移速倍率
            if (SpeedBoostFeature.BlackSpeedMul != null)
            {
                if (_baseSpeed < 0f)
                    _baseSpeed = SpeedBoostFeature.BlackSpeedMul.Value;
                SpeedBoostFeature.BlackSpeedMul.Value =
                    _baseSpeed + (SpeedBonusPerLevel?.Value ?? 0.1f) * Levels[DirSpeed];
            }

            // 任务门槛：抬高白方获胜所需进度
            if (WhiteWinFeature.MinMissionProgress != null)
            {
                if (_baseTask < 0f)
                    _baseTask = WhiteWinFeature.MinMissionProgress.Value;
                WhiteWinFeature.MinMissionProgress.Value =
                    (int)(_baseTask + (TaskBonusPerLevel?.Value ?? 10f) * Levels[DirTask]);
            }
        }

        /// <summary>系统告示：全房可见，并进入发信机（密聊）记录供随时回溯。</summary>
        internal static void Announce(GameRoom room, string text)
        {
            try
            {
                room?.Broadcast(new S_CHAT_MESSAGE
                {
                    Type = EChatType.SecretChat,
                    Text = "【黑学分】" + text,
                    DeviceId = 999999,
                    Time = (int)(TimeManager.Instance?.SurviveTime ?? 0f)
                });
            }
            catch (global::System.Exception ex)
            {
                Plugin.Log.LogWarning($"[HS] KillUpgrade：告示失败 — {ex.Message}");
            }
        }

        // ── 生命周期 ────────────────────────────────────────────────
        [HarmonyPatch(typeof(GameRoom), "StartLobby")]
        internal static class LobbyHook
        {
            [HarmonyPostfix]
            private static void Postfix() => ResetAll();
        }

        [HarmonyPatch(typeof(GameRoom), "StartDetective")]
        internal static class DetectiveHook
        {
            [HarmonyPostfix]
            private static void Postfix() => ResetAll();
        }

        private static void ResetAll()
        {
            _credits = 0f;
            for (int i = 0; i < DirCount; i++)
                Levels[i] = 0;

            // 还原被我们改写过的配置，避免跨局继续生效
            if (_baseEnter >= 0f && AoiCullingFeature.EnterRange != null)
            {
                AoiCullingFeature.EnterRange.Value = _baseEnter;
                AoiCullingFeature.ExitRange.Value = _baseExit;
            }
            if (_baseSpeed >= 0f && SpeedBoostFeature.BlackSpeedMul != null)
                SpeedBoostFeature.BlackSpeedMul.Value = _baseSpeed;
            if (_baseTask >= 0f && WhiteWinFeature.MinMissionProgress != null)
                WhiteWinFeature.MinMissionProgress.Value = (int)_baseTask;

            _baseEnter = _baseExit = _baseSpeed = _baseTask = -1f;
        }
    }
}
