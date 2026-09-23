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
        private const string WhiteHelp =
            "【白方】" +
            "\n/rad — 全图扫描 15 秒（2 次/局）CD75" +
            "\n/sta — 冻结黑方 5 秒，耗 5% 进度 CD90" +
            "\n/rep — 立即恢复供电，耗 10% 进度（仅断电）";

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
                    if (player.Color != EPlayerColor.White)
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

        private static void Handle(GameRoom room, GamePlayer player, string name, int deviceId)
        {
            switch (name)
            {
                case "help":
                    Reply(player, deviceId, WhiteHelp);
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

            if (AnnounceOnUse == null || AnnounceOnUse.Value)
                SendPublic(room, "扫描已开启。");
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

        /// <summary>向全房发一条公开聊天（不署名）。</summary>
        private static void SendPublic(GameRoom room, string text)
        {
            try
            {
                room?.Broadcast(new S_CHAT_MESSAGE
                {
                    Type = EChatType.NormalChat,
                    Text = text,
                    PlayerId = 0,
                    Time = (int)(TimeManager.Instance?.SurviveTime ?? 0f),
                    IsDead = false
                });
            }
            catch (global::System.Exception ex)
            {
                Plugin.Log.LogWarning($"[HS] 白方命令：公开发言失败 — {ex.Message}");
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
                LastUse.Clear();
            }
        }

        [HarmonyPatch(typeof(GameRoom), "StartLobby")]
        internal static class LobbyHook
        {
            [HarmonyPostfix]
            private static void Postfix()
            {
                Uses.Clear();
                LastUse.Clear();
            }
        }
    }
}

