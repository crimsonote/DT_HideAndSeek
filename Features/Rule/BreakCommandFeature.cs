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
    /// 让黑方通过「发信站 → 密聊 → break」拆电。
    ///
    /// 背景：原版只有 Dark（黑幕）能拆电 —— 客户端 InputInteract（:14583-14607）
    /// 把 Black 的 Q 键导流成挥刀，而开拆入口 UseSabotage 在客户端，
    /// Host 侧无法让 Black 发出 C_INTERACT_FUSEBOX；IsDestroyEvidenceTarget 等
    /// 又是纯客户端本地计算（:7809-7821），也无法把电箱伪装成别的设备借道。
    ///
    /// 唯一能承载任意字符串、且服务端已内建「仅 Black/Dark」闸门的通道是密聊：
    ///   C_CHAT_MESSAGE{Type=SecretChat} → Handle_C_CHAT_MESSAGE（:174587）
    ///   RelayDeviceChat（:174755）对非 Black/Dark 静默丢弃。
    /// 于是约定：在密聊里发 "break" 或 "break &lt;电箱ID&gt;" 即触发拆电。
    ///
    /// 四条硬约束（来自调研）：
    ///   1. 状态改动放进 GameRoom.Push —— GameRoom 是 JobSerializer，网络线程直接改会竞态
    ///   2. 不走 DeviceManager.Interact —— 它置的 InteractLock 会与原版紧随的
    ///      ChatDevice.Interact 互踩，导致黑方占不到发信站、密聊界面打不开
    ///   3. 自己补齐前置校验（MissionType==-1 / Survive / StateList[0]==0 / 颜色）
    ///   4. 类名用 Server.Game.* 别名（服务端与客户端存在同名类）
    /// </summary>
    [PatchFeature(
        section: "BreakCommand",
        description: "黑方在发信站密聊频道输入 break 即可拆电（原版只有黑幕能拆）。",
        defaultEnabled: true,
        side: FeatureSide.Host)]
    internal static class BreakCommandFeature
    {
        [ConfigField(true, "允许黑方用密聊命令 break [电箱ID] 拆电。无参 = 拆离自己最近的可拆电箱。")]
        public static ConfigEntry<bool> AllowBreakBySecretChat;

        private static MethodInfo _disconnectMethod;

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
                        return true;                     // 只认密聊

                    string text = (msg.Text ?? "").Trim();
                    if (!TryParseBreak(text, out int wantId))
                        return true;                     // 不是我们的命令 → 放行给原版

                    var peer = session as HostPeerSession;
                    var player = peer?.Player;
                    if (player == null)
                        return true;

                    // 原版闸门在 RelayDeviceChat(:174755)；我们若吞包就绕过了它，必须自己复刻
                    if (player.Color != EPlayerColor.Black)
                    {
                        Plugin.Log.LogWarning(
                            $"[HS] BreakCommand：非黑方 #{player.PublicInfo?.PlayerId} 试图使用 break，已忽略。");
                        return true;
                    }

                    var room = GameRoom.Instance;
                    if (room == null)
                        return true;

                    room.Push(delegate { DoBreak(room, player, wantId); });
                    return false;                        // 吞掉命令，不当作聊天广播出去
                }
                catch (global::System.Exception ex)
                {
                    Plugin.Log.LogWarning($"[HS] BreakCommand：解析密聊失败 — {ex.Message}");
                    return true;
                }
            }
        }

        /// <summary>整行匹配 "break" 或 "break &lt;数字&gt;"（大小写不敏感）。</summary>
        private static bool TryParseBreak(string text, out int fuseboxId)
        {
            fuseboxId = 0;
            if (string.IsNullOrEmpty(text))
                return false;

            string[] parts = text.Split(new[] { ' ', '\t' }, global::System.StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 0 || !parts[0].Equals("break", global::System.StringComparison.OrdinalIgnoreCase))
                return false;

            if (parts.Length == 1)
                return true;                             // 无参：拆最近一个

            return int.TryParse(parts[1], out fuseboxId) && fuseboxId > 0;
        }

        private static void DoBreak(GameRoom room, GamePlayer player, int wantId)
        {
            try
            {
                if (room.State != EGameState.Survive || !player.IsAlive)
                    return;

                var manager = GameDeviceManager.Instance;
                if (manager?.Fuseboxes == null)
                    return;

                GameFusebox target = null;
                float bestSq = float.MaxValue;
                var selfPos = player.PublicInfo?.Pos;

                foreach (var fusebox in manager.Fuseboxes)
                {
                    var info = fusebox?.DeviceInfo;
                    if (info?.StateList == null || info.StateList.Count == 0)
                        continue;
                    if (info.MissionType != -1)              // 只拆被标记的目标（原版 DisconnetCable 的唯一守卫）
                        continue;
                    if (info.StateList[0] != 0)              // 已经断电的不再重复拆
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

                if (target == null)
                {
                    Plugin.Log.LogInfo("[HS] BreakCommand：当前没有可拆的电箱（未派发或已全部断电）。");
                    return;
                }

                // 反射复用原版拆电：灯光/箭头/音效/线索/OnBlackout 奖励/黑幕通知全走原版逻辑。
                // 注意原版方法名拼写少一个 c。
                if (_disconnectMethod == null)
                    _disconnectMethod = AccessTools.Method(typeof(GameFusebox), "DisconnetCable");

                if (_disconnectMethod == null)
                {
                    Plugin.Log.LogWarning("[HS] BreakCommand：找不到 Fusebox.DisconnetCable。");
                    return;
                }

                _disconnectMethod.Invoke(target, new object[] { player });
                Plugin.Log.LogInfo(
                    $"[HS] BreakCommand：黑方 #{player.PublicInfo?.PlayerId} 经密聊拆除了电箱 #{target.ID}。");
            }
            catch (global::System.Exception ex)
            {
                Plugin.Log.LogWarning($"[HS] BreakCommand：拆电失败 — {ex.Message}");
            }
        }
    }
}
