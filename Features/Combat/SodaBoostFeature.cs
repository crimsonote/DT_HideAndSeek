using System.Collections.Generic;
using BepInEx.Configuration;
using HarmonyLib;
using Protocol;
using Server.Game;
using HideAndSeek.Core;
using GameItem = Server.Game.Item;
using GamePlayer = Server.Game.Player;

namespace HideAndSeek.Features.Combat
{
    /// <summary>
    /// 汽水：喝下后一段时间内移速提升。命令 /soda（公共，黑白都能申请）。
    ///
    /// 为什么需要这个功能：原版 3001~3005（Can01~05）本来就标着 IsConsumable = true、
    /// ActiveType = SpeedUp，但 <c>Player.UseItem</c> 的 SpeedUp 分支（:176207）**只有一行音效**：
    ///     case EItemActiveType.SpeedUp:
    ///         GameRoom.Instance.SendSystemSFX(ESoundType.SpeedUpSfx, this);
    ///         break;
    /// 没有任何移速改动 —— 是一处没写完的功能。这里把缺的那一步补上，其余全部沿用原版：
    ///   · 使用间隔与消耗由原版负责（DelayUseItem(2000) 与 :176231 的 IsConsumable → RemoveHand）
    ///   · 所以本功能**不写**任何移除/冷却代码，只写"使用了哪罐汽水"与"给他加速多久"
    ///
    /// 移速的原理（与 SpeedBoostFeature 同一套）：速度是客户端本地权威，
    /// BuffComponent.RefreshSpeed(:161475) 是速度的唯一来源，服务端改
    /// player.PrivateInfo.Speed 后 SendChangeSpeed() 下发即可。
    /// 必须挂 Postfix —— Prefix 里改会被原方法从 560*delta 重算覆盖。
    /// SpeedBoostFeature 只对 EPlayerColor.Black 生效，所以两者互不干扰、同时命中时自然叠乘。
    /// </summary>
    [PatchFeature(
        section: "SodaBoost",
        description: "汽水：喝下后一段时间内移速提升（补上原版 SpeedUp 只有音效的缺口）。",
        defaultEnabled: true,
        side: FeatureSide.Host)]
    internal static class SodaBoostFeature
    {
        [ConfigField(3001, "汽水物品 ID 范围下界（含）。", Min = 1f, Max = 9999f)]
        public static ConfigEntry<int> SodaIdFrom;

        [ConfigField(3005, "汽水物品 ID 范围上界（含）。3001~3005 = Can01~Can05 五种口味。",
            Min = 1f, Max = 9999f)]
        public static ConfigEntry<int> SodaIdTo;

        [ConfigField(1.8f, "喝下后的移速倍率。1.8 = 180%；SpeedBoost.BlackSpeedMul 是黑方的基础倍率，两者叠乘。",
            Min = 1f, Max = 3f)]
        public static ConfigEntry<float> SodaSpeedMul;

        [ConfigField(30f, "加速持续秒数。", Min = 1f, Max = 300f)]
        public static ConfigEntry<float> SodaSeconds;

        // ── 申领配额与冷却（都由本类管，不走命令引擎的 uses=/cd=，这样才能用自定义文案）──

        [ConfigField(300f, "配额窗口秒数。默认 300 = 每 5 分钟。", Min = 10f, Max = 3600f)]
        public static ConfigEntry<float> QuotaWindowSeconds;

        [ConfigField(4, "配额窗口内全房最多发几瓶。", Min = 1f, Max = 100f)]
        public static ConfigEntry<int> QuotaMax;

        [ConfigField(240f, "同一个人的两次申领之间要隔多少秒。", Min = 0f, Max = 3600f)]
        public static ConfigEntry<float> IssueCooldown;

        /// <summary>PlayerId → 加速到期时刻（SurviveTime 秒）。</summary>
        private static readonly Dictionary<int, float> Active = new Dictionary<int, float>();

        /// <summary>滚动窗口内"已发出"的时刻（全房共享）。</summary>
        private static readonly List<float> IssuedTimes = new List<float>();

        /// <summary>PlayerId → 上次申领时刻，用于每人冷却。</summary>
        private static readonly Dictionary<int, float> LastIssue = new Dictionary<int, float>();

        private static float Now => TimeManager.Instance?.SurviveTime ?? 0f;

        // ══ 申领 ════════════════════════════════════════════════════════

        /// <summary>
        /// 申领一瓶汽水（随机一种口味）。全房滑窗配额 + 每人冷却都在这里判，
        /// 因为要用自定义文案（"你暂时不能申领第二瓶汽水"），引擎那句通用冷却提示不合适。
        /// 返回 false 时 <paramref name="text"/> 说明原因。
        /// </summary>
        internal static bool TryIssue(GamePlayer player, out string text)
        {
            text = null;
            if (player?.PublicInfo == null)
                return false;

            float now = Now;
            float window = QuotaWindowSeconds?.Value ?? 300f;
            IssuedTimes.RemoveAll(t => now - t > window);

            int pid = player.PublicInfo.PlayerId;

            // 顺序：**总配额卖空优先于"个人冷却中"** —— 配额是全局状态，
            // 先告诉玩家"没货了"比"你还在冷却"更有信息量。
            int max = QuotaMax?.Value ?? 4;
            if (IssuedTimes.Count >= max)
            {
                text = Text("SodaQuota");
                return false;
            }

            float cd = IssueCooldown?.Value ?? 240f;
            float last;
            if (cd > 0f && LastIssue.TryGetValue(pid, out last) && now - last < cd)
            {
                text = Text("SodaCooldown");
                return false;
            }

            int from = SodaIdFrom?.Value ?? 3001;
            int to = SodaIdTo?.Value ?? 3005;
            if (to < from)
                to = from;
            int id = from + UnityEngine.Random.Range(0, to - from + 1);

            if (!ItemGrant.Give(player, id, out bool dropped))
                return false;

            IssuedTimes.Add(now);
            LastIssue[pid] = now;

            float sec = SodaSeconds?.Value ?? 30f;
            float mul = SodaSpeedMul?.Value ?? 1.8f;

            // 回执只讲"命令的结果"：申领到了什么、怎么用。
            // "手上满所以掉地上了"不是命令结果，不进回执（日志里有）。
            text = Text("SodaTaken")
                 + "\n" + Text("SodaHowTo", "sec", sec.ToString("F0"), "mul", (mul * 100f).ToString("F0"));

            Plugin.Log.LogInfo(
                $"[HS] SodaBoost：玩家 #{pid} 申领了汽水 {id}" +
                (dropped ? "（手上已有物品，已落在脚下）" : "") +
                $"，本窗口内已发 {IssuedTimes.Count}/{max} 瓶。");
            return true;
        }

        private static string Text(string key, params string[] pairs)
            => HideAndSeek.Features.Rule.CommandFeature.Text(key, pairs);

        // ══ 喝了汽水 ════════════════════════════════════════════════════

        [HarmonyPatch(typeof(GamePlayer), "UseItem")]
        internal static class UseItemHook
        {
            /// <summary>
            /// 两罐汽水不叠加：有效期内的第二次使用在这里被**吞掉** —— 整个原方法跳过，
            /// 于是不播音效、不消耗（IsConsumable 的 RemoveHand 不会执行）、不重新计时，
            /// 也不动 CanUseItem（否则之后 2 秒会被原版 DelayUseItem 挡住，玩家更莫名其妙）。
            /// </summary>
            [HarmonyPrefix]
            private static bool Prefix(GamePlayer __instance, GameItem item)
            {
                if (ModeRuntime.Bypass)
                    return true;
                if (__instance?.PublicInfo == null || item?.Data == null)
                    return true;

                int id = item.Data.DataId;
                if (id < (SodaIdFrom?.Value ?? 3001) || id > (SodaIdTo?.Value ?? 3005))
                    return true;                          // 不是汽水

                int pid = __instance.PublicInfo.PlayerId;
                float expireAt;
                if (!Active.TryGetValue(pid, out expireAt) || Now >= expireAt)
                    return true;                          // 没有生效中的加速 → 正常喝

                int left = (int)(expireAt - Now) + 1;
                // 静默吞掉：这是"使用物品"的动作，不是命令的报告（日志里有）。
                Plugin.Log.LogInfo(
                    $"[HS] SodaBoost：玩家 #{pid} 在有效期（还剩 {left} 秒）内又用了汽水，已吞掉。");
                return false;
            }

            [HarmonyPostfix]
            private static void Postfix(GamePlayer __instance, GameItem item)
            {
                Diagnostics.Hit("SodaBoost");
                if (ModeRuntime.Bypass)
                    return;
                if (__instance?.PublicInfo == null || item?.Data == null)
                    return;

                int id = item.Data.DataId;
                if (id < (SodaIdFrom?.Value ?? 3001) || id > (SodaIdTo?.Value ?? 3005))
                    return;

                float seconds = SodaSeconds?.Value ?? 30f;
                int pid = __instance.PublicInfo.PlayerId;
                Active[pid] = Now + seconds;

                // 立刻重算并下发：否则要等下一次 RefreshSpeed（站定/起跑）才看得到效果
                __instance.BuffComponent?.RefreshSpeed();
                __instance.SendChangeSpeed();

                // 静默：喝下汽水是"使用物品"，不是命令的报告（日志里有）
                Plugin.Log.LogInfo(
                    $"[HS] SodaBoost：玩家 #{pid} 喝下了汽水 {id}，{seconds:F0} 秒内移速 ×{SodaSpeedMul?.Value ?? 1.8f:F2}。");
            }
        }

        // ══ 加速生效 ════════════════════════════════════════════════════

        [HarmonyPatch(typeof(BuffComponent), nameof(BuffComponent.RefreshSpeed))]
        internal static class RefreshSpeedHook
        {
            [HarmonyPostfix]
            private static void Postfix(BuffComponent __instance)
            {
                if (ModeRuntime.Bypass)
                    return;

                var player = __instance?.Owner;
                if (player?.PublicInfo == null)
                    return;
                // 幽灵：MakeSpectatorGhost 置 Hide 并直接设速度，别掺和
                if (!player.IsAlive || player.State == EPlayerState.Hide)
                    return;

                float expireAt;
                if (!Active.TryGetValue(player.PublicInfo.PlayerId, out expireAt) || Now >= expireAt)
                    return;

                player.PrivateInfo.Speed *= SodaSpeedMul?.Value ?? 1.8f;
                player.SendChangeSpeed();
            }
        }

        // ══ 到期恢复 ════════════════════════════════════════════════════

        [HarmonyPatch(typeof(GameRoom), "SurvivalTick")]
        internal static class TickHook
        {
            [HarmonyPostfix]
            private static void Postfix()
            {
                if (ModeRuntime.Bypass || Active.Count == 0)
                    return;

                float now = Now;
                List<int> expired = null;
                foreach (var kv in Active)
                {
                    if (now >= kv.Value)
                        (expired ?? (expired = new List<int>())).Add(kv.Key);
                }
                if (expired == null)
                    return;

                foreach (int pid in expired)
                    Active.Remove(pid);

                var room = GameRoom.Instance;
                if (room?.Players == null)
                    return;

                foreach (var player in room.Players)
                {
                    if (player?.PublicInfo == null)
                        continue;
                    if (!expired.Contains(player.PublicInfo.PlayerId))
                        continue;

                    // 此刻该玩家已不在 Active 里，RefreshSpeed 会按原版重算基础速度，
                    // 本类的 Postfix 也不再乘倍率，然后下发给他即可。
                    player.BuffComponent?.RefreshSpeed();
                    player.SendChangeSpeed();
                    Plugin.Log.LogInfo($"[HS] SodaBoost：玩家 #{player.PublicInfo.PlayerId} 的汽水效果结束。");
                }
            }
        }

        // ══ 生命周期 ════════════════════════════════════════════════════

        [HarmonyPatch(typeof(GameRoom), "StartSurvive")]
        internal static class StartHook
        {
            [HarmonyPostfix]
            private static void Postfix() => Active.Clear();
        }

        [HarmonyPatch(typeof(GameRoom), "StartLobby")]
        internal static class LobbyHook
        {
            [HarmonyPostfix]
            private static void Postfix() => Active.Clear();
        }

        private static void Reply(GamePlayer player, string text)
        {
            if (player?.Session == null || string.IsNullOrEmpty(text))
                return;

            player.Session.Send(new S_CHAT_MESSAGE
            {
                Type = EChatType.NormalChat,
                DeviceId = 0,
                Text = text,
                PlayerId = player.PublicInfo?.PlayerId ?? 0,
                Time = (int)Now,
                IsDead = false
            });
        }
    }
}
