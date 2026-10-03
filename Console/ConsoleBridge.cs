using System;
using System.Reflection;
using System.Text;
using System.Threading;
using HarmonyLib;
using HideAndSeek.Core;

namespace HideAndSeek.Console
{
    /// <summary>
    /// 把 hs_* 命令接入 DT_Tools 的 Web 控制台 —— 零改动上游，全部走反射。
    ///
    /// 两个环节：
    ///   ① 执行 —— Prefix WebConsole.ExecuteCommand（:318，private）。命中本模块前缀时自行处理，
    ///      写回 PendingRequest.ResultJson 并 Set Done，返回 false 跳过原流程
    ///      （否则原版会把它当"未知命令"）。
    ///   ② 列表 —— Postfix WebConsole.BuildCommandsJson（:463，private）。DT_Tools 只注册自己
    ///      程序集里的 IConsoleCommand，本模块的命令不会自动出现；这里在它生成的 JSON 数组里
    ///      追加 hs_* 条目，使 /api/commands 与前端补全能看见。
    ///
    /// 这是一个**可选**集成 —— 配置文件独立化之后，它是本插件唯一还与上游有关的地方（配置侧只剩
    /// "把设置镜像到上游 CONFIG 页"这一条便利路径，见 Core/DtBridge.cs）。之所以保留，是因为
    /// hs_* 目前没有别的执行入口。开关语义：
    ///   - [ConsoleBridge].Enabled = false ⇒ 两个补丁都不挂载，hs_* 完全不接进上游（整类跳过）；
    ///   - 上游未安装 ⇒ HarmonyPrepare 让补丁类整体跳过，只记 INFO、不报错，其余功能不受影响；
    ///   - 上游已安装但方法缺失 ⇒ 记 WARNING（这才是真正需要关注的情况）。
    /// </summary>
    [PatchFeature(
        section: "ConsoleBridge",
        description: "把 hs_* 命令接入 DT_Tools 的 Web 控制台（可选集成，零改动上游）。关掉本段即完全断开与上游的连接；未安装 DT_Tools 时自动跳过，不报错。",
        defaultEnabled: true,
        side: FeatureSide.Host)]
    internal static class ConsoleBridge
    {
        private const string DtConsoleTypeName = "DT_Tools.Console.WebConsole";

        /// <summary>与 <see cref="PatchFeatureAttribute.Section"/> 上的段名保持一致（Diagnostics 按段名查挂载状态）。</summary>
        private const string Section = "ConsoleBridge";

        /// <summary>hs_* 命令的元数据，用于补进 DT_Tools 的命令列表。</summary>
        private static readonly (string Name, string Usage, string Description)[] HsCommands =
        {
            ("hs",           "hs",                                                "捉迷藏模式：总览当前状态"),
            ("hs_check",     "hs_check",                                          "自检：各功能的挂载状态与触发次数"),
            ("hs_reload",    "hs_reload",                                         "重新读取 .cfg（BepInEx 不监听文件变化，改完配置需执行一次）"),
            ("hs_mode",      "hs_mode <on|off>",                                  "捉迷藏模式总开关（含可见性与光照回滚）"),
            ("hs_aoi",       "hs_aoi [on|off] [enter=750] [exit=1100] [min=3]",   "黑方视野裁剪参数"),
            ("hs_cd",        "hs_cd <秒>",                                        "黑方击杀后的冷却秒数"),
            ("hs_killlimit", "hs_killlimit <n|unlimited>",                        "黑方击杀次数上限"),
            ("hs_dummy",     "hs_dummy <add [座位号] [角色ID]|del <座位号>|list|clear>", "假人玩家（测试用）：生成 / 移除 / 查看靶子"),
            ("hs_flash",     "hs_flash <on|off>",                                 "开局灯效：开关闪烁（关闭后直接进入白亮黑灭的定态）"),
            ("hs_roomname",  "hs_roomname [新名字]",                              "修改房间名（Steam 列表 + 游戏内显示，仅房主可改）"),
            ("hs_tp",        "hs_tp <玩家ID> <x> <y> | <玩家ID> to <目标ID>",      "调试用传送：把玩家（含假人）挪到坐标或另一名玩家身边"),
            ("hs_grant",     "hs_grant <on|off>",                                  "发刀模式：on=开局随机发刀并锁死武器架，off=自行跑刀（下一局生效）"),
            ("hs_radar",     "hs_radar <on|off>",                                  "白方全图雷达：白方地图显示所有存活玩家位置（不区分阵营）"),
            ("hs_upgrade",   "hs_upgrade [vision|speed|task]",                   "黑学分：击杀获得学分，换取视野/移速/任务门槛强化"),
            ("hs_panel",     "hs_panel",                                         "直接放一次「全屏面板+心跳声」特效（一次性，可能残留到大厅）"),
        };

        private static Type DtConsoleType() => AccessTools.TypeByName(DtConsoleTypeName);

        /// <summary>上游 DT_Tools 是否已加载（只查类型，不判断其 Awake 是否执行）。</summary>
        private static bool UpstreamPresent => DtConsoleType() != null;

        /// <summary>
        /// 补丁挂上之后，主动重建 DT_Tools 的命令列表缓存。
        ///
        /// 为什么需要这一步：WebConsole 在它**自己的 Awake** 里就执行了
        /// `_cachedCommandsJson = BuildCommandsJson()`，而本插件的补丁要到
        /// **本插件的 Start** 才挂上（BepInEx 的顺序是"所有 Awake → 所有 Start"）。
        /// 也就是说 Postfix 挂上时缓存早已定型，之后再不会调用 BuildCommandsJson，
        /// 于是 hs_* 处于一种尴尬状态：**能执行**（执行是运行时路径，Prefix 有效），
        /// 却不出现在 /api/commands、命令列表与前端补全里。
        /// 这里在装载完成后主动重算一次缓存即可解决。
        /// </summary>
        internal static void RefreshCommandList()
        {
            try
            {
                // 命令桥是可选集成：段被关掉、或上游没装，都必须**整类**跳过而不是只跳过一半 ——
                // 否则 hs_* 会出现在上游命令列表里，点了却没反应（ExecuteHook 没挂上）。
                // 这两种情况都只记 INFO，不产生 WARNING/ERROR。
                if (!Diagnostics.IsLoaded(Section))
                {
                    Plugin.Log.LogInfo($"[HS] ConsoleBridge：命令桥已关闭（[{Section}].Enabled = false），hs_* 不接入上游控制台。");
                    return;
                }

                var type = DtConsoleType();
                if (type == null)
                {
                    Plugin.Log.LogInfo("[HS] ConsoleBridge：未检测到上游 DT_Tools，hs_* 命令桥自动跳过（其余功能不受影响）。");
                    return;
                }

                var instance = AccessTools.PropertyGetter(type, "Instance")?.Invoke(null, null);
                if (instance == null)
                {
                    Plugin.Log.LogInfo("[HS] ConsoleBridge：DT_Tools 控制台尚未就绪，跳过命令列表刷新。");
                    return;
                }

                var build = AccessTools.Method(type, "BuildCommandsJson");
                var field = AccessTools.Field(type, "_cachedCommandsJson");
                if (build == null || field == null)
                {
                    Plugin.Log.LogWarning("[HS] ConsoleBridge：找不到 BuildCommandsJson/_cachedCommandsJson，hs_* 不会出现在命令列表（仍可执行）。");
                    return;
                }

                field.SetValue(instance, build.Invoke(instance, null));
                Plugin.Log.LogInfo("[HS] ConsoleBridge：已重建 DT_Tools 命令列表缓存，hs_* 现在应在列表与补全中可见。");
            }
            catch (Exception ex)
            {
                Plugin.Log.LogWarning($"[HS] ConsoleBridge：重建命令列表失败 — {ex.Message}");
            }
        }

        // ── ① 拦截执行 ──────────────────────────────────────────────
        [HarmonyPatch]
        internal static class ExecuteHook
        {
            private static MethodBase Target()
                => AccessTools.Method(DtConsoleType(), "ExecuteCommand");

            [HarmonyPrepare]
            private static bool Prepare()
            {
                if (Target() != null)
                    return true;

                // 上游没装 ⇒ 这是正常情况（可选集成），只记 INFO；
                // 装了却找不到方法 ⇒ 上游内部结构变动，才值得 WARNING。
                if (UpstreamPresent)
                    Plugin.Log.LogWarning("[HS] ConsoleBridge：DT_Tools 已加载但找不到 WebConsole.ExecuteCommand，hs_* 命令不可执行（其余功能不受影响）。");
                else
                    Plugin.Log.LogInfo("[HS] ConsoleBridge：未检测到上游 DT_Tools，hs_* 命令桥自动跳过（其余功能不受影响）。");
                return false;
            }

            [HarmonyTargetMethod]
            private static MethodBase TargetMethod() => Target();

            [HarmonyPrefix]
            private static bool Prefix(object __instance, object __0)
            {
                if (__0 == null)
                    return true;

                Diagnostics.Hit("ConsoleBridge");

                var pending = Traverse.Create(__0);
                string raw = pending.Field("Command").GetValue<string>();
                if (string.IsNullOrWhiteSpace(raw))
                    return true;

                string trimmed = raw.TrimStart('/', '!').Trim();
                var parts = trimmed.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length == 0 || !HsCommandRouter.IsHsCommand(parts[0]))
                    return true;   // 不是本模块的命令 → 交还原版

                string json;
                try
                {
                    json = HsCommandRouter.Execute(trimmed);
                }
                catch (Exception ex)
                {
                    json = "{\"ok\":false,\"error\":\"" + ex.Message.Replace("\"", "'") + "\"}";
                }

                pending.Field("ResultJson").SetValue(json);
                pending.Field("Done").GetValue<ManualResetEventSlim>()?.Set();

                // 同时写入日志流。WebConsole 有两条返回通道：
                //   ResultJson（结构化）与 Log（文本，前端终端区轮询 /api/log）。
                // 只写 ResultJson 的话，命令确实执行了、脚本也能拿到 JSON，
                // 但终端文本区看起来"什么都不返回"。
                try
                {
                    var logMethod = AccessTools.Method(__instance?.GetType(), "Log");
                    logMethod?.Invoke(__instance, new object[] { json, global::BepInEx.Logging.LogLevel.Info });
                }
                catch
                {
                    // 日志写入失败不影响命令结果
                }

                return false;
            }
        }

        // ── ② 补进命令列表 ──────────────────────────────────────────
        [HarmonyPatch]
        internal static class CommandsListHook
        {
            private static MethodBase Target()
                => AccessTools.Method(DtConsoleType(), "BuildCommandsJson");

            [HarmonyPrepare]
            private static bool Prepare()
            {
                if (Target() != null)
                    return true;

                if (UpstreamPresent)
                    Plugin.Log.LogWarning("[HS] ConsoleBridge：DT_Tools 已加载但找不到 WebConsole.BuildCommandsJson，hs_* 不会出现在命令列表（仍可执行）。");
                else
                    Plugin.Log.LogInfo("[HS] ConsoleBridge：未检测到上游 DT_Tools，跳过命令列表注入。");
                return false;
            }

            [HarmonyTargetMethod]
            private static MethodBase TargetMethod() => Target();

            [HarmonyPostfix]
            private static void Postfix(ref string __result)
            {
                try
                {
                    string extra = BuildHsCommandsJson();
                    if (string.IsNullOrEmpty(extra))
                        return;

                    string cur = (__result ?? "[]").Trim();
                    string body = cur.Length >= 2 && cur[0] == '[' && cur[cur.Length - 1] == ']'
                        ? cur.Substring(1, cur.Length - 2).Trim()
                        : "";

                    __result = body.Length == 0
                        ? "[" + extra + "]"
                        : "[" + body + "," + extra + "]";
                }
                catch (Exception ex)
                {
                    Plugin.Log.LogWarning($"[HS] 追加 hs_* 到命令列表失败：{ex.Message}");
                }
            }
        }

        private static string BuildHsCommandsJson()
        {
            var sb = new StringBuilder();
            for (int i = 0; i < HsCommands.Length; i++)
            {
                var (name, usage, description) = HsCommands[i];
                if (i > 0) sb.Append(',');
                sb.Append("{\"name\":\"").Append(name).Append('"')
                  .Append(",\"aliases\":[]")
                  .Append(",\"usage\":\"").Append(usage).Append('"')
                  .Append(",\"description\":\"").Append(description).Append('"')
                  .Append(",\"author\":\"HideAndSeek\"}");
            }
            return sb.ToString();
        }
    }
}
