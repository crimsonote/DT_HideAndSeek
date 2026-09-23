# HideAndSeek 用户界面文本

> 仅含**玩家能看到**的文本：配置项说明、聊天播报、命令回执、帮助文案、提示消息。
> 已排除函数注释、日志输出、内部标识。
> 
> `{0}` `{1}` 等是运行时占位符，**不要删**。

导出时间：2026-09-23 20:52　共 114 处

## AoiCullingFeature.cs

| 行 | 代码 |
|---|---|
| 48 | `[ConfigField(750f, "进入可见范围的距离。建议不小于客户端攻击距离 224；调大能让靠近时更早被识别。",` |
| 52 | `[ConfigField(1100f, "离开可见范围的距离。须明显大于进入距离（滞回），否则边界会抖动。",` |
| 56 | `[ConfigField(3f, "最短可见秒数：进入视野后至少保持这么久不被剔除。", Min = 0f, Max = 60f)]` |
| 59 | `[ConfigField(true, "技能感知：持时停（放宽到 672）或放出小熊（圆心改为召唤物）时自动豁免裁剪，避免技能静默失效。")]` |
| 68 | `[ConfigField(448f, "小熊豁免的进入半径（以召唤物为圆心），对应其客户端探测圈。",` |
| 72 | `[ConfigField(648f, "小熊豁免的退出半径（滞回 200，采样粒度约 72.8）。",` |

## BreakCommandFeature.cs

| 行 | 代码 |
|---|---|
| 44 | `[ConfigField(true, "启用黑方密聊命令通道。关闭后以 / 开头的密聊也会被当作普通聊天。")]` |
| 231 | `Reply(player, deviceId, $"未知命令 /{name}。\n{BuildHelp(forHelp)}");` |
| 265 | `Reply(player, deviceId, "当前阶段无法使用命令。");` |
| 273 | `Reply(player, deviceId, $"/{def.Name} 本局已用完（上限 {def.MaxUses} 次）。");` |
| 291 | `Reply(player, deviceId, $"/{def.Name} 条件未满足（{def.Condition}）。");` |
| 333 | `Reply(player, deviceId, "传送失败：" + tpErr);` |
| 368 | `Reply(player, deviceId, $"命令配置有误：{a}");` |
| 376 | `Reply(player, deviceId, $"命令配置有误：{a}");` |
| 386 | `Reply(player, deviceId, $"/{def.Name} 已执行。");` |
| 391 | `Reply(player, deviceId, $"/{def.Name} 执行失败，请查看房主日志。");` |
| 423 | `Reply(player, deviceId, "用法 /cre v\|s\|t\|help");` |
| 477 | `Reply(player, deviceId, "附近没有可锁的门。");` |
| 481 | `Reply(player, deviceId, $"已锁住 {count} 扇门（半径 {radius:F0}）。");` |
| 489 | `Reply(player, deviceId, "电箱 ID 必须是数字。");` |
| 496 | `Reply(player, deviceId, "当前没有可拆的电箱。");` |
| 556 | `Reply(player, deviceId, "拆电功能当前不可用（内部方法未找到）。");` |

## BroadcastFeature.cs

| 行 | 代码 |
|---|---|
| 43 | `[ConfigField(true, "启用播报。")]` |
| 46 | `[ConfigField("捉迷藏模式", "标题（进房介绍与开局提示共用）。")]` |
| 58 | `[ConfigField("当前，游戏开始后刷新刀具", "自行拿刀模式下 {knife} 的内容。")]` |
| 61 | `[ConfigField("当前，黑方角色将会自动指定。", "自动发刀模式下 {knife} 的内容。")]` |
| 64 | `[ConfigField("在倒计时结束之前，寻找刀具开始杀戮，或者完成任务逃离杀戮~",` |
| 68 | `[ConfigField("开始杀戮、开始搜索吧~在倒计时结束之前",` |
| 72 | `[ConfigField("躲避杀手，完成任务，在倒计时结束之前。祝你好运~",` |
| 76 | `[ConfigField("{name} 已经死亡({alive}/{total})",` |
| 80 | `[ConfigField("捉迷藏开始了~", "拿刀通告（仅自行拿刀模式发出；自动指定黑方时不发）。")]` |
| 83 | `[ConfigField(true, "房主用 hs_* 命令改动玩法设置时，向全场播报这次调整（仅在生存阶段播报）。")]` |
| 85 | `[ConfigField(true, "有玩家进入房间时，单独向他播报玩法规则。")]` |
| 88 | `[ConfigField(2500, "进房介绍的延迟毫秒数（等客户端就绪）。", Min = 0f, Max = 30000f)]` |
| 91 | `[ConfigField(3200, "开局提示的延迟毫秒数（须晚于自动发刀，才能分出黑方）。", Min = 0f, Max = 30000f)]` |
| 149 | `Notice(room, "【规则调整】" + change);` |
| 169 | `Notice(__instance, "【规则调整】" + kv.Value);` |

## ConfigBinder.cs

| 行 | 代码 |
|---|---|
| 37 | `$"[ConfigField] {featureType.Name}.{field.Name} 必须是 ConfigEntry<T>，实际为 {entryType.Name}");` |

## CorpseReportFeature.cs

| 行 | 代码 |
|---|---|
| 42 | `[ConfigField(true, "禁止玩家对尸体报警（手动开会）。")]` |

## DummyFeature.cs

| 行 | 代码 |
|---|---|
| 31 | `[ConfigField(0, "进入选角阶段时自动生成的假人数量（0 = 不自动生成，改用 hs_dummy 命令）。", Min = 0f, Max = 8f)]` |
| 34 | `[ConfigField("假人", "假人名字前缀，实际名字 = 前缀 + 座位号。")]` |
| 37 | `[ConfigField("", "默认角色 ID 列表（英文逗号分隔，按假人顺序循环取用）。留空则每个假人随机。")]` |
| 40 | `[ConfigField(false, "拦截假人引发的对官方服务器的上报（战绩 / 加星 / 成就）。开启前请确认已在测试环境。")]` |

## FuseboxRevealFeature.cs

| 行 | 代码 |
|---|---|
| 35 | `[ConfigField(true, "开：可拆电箱标记同时发给黑方。关：保持原版（仅黑幕可见）。")]` |

## KillLimitFeature.cs

| 行 | 代码 |
|---|---|
| 26 | `[ConfigField(9999, "黑方持有武器后的可击杀次数上限。原版为 1~2；设为极大值即等于无限。", Min = 1f)]` |

## KillTimeBonusFeature.cs

| 行 | 代码 |
|---|---|
| 28 | `[ConfigField(30, "每次击杀给倒计时增加的秒数。0 = 关闭本效果。", Min = 0f, Max = 600f)]` |
| 31 | `[ConfigField(true, "击杀加时时向全场播报一行提示。")]` |
| 34 | `[ConfigField("击杀成功，倒计时增加 {sec} 秒",` |

## KillUpgradeFeature.cs

| 行 | 代码 |
|---|---|
| 40 | `[ConfigField(100f, "学分池总量。杀光全部白方恰好发完。", Min = 10f, Max = 10000f)]` |
| 43 | `[ConfigField(18f, "每级消耗（池子的百分比）。默认 18% → 6/7 池子约够点 5 级。",` |
| 47 | `[ConfigField(3, "每个方向的最大等级。", Min = 0f, Max = 9f)]` |
| 50 | `[ConfigField(0.5f, "【视野】每级扩大比例。0.5 = 每级 +50%（连升三级 ≈ 3.4 倍）。",` |
| 54 | `[ConfigField(0.1f, "【移速】每级增加量（直接加到 SpeedBoost.BlackSpeedMul）。",` |
| 58 | `[ConfigField(10f, "【任务门槛】每级增加的任务进度要求（加到 WhiteWinOnTimeout.MinMissionProgress）。",` |
| 62 | `[ConfigField(true, "每次升级向全场发系统告示（同时进入发信机记录）。")]` |
| 134 | `message = "未知升级方向（vision / speed / task）";` |
| 139 | `message = $"{DirName(dir)} 已达到上限 {maxLevel} 级";` |
| 146 | `message = $"学分不足（需要 {cost:F1}，现有 {_credits:F1}）";` |

## LockDoorFeature.cs

| 行 | 代码 |
|---|---|
| 37 | `[ConfigField(0.3f, "锁定半径 = 地图短边 × 此比例。0.25 ≈ 1/4 地图，0.33 ≈ 1/3。", Min = 0.05f, Max = 1f)]` |
| 40 | `[ConfigField(10, "锁定持续秒数（写入门自身的锁定总时长，由原版 TickDoor 计时解锁）。", Min = 1f, Max = 300f)]` |

## LunaImmunityFeature.cs

| 行 | 代码 |
|---|---|
| 30 | `[ConfigField(true, "拦截时给攻击者播放失败音效（原版护盾音效不在服务端音效枚举中，故用 FailedSfx）。")]` |

## MissionTimePenaltyFeature.cs

| 行 | 代码 |
|---|---|
| 33 | `[ConfigField(true, "启用任务扣时。")]` |
| 36 | `[ConfigField(1f, "额外乘数。最终限制时间变化 = (-1) × 原版加时量 × 本乘数；原版加时量已包含大厅设置的时限权重，因此本项只是在其之上再乘一个系数，不替换原始设置。", Min = 0.1f, Max = 5f)]` |

## MiyukiScanFeature.cs

| 行 | 代码 |
|---|---|
| 40 | `[ConfigField(15, "扫描间隔（秒）。", Min = 5f, Max = 300f)]` |
| 43 | `[ConfigField(1, "AOI 解封持续（秒）。到点后收回超范围玩家的可见性。", Min = 0f, Max = 10f)]` |
| 46 | `[ConfigField(3, "地图标记总持续（秒）。其中前 1 秒实时跟随，之后静止，到点消失。", Min = 1f, Max = 30f)]` |
| 49 | `[ConfigField(true, "对美幸启用该被动。")]` |

## PowerRepairFeature.cs

| 行 | 代码 |
|---|---|
| 39 | `[ConfigField(1, "已修复的电箱数达到此值即恢复供电。0 = 原版行为（必须全部修好）；1 = 修好任意一个即恢复。",` |

## RoomNameFeature.cs

| 行 | 代码 |
|---|---|
| 32 | `[ConfigField("", "最近一次设置的房间名（留空表示尚未设置过）。")]` |

## SoloPlayFeature.cs

| 行 | 代码 |
|---|---|
| 31 | `[ConfigField(1, "开始游戏所需的最少玩家数。原版正式服为 5；设为 1 即可单人开局。", Min = 1f, Max = 10f)]` |

## SpeedBoostFeature.cs

| 行 | 代码 |
|---|---|
| 30 | `[ConfigField(1.0f, "黑方移动速度倍率。1.0 = 原版；1.2 = 快 20%。", Min = 0.5f, Max = 3f)]` |

## StartFlashFeature.cs

| 行 | 代码 |
|---|---|
| 34 | `[ConfigField(true, "有人拿走武器（转为黑方）时播放 —— 这就是「捉迷藏开始」的时刻。")]` |
| 37 | `[ConfigField(false, "进入生存阶段（开局）时也播放。默认关闭：开局就闪太吵，改由拿刀触发。")]` |
| 40 | `[ConfigField(12, "闪烁次数（模拟坏灯抽搐）。", Min = 2f, Max = 60f)]` |
| 43 | `[ConfigField(25, "闪烁的最小间隔毫秒数。", Min = 10f, Max = 1000f)]` |
| 46 | `[ConfigField(160, "闪烁的最大间隔毫秒数。与最小值拉开距离才有「坏掉」的随机感。", Min = 10f, Max = 3000f)]` |
| 49 | `[ConfigField(220, "闪烁结束后到落定之间的毫秒数。", Min = 0f, Max = 3000f)]` |
| 52 | `[ConfigField(2500, "开局触发时的延迟毫秒数（等待客户端进入对局）。", Min = 0f, Max = 30000f)]` |

## TeleportCommandFeature.cs

| 行 | 代码 |
|---|---|
| 31 | `[ConfigField(3000, "预警到落地之间的毫秒数（留给目标的逃跑时间）。", Min = 0f, Max = 15000f)]` |
| 34 | `[ConfigField(false, "传送落地时播放原版的 TeleportVfx（黑洞状特效）。默认关闭 —— 观感突兀。")]` |
| 50 | `[ConfigField(true, "预警期间在落点播一个世界特效（闪光），让目标看清黑方将从哪里出现。")]` |
| 54 | `[ConfigField("黑方即将传送到标记处",` |

## TeleportGuardFeature.cs

| 行 | 代码 |
|---|---|
| 38 | `[ConfigField(true, "改写传送落点：改为 StartPosList 中离目标最近的出生点。关闭则保持原版（精确落到目标身上）。")]` |
| 41 | `[ConfigField(true, "把黑洞特效改写到实际落点（就近出生点），用于提示附近的人它要来了；关闭则保留原版的坐标泄露。")]` |

## WeaponCooldownFeature.cs

| 行 | 代码 |
|---|---|
| 33 | `[ConfigField(10, "击杀后重新可出刀的冷却秒数。原版为 20，游戏内平衡建议 5~15。", Min = 1f, Max = 600f)]` |

## WeaponGrantFeature.cs

| 行 | 代码 |
|---|---|
| 30 | `[ConfigField(false, "开启后开局直接发放武器；关闭则保持原版，由玩家自行去武器架跑刀。")]` |
| 33 | `[ConfigField(2001, "发放的武器 ID。2001 刀 / 2002 棒 / 2003 锤 / 2004 铲 / 2005 手风琴。", Min = 2000f, Max = 2999f)]` |
| 36 | `[ConfigField(2500, "发刀延迟毫秒数（等待全体客户端进入对局）。", Min = 0f, Max = 30000f)]` |
| 39 | `[ConfigField("", "发刀时最高优先级排除的玩家 ID（逗号分隔，如 \"1,3\"）。" +` |
| 112 | `[ConfigField(true, "自动发刀后让武器架不再开启 —— 刀根本不会出现，因此既看不到也点不了。")]` |

## WhiteCommandFeature.cs

| 行 | 代码 |
|---|---|
| 41 | `[ConfigField(true, "启用白方公开聊天命令通道。")]` |
| 44 | `[ConfigField(2, "每个白方每局可使用 /radar 的次数。", Min = 0f, Max = 20f)]` |
| 47 | `[ConfigField(75, "/radar 的冷却秒数（每个白方独立计算）。", Min = 0f, Max = 600f)]` |
| 50 | `[ConfigField(15, "/radar 单次持续的秒数。", Min = 1f, Max = 300f)]` |
| 53 | `[ConfigField(true, "使用 /radar 时在公开聊天发一条不署名的公告。")]` |
| 108 | `[ConfigField(true, "允许白方用 /stasis 消耗任务进度时停黑方。")]` |
| 111 | `[ConfigField(5f, "/stasis 消耗的任务进度百分比。", Min = 0f, Max = 100f)]` |
| 114 | `[ConfigField(5f, "/stasis 时停黑方的秒数。", Min = 1f, Max = 60f)]` |
| 117 | `[ConfigField(90f, "/stasis 的冷却秒数（每人独立）。", Min = 0f, Max = 600f)]` |
| 120 | `[ConfigField(true, "允许白方用 /repair 消耗任务进度立即恢复供电（仅断电时可用）。")]` |
| 123 | `[ConfigField(10f, "/repair 消耗的任务进度百分比。", Min = 0f, Max = 100f)]` |
| 231 | `Reply(player, deviceId, $"全图扫描次数已用尽（{used}/{max}）。");` |
| 245 | `Reply(player, deviceId, "扫描冷却中。");` |
| 259 | `Reply(player, deviceId, $"(实验性)全图扫描已开启({shown}/{max})。");` |
| 262 | `SendPublic(room, "瞭望已开启。");` |
| 390 | `Reply(player, deviceId, "时停冷却中。");` |
| 426 | `Reply(player, deviceId, $"已时停黑方 {StasisSeconds?.Value ?? 5} 秒。");` |
| 443 | `Reply(player, deviceId, "当前没有断电，无需修复。");` |
| 472 | `Reply(player, deviceId, $"已立即恢复供电（修复 {fixedCount} 处）。");` |

## WhiteRadarFeature.cs

| 行 | 代码 |
|---|---|
| 50 | `[ConfigField(false, "是否启用白方全图雷达。可用控制台 hs_radar on\|off，或规则引擎的 Radar 动作改写。")]` |
| 60 | `[ConfigField(30, "自动关闭的秒数（0 = 一直开启）。PureDot 模式强烈建议保持限时。", Min = 0f, Max = 600f)]` |
| 63 | `[ConfigField(false, "跳过假人。默认 false —— 测试房里往往只有假人，跳过会导致雷达看起来完全无效。")]` |

## WhiteWinFeature.cs

| 行 | 代码 |
|---|---|
| 39 | `[ConfigField(true, "限制时间归零时判白方胜利。")]` |
| 42 | `[ConfigField(0, "白方获胜所需的最低任务进度（百分比，0 = 不限制）。" +` |

