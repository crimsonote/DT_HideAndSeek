using System.Linq;
using BepInEx.Configuration;
using HarmonyLib;
using Protocol;
using Server.Game;
using HideAndSeek.Core;
using GamePlayer = Server.Game.Player;

namespace HideAndSeek.Features.Weapon
{
    /// <summary>
    /// 开局武器供给：自行跑刀（原版）或开局直接发一把武器。
    ///
    /// 原版机制：玩家到武器架拔刀 → ItemManager.InsertWeapon（:172688）
    /// 把他涂成 Black 并写入 RemainKill = BlackKillLimit（:172701）。
    /// 因此"开局发刀"等价于替某名玩家提前完成武器入包 ——
    /// ItemManager.CreateAndInsertInven 内部对 2000~2999 的武器走同一条 InsertWeapon 分支。
    ///
    /// 时机：GameRoom.StartSurvive（:171277，private）之后延迟若干毫秒。
    /// 该方法用 SyncAllPlayer 等待全体客户端加载完成，立即发刀可能早于客户端就绪。
    /// </summary>
    [HarmonyPatch(typeof(GameRoom), "StartSurvive")]
    [PatchFeature(
        section: "WeaponGrant",
        description: "开局武器供给：自行跑刀（原版）或开局直接给随机一名玩家发一把武器。",
        defaultEnabled: true,
        side: FeatureSide.Host)]
    internal static class WeaponGrantFeature
    {
        [ConfigField(false, "开启后开局直接发放武器；关闭则保持原版，由玩家自行去武器架跑刀。")]
        public static ConfigEntry<bool> GiveAtStart;

        [ConfigField(2001, "发放的武器 ID。2001 刀 / 2002 棒 / 2003 锤 / 2004 铲 / 2005 手风琴。", Min = 2000f, Max = 2999f)]
        public static ConfigEntry<int> WeaponId;

        [ConfigField(2500, "发刀延迟毫秒数（等待全体客户端进入对局）。", Min = 0f, Max = 30000f)]
        public static ConfigEntry<int> GrantDelayMs;

        [HarmonyPostfix]
        private static void Postfix(GameRoom __instance)
        {
            Diagnostics.Hit("WeaponGrant");
            if (ModeRuntime.Bypass)
                return;
            if (GiveAtStart == null || !GiveAtStart.Value)
                return;

            int delay = GrantDelayMs?.Value ?? 2500;
            __instance.PushAfter(delay < 0 ? 0 : delay, () => Grant(__instance));
        }

        private static void Grant(GameRoom room)
        {
            if (room == null || room.State != EGameState.Survive)
                return;

            var candidates = room.Players
                .Where(p => p?.PublicInfo != null && p.IsAlive && !p.IsSpectator && !p.IsDummy)
                .ToList();

            if (candidates.Count == 0)
                return;

            var target = candidates[Util.GetRandomNumber(0, candidates.Count)];
            int weaponId = WeaponId?.Value ?? Define.ITEM_ID_KNIFE;

            ItemManager.Instance.CreateAndInsertInven(target, weaponId);

            Plugin.Log.LogInfo(
                $"[HS] WeaponGrant：已向随机玩家 #{target.PublicInfo.PlayerId} 发放武器 {weaponId}（开局发刀模式）。");
        }

        [ConfigField(true, "自动发刀后禁止其他人再从地图武器架取刀（否则会出现两个黑方）。")]
        public static ConfigEntry<bool> BlockFurtherWeapons;

        /// <summary>
        /// 自动发刀模式下地图仍会刷刀 —— 不拦的话第二个拿到刀的人也会变黑。
        /// InsertWeapon（:172688）是"变黑"的唯一入口（:172694 设 Color = Black），在这里拒绝即可。
        /// </summary>
        [HarmonyPatch(typeof(ItemManager), nameof(ItemManager.InsertWeapon))]
        internal static class BlockSecondWeaponHook
        {
            [HarmonyPrefix]
            private static bool Prefix(GamePlayer player)
            {
                if (ModeRuntime.Bypass)
                    return true;
                if (BlockFurtherWeapons == null || !BlockFurtherWeapons.Value)
                    return true;
                if (GiveAtStart == null || !GiveAtStart.Value)
                    return true;                       // 只在自动发刀模式下生效
                if (player == null || player.Color == EPlayerColor.Black)
                    return true;

                var room = GameRoom.Instance;
                if (room == null)
                    return true;

                bool hasBlack = room.Players.Any(p => p?.PublicInfo != null
                    && (p.Color == EPlayerColor.Black || p.Color == EPlayerColor.Dark));
                if (!hasBlack)
                    return true;

                Plugin.Log.LogInfo(
                    $"[HS] WeaponGrant：已有黑方，拒绝 #{player.PublicInfo.PlayerId} 再从武器架取刀。");
                return false;
            }
        }
    }
}
