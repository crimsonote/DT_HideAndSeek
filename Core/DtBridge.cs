using System;
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
