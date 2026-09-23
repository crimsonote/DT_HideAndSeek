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

        [ConfigField("", "发刀时最高优先级排除的玩家 ID（逗号分隔，如 \"1,3\"）。" +
            "若排除后真人候选为空，会自动放开假人限制在原池外重抽 —— 即宁可发给假人，也不发给被排除者。")]
        public static ConfigEntry<string> ExcludePlayerIds;

        /// <summary>该玩家是否在排除名单里。</summary>
        private static bool IsExcluded(GamePlayer player, string list)
        {
            if (string.IsNullOrWhiteSpace(list) || player?.PublicInfo == null)
                return false;

            int id = player.PublicInfo.PlayerId;
            foreach (string part in list.Split(','))
            {
                if (int.TryParse(part.Trim(), out int x) && x == id)
                    return true;
            }
            return false;
        }

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
                .Where(p => !IsExcluded(p, ExcludePlayerIds?.Value))
                .ToList();

            // 真人池被排除空了 → 放开假人限制重抽：宁可把刀发给假人，也不发给被排除者
            if (candidates.Count == 0)
            {
                candidates = room.Players
                    .Where(p => p?.PublicInfo != null && p.IsAlive && !p.IsSpectator)
                    .Where(p => !IsExcluded(p, ExcludePlayerIds?.Value))
                    .ToList();

                if (candidates.Count > 0)
                    Plugin.Log.LogInfo("[HS] WeaponGrant：真人候选被排除后为空，已放开假人限制重抽。");
            }

            if (candidates.Count == 0)
            {
                Plugin.Log.LogWarning("[HS] WeaponGrant：排除后无任何候选，本次不发刀。");
                return;
            }

            var target = candidates[Util.GetRandomNumber(0, candidates.Count)];
            int weaponId = WeaponId?.Value ?? Define.ITEM_ID_KNIFE;

            ItemManager.Instance.CreateAndInsertInven(target, weaponId);

            // 顺手关掉可能已经开启的武器架，避免它在发刀之前就已展开
            try { Server.Game.DeviceManager.Instance?.CurrentArmory?.RefreshState(EArmoryState.EmptyArmory); }
            catch { /* 没有可关的就忽略 */ }

            Plugin.Log.LogInfo(
                $"[HS] WeaponGrant：已向随机玩家 #{target.PublicInfo.PlayerId} 发放武器 {weaponId}（开局发刀模式）。");
        }

        [ConfigField(true, "自动发刀后让武器架不再开启 —— 刀根本不会出现，因此既看不到也点不了。")]
        public static ConfigEntry<bool> BlockFurtherWeapons;

        /// <summary>
        /// 自动发刀模式下让武器架永不开启。
        ///
        /// 客户端只在 DeviceState == 1（即 OpenArmory）时才 SpawnWeapon 显示刀（:5122），
        /// 服务端 AcquireWeapon 也要求 State == 1（:161679）。而全游戏只有一处会打开它 ——
        /// DeviceManager 的"下一把刀"定时逻辑（:164186）。
        /// 拦住这一处就等于"刀不存在"：看不见、也点不了，而不是"拿了再没收"。
        /// </summary>
        [HarmonyPatch(typeof(Server.Game.Armory), nameof(Server.Game.Armory.RefreshState))]
        internal static class ArmoryLockHook
        {
            [HarmonyPrefix]
            private static bool Prefix(EArmoryState state)
            {
                if (ModeRuntime.Bypass)
                    return true;
                if (BlockFurtherWeapons == null || !BlockFurtherWeapons.Value)
                    return true;
                if (GiveAtStart == null || !GiveAtStart.Value)
                    return true;                       // 只在自动发刀模式下生效
                if (state != EArmoryState.OpenArmory)
                    return true;                       // 关闭/清空的操作照常放行

                Plugin.Log.LogInfo("[HS] WeaponGrant：自动发刀模式，武器架不开启（刀不会出现）。");
                return false;
            }
        }

        /// <summary>
        /// 兜底：万一仍有武器绕过武器架锁（迁移恢复、异常状态等）到达玩家手里，
        /// 也不能让第二个人变黑。主手段是上面的 ArmoryLockHook。
        /// </summary>
        [HarmonyPatch(typeof(ItemManager), nameof(ItemManager.InsertWeapon))]
        internal static class BlockSecondWeaponHook
        {
            [HarmonyPrefix]
            private static bool Prefix(GamePlayer player, Server.Game.Item item)
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

                // 不是"拒绝拿刀"——那样他会攥着一把没用的刀。游戏里并不存在"地上刷出的刀"，
                // 刀来自武器架交互，InsertWeapon（:172688）正是它进入玩家之手的入口，
                // 所以在这里把这把刀直接从世界移除，等价于"这把刀不存在"。
                Confiscate(player, item);
                Plugin.Log.LogInfo(
                    $"[HS] WeaponGrant：已有黑方，已没收 #{player.PublicInfo.PlayerId} 取到的武器。");
                return false;
            }

            /// <summary>把武器从玩家与世界移除。</summary>
            private static void Confiscate(GamePlayer player, Server.Game.Item item)
            {
                try
                {
                    if (item != null && ItemManager.Instance != null)
                        ItemManager.Instance.RemoveItem(item);
                    else
                        player.RemoveWeapon();
                }
                catch (global::System.Exception ex)
                {
                    Plugin.Log.LogWarning($"[HS] WeaponGrant：没收武器失败 — {ex.Message}");
                    try { player.RemoveWeapon(); } catch { /* 已尽力 */ }
                }
            }
        }
    }
}
