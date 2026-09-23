# HideAndSeek 提示文本清单

> 自动导出，供人工全面修改。改完告诉我，我按此回填代码。
> 
> 注意：带 `{0}` `{1}` 的是占位符（运行时替换），不要删。

导出时间：2026-09-23 20:51　共 452 处

## AoiCullingFeature.cs

| 行 | 代码 |
|---|---|
| 43 | `description: "黑方视野裁剪：黑方只能看到附近的玩家（模型与小地图 pin 同时消失）。对时停、小熊等依赖真实目标信息的技能自动豁免。",` |
| 48 | `[ConfigField(750f, "进入可见范围的距离。建议不小于客户端攻击距离 224；调大能让靠近时更早被识别。",` |
| 52 | `[ConfigField(1100f, "离开可见范围的距离。须明显大于进入距离（滞回），否则边界会抖动。",` |
| 56 | `[ConfigField(3f, "最短可见秒数：进入视野后至少保持这么久不被剔除。", Min = 0f, Max = 60f)]` |
| 59 | `[ConfigField(true, "技能感知：持时停（放宽到 672）或放出小熊（圆心改为召唤物）时自动豁免裁剪，避免技能静默失效。")]` |
| 63 | `"小熊豁免：小熊的『视野切换』与『附近探测』都由客户端遍历本地玩家完成，" +` |
| 64 | `"不豁免则两者在 AOI 下一起失效。代价是黑方小地图会为召唤物附近（半径见下）的人生成 pin —— " +` |
| 65 | `"原版该技能只给 HUD 提示、不给位置。")]` |
| 68 | `[ConfigField(448f, "小熊豁免的进入半径（以召唤物为圆心），对应其客户端探测圈。",` |
| 72 | `[ConfigField(648f, "小熊豁免的退出半径（滞回 200，采样粒度约 72.8）。",` |
| 124 | `Plugin.Log.LogWarning($"[HS] AoiCulling：无法读取 SkillComponent._summonId，小熊豁免将不生效（{ex.Message}）。");` |
| 338 | `$"[HS] AoiCulling：黑方 #{black.PublicInfo.PlayerId} 剔除 #{other.PublicInfo.PlayerId}" +` |
| 339 | `$"（距离 {Math.Sqrt(dSq):F0} > 阈值 {exit:F0}，圆心 {cx:F0},{cy:F0}）");` |

## BlackVisionFeature.cs

| 行 | 代码 |
|---|---|
| 34 | `description: "黑方恒黑灯视野：转为黑方后在生存阶段持续保持黑灯（等同真停电的表现）；离开生存阶段不再干预，交还原版收尾。",` |
| 81 | `Plugin.Log.LogInfo($"[HS] BlackVision：玩家 #{__instance.PublicInfo.PlayerId} 转为黑方，已下发黑灯。");` |

## BlackWinFeature.cs

| 行 | 代码 |
|---|---|
| 25 | `description: "非露娜系白方全部淘汰时判黑方胜利（复用原版 GameOver：幸存者项圈自爆 + 7.5 秒结算）。",` |
| 46 | `Plugin.Log.LogInfo("[HS] BlackWin：非露娜系白方已全部淘汰 → 判黑方胜利。");` |

## BreakCommandFeature.cs

| 行 | 代码 |
|---|---|
| 39 | `description: "黑方密聊命令通道：命令在配置中注册（含条件/CD/次数/效果），不满足即拒绝。",` |
| 44 | `[ConfigField(true, "启用黑方密聊命令通道。关闭后以 / 开头的密聊也会被当作普通聊天。")]` |
| 50 | `"命令注册表。每条一行，格式：\n" +` |
| 51 | `"    <命令名> = <条件> -> <效果> ; cd=<秒> ; uses=<每局次数>\n" +` |
| 52 | `"条件可留空（= 无条件）；可用 fusebox（地图上有可拆电箱）\n" +` |
| 53 | `"      time<=N（剩余秒）kills>=N（黑方击杀）alive<=N（白方存活），多个用 & 连接。\n" +` |
| 54 | `"效果：配置键=值（键同规则引擎：SpeedMul/EnterRange/ExitRange/Cooldown/KillLimit/RepairCount/MinProgress），\n" +` |
| 55 | `"      或特殊动作：Disconnect（拆最近可拆电箱）/ Lock（锁住附近的门）/\n" +` |
| 56 | `"      Teleport（预警数秒后传送到目标位置，可跟玩家 ID 参数）。多个动作用 , 连接。\n" +` |
| 57 | `"cd / uses 可省略，0 或省略 = 不限。行首 # 为注释。\n" +` |
| 58 | `"默认的 fusebox 条件已隐含原版派发节奏（断电归零后 60 秒才重新派发目标），通常不必再设 cd。")]` |
| 126 | `$"[HS] 密聊命令：非黑方 #{player.PublicInfo?.PlayerId} 试图使用 /{name}，已忽略。");` |
| 140 | `Plugin.Log.LogWarning($"[HS] 密聊命令：解析失败 — {ex.Message}");` |
| 168 | `Plugin.Log.LogInfo($"[HS] Debug：以玩家 #{player.PublicInfo?.PlayerId} 身份执行 /{name} {arg}");` |
| 231 | `Reply(player, deviceId, $"未知命令 /{name}。\n{BuildHelp(forHelp)}");` |
| 265 | `Reply(player, deviceId, "当前阶段无法使用命令。");` |
| 273 | `Reply(player, deviceId, $"/{def.Name} 本局已用完（上限 {def.MaxUses} 次）。");` |
| 283 | `$"/{def.Name} 冷却中，还需 {(int)(def.Cooldown - (now - last)) + 1} 秒。");` |
| 291 | `Reply(player, deviceId, $"/{def.Name} 条件未满足（{def.Condition}）。");` |
| 333 | `Reply(player, deviceId, "传送失败：" + tpErr);` |
| 368 | `Reply(player, deviceId, $"命令配置有误：{a}");` |
| 376 | `Reply(player, deviceId, $"命令配置有误：{a}");` |
| 379 | `Plugin.Log.LogInfo($"[HS] 密聊命令 /{def.Name}：{key} → {value}");` |
| 386 | `Reply(player, deviceId, $"/{def.Name} 已执行。");` |
| 390 | `Plugin.Log.LogWarning($"[HS] 密聊命令 /{def.Name} 执行失败 — {ex.Message}");` |
| 391 | `Reply(player, deviceId, $"/{def.Name} 执行失败，请查看房主日志。");` |
| 416 | `case "v": case "vision": case "视野":` |
| 418 | `case "s": case "speed": case "移速":` |
| 420 | `case "t": case "task": case "任务":` |
| 423 | `Reply(player, deviceId, "用法 /cre v\|s\|t\|help");` |
| 437 | `return "当前没有玩家";` |
| 450 | `if (p.IsDummy) one.Append("[假人]");` |
| 451 | `if (p.IsSpectator) one.Append("[观战]");` |
| 452 | `else if (!p.IsAlive) one.Append("[死亡]");` |
| 459 | `var sb = new global::System.Text.StringBuilder("玩家列表（/tp 可用 ID）：");` |
| 477 | `Reply(player, deviceId, "附近没有可锁的门。");` |
| 481 | `Reply(player, deviceId, $"已锁住 {count} 扇门（半径 {radius:F0}）。");` |
| 489 | `Reply(player, deviceId, "电箱 ID 必须是数字。");` |
| 496 | `Reply(player, deviceId, "当前没有可拆的电箱。");` |
| 544 | `? $"没有可拆的电箱 #{wantId}（可能未派发、已断电或不在本局目标内）。"` |
| 545 | `: "当前没有可拆的电箱（尚未派发或已全部断电）。");` |
| 555 | `Plugin.Log.LogWarning("[HS] 密聊命令：找不到 Fusebox.DisconnetCable。");` |
| 556 | `Reply(player, deviceId, "拆电功能当前不可用（内部方法未找到）。");` |
| 561 | `Plugin.Log.LogInfo($"[HS] 密聊命令：黑方 #{player.PublicInfo?.PlayerId} 拆除了电箱 #{target.ID}。");` |
| 658 | `case "brk": return "拆电断电（默认随机两个电箱） CD{cd}";` |
| 659 | `case "lck": return "锁住附近的门 CD{cd}";` |
| 660 | `case "tp":  return "3 秒后传送到目标处 [玩家ID] CD{cd}";` |
| 661 | `case "ls":  return "列出玩家 ID 与昵称";` |
| 662 | `case "cre": return "查看/消耗积分升级 v\|s\|t\|help";` |
| 669 | `var sb = new global::System.Text.StringBuilder("【黑方】");` |
| 686 | `sb.Append(" 条件:").Append(d.Condition);` |
| 774 | `Plugin.Log.LogWarning($"[HS] 密聊命令：回执失败 — {ex.Message}");` |

## BroadcastFeature.cs

| 行 | 代码 |
|---|---|
| 35 | `description: "播报：进房规则介绍、开局身份提示、死亡通告、拿刀通告。支持 {name} {alive} {total} {knife} {sec} 占位符。",` |
| 43 | `[ConfigField(true, "启用播报。")]` |
| 46 | `[ConfigField("捉迷藏模式", "标题（进房介绍与开局提示共用）。")]` |
| 50 | `"此房间已启用捉迷藏，具有特殊胜负条件\\n" +` |
| 51 | `"黑方：在倒计时结束前杀死所有可以杀死的人，视野缩小，刀CD缩短\\n" +` |
| 52 | `"白方：在倒计时结束前，避免死亡。通过完成任务可以缩短倒计时，倒计时结束后白方胜利。\\n" +` |
| 54 | `"报告功能被禁用，不分配黑幕角色，部分角色的技能效果会有改变。",` |
| 55 | `"进房介绍正文。{knife} 按发刀模式替换为下面两行之一。用 \\n 表示换行。")]` |
| 58 | `[ConfigField("当前，游戏开始后刷新刀具", "自行拿刀模式下 {knife} 的内容。")]` |
| 61 | `[ConfigField("当前，黑方角色将会自动指定。", "自动发刀模式下 {knife} 的内容。")]` |
| 64 | `[ConfigField("在倒计时结束之前，寻找刀具开始杀戮，或者完成任务逃离杀戮~",` |
| 65 | `"开局提示：自行拿刀模式（所有人同一句）。")]` |
| 68 | `[ConfigField("开始杀戮、开始搜索吧~在倒计时结束之前",` |
| 69 | `"开局提示：自动发刀模式下发给黑方。")]` |
| 72 | `[ConfigField("躲避杀手，完成任务，在倒计时结束之前。祝你好运~",` |
| 73 | `"开局提示：自动发刀模式下发给白方。")]` |
| 76 | `[ConfigField("{name} 已经死亡({alive}/{total})",` |
| 77 | `"死亡通告。占位符：{name} 死者名 / {alive} 剩余存活白方数（含露娜）/ {total} 开局白方总数。")]` |
| 80 | `[ConfigField("捉迷藏开始了~", "拿刀通告（仅自行拿刀模式发出；自动指定黑方时不发）。")]` |
| 83 | `[ConfigField(true, "房主用 hs_* 命令改动玩法设置时，向全场播报这次调整（仅在生存阶段播报）。")]` |
| 85 | `[ConfigField(true, "有玩家进入房间时，单独向他播报玩法规则。")]` |
| 88 | `[ConfigField(2500, "进房介绍的延迟毫秒数（等客户端就绪）。", Min = 0f, Max = 30000f)]` |
| 91 | `[ConfigField(3200, "开局提示的延迟毫秒数（须晚于自动发刀，才能分出黑方）。", Min = 0f, Max = 30000f)]` |
| 138 | `Notice(room, "【规则调整】" + change);` |
| 158 | `Notice(__instance, "【规则调整】" + kv.Value);` |
| 268 | `string name = __instance?.Name ?? "某人";` |

## ConfigBinder.cs

| 行 | 代码 |
|---|---|
| 37 | `$"[ConfigField] {featureType.Name}.{field.Name} 必须是 ConfigEntry<T>，实际为 {entryType.Name}");` |
| 85 | `$"无法将默认值 {value} ({value.GetType().Name}) 转为 {targetType.Name}", ex);` |

## ConfigMigration.cs

| 行 | 代码 |
|---|---|
| 32 | `"内部：配置迁移版本，请勿手动修改。");` |
| 38 | `log.LogInfo($"[HS] 配置迁移：v{from} → v{CurrentVersion}");` |
| 63 | `log.LogInfo($"[HS] 配置迁移完成（当前 v{CurrentVersion}）。");` |
| 81 | `log.LogInfo($"[HS]   修正 {section}.{key}：{!expected} → {expected}");` |
| 85 | `log.LogWarning($"[HS]   迁移 {section}.{key} 失败：{ex.Message}");` |
| 100 | `log.LogInfo($"[HS]   补齐 {section}.{key} = {expected}");` |
| 104 | `log.LogWarning($"[HS]   迁移 {section}.{key} 失败：{ex.Message}");` |

## ConsoleBridge.cs

| 行 | 代码 |
|---|---|
| 26 | `description: "把 hs_* 命令接入 DT_Tools 的 Web 控制台（零改动上游；未安装 DT_Tools 时自动失效）：既可执行，也显示在命令列表与补全中。",` |
| 36 | `("hs",           "hs",                                                "捉迷藏模式：总览当前状态"),` |
| 37 | `("hs_check",     "hs_check",                                          "自检：各功能的挂载状态与触发次数"),` |
| 38 | `("hs_mode",      "hs_mode <on\|off>",                                  "捉迷藏模式总开关（含可见性与光照回滚）"),` |
| 39 | `("hs_aoi",       "hs_aoi [on\|off] [enter=750] [exit=1100] [min=3]",   "黑方视野裁剪参数"),` |
| 40 | `("hs_cd",        "hs_cd <秒>",                                        "黑方击杀后的冷却秒数"),` |
| 41 | `("hs_killlimit", "hs_killlimit <n\|unlimited>",                        "黑方击杀次数上限"),` |
| 42 | `("hs_dummy",     "hs_dummy <add [座位号] [角色ID]\|del <座位号>\|list\|clear>", "假人玩家（测试用）：生成 / 移除 / 查看靶子"),` |
| 43 | `("hs_flash",     "hs_flash <on\|off>",                                 "开局灯效：开关闪烁（关闭后直接进入白亮黑灭的定态）"),` |
| 44 | `("hs_roomname",  "hs_roomname [新名字]",                              "修改房间在 Steam 列表里显示的名字（仅房主可改）"),` |
| 45 | `("hs_tp",        "hs_tp <玩家ID> <x> <y> \| <玩家ID> to <目标ID>",      "调试用传送：把玩家（含假人）挪到坐标或另一名玩家身边"),` |
| 46 | `("hs_grant",     "hs_grant <on\|off>",                                  "发刀模式：on=开局随机发刀并锁死武器架，off=自行跑刀（下一局生效）"),` |
| 47 | `("hs_radar",     "hs_radar <on\|off>",                                  "白方全图雷达：白方地图显示所有存活玩家位置（不区分阵营）"),` |
| 48 | `("hs_upgrade",   "hs_upgrade [vision\|speed\|task]",                   "黑学分：击杀获得学分，换取视野/移速/任务门槛强化"),` |
| 75 | `Plugin.Log.LogInfo("[HS] ConsoleBridge：DT_Tools 控制台尚未就绪，跳过命令列表刷新。");` |
| 83 | `Plugin.Log.LogWarning("[HS] ConsoleBridge：找不到 BuildCommandsJson/_cachedCommandsJson，hs_* 不会出现在命令列表（仍可执行）。");` |
| 88 | `Plugin.Log.LogInfo("[HS] ConsoleBridge：已重建 DT_Tools 命令列表缓存，hs_* 现在应在列表与补全中可见。");` |
| 92 | `Plugin.Log.LogWarning($"[HS] ConsoleBridge：重建命令列表失败 — {ex.Message}");` |
| 108 | `Plugin.Log.LogInfo("[HS] 未找到 DT_Tools 的 WebConsole.ExecuteCommand，hs_* 命令不可执行（其余功能不受影响）。");` |
| 176 | `Plugin.Log.LogWarning("[HS] ConsoleBridge：找不到 WebConsole.BuildCommandsJson，hs_* 不会出现在命令列表（仍可执行）。");` |
| 203 | `Plugin.Log.LogWarning($"[HS] 追加 hs_* 到命令列表失败：{ex.Message}");` |

## CorpseReportFeature.cs

| 行 | 代码 |
|---|---|
| 32 | `description: "禁止尸体报告：不进入调查/裁判阶段。自动调查改为极大延迟，手动报警直接丢弃。",` |
| 38 | `"首具尸体出现后「自动进入调查阶段」的延迟秒数。原版为 50~70 秒；改为极大值可让该路径实际不触发。",` |
| 42 | `[ConfigField(true, "禁止玩家对尸体报警（手动开会）。")]` |
| 46 | `"把尸体上的『报告』改写成『搬起尸体』。" +` |
| 47 | `"开：白方也能拖尸，且尸体不再抢占交互位（捉迷藏里尸体本就不该是举报按钮）；" +` |
| 48 | `"关：只丢弃报告，尸体仍会挡住它旁边的交互目标。")]` |
| 140 | `Plugin.Log.LogWarning($"[HS] CorpseReport：改写为搬运失败 — {ex.Message}");` |

## DtBridge.cs

| 行 | 代码 |
|---|---|
| 33 | `Plugin.Log.LogInfo("[HS] 检测到 DT_Tools，配置将写入其 .cfg（DT CONFIG 页面可见）。");` |
| 37 | `Plugin.Log.LogInfo("[HS] 未检测到 DT_Tools，使用独立配置 HideAndSeek.cfg。");` |
| 60 | `Plugin.Log.LogWarning($"[HS] 探测 DT_Tools 配置失败，改用独立配置：{ex.Message}");` |

## DummyFeature.cs

| 行 | 代码 |
|---|---|
| 26 | `description: "假人玩家（测试用）：在单人房造出可被刀死的白方靶子，供房主自己当黑方验证对抗流程。",` |
| 31 | `[ConfigField(0, "进入选角阶段时自动生成的假人数量（0 = 不自动生成，改用 hs_dummy 命令）。", Min = 0f, Max = 8f)]` |
| 34 | `[ConfigField("假人", "假人名字前缀，实际名字 = 前缀 + 座位号。")]` |
| 37 | `[ConfigField("", "默认角色 ID 列表（英文逗号分隔，按假人顺序循环取用）。留空则每个假人随机。")]` |
| 40 | `[ConfigField(false, "拦截假人引发的对官方服务器的上报（战绩 / 加星 / 成就）。开启前请确认已在测试环境。")]` |
| 84 | `Plugin.Log.LogWarning($"[HS] Dummy：自动生成第 {i + 1} 个失败 — {err}");` |
| 87 | `Plugin.Log.LogInfo($"[HS] Dummy：选角前自动生成 {ok}/{want} 个假人。");` |
| 155 | `Plugin.Log.LogInfo($"[HS] Dummy：回大厅后重建 {ok} 个假人（原有 {have}，目标 {want}）。");` |
| 189 | `Plugin.Log.LogInfo("[HS] Dummy：已拦截结算界面的免费货币兑换请求（测试局不刷货币）。");` |

## DummyManager.cs

| 行 | 代码 |
|---|---|
| 75 | `error = "当前没有活动房间";` |
| 82 | `error = "座位表尚未初始化（还没进过大厅）";` |
| 90 | `error = $"人数已达出生点上限 {StartPositionCapacity}（MapData.StartPosList 只有这么多）";` |
| 105 | `error = "没有空闲座位（上限 16）";` |
| 121 | `string name = (DummyFeature.NamePrefix?.Value ?? "假人") + id;` |
| 164 | `$"[HS] Dummy：已生成假人 #{id}（{name}，角色 {player.CharacterId}，AccountID={player.AccountID}）。");` |
| 171 | `Plugin.Log.LogError($"[HS] Dummy：生成假人失败 — {error}");` |
| 183 | `error = "当前没有活动房间";` |
| 190 | `error = $"没有假人 #{id}";` |
| 208 | `Plugin.Log.LogInfo($"[HS] Dummy：已移除假人 #{id}。");` |
| 286 | `Plugin.Log.LogWarning($"[HS] Dummy：随机挑角色失败 — {ex.Message}");` |
| 294 | `Plugin.Log.LogInfo($"[HS] Dummy：选角回调 State={room?.State} 待处理={DesiredCharacter.Count}");` |
| 354 | `Plugin.Log.LogInfo($"[HS] Dummy：假人 #{id} 已选角 {chara}。");` |
| 356 | `Plugin.Log.LogWarning($"[HS] Dummy：假人 #{id} 选角 {chara} 尚未生效（当前 {player.CharacterId}），下个 tick 重试。");` |
| 365 | `Plugin.Log.LogWarning($"[HS] Dummy：假人 #{id} 选角异常 — {ex.Message}");` |
| 376 | `("rin", 102), ("benjamin", 102), ("小熊", 102),` |
| 377 | `("luna", 103), ("露娜", 103),` |
| 378 | `("jeremy", 104), ("杰瑞米", 104),` |
| 379 | `("hasung", 105), ("河成", 105),` |
| 380 | `("kaho", 106), ("红毛", 106),` |
| 381 | `("miyuki", 107), ("美雪", 107),` |
| 382 | `("liliana", 108), ("莉莉安娜", 108),` |
| 383 | `("seol", 109), ("雪", 109),` |
| 384 | `("louis", 110), ("路易斯", 110),` |
| 386 | `("noel", 112), ("诺艾尔", 112),` |
| 475 | `Plugin.Log.LogWarning("[HS] Dummy：找不到 Player.InitLobby，假人的事件委托未绑定（可能不影响测试）。");` |

## DummyPickFeature.cs

| 行 | 代码 |
|---|---|
| 28 | `description: "让假人按指定角色真正参与选角（含手动用 hs_dummy 生成的假人）。",` |

## FuseboxRevealFeature.cs

| 行 | 代码 |
|---|---|
| 30 | `description: "把「可拆电箱」的标记包也发给黑方（原版只发黑幕），让黑方在地图与平板上看得到目标。",` |
| 35 | `[ConfigField(true, "开：可拆电箱标记同时发给黑方。关：保持原版（仅黑幕可见）。")]` |

## HsCommandRouter.cs

| 行 | 代码 |
|---|---|
| 69 | `default:             return Error($"未知命令 {name}（输入 hs 查看总览；另有 hs_check / hs_mode / hs_aoi / hs_cd / hs_killlimit / hs_dummy / hs_flash / hs_roomname / hs_tp）");` |
| 95 | `return Error("用法: hs_mode <on\|off>");` |
| 99 | `return Error("用法: hs_mode <on\|off>");` |
| 102 | `AnnounceRule(value.Value ? "捉迷藏模式已开启" : "捉迷藏模式已关闭");` |
| 135 | `AnnounceRule($"黑方视野 = {Num(AoiCullingFeature.EnterRange, 750f)} / {Num(AoiCullingFeature.ExitRange, 1100f)}");` |
| 146 | `return Error("用法: hs_cd <秒>");` |
| 149 | `AnnounceRule($"刀冷却 = {WeaponCooldownFeature.RearmSeconds.Value} 秒");` |
| 164 | `return Error("用法: hs_killlimit <n\|unlimited>");` |
| 166 | `AnnounceRule($"击杀上限 = {KillLimitFeature.MaxKills.Value}");` |
| 186 | `return Error("座位号必须是数字（1-16，0=自动）。例: hs_dummy add 5 luna");` |
| 191 | `return Error("角色无效（用 hs_dummy chars 查看）。例: hs_dummy add 5 luna");` |
| 194 | `return Error("生成失败: " + err);` |
| 204 | `return Error("用法: hs_dummy del <座位号>");` |
| 206 | `return Error("移除失败: " + err);` |
| 215 | `return Error("用法: hs_dummy <add [座位号] [角色]\|del <座位号>\|list\|chars\|clear>；角色可填 ID 或名字，如 luna");` |
| 233 | `return Error("灯效功能未加载");` |
| 240 | `return Error("用法: hs_flash <on\|off>（关闭后不再闪烁，但仍会直接进入黑灭白亮的定态）");` |
| 254 | `return Error("读取房间名失败: " + qerr);` |
| 263 | `return Error("用法: hs_roomname <新房间名>（无参数则显示当前值）");` |
| 266 | `return Error("改名失败: " + err);` |
| 278 | `"用法: hs_tp <玩家ID> <x> <y> → 传到坐标；" +` |
| 279 | `"hs_tp <玩家ID> to <目标玩家ID> → 传到某人身边；" +` |
| 280 | `"hs_tp to <目标玩家ID> → 省略第一个参数表示操作自己";` |
| 289 | `return Error("当前没有活动房间");` |
| 298 | `return Error("省略玩家ID时默认操作 #1（房主），但没有找到该玩家。\n" + TpUsage);` |
| 305 | `return Error("玩家ID 必须是数字。\n" + TpUsage);` |
| 309 | `return Error($"没有玩家 #{id}。\n" + TpUsage);` |
| 324 | `return Error($"没有玩家 #{targetId}。\n" + TpUsage);` |
| 327 | `desc = $"#{targetId} 的位置";` |
| 341 | `return Error("目标位置无效");` |
| 348 | `Plugin.Log.LogInfo($"[HS] Teleport：#{moverId} 已传送到 {desc}。");` |
| 364 | `return Error("发刀功能未加载");` |
| 370 | `return Error("排除名单不可用");` |
| 378 | `return "{\"ok\":true,\"exclude\":\"\",\"note\":\"已清空排除名单\"}";` |
| 385 | `return Error("用法: hs_grant exclude <玩家ID...> \| hs_grant exclude clear");` |
| 400 | `return Error("用法: hs_grant <on\|off> \| hs_grant exclude <玩家ID...> \| hs_grant exclude clear");` |
| 408 | `AnnounceRule(on.Value ? "发刀模式 = 开局随机发刀" : "发刀模式 = 自行跑刀");` |
| 409 | `return $"{{\"ok\":true,\"autoGrant\":{Bool(give.Value)},\"blockArmory\":{Bool(block?.Value ?? false)},\"exclude\":\"{exclude?.Value}\",\"note\":\"下一局生效\"}}";` |
| 426 | `return Error("不在房间中");` |
| 436 | `case "vision": case "视野": idx = HideAndSeek.Features.Combat.KillUpgradeFeature.DirVision; break;` |
| 437 | `case "speed":  case "移速": idx = HideAndSeek.Features.Combat.KillUpgradeFeature.DirSpeed; break;` |
| 438 | `case "task":   case "任务": idx = HideAndSeek.Features.Combat.KillUpgradeFeature.DirTask; break;` |
| 439 | `default: return Error("用法: hs_upgrade <vision\|speed\|task>");` |
| 456 | `return Error("不在房间中");` |
| 459 | `return Error("用法: hs_debug <black\|exec\|list\|credit> ...");` |
| 477 | `return Error($"用法: hs_debug {sub} <玩家ID> ...");` |
| 485 | `return Error($"找不到玩家 #{pid}（用 hs_debug list 查看）");` |
| 491 | `return Error("用法: hs_debug credit <数量>（可为负）");` |
| 508 | `return Error("用法: hs_debug exec <玩家ID> <命令文本>");` |
| 515 | `return Error($"未知子命令 {sub}");` |
| 524 | `return Error("雷达功能未加载");` |
| 531 | `return Error("用法: hs_radar <on\|off>（开启后白方小地图显示所有存活玩家）");` |

## KillLimitFeature.cs

| 行 | 代码 |
|---|---|
| 21 | `description: "解除黑方击杀上限：原版每人最多 1~2 杀，归零后无法再出刀。",` |
| 26 | `[ConfigField(9999, "黑方持有武器后的可击杀次数上限。原版为 1~2；设为极大值即等于无限。", Min = 1f)]` |
| 45 | `Plugin.Log.LogInfo($"[HS] KillLimit：BlackKillLimit → {__result}（原版：单人 1 / 多人 2）。");` |

## KillTimeBonusFeature.cs

| 行 | 代码 |
|---|---|
| 23 | `description: "黑方每杀死一人给倒计时增加若干秒（设为 0 关闭）。",` |
| 28 | `[ConfigField(30, "每次击杀给倒计时增加的秒数。0 = 关闭本效果。", Min = 0f, Max = 600f)]` |
| 31 | `[ConfigField(true, "击杀加时时向全场播报一行提示。")]` |
| 34 | `[ConfigField("击杀成功，倒计时增加 {sec} 秒",` |
| 35 | `"击杀加时的播报文本。占位符：{sec} 增加秒数。")]` |
| 68 | `Plugin.Log.LogInfo($"[HS] KillTimeBonus：击杀成功，倒计时 +{bonus} 秒。");` |

## KillUpgradeFeature.cs

| 行 | 代码 |
|---|---|
| 35 | `description: "黑学分：击杀获得学分，可换取视野/移速/任务门槛三项强化（每项最多 3 级）。",` |
| 40 | `[ConfigField(100f, "学分池总量。杀光全部白方恰好发完。", Min = 10f, Max = 10000f)]` |
| 43 | `[ConfigField(18f, "每级消耗（池子的百分比）。默认 18% → 6/7 池子约够点 5 级。",` |
| 47 | `[ConfigField(3, "每个方向的最大等级。", Min = 0f, Max = 9f)]` |
| 50 | `[ConfigField(0.5f, "【视野】每级扩大比例。0.5 = 每级 +50%（连升三级 ≈ 3.4 倍）。",` |
| 54 | `[ConfigField(0.1f, "【移速】每级增加量（直接加到 SpeedBoost.BlackSpeedMul）。",` |
| 58 | `[ConfigField(10f, "【任务门槛】每级增加的任务进度要求（加到 WhiteWinOnTimeout.MinMissionProgress）。",` |
| 62 | `[ConfigField(true, "每次升级向全场发系统告示（同时进入发信机记录）。")]` |
| 119 | `$"[HS] KillUpgrade：击杀获得 {share:F1} 学分（池子 {PoolTotal?.Value:F0} ÷ {divisor}），当前 {_credits:F1}。");` |
| 134 | `message = "未知升级方向（vision / speed / task）";` |
| 139 | `message = $"{DirName(dir)} 已达到上限 {maxLevel} 级";` |
| 146 | `message = $"学分不足（需要 {cost:F1}，现有 {_credits:F1}）";` |
| 190 | `return $"学分 {_credits:F0} 每级{cost:F0}\n" +` |
| 191 | `$"视野x {v}  速度 {s}\n" +` |
| 192 | `$"任务量 {k}\n" +` |
| 193 | `"用法 /cre v\|s\|t";` |
| 200 | `return $"视野提升已至 {1f + (VisionBonusPerLevel?.Value ?? 0.5f) * level:F1}";` |
| 202 | `return $"速度提升已至 {(SpeedBonusPerLevel?.Value ?? 0.1f) * level:F1}";` |
| 204 | `return $"最低任务完成量提高至 {(TaskBonusPerLevel?.Value ?? 10f) * level:F0}";` |
| 206 | `return "强化完成";` |
| 213 | `case DirVision: return "视野追踪范围";` |
| 214 | `case DirSpeed: return "移动速度";` |
| 215 | `case DirTask: return "白方任务门槛";` |
| 231 | `sb.Append("学分 ").Append(_credits.ToString("F1"))` |
| 232 | `.Append(" 每级").Append(cost.ToString("F0"));` |
| 233 | `sb.Append('\n').Append("视野 ").Append(Levels[DirVision]).Append('/').Append(max)` |
| 235 | `sb.Append(" 移速 ").Append(Levels[DirSpeed]).Append('/').Append(max)` |
| 237 | `sb.Append('\n').Append("任务 ").Append(Levels[DirTask]).Append('/').Append(max)` |
| 239 | `.Append("  用法 /credit v\|s\|t");` |
| 294 | `Text = "【黑学分】" + text,` |
| 301 | `Plugin.Log.LogWarning($"[HS] KillUpgrade：告示失败 — {ex.Message}");` |

## LockDoorFeature.cs

| 行 | 代码 |
|---|---|
| 32 | `description: "锁门命令：白方看到原生锁定，黑方看到只是关着（按 E 可秒解）。",` |
| 37 | `[ConfigField(0.3f, "锁定半径 = 地图短边 × 此比例。0.25 ≈ 1/4 地图，0.33 ≈ 1/3。", Min = 0.05f, Max = 1f)]` |
| 40 | `[ConfigField(10, "锁定持续秒数（写入门自身的锁定总时长，由原版 TickDoor 计时解锁）。", Min = 1f, Max = 300f)]` |
| 116 | `$"[HS] LockDoor：黑方 #{byPlayer?.PublicInfo?.PlayerId} 锁住 {count} 扇门（半径 {radius:F0}，{seconds} 秒）。");` |
| 200 | `$"[HS] LockDoor：黑方 #{player.PublicInfo?.PlayerId} 秒解并打开了门 #{__instance.ID}。");` |

## LunaImmunityFeature.cs

| 行 | 代码 |
|---|---|
| 25 | `description: "露娜服务端免疫：普通刀杀对露娜及其能力持有者无效（致命诡计与真实停电仍可破防）。",` |
| 30 | `[ConfigField(true, "拦截时给攻击者播放失败音效（原版护盾音效不在服务端音效枚举中，故用 FailedSfx）。")]` |
| 56 | `$"[HS] LunaImmunity：拦截 #{__instance.PublicInfo.PlayerId} → #{targetId}（目标角色={target.CharacterId}，技能={target.SkillComponent?.Data?.Type}）。");` |

## MissionTimePenaltyFeature.cs

| 行 | 代码 |
|---|---|
| 28 | `description: "完成任务减少限制时间：原版完成任务会加时，开启后改为扣减等量时间。",` |
| 33 | `[ConfigField(true, "启用任务扣时。")]` |
| 36 | `[ConfigField(1f, "额外乘数。最终限制时间变化 = (-1) × 原版加时量 × 本乘数；原版加时量已包含大厅设置的时限权重，因此本项只是在其之上再乘一个系数，不替换原始设置。", Min = 0.1f, Max = 5f)]` |
| 88 | `$"[HS] MissionTimePenalty：完成任务，限制时间净减少 {_capturedDelta * multiplier:F1} 秒。");` |

## MiyukiScanFeature.cs

| 行 | 代码 |
|---|---|
| 35 | `description: "美幸被动：每 15 秒扫描全图。黑方临时解除 AOI，地图标记 1 秒实时 + 2 秒静止后消失。",` |
| 40 | `[ConfigField(15, "扫描间隔（秒）。", Min = 5f, Max = 300f)]` |
| 43 | `[ConfigField(1, "AOI 解封持续（秒）。到点后收回超范围玩家的可见性。", Min = 0f, Max = 10f)]` |
| 46 | `[ConfigField(3, "地图标记总持续（秒）。其中前 1 秒实时跟随，之后静止，到点消失。", Min = 1f, Max = 30f)]` |
| 49 | `[ConfigField(true, "对美幸启用该被动。")]` |
| 216 | `$"[HS] MiyukiScan：美幸 #{pid} 扫描（{(isBlack ? "黑方：解封 AOI" : "白方：仅地图")}）。");` |
| 282 | `Plugin.Log.LogWarning($"[HS] MiyukiScan：收回 #{other.PublicInfo.PlayerId} 失败 — {ex.Message}");` |

## ModeRuntime.cs

| 行 | 代码 |
|---|---|
| 28 | `"捉迷藏模式总开关。关闭时所有补丁放行原版逻辑；改后即时生效，无需重启。");` |

## ModeWatchdog.cs

| 行 | 代码 |
|---|---|
| 33 | `Plugin.Log.LogInfo("[HS] 模式已关闭（当前无活动房间，无需回滚）。");` |
| 51 | `Plugin.Log.LogInfo("[HS] 模式已关闭：已回滚黑方可见性与光照。");` |

## ModeWatchdogFeature.cs

| 行 | 代码 |
|---|---|
| 17 | `description: "模式开关监视：关闭捉迷藏模式时自动回滚黑方可见性与光照，避免残留半关闭状态。",` |

## NoMasterMindFeature.cs

| 行 | 代码 |
|---|---|
| 21 | `description: "开局不分配黑幕（Dark）：跳过原版随机指定，改由玩家自行跑刀转黑方。",` |
| 36 | `Plugin.Log.LogInfo("[HS] NoMasterMind：已跳过黑幕分配，该玩家保持白方。");` |

## PatchLoader.cs

| 行 | 代码 |
|---|---|
| 75 | `log.LogInfo($"[HS] 已跳过功能: {desc.Type.Name} ([{desc.Section}].Enabled = false)");` |
| 98 | `log.LogError($"[HS] 嵌套补丁加载失败: {desc.Type.Name}.{nested.Name} ([{desc.Section}]) — " +` |
| 105 | `log.LogInfo($"[HS] 已启用功能: {desc.Type.Name} ([{desc.Section}], {desc.Side})");` |
| 110 | `log.LogError($"[HS] 功能加载失败: {desc.Type.Name} ([{desc.Section}]) — " +` |
| 133 | `throw new InvalidOperationException($"重复的配置段名 [{feature.Section}]（类型 {type.FullName}）");` |

## Plugin.cs

| 行 | 代码 |
|---|---|
| 55 | `$"[HS] HideAndSeek {Version} 加载完成（DT_Tools {(DtBridge.HasDtTools ? "已检测到" : "未检测到")}）：" +` |
| 56 | `$"启用 {result.EnabledCount}，跳过 {result.SkippedCount}，失败 {result.FailedCount}。" +` |
| 57 | `$"捉迷藏模式当前 {(ModeRuntime.Active ? "开启" : "关闭")}（段 [{ModeRuntime.Section}].Enabled）。");` |

## PowerRepairFeature.cs

| 行 | 代码 |
|---|---|
| 34 | `description: "电力恢复条件：已修电箱数达到该值即恢复供电（0 = 原版，需全部修好）。",` |
| 39 | `[ConfigField(1, "已修复的电箱数达到此值即恢复供电。0 = 原版行为（必须全部修好）；1 = 修好任意一个即恢复。",` |

## RoomNameFeature.cs

| 行 | 代码 |
|---|---|
| 27 | `description: "修改当前房间在 Steam 房间列表里显示的名字（只有房主可改；已在房间内的玩家界面不会随之变化）。",` |
| 32 | `[ConfigField("", "最近一次设置的房间名（留空表示尚未设置过）。")]` |
| 44 | `error = "尚未创建房间";` |
| 51 | `error = "拿不到 LobbyId（可能还没进入大厅）";` |
| 73 | `error = "找不到 SteamMatchmaking.SetLobbyData";` |
| 82 | `error = "Steam 拒绝了修改（只有房主可以改，且需已创建大厅）";` |
| 89 | `Plugin.Log.LogInfo($"[HS] RoomName：房间名已改为「{name}」。");` |
| 115 | `error = "找不到 SteamMatchmaking.GetLobbyData";` |

## RuleRewriteFeature.cs

| 行 | 代码 |
|---|---|
| 36 | `description: "按剩余时间/击杀数/存活数动态重写玩法参数（实验性规则引擎）。",` |
| 42 | `"动态规则。格式：条件 -> 动作；多条用换行或分号分隔。" +` |
| 43 | `"条件可用 time<=N（剩余秒）、kills>=N（黑方击杀）、alive<=N（白方存活），多个用 & 连接；" +` |
| 44 | `"动作是 Key=Value，多个用 , 连接。可用键：SpeedMul/EnterRange/ExitRange/Cooldown/KillLimit/RepairCount/MinProgress。" +` |
| 45 | `"例：time<=120 -> SpeedMul=1.3, EnterRange=1200")]` |
| 232 | `Plugin.Log.LogWarning($"[HS] RuleRewrite：写入 {key}={value} 失败 — {ex.Message}");` |
| 286 | `Plugin.Log.LogWarning($"[HS] RuleRewrite：未知的动作键 {key}。");` |
| 313 | `Plugin.Log.LogWarning($"[HS] RuleRewrite：还原 {kv.Key} 失败 — {ex.Message}");` |
| 317 | `Plugin.Log.LogInfo($"[HS] RuleRewrite：已还原 {Originals.Count} 个被规则改写的参数。");` |

## SoloPlayFeature.cs

| 行 | 代码 |
|---|---|
| 26 | `description: "单人/少人开局（测试向）：放开开始游戏所需的最少玩家数，正式对局无需开启。",` |
| 31 | `[ConfigField(1, "开始游戏所需的最少玩家数。原版正式服为 5；设为 1 即可单人开局。", Min = 1f, Max = 10f)]` |

## SpeedBoostFeature.cs

| 行 | 代码 |
|---|---|
| 25 | `description: "黑方移动速度倍率（1.0 = 原版）。",` |
| 30 | `[ConfigField(1.0f, "黑方移动速度倍率。1.0 = 原版；1.2 = 快 20%。", Min = 0.5f, Max = 3f)]` |

## StartFlashFeature.cs

| 行 | 代码 |
|---|---|
| 24 | `description: "捉迷藏开始灯效：坏灯式闪烁（白方终亮、黑方终暗）。默认由「有人拿刀」触发，可用 hs_flash 开关。",` |
| 30 | `"是否播放闪烁。关闭后不再闪，改为直接进入定态（白方亮、黑方暗）——" +` |
| 31 | `"玩法所需的光照结果不受影响。可用命令 hs_flash on\|off 运行时切换。")]` |
| 34 | `[ConfigField(true, "有人拿走武器（转为黑方）时播放 —— 这就是「捉迷藏开始」的时刻。")]` |
| 37 | `[ConfigField(false, "进入生存阶段（开局）时也播放。默认关闭：开局就闪太吵，改由拿刀触发。")]` |
| 40 | `[ConfigField(12, "闪烁次数（模拟坏灯抽搐）。", Min = 2f, Max = 60f)]` |
| 43 | `[ConfigField(25, "闪烁的最小间隔毫秒数。", Min = 10f, Max = 1000f)]` |
| 46 | `[ConfigField(160, "闪烁的最大间隔毫秒数。与最小值拉开距离才有「坏掉」的随机感。", Min = 10f, Max = 3000f)]` |
| 49 | `[ConfigField(220, "闪烁结束后到落定之间的毫秒数。", Min = 0f, Max = 3000f)]` |
| 52 | `[ConfigField(2500, "开局触发时的延迟毫秒数（等待客户端进入对局）。", Min = 0f, Max = 30000f)]` |
| 69 | `Plugin.Log.LogInfo("[HS] StartFlash：闪烁已关闭，直接进入定态（白方亮 / 黑方暗）。");` |
| 96 | `$"[HS] StartFlash：已播放坏灯式闪烁（{count} 次，间隔 {lo}~{hi}ms，历时约 {at}ms）。");` |

## TeleportCommandFeature.cs

| 行 | 代码 |
|---|---|
| 26 | `description: "传送命令：预警数秒后传送到指定或随机玩家在发起时的位置。",` |
| 31 | `[ConfigField(3000, "预警到落地之间的毫秒数（留给目标的逃跑时间）。", Min = 0f, Max = 15000f)]` |
| 34 | `[ConfigField(false, "传送落地时播放原版的 TeleportVfx（黑洞状特效）。默认关闭 —— 观感突兀。")]` |
| 43 | `"落点特效（EEffectType 名）：TeleportVfx / BlackHoleVfx / MineBombVfx / FlashVfx / ScopeVfx / none")]` |
| 47 | `"落点音效（ESoundType 名）：TeleportSfx / WarningSfx / ExplosionSfx / AirHornSfx / BlackholeSfx / none")]` |
| 50 | `[ConfigField(true, "预警期间在落点播一个世界特效（闪光），让目标看清黑方将从哪里出现。")]` |
| 54 | `[ConfigField("黑方即将传送到标记处",` |
| 55 | `"预警时发给目标的一行文字（出现在其聊天栏）。留空则不发。")]` |
| 81 | `Plugin.Log.LogInfo($"[HS] Teleport：目标 #{target.PublicInfo.PlayerId} 在柜子里，落点改用柜子出口。");` |
| 87 | `Plugin.Log.LogWarning($"[HS] Teleport：查询柜子失败 — {ex.Message}");` |
| 115 | `Plugin.Log.LogInfo($"[HS] Teleport：目标 #{target.PublicInfo.PlayerId} 在游戏机上，落点改用退场点。");` |
| 124 | `Plugin.Log.LogWarning($"[HS] Teleport：查询游戏机失败 — {ex.Message}");` |
| 135 | `error = "没有可用的传送者";` |
| 140 | `error = "当前阶段无法传送";` |
| 159 | `error = $"没有玩家 #{targetId}";` |
| 177 | `error = "没有其他存活玩家可作为目标";` |
| 184 | `error = "不能传送到自己身上";` |
| 189 | `error = $"玩家 #{target.PublicInfo.PlayerId} 已死亡";` |
| 200 | `error = "目标位置不可用";` |
| 220 | `catch (global::System.Exception ex) { Plugin.Log.LogWarning($"[HS] Teleport：落点音效失败 — {ex.Message}"); }` |
| 224 | `Plugin.Log.LogWarning($"[HS] Teleport：未知音效名 {sfxName}");` |
| 245 | `Plugin.Log.LogWarning("[HS] Teleport：LandingVfxType 为 TeleportVfx（无世界坐标渲染分支），已自动改用 BlackHoleVfx。");` |
| 258 | `Plugin.Log.LogInfo($"[HS] Teleport：落点特效 {vfx} @ ({dest.X:F0},{dest.Y:F0})。");` |
| 262 | `Plugin.Log.LogWarning($"[HS] Teleport：落点特效失败 — {ex.Message}");` |
| 271 | `Plugin.Log.LogWarning($"[HS] Teleport：未知特效名 {vfxName}");` |
| 293 | `Plugin.Log.LogWarning($"[HS] Teleport：落点文字失败 — {ex.Message}");` |
| 299 | `$"[HS] Teleport：黑方 #{caster.PublicInfo?.PlayerId} 锁定 #{targetPid}，" +` |
| 300 | `$"{(WarnDelayMs?.Value ?? 3000)}ms 后落地 ({dest.X:F0},{dest.Y:F0})。");` |
| 309 | `Plugin.Log.LogInfo("[HS] Teleport：阶段已变化或传送者已死亡，取消落地。");` |
| 319 | `Plugin.Log.LogInfo($"[HS] Teleport：已落地到 #{targetPid} 的位置。");` |

## TeleportGuardFeature.cs

| 行 | 代码 |
|---|---|
| 33 | `description: "黑洞防滥用：把传送落点改为目标附近的出生点（不再精准落到人身上），并拦掉暴露目标真实坐标的黑洞特效。",` |
| 38 | `[ConfigField(true, "改写传送落点：改为 StartPosList 中离目标最近的出生点。关闭则保持原版（精确落到目标身上）。")]` |
| 41 | `[ConfigField(true, "把黑洞特效改写到实际落点（就近出生点），用于提示附近的人它要来了；关闭则保留原版的坐标泄露。")]` |
| 45 | `"是否区分阵营。关闭（默认）＝黑白方行为一致，落点都会被改写 —— " +` |
| 46 | `"不会因「只有黑方被改写」而让玩家从落点差异推断出谁是黑方；" +` |
| 47 | `"开启后只限制黑方、白方完全保持原版。")]` |
| 99 | `__result = true;   // 必须 true：false 会让原版回退成"精确落到目标当前位置"` |
| 101 | `$"[HS] TeleportGuard：落点改写为出生点 ({spawn.X:F0},{spawn.Y:F0})（目标 #{target?.PublicInfo?.PlayerId}）。");` |

## WeaponCooldownFeature.cs

| 行 | 代码 |
|---|---|
| 25 | `description: "缩短黑方出刀冷却：击杀后重新可出刀的时间（原版 20 秒）。",` |
| 33 | `[ConfigField(10, "击杀后重新可出刀的冷却秒数。原版为 20，游戏内平衡建议 5~15。", Min = 1f, Max = 600f)]` |
| 48 | `Plugin.Log.LogInfo($"[HS] WeaponCooldown：击杀后冷却 {VanillaRearmSeconds} → {seconds} 秒。");` |

## WeaponGrantFeature.cs

| 行 | 代码 |
|---|---|
| 25 | `description: "开局武器供给：自行跑刀（原版）或开局直接给随机一名玩家发一把武器。",` |
| 30 | `[ConfigField(false, "开启后开局直接发放武器；关闭则保持原版，由玩家自行去武器架跑刀。")]` |
| 33 | `[ConfigField(2001, "发放的武器 ID。2001 刀 / 2002 棒 / 2003 锤 / 2004 铲 / 2005 手风琴。", Min = 2000f, Max = 2999f)]` |
| 36 | `[ConfigField(2500, "发刀延迟毫秒数（等待全体客户端进入对局）。", Min = 0f, Max = 30000f)]` |
| 39 | `[ConfigField("", "发刀时最高优先级排除的玩家 ID（逗号分隔，如 \"1,3\"）。" +` |
| 40 | `"若排除后真人候选为空，会自动放开假人限制在原池外重抽 —— 即宁可发给假人，也不发给被排除者。")]` |
| 90 | `Plugin.Log.LogInfo("[HS] WeaponGrant：真人候选被排除后为空，已放开假人限制重抽。");` |
| 95 | `Plugin.Log.LogWarning("[HS] WeaponGrant：排除后无任何候选，本次不发刀。");` |
| 109 | `$"[HS] WeaponGrant：已向随机玩家 #{target.PublicInfo.PlayerId} 发放武器 {weaponId}（开局发刀模式）。");` |
| 112 | `[ConfigField(true, "自动发刀后让武器架不再开启 —— 刀根本不会出现，因此既看不到也点不了。")]` |
| 138 | `Plugin.Log.LogInfo("[HS] WeaponGrant：自动发刀模式，武器架不开启（刀不会出现）。");` |
| 176 | `$"[HS] WeaponGrant：已有黑方，已没收 #{player.PublicInfo.PlayerId} 取到的武器。");` |
| 192 | `Plugin.Log.LogWarning($"[HS] WeaponGrant：没收武器失败 — {ex.Message}");` |

## WhiteCommandFeature.cs

| 行 | 代码 |
|---|---|
| 36 | `description: "白方公开聊天命令通道（每人独立 CD 与次数；黑方发命令一律吞掉）。",` |
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
| 126 | `"【白方】" +` |
| 127 | `"\n/rad — 全图扫描 15 秒（2 次/局）CD75" +` |
| 128 | `"\n/sta — 时停黑方 5 秒，耗 5% 进度 CD90" +` |
| 129 | `"\n/rep — 立即恢复供电，耗 10% 进度（仅断电）";` |
| 149 | `Plugin.Log.LogInfo($"[HS] WhiteCommand：收到聊天 Type={msg.Type} Text=\"{msg.Text}\"");` |
| 184 | `Plugin.Log.LogWarning($"[HS] 白方命令：解析失败 — {ex.Message}");` |
| 231 | `Reply(player, deviceId, $"全图扫描次数已用尽（{used}/{max}）。");` |
| 245 | `Reply(player, deviceId, "扫描冷却中。");` |
| 259 | `Reply(player, deviceId, $"(实验性)全图扫描已开启({shown}/{max})。");` |
| 262 | `SendPublic(room, "瞭望已开启。");` |
| 305 | `Plugin.Log.LogInfo($"[HS] WhiteCommand：已广播任务进度 {cur:F0}/{goal:F0} = {(int)(cur / goal * 100f)}%。");` |
| 309 | `Plugin.Log.LogWarning($"[HS] WhiteCommand：广播任务进度失败 — {ex.Message}");` |
| 327 | `Plugin.Log.LogWarning($"[HS] WhiteCommand：刷新黑方速度失败 — {ex.Message}");` |
| 337 | `if (inst == null) { why = "读不到任务进度"; return false; }` |
| 341 | `if (curProp == null) { why = "读不到任务进度字段"; return false; }` |
| 346 | `if (cur <= 0f) { why = "当前任务进度为 0"; return false; }` |
| 356 | `if (cur < cost) { why = $"进度不足（现有 {cur:F0}，需要 {cost:F0}）"; return false; }` |
| 363 | `Plugin.Log.LogInfo($"[HS] WhiteCommand：消耗任务进度 {cost:F0}（{cur:F0} → {cur - cost:F0}）。");` |
| 368 | `why = "扣进度失败：" + ex.Message;` |
| 390 | `Reply(player, deviceId, "时停冷却中。");` |
| 419 | `Plugin.Log.LogWarning($"[HS] WhiteCommand：施加时停失败 — {ex.Message}");` |
| 423 | `Plugin.Log.LogInfo($"[HS] WhiteCommand：时停已施加（TheWorld {stasisMs}ms）。");` |
| 426 | `Reply(player, deviceId, $"已时停黑方 {StasisSeconds?.Value ?? 5} 秒。");` |
| 443 | `Reply(player, deviceId, "当前没有断电，无需修复。");` |
| 469 | `Plugin.Log.LogWarning($"[HS] WhiteCommand：修电失败 — {ex.Message}");` |
| 472 | `Reply(player, deviceId, $"已立即恢复供电（修复 {fixedCount} 处）。");` |
| 497 | `Plugin.Log.LogInfo("[HS] WhiteCommand：时停结束，黑方移速已还原。");` |
| 530 | `Plugin.Log.LogWarning($"[HS] 白方命令：回执失败 — {ex.Message}");` |
| 550 | `Plugin.Log.LogWarning($"[HS] 白方命令：公开发言失败 — {ex.Message}");` |

## WhiteRadarFeature.cs

| 行 | 代码 |
|---|---|
| 44 | `description: "白方全图雷达：白方地图显示所有存活玩家的位置（不区分阵营，可热切换两种外观方案）。",` |
| 50 | `[ConfigField(false, "是否启用白方全图雷达。可用控制台 hs_radar on\|off，或规则引擎的 Radar 动作改写。")]` |
| 54 | `"外观方案（可热切换，改后下一 tick 生效）：\n" +` |
| 55 | `"Badge   = 白点 + 绿环徽章（用非玩家 id 走 S_PIN_MOVE，零副作用，不暴露身份）\n" +` |
| 56 | `"PureDot = 纯净白点（发 ChangeColor=3 放行客户端原生绘制；" +` |
| 57 | `"副作用：状态面板消失、目标文本空白、雷达期间无法从武器库取武器）")]` |
| 60 | `[ConfigField(30, "自动关闭的秒数（0 = 一直开启）。PureDot 模式强烈建议保持限时。", Min = 0f, Max = 600f)]` |
| 63 | `[ConfigField(false, "跳过假人。默认 false —— 测试房里往往只有假人，跳过会导致雷达看起来完全无效。")]` |
| 94 | `Plugin.Log.LogInfo("[HS] WhiteRadar：已关闭。");` |
| 104 | `$"[HS] WhiteRadar：已开启（模式 {Mode?.Value}）{(seconds > 0 ? $"，{seconds} 秒后自动关闭" : "")}。");` |
| 149 | `Plugin.Log.LogInfo("[HS] WhiteRadar：PureDot 假颜色已还原。");` |
| 169 | `Plugin.Log.LogWarning($"[HS] WhiteRadar：发送颜色失败 — {ex.Message}");` |
| 197 | `Plugin.Log.LogWarning($"[HS] WhiteRadar：发送 pin 失败 — {ex.Message}");` |

## WhiteWinFeature.cs

| 行 | 代码 |
|---|---|
| 33 | `description: "限制时间归零时判白方胜利（原版为黑方胜利）；可要求最低任务进度。",` |
| 39 | `[ConfigField(true, "限制时间归零时判白方胜利。")]` |
| 42 | `[ConfigField(0, "白方获胜所需的最低任务进度（百分比，0 = 不限制）。" +` |
| 43 | `"未达标时时间归零直接判黑方胜 —— 本玩法不报告尸体，故不回退到审判阶段。",` |
| 79 | `Plugin.Log.LogInfo($"[HS] 时间归零：任务进度 {progress}% < 要求 {need}% → 判黑方胜利。");` |
| 101 | `Plugin.Log.LogWarning("[HS] 找不到 Server.Game.MissionManager，任务进度相关判断将跳过。");` |
| 125 | `Plugin.Log.LogWarning($"[HS] 读取 MissionManager.AllClear 失败：{ex.Message}");` |
| 153 | `Plugin.Log.LogWarning($"[HS] 读取任务进度失败：{ex.Message}");` |
| 175 | `Plugin.Log.LogInfo("[HS] 限制时间归零 → 判白方胜利。");` |
| 195 | `Plugin.Log.LogWarning($"[HS] 调用原版 GameOver 失败，改用手写结算：{ex.Message}");` |
| 212 | `Plugin.Log.LogInfo("[HS] 时间归零且任务未达标 → 判黑方胜利（手写结算）。");` |

