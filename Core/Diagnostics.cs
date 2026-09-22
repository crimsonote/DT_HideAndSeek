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
        public static void MarkLoaded(string section)
        {
            if (string.IsNullOrEmpty(section))
                return;
            Get(section).Loaded = true;
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
                  .Append('}');
            }

            sb.Append("]}");
            return sb.ToString();
        }
    }
}
