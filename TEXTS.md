# 玩家可见文本

## BreakCommandFeature.cs

- L231: Reply(player, deviceId, $"未知命令 /{name}。\n{BuildHelp(forHelp)}");
- L265: Reply(player, deviceId, "当前阶段无法使用命令。");
- L273: Reply(player, deviceId, $"/{def.Name} 本局已用完（上限 {def.MaxUses} 次）。");
- L291: Reply(player, deviceId, $"/{def.Name} 条件未满足（{def.Condition}）。");
- L333: Reply(player, deviceId, "传送失败：" + tpErr);
- L368: Reply(player, deviceId, $"命令配置有误：{a}");
- L376: Reply(player, deviceId, $"命令配置有误：{a}");
- L386: Reply(player, deviceId, $"/{def.Name} 已执行。");
- L391: Reply(player, deviceId, $"/{def.Name} 执行失败，请查看房主日志。");
- L423: Reply(player, deviceId, "用法 /cre v|s|t|help");
- L477: Reply(player, deviceId, "附近没有可锁的门。");
- L481: Reply(player, deviceId, $"已锁住 {count} 扇门（半径 {radius:F0}）。");
- L489: Reply(player, deviceId, "电箱 ID 必须是数字。");
- L496: Reply(player, deviceId, "当前没有可拆的电箱。");
- L556: Reply(player, deviceId, "拆电功能当前不可用（内部方法未找到）。");

## BroadcastFeature.cs

- L43: [ConfigField(true, "启用播报。")]
- L46: [ConfigField("捉迷藏模式", "标题（进房介绍与开局提示共用）。")]
- L58: [ConfigField("当前，游戏开始后刷新刀具", "自行拿刀模式下 {knife} 的内容。")]
- L61: [ConfigField("当前，黑方角色将会自动指定。", "自动发刀模式下 {knife} 的内容。")]
- L64: [ConfigField("在倒计时结束之前，寻找刀具开始杀戮，或者完成任务逃离杀戮~",
- L68: [ConfigField("开始杀戮、开始搜索吧~在倒计时结束之前",
- L72: [ConfigField("躲避杀手，完成任务，在倒计时结束之前。祝你好运~",
- L76: [ConfigField("{name} 已经死亡({alive}/{total})",
- L80: [ConfigField("捉迷藏开始了~", "拿刀通告（仅自行拿刀模式发出；自动指定黑方时不发）。")]
- L83: [ConfigField(true, "房主用 hs_* 命令改动玩法设置时，向全场播报这次调整（仅在生存阶段播报）。")]
- L85: [ConfigField(true, "有玩家进入房间时，单独向他播报玩法规则。")]
- L88: [ConfigField(2500, "进房介绍的延迟毫秒数（等客户端就绪）。", Min = 0f, Max = 30000f)]
- L91: [ConfigField(3200, "开局提示的延迟毫秒数（须晚于自动发刀，才能分出黑方）。", Min = 0f, Max = 30000f)]
- L149: Notice(room, "【规则调整】" + change);
- L169: Notice(__instance, "【规则调整】" + kv.Value);

## KillUpgradeFeature.cs

- L134: message = "未知升级方向（vision / speed / task）";
- L139: message = $"{DirName(dir)} 已达到上限 {maxLevel} 级";
- L146: message = $"学分不足（需要 {cost:F1}，现有 {_credits:F1}）";

## WhiteCommandFeature.cs

- L231: Reply(player, deviceId, $"全图扫描次数已用尽（{used}/{max}）。");
- L245: Reply(player, deviceId, "扫描冷却中。");
- L259: Reply(player, deviceId, $"(实验性)全图扫描已开启({shown}/{max})。");
- L262: SendPublic(room, "扫描已开启。");
- L390: Reply(player, deviceId, "冻结冷却中。");
- L426: Reply(player, deviceId, $"已冻结黑方 {StasisSeconds?.Value ?? 5} 秒。");
- L443: Reply(player, deviceId, "当前没有断电，无需修复。");
- L472: Reply(player, deviceId, $"已立即恢复供电（修复 {fixedCount} 处）。");

