using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;

namespace HideAndSeek.Core
{
    /// <summary>
    /// 从 DT_Tools 的配置文件一次性搬迁本模块的全部配置段。
    ///
    /// 为什么必须有这一步：在配置文件独立化之前，本模块的段被 Bind 到
    /// <c>DT_Tools.Plugin.Instance.Config</c>，用户的定制（例如 [KeyLock] LanternItemId）
    /// 全部躺在 <c>BepInEx/config/DT_Tools.cfg</c> 里。换成自有 HideAndSeek.cfg 之后，
    /// BepInEx **只在键不存在时**才用代码默认值 —— 不搬迁就等于把所有用户的定制静默重置
    /// 为默认值（其中一些一丢就让功能失效）。
    ///
    /// 做法（不解析 INI、不猜类型）：把旧文件整个读进一个"什么都不 Bind"的 ConfigFile，
    /// 此时它读到的每个键都停在 OrphanedEntries 里、值仍是原始字符串；再把这些
    /// (段, 键) → 原始值 注入**新** ConfigFile 的 OrphanedEntries。之后 PatchLoader /
    /// ConfigBinder 的 Bind 会像"从文件里读到"一样取用它们 —— 因为 BepInEx 的 Bind
    /// 正是先查 OrphanedEntries，命中则 SetSerializedValue 再摘除。
    ///
    /// 因此搬迁**必须早于 Bind**，且值的类型转换完全交给 BepInEx 自己做。
    /// 这样搬进来的值还能照常参与 ConfigMigration 的"旧默认值"判定，与"用户一直用这个文件"完全等价。
    ///
    /// 关于反射：BepInEx 5.4 的 <c>ConfigFile.OrphanedEntries</c> 是 internal（编译期不可见），
    /// 所以这里用反射拿它的**字典实例**，只增删内容、不替换引用。取不到时只降级为
    /// WARNING + 按默认值生成，不会让插件加载失败。
    ///
    /// 幂等与安全：
    ///   - 只有当 HideAndSeek.cfg **尚不存在**时才搬（即首次独立运行），
    ///     之后永不再碰，避免覆盖用户在新文件里改过的值；
    ///   - 只搬本模块拥有的段，DT_Tools 自己的 [WebConsole] 等段原样留在旧文件里；
    ///   - 全程只读旧文件（旧 ConfigFile 不 Bind 任何键，因此也不会回写）。
    /// </summary>
    internal static class LegacyConfigImport
    {
        public const string LegacyFileName = "DT_Tools.cfg";

        private static readonly PropertyInfo OrphanedEntriesProperty =
            typeof(ConfigFile).GetProperty(
                "OrphanedEntries",
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);

        /// <summary>搬迁的键数；0 表示未搬（无需搬、已搬过、或降级）。</summary>
        public static int RunOnce(ConfigFile target, IEnumerable<string> ownedSections, ManualLogSource log)
        {
            if (target == null)
                return 0;

            try
            {
                if (File.Exists(target.ConfigFilePath))
                {
                    log.LogInfo(
                        $"[HS] 配置：{HsConfigFile.FileName} 已存在，跳过从 {LegacyFileName} 的搬迁" +
                        "（不会覆盖你已改过的值；如需重新搬迁，先删除该文件）。");
                    return 0;
                }

                string legacyPath = Path.Combine(Paths.ConfigPath, LegacyFileName);
                if (!File.Exists(legacyPath))
                {
                    log.LogInfo($"[HS] 配置：未发现旧配置 {LegacyFileName}，{HsConfigFile.FileName} 将按代码默认值生成。");
                    return 0;
                }

                if (OrphanedEntriesProperty == null)
                {
                    log.LogWarning(
                        "[HS] 配置搬迁：当前 BepInEx 的 ConfigFile 没有 OrphanedEntries，无法搬迁 —— " +
                        $"将按代码默认值生成 {HsConfigFile.FileName}。");
                    return 0;
                }

                var owned = new HashSet<string>(StringComparer.Ordinal);
                if (ownedSections != null)
                {
                    foreach (var section in ownedSections)
                    {
                        if (!string.IsNullOrEmpty(section))
                            owned.Add(section);
                    }
                }

                // 什么都不 Bind ⇒ 旧文件里的每个键都留在 OrphanedEntries（值为原始字符串）。
                var legacy = new ConfigFile(legacyPath, saveOnInit: false);

                var legacyOrphans = OrphanedEntriesProperty.GetValue(legacy, null) as IDictionary;
                var targetOrphans = OrphanedEntriesProperty.GetValue(target, null) as IDictionary;
                if (legacyOrphans == null || targetOrphans == null)
                {
                    log.LogWarning("[HS] 配置搬迁：取不到 OrphanedEntries 字典，跳过搬迁。");
                    return 0;
                }

                int imported = 0, foreign = 0;
                foreach (DictionaryEntry pair in legacyOrphans)
                {
                    if (!(pair.Key is ConfigDefinition def) || !owned.Contains(def.Section))
                    {
                        foreign++;
                        continue;
                    }

                    // 新文件尚不存在 ⇒ 它的 OrphanedEntries 是空的，这里只做新增。
                    targetOrphans[def] = pair.Value;
                    imported++;
                }

                log.LogInfo(
                    $"[HS] 配置搬迁：从 {LegacyFileName} 搬入 {imported} 个键" +
                    $"（忽略属于上游自己的 {foreign} 个键），将写入 {HsConfigFile.FileName}。");

                if (imported == 0)
                {
                    log.LogWarning(
                        $"[HS] 配置搬迁：{LegacyFileName} 里没有找到任何本模块的配置段 —— " +
                        "如果你此前确实在本模块里改过设置，请确认该文件就是当初使用的那个。");
                }

                return imported;
            }
            catch (Exception ex)
            {
                log.LogWarning(
                    $"[HS] 配置搬迁失败（将按代码默认值生成 {HsConfigFile.FileName}）：{ex.Message}");
                return 0;
            }
        }
    }
}
