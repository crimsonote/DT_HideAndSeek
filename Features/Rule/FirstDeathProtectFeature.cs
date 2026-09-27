using BepInEx.Configuration;
using HarmonyLib;
using HideAndSeek.Core;
using Protocol;
using Server.Game;
using GamePlayer = Server.Game.Player;

namespace HideAndSeek.Features.Rule
{
    /// <summary>
    /// 连续对局的首刀保护：**上一局第一个死亡的玩家**，在本局「有其他人死亡」之前不会被杀。
    ///
    /// 为什么需要：连续开多局时，每局开局黑方都知道位置，上一局最先倒的人很容易被连杀，
    /// 体验很差。给他一段"开局免死"，直到本局出现第一个死亡为止。
    ///
    /// 拦截点选在 <c>OnDamaged</c> 的 Prefix（**服务端吞刀**）：
    /// ```
    /// :175940  OnDamaged(Player attacker, ItemData weapon)
    /// :175949      else if (weapon.Type == EItemType.Weapon)      ← 真武器
    /// :175951          CancelTeleport(); Session.Send(S_STOP_CONTROL());
    /// :175953          GameRoom.Instance.PushAfter(400, delegate  ← 先延迟 400ms
    /// :175961              OnDead(attacker, EDeathType.Murder);   ← 死亡在这里结算
    /// ```
    /// 在 Prefix 里直接 <c>return false</c> ⇒ 连那 400ms 延迟、停控、命中音效都不会发生。
    /// 若改去拦 <c>OnDead</c>，就得回滚一堆已经发出去的副作用，得不偿失。
    ///
    /// 跨局标识用 <b>AccountID</b>（string）而不是 PlayerId —— 座位号跨局会复用
    /// （HideAndSeek/AGENTS.md「改默认值」一节记过同类事故）。
    ///
    /// 「本局是否已有人死」不自己维护标志：原版 <c>GameRoom.DeadPlayers</c> 就是死亡名单
    /// （<c>:175977 DeadPlayers.Insert(0, this)</c>，插在头部），而它在
    /// <c>FullReset</c> / <c>StartLobby</c> / <c>StartPick</c> 三处都会 <c>Clear()</c>
    /// ⇒ 每局开始必然为空 ⇒ 在 <c>OnDead</c> 的 Prefix 里看 <c>Count == 0</c>
    /// 就能认出"本局第一个死者"（Prefix 跑在 Insert 之前）。
    /// </summary>
    [PatchFeature("FirstDeathProtect",
        "首刀保护：上一局第一个死亡的玩家，在本局有人死亡之前不会被杀（服务端吞刀），并给黑方提示。",
        defaultEnabled: false, side: FeatureSide.Host)]
    internal static class FirstDeathProtectFeature
    {
        [ConfigField(true, "启用首刀保护。")]
        public static ConfigEntry<bool> ProtectEnabled;

        [ConfigField(true, "目标处于保护中时，给攻击者（黑方）发一条提示。")]
        public static ConfigEntry<bool> NotifyAttacker;

        /// <summary>上一局第一个死亡者的 AccountID（空 = 无）。</summary>
        private static string _lastFirstDeadAccount = "";

        /// <summary>本局的受保护者 AccountID（开局时从上面那个搬过来）。</summary>
        private static string _protectedAccount = "";

        /// <summary>本局保护是否已解除（有人死亡、或命中兜底条件）。开局时按上面两个字段重算。</summary>
        private static bool _released = true;

        /// <summary>本局是否处于生存阶段（防止在大厅/审判阶段的死亡被误记）。</summary>
        private static bool InSurvive()
            => GameRoom.Instance?.State == EGameState.Survive;

        // ── ① 开局：把"上一局第一个死者"搬成本局的受保护者 ──
        [HarmonyPatch(typeof(GameRoom), "StartSurvive")]
        internal static class StartHook
        {
            [HarmonyPostfix]
            private static void Postfix()
            {
                _protectedAccount = _lastFirstDeadAccount;
                _released = string.IsNullOrEmpty(_protectedAccount);

                if (_released)
                {
                    Plugin.Log.LogInfo("[HS] FirstDeathProtect：本局无保护对象（上一局没人死亡）。");
                }
                else
                {
                    Plugin.Log.LogInfo(
                        $"[HS] FirstDeathProtect：本局保护上一局第一个死亡者（Account={_protectedAccount}）。");
                }
            }
        }

        // ── ② 记录本局第一个死者（作为下一局的保护对象），并解除本局保护 ──
        [HarmonyPatch(typeof(GamePlayer), nameof(GamePlayer.OnDead))]
        internal static class DeadHook
        {
            [HarmonyPrefix]
            private static void Prefix(GamePlayer __instance)
            {
                if (ModeRuntime.Bypass || __instance == null) return;
                if (!InSurvive()) return;

                var room = GameRoom.Instance;
                int deadCount = room.DeadPlayers != null ? room.DeadPlayers.Count : 0;

                if (deadCount == 0)
                {
                    // Prefix 跑在 Insert(0, this) 之前 ⇒ Count==0 说明他就是本局第一个走的
                    _lastFirstDeadAccount = __instance.AccountID ?? "";
                    Plugin.Log.LogInfo(
                        $"[HS] FirstDeathProtect：本局第一个死亡者 = {__instance.Name}（Account={_lastFirstDeadAccount}），" +
                        "下一局将受保护。");
                }

                // 有人死了（不论是谁）⇒ 本局保护结束。
                // 包括"受保护者本人死了"这种漏网情况 —— 那时保护显然也该结束。
                if (!_released)
                {
                    _released = true;
                    Plugin.Log.LogInfo("[HS] FirstDeathProtect：本局已有他人死亡，保护解除。");
                }
            }
        }

        // ── ③ 吞刀：受保护者被真武器攻击时直接吞掉 ──
        [HarmonyPatch(typeof(GamePlayer), nameof(GamePlayer.OnDamaged))]
        internal static class DamagedHook
        {
            [HarmonyPrefix]
            private static bool Prefix(GamePlayer __instance, GamePlayer attacker)
            {
                if (ModeRuntime.Bypass) return true;
                if (ProtectEnabled == null || !ProtectEnabled.Value) return true;

                // 没有保护对象 / 保护已解除 ⇒ 不干预
                if (_released || string.IsNullOrEmpty(_protectedAccount)) return true;

                // 只保护那一个人
                if (__instance?.AccountID != _protectedAccount) return true;

                // 攻击者必须真活着（死人的幽灵不该触发提示）
                if (attacker?.PublicInfo == null || !attacker.IsAlive) return true;

                // 兜底：存活白方只剩他一个 ⇒ 解除保护。
                // 否则黑方永远砍不死他、对局无法结束（这是本功能唯一的"必须"边界处理）。
                int aliveWhite = 0;
                var room = GameRoom.Instance;
                if (room?.Players != null)
                {
                    for (int i = 0; i < room.Players.Count; i++)
                    {
                        var p = room.Players[i];
                        if (p?.PublicInfo == null || !p.IsAlive) continue;
                        if (p.Color == EPlayerColor.White) aliveWhite++;
                    }
                }

                if (aliveWhite <= 1)
                {
                    _released = true;
                    Plugin.Log.LogInfo("[HS] FirstDeathProtect：存活白方只剩受保护者一人，强制解除保护（否则对局无法结束）。");
                    return true;
                }

                if (NotifyAttacker == null || NotifyAttacker.Value)
                {
                    try
                    {
                        ChatOut.ToPlayer(attacker, $"{__instance.Name} 处于首刀保护中，暂时无法击杀。");
                    }
                    catch (global::System.Exception ex)
                    {
                        Plugin.Log.LogWarning($"[HS] FirstDeathProtect：提示发送失败 — {ex.Message}");
                    }
                }

                Plugin.Log.LogInfo(
                    $"[HS] FirstDeathProtect：吞掉对 {__instance.Name} 的一刀（攻击者 {attacker.Name}）。");

                return false;   // ← 吞刀：不走原版的延迟结算，也就没有死亡
            }
        }
    }
}
