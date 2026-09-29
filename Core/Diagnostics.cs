using System;
using System.Collections.Generic;
using System.Text;

namespace HideAndSeek.Core
{
    /// <summary>
    /// 运行期自检：记录每个功能是否成功挂载、被触发了多少次，供 hs_check 命令输出。
    /// 用于快速判断"某个模块到底有没有在工作"—— 不必依赖各自的业务日志。
    ///
    /// 线程假设：Harmony 补丁均在游戏主线程执行，故读取不加锁。
    /// </summary>
    internal static class Diagnostics
    {
        private sealed class Entry
        {
            public string Section = "";
            public bool Loaded;
            public int Hits;

            /// <summary>该段里没挂上的嵌套补丁数。大于 0 表示"段加载了，但有钩子失效"。</summary>
            public int FailedNested;
        }

        private static readonly Dictionary<string, Entry> Map =
            new Dictionary<string, Entry>(StringComparer.Ordinal);

        private static Entry Get(string section)
        {
            if (!Map.TryGetValue(section, out var entry))
            {
                entry = new Entry { Section = section };
                Map[section] = entry;
            }
            return entry;
        }

        /// <summary>PatchLoader 成功挂载某功能时调用。</summary>
        public static void MarkLoaded(string section, int failedNested = 0)
        {
            if (string.IsNullOrEmpty(section))
                return;

            var entry = Get(section);
            entry.Loaded = true;
            entry.FailedNested = failedNested;
        }

        /// <summary>
        /// 该段是否已被 PatchLoader 挂载。段级 Enabled = false 时为 false ——
        /// 调用方据此判断"整个段被跳过了"，而不是"段内的某个钩子没挂上"。
        /// </summary>
        public static bool IsLoaded(string section)
        {
            if (string.IsNullOrEmpty(section))
                return false;

            return Map.TryGetValue(section, out var entry) && entry.Loaded;
        }

        /// <summary>功能被触发时调用（放在 Prefix/Postfix 首行）。</summary>
        public static void Hit(string section)
        {
            if (string.IsNullOrEmpty(section))
                return;
            Get(section).Hits++;
        }

        /// <summary>输出 JSON 报告：各功能的挂载状态与触发次数。</summary>
        public static string Report()
        {
            var sb = new StringBuilder();
            sb.Append("{\"ok\":true,\"mode\":").Append(ModeRuntime.Active ? "true" : "false");
            sb.Append(",\"features\":[");

            bool first = true;
            foreach (var entry in Map.Values)
            {
                if (!first) sb.Append(',');
                first = false;
                sb.Append("{\"section\":\"").Append(entry.Section).Append('"')
                  .Append(",\"loaded\":").Append(entry.Loaded ? "true" : "false")
                  .Append(",\"hits\":").Append(entry.Hits)
                  .Append(",\"failed\":").Append(entry.FailedNested)
                  .Append('}');
            }

            sb.Append("]}");
            return sb.ToString();
        }
    }
}
