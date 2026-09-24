using System.Collections.Generic;
using System.Linq;
using BepInEx.Configuration;
using HarmonyLib;
using Protocol;
using Server.Game;
using HideAndSeek.Core;
using HideAndSeek.Features.Combat;
using HideAndSeek.Features.Vision;
using GamePlayer = Server.Game.Player;

namespace HideAndSeek.Features.Rule
{
    /// <summary>
    /// 动态规则：按局内形势（剩余时间 / 黑方击杀数 / 白方存活数）自动重写玩法参数。
    ///
    /// 配置格式（多条用换行或分号分隔）：
    ///     time&lt;=120 -&gt; SpeedMul=1.3, EnterRange=1200
    ///     kills&gt;=2  -&gt; ExitRange=1500
    ///     alive&lt;=1  -&gt; KillLimit=9999
    ///
    /// 条件（多个用 &amp; 连接，需全部成立）：
    ///     time&lt;=N    剩余倒计时 ≤ N 秒
    ///     kills&gt;=N   黑方累计击杀 ≥ N
    ///     alive&lt;=N   白方存活 ≤ N
    /// 动作（多个用 , 连接）：Key=Value，可用键见 Apply() 的 switch。
    ///
    /// 语义：
    ///   · 一条规则只触发**一次**（触发后记入 Fired），避免每秒重复写入
    ///   · 触发即生效、**不因条件不再满足而回退** —— 期望的是"到某个节点后局势升级"
    ///   · 回大厅（StartLobby）时把被改过的键**还原成进入对局前的值**，否则会越改越乱
    ///   · 多个规则改同一个键时，按书写顺序后者覆盖前者
    /// </summary>
    [PatchFeature(
        section: "RuleRewrite",
        description: "按剩余时间/击杀数/存活数动态重写玩法参数（实验性规则引擎）。",
        defaultEnabled: true,
        side: FeatureSide.Host)]
    internal static class RuleRewriteFeature
    {
        [ConfigField("",
            "动态规则。格式：条件 -> 动作；多条用换行或分号分隔。" +
            "条件可用 time<=N（剩余秒）、kills>=N（黑方击杀）、alive<=N（白方存活），多个用 & 连接；" +
            "动作是 Key=Value，多个用 , 连接。可用键：SpeedMul/EnterRange/ExitRange/Cooldown/KillLimit/RepairCount/MinProgress。" +
            "例：time<=120 -> SpeedMul=1.3, EnterRange=1200")]
        public static ConfigEntry<string> Rules;

        /// <summary>键名 → 进入对局前的原始值，用于回大厅时还原。</summary>
        private static readonly Dictionary<string, string> Originals = new Dictionary<string, string>();

        /// <summary>已触发过的规则下标（规则只触发一次）。</summary>
        private static readonly HashSet<int> Fired = new HashSet<int>();

        /// <summary>本局黑方击杀累计。</summary>
        private static int _killCount;

        // ── 计数与生命周期 ──────────────────────────────────────────
        [HarmonyPatch(typeof(GamePlayer), "OnDeadMurder")]
        internal static class KillCountHook
        {
            [HarmonyPostfix]
            private static void Postfix()
            {
                if (ModeRuntime.Bypass)
                    return;
                _killCount++;
            }
        }

        [HarmonyPatch(typeof(GameRoom), "StartSurvive")]
        internal static class StartHook
        {
            [HarmonyPostfix]
            private static void Postfix()
            {
                if (ModeRuntime.Bypass)
                    return;
                _killCount = 0;
                Fired.Clear();
            }
        }

        [HarmonyPatch(typeof(GameRoom), "StartLobby")]
        internal static class LobbyHook
        {
            [HarmonyPostfix]
            private static void Postfix()
            {
                Restore();
                Fired.Clear();
                _killCount = 0;
            }
        }

        // ── 每秒评估 ────────────────────────────────────────────────
        [HarmonyPatch(typeof(GameRoom), "SurvivalTick")]
        internal static class TickHook
        {
            [HarmonyPostfix]
            private static void Postfix(GameRoom __instance)
            {
                Diagnostics.Hit("RuleRewrite");
                if (ModeRuntime.Bypass)
                    return;
                if (__instance == null || __instance.State != EGameState.Survive)
                    return;

                string raw = Rules?.Value;
                if (string.IsNullOrWhiteSpace(raw))
                    return;

                string[] lines = raw.Split(new[] { '\n', '\r', ';' },
                    global::System.StringSplitOptions.RemoveEmptyEntries);

                for (int i = 0; i < lines.Length; i++)
                {
                    if (Fired.Contains(i))
                        continue;                        // 已触发过，不再重复写入

                    string line = lines[i].Trim();
                    if (line.Length == 0 || line.StartsWith("#"))
                        continue;                        // 支持用 # 注释整行

                    int arrow = line.IndexOf("->", global::System.StringComparison.Ordinal);
                    if (arrow < 0)
                        continue;

                    string condPart = line.Substring(0, arrow).Trim();
                    string actPart = line.Substring(arrow + 2).Trim();

                    if (!MatchesAll(condPart, __instance))
                        continue;

                    Fired.Add(i);
                    Apply(actPart);
                }
            }
        }

        internal static bool MatchesAll(string condPart, GameRoom room)
        {
            foreach (string cond in condPart.Split('&'))
            {
                string c = cond.Trim();
                if (c.Length == 0)
                    continue;
                if (!Matches(c, room))
                    return false;                    // 有一个不成立就整体不成立
            }
            return true;
        }

        private static bool Matches(string cond, GameRoom room)
        {
            if (cond.StartsWith("time<=", global::System.StringComparison.OrdinalIgnoreCase))
            {
                if (!TryFloat(cond.Substring(6), out float n))
                    return false;
                return TimeManager.Instance != null && TimeManager.Instance.RemainTime <= n;
            }

            if (cond.StartsWith("elapsed>=", global::System.StringComparison.OrdinalIgnoreCase))
            {
                // 本局**已经过去**的生存秒数 ≥ N。与 time<=N（剩余时间）互补：
                // 「开局 60 秒内不能用」= elapsed>=60。
                if (!TryFloat(cond.Substring(9), out float n))
                    return false;
                return TimeManager.Instance != null && TimeManager.Instance.SurviveTime >= n;
            }

            if (cond.StartsWith("kills>=", global::System.StringComparison.OrdinalIgnoreCase))
            {
                if (!TryFloat(cond.Substring(7), out float n))
                    return false;
                return _killCount >= n;
            }

            if (cond.StartsWith("alive<=", global::System.StringComparison.OrdinalIgnoreCase))
            {
                if (!TryFloat(cond.Substring(7), out float n))
                    return false;

                // 只数白方（与 Broadcast 的口径一致）：黑方与黑幕不算"白方存活"
                int whites = room.Players.Count(p => p?.PublicInfo != null
                    && p.IsAlive
                    && p.Color == EPlayerColor.White);
                return whites <= n;
            }

            // fusebox：地图上存在"已派发目标、且尚未断电"的电箱。
            // 原版在断电数归零后会 PushSurvivalJob(60, StartFuseboxSabotage) 重新派发 3 个目标
            //（RefreshLight :173503），所以这个条件天然包含了那 60 秒节奏 ——
            // 命令的可用性直接交给原版派发时机，不必再另设 CD。
            if (cond.Equals("fusebox", global::System.StringComparison.OrdinalIgnoreCase))
                return HasBreakableFusebox();

            return false;
        }

        /// <summary>地图上是否有可拆的电箱（已派发目标且尚未断电）。</summary>
        internal static bool HasBreakableFusebox()
        {
            var manager = Server.Game.DeviceManager.Instance;
            if (manager?.Fuseboxes == null)
                return false;

            foreach (var fusebox in manager.Fuseboxes)
            {
                var info = fusebox?.DeviceInfo;
                if (info?.StateList == null || info.StateList.Count == 0)
                    continue;
                if (info.MissionType == -1 && info.StateList[0] == 0)
                    return true;
            }
            return false;
        }

        /// <summary>执行动作串，如 "SpeedMul=1.3, EnterRange=1200"。</summary>
        private static void Apply(string actPart)
        {
            foreach (string action in actPart.Split(','))
            {
                string a = action.Trim();
                if (a.Length == 0)
                    continue;

                int eq = a.IndexOf('=');
                if (eq <= 0)
                    continue;

                string key = a.Substring(0, eq).Trim();
                string value = a.Substring(eq + 1).Trim();

                try
                {
                    if (SetValue(key, value))
                        Plugin.Log.LogInfo($"[HS] RuleRewrite：{key} → {value}");
                }
                catch (global::System.Exception ex)
                {
                    Plugin.Log.LogWarning($"[HS] RuleRewrite：写入 {key}={value} 失败 — {ex.Message}");
                }
            }
        }

        /// <summary>把动作键映射到各功能的配置项；写入前先记录原值以便还原。</summary>
        internal static bool SetValue(string key, string value)
        {
            switch (key.ToLowerInvariant())
            {
                case "speedmul":
                    if (!TryFloat(value, out float sp)) return false;
                    Remember(key, SpeedBoostFeature.BlackSpeedMul);
                    SpeedBoostFeature.BlackSpeedMul.Value = sp;
                    return true;

                case "enterrange":
                    if (!TryFloat(value, out float en)) return false;
                    Remember(key, AoiCullingFeature.EnterRange);
                    AoiCullingFeature.EnterRange.Value = en;
                    return true;

                case "exitrange":
                    if (!TryFloat(value, out float ex)) return false;
                    Remember(key, AoiCullingFeature.ExitRange);
                    AoiCullingFeature.ExitRange.Value = ex;
                    return true;

                case "cooldown":
                    if (!TryFloat(value, out float cd)) return false;
                    Remember(key, WeaponCooldownFeature.RearmSeconds);
                    WeaponCooldownFeature.RearmSeconds.Value = (int)cd;
                    return true;

                case "killlimit":
                    if (!TryFloat(value, out float kl)) return false;
                    Remember(key, KillLimitFeature.MaxKills);
                    KillLimitFeature.MaxKills.Value = (int)kl;
                    return true;

                case "repaircount":
                case "repairthreshold":   // 旧键名，保留兼容
                    if (!TryFloat(value, out float rt)) return false;
                    Remember(key, PowerRepairFeature.RepairCount);
                    PowerRepairFeature.RepairCount.Value = (int)rt;
                    return true;

                case "minprogress":
                    if (!TryFloat(value, out float mp)) return false;
                    Remember(key, WhiteWinFeature.MinMissionProgress);
                    WhiteWinFeature.MinMissionProgress.Value = (int)mp;
                    return true;

                default:
                    Plugin.Log.LogWarning($"[HS] RuleRewrite：未知的动作键 {key}。");
                    return false;
            }
        }

        /// <summary>首次改写某键时记下它的原值。</summary>
        private static void Remember(string key, ConfigEntryBase entry)
        {
            if (entry == null || Originals.ContainsKey(key))
                return;
            Originals[key] = entry.BoxedValue?.ToString() ?? "";
        }

        /// <summary>回大厅时把被规则改过的键还原。</summary>
        private static void Restore()
        {
            if (Originals.Count == 0)
                return;

            foreach (var kv in Originals)
            {
                try
                {
                    SetValue(kv.Key, kv.Value);
                }
                catch (global::System.Exception ex)
                {
                    Plugin.Log.LogWarning($"[HS] RuleRewrite：还原 {kv.Key} 失败 — {ex.Message}");
                }
            }

            Plugin.Log.LogInfo($"[HS] RuleRewrite：已还原 {Originals.Count} 个被规则改写的参数。");
            Originals.Clear();
        }

        private static bool TryFloat(string text, out float value)
            => float.TryParse(text, global::System.Globalization.NumberStyles.Float,
                global::System.Globalization.CultureInfo.InvariantCulture, out value);
    }
}
