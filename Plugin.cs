using System.IO;
using BepInEx;
using BepInEx.Logging;
using HarmonyLib;
using HideAndSeek.Core;

namespace HideAndSeek
{
    /// <summary>
    /// 入口，仅组装：初始化配置来源、装载功能补丁、写日志。
    /// 不含任何玩法逻辑（与 DT_Tools 的 Plugin 约定一致）。
    /// </summary>
    [BepInPlugin(Guid, "HideAndSeek", Version)]
    public class Plugin : BaseUnityPlugin
    {
        public const string Guid = "YumeHatsuyuki.DeadlyTrick.HideAndSeek";
        public const string Version = "1.0.2";

        public static Plugin Instance { get; private set; }

        /// <summary>
        /// 本插件的 ConfigFile（命名为 HsConfig 以避免与 BaseUnityPlugin.Config 重名）。暴露出来是为了 /reload —— BepInEx 不监听 .cfg 变化，
        /// 改文件后必须显式 Reload() 才会重读到内存。
        /// </summary>
        public static BepInEx.Configuration.ConfigFile HsConfig { get; private set; }
        public static ManualLogSource Log { get; private set; }

        private void Awake()
        {
            Instance = this;
            Log = Logger;
        }

        private void Start()
        {
            // 推迟到 Start：确保 DT_Tools 的 Awake 已执行，其 Plugin.Instance 可被探测到，
            // 从而把 HS_* 段写进同一个 ConfigFile（DT CONFIG 页面可见）。
            var config = DtBridge.ResolveConfig();
            HsConfig = config;

            var result = PatchLoader.Load(
                new Harmony(Guid),
                config,
                Logger,
                extraSections: new[] { (ModeRuntime.Section, ModeRuntime.BindAction(config)) });

            // 配置迁移：BepInEx 不会更新已存在 .cfg 里的旧默认值，
            // 必须在这里定向修正那些"旧值必然导致功能失效"的项（详见 ConfigMigration）。
            // 放在 Load 之后 —— 此时全部配置项都已绑定。
            ConfigMigration.Run(config, Logger);

            // 把当前全部配置段落盘：首次运行生成完整 .cfg，
            // 已存在时也只写入内存中的值（来自读取该文件），不会丢用户设置。
            // 之后运行期的修改仍遵循"内存优先、显式保存才落盘"的语义。
            config.Save();

            // 补丁挂上后重建 DT_Tools 的命令列表缓存 —— 它的缓存是在**自己的 Awake** 里
            // 生成的，早于本插件挂补丁；不刷新的话 hs_* 处于"能执行但不在列表/补全里"的状态。
            HideAndSeek.Console.ConsoleBridge.RefreshCommandList();

            Log.LogInfo(
                $"[HS] HideAndSeek {Version} 加载完成（DT_Tools {(DtBridge.HasDtTools ? "已检测到" : "未检测到")}）：" +
                $"启用 {result.EnabledCount}，跳过 {result.SkippedCount}，失败 {result.FailedCount}。" +
                $"捉迷藏模式当前 {(ModeRuntime.Active ? "开启" : "关闭")}（段 [{ModeRuntime.Section}].Enabled）。");
        }
    }
}
