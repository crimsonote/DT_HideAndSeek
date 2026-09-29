using System;
using System.Reflection;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;

namespace HideAndSeek.Core
{
    /// <summary>
    /// 与 DT_Tools 的**可选**集成。全部走反射，无编译期依赖，也不修改对方源码。
    ///
    /// 两条路径，都按"上游是否存在"自动选择：
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
    /// 镜像只是"让设置也能在上游页面里改"的便利，独立运行不依赖它。
    /// </summary>
    internal static class DtBridge
    {
        public const string DtPluginTypeName = "DT_Tools.Plugin";

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
                foreach (var pair in mine)
                {
                    // 段名与上游无重叠（verify.ps1 与报告都核对过）；真撞上就让上游那条留着，
                    // 避免把它的条目挤掉 —— 覆盖上游的配置比"少显示几项"严重得多。
                    if (theirs.ContainsKey(pair.Key))
                    {
                        conflicted++;
                        continue;
                    }

                    theirs[pair.Key] = pair.Value;
                    added++;
                }

                log.LogInfo(
                    $"[HS] 已把 {added} 个配置项镜像到 DT_Tools 的 CONFIG 页（以 {own.ConfigFilePath} 为准）"
                    + (conflicted > 0 ? $"；{conflicted} 个键名与上游冲突，已跳过" : "")
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
        /// 只读探测上游的 ConfigFile（镜像目标）。**不再**作为本模块的配置来源。
        /// 必须在本插件的 Start() 里调用 —— 此时上游的 Awake 均已执行完毕
        /// （上游的 Plugin.Instance 是在它自己的 Awake 里赋值的；BepInEx 的顺序是"所有 Awake → 所有 Start"）。
        /// </summary>
        private static ConfigFile TryGetDtConfig()
        {
            try
            {
                var type = AccessTools.TypeByName(DtPluginTypeName);
                if (type == null)
                    return null;

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
