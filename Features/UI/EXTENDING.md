# 设置页框架：扩展点说明

> 对象：以后要给「详细设置」加设置项的自己。
> 一句话：**加一个开关 = 加一行声明；加一种新控件 = 加一行声明 + 一个克隆方法。补丁代码永远不动。**

---

## 1. 文件职责（Features/UI/）

| 文件 | 职责 | 什么时候要改 |
|---|---|---|
| `LobbySettingItem.cs` | 设置项**声明**（纯数据：键/标题/形态/配置项/可编辑性） | 只有加新**控件形态**时（加一个静态工厂） |
| `SettingRegistry.cs` | 页签 + 设置项的**注册表 = 唯一扩展点** | **不用改** |
| `HideAndSeekSettingsContent.cs` | 本插件页签的**全部声明** | **加/删设置项就改这里** |
| `SettingRowBuilder.cs` | 克隆行模板 → 换标题 → 重绑控件 | 只有加新**控件形态**时（加一个 `BuildXxxRow`） |
| `LobbySettingHost.cs` | 建页签/面板、三页互斥可见性、值同步、写回 + 落盘、房主闸门 | 只有加新**控件形态**时（加一个 `case` 分发） |
| `LobbySettingPatches.cs` | 三个 Harmony 钩子（Init / Refresh / SwitchTab），只做转发 | **不用改** |
| `UiLog.cs` | 统一日志出口，前缀 `[HS][UI]` | 不用改 |

---

## 2. 场景 A：加一个开关（最常用）

只改 `HideAndSeekSettingsContent.cs`，在 `RegisterAll()` 里加一行：

```csharp
LobbySettingItem.Toggle(
    "syringe",                              // 稳定标识，只进日志，不写 .cfg
    "启用注射器强化",                        // 行标题（中文，直接进 TMP_Text）
    () => SyringeFeature.Boost),            // 惰性取配置项；不要写成 SyringeFeature.Boost（那是 null）
```

必须遵守三条：

1. **配置项用 `() => Xxx.Entry` 惰性引用**，不能直接传字段值 ——
   `RegisterAll()` 跑在 `PatchLoader.Load` **之前**，那时 `ConfigEntry` 还没绑定，直接传是 `null`。
2. **不要新写 `Config.Bind`** —— 用 `[ConfigField]` 让 `ConfigBinder` 绑定
   （合法例外只有框架层那 4 处，见 AGENTS.md）。
3. **不要动 `.cfg` 默认值** —— 已有键的值优先，改代码默认值对老用户无效。

可选：让某一行随其它配置变灰/不可点，用 `IsEditable`：

```csharp
LobbySettingItem.Toggle("weapon_id", "发刀武器", () => WeaponGrantFeature.WeaponId,
    isEditable: () => WeaponGrantFeature.GiveAtStart != null && WeaponGrantFeature.GiveAtStart.Value),
```

`IsEditable` 返回 `false` 时，该行 alpha=0.4 且不响应点击（语义照抄原版
`UI_LobbyPreset.SetRowEditable`，`ACS:57129-57135`）。传 `null`（默认）= 始终可编辑。

---

## 3. 场景 B：加滑条 / 下拉 / 文本行

两步，**都不碰补丁**：

**第 1 步** 在 `LobbySettingItem.cs` 加一个静态工厂（例：滑条）：

```csharp
public static LobbySettingItem Slider(string key, string title,
    Func<ConfigEntry<float>> entry, float min, float max, Func<bool> isEditable = null) { ... }
```

**第 2 步** 在 `SettingRowBuilder.cs` 加 `BuildSliderRow`，模板取原版对应行
（下表已实测，不需要再摸 prefab）；再到 `LobbySettingHost.BuildPanel` 的 `switch` 里加一个 `case`。

| 控件 | 占位模板（原版行） | 模板里的关键组件 |
|---|---|---|
| 开关 | `ETCPresetContent/MastermindVoteLoss` | `TitleBg` + `MastermindVoteLossToggle` |
| 滑条 | `ETCPresetContent/WeaponMove` | `TitleBg` + `WeaponMoveSlider`(`Slider`+`UI_GaugeSlider`) + `NumberBg/WeaponMoveValue` |
| 下拉 | `DifficultyPresetContent/Difficulty/DifficultyDropdown` | `TMP_Dropdown`（`_dropdown` 是原版私有字段，克隆后要自己持引用） |
| 纯文本行 | 任意行的 `TitleBg` | 只有标题，无控件 |

模板的共同结构（实测 dump，见 `.tmps/lobbyprefab_full.txt`）：

```text
<行根>  ::  RectTransform                      ← 直接父物体是内容容器，克隆就是这么整行克隆
  TitleBg  ::  Image, ContentSizeFitter, HorizontalLayoutGroup
    <Xxx>Title  ::  TextMeshProUGUI, ContentSizeFitter
  <Xxx>Toggle / <Xxx>Slider / ...
```

新形态在 `LobbySettingKind` 里也要加一个枚举值（`LobbySettingHost` 的 `default` 分支会在漏配时打 Error 日志，不会静默）。

---

## 4. 场景 C：加第二个页签

再调一次 `Register(...)` 即可，**不需要重构**：

```csharp
SettingRegistry.Register("更多", 200,
    LobbySettingItem.Toggle("xxx", "某开关", () => XxxFeature.Some));
```

宿主按注册表循环建页签与面板，页签栏自带的 `ToggleGroup` 负责互斥，
`HorizontalLayoutGroup` + `ContentSizeFitter` 负责排布与撑宽，**不用手写互斥、不用手工定位**。

---

## 5. 铁律（违反会静默出错）

| # | 铁律 | 症状 |
|---|---|---|
| 1 | 所有 `.cs` 必须带 **UTF-8 BOM** | 中文在**编译期**坏成 GBK 乱码，**零警告**。写完文件必跑 BOM 补齐脚本 |
| 2 | 补丁嵌套**只有一层**：L0 = `[PatchFeature]` 类，L1 = 钩子类 | L2 **静默失效**，日志里「失败 0」也不反映 |
| 3 | 任何写配置的代码必须显式 `Save()` | 复用上游 DT_Tools 时其 `SaveOnConfigSet = false` →「房主改完设置、退出就丢」。本框架在 `LobbySettingHost.OnRowValueChanged` 里统一 `HsConfig.Save()` |
| 4 | `Features.*` 下写 `System.*` 会被 `HideAndSeek.Features.System` 截胡 | 用 `global::System.XXX` |
| 5 | 克隆出来的控件必须 `RemoveAllListeners()` | `Instantiate` 会把原版监听器一起复制。页签上表现为"点我的页签连带触发原版 SwitchTab"；行上表现为"拨我的开关连带改原版设置"。**最易踩、最难查** |
| 6 | 零改动 `DT_Tools\`（上游）与 `HideAndSeek\`（主工作区） | —— |

---

## 6. prefab 路径常量（`LobbySettingHost` 顶部）

全部来自 `UI_LobbyPreset` prefab 的实测 dump（`.tmps/lobbyprefab_full.txt`，来源 bundle
`common_prefab_assets_all_*.bundle`），不是猜的：

| 常量 | 值 |
|---|---|
| `TabBarPath` | `PresetPanel/FooterToggleGroup` |
| `PanelRootPath` | `PresetPanel/PresetPanels` |
| 原版页签 | `Toggle_Difficulty`、`Toggle_ETC`（**页签栏不在 `UI_LobbyPreset.GameObjects` 枚举里**，代码取不到，只能按路径/父物体反推） |
| 原版面板 | `Preset_Difficulty`、`Preset_ETC` |
| `ContentPath` | `Scroll View/Viewport/ETCPresetContent` |
| `RowTemplatePath` | `Scroll View/Viewport/ETCPresetContent/MastermindVoteLoss` |

路径全部未命中时**只会打一条 Error 日志并安静退出**（不影响游戏本体），
日志形如 `[HS][UI] 设置页签构建失败：prefab 层级路径未命中（tabBar=False, ...）`
—— 看到这条就说明上游改了 prefab 结构，按交接文档 §8.3 上运行时探针重新对路径。

页签选中态视觉（底图 + 文字色）是**复刻**原版 `UI_LobbyPreset.SwitchTab` / `SetTabSprite`
的：底图 `setting_tab_on.sprite` / `setting_tab_off.sprite`，选中文字色 `#FF2E70`。

---

## 7. 验收步骤

```powershell
cd D:\git\DT_Tools\.tmps\hs-ui-framework
# 1) 补 BOM（write/edit 之后必做，否则中文编译期就坏了）
#    脚本见 AGENTS.md「编码」节
dotnet build -c Release --nologo          # 期望 0 警告 0 错误

# 2) DLL 里的中文没坏（比看源码可靠）
$dll = '.\bin\Release\netstandard2.1\HideAndSeek.dll'
$b = [System.IO.File]::ReadAllBytes($dll)
([System.Text.Encoding]::Unicode.GetString($b)) -match '启用捉迷藏'   # True
([System.Text.Encoding]::Unicode.GetString($b)) -match '鍋'           # False

# 3) 部署（游戏需先退出）
pwsh -File deploy.ps1
```

日志里要看到（`失败` 必须为 0，补丁挂不上不会报错、只会静默失效）：

```text
[HS] 已启用功能: LobbySettingPatches ([UI_LobbyPreset], Client)
[HS][UI] 设置页签已构建：1 个页签 / 3 个设置项，当前是房主（可改）。
[HS] HideAndSeek 1.1.0 加载完成（…）：启用 N，跳过 M，失败 0。
```

进游戏：大厅选中房主 → 点「详细设置」→ 第三个页签《捉迷藏》→
三行开关可点 → 改完退出重进，值还在（这一步专门验证 `Save()` 有没有漏）→
非房主进同一页面，开关变半透明且点不动。

---

## 8. 已知限制

1. **段开关与参数开关是两回事。**
   本页签改的是各功能**参数**的值。若对应功能的段 `[Xxx].Enabled = false`，
   `PatchLoader` 会跳过该段全部补丁 —— 此时开关照样能拨、值也照样落盘，
   但要**重启并把段启用**之后才真正生效。当前没有把"段未启用"可视化到行上。
   若要补：给每一行加 `IsEditable: () => 段已启用`，但**注意 `HS_Mode` 不是
   `[PatchFeature]` 段**（它是 `Plugin.Start` 里的 `extraSections`），
   `Diagnostics` 的 `MarkLoaded` 不会为它调用，所以对 `hs_mode` 那一行不能套用同一判据。
2. **页签宽度是原版 `LayoutElement` 的固定值**（219×64），页签靠 `ContentSizeFitter` 撑宽容器。
   页签标题过长不会自动变宽，会溢出到相邻页签上；目前标题都在 3 字以内。
3. **中文字体取决于游戏当前语言**：`Util.SetFontAndMaterial` 按
   `Managers.Language + "_Normal_01"` 取字体。非中文语言时依赖 TMP 的 fallback 字体链。
4. **`Extending` 的第 3 条（滑条/下拉）尚无实现**，只留了扩展位与模板对照表；
   `LobbySettingHost` 的 `default` 分支会对未实现的形态打 Error 日志并跳过该行。
