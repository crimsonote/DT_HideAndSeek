using BepInEx.Configuration;
using BepInEx.Logging;

namespace HideAndSeek.Core
{
    /// <summary>
    /// 配置迁移。
    ///
    /// 存在的理由：BepInEx 只在**新建** .cfg 时写入默认值，之后无论代码里的默认值怎么改，
    /// 老配置文件都保持原样。这已经造成过两次"功能看起来完全无效"的误判 ——
    /// 最近一次是 [WhiteRadar] SkipDummies 停在旧默认值 true，导致假人全被跳过、
    /// 雷达没有任何可显示目标。
    ///
    /// 做法：用一个 ConfigVersion 计数，每次针对**已确认会导致功能静默失效**的项做定向修正。
    /// 只处理白名单里的键，不碰其它任何用户设置；每项迁移都在日志里留痕。
    ///
    /// 注意：这里无法区分"用户故意设成旧值"与"从未改过"，所以白名单要足够克制 ——
    /// 只在"旧默认值必然导致功能失效"时才纳入。
    /// </summary>
    internal static class ConfigMigration
    {
        /// <summary>当前配置版本。新增迁移时 +1。</summary>
        private const int CurrentVersion = 7;

        public static void Run(ConfigFile config, ManualLogSource log)
        {
            if (config == null)
                return;

            var version = config.Bind(
                ModeRuntime.Section, "ConfigVersion", 1,
                "内部：配置迁移版本，请勿手动修改。");

            if (version.Value >= CurrentVersion)
                return;

            int from = version.Value;
            log.LogInfo($"[HS] 配置迁移：v{from} → v{CurrentVersion}");

            // v6 → v7
            if (from < 7)
            {
                // MiyukiScan.BlackPinRange 上一版默认是硬编码 900，现在默认 -1 = 跟随视野。
                // 若仍是 900（旧默认）就推进到 -1，让它随 AoiCulling.ExitRange 一起变；
                // 用户手改成别的值则保留。
                MigrateFloat(config, log, "MiyukiScan", "BlackPinRange", 900f, -1f);
            }

            // v5 → v6

            if (from < 6)
            {
                // AOI 内圈缩小到 700（外圈同比缩到 900，滞回仍为 200）：
                // 900 的进入距离让黑方太早发现白方，白方几乎没有周旋空间。
                // 注意必须按顺序：先把仍是 900/1100 的旧默认推进，再处理已迁移到 900 的 EnterRange。
                MigrateFloat(config, log, "AoiCulling", "ExitRange", 1100f, 900f);
                MigrateFloat(config, log, "AoiCulling", "EnterRange", 900f, 700f);

                // 内圈缩小后，视野升级的相对价值提高，倍率 0.5 → 0.6 作为补偿：
                // 3 级时 700 × 2.8 = 1960，接近原来 900 × 2.5 = 2250 的水平。
                MigrateFloat(config, log, "KillUpgrade", "VisionBonusPerLevel", 0.5f, 0.6f);
            }

            // v4 → v5

            if (from < 5)
            {
                // EnterRange 旧默认 750 只覆盖小地图可视半径(约1223)的 61%，
                // 观感上是"要贴很近才现身"，而 ExitRange 1100 已接近边缘，
                // 造成"进得晚、出得也晚"的不对称。改为 900（约74%），滞回带收窄到 200。
                MigrateFloat(config, log, "AoiCulling", "EnterRange", 750f, 900f);
            }

            // v3 → v4

            if (from < 4)
            {
                // WelcomeDelayMs 旧默认 2500 太短：客户端场景未加载完就发送会丢消息，
                // 用户实测要求改到 10 秒。
                MigrateInt(config, log, "Broadcast", "WelcomeDelayMs", 2500, 10000);
            }

            // v2 → v3

            if (from < 3)
            {
                // ShowWarningArrow 旧默认值 true 会在目标身上留一个 CharacterArrow 图标
                // （用户描述为"黑洞角色的图标"），观感突兀且会与 Kaho 的监视箭头抢槽位。
                // 新默认是 false，这里把老配置一并对齐。
                MigrateBool(config, log, "TeleportCommand", "ShowWarningArrow", true, false);
            }

            // v1 → v2

            if (from < 2)
            {
                // SkipDummies 旧默认值 true 会让雷达在"只有假人"的测试房里完全无效。
                // 这是纯测试向开关，覆盖无风险。
                MigrateBool(config, log, "WhiteRadar", "SkipDummies", true, false);

                // RepairThreshold 已改名为 RepairCount（语义也从"剩余阈值"改为"已修数"），
                // 旧键无法自动搬运，这里只确保新键存在且为期望默认值。
                EnsureInt(config, log, "PowerRepair", "RepairCount", 1);
            }

            version.Value = CurrentVersion;
            log.LogInfo($"[HS] 配置迁移完成（当前 v{CurrentVersion}）。");
        }

        /// <summary>把已存在的布尔键修正为指定值；键不存在则不动（交给正常的默认值绑定）。</summary>


        /// <summary>确保整型键存在且不小于期望值（新键名场景用它兜底）。</summary>
        /// <summary>
        /// 把"仍是旧默认值"的整型配置推进到新默认值。
        ///
        /// 只做这件事：**当前值恰好等于 oldDefault 时**才改写。
        /// 用户显式改过的值（例如把 EnterRange 调成 800）一律保留 ——
        /// 迁移的职责是"旧默认值已过时"，不是"替用户做决定"。
        /// </summary>
        private static void MigrateInt(ConfigFile config, ManualLogSource log,
            string section, string key, int oldDefault, int newDefault)
        {
            try
            {
                ConfigDefinition def = new ConfigDefinition(section, key);
                if (!config.ContainsKey(def))
                    return;

                var entry = config.Bind(section, key, newDefault);
                if (entry.Value != oldDefault)
                    return;                            // 用户改过 → 不动

                entry.Value = newDefault;
                log.LogInfo($"[HS]   更新 {section}.{key}：{oldDefault} → {newDefault}");
            }
            catch (global::System.Exception ex)
            {
                log.LogWarning($"[HS]   迁移 {section}.{key} 失败：{ex.Message}");
            }
        }

        /// <summary>把"仍是旧默认值"的浮点配置推进到新默认值（语义同 MigrateInt）。</summary>
        private static void MigrateFloat(ConfigFile config, ManualLogSource log,
            string section, string key, float oldDefault, float newDefault)
        {
            try
            {
                ConfigDefinition def = new ConfigDefinition(section, key);
                if (!config.ContainsKey(def))
                    return;

                var entry = config.Bind(section, key, newDefault);
                if (global::System.Math.Abs(entry.Value - oldDefault) > 0.0001f)
                    return;                            // 用户改过 → 不动

                entry.Value = newDefault;
                log.LogInfo($"[HS]   更新 {section}.{key}：{oldDefault} → {newDefault}");
            }
            catch (global::System.Exception ex)
            {
                log.LogWarning($"[HS]   迁移 {section}.{key} 失败：{ex.Message}");
            }
        }

        /// <summary>把"仍是旧默认值"的布尔配置推进到新默认值（语义同 MigrateInt）。</summary>
        private static void MigrateBool(ConfigFile config, ManualLogSource log,
            string section, string key, bool oldDefault, bool newDefault)
        {
            try
            {
                ConfigDefinition def = new ConfigDefinition(section, key);
                if (!config.ContainsKey(def))
                    return;

                var entry = config.Bind(section, key, newDefault);
                if (entry.Value != oldDefault)
                    return;                            // 用户改过 → 不动

                entry.Value = newDefault;
                log.LogInfo($"[HS]   更新 {section}.{key}：{oldDefault} → {newDefault}");
            }
            catch (global::System.Exception ex)
            {
                log.LogWarning($"[HS]   迁移 {section}.{key} 失败：{ex.Message}");
            }
        }

        private static void EnsureInt(ConfigFile config, ManualLogSource log,
            string section, string key, int expected)
        {
            try
            {
                ConfigDefinition def = new ConfigDefinition(section, key);
                if (config.ContainsKey(def))
                    return;                       // 已存在（可能来自默认值绑定）→ 不动

                config.Bind(section, key, expected);
                log.LogInfo($"[HS]   补齐 {section}.{key} = {expected}");
            }
            catch (global::System.Exception ex)
            {
                log.LogWarning($"[HS]   迁移 {section}.{key} 失败：{ex.Message}");
            }
        }
    }
}
