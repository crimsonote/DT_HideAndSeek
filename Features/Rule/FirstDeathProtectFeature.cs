using System.Linq;
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
    /// 为什么需要：连续开多局时每局开局黑方都知道位置，上一局最先倒的人很容易被连杀。
    ///
    /// 与 <see cref="HideAndSeek.Features.Combat.LunaImmunityFeature"/>（露娜免疫）的关系：
    /// **判据与生命周期本质不同**（那边看角色/技能属性、永久；这边看跨局记住的 AccountID、
    /// 到本局有人死为止），所以是两个独立功能、各自一个段开关。
    /// 但"拒绝一次攻击"的动作两边完全一样 ⇒ 共用 <see cref="KillBlocker.Reject"/>，
    /// 拦截点也刻意选同一个（<c>GamePlayer.UseWeapon</c> 的 Prefix）。
    ///
    /// 拦截点为什么是 <c>UseWeapon</c> 而不是 <c>OnDamaged</c>：
    /// ```
    /// :176263  UseWeapon(int targetId)        ← 服务端校验入口（本功能拦这里）
    /// :175940  OnDamaged(attacker, weapon)    ← 再往下才是它，且里面 PushAfter(400ms) 才结算死亡
    /// ```
    /// 拦在更前面，连那 400ms 延迟结算、停控、命中音效都不会发生。
    ///
    /// 跨局标识用 **AccountID（string）**，不用 PlayerId —— 座位号跨局会复用
    /// （HideAndSeek/AGENTS.md 记过同类事故）。
    ///
    /// 「本局是否已有人死」不自己维护标志：原版 <c>GameRoom.DeadPlayers</c> 就是死亡名单
    /// （<c>:175977 Insert(0, this)</c> 插在头部），而它在 <c>FullReset</c> / <c>StartLobby</c> /
    /// <c>StartPick</c> 三处都会 <c>Clear()</c> ⇒ 每局开始必为空 ⇒
    /// 在 <c>OnDead</c> 的 Prefix 里看 <c>Count == 0</c> 就能认出"本局第一个死者"（Prefix 早于 Insert）。
    /// </summary>
    [PatchFeature("FirstDeathProtect",
        "首刀保护：上一局第一个死亡的玩家，在本局有人死亡之前不会被杀（服务端权威；真停电与致命诡计照露娜规则可破防）。",
        defaultEnabled: false, side: FeatureSide.Host)]
    internal static class FirstDeathProtectFeature
    {
        [ConfigField(true, "启用首刀保护。")]
        public static ConfigEntry<bool> ProtectEnabled;

        [ConfigField(true, "拦截时给攻击者播放失败音效（与露娜免疫一样用 FailedSfx）。")]
        public static ConfigEntry<bool> PlayFeedback;

        [ConfigField(true, "目标处于保护中时，给攻击者发一条文字提示（露娜免疫没有这条，是本功能独有）。")]
        public static ConfigEntry<bool> NotifyAttacker;

        [ConfigField(true, "照露娜规则：**真停电**时保护失效（给黑方单发的假黑灯不算，以服务端区域光照为准）。")]
        public static ConfigEntry<bool> BreakOnBlackout;

        [ConfigField(true, "照露娜规则：**致命诡计（DT）**无视保护。关掉则连 DT 也会被保护挡下。")]
        public static ConfigEntry<bool> BreakOnDeadlyTrick;

        /// <summary>上一局第一个死亡者的 AccountID（空 = 无）。</summary>
        private static string _lastFirstDeadAccount = "";

        /// <summary>本局的受保护者 AccountID（开局时从上面那个搬过来）。</summary>
        private static string _protectedAccount = "";

        /// <summary>本局保护是否已解除。开局时按上面两个字段重算。</summary>
        private static bool _released = true;

        private static bool Enabled()
            => !ModeRuntime.Bypass && ProtectEnabled != null && ProtectEnabled.Value;

        private static bool InSurvive()
            => GameRoom.Instance?.State == EGameState.Survive;

        private static bool SfxOn()
            => PlayFeedback == null || PlayFeedback.Value;

        /// <summary>该玩家此刻是否是本局的受保护者（不含破防判断）。</summary>
        private static bool IsProtected(GamePlayer player)
            => !_released
               && !string.IsNullOrEmpty(_protectedAccount)
               && player?.AccountID == _protectedAccount;

        /// <summary>照露娜规则：真停电时保护失效。</summary>
        private static bool BlackoutBreaks(GamePlayer attacker)
            => (BreakOnBlackout == null || BreakOnBlackout.Value) && GameRefs.IsRealBlackout(attacker);

        /// <summary>
        /// 死锁兜底：存活白方只剩受保护者一人时解除保护。
        /// 不做这一步，黑方永远砍不死他、对局无法结束 —— 这是死锁，不是"防御性代码"。
        /// </summary>
        private static bool OnlyProtectedWhiteLeft(GamePlayer target)
        {
            var room = GameRoom.Instance;
            if (room?.Players == null) return false;

            int aliveWhite = 0;
            for (int i = 0; i < room.Players.Count; i++)
            {
                var p = room.Players[i];
                if (p?.PublicInfo == null || !p.IsAlive) continue;
                if (p.Color == EPlayerColor.White) aliveWhite++;
            }

            if (aliveWhite > 1) return false;

            _released = true;
            Plugin.Log.LogInfo(
                $"[HS] FirstDeathProtect：存活白方只剩受保护者（{target?.Name}）一人，强制解除保护。");
            return true;
        }

        /// <summary>本功能私有的那条文字提示（露娜免疫没有）。</summary>
        private static void NotifyBlocked(GamePlayer attacker, GamePlayer target)
        {
            if (NotifyAttacker != null && !NotifyAttacker.Value) return;
            try { ChatOut.ToPlayer(attacker, $"{target.Name} 处于首刀保护中，暂时无法击杀。"); }
            catch { }
        }

        // ── ① 开局：把"上一局第一个死者"搬成本局的受保护者 ──
        [HarmonyPatch(typeof(GameRoom), "StartSurvive")]
        internal static class StartHook
        {
            [HarmonyPostfix]
            private static void Postfix()
            {
                _protectedAccount = _lastFirstDeadAccount;
                _released = string.IsNullOrEmpty(_protectedAccount);

                Plugin.Log.LogInfo(_released
                    ? "[HS] FirstDeathProtect：本局无保护对象（上一局没人死亡）。"
                    : $"[HS] FirstDeathProtect：本局保护上一局第一个死亡者（Account={_protectedAccount}）。");
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
                        $"[HS] FirstDeathProtect：本局第一个死亡者 = {__instance.Name}" +
                        $"（Account={_lastFirstDeadAccount}），下一局将受保护。");
                }

                if (!_released)
                {
                    _released = true;
                    Plugin.Log.LogInfo("[HS] FirstDeathProtect：本局已有他人死亡，保护解除。");
                }
            }
        }

        // ── ③ 普通刀：吞刀（与露娜免疫同一个拦截点，判据不同） ──
        [HarmonyPatch(typeof(GamePlayer), nameof(GamePlayer.UseWeapon))]
        internal static class WeaponHook
        {
            [HarmonyPrefix]
            private static bool Prefix(GamePlayer __instance, int targetId)
            {
                if (!Enabled()) return true;

                var target = GameRoom.Instance?.Players?
                    .FirstOrDefault(p => p.PublicInfo.PlayerId == targetId);
                if (target == null) return true;              // 交给原版做其余校验
                if (!IsProtected(target)) return true;
                if (BlackoutBreaks(__instance)) return true;  // 照露娜：真停电破防
                if (OnlyProtectedWhiteLeft(target)) return true;

                Diagnostics.Hit("FirstDeathProtect");
                NotifyBlocked(__instance, target);
                KillBlocker.Reject(__instance, target, "FirstDeathProtect", SfxOn(), "（普通刀）");
                return false;
            }
        }

        // ── ④ 致命诡计（DT）：默认照露娜规则放行，可配置成也挡 ──
        [HarmonyPatch(typeof(GamePlayer), nameof(GamePlayer.UseDeadlyTrick), new[] { typeof(C_DEADLY_TRICK) })]
        internal static class DeadlyTrickHook
        {
            [HarmonyPrefix]
            private static bool Prefix(GamePlayer __instance, C_DEADLY_TRICK pkt)
            {
                if (!Enabled()) return true;
                if (BreakOnDeadlyTrick == null || BreakOnDeadlyTrick.Value) return true;   // 照露娜：DT 破防

                var target = GameRoom.Instance?.Players?
                    .FirstOrDefault(p => p.PublicInfo.PlayerId == pkt.TargetId);
                if (target == null) return true;
                if (!IsProtected(target)) return true;
                if (BlackoutBreaks(__instance)) return true;
                if (OnlyProtectedWhiteLeft(target)) return true;

                Diagnostics.Hit("FirstDeathProtect");
                NotifyBlocked(__instance, target);
                KillBlocker.Reject(__instance, target, "FirstDeathProtect", SfxOn(), "（致命诡计）");
                return false;
            }
        }
    }
}
