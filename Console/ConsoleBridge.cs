using System;
using System.Reflection;
using System.Threading;
using HarmonyLib;
using HideAndSeek.Core;

namespace HideAndSeek.Console
{
    /// <summary>
    /// 把 hs_* 命令桥接到 DT_Tools 的 Web 控制台 —— 零改动上游。
    ///
    /// 做法：Prefix WebConsole.ExecuteCommand（:318，private）。命中本模块前缀时自行处理，
    /// 写回 PendingRequest.ResultJson 并 Set Done，然后返回 false 跳过原流程
    /// （否则原版会把它当"未知命令"）。非本模块命令一律放行。
    ///
    /// 上游未安装、或其内部结构变动导致方法缺失时，HarmonyPrepare 会让整个补丁类跳过，
    /// 不影响其余功能。所有访问都走反射，因此本模块不引用 DT_Tools.dll。
    /// </summary>
    [HarmonyPatch]
    [PatchFeature(
        section: "ConsoleBridge",
        description: "在 DT_Tools 的 Web 控制台提供 hs_* 命令（零改动上游；未安装 DT_Tools 时自动失效）。",
        defaultEnabled: true,
        side: FeatureSide.Host)]
    internal static class ConsoleBridge
    {
        private const string DtConsoleTypeName = "DT_Tools.Console.WebConsole";

        private static MethodBase ResolveTarget()
        {
            var type = AccessTools.TypeByName(DtConsoleTypeName);
            return type == null ? null : AccessTools.Method(type, "ExecuteCommand");
        }

        [HarmonyPrepare]
        private static bool Prepare()
        {
            bool ok = ResolveTarget() != null;
            if (!ok)
                Plugin.Log.LogInfo("[HS] 未找到 DT_Tools 的 WebConsole.ExecuteCommand，hs_* 命令不可用（其余功能不受影响）。");
            return ok;
        }

        [HarmonyTargetMethod]
        private static MethodBase TargetMethod() => ResolveTarget();

        [HarmonyPrefix]
        private static bool Prefix(object __0)
        {
            if (__0 == null)
                return true;

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
}
