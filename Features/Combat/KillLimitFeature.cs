using BepInEx.Configuration;
using HarmonyLib;
using Server.Game;
using HideAndSeek.Core;

namespace HideAndSeek.Features.Combat
{
    /// <summary>
    /// 解除黑方击杀次数上限。
    ///
    /// 原版 GameRoom.BlackKillLimit（:169552）：开局人数 < Define.BLACK_DOUBLE_KILL_MIN_PLAYER
    /// （常量 6，:93772）时为 1 次，否则 2 次。该值在 InsertWeapon（:172701）写入 player.RemainKill，
    /// ConsumeKillAndRearm（:176170）每击杀一人自减一次，归零后 CanAttack 永久为 false
    /// —— 表现为"刀废了、再也砍不动"。
    ///
    /// 捉迷藏要求黑方持续淘汰玩家（直至只剩露娜系），因此这里直接改写上限。
    /// </summary>
    [HarmonyPatch(typeof(GameRoom), "get_BlackKillLimit")]
    [PatchFeature(
        section: "KillLimit",
        description: "解除黑方击杀上限：原版每人最多 1~2 杀，归零后无法再出刀。",
        defaultEnabled: true,
        side: FeatureSide.Host)]
    internal static class KillLimitFeature
    {
        [ConfigField(9999, "黑方持有武器后的可击杀次数上限。原版为 1~2；设为极大值即等于无限。", Min = 1f)]
        public static ConfigEntry<int> MaxKills;

        [HarmonyPrefix]
        private static bool Prefix(ref int __result)
        {
            if (ModeRuntime.Bypass)
                return true;

            int value = MaxKills?.Value ?? 9999;
            __result = value < 1 ? 1 : value;
            return false;
        }
    }
}
