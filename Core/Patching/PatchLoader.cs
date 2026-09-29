using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;

namespace HideAndSeek.Core
{
    /// <summary>
    /// 扫描 [PatchFeature]，按段名排序绑定 Enabled + 子配置，再按开关 PatchAll。
    /// 与 DT_Tools 的装载流程同构，但只扫描本程序集（本模块的功能全部自带）。
    ///
    /// 两层开关：
    ///   - 段级 Enabled：启动时决定是否挂载补丁（改后需重启，与 DT_Tools 语义一致）；
    ///   - <see cref="ModeRuntime"/> 总开关：运行期短路，改 .cfg 即时生效。
    /// </summary>
    internal static class PatchLoader
    {
        public sealed class LoadResult
        {
            public int EnabledCount { get; set; }
            public int SkippedCount { get; set; }
            public int FailedCount { get; set; }
        }

        private sealed class FeatureDesc
        {
            public Type Type;
            public string Section;
            public string Description;
            public bool DefaultEnabled;
            public string Author;
            public FeatureSide Side;
        }

        public static LoadResult Load(
            Harmony harmony,
            ConfigFile config,
            ManualLogSource log,
            IEnumerable<(string Section, Action BindSection)> extraSections = null)
        {
            var features = DiscoverFeatures(typeof(PatchLoader).Assembly);

            // 先收集所有绑定动作，再按段名统一排序，
            // 保证同一段内 Enabled 先于子项写入 .cfg。
            var sectionBindActions = new List<(string Section, Action Bind)>();

            foreach (var desc in features)
            {
                var captured = desc;
                sectionBindActions.Add((captured.Section, () => BindFeatureSection(config, captured)));
            }

            if (extraSections != null)
            {
                foreach (var extra in extraSections)
                    sectionBindActions.Add((extra.Section, extra.BindSection));
            }

            foreach (var group in sectionBindActions.OrderBy(x => x.Section, StringComparer.Ordinal))
                group.Bind();

            var result = new LoadResult();

            foreach (var desc in features.OrderBy(f => f.Section, StringComparer.Ordinal))
            {
                var enabled = config.Bind(
                    desc.Section, "Enabled", desc.DefaultEnabled, BuildSectionComment(desc));

                if (!enabled.Value)
                {
                    result.SkippedCount++;
                    log.LogInfo($"[HS] 已跳过功能: {desc.Type.Name} ([{desc.Section}].Enabled = false)");
                    continue;
                }

                try
                {
                    harmony.PatchAll(desc.Type);

                    // 支持嵌套补丁类（与 DT_Tools 相同的写法）
                    // nestedFailed 要传给 Diagnostics：段级 MarkLoaded 只能说明"这个功能加载了"，
                    // 无法反映"其中某个钩子没挂上"。以前那种情况 hs_check 仍显示 loaded=true，
                    // 只能靠翻启动日志才发现路径静默失效。
                    int nestedFailed = 0;
                    foreach (var nested in desc.Type.GetNestedTypes(BindingFlags.Public | BindingFlags.NonPublic))
                    {
                        if (!nested.IsClass || nested.IsGenericTypeDefinition)
                            continue;
                        if (!Attribute.IsDefined(nested, typeof(HarmonyPatch)))
                            continue;

                        try
                        {
                            harmony.PatchAll(nested);
                        }
                        catch (Exception nestedEx)
                        {
                            result.FailedCount++;
                            nestedFailed++;
                            log.LogError($"[HS] 嵌套补丁加载失败: {desc.Type.Name}.{nested.Name} ([{desc.Section}]) — " +
                                         $"{nestedEx.GetType().Name}: {nestedEx.Message}");
                        }
                    }

                    result.EnabledCount++;
                    Diagnostics.MarkLoaded(desc.Section, nestedFailed);
                    log.LogInfo($"[HS] 已启用功能: {desc.Type.Name} ([{desc.Section}], {desc.Side})");
                }
                catch (Exception ex)
                {
                    result.FailedCount++;
                    log.LogError($"[HS] 功能加载失败: {desc.Type.Name} ([{desc.Section}]) — " +
                                 $"{ex.GetType().Name}: {ex.Message}");
                }
            }

            return result;
        }

        /// <summary>
        /// 本模块拥有的全部配置段名（含 <see cref="ModeRuntime.Section"/>），不触发任何 Bind。
        /// 供配置搬迁使用 —— 只搬这些段，绝不碰上游 DT_Tools 自己的段。
        /// </summary>
        public static IEnumerable<string> OwnedSectionNames()
        {
            foreach (var desc in DiscoverFeatures(typeof(PatchLoader).Assembly))
                yield return desc.Section;

            yield return ModeRuntime.Section;
        }

        private static List<FeatureDesc> DiscoverFeatures(Assembly assembly)
        {
            var list = new List<FeatureDesc>();
            var seenSections = new HashSet<string>(StringComparer.Ordinal);

            foreach (var type in assembly.GetTypes())
            {
                if (!type.IsClass)
                    continue;

                var feature = (PatchFeatureAttribute)Attribute.GetCustomAttribute(type, typeof(PatchFeatureAttribute));
                if (feature == null)
                    continue;

                if (!seenSections.Add(feature.Section))
                    throw new InvalidOperationException($"重复的配置段名 [{feature.Section}]（类型 {type.FullName}）");

                list.Add(new FeatureDesc
                {
                    Type = type,
                    Section = feature.Section,
                    Description = feature.Description,
                    DefaultEnabled = feature.DefaultEnabled,
                    Author = feature.Author,
                    Side = feature.Side
                });
            }

            return list;
        }

        private static void BindFeatureSection(ConfigFile config, FeatureDesc desc)
        {
            config.Bind(desc.Section, "Enabled", desc.DefaultEnabled, BuildSectionComment(desc));
            ConfigBinder.BindFields(config, desc.Type, desc.Section);
        }

        private static string BuildSectionComment(FeatureDesc desc)
        {
            var sb = new System.Text.StringBuilder();
            if (!string.IsNullOrWhiteSpace(desc.Author))
                sb.Append("Author: ").Append(desc.Author.Trim()).Append('\n');
            sb.Append("Side: ").Append(FormatSide(desc.Side)).Append('\n');
            if (!string.IsNullOrWhiteSpace(desc.Description))
                sb.Append(desc.Description.Trim());
            return sb.ToString();
        }

        private static string FormatSide(FeatureSide side) => side switch
        {
            FeatureSide.Client => "Client",
            FeatureSide.Host => "Host",
            FeatureSide.Both => "Both",
            _ => side.ToString()
        };
    }
}
