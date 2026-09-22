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
    /// 上游未安装、或内部结构变动导致方法缺失时，HarmonyPrepare 会让对应补丁类整体跳过，
    /// 不影响其余功能。
    /// </summary>
    [PatchFeature(
        section: "ConsoleBridge",
        description: "把 hs_* 命令接入 DT_Tools 的 Web 控制台（零改动上游；未安装 DT_Tools 时自动失效）：既可执行，也显示在命令列表与补全中。",
        defaultEnabled: true,
        side: FeatureSide.Host)]
    internal static class ConsoleBridge
    {
        private const string DtConsoleTypeName = "DT_Tools.Console.WebConsole";

        /// <summary>hs_* 命令的元数据，用于补进 DT_Tools 的命令列表。</summary>
        private static readonly (string Name, string Usage, string Description)[] HsCommands =
        {
            ("hs",           "hs",                                                "捉迷藏模式：总览当前状态"),
            ("hs_check",     "hs_check",                                          "自检：各功能的挂载状态与触发次数"),
            ("hs_mode",      "hs_mode <on|off>",                                  "捉迷藏模式总开关（含可见性与光照回滚）"),
            ("hs_aoi",       "hs_aoi [on|off] [enter=600] [exit=900] [min=3]",    "黑方视野裁剪参数"),
            ("hs_cd",        "hs_cd <秒>",                                        "黑方击杀后的冷却秒数"),
            ("hs_killlimit", "hs_killlimit <n|unlimited>",                        "黑方击杀次数上限"),
            ("hs_dummy",     "hs_dummy <add [座位号] [角色ID]|del <座位号>|list|clear>", "假人玩家（测试用）：生成 / 移除 / 查看靶子"),
        };

        private static Type DtConsoleType() => AccessTools.TypeByName(DtConsoleTypeName);

        // ── ① 拦截执行 ──────────────────────────────────────────────
        [HarmonyPatch]
        internal static class ExecuteHook
        {
            private static MethodBase Target()
                => AccessTools.Method(DtConsoleType(), "ExecuteCommand");

            [HarmonyPrepare]
            private static bool Prepare()
            {
                bool ok = Target() != null;
                if (!ok)
                    Plugin.Log.LogInfo("[HS] 未找到 DT_Tools 的 WebConsole.ExecuteCommand，hs_* 命令不可执行（其余功能不受影响）。");
                return ok;
            }

            [HarmonyTargetMethod]
            private static MethodBase TargetMethod() => Target();

            [HarmonyPrefix]
            private static bool Prefix(object __0)
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
            private static bool Prepare() => Target() != null;

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
