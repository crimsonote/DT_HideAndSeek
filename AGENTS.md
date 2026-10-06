# HideAndSeek 开发规范

> **给外部读者**：本文件以及 `docs\` 下的部分内容是**本项目的内部协作材料** ——
> 面向维护者与 AI 会话，其中大量引用指向 `.tmps\` 下的过程材料（调查报告、试错记录、
> 一次性脚本）。那是**本地工作目录，刻意不进版本库**，所以那些引用在本仓库里点不开。
> 只想知道怎么用，请看根目录的 `README.md`。

《Deadly Trick》的**房主端**捉迷藏玩法模块，参照 DT_Tools 的框架实现。

**核心约束：零改动上游。** 不修改 `D:\git\DT_Tools\DT_Tools\` 下任何文件。

> ### 术语：本文档里的「上游」= DT_Tools（**可选依赖**，不是 git 上下游）
>
> 2026-10-07 约定：DT_Tools 对本模块是**可选依赖** —— 装了才有配置镜像与命令桥，
> 不装则全部静默降级、玩法功能一个不少。它**不是** git 意义上的上游：没有 fork、
> 没有 submodule、没有编译期引用，本仓库也不跟随它的版本节奏。
>
> **新写的文字请直接叫 `DT_Tools`**（要强调关系时写「可选依赖 DT_Tools」）。
> 历史段落里的「上游」沿用旧叫法，**见到顺手改掉即可，不要求一次性全改**；
> `CHANGELOG.md` 与 git 提交历史里的「上游」是历史记录，**不要改**。

**本模块可单独安装、单独配置。** 配置固定写在自己的 `BepInEx/config/HideAndSeek.cfg`，
与上游是否存在无关；装了上游时额外把**同一批配置条目**镜像进它的 ConfigFile，
使设置照旧出现在 DT CONFIG 页（见下文第 6 条）。命令桥（`hs_*`）是**可选**集成，上游不在就静默跳过。

---

## 编码（最容易踩，且不报错）

**所有 `.cs` 与 `.ps1` 都必须带 UTF-8 BOM。**

中文 Windows 上 Roslyn 对"无 BOM + 非 ASCII"的 `.cs` 会退回**系统 ANSI（GBK）**读取，
中文字面量在**编译期**就已损坏，且不产生任何警告 —— 表现是游戏里看到乱码：

```text
"假人" 的 UTF-8 字节 = E5 81 87 E4 BA BA
E5 81 按 GBK 解读     = 鍋      ← 实测就出现过"假人 → 鍋水漢"这种乱码
```

`.ps1` 是同一类问题，但**症状不同且更响**：PowerShell 也按 ANSI 读无 BOM 的脚本，
中文乱码后**乱码字节会破坏字符串引号的配对**，于是脚本根本解析不了：

```text
Missing closing '}' in statement block or type definition.
Unexpected token ')' in expression or statement.
The string is missing the terminator: ".
# 且报错回显里中文本身是乱码，例如 "濡傛灉杩欐槸…"
```

⇒ 看到"脚本语法错误 + 报错里中文乱码"就该先查 BOM，别去改语法。
（实机踩过：新写的 `deploy-version.ps1` 漏 BOM，被误判成 `Write-Host` 拼接写法错。）

- 用工具（编辑器 / write 类 API）**重写文件后必须复查 BOM** —— 覆写常常会丢掉它
- 复查与补齐（`.cs` 与 `.ps1` 一起扫）：

```powershell
$utf8Bom = New-Object System.Text.UTF8Encoding($true)
Get-ChildItem -Recurse -Include *.cs,*.ps1 | Where-Object { $_.FullName -notmatch '\\obj\\' } | ForEach-Object {
    $b = [System.IO.File]::ReadAllBytes($_.FullName)
    if (-not ($b.Length -ge 3 -and $b[0] -eq 0xEF -and $b[1] -eq 0xBB -and $b[2] -eq 0xBF)) {
        [System.IO.File]::WriteAllText($_.FullName, [System.Text.Encoding]::UTF8.GetString($b), $utf8Bom)
    }
}
```

- 验证编译产物里的中文是否正确（比看源码可靠）：

  ⚠️ **不要**用"把 dll 按 UTF-8 解码再搜字符串"的做法 —— 它只在**碰巧**成立时有效：
  普通字符串字面量存在 **`#US` 堆、是 UTF-16**；只有 `[ConfigField("…")]` / `[PatchFeature(description: "…")]`
  这类**自定义特性的参数**才在 `#Blob` 堆里、是 UTF-8。而把整个 PE 文件按固定 2 字节解码又会因为
  各堆长度差而错位。结果是「该找到的找不到、不该找到的找到了」—— 实机踩过：`假人`（在特性里）能搜到，
  而同一批**新增的日志文案**（在 `#US` 里）搜不到，白排查了一轮"产物是不是没更新"。

  可靠做法是用 BepInEx 自带的 `Mono.Cecil` 直接读 IL 里的字符串操作数：

```powershell
$dll   = "$GameDir\BepInEx\plugins\HideAndSeek.dll"
[void][System.Reflection.Assembly]::LoadFrom("$GameDir\BepInEx\core\Mono.Cecil.dll")
$asm   = [Mono.Cecil.AssemblyDefinition]::ReadAssembly($dll)
"AssemblyVersion = " + $asm.Name.Version
$all   = New-Object System.Collections.Generic.List[string]
$stack = New-Object System.Collections.Stack
$asm.MainModule.Types | ForEach-Object { $stack.Push($_) }
while ($stack.Count -gt 0) {
    $ty = $stack.Pop()
    $ty.NestedTypes | ForEach-Object { $stack.Push($_) }
    foreach ($m in $ty.Methods) {
        if (-not $m.HasBody) { continue }
        foreach ($ins in $m.Body.Instructions) { if ($ins.Operand -is [string]) { $all.Add($ins.Operand) } }
    }
}
@($all | Where-Object { $_.Contains('配置搬迁') }).Count      # 新增文案应为 1 以上
@($all | Where-Object { $_.Contains('鍋') }).Count           # 乱码哨兵应为 0
```

  这一招同时能确认「**部署的 dll 到底是不是这一版源码构建的**」，比看文件时间戳可靠。

---

## 构建与部署

```powershell
# 构建（必须在 HideAndSeek 目录内：根目录的 global.json 钉的是未安装的 7.0.410）
cd D:\git\DT_Tools\HideAndSeek
dotnet build -c Release --nologo

# 游戏不在默认路径时
dotnet build -c Release --nologo -p:GameDir="D:\...\Deadly Trick"
# 固定到仓库内快照（可复现构建）而不是游戏安装目录
dotnet build -c Release --nologo -p:GameManaged=..\libs

# 部署（游戏需先退出，否则 DLL 被锁）
pwsh -File deploy.ps1
```

游戏程序集默认取 `$(GameDir)\DeadlyTrick_Data\Managed`（= 运行期实际加载的那一份），
**不依赖 DT_Tools 仓库的 `libs/`** —— 本插件是独立仓库，单独 clone 只填 `GameDir` 就能构建。

部署后**先看日志的"失败 N"**：

```text
[HS] HideAndSeek 1.4.0 加载完成（DT_Tools 已检测到）：启用 17，跳过 2，失败 0。配置：...\BepInEx\config\HideAndSeek.cfg。捉迷藏模式当前 关闭（段 [HS_Mode].Enabled）。
```

**`失败` 必须为 0。** 补丁挂不上不会让功能报错，只会静默失效 —— 曾因此让
"离开对局恢复光照"白改两轮。

---

## 提交纪律：改完就提交，别攒着

**每个功能 / 修复做完（构建通过、verify.ps1 跑过）就提交，不要先跑去部署。**

为什么（2026-10-07 实际踩过）：一批改动（9 个文件、含一个新文件）做完后直接构建 + 部署，
工作区一直晾着未提交 ⇒ 丢改动的风险、回不到改动前、没法 review 中间过程；
`git log` 里那段时间是空白的，事后也说不清"部署出去的 DLL 对应哪一版源码"。

- 一次提交 = 一个逻辑改动。别把"适配上游 + 修检查器 + 文档回写"塞进同一个提交。
- 提交信息沿用本仓库风格：`类型(范围): 中文一句话`，正文写清**为什么**。
- **部署前先提交**：部署出去的产物必须能对应到一个提交。
- 提交后 `git status` 必须干净 —— 含**新增文件**（`git add` 容易漏掉 `??`）。
- 行尾：工作区一律 **CRLF**（`.gitattributes` 声明 `text`，仓库内 LF、检出 CRLF）。
  ⚠️ `edit` / `write` 这类工具写出来的文件可能是 **LF 或混合行尾**，编辑完要复查：
  `git ls-files --eol <文件>` 应显示 `w/crlf`。`verify.ps1` 有这项检查。
- 提交者身份：本仓库用 `crimsonote <crimsonote@outlook.com>`。
  ⚠️ DT_Tools 的 fork 仓库**没配** identity，在那里提交要带
  `git -c user.name=... -c user.email=... commit ...`。

## 代码组织

- 命名空间与目录一致：`HideAndSeek.Features.Vision` ↔ `Features/Vision/`。
- 功能一律 `[PatchFeature(section, description, defaultEnabled, side)]` 声明，
  子项用 `[ConfigField(default, desc, Min, Max)]`，由 `ConfigBinder` 自动 Bind。
  **禁止在功能里手写 `Config.Bind`** —— 合法例外只有**框架层 / 迁移层**这四处：
  `ConsoleBridge` 段、`Core/ConfigMigration.cs`（迁移版本键 `ConfigVersion`，以及迁移时"取回"现有段开关）、
  `Core/ModeRuntime.cs`（捉迷藏模式总开关）、`Core/Patching/PatchLoader.cs`（段级 `Enabled` 开关）。
  这四处都是"框架自己需要"，不是"某个功能偷偷绕过 `[ConfigField]`"。
- `Plugin.cs` 只组装：打开自有配置 → 搬迁旧配置 → `PatchLoader.Load` → 迁移 → 落盘 → 镜像到上游页面 → 报告。
- 不署他人之名；author 留空。

---

## 设置页框架（`Features/UI/`）

大厅「详细设置」（`UI_LobbyPreset`）里的《捉迷藏》页签与设置框架。

**加设置项 = 加一行声明**（改 `Features/UI/HideAndSeekSettingsContent.cs`），
**补丁代码（`LobbySettingPatches.cs`）与宿主（`LobbySettingHost.cs`）都不用动。**
加滑条/下拉/新页签的做法、prefab 路径常量、铁律与已知限制，见
**`Features/UI/EXTENDING.md`**（动手前先读它）。

两个容易踩的点：

- 声明里配置项必须写成 `() => Xxx.Entry` 的**惰性委托** —— `RegisterAll()` 跑在
  `PatchLoader.Load` 之前，那时所有 `ConfigEntry` 还是 `null`。
- 克隆出来的控件**必须** `RemoveAllListeners()`：`Instantiate` 会把原版监听器一起复制，
  否则"点我的页签会连带触发原版 `SwitchTab`"、"拨我的开关会连带改原版设置"。

---

## 已踩过的坑（改代码前先读这一节）

### 1. 同名类型会被外层命名空间抢走

`Assembly-CSharp` 里存在这些重名：`Corpse` / `Player` / `Item` / `DeviceManager` / `Summon`。
C# 的查找规则里**外层（全局）命名空间优先于 `using` 引入的命名空间**，于是
`typeof(Corpse)` 会静默解析到**全局那个**，编译通过但 Harmony 运行时找不到目标。

→ 一律用别名显式限定：

```csharp
using GamePlayer = Server.Game.Player;
using GameCorpse = Server.Game.Corpse;
using GameDeviceManager = Server.Game.DeviceManager;
```

另外服务端 `Device` 读状态用 **`DeviceInfo`**，客户端的 `DeviceBase.Info` 是另一套。

### 2. `System.*` 会被自己的命名空间截胡

本程序集存在 `HideAndSeek.Features.System`，所以在 `Features.*` 下写
`System.StringSplitOptions` 会解析到它。→ 用 `global::System.XXX`。

### 3. Harmony 重载歧义

目标方法有重载时，`nameof` 不带参数类型会抛 `Ambiguous match`，整个补丁挂不上。
`GameRoom.ChangeGameState` 就有两个重载（public 单参 / private 双参）：

```csharp
[HarmonyPatch(typeof(GameRoom), nameof(GameRoom.ChangeGameState), new[] { typeof(EGameState) })]
```

私有方法用字符串定位（`[HarmonyPatch(typeof(X), "PrivateMethod")]`），
但内部类型（如 `MissionManager`）用字符串类型名会解析到 `0Harmony` 上，
必须改用 `[HarmonyTargetMethod]` + `AccessTools.TypeByName`。

### 4. `AddPlayer` 是双向的，剔除必须自己补回来

`X.AddPlayer(Y)` 的语义是"把 X 介绍给 Y"。原版让某人**重新可见**的唯一途径是
**被观察者自己移动**（`Player.Move` → `SearchAndUpdatePlayer`）。
站着不动的目标一旦被 `RemovePlayer`，就再也没有路径被加回来 —— 客户端上那个
Player 对象已 despawn，走到跟前也看不见。所以 AOI 必须：
黑方移动时立即校正 + 每秒 tick 兜底，两处都主动 `AddPlayer`。

同时要跳过 `State == EPlayerState.Hide` —— 它**有两个来源**，两个都要跳：

- **活人躲进柜子**：`Cabinet.HideCabinet`（`:162129`）置 `State = Hide` + `HidePlayer`（出柜时 `ExitCabinet` `:162138` 复位成 `Idle`）；
- **死亡 / 幽灵**：`MakeSpectatorGhost`（`:175590`）置 `Hide` + `IsGhost = true`。

原版 `SearchAndUpdatePlayer`（`:173429`）同样跳过他们，我们补 `AddPlayer` 时也必须跳过 ——
否则会把死人的幽灵、以及柜子里的活人一起塞给黑方。
（把这条只理解成"死亡"是常见误读：`Hide` 同时是"躲柜子"，见 `.tmps/刀杀与DT-判定-调查.md` §6。）

### 5. 假黑灯是客户端的本地状态

`Managers.Game.Darkness` 只由 `S_AREA_PUBLIC` 驱动。发这个包时 **`RoomId` 必须合法** ——
客户端 `ChangeArea` 会执行 `RoomDic[RoomId]`，`RoomId = 0` 直接抛 `KeyNotFoundException`，
`Darkness` 根本不会被改。

而且**不需要写"解除黑灯"逻辑**：原版回大厅时会自行 `Darkness = false`（客户端 `:29110`）。
多写的解除逻辑反而会在切区域（`Area.SendAreaInfo`）时把灯重新涂黑。
→ 判据只需一条：**房间处于 `Survive` 才维持黑灯**。

### 6. 上游 DT_Tools 没有"插件注册表"

它的 `[PatchFeature]` 与命令注册表（旧版叫 `IConsoleCommand`，v1.0.9.0 起改名 `ICommand`）
都只扫**自己**的程序集，外部无法注入。
因此集成只有两条**可选**路径（见 `Core/DtBridge.cs`）：

- **配置：自己的文件为准 + 镜像到上游页面**。权威来源永远是
  `BepInEx/config/HideAndSeek.cfg`（`Core/Config/HsConfigFile.cs`），上游在不在都一样。
  装了上游时，额外把同一批 `ConfigEntryBase` 对象注入它的 `ConfigFile`
  （反射私有字段 `<Entries>k__BackingField`，见 `Core/Config/ConfigEntries.cs`），
  DT CONFIG 页因此照旧列出并可修改本模块的设置。
  - ⚠️ **镜像必须在全部 Bind 之后做**。早一步会镜像进一批残缺条目。
  - ⚠️ **两侧是同一批对象**，值只有一份 ⇒ 不需要任何同步代码。经 DT 页面改写会
    立即生效，并自动落到 `HideAndSeek.cfg`。
  - ⚠️ **上游 `Save()` 会把镜像条目一起写进 `DT_Tools.cfg`** —— 那是副本，不是权威。
  - ⚠️ **段名绝不能与上游撞车**：撞上时 `TryMirror` 显式跳过该键（覆盖上游条目远比
    "少显示几项"严重）。改段名/加段前先跟上游的全量段名对一遍 —— **2026-10-07 实测上游
    v1.0.9.0 有 71 个段名（本模块 47 个），无撞车**；核对脚本见 `.tmps` 里的一次性提取命令。
  - ⚠️ **上游配置入口随版本变化**：v1.0.9.0 起是 `DT_Tools.Core.Engine.Config`（`public static`），
    更早挂在 `Plugin.Instance.Config` 上 —— `DtBridge.TryGetDtConfig` 两个都探、取到哪个用哪个。
    上游 CONFIG 页的数据源是 `ConfigService.List(Engine.Config)`，它遍历 **ConfigFile 的全部条目**，
    所以镜像进 ConfigFile 依旧会显示（这一点没变）。
  - `SaveOnConfigSet` 用法：`Plugin.Start` 先置 `false`（避免 270 项各写一次文件），
    Bind + 迁移完成后统一 `Save()`，再置回 `true` ⇒ 此后**任何来源**的改动都自动落盘。
- **命令桥（可选）** —— 上游 v1.0.9.0 把命令域整个重写了，桥接点必须跟着换
  （旧写法已**彻底失效**：类型名从 `DT_Tools.Console.WebConsole` 变成
  `DT_Tools.WebConsole.WebConsole`，而 `ExecuteCommand` / `BuildCommandsJson` /
  `_cachedCommandsJson` 三个标识符一个都不存在了）：
  - **执行**：Prefix `DT_Tools.WebConsole.WebConsole.ExecuteCommandText(string)` ——
    它是 WebUI 命令泵与 MCP `run_command` 的**唯一**执行收口。命中 hs_* 时自行处理，
    反射构造上游 `CommandResult` 作为返回值并 `return false`（否则原版按"未知命令"记警告）。
    - ⚠️ `CommandResult` 是本模块无法编译期引用的类型，所以 `__result` 只能写成 `ref object`
      （Harmony 文档允许"类型匹配**或可被其赋值**"，`object` 满足）。
      **该写法尚未实机验证** —— 首次部署后务必看日志的「失败 N」与 `hs_check`；
      万一挂不上，备选方案是改走 `CommandRegistry.TryGet` 注册"替身命令"
      （需要 DispatchProxy / Emit 动态实现 `ICommand`，代价明显更高）。
  - **列表**：上游 `CommandsApi.HandleList` 只在**首次请求**时构建 `_cachedJson`，
    而我们的补丁要到 `Start` 才挂上（BepInEx 顺序：所有 Awake → 所有 Start）。
    所以本模块在自己 Start 里（早于任何浏览器请求）反射重建该缓存 = 上游全部命令 + hs_*，
    上游命令的元数据经 `ICommand` 接口属性逐条读取。
  - ✅ 若日志出现"检测到的是**旧版** DT_Tools"，说明上游还没更新到 v1.0.9.0（不是本模块的问题）。
  - 段被关掉时**整类**都要跳过（`Diagnostics.IsLoaded` 判据）—— 只跳一半会让
    `hs_*` 出现在上游列表里、点了却没反应。
- **DT CONFIG 页的分类登记**（`DtBridge.TryRegisterCategory`）：反射往 DT_Tools 的私有静态字典
  `Engine.SectionCategories` 补登记本模块的段 ⇒ CONFIG 页出现一个 **【捉迷藏】** 文件夹，
  不再散在「其他」里。上游前端对未知分类名直接拿 key 当显示名（`CATEGORY_LABELS[k] || k`），
  所以**零改动 DT_Tools、也零改动前端**。段名不硬编码，取自本模块配置里实际出现的全部段。
- **写盘隔离**（`Core/DtMirrorGuard.cs`，段 `[DtMirror]`）：镜像的副作用是 DT_Tools 的 `Save()`
  会把我们的条目一并写进 `DT_Tools.cfg`（2026-10-07 实测：那个文件 89 段里有 41 段来自本模块）。
  现在在 `ConfigFile.Save` 的 Prefix 把我们的键**临时摘出**、Finalizer 放回 ⇒ 落盘时物理上
  没有我们的条目，而 CONFIG 页照旧能列能改。⚠️ 只对新版 DT_Tools（`Core.Engine`）生效。
- **前端增强走本地 fork**（分支 `local/webui-list-mode`，工作树 `D:\git\DT_Tools\.tmps\wt-dt-webui`，
  **不属于本仓库**）：① 配置页加「网格/列表」切换，列表行显示分类说明与段摘要
  （摘要直接取该段 `Enabled` 项的 description ⇒ 不需要后端补字段）；
  ② 顶栏加**平铺模式**：窗口铺满「顶栏之下、Dock 之上」、隐藏标题栏、禁用拖动缩放、
  平铺时自动关粒子，切换靠顶栏标签与 Dock（标签页式）。
  ⚠️ DT_Tools 一更新，这份 fork 要 rebase，否则前端会退回未改的版本。

### 8. 段开关会连带跳过该段的所有补丁（排查时先看这个）

`[PatchFeature]` 的 `defaultEnabled: false` 意味着 PatchLoader 对**整个类**跳过
PatchAll —— 该段里**所有**钩子都不会挂上，包括与"自动行为"无关的**基础能力**。
而命令桥是独立通道、不受段开关影响，于是会出现最糟的组合：
**命令能生成对象，却没有任何钩子处理它**。

所以基础能力与可选自动化必须分到不同的段（例：假人的"自动生成"归 Dummy，
"按指定角色选角"独立为 DummyPick 并默认启用）。

**排查顺序**：先确认补丁挂载状态（日志里的 `失败 N`、`已跳过功能`），再看代码逻辑。
"日志里没有输出"有两种截然不同的解释 —— 代码跑了但没打日志，还是代码根本没运行 ——
而 `已跳过功能: XxxFeature` 这行能直接排除其一。
### 7. 部署相关

- 游戏运行时 `HideAndSeek.dll` 被锁，`Copy-Item` 会失败 —— 但**不检查返回值就会误报成功**。
- 上游 DT_Tools 需要 `MonoMod.Backports.dll` 与 `MonoMod.ILHelpers.dll` 在
  `BepInEx\core`，缺了它 `Awake` 静默失败、WebConsole 根本不启动（19450 无监听）。

### 9. 与上游改同一个方法时，必须显式钉住顺序

两个插件 Prefix 同一个方法时，**Harmony 只让第一个 `return false` 的 Prefix 生效**；
若两个都是 void、只改参数（`ref`），则**谁后跑谁的值留下**。默认优先级相同时，
顺序取决于补丁挂载次序 ⇒ 会随插件加载先后漂移，实测不出来、也没法复现。

所以**只要与上游改了同一个方法，就必须写 `[HarmonyPriority(...)]`** 并在注释里说明取向。
本模块的统一取向是：**捉迷藏模式开启时模式规则优先，模式关闭时完全让给上游**
（每个钩子入口的 `ModeRuntime.Bypass` 正好就是这个分界）。

⚠️ 注意 `Priority` 的数值方向：`First = 800`、`Last = 0`，**值大者先跑**。
用常量名（`Priority.First` / `Priority.Last`）而不是字面量。

当前 5 处需要钉顺序的重叠，全部已钉死（加新功能前先跟上游的补丁目标对一遍）。

⚠️ **换上游版本后必须重跑检查器，别只看本文档的行数**：2026-10-07 上游 v1.0.9.0 那次重构
就新增了 `StartPick` 这一处（还是三方重叠），同时把两处**一直存在却被别名漏报**的重叠暴露出来。

| 目标方法 | 上游功能 | 本模块 | 优先级 | 理由 |
|---|---|---|---|---|
| `GameRoom.get_BlackKillLimit` | `BlackAttack.KillLimit` | `KillLimit` | `First` | 两边都写 `__result` 后 `return false`，先跑的赢 |
| `GamePlayer.StartWeaponCooltime` | `BlackAttack.Cooltime` | `WeaponCooldown` | `First` | 上游只认入参 5/20；本模块先把 20 改掉，上游就认不出来、不再插手 |
| `TimeManager.PushSurvivalJob` | `CorpseWait` | `CorpseReport` | **`Last`** | 本钩子是 void、只能改参数，必须**最后**赋值才能压过上游 |
| `Define.get_LOBBY_MIN_PLAYER` | `LobbyMinPlayers` | `SoloPlay` | `First` | 同第一行 |
| `GameRoom.StartPick` | `SpectatorJoin`（**bool Prefix 整替原方法**） | `Dummy`（选角前生成假人） | `First + 1` | 上游整替体自己重写了一份"清空存活池 → 分配颜色 → 随机黑方"；假人必须**赶在它那份列表快照之前**入场，否则落在 `AlivePlayers` 之外，本局等于没生成 |

复核方法（仓库内自带检查器，跟着代码走、不进 `.tmps/`）：

```powershell
# 列出两侧的补丁目标重叠，并对"两侧都是 Prefix 但本模块没写 [HarmonyPriority]"判 FAIL
pwsh -File check-upstream-overlap.ps1
pwsh -File check-upstream-overlap.ps1 -Upstream "D:\其它路径\DT_Tools"
```

```text
# 上游 4ff672f（v1.0.9.0）实测输出 —— 10 处重叠，0 FAIL
GameRoom::HandleEnterPlayer::method        HS[Postfix] DT[?+Prefix]         no-priority
GameRoom::BlackKillLimit::getter           HS[Prefix]  DT[?]                priority
Player::OnDeadMurder::method               HS[Postfix] DT[?]                no-priority  ← 别名归一并入后可见
GameRoom::StartDetective::method           HS[Postfix] DT[Transpiler]       no-priority
Player::StartWeaponCooltime::method        HS[Prefix]  DT[?]                priority     ← 别名归一并入后可见
Define::LOBBY_MIN_PLAYER::getter           HS[Prefix]  DT[?]                priority
GameRoom::StartPick::method                HS[Prefix]  DT[Prefix]           priority     ← v1.0.9.0 新增，三方重叠
GameRoom::PickCharacterTick::method        HS[Postfix] DT[?]                no-priority
Managers::Update::method                   HS[Postfix] DT[?]                no-priority
TimeManager::PushSurvivalJob::method       HS[Prefix]  DT[?]                priority
```

检查器输出的 DT 侧显示 `?` 是**启发式的正常现象**：它在 `[HarmonyPatch]` 之后 40 行内找
hook 属性，被 `[PatchFeature]` / 字段声明隔开就找不到。上表已人工核实为实际 hook 类型（见下）。

`HandleEnterPlayer` / `StartDetective` / `PickCharacterTick` / `OnDeadMurder` / `Managers::Update`
五条无需处理：上游那侧是"整段替换原方法"的 Prefix、Transpiler、`out __state` 配对的前后置补丁，
或干脆也是 Postfix，与本模块的 **Postfix** 不冲突 —— **Prefix 返回 false 时 Harmony 仍会执行 Postfix**
（Harmony ≥ 2.2 起，返回 false 的 Prefix 只跳过原方法体，**不再阻止其它 Prefix** 执行）。

### ⚠️ 别名会让检查器漏报（已修，但要知道原理）

本模块为避开同名类型大量使用 `using GamePlayer = Server.Game.Player;` 这类别名。
旧版 `check-upstream-overlap.ps1` 只比较**类型名末段**，于是本模块的
`GamePlayer::StartWeaponCooltime` 与上游的 `Player::StartWeaponCooltime` 被当成两个不同目标 ——
**真实重叠静默漏报**，`StartWeaponCooltime` 与 `OnDeadMurder` 两处一直没被检查器看见
（前者靠人工注释侥幸标了优先级，后者纯属运气）。

现在 `Scan-Lines` 会先展开文件顶部的 `using X = A.B;` 别名再归一。
**新增补丁若用了别名，务必确认检查器能看见它**（跑一次，看目标名是否为真实类型）。

---

## 改默认值时必须同时改 `.cfg`

**不要**为"升级用户"写配置迁移去覆盖旧值 —— 那可能覆盖用户自己的定制，而且会无限膨胀
（每改一次默认值就往迁移表塞一条，几年后没人敢删）。

正确做法：

- 改 `[ConfigField]` 默认值的**同一次提交**里，把 `.cfg` 对应的键一起改掉；
- 并在提交说明里**列出改了哪些键、新值是什么**；
- 代码只管"键不存在时用默认值"，**不猜用户意图**。

**为什么**：BepInEx 的 `ConfigFile` 只在键**不存在**时才用默认值写入；一旦 `.cfg` 里已有该键，
之后改代码默认值**完全不会生效**。实机踩过两次：

- 段名从 `[BreakCommand]`/`[WhiteCommand]` 改成 `[Command]` 后，旧键留在 `.cfg` 里，
  新默认值进不去（这次是"迁移整段空转"，见 `ConfigMigration` 的注释）；
- `[KeyLock] LanternItemId` 是 v0.3.3 首次启动时生成的（当时默认 `4006`），
  后来代码改成 `1059` 也进不去 ⇒ 症状是"发蓝提灯（图标缺失→白方块）+ 永远黏不了门"
  （判定是"手里 ID 在 1059~1061 才算鱼"，而手里是 4006）。

`ConfigVersion` 只在**真的需要搬键**（段名/键名变动、类型变更）时 +1；
单纯改默认值不动它。

---

## 补丁的嵌套层级只有一层

`PatchLoader` 用 `GetNestedTypes()` 扫的是**一层**，`Harmony.PatchAll(Type)` 也只处理
该类型**声明**的方法。所以：

```csharp
[PatchFeature(section: "Xxx", ...)]
internal static class XxxFeature            // L0
{
    [HarmonyPatch(typeof(T), "M")]
    internal static class SomeHook          // L1 ✅ 会挂上
    {
        [HarmonyPatch(typeof(T2), "M2")]
        internal static class InnerHook     // L2 ❌ 永远不会挂载
        { ... }
    }
}
```

**L2 静默失效，而且日志里的「失败 0」也不会反映** —— `FailedCount++` 只统计已扫到的那一层。
实机踩过：`FuseboxRevealFeature` 的 `BecomeBlackHook`（"黑方诞生时补发电箱标记"）
嵌在 `SendSabotageMissionHook` 里面，从未挂载，于是"黑方看不到电箱"一直存在，
日志里连它那行无条件的 `LogInfo` 都没有。

- 需要多个补丁就**平级**放（都是 L1）；
- 工具方法放**最外层**（L0）—— C# 里嵌套类能访问外层 private，**兄弟嵌套类之间不能**；
- `verify.ps1` 有检查盯这个。排查顺序见"已踩过的坑"第 8 条。

---

## 设计备忘与调查（放在 `.tmps/`，**刻意不进 git**）

**备忘**（未实现的设计方案、临时结论）与**调查**（大范围源码排查的记录）都是**过程材料**，
放在 `.tmps/`，**不进版本控制** —— 它们会被改写、会过期，进仓库只会变脏、还会误导后人。

动手改某块之前**先翻一下 `.tmps/`**：里面通常有该模块之前的调查与已定方案，
避免重复调查、或推翻已定结论。

> `docs/` 是留给**正式文档**的（要长期维护、随仓库走的东西）。
> **别把备忘或调查报告塞进 `docs/`** —— 那是两种不同性质的材料。

---

## 常用命令

| 命令 | 用途 |
|---|---|
| `hs` | 模式与参数总览 |
| `hs_check` | **自检**：每个功能的 `loaded`（补丁是否挂上）与 `hits`（触发次数）——能区分"没挂上"和"挂上但没触发" |
| `hs_mode on\|off` | 模式总开关（含可见性与光照回滚） |
| `hs_aoi` | 视野裁剪参数 |
| `hs_cd` / `hs_killlimit` | 击杀冷却 / 次数上限 |
| `hs_dummy add\|del\|list\|clear` | 假人靶子 |
| `hs_flash on\|off` | 开局灯效开关 |

---

## 知识复用纪律（动手前必读）

> 背景：本项目的调查结论曾长期只落在 `D:\git\DT_Tools\.tmps\`（**不进 git，每个 clone 都没有**），
> 于是新会话看不见、只能从零倒查源文件 —— 同一件事被查了不止一遍（现已积累 **44 份**过程材料）。
> 下面五条就是为此定的：**它们不是建议，是流程。**
>
> 与上文「设计备忘与调查」一节的关系：那一节说的是**过程材料别塞进 `docs/`**（仍然有效）；
> 本节补上另一半 —— **结论必须从过程材料里提炼出来、进 `docs/`**，否则下一个人看不见，只能重查。

### 1. 先查表，再读源码

动手前按顺序读：

1. `HideAndSeek\docs\README.md` —— 知识分层约定 + 44 份过程材料的**主题索引**（按 10 个机制域归类）
2. `HideAndSeek\docs\事实索引.md` —— 硬事实表（事实 / 出处 / 置信度 / 谁验证过 / 未验证项）
3. 命中就**直接引用**；只在"你即将改动的那几行"上用新版程序集的 IL 复核（`Mono.Cecil`，脚本见 `.tmps\tools\il-*.ps1`）

**禁止**一上手就全仓 grep、逐文件倒查源码。把两个词分清：

| 词 | 含义 | 代价 |
|---|---|---|
| 复核 | 确认这一行还在、语义没变 | 几十秒 |
| 重查 | 把整条链路重新推一遍 | 一次会话 |

只有 `docs\` 与 `.tmps\` 都查不到，才允许做全仓调查 —— 做完按第 2 条回写。

### 2. 调查完必须回写（否则等于没做）

| 材料 | 落点 | 进 git |
|---|---|---|
| 原始日志、一次性脚本、还没有结论的草稿、被证伪的方案 | `.tmps\` | ✗（可丢） |
| **耐久结论**（会被下次改动引用的事实 / 规格 / 约束 / 测试口径） | `docs\` | ✓（子会话必读） |

- 结论必须带三件套：**出处**（`文件:行` 或 `IL`）+ **置信度**（高 / 中 / 低）+ **未验证项**（【推测】/ 未实机 / 未用新版 IL 复核）。
- **不许只留指针**：`docs\` 里不能写"详见 `.tmps\xxx.md`"就完事 —— `.tmps\` 不在任何 clone 里，指针必死。
- 新结论顺手进 `docs\事实索引.md`；新主题顺手在 `docs\README.md` 的对应机制域加一行。

### 3. 行号必须带版本

同一份反编译在不同时间点行号不同。引用一律写成 `ACS:175952` 这类**带口径**的形式，并注明是哪一版：

| 口径 | 指什么 | 时间 |
|---|---|---|
| `ACS`（旧反编译） | `.tmps\decomp\Assembly-CSharp.decompiled.cs`，180168 行，游戏 0.1.14b | 2026-09-22 |
| `IL`（新版程序集） | `DeadlyTrick_Data\Managed\Assembly-CSharp.dll`，MVID `fbd56bfe-9ce4-4ae7-bafb-3dc77be19ee8` | 2026-10-02 |
| 本仓库代码 | 直接写 `文件:行`，并优先用**方法名**定位（代码一改行号就漂） | 随提交 |

引用旧行号时必须写清「**未用新版 IL 复核**」还是「**已用新版 IL 复核（MVID …）**」—— 两者是不同强度的证据。

### 4. 派子会话的提示词模板

必读清单、交付格式、"查不到怎么写"三件缺一不可，直接抄这段：

```text
【必读，动手前读完，不要跳】
1. D:\git\DT_Tools\HideAndSeek\docs\README.md      ← 知识索引，先看你要动的机制域
2. D:\git\DT_Tools\HideAndSeek\docs\事实索引.md     ← 硬事实表，命中就直接引用
3. D:\git\DT_Tools\HideAndSeek\docs\<机制-主题>.md  ← 与本次任务相关的那一份耐久文档
4. D:\git\DT_Tools\HideAndSeek\AGENTS.md           ← 铁律与踩坑
禁止：一上手全仓 grep 倒查源码（前面已有 44 份调查，大概率是重复劳动）。

【交付格式，逐条写】
- 结论：一句话先行
- 证据：文件:行号 / IL（标明是旧反编译 09-22 还是新版程序集 10-02）
- 置信度：高 / 中 / 低
- 未验证项：哪些是【推测】、哪些没实机验证、哪些没复核
- 查不到的写「未找到」，并给出下一步怎么查（换口径 / 加探针 / 问房主）——不许用推测填空
- 过程材料写进 .tmps\（不进 git）；耐久结论按上面第 2 条回写 docs\

【不许做的事】
- 不许改 D:\git\DT_Tools\DT_Tools\ 下任何文件（上游零改动）
- 不许把结论只留在会话里：会话一结束就没了，等于没查
```

### 5. 文档落点速查

| 你要写的东西 | 放哪 | 进 git |
|---|---|---|
| 一条会被反复引用的硬事实 | `docs\事实索引.md` | ✓ |
| 一个机制的规格 / 约束 / 排查依据 | `docs\<机制>-<主题>.md` | ✓ |
| 部署后逐条验的通过标准 | `docs\测试清单.md` | ✓ |
| 原始日志、探针脚本、草稿、被证伪方案 | `.tmps\` | ✗ |
| 编码 / 构建 / 补丁铁律、踩坑 | `AGENTS.md`（或 `DEVELOPMENT.md`） | ✓ |
| 紧挨代码的局部扩展方式 | 同级目录的 `EXTENDING.md` 之类 | ✓ |
