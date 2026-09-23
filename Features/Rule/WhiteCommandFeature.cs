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

        private const string WhiteHelp =
            "白方命令：/radar 开启全图瞭望、/help 查看本列表";

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
                    if (msg == null || msg.Type != EChatType.NormalChat)
                        return true;                     // 只管公开聊天；密聊交给 BreakCommandFeature

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

                    room.Push(delegate { Handle(room, player, name); });
                    return false;                        // 吞掉命令，不当聊天广播
                }
                catch (global::System.Exception ex)
                {
                    Plugin.Log.LogWarning($"[HS] 白方命令：解析失败 — {ex.Message}");
                    return true;
                }
            }
        }

        private static void Handle(GameRoom room, GamePlayer player, string name)
        {
            switch (name)
            {
                case "help":
                    SendPublic(room, WhiteHelp);
                    break;

                case "radar":
                    DoRadar(room, player);
                    break;

                default:
                    // 未知命令不公开发言，避免刷屏（只有本人能看到自己的输入被吞）
                    break;
            }
        }

        private static void DoRadar(GameRoom room, GamePlayer player)
        {
            int pid = player.PublicInfo?.PlayerId ?? 0;
            if (pid == 0)
                return;

            if (room.State != EGameState.Survive || !player.IsAlive)
                return;

            int max = RadarUsesPerPlayer?.Value ?? 0;
            int used = Uses.TryGetValue(pid, out int u) ? u : 0;
            if (max <= 0 || used >= max)
                return;                              // 次数用尽：静默（不留私人回执，避免暴露配额）

            float now = TimeManager.Instance?.SurviveTime ?? 0f;
            int cd = RadarCooldownSeconds?.Value ?? 0;
            if (cd > 0 && LastUse.TryGetValue(pid, out float last) && now - last < cd)
                return;                              // 冷却中：同样静默

            Uses[pid] = used + 1;
            LastUse[pid] = now;

            // 复用雷达本体：把限时设成本次要求的时长，再开启
            if (WhiteRadarFeature.DurationSeconds != null)
                WhiteRadarFeature.DurationSeconds.Value = RadarDurationSeconds?.Value ?? 15;
            WhiteRadarFeature.SetActive(true);

            if (AnnounceOnUse == null || AnnounceOnUse.Value)
                SendPublic(room, "瞭望已开启。");
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
