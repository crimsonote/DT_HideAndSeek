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
        private const int CurrentVersion = 9;

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

            // v8 → v9
            if (from < 9)
            {
                // ① 行宽：28 是本项很早的默认值（14 个汉字），改成 52（26 个汉字）。
                //    截图实测每行只有 14 字，说明 .cfg 里一直没跟上。
                MigrateFloat(config, log, "Broadcast", "MaxLineWidth", 28f, 52f);

                // ② 文案：这几项在 .cfg 里一旦写入就不会被新默认值覆盖，
                //    导致"改了代码但玩家看到的还是旧话术"。
                MigrateString(config, log, "Broadcast", "JoinBody",
                    "此房间已启用捉迷藏",
                    "此房间已启用捉迷藏，具有特殊胜负条件\\n" +
                    "黑方：在倒计时结束前杀死所有可以杀死的人，视野缩小，刀CD缩短\\n" +
                    "白方：在倒计时结束前，避免死亡。通过完成任务可以缩短倒计时，倒计时结束后白方胜利。\\n" +
                    "{knife}\\n" +
                    "报告功能被禁用，不分配黑幕角色，部分角色的技能效果会有改变。\\n" +
                    "除此之外，可以在发信站输入/help来获得与使用部分指令以进行某些操作。");

                MigrateString(config, log, "Broadcast", "StartBodySelfServe",
                    "在倒计时结束之前，寻找刀具",
                    "在倒计时结束之前，寻找凶器开始追捕，或者完成任务逃离追捕~\\n" +
                    "或许也可以前往发信站使用/help来获得一些帮助。两个频道不一样呢~");

                MigrateString(config, log, "Broadcast", "StartBodyBlack",
                    "开始杀戮、开始搜索吧",
                    "开始杀戮、开始搜索吧~或许也可以在发信站获得帮助(/help)在倒计时结束之前。");

                MigrateString(config, log, "Broadcast", "StartBodyWhite",
                    "躲避杀手，完成任务，",
                    "躲避杀手，完成任务，或许也可以在发信站获得帮助(/help)。在倒计时结束之前。祝你好运~");
            }

            // v7 → v8

            if (from < 8)
            {
                // 视野倍率曾在"AOI 缩圈"时被顺手改成 0.6 作为补偿，但那是两件事，
                // 混在一起会让升级曲线难以预期。改回原设计 0.5。
                MigrateFloat(config, log, "KillUpgrade", "VisionBonusPerLevel", 0.6f, 0.5f);
            }

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

        /// <summary>
        /// 把仍是旧默认值的**文本**配置推进到新默认值。
        /// 用前缀匹配而不是全等 —— 旧值里可能含 \n，且历史上文本次序有过调整，
        /// 前缀足以识别"用户没改过这一项"。用户改过的内容不会被覆盖。
        /// </summary>
        private static void MigrateString(ConfigFile config, ManualLogSource log,
            string section, string key, string oldPrefix, string newValue)
        {
            try
            {
                ConfigDefinition def = new ConfigDefinition(section, key);
                if (!config.ContainsKey(def))
                    return;

                var entry = config.Bind(section, key, newValue);
                if (entry.Value == null || !entry.Value.StartsWith(oldPrefix))
                    return;                        // 用户改过 → 不动

                entry.Value = newValue;
                log.LogInfo($"[HS]   更新 {section}.{key}（文案）");
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
