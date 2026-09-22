using BepInEx.Configuration;
using HarmonyLib;
using HideAndSeek.Core;
using GamePlayer = Server.Game.Player;

namespace HideAndSeek.Features.Combat
{
    /// <summary>
    /// 缩短黑方出刀冷却。
    ///
    /// 唯一真源在服务端：Server.Game.Player.StartWeaponCooltime（:176153）
    /// 负责 CanAttack = false → PushSurvivalJob(seconds - 1, → CanAttack = true)。
    /// 调用点共两处有意义：
    ///   - ConsumeKillAndRearm（:176170）击杀后 → StartWeaponCooltime(20)
    ///   - DelayAcquireWeapon（:176148）拔刀后 → StartWeaponCooltime(5)
    /// 本功能只替换 20 那一支，保留拔刀后的 5 秒保护期。
    ///
    /// 注意 Define.BLACK_REARM_DELAY_SECOND 是 const（编译期内联），运行时改不到，
    /// 只能改调用点；命令行的冷却倒计时由服务端下发的 S_COOLTIME_WEAPON 驱动，
    /// 因此改这里所有客户端 UI 会自动一致，无需客户端补丁。
    /// </summary>
    [HarmonyPatch(typeof(GamePlayer), "StartWeaponCooltime")]
    [PatchFeature(
        section: "WeaponCooldown",
        description: "缩短黑方出刀冷却：击杀后重新可出刀的时间（原版 20 秒）。",
        defaultEnabled: true,
        side: FeatureSide.Host)]
    internal static class WeaponCooldownFeature
    {
        /// <summary>原版击杀后重装冷却秒数，用于区分调用点。</summary>
        private const int VanillaRearmSeconds = 20;

        [ConfigField(10, "击杀后重新可出刀的冷却秒数。原版为 20，游戏内平衡建议 5~15。", Min = 1f, Max = 600f)]
        public static ConfigEntry<int> RearmSeconds;

        [HarmonyPrefix]
        private static void Prefix(ref int seconds)
        {
            if (ModeRuntime.Bypass)
                return;
            if (seconds != VanillaRearmSeconds)
                return;   // 拔刀后的 5 秒锁等其它调用点保持原版

            int value = RearmSeconds?.Value ?? VanillaRearmSeconds;
            seconds = value < 1 ? 1 : value;
        }
    }
}
