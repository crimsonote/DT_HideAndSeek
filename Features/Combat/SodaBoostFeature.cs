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

        /// <summary>PlayerId → 加速到期时刻（SurviveTime 秒）。</summary>
        private static readonly Dictionary<int, float> Active = new Dictionary<int, float>();

        private static float Now => TimeManager.Instance?.SurviveTime ?? 0f;

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
                Plugin.Log.LogInfo(
                    $"[HS] SodaBoost：玩家 #{pid} 在有效期（还剩 {left} 秒）内又用了汽水，已吞掉。");

                Reply(__instance,
                    HideAndSeek.Features.Rule.CommandFeature.Text("SodaAlready", "sec", left.ToString()));
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

                Plugin.Log.LogInfo(
                    $"[HS] SodaBoost：玩家 #{pid} 喝下了汽水 {id}，{seconds:F0} 秒内移速 ×{SodaSpeedMul?.Value ?? 1.8f:F2}。");

                Reply(__instance, HideAndSeek.Features.Rule.CommandFeature.Text("SodaDrunk", "sec", seconds.ToString("F0")));
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
