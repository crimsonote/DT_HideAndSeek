using System;
using System.Linq;
using System.Text;
using BepInEx.Configuration;
using HideAndSeek.Core;
using HideAndSeek.Features.Combat;
using HideAndSeek.Features.Dummy;
using HideAndSeek.Features.Vision;

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

            switch (name)
            {
                case "hs":           return Status();
                case "hs_check":     return Diagnostics.Report();
                case "hs_dummy":     return Dummy(args);
                case "hs_flash":     return Flash(args);
                case "hs_mode":      return SetMode(args);
                case "hs_aoi":       return SetAoi(args);
                case "hs_cd":        return SetCooldown(args);
                case "hs_killlimit": return SetKillLimit(args);
                default:             return Error($"未知命令 {name}（可用：hs / hs_mode / hs_aoi / hs_cd / hs_killlimit）");
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

        // ── /hs_dummy add [id] [charaId] | del <id> | list | clear ──
        private static string Dummy(string[] args)
        {
            if (args.Length == 0 || args[0].Equals("list", StringComparison.OrdinalIgnoreCase))
                return DummyManager.ListJson();

            switch (args[0].ToLowerInvariant())
            {
                case "add":
                {
                    int id = 0, chara = 0;
                    if (args.Length >= 2 && !int.TryParse(args[1], out id))
                        return Error("用法: hs_dummy add [座位号] [角色ID]");
                    if (args.Length >= 3 && !int.TryParse(args[2], out chara))
                        return Error("用法: hs_dummy add [座位号] [角色ID]");

                    if (!DummyManager.Spawn(id, chara, out int actual, out string err))
                        return Error("生成失败: " + err);

                    return $"{{\"ok\":true,\"id\":{actual}}}";
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
                    return Error("用法: hs_dummy <add [座位号] [角色ID]|del <座位号>|list|clear>");
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
