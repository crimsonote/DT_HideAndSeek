using System;
using System.Collections.Generic;
using BepInEx.Configuration;
using HarmonyLib;

namespace HideAndSeek.Core
{
    /// <summary>
    /// 配置镜像的**写盘隔离**：镜像进 DT_Tools 的条目不落进 <c>DT_Tools.cfg</c>。
    ///
    /// 为什么需要：本模块为了让设置在 DT CONFIG 页可见，把**同一批 ConfigEntryBase 对象**
    /// 注入了 DT_Tools 的 ConfigFile（见 <see cref="DtBridge.TryMirror"/>）。但 DT_Tools 的
    /// <c>Save()</c> 会遍历自己 Entries 里的每一条写盘 —— 它不看条目"娘家"在哪 ⇒
    /// <c>DT_Tools.cfg</c> 里会留下本模块整套配置的副本。实测（2026-10-07，本机游戏目录）：
    /// 那个文件 89 个段里，40 个 [PatchFeature] 段 + <c>HS_Mode</c> 全部来自本模块。
    ///
    /// 三条后果：① 本模块卸载后留下一堆无主孤儿段；② DT_Tools 将来新增同名段会被已有的挤掉；
    /// ③ 最坑的是**误导性写入** —— 用户手改 <c>DT_Tools.cfg</c> 里这些段看着像生效，
    /// 实际下次启动就被 <c>HideAndSeek.cfg</c> 的权威值盖回去。
    ///
    /// 做法：在 DT_Tools 的 ConfigFile.Save 的 Prefix 里把我们的键**临时摘出**，
    /// Finalizer 里原样放回。文件落盘时物理上没有我们的条目，而 DT CONFIG 页在保存前后
    /// 照旧能列出、能改（摘除窗口只有几毫秒）。
    ///
    /// ⚠️ **只对新结构 DT_Tools（v1.0.9.0 起）生效**：<c>Prepare</c> 判 <c>DT_Tools.Core.Engine</c>
    /// 类型是否存在。旧结构下不挂这个补丁，也就照旧会被写进 <c>DT_Tools.cfg</c> ——
    /// 这是刻意取舍：不为旧版 DT_Tools 做兼容。
    ///
    /// ⚠️ 并发窗口：摘除到放回之间，若 DT CONFIG 页的轮询正好在遍历那个字典，可能抛一次
    /// "集合已修改"。DT CONFIG 页是 3 秒轮询、Save 是罕见操作，碰撞概率极低，且后果只是
    /// 那一次请求失败（前端会重试）。BepInEx 的 <c>ConfigFile</c> 本身也不是线程安全的，
    /// 这个窗口并不比它原有风险更大。
    /// </summary>
    [PatchFeature(
        section: "DtMirror",
        description: "把本模块配置镜像到 DT CONFIG 页时，不让副本落进 DT_Tools.cfg（摘除-放回；仅新版 DT_Tools 生效）。",
        defaultEnabled: true,
        side: FeatureSide.Host)]
    internal static class DtMirrorGuard
    {
        [HarmonyPatch(typeof(ConfigFile), nameof(ConfigFile.Save))]
        internal static class SaveHook
        {
            [HarmonyPrepare]
            private static bool Prepare()
            {
                if (AccessTools.TypeByName(DtBridge.DtEngineTypeName) != null)
                    return true;

                // 旧版 DT_Tools：不挂。只记一条 INFO，免得每次启动都让人以为出了故障。
                Plugin.Log.LogInfo(
                    "[HS] DtMirror：未检测到新版 DT_Tools（Core.Engine），跳过写盘隔离 —— " +
                    "旧版下本模块配置仍会被写进 DT_Tools.cfg（本模块不为旧版做兼容）。");
                return false;
            }

            [HarmonyPrefix]
            private static void Prefix(
                ConfigFile __instance,
                out List<KeyValuePair<ConfigDefinition, ConfigEntryBase>> __state)
            {
                __state = DtBridge.DetachMirroredFrom(__instance);
            }

            /// <summary>
            /// 用 Finalizer 而不是 Postfix：原方法抛异常时 Postfix 不会执行，
            /// 条目会永久留在暂存里 —— DT CONFIG 页当场少一批设置。
            /// Finalizer 无论成败都跑（返回值原样传回，不改行为）。
            /// </summary>
            [HarmonyFinalizer]
            private static Exception Finalizer(
                List<KeyValuePair<ConfigDefinition, ConfigEntryBase>> __state,
                Exception __exception)
            {
                DtBridge.ReattachMirrored(__state);
                return __exception;
            }
        }
    }
}
