# HideAndSeek 开发接手文档

> 《Deadly Trick》(Steam appid 3088400, 对照版本 **0.1.14b**) 的**房主端**捉迷藏玩法模块。
> BepInEx 5.4.23.5 + HarmonyX 2.x，`netstandard2.1`，Unity Mono，仅房主安装。
>
> 本文面向"从没看过这个项目的人"。读完应该能：独立改一个已有功能、加一个新段、跑一次构建与部署。
>
> **可信度约定**：文中凡涉及"游戏内部行为/客户端渲染条件"的结论，若本文档作者没有在代码里读到直接依据，
> 一律标注 `【推测】`；在代码里能直接读到依据的标 `【已确认】`。行号均指当前工作区代码（`main` @ `381c2c1`），
> 代码一改就会漂移，**用方法名而不是行号去定位**。

---

## 目录

1. [项目定位与约束](#1-项目定位与约束)
2. [目录结构与代码组织](#2-目录结构与代码组织)
3. [功能清单（33 个配置段全表）](#3-功能清单33-个配置段全表)
4. [核心机制说明](#4-核心机制说明)
5. [控制台命令与游戏内命令](#5-控制台命令与游戏内命令)
6. [构建、部署与验证](#6-构建部署与验证)
7. [踩坑清单](#7-踩坑清单)
8. [当前状态与待办](#8-当前状态与待办)
9. [附录 A：已知的代码 ↔ 注释/文档不一致](#附录-a已知的代码--注释文档不一致)

---

## 1. 项目定位与约束

**一句话**：一个**独立**的 BepInEx 插件，把《Deadly Trick》改造成房主侧的捉迷藏玩法；它与同仓库的
`DT_Tools`（BepInEx 插件框架）**只通过两条外部通道交互**，不改上游一个字节。

| 项 | 值 |
|---|---|
| 模块路径 | `D:\git\DT_Tools\HideAndSeek\`（独立 git 仓库；开发在 `dev`，发布打 tag） |
| 上游 | `D:\git\DT_Tools\DT_Tools\`（**绝对不可修改**） |
| 插件 GUID / 版本 | `YumeHatsuyuki.DeadlyTrick.HideAndSeek` / `1.4.0`（`Plugin.cs:16-17`） |
| 产物 | `bin\Release\netstandard2.1\HideAndSeek.dll` → 复制到 `BepInEx\plugins\` |
| 共享引用 | 游戏安装目录 `DeadlyTrick_Data\Managed\*.dll`（默认，唯一权威来源）+ `BepInEx\core\` 的 `BepInEx.dll` / `0Harmony.dll`。**不依赖 DT_Tools 仓库的 `libs/`** —— 本插件是独立仓库，单独 clone 只填 `GameDir` 即可构建 |

### 1.1 "零改动上游"的确切含义

【已确认】`HideAndSeek.csproj` 的引用列表里**没有** `DT_Tools.dll`。整个模块对上游的访问全部走**反射**，
因此：

- 上游可以不在（未安装 → 自动降级，见 1.2 / 1.3）；
- 上游改名/改签名 → 只影响对应的那一条通道，不炸加载（各处都有 `try/catch` 与 `HarmonyPrepare` 兜底）；
- 代价：编译期**没有任何**上游类型检查，上游一改就得靠日志发现。

**不要做**（与 `PLAN.md` §2.2 一致）：不引用 `DT_Tools.dll`、不在上游新增 `[PatchFeature]`、不改它的
`Plugin` / 命令注册 / WebUI 路由、不复制 `libs/`。

### 1.2 集成路径 A：配置独立 + 镜像到上游页面

**本模块的配置来源是自己的 `BepInEx/config/HideAndSeek.cfg`，与上游是否存在无关**（`Core/Config/HsConfigFile.cs`）。
这样配置文件可以单独编辑，插件也可以单独安装 / 卸载。

装了上游时，额外把**同一批 `ConfigEntryBase` 对象**注入上游的 `ConfigFile`（`Core/DtBridge.cs` +
`Core/Config/ConfigEntries.cs`），于是 DT CONFIG 页照旧列出并可修改本模块的设置。

| 项 | 内容 |
|---|---|
| 权威来源 | `BepInEx/config/HideAndSeek.cfg`（`HsConfigFile.Open()`，`Plugin.Start` 第一句） |
| 旧值搬迁 | `Core/Config/LegacyConfigImport.cs`：首次独立运行（自有文件尚不存在）时，把 `DT_Tools.cfg` 里**只属于本模块**的段搬进 `OrphanedEntries`。必须早于 Bind |
| 镜像实现 | 反射取上游 `ConfigFile` 的私有条目表 `<Entries>k__BackingField`，把本模块的条目逐个放进去 |
| 为什么不需要同步 | 两侧是**同一批对象**，值只有一份。经 DT 页面改写 `BoxedValue` ⇒ 本模块立即读到新值，并自动落到 `HideAndSeek.cfg` |
| 降级 | 上游不在、或取不到条目表 ⇒ 只记 INFO/WARNING，配置照旧写在自有文件，功能不受影响 |

**已知陷阱**

1. **镜像必须在全部 Bind 之后做。** 早一步就会镜像进一批残缺的条目（缺 `Enabled`、缺后面的子项）。
2. **段名不能与上游撞车。** 撞上时镜像会跳过该键（`DtBridge.TryMirror` 里显式跳过并计数）——
   覆盖上游的条目比"少显示几项"严重得多。目前 33 个段与上游 35 个段零重叠，`verify.ps1` 也在盯。
3. **上游 `Save()` 会把镜像条目一起写进 `DT_Tools.cfg`。** 这是镜像的固有代价：那个副本是上游页面
   自己维护的镜像，**不是**权威。改配置请改 `HideAndSeek.cfg`（或经 DT 页面改——那也会落回自有文件）。
4. **`SaveOnConfigSet` 的用法**：`Plugin.Start` 先置 `false`（避免 270 个配置项各写一次文件），
   全部 Bind + 迁移完成后统一 `Save()`，再置回 `true`。此后**任何来源**的修改都会立即落盘 ——
   不再有"改完设置退出就丢"这一类问题。
5. `DtBridge.HasDtTools` 只说明"类型存在"，不代表上游初始化成功。它不参与任何流程分支。
6. **反向搬迁（自有 → 上游）没有实现**：本模块从不写上游的文件。此前的定制留在
   `DT_Tools.cfg` 里不影响运行（首次搬迁已经把它搬过来了）。

### 1.3 集成路径 B：命令桥（可选）

| 项 | 内容 |
|---|---|
| 实现位置 | `Console/ConsoleBridge.cs`（本身也是一个 `[PatchFeature]` 段：`ConsoleBridge`） |
| 执行拦截 | `[HarmonyTargetMethod]` → `AccessTools.Method(DT_Tools.Console.WebConsole, "ExecuteCommand")`（上游 `WebConsole.cs:318`，**private 实例方法**）；`[HarmonyPrepare]` 找不到就整类跳过 |
| 拦截内容 | `Prefix(object __instance, object __0)`：`__0` 是上游私有类型 `PendingRequest`。`Traverse` 读字段 `Command`，命中 `hs` / `hs_` 前缀就交给 `HsCommandRouter.Execute`，把 JSON 写回 `ResultJson` 并 `Done.Set()`，`return false` 阻断原流程（否则上游会当成"未知命令"） |
| 附带日志 | 额外反射调用上游 `WebConsole.Log(json, LogLevel.Info)`；**不写这一条，命令确实执行了、JSON 也拿得到，但终端文本区看起来"什么都不返回"** |
| 列表补全 | `Postfix BuildCommandsJson`（上游 `WebConsole.cs:463`，private），把 13 条 `hs_*` 条目追加进它生成的 JSON 数组 |
| 开关语义 | 段关闭 / 上游未装 ⇒ 整类跳过且只记 INFO；上游**装了**但方法缺失 ⇒ 才记 WARNING（`Prepare` 里按 `UpstreamPresent` 分流） |

**已知陷阱（最容易被咬的两条）**

1. **命令列表缓存时序**：上游在 `WebConsole.Init`（由它自己的 `Awake` 触发，`WebConsole.cs:77`）
   里就执行了 `_cachedCommandsJson = BuildCommandsJson()`；而本模块的补丁要到**本模块的 `Start`**
   才挂上（BepInEx 顺序：所有 `Awake` → 所有 `Start`）。结果是 `hs_*` 处于
   "**能执行、但不在列表/补全里**"的状态。因此 `Plugin.Start` 必须显式调用
   `ConsoleBridge.RefreshCommandList()`（`Plugin.cs:52`），反射重算并写回 `_cachedCommandsJson`。
2. **`hs_debug` 不在命令列表里**：`ConsoleBridge.HsCommands` 数组只有 13 条，缺 `hs_debug`；
   而 `HsCommandRouter` 是支持 `hs_debug` 的。因此该命令**能执行、不出现在补全**。加命令时两处都要改。

### 1.4 加载时序（一张图记完）

```text
Unity 加载所有插件
  ├─ 上游 DT_Tools.Awake     : Plugin.Instance = this；注册它自己的 IConsoleCommand；
  │                            _cachedCommandsJson = BuildCommandsJson()   ← 此刻还没有 hs_*
  └─ HideAndSeek.Awake       : Instance / Log 赋值（Plugin.cs:22-26）

所有 Awake 结束 → 开始 Start
  └─ HideAndSeek.Start
       ① HsConfigFile.Open() + SaveOnConfigSet=false   ← 自有 cfg，与上游无关
       ② LegacyConfigImport.RunOnce(...)               ← 首次独立运行的旧值搬迁（必须早于 Bind）
       ③ HideAndSeekSettingsContent.RegisterAll()      ← 大厅设置页声明（惰性委托）
       ④ PatchLoader.Load(...)                         ← Bind 全部段 + 按 Enabled 决定 PatchAll
       ⑤ ConfigMigration.Run(config)                   ← 定向修正"旧默认值必然导致失效"的键
       ⑥ config.Save() → SaveOnConfigSet=true          ← 首次生成完整 .cfg；此后改动自动落盘
       ⑦ DtBridge.TryMirror(config)                    ← 装了上游才做：条目注入上游 ConfigFile
       ⑧ ConsoleBridge.RefreshCommandList()            ← 重建上游命令列表缓存（段关掉则整类跳过）
       ⑨ 日志：配置路径 / 启用 N / 跳过 M / 失败 F
```

---

## 2. 目录结构与代码组织

### 2.1 目录 ↔ 命名空间

命名空间与目录**严格一致**（这是硬约定）：

| 目录 | 命名空间 | 职责 |
|---|---|---|
| `Plugin.cs` | `HideAndSeek` | **只做组装**：打开自有配置 → 搬迁旧配置 → 装载补丁 → 迁移 → 落盘 → 镜像到上游页面 → 重建命令列表 → 日志。不含任何玩法逻辑 |
| `Core/` | `HideAndSeek.Core` | 基础设施：特性、配置绑定、补丁装载、模式开关、与上游的桥、游戏状态判定、文本模板、自检 |
| `Core/Attributes/` | `HideAndSeek.Core` | `PatchFeatureAttribute` / `ConfigFieldAttribute` / `FeatureSide` |
| `Core/Config/` | `HideAndSeek.Core` | `ConfigBinder`（扫描 `[ConfigField]` 自动 Bind）、`HsConfigFile`（自有 cfg）、`LegacyConfigImport`（旧值搬迁）、`ConfigEntries`（读写 ConfigFile 条目表，供镜像用） |
| `Core/Patching/` | `HideAndSeek.Core` | `PatchLoader`：扫描 `[PatchFeature]` → Bind + PatchAll；`OwnedSectionNames()` 供搬迁列段名 |
| `Features/<领域>/` | `HideAndSeek.Features.<领域>` | 一个功能 = 一个文件，一个段名。领域现为 `Vision` / `Combat` / `Weapon` / `Rule` / `Broadcast` / `Dummy` / `Skill` / `System` / `Dev` |
| `Console/` | `HideAndSeek.Console` | 两种 `hs_*` 的实现：`ConsoleBridge`（接入上游）与 `HsCommandRouter`（命令本体） |

**为什么 `Console/` 里没有 `Commands/`**：本模块的命令不走上游的 `IConsoleCommand` 注册表
（上游只扫自己的程序集，外部无法注入），而是集中在一个 `switch` 里（`HsCommandRouter.Execute`）。
新增 `hs_*` 命令要同时改三处（见 §2.4）。

### 2.2 装载流水线（`Core/Patching/PatchLoader.cs`）

```text
DiscoverFeatures(自己程序集)
  ├─ 只扫本程序集；重复段名直接抛 InvalidOperationException（启动即失败，不给"悄悄覆盖"的机会）
  └─ 收集 [PatchFeature] 的 Section / Description / DefaultEnabled / Side

统一 Bind（先全部 Bind，再统一 PatchAll）
  ├─ 把每个段的绑定动作 + extraSections(HS_Mode) 一起按段名 Ordinal 排序后调用
  │    → 保证同一段内 .cfg 里 Enabled 写在子项之前
  └─ BindFeatureSection = Bind("Enabled") + ConfigBinder.BindFields(该类全部 [ConfigField])

逐段装载
  ├─ enabled == false → SkippedCount++，打印「已跳过功能: XxxFeature ([段].Enabled = false)」，continue
  ├─ harmony.PatchAll(段的主类)
  ├─ 再对每个带 [HarmonyPatch] 的**嵌套类**单独 PatchAll（各自 try/catch）
  │    → 嵌套类拆分的意义：某个目标与游戏版本对不上时，日志能指出是哪一个，而不是整块失效
  ├─ 成功 → EnabledCount++；Diagnostics.MarkLoaded(段)
  └─ 失败 → FailedCount++，打印「功能加载失败: ...」
```

**关键推论（`【已确认】`，很多人会搞错）**：
"Bind 配置"与"挂载补丁"是**两件独立的事**。`PatchLoader` 对**所有**段都会 Bind 配置项，
被 `Enabled = false` 跳过的段只是不 `PatchAll` 而已。所以：

- 被跳过段的 `ConfigEntry` 字段**依然有值**，其它代码可以安全读取；
- 典型例子：`Dummy` 段默认关闭，但 `hs_dummy` 命令仍要读 `DummyFeature.NamePrefix` /
  `DefaultCharacters` 来造假人 —— 之所以能工作，就是因为配置照样 Bind 了。

### 2.3 两层开关

| 开关 | 位置 | 生效方式 | 典型用途 |
|---|---|---|---|
| **段级** `[段].Enabled` | `.cfg` 每段第一项 | **启动时**决定是否 `PatchAll`；改后**必须重启** | 关掉整个功能（例：不要 AOI、不要播报） |
| **总开关** `HS_Mode.Enabled` | `.cfg` 的 `[HS_Mode]` | **运行期热开关**：每个 `Prefix` 首行 `if (ModeRuntime.Bypass) return true;` | 一局之内临时开关整个玩法 |
| 段内子项 | `[ConfigField]` 生成的键 | 读 `ConfigEntry.Value`，多数**即时生效** | 调参数 |

- `ModeRuntime.Bypass => !Active`（`Core/ModeRuntime.cs:21`），是一个**约定**而不是强制机制：
  新写的功能必须在每个入口首行自己判断，漏了就等于"关掉模式后这个功能还在跑"。
- 关闭模式后的**状态回滚**在 `Core/ModeWatchdog.cs`，由 `Features/System/ModeWatchdogFeature.cs`
  挂在 `GameRoom.SurvivalTick` 上驱动：检测到"由开变关"就重发真实区域光照（`Area.SendAreaInfo`）
  并重建全员可见性（`AreaManager.SearchAndUpdatePlayer`），否则会残留"黑方还看不见人 / 大厅一直黑"。
- `【已确认（代码路径）/怀疑实际时机】` `SurvivalTick` 是**生存阶段**的 tick，大厅阶段不跑。
  因此在大厅里关模式，回滚不会立刻发生。

### 2.4 新增一个功能 / 一次改动的最短路径

**加一个段（新功能）**

1. 在对应 `Features/<领域>/` 下建 `XxxFeature.cs`，`namespace` 与目录一致。
2. 类上写 `[PatchFeature(section:"Xxx", description:"...", defaultEnabled:true, side:FeatureSide.Host)]`；
   方法上写 `[HarmonyPatch(...)]`；子项写 `[ConfigField(default, "说明", Min=, Max=)]` + `public static ConfigEntry<T> ...`。
3. **不要**改 `Plugin.cs`、**不要**手写 `Config.Bind`（`ConsoleBridge` 段是唯一例外）。
4. 若功能有"开局/回大厅要清零"的静态状态，自己挂 `StartSurvive` / `StartLobby` / `StartDetective` 清理。
5. 想被 `hs_check` 看见触发次数，就在入口调 `Diagnostics.Hit("段名")`。

**加一个 `hs_*` 命令（三处都要改）**

1. `Console/ConsoleBridge.cs` 的 `HsCommands` 数组（否则不在列表/补全里）；
2. `Console/HsCommandRouter.cs` 的 `switch (name)`；
3. 若要支持 `hs xxx` 空格写法，还要加进 `HsCommandRouter.Execute` 里那个 `case` 白名单（`HsCommandRouter.cs:45-47`）。

---

## 3. 功能清单（33 个配置段全表）

- **全部 33 个段的 `side` 都是 `FeatureSide.Host`**（`【已确认】`：33 处声明无一例外），
  所以下表省掉该列 —— 本模块只有房主侧逻辑，客户端不需要装任何东西。
- "默认开关"= `[PatchFeature(defaultEnabled:)]`，也就是**首次生成 `.cfg`** 时 `[段].Enabled` 的值。
- 段名**必须保持稳定**，否则老 `.cfg` 里的用户设置会失效（等于换了新段）。

### 3.1 Vision（视野，6 段）

| 段名 | 文件 | 默认 | 一句话作用 |
|---|---|:--:|---|
| `AoiCulling` | `Features/Vision/AoiCullingFeature.cs` | **开** | 黑方只能"看见"附近的玩家（模型与小地图 pin 同时消失）；对时停/小熊自动豁免 |
| `WhiteRadar` | `Features/Vision/WhiteRadarFeature.cs` | **开** | 白方全图雷达（`RadarOn` 子项默认 **false**，即"功能可挂载但雷达默认不开"）；两种外观方案可热切换 |
| `MiyukiScan` | `Features/Vision/MiyukiScanFeature.cs` | **开** | 美幸（107）被动：每 15 秒扫描全图。黑方临时解除 AOI，地图标记 1 秒实时 + 2 秒静止后消失 |
| `BlackVision` | `Features/Vision/BlackVisionFeature.cs` | **开** | 黑方恒黑灯视野（等同真停电的表现），只在生存阶段维持，离开阶段不干预 |
| `StartFlash` | `Features/Vision/StartFlashFeature.cs` | **开** | 捉迷藏开始灯效：坏灯式闪烁，收尾白方亮 / 黑方暗；默认由"有人拿刀"触发 |
| `ProximityAlert` | `Features/Vision/ProximityAlertFeature.cs` | **关** | 黑方靠近时给白方播放警示音（默认关闭且音效为 `none`） |

### 3.2 Combat（对抗手感与黑方强度，6 段）

| 段名 | 文件 | 默认 | 一句话作用 |
|---|---|:--:|---|
| `KillLimit` | `Features/Combat/KillLimitFeature.cs` | **开** | 解除原版 1~2 杀硬上限（`get_BlackKillLimit` 改写为 9999） |
| `WeaponCooldown` | `Features/Combat/WeaponCooldownFeature.cs` | **开** | 缩短击杀后重新可出刀的冷却（原版 20 秒 → 默认 10 秒）；拔刀后的 5 秒锁不动 |
| `SpeedBoost` | `Features/Combat/SpeedBoostFeature.cs` | **开** | 黑方移速倍率（`BlackSpeedMul` 默认 1.0 = 原版） |
| `LunaImmunity` | `Features/Combat/LunaImmunityFeature.cs` | **开** | 露娜及其能力持有者的**服务端**免疫普通刀杀（真停电 / 致命诡计仍可破防） |
| `KillTimeBonus` | `Features/Combat/KillTimeBonusFeature.cs` | **开** | 黑方每杀一人给倒计时**加**时（默认 +30 秒，设 0 关闭） |
| `KillUpgrade` | `Features/Combat/KillUpgradeFeature.cs` | **开** | 黑学分：击杀攒学分，换取视野 / 移速 / 任务门槛三项强化（每项最多 3 级） |

### 3.3 Weapon（武器供给，1 段）

| 段名 | 文件 | 默认 | 一句话作用 |
|---|---|:--:|---|
| `WeaponGrant` | `Features/Weapon/WeaponGrantFeature.cs` | **开** | 自行跑刀（默认）或开局直接给随机一人发刀；自动发刀时会锁死武器架并没收第二把刀 |

### 3.4 Rule —— 胜负与规则（10 段）

| 段名 | 文件 | 默认 | 一句话作用 |
|---|---|:--:|---|
| `NoMasterMind` | `Features/Rule/NoMasterMindFeature.cs` | **开** | 开局不分配黑幕（Dark），改由玩家自行跑刀转黑方 |
| `CorpseReport` | `Features/Rule/CorpseReportFeature.cs` | **开** | 禁止尸体报告（不进入调查/裁判）；手动报警被改写成"搬起尸体" |
| `WhiteWinOnTimeout` | `Features/Rule/WhiteWinFeature.cs` | **开** | 限制时间归零判**白方**胜利；可要求最低任务进度，未达标则判黑方胜 |
| `BlackWin` | `Features/Rule/BlackWinFeature.cs` | **开** | 非露娜系白方全部淘汰 → 判黑方胜利（复用原版 `GameOver`：项圈自爆 + 7.5 秒结算） |
| `MissionTimePenalty` | `Features/Rule/MissionTimePenaltyFeature.cs` | **开** | 完成任务**减少**限制时间（原版为增加） |
| `RuleRewrite` | `Features/Rule/RuleRewriteFeature.cs` | **开** | 实验性规则引擎：按剩余时间/击杀数/存活数自动重写玩法参数 |
| `PowerRepair` | `Features/Rule/PowerRepairFeature.cs` | **开** | 电力恢复条件放宽：已修电箱数达标即恢复供电（默认 1 = 修好任意一个） |
| `FuseboxReveal` | `Features/Rule/FuseboxRevealFeature.cs` | **开** | 把"可拆电箱"标记包也发给黑方（原版只发黑幕），让黑方地图/平板上看得见目标 |
| `LockDoor` | `Features/Rule/LockDoorFeature.cs` | **开** | 锁门命令的底层：白方看到原生锁定，黑方看到"只是关着"从而能按 E 秒解 |
| `GhostPhaseRefresh` | `Features/Rule/GhostPhaseRefreshFeature.cs` | **开** | **【临时补丁】**操作发信机时被刀杀死的人会整段 Survive 拿不回操作权 ⇒ 服务端只对他定向补一次「当前阶段」刷新（走原版换阶段的解锁路）；官方修复后应整段移除 |

### 3.5 Rule —— 命令通道（3 段）

| 段名 | 文件 | 默认 | 一句话作用 |
|---|---|:--:|---|
| `BreakCommand` | `Features/Rule/BreakCommandFeature.cs` | **开** | **黑方**密聊命令通道（`/brk` `/lck` `/tp` `/ls` `/cre` `/help`） |
| `WhiteCommand` | `Features/Rule/WhiteCommandFeature.cs` | **开** | **白方**公开聊天命令通道（`/rad` `/sta` `/rep` `/help`） |
| `TeleportCommand` | `Features/Rule/TeleportCommandFeature.cs` | **开** | `/tp` 的传送本体：预警若干毫秒后落到"目标在发起那一刻的位置" |

### 3.6 Broadcast（播报，1 段）

| 段名 | 文件 | 默认 | 一句话作用 |
|---|---|:--:|---|
| `Broadcast` | `Features/Broadcast/BroadcastFeature.cs` | **开** | 进房规则介绍、开局身份提示、死亡通告、拿刀通告；文本全部可配 |

### 3.7 Dummy（假人，2 段）

| 段名 | 文件 | 默认 | 一句话作用 |
|---|---|:--:|---|
| `Dummy` | `Features/Dummy/DummyFeature.cs` | **关** | 假人玩家：自动生成 / 回大厅重建 / 拦截结算免费货币兑换 |
| `DummyPick` | `Features/Dummy/DummyPickFeature.cs` | **开** | 让假人按指定角色真正参与选角（**含手动 `hs_dummy` 生成的假人**） |

> **这两个段为什么要拆开**：`Dummy` 默认关闭 → `PatchLoader` 对整段跳过 `PatchAll`，
> 连"选角"这种基础能力也不会挂。而命令桥是独立通道、不受段开关影响，
> 于是会出现最糟的组合：**`hs_dummy` 能造出假人，却没有任何钩子替它选角**。
> 因此基础能力（选角）独立成 `DummyPick` 并默认启用。这是本仓库最值得记住的一个结构教训。

### 3.8 Skill / System / Dev / Console（5 段）

| 段名 | 文件 | 默认 | 一句话作用 |
|---|---|:--:|---|
| `TeleportGuard` | `Features/Skill/TeleportGuardFeature.cs` | **开** | 黑洞（Noel）防滥用：落点改为就近出生点，并把黑洞特效画在真实落点 |
| `ModeWatchdog` | `Features/System/ModeWatchdogFeature.cs` | **开** | 驱动模式开关监视：关闭模式时回滚黑方可见性与光照 |
| `RoomName` | `Features/System/RoomNameFeature.cs` | **开** | 修改房间在 Steam 房间列表里显示的名字（只有房主可改） |
| `SoloPlay` | `Features/Dev/SoloPlayFeature.cs` | **关** | 单人/少人开局（测试向）：放开开始游戏所需最少玩家数 |
| `ConsoleBridge` | `Console/ConsoleBridge.cs` | **开** | 把 `hs_*` 接入上游 DT Web 控制台（未装上游时自动失效） |

### 3.9 段级默认关闭的三个段（排查时先看这里）

| 段 | 关闭时会发生什么 |
|---|---|
| `Dummy` | 自动生成假人、回大厅重建、选角重试、官方上报拦截**全部不挂**；手动 `hs_dummy` 仍能用，选角由 `DummyPick` 兜底 |
| `ProximityAlert` | 接近预警不挂（且该段内 `Enabled` 子项默认也是 false，双重关闭） |
| `SoloPlay` | 最少玩家数保持原版（正式服 5），单人房按开始无反应 |

---

## 4. 核心机制说明

### 4.1 AOI 视野裁剪 —— `Features/Vision/AoiCullingFeature.cs`

**机制**：复用原版自己的"兴趣区域"原语。`X.SharedPlayers` 的语义是"能看到 X 的人"；
服务端 `AddPlayer(other)` 向观察者发 `S_SPAWN`、`RemovePlayer(other)` 发 `S_DESPAWN`，
位置广播只发给 `SharedPlayers`。把远处的玩家从黑方的观察列表移除，黑方客户端上
**3D 模型、小地图 pin、以及 `GetTargetPlayer` 索敌会同时消失**（这是"看不见"而不是"拿不到"，
服务端仍全量广播位置 —— 见 §8 已知限制）。

**关键位置**

| 环节 | 位置 |
|---|---|
| 加入闸门（拒绝把远处的人介绍给黑方） | `PrefixAddPlayer`（`:217`） |
| 记录首次可见时间（供最短可见保护） | `PostfixAddPlayer`（`:242`） |
| 黑方移动时立即校正（消除"靠近慢一拍"） | `MoveHook`（`:256`，挂 `Player.Move(PosInfo,bool)` 重载） |
| 每秒兜底（补加 + 剔除） | `PostfixSurvivalTick`（`:276`） |
| 新局清空可见时间记录 | `PostfixStartSurvive`（`:352`） |
| 二次修正圆心/半径 | `Resolve`（`:165`）、`RevealNearby`（`:192`） |

**设计理由（为什么必须自己补 `AddPlayer`）**

原版让某人**重新可见**的唯一途径是**被观察者自己移动**
（`Player.Move` → `AreaManager.SearchAndUpdatePlayer` → `AddPlayer`）。
站着不动的目标一旦被 `RemovePlayer`，客户端上那个 `Player` 对象已 despawn，**再没有任何路径被加回来**，
走到跟前也看不见。所以这里必须双向维护：黑方移动时立即校正 + `SurvivalTick` 每秒兜底，两处都主动 `AddPlayer`
（`AddPlayer` 幂等，重复调用无害）。`PosfixStartSurvive` 清 `VisibleSince` 同理：不清的话新局 `SurviveTime`
从 0 重新计时，`now - since` 变负，恒小于最短可见时间，那批人将**永远不被剔除**。

**滞回（内圈/外圈）**

| 参数 | 当前默认 | 作用 |
|---|---|---|
| `EnterRange` | **700** | 进入可见范围的距离 |
| `ExitRange` | **900** | 离开可见范围的距离（必须明显大于进入距离） |
| `MinVisibleSeconds` | 3 | 进入后至少保持这么久不被剔除 |

区间 `(700, 900]` 内**维持现状、不增不减**，这就是滞回，避免边界抖动
（位置采样粒度约 72.8 单位/包，滞回必须远大于它）。
`Resolve()` 里的兜底常量仍是旧的 `750f/1100f` —— 见附录 A。

**Hide 状态与幽灵的跳过**

`State == EPlayerState.Hide` **有两个来源**，两个都要跳：

| 来源 | 位置 | 说明 |
|---|---|---|
| **活人躲进柜子** | `Cabinet.HideCabinet`（`:162129`）| 置 `State = Hide` + `HidePlayer`；出柜由 `ExitCabinet`（`:162138`）复位成 `Idle` |
| **死亡 / 幽灵** | `MakeSpectatorGhost`（`:175590`）、`ExitPlayer`（`:176098`）| 置 `Hide` + `IsGhost = true` |

原版 `SearchAndUpdatePlayer`（`:173429`）会跳过他们。我们主动补 `AddPlayer` 时必须同样跳过
（`RevealNearby`），否则会把死人的幽灵、以及柜子里的活人一起塞给黑方；反过来在 tick 里对
`Hide` 状态还要**主动 `RemovePlayer`**（`PostfixSurvivalTick`），因为它可能从别处进过 `SharedPlayers`。

> ⚠️ 只把这条理解成"死亡幽灵"是常见误读 —— `Hide` 同时是"躲柜子"。
> 本节旧版本引用的 `AoiCullingFeature.cs :209` / `:314-317` 早已失效（指向的是 `WallBlocks`
> 与范围辅助函数），现已改为方法名引用，别再按行号找。

**与技能豁免的交互（`SkillAware`）**

裁剪会顺带切断"客户端本地索敌"，而有两类技能恰恰依赖它：

| 技能 | 问题 | 处理 | 数值来源 |
|---|---|---|---|
| 时停（Seol, `TimeStop`） | 客户端 `GetSkillTarget` 只遍历**本地已生成**玩家，半径不够时 `CanUseSkillCondition = false`，按键毫无反应 | `Resolve()` 里把阈值放宽到 `TimeStopSearchRange` | 672 = `SkillData.Range` 3.0 × 224 |
| 小熊（Rin, `Marionette`） | 探测是**纯客户端本地判定**，圆心是**召唤物坐标**（召唤物固定在自己脚下、不随人移动） | `TryMiniCenter` 把圆心改为召唤物坐标，半径改用召唤物自己的探测圈 | 进 448 / 出 648（滞回 200） |

召唤物坐标经 `SkillComponent._summonId`（**私有字段，反射读取**）→ `DeviceManager.GetSummon(id)` 取得；
反射失败会**熔断**（`_summonIdFailed = true`）并打一条警告，只降级小熊豁免，不影响其余逻辑。
半径用 448 而不是 900 是刻意的：用 900 会让黑方可视范围几乎翻倍。

**已知限制**

- 只隐藏"表现"，不隐藏数据：服务端仍全量广播位置，**读内存的作弊工具依然可见**。这是刻意取舍 ——
  若在服务端掐断位置下发，会连 3D 模型一起消失，黑方无法选中目标出刀。
- 裁剪依赖 `GameRoom.Players` 而不是 `AlivePlayers`（`:290` 有注释）：假人可能不在 `AlivePlayers` 里。
- 小熊豁免的代价：黑方小地图会为召唤物附近的人生成 pin，而原版该技能只给 HUD 提示、不给位置（配置项注释里已写明）。

### 4.2 白方雷达 —— `Features/Vision/WhiteRadarFeature.cs`

**机制**：需求是"白方在地图上看到所有存活玩家的位置，**不区分阵营、不暴露是谁**"。
但客户端的 pin 绘制有一段硬门控：`RefreshPlayerPin` 开头 `if (myPlayer.Color == EPlayerColor.White) return;`
—— **白方永远进不到绘制循环**，服务端也没有"下发白点"的包。于是只有两条路，做成可热切换的 `Mode`：

| 方案 | 做法 | 观感 | 代价 |
|---|---|---|---|
| `Badge`（默认） | 走 `S_PIN_MOVE`，但用**非玩家 id**（`PinIdBase = 90000` + `PlayerId`）；客户端查 `PlayerCache` 必然为 null → 不设头像、徽章保留预制体默认贴图、且不生成世界箭头 | 白点 + 绿环徽章 + 镜像小箭头 | 零副作用，不暴露身份 |
| `PureDot` | 发 `S_MODIFY_MY_PLAYER{ChangeColor, 3}` 让客户端"以为自己不是白方"，放行原生白点绘制 | 纯净白点 | **客户端硬编码的代价绕不开**：状态面板消失、目标文本空白、**雷达期间无法与武器库交互取武器**；结算前必须复原。因此配 `DurationSeconds` 限时脉冲 |

**关键位置与设计点**

- `PinIdBase = 90000`（`:67`）：刻意避开真实 `PlayerId`，让客户端查不到缓存。
  `RadarMissionType = 99`（`:70`）避开 `38/39` 这两个已被占用的任务 pin 类型；
  `FakeColorValue = 3`（`:73`）避开 `Black(1)` / `Dark(2)` 分支。
- `SendPin`（`:174`）：`Pos = null` 时发 `(0,0)` 删除哨兵。
- `IsActive` / `SetActive(bool)`（`:78` / `:84`）：`SetActive(false)` 会**先撤销 PureDot 再清 pin**。
- 撤销而不是跳过（`:254-264`）：阵亡/躲藏/旁观的玩家必须**撤销 pin**。原因很关键 ——
  白方阵亡瞬间会收到全量 `S_NOTIFY_BLACK`，pin 若还在就会被 `RefreshBlackPin` 命中并 `TurnBlack`，
  于是"雷达点"变成"红点"，等于泄露黑方身份。
- 位置换算/`IsForce`：**这一处注释自相矛盾，必须以实测为准**。
  代码传的是 `IsForce = true`，其上方注释写成"`IsForce = true` 是关键"，紧跟着另一段注释又写
  "`IsForce` 必须为 false，与原版 `SendTraceTarget` 一致"，并说明两种取值走不同的客户端方法
  （`SetTargetPosition` vs `SetLocalPosition`）。详见附录 A 第 1 条。
- 生命周期：`StartLobby` / `StartDetective` 两个 Postfix 都会关闭雷达并还原假颜色（`:277` / `:294`）。
  非 `Survive` 阶段在 tick 里也会先停手并清干净 —— 在审判阶段下发 `S_PIN_MOVE` 会让客户端 NRE。

**已知限制**：`PureDot` 会破坏武器库交互与结算，务必配限时；`Badge` 的小箭头是徽章的副产品。

### 4.3 美幸扫描 —— `Features/Vision/MiyukiScanFeature.cs`

**机制**：美幸（`CharacterId 107`）的捉迷藏被动，每 15 秒扫描全图一次。**黑白两条路径完全不同**：

| 路径 | 做法 | 为什么 |
|---|---|---|
| **黑方：临时解除 AOI** | `TriggerScan` 里把该黑方加入 `Unlocking`，AOI 的闸门（`PrefixAddPlayer`）与 tick 循环都对其放行，并主动 `AddPlayer` 所有人 | 黑方小地图只渲染**游戏内可见的人**；远处的人先得在游戏内可见，地图上才会有东西可更新。**只发 pin 是不够的** |
| **白方：只发地图 pin** | 复用 `WhiteRadarFeature.SendPin` 同一批 pin id | 白方本来就没有 AOI 通道 |

**实时段与静止段的区分（快照重发）**

时间线（默认参数 `UnlockSeconds = 1`、`MarkerSeconds = 3`）：

```text
t=0      触发扫描：记录快照；黑方 Unlocking.Add + 主动 AddPlayer；白方发 pin
t=0~1s   实时段（now < LiveUntil）：每秒重发 pin → 地图标记跟随真实位置；
         同时 SnapshotPins 持续刷新快照
t=1s     撤销解锁：Unlocking.Remove → ReapplyCull 把超范围的人 RemovePlayer 收回；
         此后不再发"实时"pin
t=1~3s   静止段：SendSnapshotPins 用**快照位置**继续重发 → 标记既存活又静止
t=3s     发删除哨兵 → pin 消失
```

**为什么静止段还要重发**：客户端 pin 有存活时间，不重发就会提前消失
（实测现象："1 秒刚过白点就没了"）。所以用快照位置重发 —— 既保活，又保持静止，
这就是需求里的"停留两秒"。这也是 `PinSnapshot` 的全部用途。
`SendSnapshotPins` 还会把"快照里没有的人"的 pin 清掉（可能在这 2 秒内死亡/躲进柜子）。

**关键位置**：`HasMiyukiAbility`（`:73`）、`TickHook`（`:122`）、`TriggerScan`（`:194`）、
`SendAllPins`（`:233`）、`SnapshotPins`（`:258`）、`SendSnapshotPins`（`:279`）、
`ReapplyCull`（`:317`）、`Reset`（`:368`）。

**两条容易漏的细节**

1. **黑方不发 pin**（`:217-226`）：AOI 解封后原版黑方地图本来就会显示所有人，
   再叠我们那套反而会多出白色方块徽章（`TurnComplyRules` 的副产品）。
2. **能力判据是"技能"而不是"角色"**（`HasMiyukiAbility`）：本人是美幸，**或**技能被 RuleBreaker
   换成美幸技能的人（Soi 偷取）都算。与 `GameRefs.IsLunaSide` 同思路 ——
   服务端权威判据是技能，否则偷到能力的人拿不到效果。

**已知限制**：`Reset()` 清了 5 个字典/集合，**却没有清 `PinSnapshot`**（见附录 A 第 6 条）。

### 4.4 时停命令 `DoStasis` —— `Features/Rule/WhiteCommandFeature.cs`

**最终做法**：**完全复刻原版的 `UseTimeStop`** —— 对每个黑方
`BuffComponent.AddBuff(EBuffType.TheWorld, 秒数 × 1000)`（`:408-420`），外加
`BroadcastWorldSFX(ESoundType.TheWorldSfx, 施法者坐标)`。

**为什么是 Buff，而不是别的**（两次失败尝试的记录都在代码与提交里）：

| 尝试 | 做法 | 结果与失败原因 |
|---|---|---|
| ① 改速度 | 让黑方移速倍率归零 / 走 `SpeedBoost` 的乘法 | **静默失效**。`SpeedBoostFeature.cs:45-46` 的注释直接写了这个坑：早期写法里 `if (mul <= 0f) return;` 让"把 `BlackSpeedMul` 设成 0 想减速"完全没效果 —— 后来收紧成"只在 `abs(mul-1) < 0.001` 时放行"就是为了修它。另一层原因是**速度是客户端本地权威**（服务端 `HandleMove` 只校验坐标合法性），把服务端速度改小并不能让客户端停下。`【推测】` 后半句代码里没有直接注释，是从"放大速度不会回拉"的注释对称推出来的 |
| ② 拦截服务端 `Player.Move` | `MoveFreezeHook` 拦 `Player.Move` | **不是真正的时停**。文件里保留了删除说明（`:475-476`）："原版时停走 `TheWorld` buff（客户端把动画 `timeScale` 置 0 并锁操作），不需要、也不应该由我们去拦截服务端 `Player.Move`"。真正的时停效果发生在**客户端**：`SkeletonAnim.timeScale = 0` + `PlayGray` + `_lockControlStack++` + `CancelAllInteract`，服务端拦包做不到这些 |
| ③ **复刻原版** | 逐个黑方 `AddBuff(TheWorld, ms)` | 走的是原版同一条路径，客户端效果、音效、时长语义全部免费对齐；只对黑方施加，不波及白方 |

**消耗与冷却**：`/sta` 消耗任务进度（默认 5%，见 `TrySpendProgress` `:329`），冷却默认 90 秒。
扣进度的**基数是 `GoalPoint` 而不是 `CurrentPoint`** —— 旧写法按当前值算，5/45 时 5% 只有 0.25、
被下限抬到 1，进度条上根本看不出来，用户因此认定"完全没扣"。
扣完必须 `BroadcastMissionState()`（`:275`）把进度广播出去，否则只有服务端字段变了、客户端进度条不动。
注意 `BroadcastMissionState` 的注释里提到 `S_MISSION_STATE`，但**实际发的包是
`S_MISSION_PROGRESS_PERCENT`**（`:300`）—— 注释里已说明前者第一行就是"非房主直接丢弃"，所以只能用后者。

**残留**：`_savedSpeed` 字段与 `StasisTickHook` 的"还原移速"分支是尝试 ① 的遗骸 ——
`_savedSpeed` 现在**从未被赋值**，`IsStasisActive` 属性也**没有任何调用者**（`MoveFreezeHook` 已删）。
功能上无害，但读代码时会造成误判。见附录 A。

### 4.5 黑学分升级 —— `Features/Combat/KillUpgradeFeature.cs`

**定义（必须记牢这三个词）**

| 名词 | 定义 | 代码 |
|---|---|---|
| **池子** `PoolTotal` | 整局可发放的学分总量（默认 100）。**杀光全部白方恰好发完整个池子** | `:40-41` |
| **每股** `share` | 每杀一人发放的量 = 池子 ÷ **分母** | `:127` |
| **分母** `divisor` | **可击杀人数 = 开局白方总数 − 1**（不是当前存活数，也不是白方总数） | `:122-123` |

**为什么分母是"开局总数 − 1"，而不是"当前存活数"**

- 用**当前存活数**当分母 → 越杀越小、每次所得越来越大 → 总发放量远超池子。
  实测证据写在注释里：8 人房 6 杀能拿到 **159**（池子只有 100），于是能升 8 级而不是 4 级。
- 用**开局白方总数**当分母 → 低估每股：7 白时 100/7 = 14.3，杀满 6 人仅 85.7，差 4.3 就能点第 5 级。
- 用**开局白方总数 − 1** → 每股 100/6 = 16.7，杀满正好 100，可升 5 级。
  理由是"**最后一名白方是黑方的胜利条件**（杀了就结束），不会被计入击杀收益"。
- 开局总数在 `StartSurvive` 的 Postfix 里记录（`CountWhitesHook` `:329`），整局固定；
  露娜计入分母（她确实占一个白方名额）。
- 每个方向的等级上限 = `MaxLevelPerItem`（默认 3），每级成本 = 池子 × `CostPercentPerLevel`（默认 18%）。

**三个方向如何生效 —— 全部通过"改写既有功能的配置项"**（`ApplyUpgrades` `:264`）

| 方向 | 改写对象 | 公式 |
|---|---|---|
| `vision`（视野追踪范围） | `AoiCullingFeature.EnterRange` / `ExitRange` | `基础值 × (1 + 0.6 × 等级)`，3 级 = 2.8 倍 |
| `speed`（移动速度） | `SpeedBoostFeature.BlackSpeedMul` | `基础值 + 0.1 × 等级` |
| `task`（任务门槛） | `WhiteWinFeature.MinMissionProgress` | `基础值 + 10 × 等级`（取整） |

"先还原再套用"：首次改写前把原值记进 `_baseEnter/_baseExit/_baseSpeed/_baseTask`，
之后每次都从基础值重算，避免叠加。`StartLobby` / `StartDetective` 的 `ResetAll()` 会把被改写的配置**还原**，
否则跨局继续生效。**这是"用改配置实现效果"的通用范式：记基础值 → 幂等重算 → 生命周期末还原。**

**升级文本**（`UpgradeText` `:207` / `HelpText` `:183` / `Status` `:233`）

- 播报口径：只说"提升到哪里"，**不透露剩余积分，也不提"白方"二字**（任务门槛那项刻意回避）。
- `HelpText` 逐级列出**实际数值**而不是等级（"任务升了 2 级"没有信息量）。
- 播报走**双通道**：`SecretChat`（弹泡 + 密聊记录）+ `DeviceChat`（公共发信机记录）。
  **不能用 `NormalChat`** —— 见 4.6。
- 每次升级的播报由 `AnnounceUpgrade` 开关控制；击杀本身**不播报**（死亡信息已由 `Broadcast` 段负责，
  再播一次就是刷屏）。

### 4.6 播报通道 —— `Features/Broadcast/BroadcastFeature.cs`

**三个通道的差异与显示条件**（这是本节最需要记住的）

| 通道 | 客户端落到哪里 | 显示条件 | 本项目的用法 |
|---|---|---|---|
| `SecretChat` | 密聊弹泡 + 密聊记录 | 弹泡要求 `State == Survive` **且 `IsAlive`**；死亡玩家会被跳过 | 死亡通告（`NoticeSecret` `:217`）、升级告示的第二条 |
| `NormalChat` | 聊天栏 | **只在 大厅 / 裁判 阶段被渲染**；生存阶段等于白发 | 进房介绍、开局提示（`:261` `NoticeToWrapped`） |
| `DeviceChat` | 公共发信机聊天记录 | 进 NormalLog 也要求 `IsAlive` | 升级告示的第一条 |
| （世界坐标音效/特效） | 按 `Pos` 广播给附近的人 | — | `/tp` 落点提示（`BroadcastWorldSFX/VFX`，半径 1792） |

**为什么进房介绍必须走 `NormalChat`**

进房介绍是在**大厅**发的。而 `SecretChat` 的弹泡有 `State != Survive → skip` 的门控，
在大厅走 `SecretChat` 的弹泡会被直接丢弃，新人什么都看不到（这正是一次实际返工的原因：
提交 `477055e` "进房介绍改走聊天栏（原来走 SecretChat，在大厅根本看不到）"）。
反过来，生存阶段的死亡/升级信息又不能只靠 `NormalChat`（渲染条件不满足），
所以死亡通告走 `SecretChat`、升级告示同时走 `SecretChat` + `DeviceChat`。

> **注意**：源码里 `Notice()`（`:119`）与 `NoticeTo()`（`:224`）的注释仍写着
> "`NormalChat` → 进聊天栏，长期可滚动回看"、"`NormalChat` 则进聊天栏，任何阶段都显示"，
> 与"生存阶段不渲染"的结论**相反**。以提交 `690e68e`（"NormalChat 在生存阶段是白发"）为准。
> 也就是说 `Notice()` 在生存阶段发的那条 `NormalChat`（拿刀通告）很可能是无效的一半。见附录 A。

**为什么要按宽度折行（`WrapLine` `:236` / `WrapByWidth`）**

聊天栏放不下时是**直接截断**，不是折行。所以长文本必须自己先按显示宽度切开，
再按"每 3 行一条消息"分段发送。宽度算法很朴素：`c > 0x7F` 记 2 个半角单位，否则记 1。
两套实现各有一份常量（`BroadcastFeature.MaxLineWidth` 默认 28 可配；命令回执用常量 40 = 20 个汉字）。

**10000ms 延迟与旁观补发的由来**

- 进房介绍延迟 `WelcomeDelayMs`（默认 **10000**）：客户端还没把场景加载完就发送会**丢消息**，
  实测 2500ms 不够（配置迁移 v3→v4 就是把这个旧默认值推上来的）。
- 回大厅后补发：对局进行中以**旁观身份**加入的人，此刻没有可靠的显示链路
  （弹泡要求 `Survive`、聊天栏在旁观界面也不保证可见），所以记入 `PendingWelcome`，
  在 `StartLobby` 的 Postfix 里延迟 1000ms 补发（`LobbyAnnounceHook` `:166`）。
- 规则调整播报（`AnnounceRule` `:143`）：大厅里改 → 立即播；对局中改 → 攒进 `PendingRuleChanges`，
  回大厅统一播。理由：对局中播等于当场告诉所有人"房主正在动规则"，本身就是额外信息。

**其它已知限制**（客户端行为，属预期）

- 死亡玩家收不到弹泡，所以死亡通告不必在服务端区分收件人。
- 播报会写进 `SecretLog`，主机迁移时被重放。
- 打开对讲机界面时，客户端会用 `DeviceId` 反查名字 → 本项目用魔数 `999999`，该处显示为"未知"。
- `Time` 必须填当前 `SurviveTime`，否则客户端按过期丢弃。
- 100 字截断只发生在原版 `Handle_C_CHAT_MESSAGE` 内部，房主直接构造包不经那个入口，因此不受限。

### 4.7 配置迁移 —— `Core/ConfigMigration.cs`

**为什么需要它**：BepInEx 只在**新建** `.cfg` 时写入默认值。之后无论代码里的默认值怎么改，
老配置文件都保持原样。这已经造成过两次"功能看起来完全无效"的误判 ——
最近一次是 `[WhiteRadar] SkipDummies` 停在旧默认值 `true`，导致假人全被跳过、雷达没有任何可显示目标。

**做法**：一个 `ConfigVersion` 计数（当前 `CurrentVersion = 6`），每次只针对
**"旧默认值必然导致功能静默失效"**的键做定向修正，白名单式，绝不碰其它用户设置，每项都留日志。

**三个 `Migrate*` 的语义（最容易理解错的一点）**

```text
MigrateInt/Float/Bool(section, key, oldDefault, newDefault)
    if (!config.ContainsKey(key)) return;        // 键不存在 → 什么都不做（交给正常绑定写新默认值）
    entry = config.Bind(section, key, newDefault)
    if (entry.Value != oldDefault) return;       // ← 关键：只在"当前值仍等于旧默认值"时才推进
    entry.Value = newDefault;
```

也就是说：**只在当前值恰好等于旧默认值时才改写，用户显式设置过的值一律保留**。
迁移的职责是"旧默认值已过时"，不是"替用户做决定"。
（历史上这里曾经是"只补缺失键"的错误语义，提交 `26198a3` / `611b622` 就是为了改掉它。）

**`EnsureInt` 的区别**

`EnsureInt` 用于**新增键**场景：键已存在就返回，不存在才 `Bind` 一次。
典型用例是 `RepairThreshold` 改名为 `RepairCount`——旧键无法自动搬运，只能确保新键存在且为期望默认值。

**如何新增一次迁移**

1. `CurrentVersion++`（`:23`）。
2. 加一段 `if (from < N) { ... }`，写清"为什么这个旧值会导致失效"。
3. **注意顺序依赖**：v6 那段里必须先把 `ExitRange` 1100→900，再处理"已迁移到 900 的 `EnterRange`"
   （900→700）—— 顺序反了就会漏掉一批配置。
4. 只在"旧值必然导致功能失效"时才纳入白名单。

**版本历史**（代码里的注释就是迁移日志）

| 版本 | 修正内容 |
|---|---|
| v2 | `WhiteRadar.SkipDummies` true→false；补齐已改名的 `PowerRepair.RepairCount` |
| v3 | `TeleportCommand.ShowWarningArrow` true→false（旧默认会在目标身上留箭头图标，还会抢 Kaho 的监视槽位） |
| v4 | `Broadcast.WelcomeDelayMs` 2500→10000 |
| v5 | `AoiCulling.EnterRange` 750→900 |
| v6 | `AoiCulling.ExitRange` 1100→900、`EnterRange` 900→700；`KillUpgrade.VisionBonusPerLevel` 0.5→0.6 |

### 4.8 【**临时补丁**】操作发信机时被刀杀死 ⇒ 定向补一次「当前阶段」刷新 —— `Features/Rule/GhostPhaseRefreshFeature.cs`

> ⚠ **这是临时补丁。官方修好客户端之后应整段作为冗余代码删除。** 段名 `GhostPhaseRefresh`（默认开），
> 关掉即完全回到原版行为。移除条件见本节末。

**症状**（房主原话）：**生存阶段**里，某人**正在操作发信机**（`ChatDevice` / `UI_ChatDevicePopup`）时被刀杀死，
他会**整段 Survive 拿不回操作权**（不能动、也开不了平板），直到下一次换阶段（回大厅）才恢复。

**根因（原版客户端缺陷；证据见 `.tmps/幽灵卡死-独立复核.md` §1/§8）**：

- 被刀命中那一刻服务端发 `S_STOP_CONTROL`（`Player.OnDamaged` `:175952`），客户端只做一件事：
  `Managers.Game.CanControl = false`（`:42338-42341`）。
- 一局内在 Survive 里能把它置回 true 的路**只有一条**：死亡时那条约 6.0 秒「你已死亡」提示的
  **最后一个 DOTween 回调**（`UI_ClassPopup.<ShowDeadMessage>b__10_0` `:61050`）。
- 而 `UI_ClassPopup.OnDisable` / `OnDestroy` 会把那条序列 **`Kill(false)`**（`:61156-61166`）
  —— `complete=false` ⇒ 回调永不执行 ⇒ **解锁永久丢失**。
- 原版还有一条后路是"下一次换阶段"（`UIManager.<EndLoading>b__31_0` `:38652`）；但本玩法
  **禁用了报告尸体** ⇒ 侦探/审判阶段不再发生 ⇒ 那条后路被拉远到结算/回大厅，症状就成了"卡到本局结束"。

**本补丁做什么**：服务端**只对那一个人**补一次「当前阶段」刷新，让他重走原版每次换阶段都在走的那条路：

```csharp
p.Session.Send(new S_CHANGE_GAME_STATE { State = room.State });   // 单人定向，不广播
// 等他的 C_COMPLETE_PACKET（带超时）
p.Session.Send(new S_FADE_IN());                                  // 客户端 EndLoading 回调把 CanControl 置 true
```

顺序不能反（反了会被客户端 `UIManager.EndLoading` 开头的 `if (_loadingUI == null) return;` `:38646` 吃掉）。
**服务端真实状态不变**（这里不动 `room.State`，也不走 `ChangeGameState` —— 后者开头就是
`if (State == state) return;`，对"当前阶段"是空转）。写法与 `ReplayDirector` 里那套同源。

**触发判据（确定性，不猜客户端行为）**：

| 信号 | 位置 | 为什么确定性 |
|---|---|---|
| 死时 `PublicInfo.State == EPlayerState.Interact(5)` | `Player.OnDead` 的 **Prefix**（`:175970` 之前） | 客户端开界面会发 `C_MODIFY_PLAYER{ChangePlayerState=5}`，服务端 `Player.ModifyPlayer` 该分支**无条件写入**（`:176801`；守卫只挡 Hide/Sit） |
| 「在发信机上被打」 | `Server.Game.ChatDevice.OnUserDamaged`（`:162331`，private） | 它挂在 `player.OnDamagedEvent` 上（`:162315-316`），被调用的位置正是 `OnDamaged` 那发 400ms 回调里、**`OnDead` 的前一行**（`:175955` / `:175961`） |

⚠ **必须是 `OnDead` 的 Prefix**：`OnDead` 末尾的 `ExitPlayer()` 会把 `State` 改成 `Hide`（`:176098`），Postfix 读到的是错的。
⚠ `ChatDevice` 客户端/服务端**同名** ⇒ 代码里用别名 `GameChatDevice = Server.Game.ChatDevice`（本仓库同名类型坑之一）。

**两个当初的未知项怎么兜的**：

1. 「客户端收到**同状态**的 `S_CHANGE_GAME_STATE` 会不会自己忽略」——**不需要它不忽略**：客户端
   `Handle_S_CHANGE_GAME_STATE` 不判重，直接 `StartLoading(state)`；而 `GameManagerEX.State` 的 setter
   自带 `_state != value` 守卫（`:28716`）⇒「设成同一个 Survive」本身**无副作用**，整条路照走、回执照发、最后解锁。
2. 「服务端**没有等待者**时收到那发 `C_COMPLETE_PACKET` 有没有害」——**已核实无害**：
   `GameRoom.CompletePacket`（`:169808` → 基类 `:173031`）整段体就是 `if (_completeAction != null) { … }`，
   无等待者时**一个字都不做**。所以本补丁**不注册、也不碰**原版那套等待者集合，只挂一个纯观察 Postfix。

等回执仍**带超时**；**超时也补发 `S_FADE_IN`** —— 只 WARN 不补的话客户端会停在 `_loadingUI != null`
的加载态，**比不修更糟**（超时也可能只是客户端走了 `IsCatchingUp` 的同步解锁路径）。
补发前还会再查一次房间是否已被**真实**换阶段/回放接管 —— 那种情况下那次转换自己会发 `S_FADE_IN`，我们让给原版。

**实机上怎么确认它生效**（`VerboseLog` 默认开）：

```text
[HS] GhostPhaseRefresh： #N（名字）死时在操作发信机（State=Interact:True 近期被打:False） ⇒ 8000ms 后定向补一次当前阶段刷新。
[Warning:HideAndSeek] [HS] GhostPhaseRefresh（临时补丁）：#N（名字）死时在操作发信机，已定向补发 S_CHANGE_GAME_STATE{Survive}，等回执（超时 2500ms）后补 S_FADE_IN。
[Info   :HideAndSeek] [HS] GhostPhaseRefresh（临时补丁）：#N（名字）已收到客户端回执 ⇒ 已补发 S_FADE_IN（客户端 EndLoading 回调会把 CanControl 置 true）。
[Info   :HideAndSeek] [HS] GhostPhaseRefresh（临时补丁）：#N（名字）补刷新后 6000ms：坐标已变化（他拿回操作权并动了）。
```

最后那行是**唯一的直接证据**：服务端看不到 `CanControl`，但看得到坐标有没有变。若它显示"仍未变化"，
就要在客户端加 `set_CanControl` 探针（调查报告 §8.8 的 P1）再看一眼。

**移除条件**（满足任意一条即可整段删掉本文件）：

- 官方修掉客户端 `UI_ClassPopup.OnDisable` / `OnDestroy` 的 `Kill(false)`（不再取消那个回调，或把解锁做成幂等）；
- 或官方让"拿回操作权"不再只依赖那一条 DOTween 回调（例如服务端给一发能直接置 `CanControl` 的包）；
- 或本玩法的换阶段后路恢复（重新允许报告尸体 / 恢复侦探阶段）。

---

## 5. 控制台命令与游戏内命令

### 5.1 控制台命令（房主，经上游 DT WebConsole / DT CONSOLE）

命令本体在 `Console/HsCommandRouter.cs`；接入方式见 §1.3。返回统一是 JSON。

| 命令 | 用法 | 作用 | 实现 |
|---|---|---|---|
| `hs` | `hs` | 总览：模式、AOI（enter/exit/min）、刀冷却、击杀上限 | `Status()` `:74` |
| `hs_check` | `hs_check` | **自检**：各段的 `loaded` / `hits`（语义见 §6.4） | `Diagnostics.Report()` |
| `hs_mode` | `hs_mode <on\|off>` | 玩法总开关（会顺带触发规则播报） | `:92` |
| `hs_aoi` | `hs_aoi [on\|off] [enter=700] [exit=900] [min=3]` | 视野裁剪参数；无参 = 回显状态 | `:107` |
| `hs_cd` | `hs_cd <秒>` | 击杀后冷却秒数（< 1 抬到 1） | `:140` |
| `hs_killlimit` | `hs_killlimit <n\|unlimited>` | 击杀次数上限 | `:154` |
| `hs_dummy` | `hs_dummy list` / `chars` / `add [座位号] [角色]` / `del <座位号>` / `clear` | 假人靶子：生成 / 删除 / 列表 / 角色清单（角色可写 `luna` / `露娜` / `seol` …） | `:171` |
| `hs_flash` | `hs_flash [on\|off]` | 开局灯效闪烁开关（关闭后仍会进入"白亮黑灭"定态） | `:229` |
| `hs_roomname` | `hs_roomname [新名字]` | 改 Steam 房间列表里显示的名字；无参回读大厅实际值 | `:249` |
| `hs_tp` | `hs_tp <玩家ID> <x> <y>` / `<玩家ID> to <目标ID>` / `to <目标ID>` | 调试传送（含假人）；`to <目标ID>` 省略操作者 = 房主自己 | `:282` |
| `hs_grant` | `hs_grant [on\|off]` / `exclude <ID...>` / `exclude clear` | 发刀模式（下一局生效）；排除名单优先级最高，宁可发给假人也不发给被排除者 | `:358` |
| `hs_radar` | `hs_radar [on\|off]` | 白方全图雷达开关 | `:520` |
| `hs_debug` | `black <玩家ID>` / `exec <玩家ID> <命令文本>` / `list` / `credit <数量>` | 非常规测试操作收拢处。`exec` 走真实密聊命令路径（CD/条件/效果都会真实触发） | `:452` |
| `hs_upgrade` | `hs_upgrade [vision\|speed\|task]`（也接受 `视野`/`移速`/`任务`） | 黑学分：无参看状态，带方向则升级 | `:422` |

补充说明：

- **`hs xxx` 空格写法可用**：`Execute` 里有一个白名单把 `hs mode on` 归一成 `hs_mode on`（`:40-52`）。
  不加白名单会退化成"无参的 `hs`"，看起来成功其实什么都没做 —— 注释里特意写了这点。
- **`hs_debug` 不在命令列表里**（见 §1.3 陷阱 2），但能执行。
- `hs_debug credit` 的实现有缺陷：`args[1]` 先被当作**玩家 ID** 解析并要求该玩家存在，
  之后才被当作**数量**。详见附录 A 第 4 条。

### 5.2 游戏内命令 —— 黑方（密聊通道）

实现：`Features/Rule/BreakCommandFeature.cs`。玩家在**密聊**里发以 `/` 开头的文本。
选择密聊的理由：原版只有 Dark（黑幕）能拆电、客户端把 Black 的 Q 导流成挥刀，
而密聊是**唯一**能承载任意字符串、且服务端已内建"仅 Black/Dark"闸门的通道。
命令包会被吞掉（`return false`），**不会**转发给同阵营 —— 黑幕看不到黑方在用命令；回执只发给本人。

| 命令 | 旧长名（隐性别名） | 条件 | 冷却 | 效果 | 回执 |
|---|---|---|---|---|:--:|
| `/brk` | `break` | `fusebox`（地图上有可拆电箱） | 90s | 拆电箱；可带 1~2 个 ID，不足两个自动补随机目标（原版断电需两个同时被拆） | 成功**不回复**（断电本身有全图黑 + 音效 + 箭头） |
| `/lck` | `lock` | 无 | 60s | 锁住附近的门（半径 = 地图短边 × 0.3） | 已锁 N 扇 / 附近没有可锁的门 |
| `/tp` | — | 无 | 60s | 预警 3 秒后传送到指定或随机玩家处 | 传送失败原因 |
| `/ls` | `list` | 无 | 0 | 列出玩家 ID + 昵称 + 状态（一行并排两个，省聊天栏行数） | 列表 |
| `/cre` | `credit` | 无 | 0 | 无参看学分与三项等级；`v`/`s`/`t`/`help` 升级或看逐级数值 | 状态 / 升级结果 |
| `/help` | — | 无 | — | 黑方命令帮助（符号化精简、按宽度折行、每 3 行分段） | 帮助 |

**隐性别名在 `Normalize`（`:172`）**：`break→brk`、`lock→lck`、`list→ls`、`credit→cre`。
命令名缩短后旧用法仍然可用。

**内置兜底（`BuiltinCommand` `:239`）**：`brk` / `lck` / `cre` / `ls` / `tp` 这五个命令
**即使配置表里没有也照常工作** —— 因为 `.cfg` 只在新建时写默认值，老配置里不会有新命令，
让用户为了拿到 `/lock` 去手改文件是设计缺陷。

> ⚠️ **配置表默认值与内置命令不一致（会让配置项失效）**：`Commands` 的默认内容注册的是
> `break` / `lock` / `tp`，但请求名会先被 `Normalize` 归一成 `brk` / `lck`，
> 于是 `defs` 里找不到 `brk`/`lck` → 回落到 `BuiltinCommand`。
> 净效果：**配置表里那两条 `break`/`lock` 永远不生效**（实际生效的是内置的 `brk` cd=90、`lck` cd=60）。
> 想改这两个命令的冷却，要写 `brk`/`lck`（或在代码里改 `BuiltinCommand`）。

### 5.3 游戏内命令 —— 白方（公开聊天通道）

实现：`Features/Rule/WhiteCommandFeature.cs`。选择公开聊天（`NormalChat` / 设备聊天 `DeviceChat`）的理由：
原版密聊在服务端有硬性颜色闸门（丢弃非 Black/Dark），**白方根本发不出去**。
代价是命令会被全房看见，所以命令包必须吞掉、不当聊天广播。

| 命令 | 别名 | 冷却 | 消耗 | 效果 | 回执 |
|---|---|---|:--:|---|:--:|
| `/rad` | `/radar` | 75s（每人独立） | 每局 2 次（`RadarUsesPerPlayer`） | 全图扫描 15 秒（复用 `WhiteRadarFeature`，把 `DurationSeconds` 改成 15 后 `SetActive(true)`） | 只有本人 |
| `/sta` | `/stasis` | 90s | 5% 任务进度 | 冻结（时停）黑方 5 秒（`EBuffType.TheWorld`） | 只有本人 |
| `/rep` | `/repair` | — | 10% 任务进度 | 立即恢复供电（仅断电时可用；逐个 `Fusebox.ConnetCable()`） | 只有本人 |
| `/help` | — | — | — | 白方帮助（三行） | 只有本人 |

**行为约定（`【已确认】`，实现里都写明了）**

- **黑方（含黑幕）发这些命令 → 直接吞掉，不响应也不回执**（`:171-172`）。
- 每个白方**独立计数与独立冷却**（字典按 `PlayerId`）。
- **冷却提示每 10 秒最多回一次**（`CdNoticeInterval`），避免刷屏。
- `/rad` 与 `/sta`、`/rep` **不做全房公告**：只有执行者自己知道，
  避免向黑方暴露"白方动用了消耗手段"（类注释里第 ③ 条"开启时发不署名公告"与实现不符，见附录 A）。
- 未知命令静默吞掉，不公开发言（避免刷屏）。
- `DoRadar` 里 `LastUse[pid] = now;` 这行曾漏写，导致 CD 判据永远拿不到上次时间、**次次放行**
  —— 代码里保留了这行注释，改动时别删。

**回执通道（`Reply` `:499`）**：**按命令的来源通道回** ——
`deviceId > 0` 发 `DeviceChat`，否则发 `NormalChat`。
白方多半是在**发信机设备界面**上发命令，那种情况下回执若走 `NormalChat`，设备界面根本不显示，
表现就是"命令没反应"（这是白方命令"无反应"的真因，历史提交 `2f84150`）。

### 5.4 命令的执行线程与状态改动

- 两条命令通道都挂在 `HostPacketHandler.Handle_C_CHAT_MESSAGE` 的 Prefix 上，按 `msg.Type` 分流
  （黑方管 `SecretChat`，白方管 `NormalChat` / `DeviceChat`）—— 同一方法上的多补丁共存。
- 解析后**必须** `room.Push(delegate { ... })` 再执行：直接在网络线程改游戏状态会竞态。
- 吞包（`return false`）绕过了原版闸门，所以**自补前置条件**是必须的：
  颜色、`State == Survive`、`IsAlive`、电箱的 `MissionType == -1` / `StateList[0] == 0`。

---

## 6. 构建、部署与验证

### 6.1 为什么必须在 `HideAndSeek` 目录内构建

| 文件 | 内容 | 后果 |
|---|---|---|
| `D:\git\DT_Tools\global.json` | `"rollForward": "disable"`, `"version": "7.0.410"` | 本机只有 **6.0.420 / 8.0.303**，`rollForward: disable` 意味着**在仓库根执行 `dotnet build` 直接失败**（上游 `DT_Tools` 自身也构建不了） |
| `D:\git\DT_Tools\HideAndSeek\global.json` | `"rollForward": "latestMajor"` | 只约束本目录，用已安装的最新 SDK；**不影响上游** |

所以：`cd HideAndSeek` 再构建。这不是习惯问题，是硬约束。

### 6.2 构建

```powershell
cd D:\git\DT_Tools\HideAndSeek
dotnet build -c Release --nologo
```

- 游戏不在默认路径时：`dotnet build -c Release -p:GameDir="D:\SteamLibrary\steamapps\common\Deadly Trick"`
  （默认 `C:\Program Files (x86)\Steam\steamapps\common\Deadly Trick`）。
- 产物：`HideAndSeek\bin\Release\netstandard2.1\HideAndSeek.dll`。
- **刻意不用 NuGet**：本机受限令牌下访问 nuget.org 不可用，而游戏已装 BepInEx 5.4.23.5，
  直接引用 `BepInEx\core\` 的两个 DLL 即可（`csproj` 里有注释说明）。
- `【已确认】（本次实测）`：按上述命令构建通过，输出 `已成功生成。 0 个警告 0 个错误`，耗时约 22 秒。

### 6.3 部署

```powershell
pwsh -File deploy.ps1                            # 只复制 DLL + 检查 doorstop
pwsh -File redeploy.ps1                          # 补 BOM → 构建 → 关游戏 → 复制 → 校验哈希 → 启动
pwsh -File redeploy.ps1 -NoLaunch                # 只关+部署，不启动
```

- `deploy.ps1` 会顺带检查 `winhttp.dll` 是否被改名成 `.disable`（那样 BepInEx 不加载任何插件）。
- `redeploy.ps1` 的步骤 1 是**先全量补 BOM 再构建** —— 用 write/edit 类工具改过 `.cs` 之后，
  这是最省事的兜底。步骤 3 会比对源/目标文件哈希并打印 `哈希一致=True/False`。
- **游戏运行时 `HideAndSeek.dll` 被锁**，`Copy-Item` 会失败。两个脚本都设了
  `$ErrorActionPreference = 'Stop'`，所以失败会**抛错中断**而不是静默通过；
  `redeploy.ps1` 更会先请游戏退出（超时再强杀）再复制。
  > `AGENTS.md` §7 里"不检查返回值就会误报成功"描述的是更早的脚本版本或手工复制场景，当前脚本不会。

### 6.4 部署后必须做的事

**第一步：看日志的"失败 N"**

```text
[HS] HideAndSeek 0.1.0 加载完成（DT_Tools 已检测到）：启用 30，跳过 3，失败 0。捉迷藏模式当前 关闭（段 [HS_Mode].Enabled）。
```

**`失败` 必须为 0。** 补丁挂不上**不会让功能报错**，只会静默失效 —— 项目历史上曾因此让
"离开对局恢复光照"白改两轮。失败时日志会紧跟 `[HS] 功能加载失败: XxxFeature ([段]) — 异常类型: 消息`。

**第二步：看是否跳过了段**

跳过会逐行打印 `[HS] 已跳过功能: XxxFeature ([段].Enabled = false)`。
默认配置下跳过 3 个（`Dummy` / `ProximityAlert` / `SoloPlay`）。

**第三步：`hs_check` 怎么区分"补丁没挂上"和"挂上但没触发"**

`hs_check` = `Diagnostics.Report()`，输出形如：

```json
{"ok":true,"mode":false,"features":[{"section":"AoiCulling","loaded":true,"hits":12}, ...]}
```

**但要注意这套机制的三个真实语义（都是 `【已确认】` 的代码事实）**：

1. **`loaded: false` 在当前代码里几乎不可达。**
   `Diagnostics.Map` 的条目只由 `MarkLoaded`（挂载成功时）和 `Hit`（功能被触发时）创建。
   段被跳过 → 不 `MarkLoaded`（也不 `Hit`）→ **该段根本不出现在 `features` 列表里**；
   段挂载失败 → 同样不 `MarkLoaded`。
   所以正确的判据是：**"段是否出现在列表里"**，而不是 `loaded` 字段。
   要区分"被跳过"和"挂载失败"，得回看启动日志的 `已跳过功能` 与 `失败 N`。
2. **`hits: 0` 不等于"没触发过"。** 只有 21 个段真的调了 `Diagnostics.Hit`。
   下面这 **12 个段永远不会报告 hits**（恒为 0），别据此判定它们失效：
   `WhiteRadar`、`MiyukiScan`、`StartFlash`、`ProximityAlert`、`SpeedBoost`、
   `FuseboxReveal`、`LockDoor`、`PowerRepair`、`TeleportCommand`、`BreakCommand`、
   `WhiteCommand`、`RoomName`。
   这些段请改看各自的业务日志（例如 `WhiteRadar：已开启`、`MiyukiScan：美幸 #N 扫描`、
   `LockDoor：黑方 #N 锁住 M 扇门`、`RuleRewrite：键 → 值`）。
3. **`Hit` 的位置决定它统计的是什么。** 例如 `AoiCulling` 只在 `PrefixAddPlayer` 里打点
   （即"有人被介绍给黑方"），`Broadcast` 只在 `StartSurvive` 里打点。所以 `hits` 是
   "打点处被走到几次"，不是"功能生效几次"。

**第四步（怀疑中文乱码时）：校验编译产物里的中文**

见 §7 第 1 条 —— 注意用 **UTF-16** 解码，AGENTS.md 里写的 UTF-8 解法是不可靠的。

### 6.5 段级开关的排查含义（再强调一次）

`defaultEnabled: false` 会让 `PatchLoader` 对**整个类**跳过 `PatchAll` ——
该段里**所有**钩子都不会挂，包括与"自动行为"无关的**基础能力**。
而命令桥是独立通道、不受段开关影响，于是会出现最糟的组合：
**命令能生成对象，却没有任何钩子处理它**（`Dummy` / `DummyPick` 的拆分就是为了这个）。

**排查顺序**：先确认补丁挂载状态（`失败 N`、`已跳过功能`、`hs_check` 的段列表），再看代码逻辑。
"日志里没有输出"有两种截然不同的解释 —— 代码跑了但没打日志，还是代码根本没运行 ——
而 `已跳过功能: XxxFeature` 这行能直接排除其一。

---

## 7. 踩坑清单

> 前 9 条是 `AGENTS.md` 已有的坑（此处保留并补全细节），第 10 条起是本文档作者在通读代码时新发现的。

### 1. UTF-8 BOM 必须带（最容易踩，且不报错）

Roslyn 对"无 BOM + 非 ASCII"的 `.cs` 会退回**系统 ANSI（GBK）**读取，中文字面量在**编译期**就已损坏，
且**不产生任何警告**。表现是游戏里看到乱码。

实测：`假人` 的 UTF-8 字节 `E5 81 87 E4 BA BA` 按 GBK 解读 = `鍋囦汉`（首字变 `鍋`）。
> 注：`AGENTS.md` 里记的是"假人 → 鍋水漢"，与本次实测的 `鍋囦汉` 不完全一致（可能是别的字符串或旧记录），
> 但"首字变成 `鍋`"这个判据是成立的。

- **用 write/edit 类工具重写文件后必须复查 BOM** —— 覆写常常会丢掉它。
- 当前状态（`【已确认】`）：全部 33 个 `.cs` 都带 BOM；
  `AGENTS.md` / `TEXTS.md` 带 BOM，而 `README.md` / `PLAN.md` **不带**（与"所有文本文件都带 BOM"的说法不一致）。
- 补齐脚本（`redeploy.ps1` 步骤 1 已内置同一段逻辑）：

```powershell
$utf8Bom = New-Object System.Text.UTF8Encoding($true)
Get-ChildItem -Recurse -Filter *.cs |
  Where-Object { $_.FullName -notmatch '\\obj\\' } |
  ForEach-Object {
    $b = [System.IO.File]::ReadAllBytes($_.FullName)
    if (-not ($b.Length -ge 3 -and $b[0] -eq 0xEF -and $b[1] -eq 0xBB -and $b[2] -eq 0xBF)) {
        [System.IO.File]::WriteAllText($_.FullName, [System.Text.Encoding]::UTF8.GetString($b), $utf8Bom)
    }
  }
```

**校验编译产物里的中文 —— 请用 UTF-16，不要用 UTF-8**

`AGENTS.md` 给的写法是把 DLL 按 UTF-8 解码再匹配。实测这是**部分失灵**的：

```powershell
# AGENTS.md 的写法（不推荐）
$u8 = [System.Text.Encoding]::UTF8.GetString([System.IO.File]::ReadAllBytes($dll))
$u8 -match '假人'    # True  ← 但这是假阳性来源
$u8 -match '冻结'    # False ← 源码里明明有"冻结冷却中。"（假阴性！）
```

原因：`.NET` 程序集里，**代码里的字符串字面量以 UTF-16 存在 `#US` 堆**，
而**自定义特性（`[PatchFeature]`/`[ConfigField]`）的字符串参数以 UTF-8 存在 blob 堆**。
按 UTF-8 解码只能看到后者，所以：

- `假人`（出现在 `[ConfigField("假人", ...)]` 里）→ 匹配成功，**即使代码字面量全烂了也会成功**；
- `冻结`（只出现在 `WhiteCommandFeature` 的代码字面量里）→ 匹配失败，**即使它是好的**。

正确写法（本次实测全部命中）：

```powershell
$u16 = [System.Text.Encoding]::Unicode.GetString([System.IO.File]::ReadAllBytes($dll))
$u16 -match '冻结'     # True
$u16 -match '扫描冷却中' # True
$u16 -match '鍋'       # False  ← 乱码判据依然有效
```

### 2. 同名类型会被外层命名空间抢走

`Assembly-CSharp` 里存在重名类型：`Corpse` / `Player` / `Item` / `DeviceManager` / `Summon`
（以及本项目踩到的 `Door` / `Fusebox` / `GameRoom` / `SkillComponent`）。
C# 的查找规则里**外层（全局）命名空间优先于 `using` 引入的命名空间**，
于是 `typeof(Corpse)` 会**静默解析到全局那个**，编译通过但 Harmony 运行期找不到目标。

→ 一律用别名显式限定。本项目在用的别名（新增代码照抄）：

```csharp
using GamePlayer = Server.Game.Player;
using GameCorpse = Server.Game.Corpse;
using GameDeviceManager = Server.Game.DeviceManager;
using GameDoor = Server.Game.Door;
using GameFusebox = Server.Game.Fusebox;
using GameSkill = Server.Game.SkillComponent;
using GameRoom = Server.Game.GameRoom;
```

另外：服务端 `Device` 读状态用 **`DeviceInfo`**，客户端的 `DeviceBase.Info` 是另一套。

### 3. `System.*` 与 `Console.*` 也会被自己的命名空间截胡

- 本程序集存在 `HideAndSeek.Features.System`，所以在 `Features.*` 下写 `System.StringSplitOptions`
  会解析到它 → 用 `global::System.XXX`。项目里用了 **127 处** `global::System.`，这不是风格洁癖。
- 同理存在 `HideAndSeek.Console`：在 `namespace HideAndSeek` 下写 `Console.Xxx` 会解析到本程序集。
  `Plugin.cs:52` 因此写成完整限定名 `HideAndSeek.Console.ConsoleBridge.RefreshCommandList()`。

### 4. Harmony 重载歧义 / 内部类型定位

- 目标方法有重载时，`nameof` 不带参数类型会抛 `Ambiguous match`，**整个补丁挂不上**。
  正确写法（例）：

```csharp
[HarmonyPatch(typeof(GameRoom), nameof(GameRoom.ChangeGameState), new[] { typeof(EGameState) })]
[HarmonyPatch(typeof(GamePlayer), nameof(GamePlayer.Move), new[] { typeof(PosInfo), typeof(bool) })]
```

- 私有方法用字符串定位（`[HarmonyPatch(typeof(X), "PrivateMethod")]`），例：`SurvivalTick`、`StartPick`、
  `PickCharacterTick`、`StartWeaponCooltime`、`OnDeadMurder`、`DisconnetCable`（注意原版拼写少一个 c）。
- **但内部类型（`internal`）用字符串类型名会解析到 `0Harmony` 上**（如 `MissionManager`），
  必须改用 `[HarmonyTargetMethod]` + `AccessTools.TypeByName`。已在用的：`Server.Game.MissionManager`、
  `UI_TotalResult`、`DT_Tools.Console.WebConsole`。
- `[HarmonyPrepare]` 是"上游不存在就整类跳过"的标准姿势（`ConsoleBridge` / `DummyFeature.OfficialReportHook`）。

### 5. `AddPlayer` 是双向的，剔除必须自己补回来

`X.AddPlayer(Y)` 的语义是"把 X 介绍给 Y"。原版让某人**重新可见**的唯一途径是**被观察者自己移动**。
站着不动的目标一旦被 `RemovePlayer`，客户端上那个 `Player` 对象已 despawn，走到跟前也看不见。
→ AOI 必须"黑方移动时立即校正 + 每秒 tick 兜底"，两处都主动 `AddPlayer`。
→ 同时必须跳过 `State == EPlayerState.Hide`（**躲柜子的活人** `Cabinet.HideCabinet :162129`
　＋ **死亡幽灵** `MakeSpectatorGhost :175590`），否则会把死人的幽灵、以及柜子里的活人一起塞给黑方。

### 6. 假黑灯是客户端的本地状态

`Managers.Game.Darkness` 只由 `S_AREA_PUBLIC` 驱动。发这个包时 **`RoomId` 必须合法** ——
客户端 `ChangeArea` 会执行 `RoomDic[RoomId]`，`RoomId = 0` 直接抛 `KeyNotFoundException`。
而且**不需要写"解除黑灯"逻辑**：原版回大厅时会自行 `Darkness = false`；
多写的解除逻辑反而会在切区域（`Area.SendAreaInfo`）时把灯重新涂黑。
→ 判据只需一条：**房间处于 `Survive` 才维持黑灯**。

### 7. 上游 DT_Tools 没有"插件注册表"

它的 `[PatchFeature]` 与 `IConsoleCommand` 都只扫**自己**的程序集（`WebConsole.RegisterDiscoveredCommands`
里是 `GetType().Assembly`），外部无法注入。集成只有 §1.2 / §1.3 那两条路，各自的时序陷阱都写在那边。

### 8. 段开关会连带跳过该段的所有补丁

见 §6.5。**新增功能时先想清楚：这个段被关掉后，还会不会有人需要它的能力？**
需要就拆成两个段（基础能力段默认启用 + 自动行为段默认关闭）。

### 9. 部署相关

- 游戏运行时 `HideAndSeek.dll` 被锁 → 先退游戏（`redeploy.ps1` 已处理）。
- 上游 DT_Tools 需要 `MonoMod.Backports.dll` 与 `MonoMod.ILHelpers.dll` 在 `BepInEx\core`，
  缺了它上游 `Awake` 静默失败、WebConsole 根本不启动（19450 无监听）。
  本模块的玩法功能不依赖 WebConsole，所以症状是"玩法正常、`hs_*` 命令全不可用"。

### 10. 配置默认值改了，老 `.cfg` 不会跟着改

见 §4.7。**任何"改默认值"的提交都必须考虑是否要加一次 `ConfigMigration`**，
判据是"旧值是否必然导致功能静默失效"。纯手感调整不必迁移。

### 11. `NormalChat` 在生存阶段是白发

客户端的聊天栏只在 大厅 / 裁判 阶段渲染 `NormalChat`。
→ **生存阶段要给玩家看的信息，只有两条路**：`SecretChat`（弹泡 + 密聊记录，要求 `Survive` + `IsAlive`）
或 `DeviceChat`（公共发信机记录，进 NormalLog 也要求 `IsAlive`）。
→ 进房介绍这类"在大厅发"的信息反过来必须用 `NormalChat`（`SecretChat` 的弹泡在大厅会被丢弃）。

### 12. 聊天回执必须自己折行 + 分段

聊天栏放不下是**直接截断**；一次最多显示 **3 行**。所以：
先按显示宽度（`c > 0x7F` 记 2）折行，再每 3 行发一条。
黑方回执（`BreakCommandFeature.Reply`）与白方回执（`WhiteCommandFeature.Reply`）各有一套实现，
宽度上限都是常量 `40`（= 20 个汉字），行数上限 `3`。
不这么做就会出现"帮助显示不全"（历史提交 `a2d73d1` / `0ce1b63` 都是修这个）。

### 13. 跨局状态必须在生命周期钩子里自己清

`PushAfter` 不随阶段清空、字典也不随场景销毁。**任何静态字段都要问一句"下一局它还成立吗"**。
本项目已挂清理的位置：

| 类 | 清了什么 | 挂在哪 |
|---|---|---|
| `AoiCullingFeature` | `VisibleSince`（不清会让新人永远不被剔除） | `StartSurvive` |
| `BreakCommandFeature` | `Uses` / `LastUse` | `StartSurvive` |
| `WhiteCommandFeature` | `Uses` / `LastUse` | `StartSurvive` + `StartLobby` |
| `KillUpgradeFeature` | 学分 / 等级 / 被改写的配置还原 | `StartLobby` + `StartDetective` |
| `RuleRewriteFeature` | 被改写的配置还原 / `Fired` / 击杀计数 | `StartLobby`（计数在 `StartSurvive`） |
| `LockDoorFeature` | `LockedDoors` | `StartSurvive` + `StartDetective` |
| `PowerRepairFeature` | `_peak` | `StartSurvive` + `StartLobby` |
| `WhiteRadarFeature` | pin 清理 / 假颜色还原 / `Faked` | `StartLobby` + `StartDetective` |
| `MiyukiScanFeature` | 5 个计时字典（**漏了 `PinSnapshot`**，见附录 A 第 6 条） | `StartLobby` + `StartDetective` |
| `ProximityAlertFeature` | `LastAlertAt` | `StartLobby` + `StartDetective` |

### 14. 用"改配置项"实现效果时，必须记基础值 + 生命周期末还原

否则会**跨局叠加**（每局都在上一局的数值上再乘一次）。范式见 `KillUpgradeFeature.ApplyUpgrades`
（`_baseEnter` 等）与 `RuleRewriteFeature.Remember` / `Restore`。

### 15. 反射句柄失败要熔断

`AccessTools.FieldRefAccess` / `AccessTools.Method` 在每次 tick 里失败会刷屏并拖慢游戏。
项目里的熔断标志：`AoiCullingFeature._summonIdFailed`、`WhiteWinFeature._reflectFailed`、
`BreakCommandFeature._disconnectMethod == null` 检查。新写反射时照抄这个模式。

### 16. `PushAfter` 回调不随阶段清空 → 回调内必须自校验

`TeleportCommandFeature` 的落地回调先判 `room.State != Survive || !caster.IsAlive` 就直接放弃。
`StartFlashFeature` 的闪烁序列同理（若阶段已变，闪灯只是无害但难看）。写延迟回调时**必须**假设
"回调跑到时阶段已经变了"。

### 17. protobuf 消息在多收件人之间会共享引用

给多个收件人发同一个消息对象时，若其中一处改了内容，会影响所有人。
→ 逐人 `Clone()`：`LockDoorFeature.SendFakeStateToBlack` 里给每个黑方一份
`door.DeviceInfo.Clone()` 再改 `StateList[0]`（注释明确写了"直接改会污染白方与世界状态"）；
`FuseboxRevealFeature` 对 `Pos` 也 `Clone()`。

### 18. 状态改动必须走 `GameRoom.Push`

`Handle_C_CHAT_MESSAGE` 的 Prefix 运行在网络线程，直接改玩家/房间状态会与主线程竞态。
两条命令通道都是 `room.Push(delegate { Handle(...); })`。

### 19. `const` 常量编译期内联，运行时改不到

`Define.BLACK_REARM_DELAY_SECOND` 之类的 `const` 会被内联进调用点 IL，
Harmony 改不到字段值。→ 只能改**调用点**（`WeaponCooldownFeature` 就是这么做的：
拦 `StartWeaponCooltime` 且只在 `seconds == 20` 时替换，保留拔刀后 5 秒锁）。

### 20. 运行期改配置默认只改内存

`Plugin.Start` 里 `config.Save()` 只落盘**一次**（首次生成完整 `.cfg`）。
之后无论是 `hs_*` 命令还是 DT CONFIG 页面改值，都只改内存；
显式"保存到 .cfg / 覆盖配置"才写盘。
→ 所以"用命令调好参数 → 重启 → 参数回到旧值"是**预期行为**，不是 bug。

### 21. `[ConfigField]` 的字段名不要叫 `Enabled`

段级 `Enabled` 键由 `PatchLoader` 生成。若功能类里再声明一个
`[ConfigField(...)] public static ConfigEntry<bool> Enabled;`，`ConfigBinder` 会 Bind **同一个键**，
BepInEx 返回**同一个 `ConfigEntry` 实例** → 两个名字指向同一个开关，字段上的默认值**被忽略**
（先 Bind 的段级默认值胜出）。

- 现状：`WhiteRadarFeature` / `WhiteWinFeature` 的注释都明确警告了这一点（把字段改名为 `RadarOn` / `OnTimeout`），
  但 `BroadcastFeature` 与 `ProximityAlertFeature` **仍然叫 `Enabled`**（两处默认值与段级一致，所以今天无副作用）。
- `【已确认】`（BepInEx 的 `ConfigFile.Bind` 同段同键返回同一实例）+ `【推测】`（默认值冲突时"先绑定的赢"）。
- 新写功能请用有意义的名字（`XxxOn` / `AllowXxx`）。

### 22. 全角字符宽度假设

所有折行实现都用 `c > 0x7F ? 2 : 1`。这对中文/全角标点基本准确，
但对 emoji、组合字符、代理对（surrogate pair）会算错（一个代理对会被算成 4）。
目前文本里没有这类字符，**要加就自己先量一下**。

### 23. `hs_debug credit` 的实现缺陷

`hs_debug credit <数量>` 会先用 `args[1]` 解析**玩家 ID** 并要求该玩家存在，
之后才把它当**数量**用。因此只有在"存在 ID 等于该数量的玩家"时才会加学分。见附录 A 第 4 条。

### 24. `BreakCommand` 配置表默认值与 `Normalize` 不匹配

见 §5.2 的警告框：配置里默认注册的 `break` / `lock` 永远命不中，实际生效的是内置命令。

### 25. 文档比代码老

`PLAN.md` 严重过时（仍在描述 `Patches/` 目录、`HS_Text` / `HS_AOI` / `HS_Combat` 段、
用 Transpiler 改 `Corpse` 构造函数、`/hs` 带斜杠的命令名等，均与现状不符）。
`TEXTS.md` 的部分行号有 ±1~12 行偏移（例如它标 `KillUpgradeFeature.cs L134`，实际是 `:146`；
标 `WhiteCommandFeature.cs L426`，实际是 `:425`）。
**以代码为准，行号只当"附近"用。**

---

## 8. 当前状态与待办

> **重要前提**：本文档作者**只读了代码与提交记录**，没有运行游戏。
> 因此下面区分"代码里已经这么写了"（`【已确认】`——即我读到了实现）与"是否在实机上真的生效"（**未知**）。
> 玩家侧的实际手感、客户端渲染、多人联机时序，一律需要实机验证。

### 8.1 仓库状态

| 项 | 值 |
|---|---|
| 分支 / HEAD | `main` / `381c2c1`（"change: ProximityAlert 默认关闭并静音"） |
| 提交总数 | 120 |
| 工作区 | 干净（`git status` 无输出） |
| 构建 | `【已确认】`本次重建通过：0 警告 0 错误 |
| 段总数 | 33（全部 `Host` 侧），默认启用 30，默认关闭 3 |

### 8.2 近 30 个提交在做什么（归纳）

近 30 个提交**全部集中在同一天**，主题高度集中在四件事（按时间从新到旧）：

**① 黑学分（`KillUpgrade`）从新增到修平衡 —— 改动最密集**

- `b3e8df5` 新增黑学分系统；`33247cc` 加 `/cre` 与白方 `/sta` `/rep`
- `d490096` **修真正的超发 bug**（分母问题）+ 美幸白点提前消失
- `edfe0d6` 分母定为"可击杀人数"（白方总数 − 1）
- `9d4d40d` 视野升级倍率 0.5 → 0.6（配合 AOI 内圈缩小）
- `86c0b12` `/cre help` 显示逐级实际数值；`765a809` 命令名缩短；`4d9ba04` `/cre` 无参打印完整状态
- `30203ce` / `690e68e` **升级播报通道**从 `NormalChat` 改到 `DeviceChat`（+ `SecretChat`）

**② 播报通道与文本 —— 反复返工最多的一块**

- `477055e` 进房介绍从 `SecretChat` 改到聊天栏（原来在大厅根本看不到）
- `690e68e` 发现 `NormalChat` 在生存阶段是白发 → 升级播报改走 `DeviceChat`
- `1e573fa` 进房介绍延迟改 10 秒 + 对局中加入者回大厅补发
- `6e309b8` 进房介绍/开局提示按行宽自动折行（避免聊天栏截断）
- `30203ce` 死亡通告只进密聊通道
- `a5d34d4` / `633d787` / `93010f1` / `5475ca6` 文案批量调整 + `TEXTS.md` 反复重做

**③ 白方命令与传送（`/tp` `/rad` `/sta` `/rep`）—— 一排 fix 链**

- `2f84150` 白方命令"没有任何反应"的真因：回执走错了通道
- `8995176` `/radar` 无 CD + `/stasis` 无效果 + 箭头残留（配置迁移 v3）
- `dd6ba15` 扣进度基数算错（"没扣"的真因）
- `2733bc2` → `5724a0f` 时停两次返工，最终改为复刻原版 `UseTimeStop`（`TheWorld`）
- `c5e1739` 删掉 `/tp` 的箭头逻辑 + 黑洞状态加过期窗口
- `82da175` 落点提示广播半径 99999 → 1792
- `bdeaffe` 进度广播换包 + 落点特效纠旧值 + `Suppress` 屏蔽自我改写 + 速度判断收紧
- `d95e69e` 落点提示全走世界坐标 + 特效/音效可配

**④ 配置迁移机制本身**

- `26198a3` 迁移语义改为"只在仍是旧默认值时才推进"（**绝不覆盖用户显式设置**）
- `611b622` 修"只补缺失"的语义错误
- 目前 `CurrentVersion = 6`

### 8.3 可以认为"已经修好"的部分（有代码依据）

- **配置迁移**：语义已明确，v2~v6 的白名单与理由都写在注释里。
- **黑学分分母**：从"当前存活数"改成"开局白方总数 − 1"，并留下了 159 vs 100 的实测证据。
- **聊天回执折行/分段**：黑方与白方两套实现都按"先折宽度、再每 3 行"处理。
- **命令名归一化**：`break/lock/list/credit` 旧长名保留为隐性别名。
- **时停**：改为复刻原版 `AddBuff(TheWorld)`，不再改速度、不再拦 `Player.Move`。
- **`/rad` 的 CD**：漏写 `LastUse[pid] = now` 的问题已修（代码里保留了警示注释）。
- **升级播报通道**：不再使用生存阶段无效的 `NormalChat`。

### 8.4 尚未实机验证（诚实标注）

`PLAN.md` §"待你实机验证"里列的 5 项**至今没有勾选记录**，我也没有条件验证：

1. `KillLimit` 是否真被游戏调用 → 看 `[HS] KillLimit：BlackKillLimit → N` 日志。
2. 离开对局后黑灯是否恢复（`PLAN.md` 里提到的 `[HS] BlackVision：已恢复真实光照（切至 ...）`
   这条日志**在当前代码里找不到** —— `BlackVisionFeature` 现在只在生存阶段下发黑灯、
   不写任何"恢复"逻辑，说明那次改动后来被移除了）。**这一项的可验证手段需要更新。**
3. 假人：`hs_dummy add` 后客户端是否出现名牌/身体、能否被刀死、回大厅是否自动重建。
4. 开局灯效（现在的实现是"坏灯式随机闪烁 12 次"，已不是 `PLAN.md` 写的"亮灭两次"）节奏是否合适。
5. 黑洞：黑方使用后落点是否为出生点、目标身上是否还有黑洞特效。

此外，以下几处**我认为需要实机确认**（`【推测】`，代码路径上可疑）：

- `WhiteRadarFeature.SendPin` 的 `IsForce` 取值（注释自相矛盾，见附录 A 第 1 条）。
- `TeleportCommandFeature.ShowTeleportVfx = true` 时是否真能看到特效
  （同一文件里明确说 `TeleportVfx` 没有世界坐标渲染分支，会被自动纠正为 `BlackHoleVfx`）。
- `/tp` 的落点文字提示走 `NormalChat` 单人发送，在**生存阶段**是否真的显示（与"生存阶段不渲染 NormalChat"冲突）。
- `ProximityAlert` 的 `Enabled` 子项与段级 `Enabled` 是同一个键，实际只有段级开关在起作用。
- `MiyukiScan` 的 `PinSnapshot` 跨局残留在 `PlayerId` 复用时会不会导致 pin 显示错位置。

### 8.5 已知遗留问题（代码层，均为 `【已确认】` 可读到的）

1. **`hs_debug credit` 参数语义错**（既当玩家 ID 又当数量）→ 调试时几乎不可用。
2. **`hs_debug` 不在命令列表/补全里**。
3. **`BreakCommand.Commands` 默认注册的 `break`/`lock` 永远命不中**（被 `Normalize` + 内置命令覆盖）。
4. **`WhiteCommandFeature.AnnounceOnUse`、`_savedSpeed`、`IsStasisActive`、
   `KillTimeBonusFeature.Announce` / `AnnounceText` 是死配置/死代码**（声明了但永不生效或被使用）。
5. **AOI 相关的兜底常量是旧值**（`AoiCullingFeature.Resolve` 750/1100、`MiyukiScanFeature.ReapplyCull` 750、
   `hs_aoi` 的 `AnnounceRule` 750/1100、`hs` 总览的 600/900）—— 只在配置项为 null 时命中，
   但会误导阅读者以为默认值是这些。
6. **`MiyukiScanFeature.Reset()` 漏清 `PinSnapshot`。**
7. **`TEXTS.md` 行号过期、`PLAN.md` 整体过期**。
8. **`README.md` 的"配置段"表已过期**：它列的是 `[HS_Mode]` 加各段名的**旧命名**（现在段名是
   `AoiCulling` / `WeaponCooldown` 等，没有 `HS_` 前缀），默认值也旧（写 `EnterRange 600 / ExitRange 900`，
   实际 700/900）；"命令"表只列了 6 条（实际 14 条）。

### 8.6 建议的接手顺序

1. 先跑 §6 的构建 + 部署，确认日志 `失败 0`，在 DT CONSOLE 执行 `hs_check` 看段列表。
2. 打开一局单人测试（`SoloPlay` 段打开），`hs_dummy add` 造靶子，把 §8.4 的 5 项逐条验证并记录。
3. 需要改哪个功能就按 §3 的表找到段名与文件；**先读类级注释**（本项目的类注释信息量很大，
   往往包含"为什么不能那么做"）。
4. 改完代码记得：BOM（§7.1）→ 构建 → 日志 `失败 0` → `hs_check`。

---

## 附录 A：已知的代码 ↔ 注释/文档不一致

> 这些不影响"能不能编译"，但会误导接手者。按"危害程度"排序。

| # | 位置 | 现象 | 影响 |
|:--:|---|---|---|
| 1 | `Features/Vision/WhiteRadarFeature.cs` `SendPin`（`:174-199`） | **注释自相矛盾**：先写"`IsForce = true` 是关键：…`SetLocalPosition`（立即定位）…否则…补间永远不会执行"，紧接着另一段又写"`IsForce` 必须为 false，与原版 `SendTraceTarget` 一致：false → `SetTargetPosition`；true → `SetLocalPosition`…传 true 会让位置算错、pin 反而全部不可见"。**代码实际传 `true`。** | 高。改这块之前必须先实测确认坐标系，不能只信注释。两段注释明显是一次返工后只改了一半 |
| 2 | `Features/Broadcast/BroadcastFeature.cs`（`:126-127`、`:202-212`） | 注释仍写"`NormalChat` → 进聊天栏，长期可滚动回看"、"`NormalChat` 则进聊天栏，**任何阶段都显示**"，与同一仓库后续结论（`KillUpgradeFeature.cs:306`"不能用 `NormalChat` —— 客户端只在 大厅/裁判 渲染它，生存阶段等于白发"）**相反** | 高。会让人继续用 `NormalChat` 播报生存阶段信息；`Notice()` 里那条生存阶段发的 `NormalChat`（拿刀通告）很可能是无效的一半 |
| 3 | `Features/Rule/BreakCommandFeature.cs` `Commands` 默认值（`:47-49`）vs `Normalize`（`:172`）/ `BuiltinCommand`（`:239`） | 配置默认注册 `break`/`lock`/`tp`，但请求名先归一成 `brk`/`lck`，导致配置里的 `break`/`lock` 条目**永远不被命中**，实际执行内置命令（`brk` cd=90、`lck` cd=60，与配置写的一致或不同） | 高。改这两个命令的冷却写了配置却不生效 |
| 4 | `Console/HsCommandRouter.cs` `Debug()`（`:452-516`） | `hs_debug credit <数量>` 的用法注释是"直接增减黑学分"，但实现先用 `args[1]` 解析**玩家 ID 并要求该玩家存在**，之后才把它当数量。 | 高。功能实际不可用（除非恰有同 ID 的玩家） |
| 5 | `Features/Rule/WhiteCommandFeature.cs` 类注释第 ③ 条（`:28`） | 写"开启时在公开聊天发一条**不署名**的公告"，而实现是**不公告**（`:261` 注释"不公告：/rad 的开启不对外播报"） | 中 |
| 6 | `Features/Vision/MiyukiScanFeature.cs` `Reset()`（`:368-375`） | 清了 `Unlocking` / `NextScanAt` / `UnlockUntil` / `MarkerUntil` / `LiveUntil`，**没清 `PinSnapshot`**（`:254`），也没有清 `LiveUntil` 之外的 `_skillResolved` 缓存 | 中。`PlayerId` 跨局复用时可能读到上一局的快照位置 |
| 7 | `Features/Combat/KillUpgradeFeature.cs` 类注释（`:16-29`） | 仍写"每股学分 = 池子总量 ÷ **白方人数**"、"每级固定成本 ≈ 池子的 **17%**"，而代码是"÷（白方总数 − 1）"、默认 **18%** | 中。类注释与实现（及后面的行内注释）不同步 |
| 8 | `Features/Rule/WhiteCommandFeature.cs` `MoveFreezeHook` 删除处（`:475-476`）+ `_savedSpeed`（`:73`）/`IsStasisActive`（`:69`）/`StasisTickHook`（`:479`） | 拦截 `Player.Move` 的方案已删，但"还原黑方移速"的字段与分支留着，`_savedSpeed` **从未被赋值**；`IsStasisActive` **无任何调用者** | 中。读代码时会以为还有速度通道在起作用 |
| 9 | `Features/Combat/KillTimeBonusFeature.cs` `Announce` / `AnnounceText`（`:31-36`） | 两个配置项声明了默认值与说明，但实现里明确"击杀加时不再公开播报"，**永不读取** | 中。DT CONFIG 页面里会看到不起作用的开关 |
| 10 | `Features/Rule/WhiteCommandFeature.cs` `AnnounceOnUse`（`:53-54`） | 同上：声明"使用 /rad 时公开公告"，实现不公告 | 中 |
| 11 | `Features/Rule/TeleportCommandFeature.cs` `LandingVfxType` 说明（`:42-44`）与 `ShowTeleportVfx`（`:34`） | 选项说明把 `TeleportVfx` 列为可选值，但代码在读到该值时会**自动纠正为 `BlackHoleVfx`** 并警告（因为 `TeleportVfx` 在客户端没有世界坐标渲染分支）；而 `ShowTeleportVfx`（落地时给施法者放特效）用的**正是 `TeleportVfx`** | 中。`ShowTeleportVfx = true` 很可能看不到任何东西 |
| 12 | `Console/ConsoleBridge.cs` `HsCommands` 数组（`:39`、`:47`） | usage 里写 `enter=750` / `exit=1100`（旧默认值），实际默认 700/900；且数组**缺 `hs_debug`** | 低-中 |
| 13 | 多处兜底常量 | `AoiCullingFeature.Resolve` 750/1100、`MiyukiScanFeature.ReapplyCull` 750、`hs_aoi` 的 `AnnounceRule` 750/1100、`hs` 总览 600/900 —— 都是旧默认值 | 低（仅 entry 为 null 时命中，但会误导） |
| 14 | `Features/Broadcast/BroadcastFeature.cs` `Enabled`（`:43`）/ `Features/Vision/ProximityAlertFeature.cs` `Enabled`（`:28`） | 字段名与段级 `Enabled` 撞键，实际是**同一个开关**，字段上的默认值被忽略。而 `WhiteRadarFeature` / `WhiteWinFeature` 的注释明确警告"不能叫 `Enabled`" | 低（两处默认值恰好一致，今天无副作用），但是潜在陷阱 |
| 15 | `Features/Rule/RuleRewriteFeature.cs` 配置说明（`:41-45`） | 说明只列了 `time<=` / `kills>=` / `alive<=` 三种条件，实现里还有第四种 **`fusebox`**（`:185`） | 低 |
| 16 | `AGENTS.md` §1 的乱码示例 | 记的是"假人 → 鍋水漢"，实测 GBK 解码为 `鍋囦汉`（首字 `鍋` 一致） | 低 |
| 17 | `AGENTS.md` §"验证编译产物里的中文" | 建议用 UTF-8 解码 DLL 校验。实测会**假阳性**（`假人` 命中来自自定义特性的 UTF-8 存储，而非代码字面量）并**假阴性**（`冻结` 明明正确却匹配失败）。正确做法是 `[System.Text.Encoding]::Unicode` | 中（会给出错误的安全感） |
| 18 | `AGENTS.md` §7 第 1 条 | 写"`Copy-Item` 会失败 —— 但不检查返回值就会误报成功"；当前 `deploy.ps1` / `redeploy.ps1` 都设了 `$ErrorActionPreference = 'Stop'`，失败会抛错中断 | 低（描述的是更早的脚本版本） |
| 19 | `README.md` | "配置段"表用的是旧段名（`HS_*` 前缀）与旧默认值；"命令"表只列 6 条（实际 14 条）；"玩法概要"写 AOI 900、灯效"亮灭两次"等，均与现状不符 | 中（对外文档） |
| 20 | `PLAN.md` | 整体过时：`Patches/` 目录布局、`HS_Text`/`HS_AOI`/`HS_Combat` 段、用 Transpiler 改 `Corpse` 构造函数、`/hs` 带斜杠命令名、待办里"确定署名"等，全与现状不符（实际是 `Features/<领域>/`、无 `HS_` 前缀以外的段、用 `PushSurvivalJob` Prefix 拦截、命令不带斜杠、author 留空） | 中。建议只当"历史决策记录"读，不要当现状 |
| 21 | `TEXTS.md` | 行号部分过期（`BroadcastFeature` 偏 1~2 行、`KillUpgradeFeature` 偏 12 行、`WhiteCommandFeature` 偏 1 行），且未覆盖全部玩家可见文本（如"冷却中，还需 N 秒"、"玩家列表（/tp 可用 ID）"、"冻结冷却中"等） | 低-中 |
| 22 | `Features/Combat/SpeedBoostFeature.cs`（`:45-48`）、`Features/Rule/WhiteCommandFeature.cs`（`:342-343`）、`Features/Skill/TeleportGuardFeature.cs`（`:98`、`:154-160`） | 注释缩进被破坏（顶格或半格），是可读性问题 | 低 |
