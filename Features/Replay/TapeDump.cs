using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Protocol;

namespace HideAndSeek.Features.Replay
{
    /// <summary>
    /// 【磁带 dump】—— 把**客户端真实回传的磁带**落成文本文件。
    ///
    /// 为什么值得单独做一个（这是把"验证"从"进游戏"变成"跑脚本"的关键）：
    ///   回放这条链路上最难的部分是"房主手里有什么素材、客户端是怎么裁的"，
    ///   而这两件事**每次都要打一局才能看到**。把磁带落盘之后：
    ///     · 窗口算得对不对、锚点落在哪、时间基准怎么混、帧密度多少 —— 全部可以**离线**核对；
    ///     · 新实现（`Features/Replay/`）的装配逻辑可以用真实样本**离线**跑，不需要实机；
    ///     · 出过的每一局都有凭证，不再是"当时日志里好像是这样"。
    ///
    /// 为什么几乎不花代价：`SnapShot` 是 protobuf 生成的类，
    /// `ToString()` 直接输出**全字段**（含嵌套的 `PublicPlayerInfo` / `MoveSnapShot` …），
    /// 所以不需要手写序列化，也不会漏字段。
    ///
    /// ⚠ 本类**只写文件**，不改任何游戏状态、不发包 —— 零行为影响。
    ///   出问题时删掉 `插件目录/tapedump/` 即可，不影响对局。
    /// </summary>
    internal static class TapeDump
    {
        /// <summary>保留最近几局的 dump（超出就删最老的目录）。</summary>
        private const int KeepRounds = 8;

        private static string _dir;
        private static int _saved;

        /// <summary>本局的 dump 目录；没有就是"没开或还没开始"。</summary>
        public static string CurrentDir => _dir;

        /// <summary>一局开始时调用：新建本局目录并清理过老的。</summary>
        public static void BeginRound()
        {
            try
            {
                _saved = 0;
                string root = Path.Combine(PluginDir(), "tapedump");
                Directory.CreateDirectory(root);
                Rotate(root);

                _dir = Path.Combine(root, DateTime.Now.ToString("MMdd-HHmmss"));
                Directory.CreateDirectory(_dir);
            }
            catch (Exception ex)
            {
                _dir = null;
                Plugin.Log.LogWarning($"[HS] TapeDump：准备目录失败，本次不落盘 — {ex.Message}");
            }
        }

        /// <summary>
        /// 落一段磁带。
        /// </summary>
        /// <param name="recorderId">录制者（谁回传的）。</param>
        /// <param name="key">RecordTime。</param>
        /// <param name="kind">片段种类（拿刀/杀人/最后/自爆/巡礼/黑方）。</param>
        /// <param name="at">登记这一刻的房主时钟（`Clip.At`）。</param>
        /// <param name="before">窗口前秒（`Clip.Before`）。</param>
        /// <param name="after">窗口后秒（`Clip.After`）。</param>
        /// <param name="raw">客户端回传的**原始**帧。</param>
        /// <param name="trimmed">旧实现裁完、实际要广播的帧（没有就传 null）。</param>
        /// <param name="who">旧实现算出的"首帧主视角"描述（没有就传 null）。</param>
        public static void Save(int recorderId, int key, string kind,
            float at, float before, float after,
            List<SnapShot> raw, List<SnapShot> trimmed, string who)
        {
            if (_dir == null)
                return;

            try
            {
                var sb = new StringBuilder(64 * 1024);

                sb.AppendLine("# HideAndSeek 磁带 dump（客户端回传的真实数据）");
                sb.AppendLine($"# 录制者 = #{recorderId}   RecordTime = {key}   片段 = {kind}");
                sb.AppendLine($"# Clip.At = {at:F3}   Before = {before:F3}   After = {after:F3}");
                sb.AppendLine($"# ⇒ 旧实现的窗口（用 At 算）= [{at - before:F3}, {at + after:F3}]");
                sb.AppendLine($"# 原始帧数 = {raw?.Count ?? 0}   重裁后帧数 = {trimmed?.Count ?? 0}");
                if (!string.IsNullOrEmpty(who))
                    sb.AppendLine($"# 旧实现的首帧主视角 = {who}");
                if (raw != null && raw.Count > 0)
                {
                    float lo = float.MaxValue, hi = float.MinValue;
                    foreach (var s in raw)
                    {
                        if (s.TimeStamp < lo) lo = s.TimeStamp;
                        if (s.TimeStamp > hi) hi = s.TimeStamp;
                    }
                    sb.AppendLine($"# 原始时间戳范围 = [{lo:F3}, {hi:F3}]（首帧 {raw[0].TimeStamp:F3}）");
                }
                sb.AppendLine();

                DumpSection(sb, "原始磁带（客户端回传，按原顺序）", raw);
                if (trimmed != null)
                {
                    sb.AppendLine();
                    DumpSection(sb, "重裁后（旧实现实际广播的）", trimmed);
                }

                string file = Path.Combine(_dir,
                    $"{_saved:D2}_{kind}_r{recorderId}_k{key}.txt");
                File.WriteAllText(file, sb.ToString(), new UTF8Encoding(false));
                _saved++;

                Plugin.Log.LogInfo($"[HS] TapeDump：已落盘 {kind}#{key}（录制者 #{recorderId}，"
                    + $"{raw?.Count ?? 0} 帧）→ {file}");
            }
            catch (Exception ex)
            {
                Plugin.Log.LogWarning($"[HS] TapeDump：落盘失败（不影响对局）— {ex.Message}");
            }
        }

        private static void DumpSection(StringBuilder sb, string title, List<SnapShot> shots)
        {
            sb.AppendLine($"## {title}（{shots?.Count ?? 0} 帧）");
            if (shots == null)
                return;

            for (int i = 0; i < shots.Count; i++)
            {
                var s = shots[i];
                if (s == null)
                {
                    sb.AppendLine($"[{i,4}] (null)");
                    continue;
                }

                // ★ protobuf 生成的类，`ToString()` 直接给全字段 JSON —— 不用手写序列化，也不会漏字段。
                sb.AppendLine($"[{i,4}] t={s.TimeStamp,9:F3}  {s.Type}  {s}");
            }
        }

        private static string PluginDir()
        {
            try
            {
                string loc = typeof(Plugin).Assembly.Location;
                if (!string.IsNullOrEmpty(loc))
                    return Path.GetDirectoryName(loc);
            }
            catch
            {
                // 落到下面的兜底
            }
            return ".";
        }

        private static void Rotate(string root)
        {
            try
            {
                var dirs = new DirectoryInfo(root).GetDirectories()
                    .OrderByDescending(d => d.Name)
                    .Skip(KeepRounds)
                    .ToList();
                foreach (var d in dirs)
                    d.Delete(true);
            }
            catch
            {
                // 清理失败不该影响落盘。
            }
        }
    }
}
