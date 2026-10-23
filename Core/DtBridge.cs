using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;

namespace HideAndSeek.Core
{
    /// <summary>
    /// 与 DT_Tools 的**可选依赖**集成。全部走反射，无编译期依赖，也不修改对方源码。
    ///
    /// 两条路径，都按"DT_Tools 是否存在"自动选择：
    ///
    ///   - **配置镜像**（本文件）：本模块的配置永远以自有
    ///     <c>BepInEx/config/HideAndSeek.cfg</c> 为准（见 <see cref="HsConfigFile"/>）——
    ///     这样它可以被单独编辑、单独安装、单独卸载。装了 DT_Tools 时，额外把**同一批**
    ///     <see cref="ConfigEntryBase"/> 对象注入它的 ConfigFile，DT 的 CONFIG 页于是照旧
    ///     列出并可修改本模块的设置。两个文件共用同一批条目对象 ⇒ 值只有一份，
    ///     不存在"两套配置需要同步"的问题。
    ///
    ///   - **命令桥**（<c>Console/ConsoleBridge.cs</c>）：装了就用 DT 的 Web 控制台执行 hs_*，
    ///     没装则整类静默跳过。
    ///
    /// ⚠️ 注入用的是 BepInEx 的私有条目表（<see cref="ConfigEntries"/>），失败只降级记 WARNING：
    /// 镜像只是"让设置也能在 DT_Tools 页面里改"的便利，独立运行不依赖它。
    ///
    /// ⚠️ 镜像有一个副作用：DT_Tools 的 <c>Save()</c> 会把注入的条目一并写进它自己的
    /// <c>DT_Tools.cfg</c>（它不检查条目的"娘家"）。于是那份文件里会留下本模块整套配置的副本 ——
    /// 卸载后变成无主孤儿段，手改那份副本还会"看着生效、实际被 HideAndSeek.cfg 的权威值盖回"。
    /// 解法见 <see cref="DtMirrorGuard"/>：它写盘前把我们的键临时摘出、写完再放回。
    /// </summary>
    internal static class DtBridge
    {
        public const string DtPluginTypeName = "DT_Tools.Plugin";

        /// <summary>
        /// 新版 DT_Tools（v1.0.9.0 起）的配置入口：<c>DT_Tools.Core.Engine.Config</c>（public static）。
        /// 那一版起 <c>Plugin</c> 不再持有 Instance / Config 属性，配置统一挂在 Engine 上。
        /// </summary>
        public const string DtEngineTypeName = "DT_Tools.Core.Engine";

        /// <summary>功能标签位（DT_Tools 2.0.0.0 起取代目录分类）。</summary>
        public const string DtFeatureTagTypeName = "DT_Tools.Core.Attributes.FeatureTag";

        /// <summary>标签字典：给出分组、中文名与说明。</summary>
        public const string DtFeatureTagCatalogTypeName = "DT_Tools.Core.Attributes.FeatureTagCatalog";

        /// <summary>功能作用面枚举（配置页据此过滤 Client / Host / Both）。</summary>
        public const string DtFeatureSideTypeName = "DT_Tools.Core.Attributes.FeatureSide";

        /// <summary>
        /// 镜像目标：DT_Tools 的 ConfigFile。镜像成功后赋值 —— 写盘隔离（<see cref="DtMirrorGuard"/>）
        /// 据此判断"这次 Save 是不是它在写自己那一份"，避免误伤我们自己的 HideAndSeek.cfg。
        /// </summary>
        internal static ConfigFile MirroredFile;

        /// <summary>已镜像进 DT_Tools 的键 —— 它写盘前要临时摘除的就是这些。</summary>
        private static readonly List<ConfigDefinition> MirroredKeys = new List<ConfigDefinition>();

        /// <summary>DT_Tools 是否已加载（只查类型，不依赖其 Awake 是否执行）。</summary>
        public static bool HasDtTools => AccessTools.TypeByName(DtPluginTypeName) != null;

        /// <summary>
        /// 把本模块已绑定的配置条目镜像进 DT_Tools 的 ConfigFile。
        /// 必须在全部 Bind 之后调用（否则镜像进去的是残缺的一批）。
        /// </summary>
        public static void TryMirror(ConfigFile own, ManualLogSource log)
        {
            var dt = TryGetDtConfig();
            if (dt == null)
            {
                log.LogInfo($"[HS] 未检测到 DT_Tools：配置只写在自有 {HsConfigFile.FileName}（可独立运行）。");
                return;
            }

            try
            {
                var mine = ConfigEntries.Of(own);
                var theirs = ConfigEntries.Of(dt);
                if (mine == null || theirs == null)
                {
                    log.LogWarning(
                        "[HS] 镜像到 DT CONFIG 页失败：取不到 ConfigFile 的条目表（BepInEx 内部结构变动？）。" +
                        $"配置仍写在 {own.ConfigFilePath}，功能不受影响。");
                    return;
                }

                int added = 0, conflicted = 0;
                MirroredKeys.Clear();
                foreach (var pair in mine)
                {
                    // 段名与 DT_Tools 无重叠（verify.ps1 与报告都核对过）；真撞上就让 DT_Tools 那条留着，
                    // 避免把它的条目挤掉 —— 覆盖 DT_Tools 的配置比"少显示几项"严重得多。
                    if (theirs.ContainsKey(pair.Key))
                    {
                        conflicted++;
                        continue;
                    }

                    theirs[pair.Key] = pair.Value;
                    MirroredKeys.Add(pair.Key);
                    added++;
                }

                // 记下目标文件与键集：写盘隔离补丁据此判断"该不该摘条目"（见 DtMirrorGuard）
                MirroredFile = MirroredKeys.Count > 0 ? dt : null;

                // 顺手登记分类，DT CONFIG 页才会有【捉迷藏】专属文件夹（否则段全散在「其他」里）
                TryRegisterCategory(log, mine);

                log.LogInfo(
                    $"[HS] 已把 {added} 个配置项镜像到 DT_Tools 的 CONFIG 页（以 {own.ConfigFilePath} 为准）"
                    + (conflicted > 0 ? $"；{conflicted} 个键名与 DT_Tools 冲突，已跳过" : "")
                    + "。");
            }
            catch (Exception ex)
            {
                log.LogWarning(
                    $"[HS] 镜像到 DT CONFIG 页失败：{ex.Message}。" +
                    $"配置仍写在 {own.ConfigFilePath}，功能不受影响。");
            }
        }

        /// <summary>
        /// 把本模块的「捉迷藏」标签注入 DT_Tools 2.0 的标签体系。
        /// </summary>
        /// <remarks>
        /// 三步，全部反射（本模块对 DT_Tools 无编译期依赖）：
        ///   1. 替换 <c>FeatureTagCatalog.Entries</c>（<c>public static readonly IReadOnlyList&lt;Entry&gt;</c>，
        ///      底层是定长数组）为「上游原条目 + 本模块一条」。<c>Names()</c> 与 <c>/api/config/tags</c>
        ///      每次都遍历该字段 ⇒ 替换后前端自动认识新标签，零改动 DT_Tools、也零改动前端。
        ///   2. 给每个段写 <c>Engine.SectionTags</c>（段 → 标签位）。
        ///   3. 写 <c>Engine.SectionSides</c>（Both）：不写时 <c>SideOfSection</c> 返回 null，
        ///      配置页的「作用面」过滤拿不到值。SectionGroups 不写 —— 上游对功能段默认按 "feature" 归类。
        ///
        /// 位选择 <b>bit 63</b>（<c>1UL &lt;&lt; 63</c>）：上游已用 0–37，且约定「新增标签只追加到末尾」，
        /// 即从 38 起顺序增长 ⇒ 最高的 63 最晚被占用（要上游再新增 26 个标签才碰到）。
        /// ⚠ 63 仍属上游预留区；真撞位时上游的 <c>Validate</c> **不报错**（它只拒绝"未定义的位"），
        /// 表现是两个功能串标签 ⇒ 每次跟进 DT_Tools 版本时核对 FeatureTagCatalog 的条目数。
        /// </remarks>
        /// <param name="log">插件日志。</param>
        /// <param name="mine">已镜像进 DT_Tools 的配置条目，用来取出本模块的全部段名。</param>
        /// <returns>认出是 DT_Tools 2.0 并已处理返回 true；不是 2.0 返回 false（交给旧的分类路径）。</returns>
        private static bool TryRegisterTag(
            ManualLogSource log, Dictionary<ConfigDefinition, ConfigEntryBase> mine)
        {
            const string TagKey = "HideAndSeek";
            const string TagGroup = "捉迷藏";
            const string TagLabel = "捉迷藏";
            const string TagDescription = "捉迷藏玩法的全部设置（本模块自带，与 DT_Tools 无关）";

            var catalog = AccessTools.TypeByName(DtFeatureTagCatalogTypeName);
            var tagEnum = AccessTools.TypeByName(DtFeatureTagTypeName);
            var engine = AccessTools.TypeByName(DtEngineTypeName);
            if (catalog == null || tagEnum == null || engine == null)
                return false;   // 不是 2.0（≤1.x 没有标签体系）⇒ 交回旧路径

            try
            {
                // ⚠ 2026-10-23：这些目标**一律用 Type.GetNestedType / GetField 取**，不要走
                // AccessTools.TypeByName("…+Entry") / AccessTools.Field —— 实测 DT_Tools 2.0.0.6
                // 上那条路会让本方法静默返回 false（不抛异常、也不打 Harmony 警告），于是旧分类
                // 路径接管并刷出"取不到 SectionCategories"，表现是 CONFIG 页没有【捉迷藏】标签。
                var entryType = catalog.GetNestedType("Entry", BindingFlags.Public);
                var entriesField = catalog.GetField("Entries", BindingFlags.Public | BindingFlags.Static);
                var allBitsField = catalog.GetField("AllBits", BindingFlags.Public | BindingFlags.Static);
                var tagsField = engine.GetField(
                    "SectionTags", BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Static);
                var sidesField = engine.GetField(
                    "SectionSides", BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Static);

                if (entryType == null || entriesField == null || tagsField == null)
                {
                    log.LogWarning(
                        $"[HS] 未能登记「{TagLabel}」标签：DT_Tools 的标签结构与预期不符" +
                        $"（Entry={entryType != null}，Entries={entriesField != null}，SectionTags={tagsField != null}）。" +
                        "配置仍可在 CONFIG 页修改，只是不带标签。");
                    return true;
                }

                if (!(entriesField.GetValue(null) is Array entries))
                {
                    log.LogWarning(
                        $"[HS] 未能登记「{TagLabel}」标签：FeatureTagCatalog.Entries 不是数组" +
                        $"（实际 {entriesField.FieldType.Name}）。");
                    return true;
                }

                var keyField = entryType.GetField("Key");
                var tagField = entryType.GetField("Tag");

                // 幂等：热重载时 Entries 里可能已有本模块那一条 ⇒ 沿用它的位，只补段登记
                bool injected = false;
                object tagValue = null;
                foreach (var existing in entries)
                {
                    if (existing == null || keyField == null)
                        continue;
                    if ((string)keyField.GetValue(existing) != TagKey)
                        continue;

                    injected = true;
                    tagValue = tagField?.GetValue(existing);
                    break;
                }

                if (injected && tagValue == null)
                {
                    log.LogWarning($"[HS] 未能登记「{TagLabel}」标签：已有同名条目但取不到 Tag 位。");
                    return true;
                }

                ulong tagBit = 0;
                if (!injected)
                {
                    // 位选择：上游约定「新增标签只追加到末尾」（2.0.0.6 已用到 1<<38），
                    // 从最高位往下找第一个空闲位。已知 bit 39 被 ClueDetail 硬编码占用，
                    // 所以只在 63..40 段里找，不会与它撞位。
                    ulong used = allBitsField?.GetValue(null) is ulong bits ? bits : 0UL;
                    tagBit = 1UL << 63;
                    while (tagBit != 0 && (used & tagBit) != 0)
                        tagBit >>= 1;
                    if (tagBit == 0)
                    {
                        log.LogWarning(
                            $"[HS] 未能登记「{TagLabel}」标签：DT_Tools 的标签位已用满（AllBits=0x{used:X}）。");
                        return true;
                    }

                    tagValue = Enum.ToObject(tagEnum, tagBit);

                    var grown = Array.CreateInstance(entryType, entries.Length + 1);
                    Array.Copy(entries, grown, entries.Length);
                    object entry = Activator.CreateInstance(entryType);
                    tagField?.SetValue(entry, tagValue);
                    keyField?.SetValue(entry, TagKey);
                    entryType.GetField("Group")?.SetValue(entry, TagGroup);
                    entryType.GetField("Label")?.SetValue(entry, TagLabel);
                    entryType.GetField("Description")?.SetValue(entry, TagDescription);
                    grown.SetValue(entry, entries.Length);
                    entriesField.SetValue(null, grown);

                    // AllBits 是上游 Validate 的"已定义标签"判据：不并进去，
                    // 将来上游若对全部段做校验，本模块的位会被当成非法位
                    if (allBitsField != null)
                        allBitsField.SetValue(null, used | tagBit);
                }

                if (!(tagsField.GetValue(null) is IDictionary tags))
                {
                    log.LogWarning(
                        $"[HS] 未能登记「{TagLabel}」标签：Engine.SectionTags 不是字典" +
                        $"（实际 {tagsField.FieldType.Name}）。");
                    return true;
                }

                // 作用面写 Both：本模块的段大多只在一侧生效，但 Both 不会把段从任何筛选里藏掉；
                // 逐段精确值需要读 [PatchFeature].Side，等确实要用"作用面"筛选再补。
                var sides = sidesField?.GetValue(null) as IDictionary;
                object sideBoth = null;
                var sideEnum = AccessTools.TypeByName(DtFeatureSideTypeName);
                if (sideEnum != null && sides != null)
                    sideBoth = Enum.Parse(sideEnum, "Both");

                int added = 0;
                foreach (var key in mine.Keys)
                {
                    string section = key.Section;
                    if (tags.Contains(section))
                        continue;   // 上游自己的段名不抢（防御性判断）

                    tags[section] = tagValue;
                    if (sideBoth != null)
                        sides[section] = sideBoth;
                    added++;
                }

                string tail = injected ? "标签条目已存在，只补了段登记" : $"新增标签位 0x{tagBit:X}";
                log.LogInfo($"[HS] 已把 {added} 个配置段登记进 DT_Tools 的「{TagLabel}」标签（{tail}）。");
                return true;
            }
            catch (Exception ex)
            {
                // 已确认是 2.0（上面取到了 Entries / SectionTags）⇒ 失败只降级，不回落旧路径再刷一条过时告警
                log.LogWarning($"[HS] 注入「{TagLabel}」标签失败：{ex.Message}（配置仍可用，只是不带标签）。");
                return true;
            }
        }
        /// <summary>
        /// 把本模块的配置段登记进 DT_Tools 的分类表，让 DT CONFIG 页把它们收进一个
        /// **【捉迷藏】** 文件夹，而不是散落在「其他」里。
        ///
        /// 为什么需要：上游 <c>Engine.CategoryOfSection</c> 查的是 <c>Engine</c> 的私有静态字典
        /// <c>SectionCategories</c>，那份表在**上游装载期**由 FeatureLoader 按
        /// <c>Patches/&lt;分类&gt;/&lt;功能&gt;</c> 推导填充。我们的段是外来的、不在表里 ⇒
        /// category 为 null ⇒ 前端 <c>categoryOf()</c> 回退成 <c>__other</c>「其他」。
        ///
        /// 这里反射补登记即可：**不改上游一个字节、也不改前端** —— 前端对未知分类名直接拿 key
        /// 当显示名（<c>categoryLabel(k) =&gt; CATEGORY_LABELS[k] || k</c>），排序落在已知分类之后、
        /// 「其他」之前。
        /// </summary>
        private static void TryRegisterCategory(
            ManualLogSource log, Dictionary<ConfigDefinition, ConfigEntryBase> mine)
        {
            // DT_Tools 2.0.0.0 起分类体系改为标签，SectionCategories 字典已删除
            if (TryRegisterTag(log, mine))
                return;

            const string CategoryName = "捉迷藏";

            try
            {
                var engine = AccessTools.TypeByName(DtEngineTypeName);
                var field = engine == null ? null : AccessTools.Field(engine, "SectionCategories");
                var categories = field?.GetValue(null) as Dictionary<string, string>;
                if (categories == null)
                {
                    // 上游结构变动：只降级记一条，不影响功能（设置照旧在自有文件里）
                    log.LogWarning(
                        $"[HS] 未能登记「{CategoryName}」分类（取不到 DT_Tools 的 SectionCategories），" +
                        "本模块配置会落在 CONFIG 页的「其他」里。");
                    return;
                }

                int added = 0;
                foreach (var key in mine.Keys)
                {
                    if (categories.ContainsKey(key.Section))
                        continue;   // 上游自己的段名不抢（理论上不会重，防御性判断）

                    categories[key.Section] = CategoryName;
                    added++;
                }

                log.LogInfo($"[HS] 已把 {added} 个配置段登记进 DT CONFIG 页的「{CategoryName}」分类。");
            }
            catch (Exception ex)
            {
                log.LogWarning(
                    $"[HS] 登记「{CategoryName}」分类失败：{ex.Message}（配置会落在「其他」里，功能不受影响）。");
            }
        }

        /// <summary>
        /// 把本模块镜像进 <paramref name="file"/> 的条目**临时摘出**（写盘隔离用，见 <see cref="DtMirrorGuard"/>）。
        /// 只对镜像目标本身生效；返回 null 表示"这次 Save 与本模块无关，不用管"。
        /// </summary>
        internal static List<KeyValuePair<ConfigDefinition, ConfigEntryBase>> DetachMirroredFrom(ConfigFile file)
        {
            if (MirroredFile == null || !ReferenceEquals(file, MirroredFile) || MirroredKeys.Count == 0)
                return null;

            var entries = ConfigEntries.Of(file);
            if (entries == null)
                return null;

            var detached = new List<KeyValuePair<ConfigDefinition, ConfigEntryBase>>(MirroredKeys.Count);
            foreach (var key in MirroredKeys)
            {
                if (!entries.TryGetValue(key, out var entry))
                    continue;

                detached.Add(new KeyValuePair<ConfigDefinition, ConfigEntryBase>(key, entry));
                entries.Remove(key);
            }

            return detached;
        }

        /// <summary>把 <see cref="DetachMirroredFrom"/> 摘出的条目原样放回（对象与值都不变，只是回表）。</summary>
        internal static void ReattachMirrored(List<KeyValuePair<ConfigDefinition, ConfigEntryBase>> detached)
        {
            if (detached == null || detached.Count == 0 || MirroredFile == null)
                return;

            var entries = ConfigEntries.Of(MirroredFile);
            if (entries == null)
                return;

            foreach (var pair in detached)
                entries[pair.Key] = pair.Value;
        }

        /// <summary>
        /// 只读探测 DT_Tools 的 ConfigFile（镜像目标）。**不再**作为本模块的配置来源。
        /// 必须在本插件的 Start() 里调用 —— 此时 DT_Tools 的 Awake 均已执行完毕
        /// （BepInEx 的顺序是"所有 Awake → 所有 Start"）。
        /// </summary>
        private static ConfigFile TryGetDtConfig()
        {
            try
            {
                var type = AccessTools.TypeByName(DtPluginTypeName);
                if (type == null)
                    return null;

                // 新版 DT_Tools（v1.0.9.0 起）：配置入口是 DT_Tools.Core.Engine.Config 静态属性。
                // 那一版把 Plugin 的 Instance / Config 都撤了，只探旧路径必然探空。
                var engine = AccessTools.TypeByName(DtEngineTypeName);
                if (engine != null)
                {
                    var cfg = AccessTools.PropertyGetter(engine, "Config")?.Invoke(null, null) as ConfigFile;
                    if (cfg != null)
                        return cfg;
                }

                // 旧版 DT_Tools（≤ v1.0.6.1）：Plugin.Instance.Config。
                // 保留此路径是为了兼容尚未更新的安装，两条都探不到才算失败。
                var instance = type
                    .GetProperty("Instance", BindingFlags.Public | BindingFlags.Static)
                    ?.GetValue(null);
                if (instance == null)
                    return null;

                return type.GetProperty("Config", BindingFlags.Public | BindingFlags.Instance)
                    ?.GetValue(instance) as ConfigFile;
            }
            catch (Exception ex)
            {
                Plugin.Log.LogWarning($"[HS] 探测 DT_Tools 配置失败，跳过镜像：{ex.Message}");
                return null;
            }
        }
    }
}
