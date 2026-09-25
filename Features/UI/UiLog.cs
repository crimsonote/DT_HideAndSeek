using BepInEx.Logging;

namespace HideAndSeek.Features.UI
{
    /// <summary>
    /// 设置页签的统一日志出口。前缀 <c>[HS][UI]</c> 便于 <c>Select-String '\[HS\]'</c> 过滤，
    /// 同时明确这是"UI 框架"而不是某个玩法功能打的日志。
    /// 全部方法对 <see cref="HideAndSeek.Plugin.Log"/> 为 null 的情况免疫（BepInEx 初始化顺序）。
    /// </summary>
    internal static class Log
    {
        private const string Prefix = "[HS][UI] ";

        public static void Info(string message) => Write(LogLevel.Info, message);

        public static void Warn(string message) => Write(LogLevel.Warning, message);

        public static void Error(string message) => Write(LogLevel.Error, message);

        private static void Write(LogLevel level, string message)
        {
            ManualLogSource source = HideAndSeek.Plugin.Log;
            if (source == null) return;
            source.Log(level, Prefix + message);
        }
    }
}
