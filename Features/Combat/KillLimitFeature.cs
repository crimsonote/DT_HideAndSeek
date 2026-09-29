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

        private static bool _logged;

        [HarmonyPrefix]
        // 上游 DT_Tools 的 BlackAttackFeature 也 Prefix 同一个 getter，同样是"写 __result 后返回 false"。
        // Harmony 只让**第一个**返回 false 的 Prefix 生效，所以先后顺序直接决定谁说了算；
        // 默认优先级相同时按补丁挂载次序排，会随插件加载顺序漂移。这里固定本模块先跑：
        // 捉迷藏模式开着就由本模块接管；模式关闭时下面的 Bypass 分支返回 true，
        // 上游那条照常生效。⇒ 两种情况下结果都不再依赖加载顺序。
        [HarmonyPriority(Priority.First)]
        private static bool Prefix(ref int __result)
        {
            Diagnostics.Hit("KillLimit");
            if (ModeRuntime.Bypass)
                return true;

            int value = MaxKills?.Value ?? 9999;
            __result = value < 1 ? 1 : value;

            // 首次读取时记一次，用于确认本补丁是否真的被游戏调用到
            if (!_logged)
            {
                _logged = true;
                Plugin.Log.LogInfo($"[HS] KillLimit：BlackKillLimit → {__result}（原版：单人 1 / 多人 2）。");
            }
            return false;
        }
    }
}
