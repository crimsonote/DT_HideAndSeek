# HideAndSeek — 实施计划

> 《Deadly Trick》捉迷藏玩法模块。独立 BepInEx 插件，**不修改 DT_Tools 任何源码**。
> 对照游戏版本：`0.1.14b`（所有行号基于反编译产物，见 §9）。

---

## 0. 定位与硬约束

| 项 | 决定 |
|---|---|
| 位置 | `D:\git\DT_Tools\HideAndSeek\`（与 `DT_Tools\` 同级） |
| 产物 | `HideAndSeek.dll` —— 独立 BepInEx 插件、独立 GUID、独立版本 |
| 上游 | **零修改**：不引用 `DT_Tools.dll`、不动它的任何文件、不提 issue |
| 集成 | 纯反射：配置 Bind 到它的 `ConfigFile`；命令挂它的 `ExecuteCommand`；失败则静默降级 |
| 作用面 | 全部功能在**房主端**；客户端不装任何东西 |
| 前缀 | 配置段 `HS_*` · 命令 `hs_*` · 显示名「捉迷藏」 |

---

## 1. 目录结构

```text
D:\git\DT_Tools\
├── DT_Tools\                    # 上游（一个字节都不改）
├── libs\                        # 游戏 DLL —— 两个项目共享引用
├── global.json                  # ⚠️ 见 §2.3
└── HideAndSeek\
    ├── HideAndSeek.csproj
    ├── global.json              # 自己的 SDK 约束
    ├── PLAN.md                  # 本文件
    ├── Plugin.cs                # [BepInPlugin] 入口
    ├── HsConfig.cs              # HS_* 配置段 + ResolveConfig() 探测复用
    ├── Core\
    │   ├── ModeRuntime.cs       # 热开关 Active + 回滚（AOI / 光照还原）
    │   ├── GameRefs.cs          # 露娜判定 / 距离 / 真停电判定 / 存活统计
    │   ├── TextService.cs       # 播报文本模板 + 占位符替换
    │   └── ConsoleBridge.cs     # 反射挂 DT_Tools 的 ExecuteCommand
    ├── Patches\                 # 一个功能 = 一个文件
    │   ├── NoMasterMindPatch.cs
    │   ├── CorpseReportPatch.cs
    │   ├── MissionTimePenaltyPatch.cs
    │   ├── BlackVisionPatch.cs
    │   ├── WeaponCooldownPatch.cs
    │   ├── LunaImmunityPatch.cs
    │   ├── BlackWinPatch.cs
    │   ├── WhiteWinOnTimeoutPatch.cs
    │   ├── AoiCullingPatch.cs
    │   ├── WeaponGrantPatch.cs        # 开局发刀 / 自行跑刀
    │   └── BroadcastPatch.cs          # 开局介绍 / 死亡通告 / 死者通告 / 拿刀匿名通告
    └── Commands\
        └── HsCommandRouter.cs
```

---

### 1.1 实际结构（阶段 1/2 完成态）

```text
HideAndSeek\
├── HideAndSeek.csproj / global.json / PLAN.md
├── Plugin.cs                       # 入口，仅组装（配置来源 + PatchLoader + 日志）
├── Core\
│   ├── Attributes\                 # PatchFeatureAttribute / ConfigFieldAttribute / FeatureSide
│   ├── Config\ConfigBinder.cs      # 扫描 [ConfigField] 自动 Bind
│   ├── Patching\PatchLoader.cs     # 扫描 [PatchFeature] → Bind + PatchAll
│   ├── ModeRuntime.cs              # HS_Mode 总开关（运行期热开关）
│   └── DtBridge.cs                 # 反射复用 DT_Tools 的 ConfigFile
└── Features\                       # 按领域划分，命名空间与目录一致
    └── Rule\
        ├── NoMasterMindFeature.cs  # #6
        ├── CorpseReportFeature.cs  # #3
        └── WhiteWinFeature.cs      # #5b
```

后续阶段新增：`Features\Vision\`（黑灯 / AOI）、`Features\Combat\`（刀 CD / 露娜免疫）、`Features\Broadcast\`（播报 + TextService）、`Features\Weapon\`（开局发刀）、`Console\Commands\`（DT_Tools 命令桥）。

## 2. 工程配置

### 2.1 csproj（沿用 DT_Tools 的引用写法）

```xml
<TargetFramework>netstandard2.1</TargetFramework>
<AssemblyName>HideAndSeek</AssemblyName>
<GameManaged Condition="'$(GameManaged)'==''">$(MSBuildThisFileDirectory)..\libs</GameManaged>
```

包引用：`BepInEx.Core 5.*`、`HarmonyX 2.*`、`BepInEx.PluginInfoProps 1.*`

游戏 DLL 引用（全部 `<Private>false</Private>`）：

| 程序集 | 用途 |
|---|---|
| `Assembly-CSharp.dll` | `Server.Game.*` / `GameRoom` / `Protocol` / `Define` / Harmony 目标 |
| `UnityEngine.CoreModule.dll` | `Vector2` / `Mathf` |
| `Google.Protobuf.dll` | 构造 `S_CHAT_MESSAGE` / `S_AREA_PUBLIC` / `S_MOVE` |
| `UnityEngine.dll` | 视编译报错再补 |

比 DT_Tools 少 4 个引用（不需要 UI / TextMeshPro / Steamworks / Physics2D）。

### 2.2 明确不做

- 不引用 `DT_Tools.dll`（避免加载顺序耦合，且保证"只装 HideAndSeek"也能跑）
- 不复制 `libs/`
- 不在 DT_Tools 里新增 `[PatchFeature]`，不改其 `Plugin` / 注册表 / WebUI 路由

### 2.3 ⚠️ 必须先解决的构建问题

根 `global.json` 为 `rollForward: disable` + `7.0.410`，本机只有 **6.0.420 / 8.0.303**
→ 在仓库根执行 `dotnet build` 会直接失败（DT_Tools 自身也构建不了）。

**解决**：在 `HideAndSeek\` 放自己的 `global.json`（`"rollForward": "latestMajor"`），构建时 **`cd HideAndSeek`** 再执行。不影响上游。

---

## 3. 功能清单

### 3.1 胜负与规则

| # | 功能 | 侧 | 落点 |
|:--:|---|---|---|
| 0 | 解除黑方 1~2 杀硬上限 | Host | `GameRoom.BlackKillLimit` :169552 → 按配置放开（原版：人数 <6 限 1 杀，否则 2 杀） |
| 1 | 完成任务**减少**限制时间 | Host | `MissionManager.ClearMission` :166690，`num2` 取负后 `UpdateRemainTime` :166728；同步 `S_MISSION_CLEAR.AddTime` :166733 |
| 4 | 露娜服务端免疫（例外：DT / **真停电**） | Host | `UseWeapon` :176263 插入 `IsLunaProtected && !IsRealBlackout` 分支（复用 `LogKillReject` :176303 路径）；**DT 路径 :176314 不动**（原版天然不检查护盾）；拒绝时 `SendSystemSFX(FailedSfx, attacker)` |
| 5 | 只剩露娜系 → 黑方胜 | Host | Postfix `Server.Game.Player.OnDead` :175968 判定 → 调原版 `GameRoom.GameOver()` :171389（自带项圈自爆演出 + 7.5s 结算）；需修正 `PrimaryWinnerId`（黑幕为 null 时） |
| 5b | 限制时间归零 → **白方胜** | Host | `GameRoom.SurvivalTick` :171256 分支改写 → 走 `MissionManager.ClearAllMission()` :166889 |
| 6 | 开局不分配黑幕 | Host | Prefix `GameRoom.StartPick` :171011，跳过 :171029-031 的 `Color = Dark` + `SetMasterMind` |

**第 4 条的关键设计**：`IsRealBlackout(attacker) => attacker.CurrentArea != null && !attacker.CurrentArea.IsLight`
—— 用**服务端真实光照**判定，从而与第 2a 条"给黑方看的假黑灯"解耦：

| 情形 | 服务端 `Area.IsLight` | 黑方客户端 `Darkness` | 结果 |
|---|:--:|:--:|---|
| 假黑灯（2a 造的） | `true` | `true` | **拒绝** → 露娜安全 |
| 真拉闸（≥2 电箱） | `false` | `true` | **放行** → 露娜可杀 |
| DT（致命诡计） | — | — | **放行**（原版路径不检查） |

### 3.2 视野与手感

| # | 功能 | 侧 | 落点 |
|:--:|---|---|---|
| 2a | 黑方恒黑灯视野（变黑**即刻**生效） | Host | 发假 `S_AREA_PUBLIC{IsLight=false}`：① Postfix `set_Color` :175354（变黑瞬间）② Postfix `Area.SendAreaInfo` :173361（进新区域）③ Postfix `Area.set_IsLight` :173326（复电/停电后不被洗掉） |
| 2b | 缩短刀 CD | Host | Prefix `Server.Game.Player.StartWeaponCooltime` :176153，**只改 `seconds == 20`**（击杀后重装，常量 `BLACK_REARM_DELAY_SECOND` :93424）；保留拔刀 5 秒锁（`INIT_KILL_DELAY_SECOND` :93418） |
| 8 | 黑方 AOI 视野裁剪 | Host | ① Prefix `Server.Game.Player.AddPlayer` :175822 按距离拒绝 ② 主动 `other.RemovePlayer(black)` :175841 剔除 ③ 滞回（进 600 / 出 900）+ 最短可见时间 ④ 剔除前先发假位置 `S_MOVE`（防 HUD 残留 pin） |

**参数依据**：移动同步 `C_MOVE` 固定 10 Hz + 转向即发（`UpdateMovePacket` :14865）；跑速 ≈ 728/s（`INIT_SPEED` 560 × `RUN_SPEED_DELTA` 1.3）；位置采样粒度 72.8 单位/包 → 滞回必须 > 150。

### 3.3 回合流程改造

| # | 功能 | 侧 | 落点 |
|:--:|---|---|---|
| 3 | 禁报告（三条路径分别处理） | Host | ① **自动报告**：Transpiler `Corpse` ctor :168792，把 `Util.GetRandomNumber(50, 71)` 两常量改成配置值（默认 `1000000`）→ 同时作用于 `WaitDetectiveSecond`、`StateList[5]`、`PushSurvivalJob` 三处 ② **手动报告**：Prefix `Corpse.Interact` :168866 → `return false` ③ **时间归零自动进调查**：由 5b 覆盖 |

保留 `Corpse.EndSurvival` :169030 代码路径完整（不用 Prefix 硬拦），将来改回配置值即可恢复原版行为。

### 3.4 武器供给

| # | 功能 | 侧 | 落点 |
|:--:|---|---|---|
| 9 | 开局发刀 / 自行跑刀（设置项） | Host | Postfix `GameRoom.StartSurvive` :171277，按配置 `WeaponGrant = SelfFetch \| GiveOne` 调用 `ItemManager.CreateAndInsertInven(player, weaponId)`（参照 `FishingPondFeature` :123 的用法）；**原版机制**：`ItemManager.InsertWeapon` :172688 会给该玩家 `Color = Black` + `RemainKill = BlackKillLimit` :172701 |

### 3.5 播报系统（文本全部可配置）

统一走 `S_CHAT_MESSAGE{Type = EChatType.SecretChat}`（客户端 `UI_SecretChatOverlay` :82596 弹泡，**不依赖客户端装 mod**；房主自造包可发给任意阵营，但死者因客户端限制收不到）。
构造要求：`DeviceId` 用自定义魔数避开真实 `ChatDevice` id；`Time` 必须填 `TimeManager.Instance.SurviveTime`，否则客户端按过期丢弃（:82645）。

| # | 播报 | 触发点 | 收件人 |
|:--:|---|---|---|
| 7a | **开局介绍**（玩法说明 + 黑白方胜利条件） | Postfix `GameRoom.StartSurvive` :171277 | 全员 |
| 7b | **死亡通告**（某人已淘汰 + 剩余人数） | Postfix `Server.Game.Player.OnDead` :175968 | 全员（`GameRoom.Broadcast`；死者那份由客户端丢弃） |
| 10 | **拿刀匿名通告**（"有人拿起了武器……"，不含名字） | Postfix `ItemManager.InsertWeapon` :172688 | 全员 |

**文本模板**（`[HS_Text]` 段，支持 `{name}` / `{alive}` / `{total}` 占位符）：

```ini
[HS_Text]
Enabled        = true
IntroTitle     = 捉迷藏模式
IntroBody      = 黑方目标：在时间耗尽前找出并淘汰所有白方。\n白方目标：撑到限制时间归零，或完成全部任务。\n注意：黑方视野受限，只能看见附近的人。
DeathAnnounce  = {name} 已被淘汰（剩余 {alive}/{total}）
WeaponTaken    = 有人拿起了武器……
```

**密聊通道的已知限制（已确认接受）**：

- **死亡玩家收不到弹泡**（`UI_SecretChatOverlay` :82641 的 `!IsAlive` 会 skip）→ 因此 7b 直接用 `GameRoom.Broadcast`（全员）即可，死者那一份由客户端自行丢弃，服务端无需区分
- 播报会写进 `SecretLog` / `SecretChatMirror`（主机迁移时 `ReplaySecretChatTo` :171972 会重放这些播报）
- 玩家打开对讲机界面时，`UI_ChatDevicePopup.AddRow` :46801 会用 `DeviceId` 反查头像/名字 → 我们用魔数，该处会显示为未知（可接受）
- `Time` 必须填 `TimeManager.Instance.SurviveTime`，否则客户端按过期丢弃（:82645）
- 原版 `set_Color` :175381-175387 会向**所有已死亡玩家**发 `S_NOTIFY_BLACK`（死者知道谁变黑），而**活着的白方不知道** → "跑刀"隐蔽性天然成立。若连死者也要瞒，可选拦截 `HideBlackIdentityFromDead`

### 3.6 DT_Tools 集成（零改动上游，最脆，放最后）

| 集成 | 方式 | 失败影响 |
|---|---|---|
| **配置复用**（稳） | 反射 `DT_Tools.Plugin.Instance` → `BaseUnityPlugin.Config`（公开属性）→ 把 `HS_*` 段 Bind 到**同一个 `ConfigFile`** | 无（回退自己的 `HideAndSeek.cfg`） |
| **命令桥**（脆） | Prefix `DT_Tools.Console.WebConsole.ExecuteCommand` :318（private，可 patch），截胡 `hs_` 前缀，写回 `PendingRequest.ResultJson` + `Done.Set()` | 无（命令不可用，玩法照常） |

**配置复用的依据**：`ConfigService.List` :41 遍历的是 `config.Keys`（不区分来源），`/api/config/*` 全按 `section + key` 操作 → 只要 Bind 到同一个 `ConfigFile`，DT CONFIG 页面就会自动列出并允许修改 `HS_*` 段。

**命令桥的探测**：`[HarmonyPrepare]` 里 `AccessTools.TypeByName("DT_Tools.Console.WebConsole")` + `AccessTools.Method(t, "ExecuteCommand")`，任一为 null 就整体跳过（防止上游改签名后崩加载）。

---

## 4. 阶段划分

| 阶段 | 内容 | 出口条件 |
|:--:|---|---|
| **1** | ✅ 已完成 — 框架与骨架（`Core/`：Attribute + ConfigBinder + PatchLoader + 模式总开关 + DT 桥） | 编译通过 |
| **2** | ✅ 已完成（待进游戏验证）— #6 不分配黑幕 + #3 禁报告 + #5b 时间归零改判 | 无黑幕开局；杀人不进会议；时间归零正常结束 |
| **3** | ✅ 已完成 — 视野与手感：#2a 黑灯 + #2b 刀 CD + #0 击杀上限 | 变黑瞬间黑灯、复电不洗掉、CD 可调、可连续击杀 |
| **4** | ✅ 已完成 — 胜负与露娜：#4 服务端免疫 + #5 黑方胜（项圈自爆） | 假黑灯砍不动露娜 / 真停电与 DT 可杀；只剩露娜时黑方胜 |
| **5** | ✅ 已完成 — 武器供给：#9 开局发刀 / 自行跑刀 | 两种模式都能进入正确的黑方状态 |
| **6** | ✅ 已完成 — AOI 裁剪：#8 | 600 外不可见（人 + pin）；近处正常出刀；边界不抖 |
| **7** | ✅ 已完成 — 播报：#7a/b + #10 | 三类播报按配置送达；文本可改 |
| **8** | ✅ 已完成 — DT_Tools 集成：配置复用 + 命令桥 | DT CONFIG 出现 `HS_*`；DT CONSOLE 里 `hs_*` 可用 |
| **9** | ✅ 已完成 — 热开关与回滚 | 关闭模式能还原 AOI 与光照，不残留副作用 |

> **补漏说明**：#1（完成任务减少限制时间）在原阶段划分中遗漏，已于收尾阶段补做 ——
> `Features/Rule/MissionTimePenaltyFeature.cs`（不重写原方法体，而是捕获 `ClearMission`
> 传给 `UpdateRemainTime` 的增量，返回后补一次 `-(1+倍率)×增量`，净效果为扣减等量时间）。
> 另外额外实现了 `Features/Dev/SoloPlayFeature.cs`（单人开局，测试用，默认关闭）。

**阶段 2 是"玩法是否成立"的判据，必须先单独验证再往上叠。**

---

## 5. 命令与配置

### 5.1 命令（只保留运行期需要动态调的）

| 命令 | 说明 |
|---|---|
| `/hs` | 总览：所有功能状态 + 关键参数（文本 + `SetResult` JSON） |
| `/hs_mode on\|off` | 模式热开关（含回滚） |
| `/hs_aoi [on\|off\|enter=\|exit=\|min=]` | 视野裁剪参数（手感，需边打边调） |
| `/hs_cd <秒>` | 刀冷却 |
| `/hs_killlimit <n\|unlimited>` | 击杀上限 |

纯玩法开关（愿景/播报/露娜/胜负条件等）**不做命令**，全部走 DT CONFIG 页面。

### 5.2 配置段

| 段 | 内容 |
|---|---|
| `HS_Mode` | 总开关、热键、是否启用各子功能 |
| `HS_Text` | 全部播报文本模板（§3.5） |
| `HS_AOI` | `EnterRange` / `ExitRange` / `MinVisibleTime` |
| `HS_Combat` | 刀 CD、击杀上限、露娜免疫开关、露娜反馈音效 |
| `HS_Rule` | 任务时间增减、时间归零胜方、自动调查延迟（默认 1000000） |
| `HS_Weapon` | `WeaponGrant` 模式与数量 |
| `HS_Broadcast` | 各播报的独立开关（7a 开局介绍 / 7b 死亡通告 / 10 拿刀通告） |

---

## 6. 验收标准

| 阶段 | 通过条件 |
|:--:|---|
| 1 | 启动无报错；`HideAndSeek.cfg`（或 `DT_Tools.cfg`）出现 `HS_*`；DT CONFIG 可见 |
| 2 | 无黑幕开局；杀人不进会议；手动摸尸不进会议；时间归零正常结算；主机迁移不崩 |
| 3 | 变黑瞬间进入黑灯；复电/换房间不洗掉；刀 CD 符合配置；可连续击杀不再"刀废掉" |
| 4 | 假黑灯下砍不动露娜并听到失败音效；真停电或 DT 可杀；只剩露娜时黑方胜且触发项圈自爆 |
| 5 | 自行跑刀与开局发刀两种模式都能正确产生黑方 |
| 6 | 黑方看不见 600 外的人与 pin；近处可正常出刀；边界无抖动 |
| 7 | 开局介绍、死亡通告、拿刀匿名通告按配置送达；文本改动立即生效 |
| 8 | DT CONFIG 出现 `HS_*` 可改；DT CONSOLE 里 `hs_*` 命令可用 |
| 9 | 关掉模式后无残留（玩家可见性恢复、光照恢复） |

---

## 7. 待确认 / 待验证

**已确认的设计决定**：

- 死亡通告（7b）→ 全员广播；死者那份由客户端丢弃，服务端不做区分
- **不做**"死者本人通告"（原 7c，已取消）
- 拿刀通告（10）走通用密聊通道，接受其全部限制
- 开局发刀只发 **1 把**；默认 `SelfFetch`（原版跑刀），可切换为 `GiveOne`
5. **HUD 小地图残留 pin** —— `S_DESPAWN` 只清平板 pin（`PlayerManager.Despawn` :31312 → `Managers.Tablet.DeletePin`），`UI_GameScene` 的 HUD pin 是否残留需实测；已备"先发假位置再剔除"的兜底
6. **主机迁移后 `SharedPlayers` 是否恢复** —— 影响 AOI 裁剪是否需要在迁移后重建
7. **黑方丢刀后 `Color` 是否回退** —— 用户已确认"黑方不能丢刀"，实际可省
8. **`PrimaryWinnerId` 的取值** —— 不分配黑幕后 `MasterMind` 为 null，需显式指定（建议最后挥刀的黑方）

---

## 7.5 工作节奏约定

后台暂停提醒 job 返回后，若 goal 仍要求继续行动，**必须运行前台阻塞进程**睡到恢复时间，而不是继续工作：

| 提醒时刻 | 阻塞至 |
|---|---|
| 8:50 | 12:00 |
| 13:50 | 18:00 |

实现：前台 pwsh 循环 `while ((Get-Date) -lt $resume) { Start-Sleep -Seconds 60 }`；
若被执行器的超时上限截断（进程被 kill），则**重新发起同一个阻塞**，直到越过恢复时间为止。

## 8. 反编译参考

| 资源 | 位置 |
|---|---|
| 游戏反编译源码 | `.tmps\decomp\Assembly-CSharp.decompiled.cs`（180168 行） |
| ILSpy 工具链 | `.tmps\tools\ilspycmd-9.1.0.7988\` |
| 重建脚本 | `.tmps\fetch_ilspy.py`（`python fetch_ilspy.py <版本>`） |

`.tmps` 为临时目录；若需长期保留，建议迁至 `HideAndSeek\_ref\` 并加入 `.gitignore`。

---

## 9. 实现进度与偏差记录

### 已完成
阶段 1 + 2：框架与三个规则功能落地。`dotnet build -c Release` 通过（0 警告 0 错误），产物
`HideAndSeek\bin\Release\netstandard2.1\HideAndSeek.dll`。

### 与原计划的偏差（均已按工作区 AGENTS.md 调整）

| 项 | 原计划 | 实际做法 | 原因 |
|---|---|---|---|
| 目录组织 | `Patches/` 一功能一文件 | `Core/` + `Features/<Domain>/`，命名空间与目录一致 | AGENTS.md 规范 |
| 配置声明 | 手写 `Config.Bind` | `[PatchFeature]` + `[ConfigField]` 自动 Bind | AGENTS.md 明确禁止功能里手写 Bind |
| 依赖方式 | NuGet（BepInEx.Core / HarmonyX） | 直接引用游戏目录 `BepInEx\core\` 的 `BepInEx.dll` / `0Harmony.dll` | 沙箱内访问不了 nuget.org，且本机从未还原过这些包 |
| 加载时机 | `Awake` | `Start` | 需等 DT_Tools 的 `Awake` 执行完，才能探测到其 `Plugin.Instance` 并复用配置 |
| 功能署名 | `author: "梦初雪"` | **已全部移除，待定** | 不应署他人之名 |

### 编译期踩到的坑（供后续阶段参考）

- `Corpse` 上另有不可访问的 `Interact(int)` 重载 → `nameof(Corpse.Interact)` 触发 CS0122，须改用字符串 + 参数类型定位
- `AccessTools.PropertyGetter` 返回的是 getter 的 **`MethodInfo`**，不是 `PropertyInfo`
- `Corpse` 的公开属性是 `Info`（继承自 `DeviceBase`），反编译里的 `DeviceInfo` 是基类字段，外部不可见
- 全局命名空间另有客户端 `Player`，与 `Server.Game.Player` 冲突 → 统一用 `using GamePlayer = Server.Game.Player;`

### 待办
- 进游戏验证阶段 2 的三条行为
- 确定 `[PatchFeature]` 的署名
- 部署方式：`bin\Release\netstandard2.1\HideAndSeek.dll` 需手动复制到 `BepInEx\plugins\`

## 10. 版本对照

- 游戏：`0.1.14b`
- 所有行号与行为结论均基于该版本反编译产物
- 游戏更新后需重新核对：`Corpse` ctor / `StartPick` / `SurvivalTick` / `UseWeapon` / `StartWeaponCooltime` / `AddPlayer` / `InsertWeapon` / `set_Color`
