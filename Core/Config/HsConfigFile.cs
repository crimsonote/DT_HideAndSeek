using System.IO;
using BepInEx;
using BepInEx.Configuration;

namespace HideAndSeek.Core
{
    /// <summary>
    /// 自有配置文件 <c>BepInEx/config/HideAndSeek.cfg</c> —— 本模块**唯一**的配置来源。
    ///
    /// 为什么固定用它（而不是像早期那样"装了 DT_Tools 就复用它的 .cfg"）：
    ///   - 配置文件可**单独编辑**：不与上游的 260 多个键混在同一个文件里；
    ///   - 插件可**单独安装 / 卸载**：不装 DT_Tools 也完整工作；
    ///   - 段仍由 [PatchFeature] / [ConfigField] 绑到静态 ConfigEntry 上，
    ///     所以大厅设置页与命令只读写那批字段，与配置文件是哪一个无关。
    ///
    /// 装了 DT_Tools 时这些条目会**镜像**进它的 ConfigFile（见 <see cref="DtBridge.TryMirror"/>），
    /// 于是 DT 的 CONFIG 页照旧列出并可修改本模块的设置 —— 两个文件共用同一批条目对象，
    /// 值只有一份，不存在"两套配置需要同步"的问题。
    /// </summary>
    internal static class HsConfigFile
    {
        public const string FileName = "HideAndSeek.cfg";

        /// <summary>配置文件的绝对路径（日志与排障都用它）。</summary>
        public static string FullPath => Path.Combine(Paths.ConfigPath, FileName);

        /// <summary>
        /// 打开自有配置。<c>saveOnInit: false</c> —— 这里不落盘：由 <c>Plugin.Start</c> 在全部 Bind
        /// 完成后统一 Save()，以保证首次生成时段内键序稳定（Enabled 在前、子项按声明顺序）。
        /// </summary>
        public static ConfigFile Open() => new ConfigFile(FullPath, saveOnInit: false);
    }
}
