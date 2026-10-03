using System;
using System.Linq;
using System.Text;
using BepInEx.Configuration;
using Protocol;
using Server.Game;
using HideAndSeek.Core;
using HideAndSeek.Features.Combat;
using HideAndSeek.Features.Dummy;
using HideAndSeek.Features.Vision;
using GamePlayer = Server.Game.Player;

namespace HideAndSeek.Console
{
    /// <summary>
    /// hs_* 命令的解析与执行，返回 JSON（与 DT_Tools 的 /api/run 返回约定一致）。
    ///
    /// 取舍：只保留"运行期需要反复试参数"和"必须实时操作"的命令，
    /// 纯玩法开关一律走配置文件（在 DT CONFIG 页面改即可）。
    /// </summary>
    internal static class HsCommandRouter
    {
        /// <summary>是否属于本模块的命令（决定要不要拦截 DT_Tools 的执行流程）。</summary>
        public static bool IsHsCommand(string name)
            => !string.IsNullOrEmpty(name)
               && (name.Equals("hs", StringComparison.OrdinalIgnoreCase)
                   || name.StartsWith("hs_", StringComparison.OrdinalIgnoreCase));

        public static string Execute(string rawCommand)
        {
            if (string.IsNullOrWhiteSpace(rawCommand))
                return Error("empty command");

            var parts = rawCommand.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
            string name = parts[0].ToLowerInvariant();
            string[] args = parts.Skip(1).ToArray();

            // 兼容 "hs tp ..." 这种带空格的写法：把第二个词并入命令名。
            // 否则它会被当成无参数的 hs（返回总览），看起来成功其实什么都没做。
            if (name == "hs" && args.Length > 0)
            {
                string sub = "hs_" + args[0].ToLowerInvariant();
                switch (sub)
                {
                    case "hs_check": case "hs_reload": case "hs_mode": case "hs_aoi": case "hs_cd":
                    case "hs_killlimit": case "hs_dummy": case "hs_flash":
                    case "hs_roomname": case "hs_tp": case "hs_grant": case "hs_radar": case "hs_debug": case "hs_upgrade":
                    case "hs_panel":
                        name = sub;
                        args = args.Skip(1).ToArray();
                        break;
                }
            }
            switch (name)
            {
                case "hs":           return Status();
                case "hs_check":     return Diagnostics.Report();
                case "hs_reload":    return ReloadConfig();
                case "hs_dummy":     return Dummy(args);
                case "hs_flash":     return Flash(args);
                case "hs_roomname":  return RoomName(args);
                case "hs_mode":      return SetMode(args);
                case "hs_aoi":       return SetAoi(args);
                case "hs_cd":        return SetCooldown(args);
                case "hs_killlimit": return SetKillLimit(args);
                case "hs_tp":        return Teleport(args);
                case "hs_grant":     return Grant(args);
                case "hs_radar":     return Radar(args);
                case "hs_debug":     return Debug(args);
                case "hs_upgrade":   return Upgrade(args);
                case "hs_panel":     return Panel(args);
                default:             return Error($"未知命令 {name}（输入 hs 查看总览；另有 hs_check / hs_reload / hs_mode / hs_aoi / hs_cd / hs_killlimit / hs_dummy / hs_flash / hs_roomname / hs_tp / hs_panel）");
            }
        }

        // ── /hs ─────────────────────────────────────────────────────
        private static string Status()
        {
            var sb = new StringBuilder();
            sb.Append("{\"ok\":true")
              .Append(",\"mode\":").Append(Bool(ModeRuntime.Active))
              .Append(",\"aoi\":{")
                  .Append("\"enabled\":").Append(Bool(Enabled(AoiCullingFeature.EnterRange)))
                  .Append(",\"enter\":").Append(Num(AoiCullingFeature.EnterRange, 600f))
                  .Append(",\"exit\":").Append(Num(AoiCullingFeature.ExitRange, 900f))
                  .Append(",\"min\":").Append(Num(AoiCullingFeature.MinVisibleSeconds, 3f))
              .Append("}")
              .Append(",\"cooldown\":").Append(Num(WeaponCooldownFeature.RearmSeconds, 20))
              .Append(",\"killLimit\":").Append(Num(KillLimitFeature.MaxKills, 9999))
              .Append("}");
            return sb.ToString();
        }

        // ── /hs_reload ──────────────────────────────────────────────
        // 从游戏内 /reload 迁移过来：BepInEx 不监听 .cfg 变化，ConfigEntry.Value 是启动时
        // 读入的内存副本，手动改文件后必须显式 Reload() 才会重读到内存。
        // 它是运维动作、不是玩家命令，所以只留在这里（控制台/房主侧），不再出现在游戏内 /help。
        private static string ReloadConfig()
        {
            if (Plugin.HsConfig == null)
                return Error("配置句柄不可用（DT_Tools 未就绪）");

            try
            {
                Plugin.HsConfig.Reload();
                Plugin.Log.LogInfo("[HS] 配置已通过 hs_reload 重新读取。");
                return "{\"ok\":true,\"reloaded\":true}";
            }
            catch (Exception ex)
            {
                Plugin.Log.LogWarning($"[HS] hs_reload 失败 — {ex.Message}");
                return Error("重载失败，详见日志");
            }
        }

        // ── /hs_mode on|off ─────────────────────────────────────────
        private static string SetMode(string[] args)
        {
            if (args.Length == 0 || ModeRuntime.Enabled == null)
                return Error("用法: hs_mode <on|off>");

            bool? value = ParseBool(args[0]);
            if (value == null)
                return Error("用法: hs_mode <on|off>");

            ModeRuntime.Enabled.Value = value.Value;
            AnnounceRule(value.Value ? "捉迷藏模式已开启" : "捉迷藏模式已关闭");
            return $"{{\"ok\":true,\"mode\":{Bool(ModeRuntime.Active)}}}";
        }

        // ── /hs_aoi [on|off] [enter=600] [exit=900] [min=3] ─────────
        private static string SetAoi(string[] args)
        {
            if (args.Length == 0)
                return Status();

            foreach (var arg in args)
            {
                var kv = arg.Split(new[] { '=' }, 2);
                if (kv.Length == 1)
                {
                    bool? on = ParseBool(kv[0]);
                    if (on == null)
                        continue;
                    SetFeatureEnabled(typeof(AoiCullingFeature), on.Value);
                    continue;
                }

                if (!TryFloat(kv[1], out float f))
                    continue;

                switch (kv[0].ToLowerInvariant())
                {
                    case "enter": if (AoiCullingFeature.EnterRange != null) AoiCullingFeature.EnterRange.Value = f; break;
                    case "exit":  if (AoiCullingFeature.ExitRange != null)  AoiCullingFeature.ExitRange.Value = f;  break;
                    case "min":   if (AoiCullingFeature.MinVisibleSeconds != null) AoiCullingFeature.MinVisibleSeconds.Value = f; break;
                }
            }

            AnnounceRule($"黑方视野 = {Num(AoiCullingFeature.EnterRange, 750f)} / {Num(AoiCullingFeature.ExitRange, 1100f)}");
            return Status();
        }

        // ── /hs_cd <秒> ─────────────────────────────────────────────
        private static string SetCooldown(string[] args)
        {
            if (args.Length == 0 || WeaponCooldownFeature.RearmSeconds == null)
                return Status();

            if (!TryFloat(args[0], out float f))
                return Error("用法: hs_cd <秒>");

            WeaponCooldownFeature.RearmSeconds.Value = f < 1f ? 1 : (int)f;
            AnnounceRule($"刀冷却 = {WeaponCooldownFeature.RearmSeconds.Value} 秒");
            return Status();
        }

        // ── /hs_killlimit <n|unlimited> ─────────────────────────────
        private static string SetKillLimit(string[] args)
        {
            if (args.Length == 0 || KillLimitFeature.MaxKills == null)
                return Status();

            if (args[0].Equals("unlimited", StringComparison.OrdinalIgnoreCase))
                KillLimitFeature.MaxKills.Value = 9999;
            else if (TryFloat(args[0], out float f))
                KillLimitFeature.MaxKills.Value = f < 1f ? 1 : (int)f;
            else
                return Error("用法: hs_killlimit <n|unlimited>");

            AnnounceRule($"击杀上限 = {KillLimitFeature.MaxKills.Value}");
            return Status();
        }

        // ── /hs_dummy add [座位号 1-16] [角色ID|角色名] | del <座位号> | list | chars | clear ──
        private static string Dummy(string[] args)
        {
            if (args.Length == 0 || args[0].Equals("list", StringComparison.OrdinalIgnoreCase))
                return DummyManager.ListJson();

            switch (args[0].ToLowerInvariant())
            {
                case "chars":
                    return DummyManager.CharacterList();

                case "add":
                {
                    // 座位号：1-16；0 或省略 = 自动找空位
                    int id = 0;
                    if (args.Length >= 2 && !int.TryParse(args[1], out id))
                        return Error("座位号必须是数字（1-16，0=自动）。例: hs_dummy add 5 luna");

                    // 角色：数字 ID 或角色名（luna / 露娜 / seol …）
                    int chara = 0;
                    if (args.Length >= 3 && !TryParseCharacter(args[2], out chara))
                        return Error("角色无效（用 hs_dummy chars 查看）。例: hs_dummy add 5 luna");

                    if (!DummyManager.Spawn(id, chara, out int actual, out string err))
                        return Error("生成失败: " + err);

                    // 回实际角色（随机时 chara 参数是 0，回显它会让用户以为"没角色"）
                    int actualChara = DummyManager.GetCharacterId(actual);
                    return $"{{\"ok\":true,\"id\":{actual},\"characterId\":{actualChara},\"specified\":{Bool(chara > 0)}}}";
                }

                case "del":
                {
                    if (args.Length < 2 || !int.TryParse(args[1], out int id))
                        return Error("用法: hs_dummy del <座位号>");
                    if (!DummyManager.Remove(id, out string err))
                        return Error("移除失败: " + err);

                    return $"{{\"ok\":true,\"id\":{id}}}";
                }

                case "clear":
                    return $"{{\"ok\":true,\"removed\":{DummyManager.Clear()}}}";

                default:
                    return Error("用法: hs_dummy <add [座位号] [角色]|del <座位号>|list|chars|clear>；角色可填 ID 或名字，如 luna");
            }
        }

        /// <summary>角色参数：数字 ID 或角色名（luna / 露娜 / seol …）。</summary>
        private static bool TryParseCharacter(string text, out int charaId)
        {
            if (int.TryParse(text, out charaId) && charaId >= 0)   // 0 是合法值：表示随机
                return true;

            return DummyManager.TryParseCharacterName(text, out charaId);
        }

        // ── /hs_panel ───────────────────────────────────────────────
        // **直接放一次**「全屏面板 + 心跳声」（客户端 `MineBombVfx`）—— 纯表现，用来玩/吓人。
        // 与「自爆回放」无关：回放那边只放全屏压暗（`BlackOutVfx`），不插这个效果。
        // ⚠ 它是 UI 弹层（`UI_DespairBombEffect`），实测**可能残留到大厅**（重进房间即消失）。
        private static string Panel(string[] args)
        {
            var room = GameRoom.Instance;
            if (room == null)
                return Error("不在房间里");

            if (args.Length > 0)
                return Error("用法: hs_panel（直接放一次全屏面板+心跳声；它是一次性效果，⚠ 可能残留到大厅）");

            try
            {
                room.Broadcast(new S_PLAY_EFFECT
                {
                    Type = EEffectType.MineBombVfx,
                    DeviceId = 0
                });
                Plugin.Log.LogInfo("[HS] hs_panel：已放一次「全屏面板 + 心跳声」（MineBombVfx）。");
                return "{\"ok\":true,\"bombPanel\":true}";
            }
            catch (Exception ex)
            {
                return Error("放特效失败: " + ex.Message);
            }
        }

        // ── /hs_flash [on|off] ──────────────────────────────────────
        private static string Flash(string[] args)
        {
            var entry = StartFlashFeature.FlashEnabled;
            if (entry == null)
                return Error("灯效功能未加载");

            if (args.Length == 0)
                return $"{{\"ok\":true,\"flash\":{Bool(entry.Value)}}}";

            bool? on = ParseBool(args[0]);
            if (on == null)
                return Error("用法: hs_flash <on|off>（关闭后不再闪烁，但仍会直接进入黑灭白亮的定态）");

            entry.Value = on.Value;
            return $"{{\"ok\":true,\"flash\":{Bool(entry.Value)}}}";
        }

        // ── /hs_roomname [新名字] ────────────────────────────────────
        // 改的是 Steam 大厅元数据里的房间名，即「房间列表里显示的名字」；
        // 同时同步游戏内缓存 `NetworkManager.RoomName`（2026-10 新版起，UI_GameScene 显示的是它）。
        // 无参数时把两边都回读出来 —— 不一致就说明只写成了一边。
        private static string RoomName(string[] args)
        {
            if (args.Length == 0)
            {
                if (!HideAndSeek.Features.System.RoomNameFeature.Query(out string cur, out string cached, out string qerr))
                    return Error("读取房间名失败: " + qerr);

                string last = HideAndSeek.Features.System.RoomNameFeature.LastName?.Value ?? "";
                return $"{{\"ok\":true,\"lobbyName\":\"{cur}\",\"cachedName\":\"{cached}\",\"lastSet\":\"{last}\"}}";
            }

            // 房间名可能含空格，拼回整串
            string name = string.Join(" ", args).Trim();
            if (name.Length == 0)
                return Error("用法: hs_roomname <新房间名>（无参数则显示当前值）");

            if (!HideAndSeek.Features.System.RoomNameFeature.Apply(name, out string err))
                return Error("改名失败: " + err);

            return $"{{\"ok\":true,\"name\":\"{name}\"}}";
        }

        // ── /hs_tp <玩家ID> <x> <y> | <玩家ID> to <目标ID> | to <目标ID> ──
        // 把真人或假人挪到坐标、或挪到另一名玩家身边（测试时最常用后者）。
        /// <summary>
        /// 传送命令的用法说明。任何参数错误都返回它 ——
        /// 只报"参数不对"而不给格式，会让人只能靠猜。
        /// </summary>
        private const string TpUsage =
            "用法: hs_tp <玩家ID> <x> <y> → 传到坐标；" +
            "hs_tp <玩家ID> to <目标玩家ID> → 传到某人身边；" +
            "hs_tp to <目标玩家ID> → 省略第一个参数表示操作自己";

        private static string Teleport(string[] args)
        {
            if (args.Length < 2)
                return Error(TpUsage);

            var room = GameRoom.Instance;
            if (room == null)
                return Error("当前没有活动房间");

            // 允许省略"谁"：hs_tp to 5 表示把房主自己传过去
            GamePlayer mover;
            int index;
            if (args[0].Equals("to", StringComparison.OrdinalIgnoreCase))
            {
                mover = FindPlayer(room, 1);
                if (mover == null)
                    return Error("省略玩家ID时默认操作 #1（房主），但没有找到该玩家。\n" + TpUsage);

                index = 0;
            }
            else
            {
                if (!int.TryParse(args[0], out int id))
                    return Error("玩家ID 必须是数字。\n" + TpUsage);

                mover = FindPlayer(room, id);
                if (mover == null)
                    return Error($"没有玩家 #{id}。\n" + TpUsage);

                index = 1;
            }

            PosInfo target;
            string desc;

            if (args[index].Equals("to", StringComparison.OrdinalIgnoreCase))
            {
                if (args.Length <= index + 1 || !int.TryParse(args[index + 1], out int targetId))
                    return Error(TpUsage);

                var other = FindPlayer(room, targetId);
                if (other == null)
                    return Error($"没有玩家 #{targetId}。\n" + TpUsage);

                target = other.PublicInfo.Pos;
                desc = $"#{targetId} 的位置";
            }
            else
            {
                if (args.Length <= index + 1
                    || !float.TryParse(args[index], out float x)
                    || !float.TryParse(args[index + 1], out float y))
                    return Error(TpUsage);

                target = new PosInfo { X = x, Y = y };
                desc = $"({x:F0},{y:F0})";
            }

            if (target == null)
                return Error("目标位置无效");

            int moverId = mover.PublicInfo.PlayerId;

            // force=true 会额外广播 S_RESPAWN；否则客户端只靠后续位置包插值，会"滑"过去
            mover.Move(target, force: true);

            Plugin.Log.LogInfo($"[HS] Teleport：#{moverId} 已传送到 {desc}。");
            return $"{{\"ok\":true,\"id\":{moverId},\"x\":{target.X},\"y\":{target.Y}}}";
        }

        private static GamePlayer FindPlayer(GameRoom room, int id)
            => room?.Players?.FirstOrDefault(p => p?.PublicInfo != null && p.PublicInfo.PlayerId == id);

        // ── /hs_grant [on|off] | exclude <ID...> | exclude clear ─────
        // 切换发刀模式：on = 开局随机发刀并锁死武器架；off = 原版自行跑刀。
        // GiveAtStart 只在 StartSurvive 那一刻被读取，所以改动下一局才生效。
        private static string Grant(string[] args)
        {
            var give = HideAndSeek.Features.Weapon.WeaponGrantFeature.GiveAtStart;
            var block = HideAndSeek.Features.Weapon.WeaponGrantFeature.BlockFurtherWeapons;
            var exclude = HideAndSeek.Features.Weapon.WeaponGrantFeature.ExcludePlayerIds;
            if (give == null)
                return Error("发刀功能未加载");

            // 排除名单：发刀时最高优先级排除这些人（宁可发给假人也不发给他们）
            if (args.Length > 0 && args[0].Equals("exclude", StringComparison.OrdinalIgnoreCase))
            {
                if (exclude == null)
                    return Error("排除名单不可用");

                if (args.Length < 2)
                    return $"{{\"ok\":true,\"exclude\":\"{exclude.Value}\"}}";

                if (args[1].Equals("clear", StringComparison.OrdinalIgnoreCase))
                {
                    exclude.Value = "";
                    return "{\"ok\":true,\"exclude\":\"\",\"note\":\"已清空排除名单\"}";
                }

                var sb = new StringBuilder();
                for (int i = 1; i < args.Length; i++)
                {
                    if (!int.TryParse(args[i], out int id) || id <= 0)
                        return Error("用法: hs_grant exclude <玩家ID...> | hs_grant exclude clear");

                    if (sb.Length > 0) sb.Append(',');
                    sb.Append(id);
                }

                exclude.Value = sb.ToString();
                return $"{{\"ok\":true,\"exclude\":\"{exclude.Value}\"}}";
            }

            if (args.Length == 0)
                return $"{{\"ok\":true,\"autoGrant\":{Bool(give.Value)},\"blockArmory\":{Bool(block?.Value ?? false)},\"exclude\":\"{exclude?.Value}\"}}";

            bool? on = ParseBool(args[0]);
            if (on == null)
                return Error("用法: hs_grant <on|off> | hs_grant exclude <玩家ID...> | hs_grant exclude clear");

            give.Value = on.Value;

            // 自动发刀必须同时锁死武器架，否则第二个人拿到刀就会出现两个黑方
            if (block != null)
                block.Value = on.Value;

            AnnounceRule(on.Value ? "发刀模式 = 开局随机发刀" : "发刀模式 = 自行跑刀");
            return $"{{\"ok\":true,\"autoGrant\":{Bool(give.Value)},\"blockArmory\":{Bool(block?.Value ?? false)},\"exclude\":\"{exclude?.Value}\",\"note\":\"下一局生效\"}}";
        }

        /// <summary>
        /// 广播一条规则调整。由改动玩法的命令在写入配置后调用 ——
        /// 房主单方面改规则时，在场玩家理应知情，否则只能靠察觉异常去猜。
        /// 是否真的发出由 BroadcastFeature.AnnounceRuleChanges 与当前游戏阶段决定。
        /// </summary>
        private static void AnnounceRule(string change)
            => HideAndSeek.Features.Broadcast.BroadcastFeature.AnnounceRule(change);

        // ── /hs_upgrade [vision|speed|task] ─────────────────────────
        // 黑学分：无参查看余额与等级，带方向则升级。
        private static string Upgrade(string[] args)
        {
            var room = Server.Game.GameRoom.Instance;
            if (room == null)
                return Error("不在房间中");

            if (args.Length == 0)
                return "{\"ok\":true,\"status\":\"" +
                       HideAndSeek.Features.Combat.KillUpgradeFeature.Status().Replace("\n", " / ") + "\"}";

            string dir = args[0].ToLowerInvariant();
            int idx;
            switch (dir)
            {
                case "vision": case "视野": idx = HideAndSeek.Features.Combat.KillUpgradeFeature.DirVision; break;
                case "speed":  case "移速": idx = HideAndSeek.Features.Combat.KillUpgradeFeature.DirSpeed; break;
                case "task":   case "任务": idx = HideAndSeek.Features.Combat.KillUpgradeFeature.DirTask; break;
                default: return Error("用法: hs_upgrade <vision|speed|task>");
            }

            if (!HideAndSeek.Features.Combat.KillUpgradeFeature.TryUpgrade(room, idx, out string msg))
                return Error(msg);

            return "{\"ok\":true,\"result\":\"" + msg.Replace("\"", "'") + "\"}";
        }
        // ── /hs_debug <black|exec|list> ... ─────────────────────────
        // 常规游戏不该执行的操作收拢在这里，避免污染正式命令表。
        //   black <玩家ID>              立即把该玩家设为黑方（允许同时多个）
        //   exec  <玩家ID> <命令文本>    以该玩家身份执行一条密聊命令（走真实 Handle 路径）
        //   list                        列出玩家与状态
        private static string Debug(string[] args)
        {
            var room = Server.Game.GameRoom.Instance;
            if (room == null)
                return Error("不在房间中");

            if (args.Length == 0)
                return Error("用法: hs_debug <black|exec|list|credit> ...");

            string sub = args[0].ToLowerInvariant();

            if (sub == "list")
            {
                var parts = new global::System.Collections.Generic.List<string>();
                foreach (var p in room.Players)
                {
                    if (p?.PublicInfo == null)
                        continue;
                    parts.Add($"#{p.PublicInfo.PlayerId} {p.Name} {p.Color} char={p.PublicInfo.CharacterId} alive={p.IsAlive}");
                }
                return "{\"ok\":true,\"players\":[" +
                       string.Join(",", parts.ConvertAll(x => "\"" + x.Replace("\"", "'") + "\"")) + "]}";
            }

            if (args.Length < 2 || !int.TryParse(args[1], out int pid))
                return Error($"用法: hs_debug {sub} <玩家ID> ...");

            Server.Game.Player target = null;
            foreach (var p in room.Players)
            {
                if (p?.PublicInfo != null && p.PublicInfo.PlayerId == pid) { target = p; break; }
            }
            if (target == null)
                return Error($"找不到玩家 #{pid}（用 hs_debug list 查看）");

            if (sub == "credit")
            {
                // hs_debug credit <数量>   直接增减黑学分（调试用）
                if (args.Length < 2 || !float.TryParse(args[1], out float amount))
                    return Error("用法: hs_debug credit <数量>（可为负）");

                HideAndSeek.Features.Combat.KillUpgradeFeature.AddCredits(amount);
                return "{\"ok\":true,\"credit\":" +
                       HideAndSeek.Features.Combat.KillUpgradeFeature.Credits.ToString("F1") + "}";
            }

            if (sub == "black")

            {
                target.Color = EPlayerColor.Black;
                return $"{{\"ok\":true,\"player\":{pid},\"color\":\"{target.Color}\"}}";
            }

            if (sub == "exec")
            {
                if (args.Length < 3)
                    return Error("用法: hs_debug exec <玩家ID> <命令文本>");

                string cmd = string.Join(" ", args, 2, args.Length - 2);
                HideAndSeek.Features.Rule.CommandFeature.ExecForDebug(room, target, cmd);
                return $"{{\"ok\":true,\"player\":{pid},\"exec\":\"{cmd.Replace("\"", "'")}\"}}";
            }

            return Error($"未知子命令 {sub}");
        }
        // ── /hs_radar [on|off] ──────────────────────────────────────
        // 白方全图雷达：白方小地图显示所有存活玩家位置（不区分阵营）。
        // 只能在 Survive 阶段生效 —— 审判阶段下发 S_PIN_MOVE 会让客户端 NRE。
        private static string Radar(string[] args)
        {
            var cfg = HideAndSeek.Features.Vision.WhiteRadarFeature.RadarOn;
            if (cfg == null)
                return Error("雷达功能未加载");

            if (args.Length == 0)
                return $"{{\"ok\":true,\"radar\":{Bool(cfg.Value)}}}";

            bool? on = ParseBool(args[0]);
            if (on == null)
                return Error("用法: hs_radar <on|off>（开启后白方小地图显示所有存活玩家）");

            HideAndSeek.Features.Vision.WhiteRadarFeature.SetActive(on.Value);
            return $"{{\"ok\":true,\"radar\":{Bool(cfg.Value)}}}";
        }
        // ── 小工具 ──────────────────────────────────────────────────
        /// <summary>
        /// 段级 Enabled 不在功能类字段里（由 PatchLoader 生成），
        /// 这里通过该段任一子项反查其 ConfigFile，再读 Enabled 值。
        /// </summary>
        private static void SetFeatureEnabled(Type featureType, bool value)
        {
            var entry = FindSectionEnabled(featureType);
            if (entry != null)
                entry.Value = value;
        }

        private static bool? Enabled(ConfigEntryBase anyEntryInSection)
            => FindSectionEnabled(anyEntryInSection)?.Value;

        private static ConfigEntry<bool> FindSectionEnabled(ConfigEntryBase anyEntryInSection)
            => anyEntryInSection?.ConfigFile != null
               ? FindSectionEnabled(anyEntryInSection.ConfigFile, SectionOf(anyEntryInSection))
               : null;

        private static ConfigEntry<bool> FindSectionEnabled(Type featureType)
        {
            var field = featureType
                .GetFields(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic |
                           System.Reflection.BindingFlags.Static)
                .Select(f => f.GetValue(null))
                .OfType<ConfigEntryBase>()
                .FirstOrDefault();

            return field?.ConfigFile != null
                ? FindSectionEnabled(field.ConfigFile, SectionOf(field))
                : null;
        }

        private static ConfigEntry<bool> FindSectionEnabled(ConfigFile file, string section)
            => file.Keys
                .Where(k => k.Section == section && k.Key == "Enabled")
                .Select(k => file[k] as ConfigEntry<bool>)
                .FirstOrDefault();

        private static string SectionOf(ConfigEntryBase entry)
            => entry?.Definition.Section ?? "";

        private static bool? ParseBool(string s)
        {
            if (s.Equals("on", StringComparison.OrdinalIgnoreCase) ||
                s.Equals("true", StringComparison.OrdinalIgnoreCase) ||
                s == "1")
                return true;
            if (s.Equals("off", StringComparison.OrdinalIgnoreCase) ||
                s.Equals("false", StringComparison.OrdinalIgnoreCase) ||
                s == "0")
                return false;
            return null;
        }

        private static bool TryFloat(string s, out float value)
            => float.TryParse(s, System.Globalization.NumberStyles.Float,
                              System.Globalization.CultureInfo.InvariantCulture, out value);

        private static string Bool(bool? v) => v == true ? "true" : "false";

        private static string Num(ConfigEntry<float> e, float fallback)
            => (e?.Value ?? fallback).ToString(System.Globalization.CultureInfo.InvariantCulture);

        private static string Num(ConfigEntry<int> e, int fallback)
            => (e?.Value ?? fallback).ToString(System.Globalization.CultureInfo.InvariantCulture);

        private static string Error(string message)
            => "{\"ok\":false,\"error\":\"" + message.Replace("\"", "'") + "\"}";
    }
}
