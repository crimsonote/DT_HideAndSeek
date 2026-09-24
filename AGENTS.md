# HideAndSeek 开发规范

《Deadly Trick》的**房主端**捉迷藏玩法模块，参照 DT_Tools 的框架实现。

**核心约束：零改动上游。** 不修改 `D:\git\DT_Tools\DT_Tools\` 下任何文件；
与该项目的全部交互只能通过两条外部路径（配置复用 + 命令桥），详见下文。

---

## 编码（最容易踩，且不报错）

**所有 `.cs` 必须带 UTF-8 BOM。**

中文 Windows 上 Roslyn 对"无 BOM + 非 ASCII"的文件会退回**系统 ANSI（GBK）**读取，
中文字面量在**编译期**就已损坏，且不产生任何警告 —— 表现是游戏里看到乱码：

```text
"假人" 的 UTF-8 字节 = E5 81 87 E4 BA BA
E5 81 按 GBK 解读     = 鍋      ← 实测就出现过"假人 → 鍋水漢"这种乱码
```

- 用工具（编辑器 / write 类 API）**重写文件后必须复查 BOM** —— 覆写常常会丢掉它
- 复查与补齐：

```powershell
$utf8Bom = New-Object System.Text.UTF8Encoding($true)
Get-ChildItem -Recurse -Filter *.cs | Where-Object { $_.FullName -notmatch '\\obj\\' } | ForEach-Object {
    $b = [System.IO.File]::ReadAllBytes($_.FullName)
    if (-not ($b.Length -ge 3 -and $b[0] -eq 0xEF -and $b[1] -eq 0xBB -and $b[2] -eq 0xBF)) {
        [System.IO.File]::WriteAllText($_.FullName, [System.Text.Encoding]::UTF8.GetString($b), $utf8Bom)
    }
}
```

- 验证编译产物里的中文是否正确（比看源码可靠）：

```powershell
$u8 = [System.Text.Encoding]::UTF8.GetString([System.IO.File]::ReadAllBytes($dll))
$u8 -match '假人'      # 应为 True
$u8 -match '鍋'        # 应为 False
```

---

## 构建与部署

```powershell
# 构建（必须在 HideAndSeek 目录内：根目录的 global.json 钉的是未安装的 7.0.410）
cd D:\git\DT_Tools\HideAndSeek
dotnet build -c Release --nologo

# 部署（游戏需先退出，否则 DLL 被锁）
pwsh -File deploy.ps1
```

部署后**先看日志的"失败 N"**：

```text
[HS] HideAndSeek 0.1.0 加载完成：启用 17，跳过 2，失败 0
```

**`失败` 必须为 0。** 补丁挂不上不会让功能报错，只会静默失效 —— 曾因此让
"离开对局恢复光照"白改两轮。

---

## 代码组织

- 命名空间与目录一致：`HideAndSeek.Features.Vision` ↔ `Features/Vision/`。
- 功能一律 `[PatchFeature(section, description, defaultEnabled, side)]` 声明，
  子项用 `[ConfigField(default, desc, Min, Max)]`，由 `ConfigBinder` 自动 Bind，
  **禁止手写 `Config.Bind`**（`ConsoleBridge` 段除外）。
- `Plugin.cs` 只组装：解析配置来源 → `PatchLoader.Load` → 落盘 → 报告。
- 不署他人之名；author 留空。

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

同时要跳过 `State == EPlayerState.Hide`：死亡玩家被 `MakeSpectatorGhost` 置为
`Hide` + `IsGhost=true`，原版会跳过他们，我们补 `AddPlayer` 时也必须跳过，
否则会把死人的幽灵塞给黑方。

### 5. 假黑灯是客户端的本地状态

`Managers.Game.Darkness` 只由 `S_AREA_PUBLIC` 驱动。发这个包时 **`RoomId` 必须合法** ——
客户端 `ChangeArea` 会执行 `RoomDic[RoomId]`，`RoomId = 0` 直接抛 `KeyNotFoundException`，
`Darkness` 根本不会被改。

而且**不需要写"解除黑灯"逻辑**：原版回大厅时会自行 `Darkness = false`（客户端 `:29110`）。
多写的解除逻辑反而会在切区域（`Area.SendAreaInfo`）时把灯重新涂黑。
→ 判据只需一条：**房间处于 `Survive` 才维持黑灯**。

### 6. 上游 DT_Tools 没有"插件注册表"

它的 `[PatchFeature]` 与 `IConsoleCommand` 都只扫**自己**的程序集，外部无法注入。
集成只有两条路：

- **配置复用**：Bind 到 `DT_Tools.Plugin.Instance.Config`，段会出现在 DT CONFIG 页；
  注意它的 `Plugin.Instance` 在**它自己的 Awake** 里才赋值，我们在 `Start` 里读可能还是 null。
- **命令桥**：Prefix 拦截 `DT_Tools.Console.WebConsole.ExecuteCommand`。
  ⚠️ 它的命令列表缓存 `_cachedCommandsJson` 在**它的 Awake** 里就生成好了，
  而我们的补丁在 `Start` 才挂上（BepInEx 顺序：所有 Awake → 所有 Start），
  所以必须自己重建缓存，否则命令"能执行但不在列表/补全里"。

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
