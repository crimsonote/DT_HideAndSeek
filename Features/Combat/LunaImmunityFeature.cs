using System.Linq;
using BepInEx.Configuration;
using HarmonyLib;
using Protocol;
using Server.Game;
using HideAndSeek.Core;
using GamePlayer = Server.Game.Player;

namespace HideAndSeek.Features.Combat
{
    /// <summary>
    /// 露娜免疫（服务端权威）。
    ///
    /// 原版护盾是**纯客户端判定**：MyPlayer.UseWeaponItem（:13637）调用本地 HasLunaShield（:14414），
    /// 而服务端 UseWeapon（:176263）从不校验护盾。这带来两个后果：
    ///   1) 作弊客户端可以无视护盾直接刀露娜；
    ///   2) BlackVisionFeature 让黑方客户端 Darkness = true，会使 HasLunaShield 直接返回 false。
    /// 因此把护盾搬到服务端，并保留原版的两种破防手段：
    ///   - 致命诡计（DT）：走 UseDeadlyTrick（:176314）另一条路径，原版即不检查护盾 —— 本功能不动它；
    ///   - 真停电：以服务端真实区域光照为准，只有真的断开 2 个电箱才放行。
    /// </summary>
    [HarmonyPatch(typeof(GamePlayer), nameof(GamePlayer.UseWeapon))]
    [PatchFeature(
        section: "LunaImmunity",
        description: "露娜服务端免疫：普通刀杀对露娜及其能力持有者无效（致命诡计与真实停电仍可破防）。",
        defaultEnabled: true,
        side: FeatureSide.Host)]
    internal static class LunaImmunityFeature
    {
        [ConfigField(true, "拦截时给攻击者播放失败音效（原版护盾音效不在服务端音效枚举中，故用 FailedSfx）。")]
        public static ConfigEntry<bool> PlayFeedback;

        [HarmonyPrefix]
        private static bool Prefix(GamePlayer __instance, int targetId)
        {
            Diagnostics.Hit("LunaImmunity");
            if (ModeRuntime.Bypass)
                return true;

            var room = GameRoom.Instance;
            var target = room?.Players?.FirstOrDefault(p => p.PublicInfo.PlayerId == targetId);
            if (target == null)
                return true;                      // 交给原版做其余校验
            if (!GameRefs.IsLunaSide(target))
                return true;                      // 目标不是露娜系
            if (GameRefs.IsRealBlackout(__instance))
                return true;                      // 真停电：原版规则下护盾失效

            // 拒绝动作（收刀 + 失败音效 + 日志）与首刀保护共用同一出口，避免两套逐字拷贝
            KillBlocker.Reject(
                __instance, target, "LunaImmunity",
                PlayFeedback == null || PlayFeedback.Value,
                $"（目标角色={target.CharacterId}，技能={target.SkillComponent?.Data?.Type}）");
            return false;
        }
    }
}
