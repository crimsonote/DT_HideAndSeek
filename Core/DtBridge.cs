using System;
using System.IO;
using System.Reflection;
using BepInEx;
using BepInEx.Configuration;
using HarmonyLib;

namespace HideAndSeek.Core
{
    /// <summary>
    /// 与 DT_Tools 的集成桥。全部走反射，无编译期依赖，也不修改对方源码。
    ///
    /// 配置侧（稳定）：DT_Tools 的 Web 控制台枚举配置时遍历 ConfigFile.Keys，
    /// 不区分条目来源。因此把 HS_* 段 Bind 到它的 ConfigFile 上，DT CONFIG 页面
    /// 就会自动列出并允许修改。
    /// </summary>
    internal static class DtBridge
    {
        public const string DtPluginTypeName = "DT_Tools.Plugin";

        /// <summary>DT_Tools 是否已加载（只查类型，不依赖其 Awake 是否执行）。</summary>
        public static bool HasDtTools => AccessTools.TypeByName(DtPluginTypeName) != null;

        /// <summary>
        /// 优先复用 DT_Tools 的 ConfigFile；探测失败回退到独立的 HideAndSeek.cfg。
        /// 必须在本插件的 Start() 里调用 —— 此时其余插件的 Awake 均已执行完毕。
        /// </summary>
        public static ConfigFile ResolveConfig()
        {
            var shared = TryGetDtConfig();
            if (shared != null)
            {
                Plugin.Log.LogInfo("[HS] 检测到 DT_Tools，配置将写入其 .cfg（DT CONFIG 页面可见）。");
                return shared;
            }

            Plugin.Log.LogInfo("[HS] 未检测到 DT_Tools，使用独立配置 HideAndSeek.cfg。");
            return new ConfigFile(Path.Combine(Paths.ConfigPath, "HideAndSeek.cfg"), false);
        }

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
                Plugin.Log.LogWarning($"[HS] 探测 DT_Tools 配置失败，改用独立配置：{ex.Message}");
                return null;
            }
        }
    }
}