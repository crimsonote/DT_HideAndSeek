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
                    case "hs_check": case "hs_mode": case "hs_aoi": case "hs_cd":
                    case "hs_killlimit": case "hs_dummy": case "hs_flash":
                    case "hs_roomname": case "hs_tp":
                        name = sub;
                        args = args.Skip(1).ToArray();
                        break;
                }
            }
            switch (name)
            {
                case "hs":           return Status();
                case "hs_check":     return Diagnostics.Report();
                case "hs_dummy":     return Dummy(args);
                case "hs_flash":     return Flash(args);
                case "hs_roomname":  return RoomName(args);
                case "hs_mode":      return SetMode(args);
                case "hs_aoi":       return SetAoi(args);
                case "hs_cd":        return SetCooldown(args);
                case "hs_killlimit": return SetKillLimit(args);
                case "hs_tp":        return Teleport(args);
                default:             return Error($"未知命令 {name}（输入 hs 查看总览；另有 hs_check / hs_mode / hs_aoi / hs_cd / hs_killlimit / hs_dummy / hs_flash / hs_roomname / hs_tp）");
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

        // ── /hs_mode on|off ─────────────────────────────────────────
        private static string SetMode(string[] args)
        {
            if (args.Length == 0 || ModeRuntime.Enabled == null)
                return Error("用法: hs_mode <on|off>");

            bool? value = ParseBool(args[0]);
            if (value == null)
                return Error("用法: hs_mode <on|off>");

            ModeRuntime.Enabled.Value = value.Value;
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

                    return $"{{\"ok\":true,\"id\":{actual},\"characterId\":{chara}}}";
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
            if (int.TryParse(text, out charaId) && charaId > 0)
                return true;

            return DummyManager.TryParseCharacterName(text, out charaId);
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
        // 改的是 Steam 大厅元数据里的房间名，即「房间列表里显示的名字」。
        // 无参数时回读大厅里的实际值 —— 客户端界面不展示这条元数据，只能这样验证。
        private static string RoomName(string[] args)
        {
            if (args.Length == 0)
            {
                if (!HideAndSeek.Features.System.RoomNameFeature.Query(out string cur, out string qerr))
                    return Error("读取房间名失败: " + qerr);

                string last = HideAndSeek.Features.System.RoomNameFeature.LastName?.Value ?? "";
                return $"{{\"ok\":true,\"lobbyName\":\"{cur}\",\"lastSet\":\"{last}\"}}";
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
        private static string Teleport(string[] args)
        {
            if (args.Length < 2)
                return Error("用法: hs_tp <玩家ID> <x> <y> | hs_tp <玩家ID> to <目标玩家ID> | hs_tp to <目标玩家ID>（省略=操作自己）");

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
                    return Error("省略玩家ID时默认操作 #1（房主），但没有找到该玩家");

                index = 0;
            }
            else
            {
                if (!int.TryParse(args[0], out int id))
                    return Error("玩家ID 必须是数字");

                mover = FindPlayer(room, id);
                if (mover == null)
                    return Error($"没有玩家 #{id}");

                index = 1;
            }

            PosInfo target;
            string desc;

            if (args[index].Equals("to", StringComparison.OrdinalIgnoreCase))
            {
                if (args.Length <= index + 1 || !int.TryParse(args[index + 1], out int targetId))
                    return Error("用法: hs_tp <玩家ID> to <目标玩家ID>");

                var other = FindPlayer(room, targetId);
                if (other == null)
                    return Error($"没有玩家 #{targetId}");

                target = other.PublicInfo.Pos;
                desc = $"#{targetId} 的位置";
            }
            else
            {
                if (args.Length <= index + 1
                    || !float.TryParse(args[index], out float x)
                    || !float.TryParse(args[index + 1], out float y))
                    return Error("用法: hs_tp <玩家ID> <x> <y>");

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
