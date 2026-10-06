using System;
using System.Collections;
using System.Reflection;
using System.Text;
using HarmonyLib;
using HideAndSeek.Core;

namespace HideAndSeek.Console
{
    /// <summary>
    /// 把 hs_* 命令接入 DT_Tools 的 Web 控制台 —— 零改动 DT_Tools，全部走反射。
    ///
    /// ⚠ DT_Tools v1.0.9.0 把命令域整个重写了（<c>Console/</c> → <c>WebConsole/</c>，命令表改由
    /// <c>CommandRegistry</c> 统一持有），本桥接随之对接**新结构**：
    ///
    ///   ① 执行 —— Prefix <c>DT_Tools.WebConsole.WebConsole.ExecuteCommandText(string)</c>。
    ///      这是 WebUI 命令泵与 MCP <c>run_command</c> 的**唯一**执行收口。旧版的实例方法
    ///      <c>WebConsole.ExecuteCommand(PendingRequest)</c> 已被删除，所以这里改写的是新入口：
    ///      命中本模块前缀时自行处理，把结果反射包成 DT_Tools <c>CommandResult</c> 作为返回值，
    ///      再 <c>return false</c> 跳过原流程（否则原版按"未知命令"记一条 Warning）。
    ///   ② 列表 —— 直接重建 DT_Tools 命令表缓存 <c>DT_Tools.WebConsole.Api.CommandsApi._cachedJson</c>。
    ///      DT_Tools <c>/api/commands</c> 只在**首次请求**时构建该缓存（旧版的
    ///      <c>WebConsole.BuildCommandsJson</c> 已不存在）；本模块在自己的 Start 里
    ///      （早于任何浏览器请求）用「DT_Tools 全部命令 + hs_*」把它一次写好。
    ///
    /// 这是一个**可选**集成 —— 配置独立化之后，它是本插件唯一还与 DT_Tools 有关的地方（DT_Tools 对本模块是**可选依赖**：装了才谈集成，不装一切照旧）（配置侧只剩
    /// "把设置镜像到 DT_Tools CONFIG 页"这条便利路径，见 Core/DtBridge.cs）。之所以保留，是因为
    /// hs_* 目前没有别的执行入口。开关语义：
    ///   - [ConsoleBridge].Enabled = false ⇒ 两个补丁都不挂载，hs_* 完全不接进 DT_Tools（整类跳过）；
    ///   - DT_Tools 未安装 ⇒ HarmonyPrepare 让补丁类整体跳过，只记 INFO、不报错，其余功能不受影响；
    ///   - DT_Tools 已安装但方法缺失 ⇒ 记 WARNING（这才是真正需要关注的情况）。
    /// </summary>
    [PatchFeature(
        section: "ConsoleBridge",
        description: "把 hs_* 命令接入 DT_Tools 的 Web 控制台（可选集成，零改动 DT_Tools）。关掉本段即完全断开与 DT_Tools 的连接；未安装 DT_Tools 时自动跳过，不报错。",
        defaultEnabled: true,
        side: FeatureSide.Host)]
    internal static class ConsoleBridge
    {
        // ── DT_Tools v1.0.9.0 起的类型名。全部走反射，无编译期依赖。 ──
        private const string DtWebConsoleTypeName = "DT_Tools.WebConsole.WebConsole";
        private const string DtCommandsApiTypeName = "DT_Tools.WebConsole.Api.CommandsApi";
        private const string DtCommandRegistryTypeName = "DT_Tools.Commands.CommandRegistry";
        private const string DtCommandInterfaceName = "DT_Tools.Commands.ICommand";
        private const string DtCommandResultTypeName = "DT_Tools.Commands.CommandResult";
        private const string DtLogTypeName = "DT_Tools.Log";

        /// <summary>DT_Tools 旧版类型名（≤ v1.0.6.1）。只用于把日志说清楚，不再对接。</summary>
        private const string DtLegacyConsoleTypeName = "DT_Tools.Console.WebConsole";

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

        private static Type DtConsoleType() => AccessTools.TypeByName(DtWebConsoleTypeName);

        /// <summary>DT_Tools 是否已加载（只查类型，不判断其 Awake 是否执行）。</summary>
        private static bool UpstreamPresent => DtConsoleType() != null;

        /// <summary>
        /// DT_Tools 不在时把"没装"和"装了但结构对不上"分开说 —— 后者才是需要处理的。
        /// </summary>
        private static void ReportUpstreamAbsent()
        {
            if (AccessTools.TypeByName(DtLegacyConsoleTypeName) != null)
            {
                Plugin.Log.LogWarning(
                    "[HS] ConsoleBridge：检测到的是**旧版** DT_Tools（DT_Tools.Console.WebConsole）。" +
                    "命令桥自 DT_Tools v1.0.9.0 起只对接 WebConsole 新结构，本次 hs_* 不接入 DT_Tools；请更新 DT_Tools。");
                return;
            }

            Plugin.Log.LogInfo("[HS] ConsoleBridge：未检测到 DT_Tools，hs_* 命令桥自动跳过（其余功能不受影响）。");
        }

        /// <summary>
        /// 补丁挂上之后，重建 DT_Tools 的命令表缓存。
        ///
        /// 为什么需要这一步：DT_Tools <c>CommandsApi.HandleList</c> 只在**首次请求**时生成
        /// <c>_cachedJson</c>，而本插件的补丁要到**本插件的 Start** 才挂上。命令表本身是
        /// 进程内恒定的（CommandRegistry 在它自己的 Awake 里就 Load 完了），所以在 Start 里
        /// 用「DT_Tools 全部命令 + hs_*」把缓存一次写好即可 —— 此时浏览器尚未连上，不会覆盖有效内容。
        /// </summary>
        internal static void RefreshCommandList()
        {
            try
            {
                // 命令桥是可选集成：段被关掉、或 DT_Tools 没装，都必须**整类**跳过而不是只跳过一半 ——
                // 否则 hs_* 会出现在 DT_Tools 命令列表里，点了却没反应（ExecuteHook 没挂上）。
                // 这两种情况都只记 INFO，不产生 WARNING/ERROR。
                if (!Diagnostics.IsLoaded(Section))
                {
                    Plugin.Log.LogInfo($"[HS] ConsoleBridge：命令桥已关闭（[{Section}].Enabled = false），hs_* 不接入 DT_Tools 控制台。");
                    return;
                }

                if (!UpstreamPresent)
                {
                    ReportUpstreamAbsent();
                    return;
                }

                var api = AccessTools.TypeByName(DtCommandsApiTypeName);
                var field = api == null ? null : AccessTools.Field(api, "_cachedJson");
                if (field == null)
                {
                    Plugin.Log.LogWarning("[HS] ConsoleBridge：找不到 DT_Tools 命令表缓存 CommandsApi._cachedJson，hs_* 不会出现在命令列表（仍可执行）。");
                    return;
                }

                field.SetValue(null, BuildCommandsJson());
                Plugin.Log.LogInfo("[HS] ConsoleBridge：已重建 DT_Tools 命令表缓存，hs_* 现在应在列表与补全中可见。");
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
                => AccessTools.Method(DtConsoleType(), "ExecuteCommandText", new[] { typeof(string) });

            [HarmonyPrepare]
            private static bool Prepare()
            {
                if (Target() != null && AccessTools.TypeByName(DtCommandResultTypeName) != null)
                    return true;

                // DT_Tools 没装 ⇒ 这是正常情况（可选集成），只记 INFO；
                // 装了却找不到方法 ⇒ DT_Tools 内部结构变动，才值得 WARNING。
                if (UpstreamPresent)
                    Plugin.Log.LogWarning("[HS] ConsoleBridge：DT_Tools 已加载但找不到 WebConsole.ExecuteCommandText / CommandResult，hs_* 命令不可执行（其余功能不受影响）。");
                else
                    ReportUpstreamAbsent();
                return false;
            }

            [HarmonyTargetMethod]
            private static MethodBase TargetMethod() => Target();

            /// <summary>
            /// <c>__result</c> 声明成 <c>object</c>：DT_Tools CommandResult 是本模块无法编译期引用的类型，
            /// 对引用类型返回值 Harmony 允许这种通用形式，反射构造后赋回即可。
            /// </summary>
            [HarmonyPrefix]
            private static bool Prefix(string __0, ref object __result)
            {
                if (string.IsNullOrWhiteSpace(__0))
                    return true;

                string trimmed = __0.TrimStart('/', '!').Trim();
                var parts = trimmed.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length == 0 || !HsCommandRouter.IsHsCommand(parts[0]))
                    return true;   // 不是本模块的命令 → 交还原版

                Diagnostics.Hit("ConsoleBridge");

                string json;
                try
                {
                    json = HsCommandRouter.Execute(trimmed);
                }
                catch (Exception ex)
                {
                    json = "{\"ok\":false,\"error\":\"" + ex.Message.Replace("\"", "'") + "\"}";
                }

                object result = MakeResult(json);
                if (result == null)
                {
                    // CommandResult 构造失败（DT_Tools 结构又变了）——放行给原版：它会按"未知命令"
                    // 记一条警告，比让 HTTP 线程拿到 null 回包安全。
                    Plugin.Log.LogWarning("[HS] ConsoleBridge：构造 DT_Tools CommandResult 失败，hs_* 回包交给原版处理。");
                    return true;
                }

                __result = result;

                // DT_Tools WebUI 把「命令回包」与「日志流」当两条通道显示：只填 ResultJson 的话
                // 脚本拿得到结果，但终端文本区看起来"什么都不返回"（旧版踩过同一个坑）。
                RelayToUpstreamLog(json);
                return false;
            }
        }

        /// <summary>
        /// 反射构造 DT_Tools 的 <c>CommandResult</c>。
        ///
        /// data 优先用 Newtonsoft 的 <c>JToken.Parse</c> 解析成对象 —— 直接塞字符串的话，
        /// DT_Tools 序列化后会把整个信封再转义一层（data 变成一坨 \"），前端与控制台都难读。
        /// Newtonsoft 由游戏/BepInEx 提供，取不到时退回原字符串，命令结果不丢。
        /// </summary>
        private static object MakeResult(string json)
        {
            var type = AccessTools.TypeByName(DtCommandResultTypeName);
            if (type == null)
                return null;

            object data = TryParseJson(json);
            return AccessTools.Method(type, "Success", new[] { typeof(object) })
                ?.Invoke(null, new[] { data });
        }

        private static object TryParseJson(string json)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(json))
                    return null;

                var jtoken = AccessTools.TypeByName("Newtonsoft.Json.Linq.JToken");
                var parse = jtoken == null ? null : AccessTools.Method(jtoken, "Parse", new[] { typeof(string) });
                if (parse != null)
                    return parse.Invoke(null, new object[] { json });
            }
            catch
            {
                // 解析失败就走字符串回退，不打扰调用方
            }

            return json;
        }

        /// <summary>把结果同时写进 DT_Tools 日志流（tag: hs），使 WebUI 的日志/终端区能看到。</summary>
        private static void RelayToUpstreamLog(string json)
        {
            try
            {
                var log = AccessTools.TypeByName(DtLogTypeName);
                var info = log == null ? null : AccessTools.Method(log, "Info", new[] { typeof(string), typeof(string) });
                info?.Invoke(null, new object[] { "hs", json });
            }
            catch
            {
                // 日志写入失败不影响命令结果
            }
        }

        /// <summary>
        /// 生成 <c>/api/commands</c> 用的完整 JSON 数组：DT_Tools 全部命令（形状与 DT_Tools HandleList
        /// 完全一致）+ 本模块 hs_*。DT_Tools 命令元数据逐条经 ICommand 接口属性反射读取。
        /// </summary>
        private static string BuildCommandsJson()
        {
            var sb = new StringBuilder();
            sb.Append('[');
            bool first = true;

            var registry = AccessTools.TypeByName(DtCommandRegistryTypeName);
            var all = AccessTools.PropertyGetter(registry, "All")?.Invoke(null, null) as IEnumerable;
            if (all != null)
            {
                var iface = AccessTools.TypeByName(DtCommandInterfaceName);
                foreach (var command in all)
                {
                    if (command == null)
                        continue;

                    AppendCommand(
                        sb, ref first,
                        ReadMember(iface, command, "Name") as string,
                        ReadMember(iface, command, "Aliases") as IEnumerable,
                        ReadMember(iface, command, "Usage") as string,
                        ReadMember(iface, command, "Description") as string,
                        ReadMember(iface, command, "Author") as string);
                }
            }

            foreach (var hs in HsCommands)
                AppendCommand(sb, ref first, hs.Name, null, hs.Usage, hs.Description, "HideAndSeek");

            sb.Append(']');
            return sb.ToString();
        }

        /// <summary>经接口属性读取成员：命令类若用显式接口实现，直接 GetProperty 在具体类上取不到。</summary>
        private static object ReadMember(Type iface, object target, string member)
        {
            try
            {
                return iface?.GetProperty(member)?.GetValue(target);
            }
            catch
            {
                return null;
            }
        }

        private static void AppendCommand(
            StringBuilder sb, ref bool first,
            string name, IEnumerable aliases, string usage, string description, string author)
        {
            if (string.IsNullOrEmpty(name))
                return;

            if (!first)
                sb.Append(',');
            first = false;

            sb.Append("{\"name\":").Append(Quote(name)).Append(",\"aliases\":[");
            bool firstAlias = true;
            if (aliases != null)
            {
                foreach (object alias in aliases)
                {
                    if (!(alias is string text) || text.Length == 0)
                        continue;
                    if (!firstAlias)
                        sb.Append(',');
                    firstAlias = false;
                    sb.Append(Quote(text));
                }
            }

            sb.Append(']')
              .Append(",\"usage\":").Append(Quote(usage))
              .Append(",\"description\":").Append(Quote(description))
              // 与 DT_Tools 一致：未署名显示「佚名」
              .Append(",\"author\":").Append(Quote(string.IsNullOrEmpty(author) ? "佚名" : author))
              .Append('}');
        }

        private static string Quote(string value)
        {
            if (value == null)
                return "\"\"";

            var sb = new StringBuilder(value.Length + 2);
            sb.Append('"');
            foreach (char c in value)
            {
                switch (c)
                {
                    case '"': sb.Append("\\\""); break;
                    case '\\': sb.Append("\\\\"); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\t': sb.Append("\\t"); break;
                    default:
                        if (c < ' ')
                            sb.Append("\\u").Append(((int)c).ToString("x4"));
                        else
                            sb.Append(c);
                        break;
                }
            }

            sb.Append('"');
            return sb.ToString();
        }
    }
}
