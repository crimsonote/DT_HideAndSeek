# HideAndSeek — 捉迷藏模式

《Deadly Trick》的**房主端**玩法模块。**只需房主安装**，其他玩家不需要任何 Mod。

- DT_Tools 是**可选依赖**：装了会多出控制台集成（`hs_*` 命令，走它的 Web 控制台）与 DT CONFIG 页；不装也完整工作。
- 对照游戏版本：`0.1.16b`

---

## 安装

1. 构建得到 `HideAndSeek.dll`（见文末）
2. 复制到 `Deadly Trick\BepInEx\plugins\`
3. 启动游戏，配置自动生成在 **`BepInEx\config\HideAndSeek.cfg`**。如果有复杂配置可以在这里更改。
4. 进入大厅后，在大厅设置页面的捉迷藏标签中启用捉迷藏。
5. 本项目对DT_tools项目有一些兼容，在安装了DT_tools的情况下可以在其webUI中对此mod配置进行修改。不过或许更长期的目标是让相关的设置项能直接在游戏内GUI进行调整。

---

## 玩法概要

默认情况下，不分配黑幕。黑方目标在倒计时结束前，抓到所有白方玩家。白方目标幸存到倒计时结束。

黑方和白方可以在发信站使用命令来进行一些特殊操作。具体操作列表可以使用`/help`来列出详情。



---

## 已知限制

1. **视野裁剪只是"看不见"，不是"拿不到"** —— 服务端仍全量广播位置，读内存的作弊工具依然可见。这是刻意的取舍：若在服务端掐断位置下发，会连 3D 模型一起消失，黑方将无法选中目标出刀。
2. 密聊通道的副作用：播报会写进 `SecretLog` 并在主机迁移时被重放；**死亡玩家收不到弹泡**（客户端会跳过）；打开对讲机界面时播报的发送者显示为"未知"。

---

## 构建

```powershell
cd HideAndSeek
dotnet build -c Release
# 产物：bin\Release\netstandard2.1\HideAndSeek.dll
```

- 目标框架 `netstandard2.1`
- **不需要 NuGet**：直接引用游戏目录 `DeadlyTrick_Data\Managed\` 与 `BepInEx\core\` 下的程序集
- 游戏不在默认路径时：`dotnet build -c Release -p:GameDir="D:\...\Deadly Trick"`
- 需要固定到仓库内快照（可复现构建）时：`-p:GameManaged=..\libs`
- `global.json` 只约束本目录（`rollForward: latestMajor`），不影响仓库根的配置

---

## 许可证

**GNU General Public License v3.0**，全文见 [`LICENSE`](LICENSE)。