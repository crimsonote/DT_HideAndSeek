using System.Collections.Generic;
using BepInEx.Configuration;
using DummyClient;
using HarmonyLib;
using Protocol;
using Server.Game;
using HideAndSeek.Core;
using HideAndSeek.Features.Vision;
using GamePlayer = Server.Game.Player;

namespace HideAndSeek.Features.Rule
{
    /// <summary>
    /// 白方命令通道：白方在**公开聊天**里以 "/" 开头的文本按命令处理。
    ///
    /// 为什么走公开聊天而不是密聊：
    ///   原版密聊（SecretChat）在服务端有硬性颜色闸门（RelayDeviceChat :174755
    ///   丢弃非 Black/Dark），白方根本发不出去；公开聊天（NormalChat）人人可发。
    ///   代价是命令会被全房看见，所以命令包必须 return false 吞掉、不当聊天广播。
    ///
    /// 与 BreakCommandFeature 的关系：
    ///   两者挂在同一个 Handle_C_CHAT_MESSAGE 上，按 Type 分流 ——
    ///   那边管 SecretChat（黑方），这边管 NormalChat（白方）。同方法多补丁共存。
    ///
    /// 已确认的规则：
    ///   ① 黑方（含黑幕）发这些命令 → 直接吞掉，不响应也不回执
    ///   ② 每个白方独立计数与独立冷却（Dictionary 按 PlayerId）
    ///   ③ 开启时在公开聊天发一条**不署名**的公告；不额外给黑方任何弹窗/气泡
    ///   ④ 成功不回私人回执 —— 效果本身就是通知
    ///
    /// 命令的执行效果复用 WhiteRadarFeature：把它的限时改成配置值后 SetActive(true)，
    /// 由雷达自身在超时后关闭（它已经处理了 Survive 阶段限制与清理）。
    /// </summary>
    [PatchFeature(
        section: "WhiteCommand",
        description: "白方公开聊天命令通道（每人独立 CD 与次数；黑方发命令一律吞掉）。",
        defaultEnabled: true,
        side: FeatureSide.Host)]
    internal static class WhiteCommandFeature
    {
        [ConfigField(true, "启用白方公开聊天命令通道。")]
        public static ConfigEntry<bool> EnabledWhiteCommands;

        [ConfigField(2, "每个白方每局可使用 /radar 的次数。", Min = 0f, Max = 20f)]
        public static ConfigEntry<int> RadarUsesPerPlayer;

        [ConfigField(75, "/radar 的冷却秒数（每个白方独立计算）。", Min = 0f, Max = 600f)]
        public static ConfigEntry<int> RadarCooldownSeconds;

        [ConfigField(15, "/radar 单次持续的秒数。", Min = 1f, Max = 300f)]
        public static ConfigEntry<int> RadarDurationSeconds;

        [ConfigField(true, "使用 /radar 时在公开聊天发一条不署名的公告。")]
        public static ConfigEntry<bool> AnnounceOnUse;

        /// <summary>PlayerId → 本局已用次数。</summary>
        private static readonly Dictionary<int, int> Uses = new Dictionary<int, int>();

        /// <summary>PlayerId → 上次使用时的 SurviveTime。</summary>
        private static readonly Dictionary<int, float> LastUse = new Dictionary<int, float>();

        /// <summary>PlayerId → 上次发「扫描冷却中」的时间；每 10 秒最多提示一次。</summary>
        private static readonly Dictionary<int, float> CdNotice = new Dictionary<int, float>();

        /// <summary>/stasis 的生效截止时刻。</summary>
        private static float _stasisUntil;

        /// <summary>时停是否正在生效（供 Move 拦截查询）。</summary>
        internal static bool IsStasisActive
            => _stasisUntil > 0f && (TimeManager.Instance?.SurviveTime ?? 0f) < _stasisUntil;

        /// <summary>/stasis 期间被压到 0 的原始黑方移速倍率。</summary>
        private static float _savedSpeed = -1f;

        private static readonly Dictionary<int, float> CmdLastUse = new Dictionary<int, float>();

        // ── /refresh 刷新身份记录 ────────────────────────────────────
        // 黑白双方都可用：它等价于"完成一个任务"，属于玩法动作而非白方阵营操作。
        [ConfigField(true, "允许用 /refresh 刷新身份记录（黑白双方都可用）。")]
        public static ConfigEntry<bool> AllowRefresh;

        [ConfigField(300, "/refresh 的冷却秒数（每人独立）。", Min = 0f, Max = 3600f)]
        public static ConfigEntry<int> RefreshCooldown;

        [ConfigField(3, "/refresh 视为完成的任务点数档位。原版 MissionData.Point 只有 7/5/4/3/2 五档，" +
            "3 分占 61.3% 为最常见档位。", Min = 0f, Max = 7f)]
        public static ConfigEntry<int> RefreshPoint;

        /// <summary>PlayerId → 上次 /refresh 时的 SurviveTime。</summary>
        private static readonly Dictionary<int, float> RefreshLastUse = new Dictionary<int, float>();

        /// <summary>S_MISSION_CLEAR.ClearedType 用的哨兵值：不在客户端任务文本表里，因此不弹"XXX 已完成"。</summary>
        private const int RefreshClearedType = 999;

        private const float CdNoticeInterval = 10f;

        /// <summary>单条回执最多 3 行（聊天框上限）。</summary>
        private const int MaxLinesPerMessage = 3;

        /// <summary>单行最大显示宽度（半角单位，中文按 2 计，40 = 20 个汉字）。超了客户端会自动折行。</summary>
        private const int MaxWidthPerLine = 40;

        private static List<string> WrapByWidth(string line)
        {
            var result = new List<string>();
            if (string.IsNullOrEmpty(line)) { result.Add(""); return result; }

            var sb = new global::System.Text.StringBuilder();
            int w = 0;
            foreach (char c in line)
            {
                int cw = c > 0x7F ? 2 : 1;
                if (w + cw > MaxWidthPerLine && sb.Length > 0)
                {
                    result.Add(sb.ToString());
                    sb.Clear();
                    w = 0;
                }
                sb.Append(c);
                w += cw;
            }
            if (sb.Length > 0) result.Add(sb.ToString());
            return result;
        }

        [ConfigField(true, "允许白方用 /stasis 消耗任务进度时停黑方。")]
        public static ConfigEntry<bool> AllowStasis;

        [ConfigField(5f, "/stasis 消耗的任务进度百分比。", Min = 0f, Max = 100f)]
        public static ConfigEntry<float> StasisCostPercent;

        [ConfigField(5f, "/stasis 时停黑方的秒数。", Min = 1f, Max = 60f)]
        public static ConfigEntry<int> StasisSeconds;

        [ConfigField(90f, "/stasis 的冷却秒数（每人独立）。", Min = 0f, Max = 600f)]
        public static ConfigEntry<int> StasisCooldown;

        [ConfigField(true, "允许白方用 /repair 消耗任务进度立即恢复供电（仅断电时可用）。")]
        public static ConfigEntry<bool> AllowRepair;

        [ConfigField(10f, "/repair 消耗的任务进度百分比。", Min = 0f, Max = 100f)]
        public static ConfigEntry<float> RepairCostPercent;
        /// <summary>
        /// 白方 /help 文案。每次调用按当前配置生成 ——
        /// 早先写成 const 字符串，数字是死的；改了 .cfg 后帮助里仍是旧值。
        /// </summary>
        private static string BuildWhiteHelp()
        {
            int uses = RadarUsesPerPlayer?.Value ?? 2;
            return "【白方】"
                + $"\n/rad — 全图扫描{RadarDurationSeconds?.Value ?? 15}秒({uses}/{uses})CD{RadarCooldownSeconds?.Value ?? 75}"
                + $"\n/sta — 冻结黑方{StasisSeconds?.Value ?? 5}秒，{(StasisCostPercent?.Value ?? 5f):F0}%任务进度CD{StasisCooldown?.Value ?? 90}"
                + $"\n/rep — 立即恢复供电，{(RepairCostPercent?.Value ?? 10f):F0}%任务进度"
                + "\n/reload — 重新读取配置文件（改了 .cfg 后不必重启）"
                + "\n/refresh — 刷新身份记录";
        }

        [HarmonyPatch(typeof(HostPacketHandler), "Handle_C_CHAT_MESSAGE")]
        internal static class ChatMessageHook
        {
            [HarmonyPrefix]
            private static bool Prefix(IPacketSink session, Packet packet)
            {
                if (ModeRuntime.Bypass)
                    return true;
                if (EnabledWhiteCommands == null || !EnabledWhiteCommands.Value)
                    return true;

                try
                {
                    var msg = packet?.Pkt as C_CHAT_MESSAGE;
                    if (msg == null)
                        return true;

                    // 诊断：确认白方到底发的是哪种 Type（NormalChat / DeviceChat）
                    Plugin.Log.LogInfo($"[HS] WhiteCommand：收到聊天 Type={msg.Type} Text=\"{msg.Text}\"");

                    if (msg.Type != EChatType.NormalChat && msg.Type != EChatType.DeviceChat)
                        return true;                     // 只管公开/设备聊天；密聊交给 BreakCommandFeature

                    string text = (msg.Text ?? "").Trim();
                    if (string.IsNullOrEmpty(text) || text[0] != '/')
                        return true;                     // 不是命令 → 正常聊天

                    string[] parts = text.Substring(1)
                        .Split(new[] { ' ', '\t' }, global::System.StringSplitOptions.RemoveEmptyEntries);
                    if (parts.Length == 0)
                        return true;

                    string name = parts[0].ToLowerInvariant();

                    var peer = session as HostPeerSession;
                    var player = peer?.Player;
                    if (player == null)
                        return true;

                    // 黑方 / 黑幕发命令 → 吞掉，不响应也不回执
                    // refresh 等价于"完成一个任务"，属于玩法动作而非白方阵营操作 → 黑白双方都可用。
                    // 其余命令仍只限白方；黑方/黑幕发出来一律吞掉，不响应也不回执。
                    if (player.Color != EPlayerColor.White && name != "refresh")
                        return false;

                    var room = GameRoom.Instance;
                    if (room == null)
                        return true;

                    int devId = msg.DeviceId;
                    room.Push(delegate { Handle(room, player, name, devId); });
                    return false;                        // 吞掉命令，不当聊天广播
                }
                catch (global::System.Exception ex)
                {
                    Plugin.Log.LogWarning($"[HS] 白方命令：解析失败 — {ex.Message}");
                    return true;
                }
            }
        }

        /// <summary>
        /// /refresh —— 刷新身份记录：按「完成一个 Point 档任务」结算。
        ///
        /// 不走 MissionManager.ClearMission：它有 `ProgressMissionList.FirstOrDefault(Type==mission)`
        /// 前置校验，只接受"当前正在进行的任务"，且完成后必定派下一个任务（会在地图上留下新标注）。
        /// 这里按同一套公式自行结算，并且不派任何任务 —— 因此零地图标注、零平板条目。
        /// 黑白双方都可用：它等价于完成一个任务，属于玩法动作而非白方阵营操作。
        /// </summary>
        private static void DoRefresh(GameRoom room, GamePlayer player, int deviceId)
        {
            if (AllowRefresh == null || !AllowRefresh.Value)
                return;                                  // 与 AllowStasis / AllowRepair 一致：关闭时静默

            if (room == null || player?.PublicInfo == null)
                return;

            if (room.State != EGameState.Survive)
            {
                Reply(player, deviceId, "只能在生存阶段刷新记录。");
                return;
            }
            if (!player.IsAlive)
            {
                Reply(player, deviceId, "已阵亡，无法刷新记录。");
                return;
            }

            int pid = player.PublicInfo.PlayerId;
            float now = TimeManager.Instance?.SurviveTime ?? 0f;
            int cd = RefreshCooldown?.Value ?? 300;

            if (cd > 0 && RefreshLastUse.TryGetValue(pid, out float last) && now - last < cd)
            {
                // 与 /rad 一致的节流，避免连点刷屏
                float lastNotice = CdNotice.TryGetValue(pid, out float ln) ? ln : -9999f;
                if (now - lastNotice >= CdNoticeInterval)
                {
                    CdNotice[pid] = now;
                    Reply(player, deviceId, "刷新间隔过短，暂时不能进行这个操作");
                }
                return;
            }

            if (RefreshRecord(room, player, RefreshPoint?.Value ?? 3))
            {
                RefreshLastUse[pid] = now;               // 只在成功时写，否则次次放行
                Reply(player, deviceId, "记录已刷新");
            }
            else
            {
                Reply(player, deviceId, "任务系统不可用。");
            }
        }

        /// <summary>
        /// 自行结算一次「完成 Point 档任务」的奖励。公式逐行照抄原版 ClearMission（:166690）。
        ///
        /// 顶满处理也照抄原版首行 `if (CurrentPoint >= GoalPoint) return;` ——
        /// 顶满时什么都不做，与真任务完成时完全一致，不做额外提示。
        /// </summary>
        private static bool RefreshRecord(GameRoom room, GamePlayer player, int point)
        {
            try
            {
                var mmType = AccessTools.TypeByName("Server.Game.MissionManager");
                if (mmType == null)
                    return false;

                object inst = mmType.GetProperty("Instance",
                    global::System.Reflection.BindingFlags.Public
                    | global::System.Reflection.BindingFlags.Static)?.GetValue(null);
                if (inst == null)
                    return false;

                var tr = Traverse.Create(inst);
                float cur = tr.Property("CurrentPoint").GetValue<float>();
                float goal = tr.Property("GoalPoint").GetValue<float>();
                if (goal <= 0f)
                    return false;

                // 照抄原版首行：顶满即跳过
                if (cur >= goal)
                    return true;

                float escape = tr.Property("EscapeGaugeWeight").GetValue<float>(1f);
                float timeWeight = tr.Property("TimeLimitIncreaseWeight").GetValue<float>(1f);
                int remain = tr.Property("PublicRemainPlayerCount").GetValue<int>(1);
                if (remain < 1)
                    remain = 1;

                // 进度：num = |Point| * EscapeGaugeWeight * 0.8
                float num = point * escape * 0.8f;
                float after = global::System.Math.Clamp(cur + num, 0f, goal);
                tr.Property("CurrentPoint").SetValue(after);
                int percent = (int)(after / goal * 100f);
                room.Broadcast(new S_MISSION_PROGRESS_PERCENT { Percent = percent });

                // 音效：原版在 ClearMission 里发这一条
                room.BroadcastSystemSFX(ESoundType.SuccessSfx);

                // 时间：num2 = |Point| * (15 / 剩余人数) * TimeLimitIncreaseWeight
                // (15 / remain) 是原版的整数除法，刻意保持一致 —— 否则与真任务的加时量不同。
                float num2 = point * (float)(15 / remain) * timeWeight;
                TimeManager.Instance?.UpdateRemainTime(num2);
                room.Broadcast(new S_MISSION_CLEAR
                {
                    ClearedType = RefreshClearedType,     // 哨兵：客户端文本表里没有它 → 不弹"XXX 已完成"
                    NextType = 0,
                    AddTime = (int)num2,
                    CompleterId = player.PublicInfo.PlayerId
                });

                // 累计分（决定结算奖章）
                AwardManager.Instance?.OnMissionPoint(player.PublicInfo.PlayerId, num);
                return true;
            }
            catch (global::System.Exception ex)
            {
                Plugin.Log.LogWarning($"[HS] /refresh 结算失败 — {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// 重新读取 .cfg。
        ///
        /// 必需：BepInEx **不监听**配置文件变化，ConfigEntry.Value 是启动时读入的内存副本；
        /// 手动编辑 .cfg 后，除非重启游戏或显式 Reload()，代码里读到的仍是旧值 ——
        /// 表现就是"改了配置但 /help、冷却、开关全都没变"。
        /// </summary>
        private static void DoReload(GamePlayer player, int deviceId)
        {
            try
            {
                if (Plugin.HsConfig == null)
                {
                    Reply(player, deviceId, "配置句柄不可用（DT_Tools 未就绪）。");
                    return;
                }

                Plugin.HsConfig.Reload();
                Reply(player, deviceId, "配置已重新读取。");
                Plugin.Log.LogInfo("[HS] 配置已通过 /reload 重新读取。");
            }
            catch (global::System.Exception ex)
            {
                Reply(player, deviceId, "重载失败，详见日志。");
                Plugin.Log.LogWarning($"[HS] /reload 失败 — {ex.Message}");
            }
        }
        private static void Handle(GameRoom room, GamePlayer player, string name, int deviceId)
        {
            switch (name)
            {
                case "help":
                    Reply(player, deviceId, BuildWhiteHelp());
                    break;

                case "rad": case "radar":
                    DoRadar(room, player, deviceId);
                    break;

                case "sta": case "stasis":
                    DoStasis(room, player, deviceId);
                    break;

                case "rep": case "repair":
                    DoRepair(room, player, deviceId);
                    break;

                    case "reload":
                        DoReload(player, deviceId);
                        break;

                    case "refresh":
                        DoRefresh(room, player, deviceId);
                        break;

                default:
                    // 未知命令不公开发言，避免刷屏（只有本人能看到自己的输入被吞）
                    break;
            }
        }

        private static void DoRadar(GameRoom room, GamePlayer player, int deviceId)
        {
            int pid = player.PublicInfo?.PlayerId ?? 0;
            if (pid == 0)
                return;

            if (room.State != EGameState.Survive || !player.IsAlive)
                return;

            int max = RadarUsesPerPlayer?.Value ?? 0;
            int used = Uses.TryGetValue(pid, out int u) ? u : 0;
            int left = max - used;

            if (max <= 0 || used >= max)
            {
                Reply(player, deviceId, $"全图扫描次数已用尽（{used}/{max}）。");
                return;
            }

            float now = TimeManager.Instance?.SurviveTime ?? 0f;
            int cd = RadarCooldownSeconds?.Value ?? 0;
            if (cd > 0 && LastUse.TryGetValue(pid, out float last) && now - last < cd)
            {
                // 冷却提示每 10 秒最多回一次，避免刷屏；仍然带上剩余次数
                float lastNotice = CdNotice.TryGetValue(pid, out float ln) ? ln : -9999f;
                if (now - lastNotice >= CdNoticeInterval)
                {
                    CdNotice[pid] = now;

                    Reply(player, deviceId, "扫描冷却中。");
                }
                return;
            }

            Uses[pid] = used + 1;
            LastUse[pid] = now;          // ← 此前漏写，导致 CD 判据永远拿不到上次时间、次次放行

            // 复用雷达本体：把限时设成本次要求的时长，再开启
            if (WhiteRadarFeature.DurationSeconds != null)
                WhiteRadarFeature.DurationSeconds.Value = RadarDurationSeconds?.Value ?? 15;
            WhiteRadarFeature.SetActive(true);

            int shown = used + 1;
            Reply(player, deviceId, $"(实验性)全图扫描已开启({shown}/{max})。");

            // 不公告：/rad 的开启不对外播报
        }

        /// <summary>
        /// 只发给该玩家的私密回执。
        /// 关键：**按命令的来源通道回** —— 白方多半是在设备（发信机）上发命令，
        /// 那种情况下回执若走 NormalChat，设备界面根本不显示，表现就是"命令没反应"。
        /// </summary>
        /// <summary>扣减任务进度。返回 false 表示不足或读不到进度。</summary>
        /// <summary>
        /// 把当前任务进度广播给全房，驱动客户端进度条。
        /// 正确的包是 S_MISSION_STATE（客户端 Handle_S_MISSION_STATE → MissionMirror.Set）。
        /// 它会**整体覆盖**客户端镜像，所以能读到的任务类型列表要一并带上。
        /// </summary>
        private static void BroadcastMissionState()
        {
            try
            {
                var mmType = AccessTools.TypeByName("Server.Game.MissionManager");
                var inst = mmType == null ? null : AccessTools.PropertyGetter(mmType, "Instance")?.Invoke(null, null);
                if (inst == null)
                    return;

                var curProp = AccessTools.Property(mmType, "CurrentPoint");
                var goalProp = AccessTools.Property(mmType, "GoalPoint");
                if (curProp == null || goalProp == null)
                    return;

                float cur = global::System.Convert.ToSingle(curProp.GetValue(inst));
                float goal = global::System.Convert.ToSingle(goalProp.GetValue(inst));
                if (goal <= 0f)
                    return;

                // 唯一能驱动进度条的包是 S_MISSION_PROGRESS_PERCENT：
                //   客户端 Handle_S_MISSION_PROGRESS_PERCENT(:42889)
                //     → BroadcastSceneEvent(ChangeMissionPercent)
                //     → UI_GameScene.ChangeMissionPercent(:75129) → DOValue
                // 而 S_MISSION_STATE 第一行就是 `if (!Managers.Host.IsHost) return;`，
                // **房主机直接丢弃**（我们就是房主），且它会清空客户端的任务列表镜像。
                GameRoom.Instance?.Broadcast(new Protocol.S_MISSION_PROGRESS_PERCENT
                {
                    Percent = (int)(cur / goal * 100f)
                });
                Plugin.Log.LogInfo($"[HS] WhiteCommand：已广播任务进度 {cur:F0}/{goal:F0} = {(int)(cur / goal * 100f)}%。");
            }
            catch (global::System.Exception ex)
            {
                Plugin.Log.LogWarning($"[HS] WhiteCommand：广播任务进度失败 — {ex.Message}");
            }
        }

        /// <summary>让所有黑方立刻按新的移速倍率重算速度（否则"僵住"不会立即体现）。</summary>
        private static void RefreshAllBlackSpeed(GameRoom room)
        {
            try
            {
                foreach (var p in room.Players)
                {
                    if (p?.PublicInfo == null || p.Color == EPlayerColor.White)
                        continue;
                    p.BuffComponent?.RefreshSpeed();
                }
            }
            catch (global::System.Exception ex)
            {
                Plugin.Log.LogWarning($"[HS] WhiteCommand：刷新黑方速度失败 — {ex.Message}");
            }
        }
        private static bool TrySpendProgress(GameRoom room, float percent, out string why)
        {
            why = null;
            try
            {
                var mmType = AccessTools.TypeByName("Server.Game.MissionManager");
                var inst = mmType == null ? null : AccessTools.PropertyGetter(mmType, "Instance")?.Invoke(null, null);
                if (inst == null) { why = "读不到任务进度"; return false; }

                var curProp = AccessTools.Property(mmType, "CurrentPoint");
                var goalProp = AccessTools.Property(mmType, "GoalPoint");
                if (curProp == null) { why = "读不到任务进度字段"; return false; }

                // CurrentPoint 是 **float** —— 之前按 int 拆箱会抛 InvalidCastException，
// 被 catch 吞成"扣进度失败"。这里统一走 Convert.ToSingle。
                float cur = global::System.Convert.ToSingle(curProp.GetValue(inst));
                if (cur <= 0f) { why = "当前任务进度为 0"; return false; }

                // 关键：百分比的基数是 GoalPoint（任务总量），不是 CurrentPoint（当前值）。
                // 旧写法按当前值算 —— 进度 5/45 时 5% 只有 0.25，被下限抬到 1，
                // 扣完 5→4 在进度条上根本看不出来，用户因此认为"完全没扣"。
                float goal = goalProp == null ? 0f : global::System.Convert.ToSingle(goalProp.GetValue(inst));
                float effective = goal > 0f ? goal : cur;
                float cost = effective * percent / 100f;
                if (cost < 1f) cost = 1f;
                if (cost > cur) cost = cur;          // 不能扣成负数
                if (cur < cost) { why = $"进度不足（现有 {cur:F0}，需要 {cost:F0}）"; return false; }

                curProp.SetValue(inst, cur - cost);

                // 只改服务端字段，客户端的任务进度条不会动 —— 必须广播一次任务状态。
                BroadcastMissionState();

                Plugin.Log.LogInfo($"[HS] WhiteCommand：消耗任务进度 {cost:F0}（{cur:F0} → {cur - cost:F0}）。");
                return true;
            }
            catch (global::System.Exception ex)
            {
                why = "扣进度失败：" + ex.Message;
                return false;
            }
        }

        private static void DoStasis(GameRoom room, GamePlayer player, int deviceId)
        {
            if (AllowStasis == null || !AllowStasis.Value)
                return;
            if (room.State != EGameState.Survive || !player.IsAlive)
                return;

            int pid = player.PublicInfo?.PlayerId ?? 0;
            float now = TimeManager.Instance?.SurviveTime ?? 0f;
            float cd = StasisCooldown?.Value ?? 90f;

            if (CmdLastUse.TryGetValue(pid, out float last) && now - last < cd)
            {
                float lastNotice = CdNotice.TryGetValue(pid, out float ln) ? ln : -9999f;
                if (now - lastNotice >= CdNoticeInterval)
                {
                    CdNotice[pid] = now;
                    Reply(player, deviceId, "冻结冷却中。");
                }
                return;
            }

            if (!TrySpendProgress(room, StasisCostPercent?.Value ?? 5f, out string why))
            {
                Reply(player, deviceId, why);
                return;
            }

            CmdLastUse[pid] = now;

            // 复刻原版 UseTimeStop（:177532）：给黑方施加 TheWorld buff。
            // 客户端 :14917 / :17153 的落地效果是 SkeletonAnim.timeScale = 0f（动画冻结）
            // + PlayGray + _lockControlStack++ + CancelAllInteract —— 这才是真正的时停。
            // BroadcastBuff 内部就是 alivePlayer.BuffComponent.AddBuff(type, duration)，
            // 我们只对**黑方逐个**施加，避免波及白方。
            int stasisMs = (StasisSeconds?.Value ?? 5) * 1000;
            foreach (var p in room.Players)
            {
                if (p?.PublicInfo == null || p.Color == EPlayerColor.White)
                    continue;
                try
                {
                    p.BuffComponent?.AddBuff(EBuffType.TheWorld, stasisMs);
                }
                catch (global::System.Exception ex)
                {
                    Plugin.Log.LogWarning($"[HS] WhiteCommand：施加时停失败 — {ex.Message}");
                }
            }
            room.BroadcastWorldSFX(ESoundType.TheWorldSfx, player.PublicInfo?.Pos);
            Plugin.Log.LogInfo($"[HS] WhiteCommand：时停已施加（TheWorld {stasisMs}ms）。");

            _stasisUntil = now + (StasisSeconds?.Value ?? 5);
            Reply(player, deviceId, $"已冻结黑方 {StasisSeconds?.Value ?? 5} 秒。");
            // 不公开：只有执行者自己知道（回执已发给他），避免向黑方暴露白方动用了消耗手段。
        }

        private static void DoRepair(GameRoom room, GamePlayer player, int deviceId)
        {
            if (AllowRepair == null || !AllowRepair.Value)
                return;
            if (room.State != EGameState.Survive)
                return;

            int broken = 0;
            try { broken = Server.Game.DeviceManager.Instance?.GetDisconnectFuseCount() ?? 0; }
            catch { }

            if (broken <= 0)
            {
                Reply(player, deviceId, "当前没有断电，无需修复。");
                return;
            }

            if (!TrySpendProgress(room, RepairCostPercent?.Value ?? 10f, out string why))
            {
                Reply(player, deviceId, why);
                return;
            }

            int fixedCount = 0;
            try
            {
                // 服务端 Fusebox 的恢复入口是 ConnetCable()（原版拼写少一个 c），
                // 状态字段是 IsLight（true = 通电）。列表在 DeviceManager.Fuseboxes。
                var dm = Server.Game.DeviceManager.Instance;
                foreach (var fb in dm.Fuseboxes)
                {
                    if (fb == null || fb.IsLight)
                        continue;
                    fb.ConnetCable();
                    fixedCount++;
                }
            }
            catch (global::System.Exception ex)
            {
                Plugin.Log.LogWarning($"[HS] WhiteCommand：修电失败 — {ex.Message}");
            }

            Reply(player, deviceId, $"已立即恢复供电（修复 {fixedCount} 处）。");
            // 不公开：只有执行者自己知道（回执已发给他），避免向黑方暴露白方动用了消耗手段。
        }

// MoveFreezeHook 已删除：原版时停走 TheWorld buff（客户端把动画 timeScale 置 0 并锁操作），
        // 不需要、也不应该由我们去拦截服务端 Player.Move。

        /// <summary>时停到点后还原黑方移速。</summary>
        [HarmonyPatch(typeof(GameRoom), "SurvivalTick")]
        internal static class StasisTickHook
        {
            [HarmonyPostfix]
            private static void Postfix(GameRoom __instance)
            {
                if (_stasisUntil <= 0f)
                    return;
                if ((TimeManager.Instance?.SurviveTime ?? 0f) < _stasisUntil)
                    return;

                _stasisUntil = 0f;
                if (_savedSpeed >= 0f && HideAndSeek.Features.Combat.SpeedBoostFeature.BlackSpeedMul != null)
                {
                    HideAndSeek.Features.Combat.SpeedBoostFeature.BlackSpeedMul.Value = _savedSpeed;
                    _savedSpeed = -1f;
                }
                Plugin.Log.LogInfo("[HS] WhiteCommand：时停结束，黑方移速已还原。");
            }
        }
        private static void Reply(GamePlayer player, int deviceId, string text)
        {
            try
            {
                // 先按宽度折行，再每 3 行发一条 —— 否则长行会被客户端自动折行、
// 实际渲染超过 3 行，超出部分被截掉（白方帮助此前就是这样显示不全的）。
                var wrapped = new List<string>();
                foreach (var raw in text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n'))
                    wrapped.AddRange(WrapByWidth(raw));

                for (int start = 0; start < wrapped.Count; start += MaxLinesPerMessage)
                {
                    int count = wrapped.Count - start < MaxLinesPerMessage
                        ? wrapped.Count - start
                        : MaxLinesPerMessage;
                    string chunk = string.Join("\n", wrapped.GetRange(start, count));

                    player.Session.Send(new S_CHAT_MESSAGE
                    {
                        Type = deviceId > 0 ? EChatType.DeviceChat : EChatType.NormalChat,
                        DeviceId = deviceId,
                        Text = chunk,
                        PlayerId = player.PublicInfo?.PlayerId ?? 0,
                        Time = (int)(TimeManager.Instance?.SurviveTime ?? 0f),
                        IsDead = false
                    });
                }
            }
            catch (global::System.Exception ex)
            {
                Plugin.Log.LogWarning($"[HS] 白方命令：回执失败 — {ex.Message}");
            }
        }


        // ── 生命周期：每局重置配额与冷却 ──────────────────────────────
        [HarmonyPatch(typeof(GameRoom), "StartSurvive")]
        internal static class StartHook
        {
            [HarmonyPostfix]
            private static void Postfix()
            {
                Uses.Clear();
            RefreshLastUse.Clear();
                LastUse.Clear();
            CmdLastUse.Clear();   // 修既有 bug：不清会让新局开局就判"冷却中"
            CdNotice.Clear();
            }
        }

        [HarmonyPatch(typeof(GameRoom), "StartLobby")]
        internal static class LobbyHook
        {
            [HarmonyPostfix]
            private static void Postfix()
            {
                Uses.Clear();
            RefreshLastUse.Clear();
                LastUse.Clear();
            CmdLastUse.Clear();   // 修既有 bug：不清会让新局开局就判"冷却中"
            CdNotice.Clear();
            }
        }
    }
}

