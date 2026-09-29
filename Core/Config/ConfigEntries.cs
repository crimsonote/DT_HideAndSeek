using System.Collections.Generic;
using System.Reflection;
using BepInEx.Configuration;

namespace HideAndSeek.Core
{
    /// <summary>
    /// 读写 <see cref="ConfigFile"/> 的条目表。
    ///
    /// 用途：把本模块的配置条目**镜像**进 DT_Tools 的 ConfigFile，使它们出现在 DT 的 CONFIG 页
    /// （见 <see cref="DtBridge.TryMirror"/>）。
    ///
    /// 关于反射：BepInEx 5.4 的 <c>Entries</c> 是 <c>protected</c> 属性（编译期不可见），
    /// 底层是私有只读字段 <c>&lt;Entries&gt;k__BackingField</c>。这里只取**该字典实例**、
    /// 只增删内容、不替换引用 —— 实测（真 BepInEx 5.4.23.5）：
    ///   - ConfigFile.Keys 与 this[ConfigDefinition] 都读这个字典 ⇒ 注入即被枚举到；
    ///   - 注入同一批 ConfigEntry 对象后，经任一侧写入都作用到同一个值；
    ///   - 上游 Save() 会把注入的条目一并写进它自己的文件（镜像的固有代价）。
    ///
    /// 字段名若在某天变动，<see cref="Of"/> 返回 null，调用方只降级记 WARNING ——
    /// 本模块独立运行的能力不依赖这里。
    /// </summary>
    internal static class ConfigEntries
    {
        private static readonly FieldInfo EntriesField = typeof(ConfigFile)
            .GetField("<Entries>k__BackingField", BindingFlags.Instance | BindingFlags.NonPublic);

        /// <summary>该配置文件的条目表；取不到时返回 null（BepInEx 内部结构变动）。</summary>
        public static Dictionary<ConfigDefinition, ConfigEntryBase> Of(ConfigFile file)
        {
            if (file == null || EntriesField == null)
                return null;

            return EntriesField.GetValue(file) as Dictionary<ConfigDefinition, ConfigEntryBase>;
        }
    }
}
