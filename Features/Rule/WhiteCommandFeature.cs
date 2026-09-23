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

        /// <summary>/stasis 期间被压到 0 的原始黑方移速倍率。</summary>
        private static float _savedSpeed = -1f;

        private static readonly Dictionary<int, float> CmdLastUse = new Dictionary<int, float>();

        private const float CdNoticeInterval = 10f;

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
            "\n/radar  全图扫描 15s 2次/局 CD75" +
            "\n/stasis 停黑5s 耗5%进度 CD90" +
            "\n/repair 立即修电 耗10%进度（仅断电）";

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

                case "radar":
                    DoRadar(room, player, deviceId);
                    break;

                case "stasis":
                    DoStasis(room, player, deviceId);
                    break;

                case "repair":
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

            // 复用雷达本体：把限时设成本次要求的时长，再开启
            if (WhiteRadarFeature.DurationSeconds != null)
                WhiteRadarFeature.DurationSeconds.Value = RadarDurationSeconds?.Value ?? 15;
            WhiteRadarFeature.SetActive(true);

            int shown = used + 1;
            Reply(player, deviceId, $"(实验性)全图扫描已开启({shown}/{max})。");

            if (AnnounceOnUse == null || AnnounceOnUse.Value)
                SendPublic(room, "瞭望已开启。");
        }

        /// <summary>
        /// 只发给该玩家的私密回执。
        /// 关键：**按命令的来源通道回** —— 白方多半是在设备（发信机）上发命令，
        /// 那种情况下回执若走 NormalChat，设备界面根本不显示，表现就是"命令没反应"。
        /// </summary>
        /// <summary>扣减任务进度。返回 false 表示不足或读不到进度。</summary>
        private static bool TrySpendProgress(GameRoom room, float percent, out string why)
        {
            why = null;
            try
            {
                var mmType = AccessTools.TypeByName("Server.Game.MissionManager");
                var inst = mmType == null ? null : AccessTools.PropertyGetter(mmType, "Instance")?.Invoke(null, null);
                if (inst == null) { why = "读不到任务进度"; return false; }

                var curProp = AccessTools.Property(mmType, "CurrentPoint");
                if (curProp == null) { why = "读不到任务进度字段"; return false; }

                int cur = (int)curProp.GetValue(inst);
                if (cur <= 0) { why = "当前任务进度为 0"; return false; }

                int cost = (int)(cur * percent / 100f);
                if (cost <= 0) cost = 1;
                if (cur < cost) { why = $"进度不足（现有 {cur}，需要 {cost}）"; return false; }

                curProp.SetValue(inst, cur - cost);
                Plugin.Log.LogInfo($"[HS] WhiteCommand：消耗任务进度 {cost}（{cur} → {cur - cost}）。");
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
                    Reply(player, deviceId, "时停冷却中。");
                }
                return;
            }

            if (!TrySpendProgress(room, StasisCostPercent?.Value ?? 5f, out string why))
            {
                Reply(player, deviceId, why);
                return;
            }

            CmdLastUse[pid] = now;

            if (HideAndSeek.Features.Combat.SpeedBoostFeature.BlackSpeedMul != null)
            {
                if (_savedSpeed < 0f)
                    _savedSpeed = HideAndSeek.Features.Combat.SpeedBoostFeature.BlackSpeedMul.Value;
                HideAndSeek.Features.Combat.SpeedBoostFeature.BlackSpeedMul.Value = 0f;
            }

            _stasisUntil = now + (StasisSeconds?.Value ?? 5);
            Reply(player, deviceId, $"已时停黑方 {StasisSeconds?.Value ?? 5} 秒。");
            SendPublic(room, "白方发动了时停。");
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
            SendPublic(room, "白方紧急恢复了供电。");
        }

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
                player?.Session?.Send(new S_CHAT_MESSAGE
                {
                    Type = deviceId > 0 ? EChatType.DeviceChat : EChatType.NormalChat,
                    DeviceId = deviceId,
                    Text = text,
                    PlayerId = player.PublicInfo?.PlayerId ?? 0,
                    Time = (int)(TimeManager.Instance?.SurviveTime ?? 0f),
                    IsDead = false
                });
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
