using BepInEx;
using BepInEx.Logging;
using HarmonyLib;
using HideAndSeek.Core;

namespace HideAndSeek
{
    /// <summary>
    /// 入口，仅组装：打开自有配置、搬迁旧配置、装载功能补丁、镜像到上游页面、写日志。
    /// 不含任何玩法逻辑（与 DT_Tools 的 Plugin 约定一致）。
    /// </summary>
    [BepInPlugin(Guid, "HideAndSeek", Version)]
    public class Plugin : BaseUnityPlugin
    {
        public const string Guid = "YumeHatsuyuki.DeadlyTrick.HideAndSeek";
        public const string Version = "1.4.2";

        public static Plugin Instance { get; private set; }

        /// <summary>
        /// 本模块的 ConfigFile（命名为 HsConfig 以避免与 BaseUnityPlugin.Config 重名）。
        /// 固定是 <c>BepInEx/config/HideAndSeek.cfg</c>，与 DT_Tools 是否存在无关 ——
        /// 这份配置可以单独编辑，本插件也可以单独安装 / 卸载。
        /// 暴露出来是为了 /reload —— BepInEx 不监听 .cfg 变化，改文件后必须显式 Reload() 才会重读到内存。
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
            var config = HsConfigFile.Open();
            HsConfig = config;

            // 启动期批量 Bind 不做逐条落盘：BepInEx 的 SaveOnConfigSet 默认为 true，
            // 那会让 270 个配置项各写一次文件。全部 Bind 完成后统一 Save 一次，
            // 之后恢复为 true —— 此后**任何来源**的修改（大厅设置页 / DT CONFIG 页 / hs_* 命令）
            // 都会立即落到 HideAndSeek.cfg，不会再出现"改完退出就丢"。
            config.SaveOnConfigSet = false;

            // 首次独立运行：把旧 DT_Tools.cfg 里本模块的段搬进来。
            // 必须早于下面的 Bind —— BepInEx 只在键不存在时才用默认值，
            // 不搬迁等于把所有用户的定制静默重置为默认值。
            LegacyConfigImport.RunOnce(config, PatchLoader.OwnedSectionNames(), Logger);

            // 登记「详细设置」里的 HideAndSeek 页签与设置项。
            // 必须在 PatchLoader.Load 之前：那时 ConfigEntry 还没绑定，所以声明处用的是惰性委托
            // （见 LobbySettingItem 的注释）。UI 本身是懒加载的，注册在 Start 里必然来得及。
            Features.UI.HideAndSeekSettingsContent.RegisterAll();

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
            config.Save();

            // 启动期结束：此后任何写入都自动落盘（含经 DT CONFIG 页的修改）
            config.SaveOnConfigSet = true;

            // 装了 DT_Tools 就把这批条目镜像进它的 ConfigFile，使设置在它的 CONFIG 页里也能改。
            // 没装则只记一行 INFO，本模块独立运行。
            DtBridge.TryMirror(config, Logger);

            // 补丁挂上后重建 DT_Tools 的命令列表缓存 —— 它的缓存是在**自己的 Awake** 里
            // 生成的，早于本插件挂补丁；不刷新的话 hs_* 处于"能执行但不在列表/补全里"的状态。
            HideAndSeek.Console.ConsoleBridge.RefreshCommandList();

            Log.LogInfo(
                $"[HS] HideAndSeek {Version} 加载完成（DT_Tools {(DtBridge.HasDtTools ? "已检测到" : "未检测到")}）：" +
                $"启用 {result.EnabledCount}，跳过 {result.SkippedCount}，失败 {result.FailedCount}。" +
                $"配置：{config.ConfigFilePath}。" +
                $"捉迷藏模式当前 {(ModeRuntime.Active ? "开启" : "关闭")}（段 [{ModeRuntime.Section}].Enabled）。");
        }
    }
}
