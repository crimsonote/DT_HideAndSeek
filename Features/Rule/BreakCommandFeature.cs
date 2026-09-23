using System.Reflection;
using BepInEx.Configuration;
using DummyClient;
using HarmonyLib;
using Protocol;
using Server.Game;
using HideAndSeek.Core;
using GameFusebox = Server.Game.Fusebox;
using GameDeviceManager = Server.Game.DeviceManager;
using GamePlayer = Server.Game.Player;

namespace HideAndSeek.Features.Rule
{
    /// <summary>
    /// 黑方密聊命令通道：发信站密聊里以 "/" 开头的文本按命令处理，否则当普通聊天。
    ///
    /// 为什么走密聊：原版只有 Dark（黑幕）能拆电 —— 客户端 InputInteract（:14583-14607）
    /// 把 Black 的 Q 导流成挥刀，开拆入口 UseSabotage 在客户端；IsDestroyEvidenceTarget /
    /// DeviceType 又是纯客户端本地计算（:7809-7821），无法把电箱伪装成别的设备借道。
    /// 密聊是唯一能承载任意字符串、且服务端已内建「仅 Black/Dark」闸门的通道
    /// （C_CHAT_MESSAGE{SecretChat} → Handle_C_CHAT_MESSAGE :174587，
    ///   RelayDeviceChat :174755 丢弃非 Black/Dark）。
    ///
    /// 四条硬约束：
    ///   1. 状态改动走 GameRoom.Push（JobSerializer，网络线程直接改会竞态）
    ///   2. 不经 DeviceManager.Interact（InteractLock 会与原版紧随的 ChatDevice.Interact 互踩）
    ///   3. 自补前置（MissionType==-1 / Survive / StateList[0]==0 / 颜色），吞包绕过了原版闸门
    ///   4. 类名用 Server.Game.* 别名（与客户端同名类冲突）
    /// </summary>
    [PatchFeature(
        section: "BreakCommand",
        description: "黑方在发信站密聊里用 /break 拆电（原版只有黑幕能拆）；其它 / 开头内容回帮助。",
        defaultEnabled: true,
        side: FeatureSide.Host)]
    internal static class BreakCommandFeature
    {
        [ConfigField(true, "允许黑方用密聊命令 /break [电箱ID] 拆电。")]
        public static ConfigEntry<bool> AllowBreakBySecretChat;

        [ConfigField(30, "拆电命令的冷却秒数（0 = 无冷却）。", Min = 0f, Max = 300f)]
        public static ConfigEntry<int> BreakCooldownSeconds;

        private const string HelpText =
            "可用命令：/break [电箱ID] —— 拆除一个可拆电箱（无参 = 离你最近的一个）";

        private static MethodInfo _disconnectMethod;
        private static float _lastBreakAt = -9999f;

        [HarmonyPatch(typeof(HostPacketHandler), "Handle_C_CHAT_MESSAGE")]
        internal static class ChatMessageHook
        {
            [HarmonyPrefix]
            private static bool Prefix(IPacketSink session, Packet packet)
            {
                if (ModeRuntime.Bypass)
                    return true;
                if (AllowBreakBySecretChat == null || !AllowBreakBySecretChat.Value)
                    return true;

                try
                {
                    var msg = packet?.Pkt as C_CHAT_MESSAGE;
                    if (msg == null || msg.Type != EChatType.SecretChat)
                        return true;

                    string text = (msg.Text ?? "").Trim();
                    if (!TryParseCommand(text, out string cmd, out string arg))
                        return true;                     // 不以 "/" 开头 → 普通聊天

                    var peer = session as HostPeerSession;
                    var player = peer?.Player;
                    if (player == null)
                        return true;

                    // 原版闸门在 RelayDeviceChat；吞包绕过了它，必须自己复刻
                    if (player.Color != EPlayerColor.Black)
                    {
                        Plugin.Log.LogWarning(
                            $"[HS] 密聊命令：非黑方 #{player.PublicInfo?.PlayerId} 试图使用 /{cmd}，已忽略。");
                        return true;
                    }

                    var room = GameRoom.Instance;
                    if (room == null)
                        return true;

                    int deviceId = msg.DeviceId;
                    room.Push(delegate { Handle(room, player, deviceId, cmd, arg); });
                    return false;                        // 吞掉命令，不当聊天广播
                }
                catch (global::System.Exception ex)
                {
                    Plugin.Log.LogWarning($"[HS] 密聊命令：解析失败 — {ex.Message}");
                    return true;
                }
            }
        }

        /// <summary>以 "/" 开头才算命令；返回 false 表示交给原版当聊天处理。</summary>
        private static bool TryParseCommand(string text, out string command, out string argument)
        {
            command = null;
            argument = null;

            if (string.IsNullOrEmpty(text) || text[0] != '/')
                return false;

            string[] parts = text.Substring(1)
                .Split(new[] { ' ', '\t' }, global::System.StringSplitOptions.RemoveEmptyEntries);

            if (parts.Length == 0)
                return false;

            command = parts[0].ToLowerInvariant();
            argument = parts.Length > 1 ? parts[1] : null;
            return true;
        }

        private static void Handle(GameRoom room, GamePlayer player, int deviceId, string command, string argument)
        {
            switch (command)
            {
                case "break":
                    DoBreak(room, player, deviceId, argument);
                    break;

                case "help":
                    Reply(player, deviceId, HelpText);
                    break;

                default:
                    Reply(player, deviceId, $"未知命令 /{command}。{HelpText}");
                    break;
            }
        }

        private static void DoBreak(GameRoom room, GamePlayer player, int deviceId, string argument)
        {
            try
            {
                if (room.State != EGameState.Survive || !player.IsAlive)
                    return;

                int wantId = 0;
                if (!string.IsNullOrEmpty(argument) && !int.TryParse(argument, out wantId))
                {
                    Reply(player, deviceId, $"电箱 ID 必须是数字。{HelpText}");
                    return;
                }

                int cooldown = BreakCooldownSeconds?.Value ?? 0;
                float now = TimeManager.Instance?.SurviveTime ?? 0f;
                if (cooldown > 0 && now - _lastBreakAt < cooldown)
                {
                    Reply(player, deviceId,
                        $"冷却中，还需 {(int)(cooldown - (now - _lastBreakAt)) + 1} 秒。");
                    return;
                }

                var manager = GameDeviceManager.Instance;
                if (manager?.Fuseboxes == null)
                {
                    Reply(player, deviceId, "当前没有可拆的电箱。");
                    return;
                }

                GameFusebox target = null;
                float bestSq = float.MaxValue;
                var selfPos = player.PublicInfo?.Pos;

                foreach (var fusebox in manager.Fuseboxes)
                {
                    var info = fusebox?.DeviceInfo;
                    if (info?.StateList == null || info.StateList.Count == 0)
                        continue;
                    if (info.MissionType != -1)              // 只拆被标记的目标（DisconnetCable 的唯一守卫）
                        continue;
                    if (info.StateList[0] != 0)              // 已断电的不重复拆
                        continue;

                    if (wantId > 0)
                    {
                        if (fusebox.ID == wantId)
                        {
                            target = fusebox;
                            break;
                        }
                        continue;
                    }

                    // 无参：取离黑方最近的一个（Host 有权威坐标）
                    if (selfPos == null || info.Pos == null)
                    {
                        if (target == null)
                            target = fusebox;
                        continue;
                    }

                    float dx = info.Pos.X - selfPos.X;
                    float dy = info.Pos.Y - selfPos.Y;
                    float d = dx * dx + dy * dy;
                    if (d < bestSq)
                    {
                        bestSq = d;
                        target = fusebox;
                    }
                }

                // 地图上没有可拆电箱即不允许拆
                if (target == null)
                {
                    Reply(player, deviceId, wantId > 0
                        ? $"没有可拆的电箱 #{wantId}（可能未派发、已断电或不在本局目标内）。"
                        : "当前没有可拆的电箱（尚未派发或已全部断电）。");
                    return;
                }

                // 反射复用原版拆电：灯光/箭头/音效/线索/OnBlackout 奖励/黑幕通知全走原版。注意拼写少一个 c。
                if (_disconnectMethod == null)
                    _disconnectMethod = AccessTools.Method(typeof(GameFusebox), "DisconnetCable");

                if (_disconnectMethod == null)
                {
                    Plugin.Log.LogWarning("[HS] 密聊命令：找不到 Fusebox.DisconnetCable。");
                    Reply(player, deviceId, "拆电功能当前不可用（内部方法未找到）。");
                    return;
                }

                _disconnectMethod.Invoke(target, new object[] { player });
                _lastBreakAt = now;

                Plugin.Log.LogInfo($"[HS] 密聊命令：黑方 #{player.PublicInfo?.PlayerId} 拆除了电箱 #{target.ID}。");
                Reply(player, deviceId, $"已拆除电箱 #{target.ID}。");
            }
            catch (global::System.Exception ex)
            {
                Plugin.Log.LogWarning($"[HS] 密聊命令：拆电失败 — {ex.Message}");
                Reply(player, deviceId, "拆电失败，请查看房主日志。");
            }
        }

        /// <summary>以密聊形式回执给该玩家（与触发通道一致，客户端会显示在密聊频道）。</summary>
        private static void Reply(GamePlayer player, int deviceId, string text)
        {
            try
            {
                if (player?.Session == null)
                    return;

                player.Session.Send(new S_CHAT_MESSAGE
                {
                    Type = EChatType.SecretChat,
                    Text = text,
                    PlayerId = player.PublicInfo?.PlayerId ?? 0,
                    DeviceId = deviceId,
                    Time = (int)(TimeManager.Instance?.SurviveTime ?? 0f),
                    IsDead = false
                });
            }
            catch (global::System.Exception ex)
            {
                Plugin.Log.LogWarning($"[HS] 密聊命令：回执失败 — {ex.Message}");
            }
        }
    }
}
