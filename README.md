# HideAndSeek — 捉迷藏模式

《Deadly Trick》的**房主端**玩法模块。**只需房主安装**，其他玩家不需要任何 Mod。

- 独立 BepInEx 5 插件（`YumeHatsuyuki.DeadlyTrick.HideAndSeek`）
- **可单独安装**：不装 DT_Tools 也完整工作；配置文件是自己的一份，与上游互不干扰
- **不修改 DT_Tools 的任何文件**；若已安装 DT_Tools，本模块的设置会同时出现在其 Web 控制台的 **DT CONFIG** 页
- 对照游戏版本：`0.1.14b`

---

## 安装

1. 构建得到 `HideAndSeek.dll`（见文末）
2. 复制到 `Deadly Trick\BepInEx\plugins\`
3. 启动游戏，配置自动生成在 **`BepInEx\config\HideAndSeek.cfg`**（无论装没装 DT_Tools）
   - 首次从旧版本升级：会自动把 `DT_Tools.cfg` 里**属于本模块的段**搬过来，你的定制不会丢
   - 装了 DT_Tools 时，这份配置同时出现在 **DT CONFIG** 页面，两处改的是同一份值
4. 把 `[HS_Mode]` 的 `Enabled` 改为 `true`（或在 DT CONSOLE 里执行 `hs_mode on`）

> 总开关是**热**的：改完立即生效，无需重启。
> 配置改动**会自动落盘**（改完即持久，不需要额外保存）。

---

## 玩法概要

| 环节 | 行为 |
|---|---|
| 开局 | **不分配黑幕**（原版会随机指定一名 Dark）；由玩家自行去武器架跑刀转为黑方，或开启「开局发刀」 |
| 黑方视野 | **恒黑灯**（转为黑方即刻生效）+ **只能看见附近的人**（超出 900 单位的玩家在模型与小地图上同时消失） |
| 会议 | **永不进入**调查/学级裁判：尸体报告被禁用（自动调查改为极大延迟、手动报警直接丢弃） |
| 任务 | 完成任务**减少**限制时间（原版为增加） |
| 露娜 | 露娜及其能力持有者**免疫普通刀杀**；致命诡计（DT）与真实停电仍可破防 |
| 黑方强度 | 解除原版 1~2 杀上限；击杀冷却可调 |
| 胜利 | **黑方**：淘汰所有非露娜系白方（触发原版项圈自爆结算）<br>**白方**：撑到限制时间归零，或任务进度满 |
| 播报 | 开局玩法介绍、死亡通告、武器被取走匿名通告（文本可配置） |

---

## 配置段

| 段 | 内容 |
|---|---|
| `HS_Mode` | 总开关（热） |
| `NoMasterMind` | 跳过黑幕分配 |
| `CorpseReport` | 自动调查延迟（默认 1000000 秒 = 不触发）、手动报警拦截 |
| `WhiteWinOnTimeout` | 时间归零判白方胜 |
| `MissionTimePenalty` | 任务扣时开关与倍率 |
| `BlackVision` | 黑方恒黑灯 |
| `StartFlash` | 开局灯效：全场亮灭两次（白方终亮 / 黑方终暗），可分别由「有人拿刀」「进入生存阶段」触发 |
| `WeaponCooldown` | 击杀后冷却秒数（默认 10，原版 20） |
| `KillLimit` | 击杀次数上限（默认 9999 = 解除） |
| `LunaImmunity` | 露娜服务端免疫、拦截反馈音效 |
| `BlackWin` | 只剩露娜系判黑方胜 |
| `AoiCulling` | 视野裁剪：`EnterRange` 600 / `ExitRange` 900 / `MinVisibleSeconds` 3 |
| `WeaponGrant` | 自行跑刀（默认）或开局发刀 |
| `Broadcast` | 三类播报的开关与文本模板 |
| `SoloPlay` | 单人开局（测试用，默认关闭） |
| `ConsoleBridge` | 命令桥（**可选集成**：装了 DT_Tools 才接得上；关掉本段即完全断开与上游的连接） |
| `ModeWatchdog` | 关闭模式时的回滚 |

播报文本支持占位符：`{name}` 玩家名 / `{alive}` 剩余存活 / `{total}` 开局人数，用 `\n` 表示换行。

---

## 命令

需安装 DT_Tools（走其 Web 控制台 / DT CONSOLE 页面）。仅保留运行期需要反复调整的项，其余走配置。

| 命令 | 说明 |
|---|---|
| `hs` | 查看模式与各关键参数状态 |
| `hs_check` | **自检**：列出每个功能的挂载状态与触发次数，用于判断"模块到底有没有在工作" |
| `hs_mode on\|off` | 模式总开关（含回滚） |
| `hs_aoi [on\|off] [enter=600] [exit=900] [min=3]` | 视野裁剪参数 |
| `hs_cd <秒>` | 击杀后冷却 |
| `hs_killlimit <n\|unlimited>` | 击杀上限 |

---

## 已知限制

1. **视野裁剪只是"看不见"，不是"拿不到"** —— 服务端仍全量广播位置，读内存的作弊工具依然可见。这是刻意的取舍：若在服务端掐断位置下发，会连 3D 模型一起消失，黑方将无法选中目标出刀。
2. 密聊通道的副作用：播报会写进 `SecretLog` 并在主机迁移时被重放；**死亡玩家收不到弹泡**（客户端会跳过）；打开对讲机界面时播报的发送者显示为"未知"。
3. **与上游 DT_Tools 有 4 处补丁目标重叠**（改同一个方法），已用 Harmony 优先级把顺序钉死，
   结果不依赖插件加载顺序：**捉迷藏模式开启时本模块的模式规则优先，模式关闭时完全让给上游**。
   重叠点是 `GameRoom.BlackKillLimit`（↔ `BlackAttack`）、`GamePlayer.StartWeaponCooltime`（↔ `BlackAttack`）、
   `TimeManager.PushSurvivalJob`（↔ `CorpseWait`）、`Define.LOBBY_MIN_PLAYER`（↔ `LobbyMinPlayers`）。
   `SoloPlay` 与外部插件 `DTSoloPlay` 改的也是最后那个 —— 那条通道没有优先级约定，仍建议不要同时启用。
4. 游戏更新后若上游方法签名变动：`ConsoleBridge` 会自动失效（其余功能不受影响）；`Corpse.Interact`、`SurvivalTick` 等补丁点需按新版本重新核对。
5. 装了 DT_Tools 时，DT 的 `/api/config/save` 会把本模块的配置**镜像副本**一并写进 `DT_Tools.cfg`。
   那个副本不是权威 —— 以 `HideAndSeek.cfg` 为准（或在 DT CONFIG 页里改，那也会落回自有文件）。

---

## 构建

```powershell
cd HideAndSeek
dotnet build -c Release
# 产物：bin\Release\netstandard2.1\HideAndSeek.dll
```

- 目标框架 `netstandard2.1`
- **不需要 NuGet**：直接引用游戏目录 `DeadlyTrick_Data\Managed\` 与 `BepInEx\core\` 下的程序集
- **不依赖 DT_Tools 仓库**：本插件是独立仓库，单独 clone 下来只填对 `GameDir` 就能构建
- 游戏不在默认路径时：`dotnet build -c Release -p:GameDir="D:\...\Deadly Trick"`
- 需要固定到仓库内快照（可复现构建）时：`-p:GameManaged=..\libs`
- `global.json` 只约束本目录（`rollForward: latestMajor`），不影响仓库根的配置
