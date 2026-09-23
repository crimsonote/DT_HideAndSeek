using System.Collections.Generic;
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
    /// 黑方密聊命令通道：发信站密聊里以 "/" 开头的文本按命令处理，其余当普通聊天。
    ///
    /// 命令在配置里注册，**条件 / CD / 次数 / 效果都写在注册项上**，
    /// 任一不满足即拒绝并回执原因 —— 杜绝"无条件给自己无限加强"。
    ///
    /// 为什么走密聊：原版只有 Dark（黑幕）能拆电，客户端 InputInteract（:14583-14607）
    /// 把 Black 的 Q 导流成挥刀，开拆入口 UseSaborage 在客户端；IsDestroyEvidenceTarget /
    /// DeviceType 又是纯客户端本地计算（:7809-7821）。而密聊是唯一能承载任意字符串、
    /// 且服务端已内建「仅 Black/Dark」闸门的通道
    /// （C_CHAT_MESSAGE{SecretChat} → Handle_C_CHAT_MESSAGE :174587；
    ///   RelayDeviceChat :174755 丢弃非 Black/Dark）。
    ///
    /// 命令包会被吞掉（return false），所以**不会**经 Replicator.Secrets(:174060)
    /// 转发给同阵营 —— 黑幕看不到黑方在用命令。回执只发给本人。
    ///
    /// 硬约束：
    ///   1. 状态改动走 GameRoom.Push（JobSerializer，网络线程直接改会竞态）
    ///   2. 拆电不经 DeviceManager.Interact（InteractLock 会与原版紧随的 ChatDevice.Interact 互踩）
    ///   3. 自补前置（MissionType==-1 / Survive / StateList[0]==0 / 颜色），吞包绕过了原版闸门
    ///   4. 类名用 Server.Game.* 别名（与客户端同名类冲突）
    /// </summary>
    [PatchFeature(
        section: "BreakCommand",
        description: "黑方密聊命令通道：命令在配置中注册（含条件/CD/次数/效果），不满足即拒绝。",
        defaultEnabled: true,
        side: FeatureSide.Host)]
    internal static class BreakCommandFeature
    {
        [ConfigField(true, "启用黑方密聊命令通道。关闭后以 / 开头的密聊也会被当作普通聊天。")]
        public static ConfigEntry<bool> AllowBreakBySecretChat;

        [ConfigField("break = fusebox -> Disconnect ; cd=0\n" +
                     "lock  = -> Lock ; cd=60\n" +
                     "tp    = -> Teleport ; cd=60",
            "命令注册表。每条一行，格式：\n" +
            "    <命令名> = <条件> -> <效果> ; cd=<秒> ; uses=<每局次数>\n" +
            "条件可留空（= 无条件）；可用 fusebox（地图上有可拆电箱）\n" +
            "      time<=N（剩余秒）kills>=N（黑方击杀）alive<=N（白方存活），多个用 & 连接。\n" +
            "效果：配置键=值（键同规则引擎：SpeedMul/EnterRange/ExitRange/Cooldown/KillLimit/RepairCount/MinProgress），\n" +
            "      或特殊动作：Disconnect（拆最近可拆电箱）/ Lock（锁住附近的门）/\n" +
            "      Teleport（预警数秒后传送到目标位置，可跟玩家 ID 参数）。多个动作用 , 连接。\n" +
            "cd / uses 可省略，0 或省略 = 不限。行首 # 为注释。\n" +
            "默认的 fusebox 条件已隐含原版派发节奏（断电归零后 60 秒才重新派发目标），通常不必再设 cd。")]
        public static ConfigEntry<string> Commands;

        private sealed class CommandDef
        {
            public string Name;
            public string Condition;
            public string Action;
            public int Cooldown;
            public int MaxUses;
        }

        private static readonly Dictionary<string, int> Uses = new Dictionary<string, int>();
        private static readonly Dictionary<string, float> LastUse = new Dictionary<string, float>();
        private static List<CommandDef> _parsed;
        private static string _parsedFrom;
        private static MethodInfo _disconnectMethod;

        // ── 每局重置计数 ────────────────────────────────────────────
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
                    if (string.IsNullOrEmpty(text) || text[0] != '/')
                        return true;                     // 非命令 → 普通聊天

                    string[] parts = text.Substring(1)
                        .Split(new[] { ' ', '\t' }, global::System.StringSplitOptions.RemoveEmptyEntries);
                    if (parts.Length == 0)
                        return true;

                    string name = parts[0].ToLowerInvariant();
                    string arg = parts.Length > 1 ? parts[1] : null;

                    var peer = session as HostPeerSession;
                    var player = peer?.Player;
                    if (player == null)
                        return true;

                    // 原版闸门在 RelayDeviceChat(:174755)；吞包绕过了它，必须自己复刻
                    if (player.Color != EPlayerColor.Black)
                    {
                        Plugin.Log.LogWarning(
                            $"[HS] 密聊命令：非黑方 #{player.PublicInfo?.PlayerId} 试图使用 /{name}，已忽略。");
                        return true;
                    }

                    var room = GameRoom.Instance;
                    if (room == null)
                        return true;

                    int deviceId = msg.DeviceId;
                    room.Push(delegate { Handle(room, player, deviceId, name, arg); });
                    return false;                        // 吞掉命令，不当聊天广播
                }
                catch (global::System.Exception ex)
                {
                    Plugin.Log.LogWarning($"[HS] 密聊命令：解析失败 — {ex.Message}");
                    return true;
                }
            }
        }

        private static void Handle(GameRoom room, GamePlayer player, int deviceId, string name, string arg)
        {
            var defs = GetCommands();

            if (name == "help")
            {
                Reply(player, deviceId, BuildHelp(defs));
                return;
            }

            CommandDef def = null;
            foreach (var d in defs)
            {
                if (d.Name == name)
                {
                    def = d;
                    break;
                }
            }

            // 内置兜底：配置项只在**新建** .cfg 时写入默认值，老配置里不会有新命令。
            // 让用户为了拿到 /lock、/tp 去手改文件是设计缺陷 —— 这三个命令始终可用。
            if (def == null)
                def = BuiltinCommand(name);

            if (def == null)
            {
                Reply(player, deviceId, $"未知命令 /{name}。\n{BuildHelp(defs)}");
                return;
            }

            Execute(room, player, deviceId, def, arg);
        }

        /// <summary>内置命令：即使配置里没注册也照常工作。</summary>
        private static CommandDef BuiltinCommand(string name)
        {
            switch (name)
            {
                case "break":
                    return new CommandDef { Name = "break", Condition = "fusebox", Action = "Disconnect", Cooldown = 0 };
                case "lock":
                    return new CommandDef { Name = "lock", Condition = "", Action = "Lock", Cooldown = 60 };
                case "list":
                    return new CommandDef { Name = "list", Condition = "", Action = "ListPlayers", Cooldown = 0 };
                case "tp":
                    return new CommandDef { Name = "tp", Condition = "", Action = "Teleport", Cooldown = 60 };
                default:
                    return null;
            }
        }

        /// <summary>依次校验次数 / 冷却 / 条件，任一不过即拒绝并回执原因。</summary>
        private static void Execute(GameRoom room, GamePlayer player, int deviceId, CommandDef def, string arg)
        {
            try
            {
                if (room.State != EGameState.Survive || !player.IsAlive)
                {
                    Reply(player, deviceId, "当前阶段无法使用命令。");
                    return;
                }

                // ① 次数
                int used = Uses.TryGetValue(def.Name, out int u) ? u : 0;
                if (def.MaxUses > 0 && used >= def.MaxUses)
                {
                    Reply(player, deviceId, $"/{def.Name} 本局已用完（上限 {def.MaxUses} 次）。");
                    return;
                }

                // ② 冷却
                float now = TimeManager.Instance?.SurviveTime ?? 0f;
                if (def.Cooldown > 0 && LastUse.TryGetValue(def.Name, out float last)
                    && now - last < def.Cooldown)
                {
                    Reply(player, deviceId,
                        $"/{def.Name} 冷却中，还需 {(int)(def.Cooldown - (now - last)) + 1} 秒。");
                    return;
                }

                // ③ 条件
                if (!string.IsNullOrWhiteSpace(def.Condition)
                    && !RuleRewriteFeature.MatchesAll(def.Condition, room))
                {
                    Reply(player, deviceId, $"/{def.Name} 条件未满足（{def.Condition}）。");
                    return;
                }

                // ④ 效果
                bool special = false;
                foreach (string action in def.Action.Split(','))
                {
                    string a = action.Trim();
                    if (a.Length == 0)
                        continue;

                    if (a.Equals("Lock", global::System.StringComparison.OrdinalIgnoreCase))
                    {
                        special = true;
                        if (!LockNearby(player, deviceId))
                            return;                      // 附近没门就不计次数、不写冷却
                        continue;
                    }

                    if (a.Equals("ListPlayers", global::System.StringComparison.OrdinalIgnoreCase))
                    {
                        special = true;
                        Reply(player, deviceId, BuildPlayerList(room));
                        continue;
                    }

                    if (a.Equals("Teleport", global::System.StringComparison.OrdinalIgnoreCase))
                    {
                        special = true;
                        int wantPid = 0;
                        if (!string.IsNullOrEmpty(arg))
                            int.TryParse(arg, out wantPid);
                        if (!TeleportCommandFeature.Begin(room, player, wantPid, out string tpErr))
                        {
                            Reply(player, deviceId, "传送失败：" + tpErr);
                            return;
                        }
                        continue;
                    }
                    if (a.Equals("Disconnect", global::System.StringComparison.OrdinalIgnoreCase))
                    {
                        special = true;
                        if (!Disconnect(room, player, deviceId, arg))
                            return;                      // 拆不动就不计次数、不写冷却

                        // 原版断电需要**两个**电箱同时被拆（AreaManager.RefreshLight :173493 n>=2）；
                        // 只拆一个不会全黑，与"立即制造断电"的语义不符。
                        // 无参时自动再拆一个凑够阈值（第二次会挑下一个可拆目标）。
                        // 配合 [PowerRepair] RepairCount=1，白方修好任意一个即恢复供电。
                        if (string.IsNullOrEmpty(arg))
                            Disconnect(room, player, deviceId, arg);
                        continue;
                    }

                    int eq = a.IndexOf('=');
                    if (eq <= 0)
                    {
                        Reply(player, deviceId, $"命令配置有误：{a}");
                        return;
                    }

                    string key = a.Substring(0, eq).Trim();
                    string value = a.Substring(eq + 1).Trim();
                    if (!RuleRewriteFeature.SetValue(key, value))
                    {
                        Reply(player, deviceId, $"命令配置有误：{a}");
                        return;
                    }
                    Plugin.Log.LogInfo($"[HS] 密聊命令 /{def.Name}：{key} → {value}");
                }

                Uses[def.Name] = used + 1;
                LastUse[def.Name] = now;

                if (!special)
                    Reply(player, deviceId, $"/{def.Name} 已执行。");
            }
            catch (global::System.Exception ex)
            {
                Plugin.Log.LogWarning($"[HS] 密聊命令 /{def.Name} 执行失败 — {ex.Message}");
                Reply(player, deviceId, $"/{def.Name} 执行失败，请查看房主日志。");
            }
        }

        /// <summary>拆离自己最近的可拆电箱；地图上没有则拒绝。返回是否真的拆了。</summary>
        /// <summary>列出全部玩家（ID + 昵称 + 状态），便于 /tp 指定目标。</summary>
        private static string BuildPlayerList(GameRoom room)
        {
            if (room?.Players == null)
                return "当前没有玩家";

            // 一行并排两个：聊天框每行字符有限，而昵称假定不超过 12 字母 / 6 汉字，
            // 两个并排正好用满一行，条目多时能省一半行数。
            var items = new global::System.Collections.Generic.List<string>();
            foreach (var p in room.Players)
            {
                if (p?.PublicInfo == null)
                    continue;

                var one = new global::System.Text.StringBuilder();
                one.Append('#').Append(p.PublicInfo.PlayerId);
                one.Append(' ').Append(p.Name ?? "?");
                if (p.IsDummy) one.Append("[假人]");
                if (p.IsSpectator) one.Append("[观战]");
                else if (!p.IsAlive) one.Append("[死亡]");

                items.Add(one.ToString());
            }

            // 一行并排两个：聊天框每行字符有限，昵称假定不超过 12 字母 / 6 汉字，
            // 两个并排正好用满一行，条目多时省一半行数。
            var sb = new global::System.Text.StringBuilder("玩家列表（/tp 可用 ID）：");
            for (int i = 0; i < items.Count; i += 2)
            {
                sb.Append('\n').Append(items[i]);
                if (i + 1 < items.Count)
                    sb.Append("  |  ").Append(items[i + 1]);
            }

            return sb.ToString();
        }
        /// <summary>以黑方为圆心锁住附近的门。返回是否真的锁到了门。</summary>
        private static bool LockNearby(GamePlayer player, int deviceId)
        {
            float radius = LockDoorFeature.GetRadius();
            int count = LockDoorFeature.LockAround(player.PublicInfo?.Pos, radius, player);

            if (count == 0)
            {
                Reply(player, deviceId, "附近没有可锁的门。");
                return false;
            }

            Reply(player, deviceId, $"已锁住 {count} 扇门（半径 {radius:F0}）。");
            return true;
        }
        private static bool Disconnect(GameRoom room, GamePlayer player, int deviceId, string arg)
        {
            int wantId = 0;
            if (!string.IsNullOrEmpty(arg) && !int.TryParse(arg, out wantId))
            {
                Reply(player, deviceId, "电箱 ID 必须是数字。");
                return false;
            }

            var manager = GameDeviceManager.Instance;
            if (manager?.Fuseboxes == null)
            {
                Reply(player, deviceId, "当前没有可拆的电箱。");
                return false;
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
                Reply(player, deviceId, wantId > 0
                    ? $"没有可拆的电箱 #{wantId}（可能未派发、已断电或不在本局目标内）。"
                    : "当前没有可拆的电箱（尚未派发或已全部断电）。");
                return false;
            }

            // 反射复用原版拆电：灯光/箭头/音效/线索/OnBlackout 奖励/黑幕通知全走原版。注意拼写少一个 c。
            if (_disconnectMethod == null)
                _disconnectMethod = AccessTools.Method(typeof(GameFusebox), "DisconnetCable");

            if (_disconnectMethod == null)
            {
                Plugin.Log.LogWarning("[HS] 密聊命令：找不到 Fusebox.DisconnetCable。");
                Reply(player, deviceId, "拆电功能当前不可用（内部方法未找到）。");
                return false;
            }

            _disconnectMethod.Invoke(target, new object[] { player });
            Plugin.Log.LogInfo($"[HS] 密聊命令：黑方 #{player.PublicInfo?.PlayerId} 拆除了电箱 #{target.ID}。");
            // 成功不回复：断电本身就有全图黑 + FuseOffSfx + 电箱箭头，文字是噪音
            return true;
        }

        /// <summary>解析命令注册表；配置未变时复用上次结果。</summary>
        private static List<CommandDef> GetCommands()
        {
            string raw = Commands?.Value ?? "";
            if (_parsed != null && _parsedFrom == raw)
                return _parsed;

            var list = new List<CommandDef>();

            foreach (string line in raw.Split(new[] { '\n', '\r' },
                global::System.StringSplitOptions.RemoveEmptyEntries))
            {
                string text = line.Trim();
                if (text.Length == 0 || text.StartsWith("#"))
                    continue;

                var def = ParseLine(text);
                if (def != null)
                    list.Add(def);
            }

            _parsed = list;
            _parsedFrom = raw;
            return list;
        }

        /// <summary>解析一行：name = cond -> action ; cd=N ; uses=N（参数分隔用 ; 或 |）</summary>
        private static CommandDef ParseLine(string text)
        {
            int eq = text.IndexOf('=');
            if (eq <= 0)
                return null;

            var def = new CommandDef { Name = text.Substring(0, eq).Trim().ToLowerInvariant() };
            if (def.Name.Length == 0)
                return null;

            string rest = text.Substring(eq + 1).Trim();

            // 行内切出参数段（cd=/uses=），其余内容原样并入主体
            var body = new global::System.Text.StringBuilder();
            foreach (string piece in rest.Split(new[] { ';', '|' }))
            {
                string p = piece.Trim();
                if (p.Length == 0)
                    continue;

                if (p.StartsWith("cd=", global::System.StringComparison.OrdinalIgnoreCase))
                {
                    if (int.TryParse(p.Substring(3).Trim(), out int cd))
                        def.Cooldown = cd < 0 ? 0 : cd;
                    continue;
                }

                if (p.StartsWith("uses=", global::System.StringComparison.OrdinalIgnoreCase))
                {
                    if (int.TryParse(p.Substring(5).Trim(), out int us))
                        def.MaxUses = us < 0 ? 0 : us;
                    continue;
                }

                if (body.Length > 0)
                    body.Append(' ');
                body.Append(p);
            }

            rest = body.ToString();
            int arrow = rest.IndexOf("->", global::System.StringComparison.Ordinal);
            if (arrow >= 0)
            {
                def.Condition = rest.Substring(0, arrow).Trim();
                def.Action = rest.Substring(arrow + 2).Trim();
            }
            else
            {
                def.Condition = "";
                def.Action = rest.Trim();
            }

            return def.Action.Length == 0 ? null : def;
        }

        /// <summary>
        /// 内置命令的用途与用法说明。配置注册的自定义命令没有这些元信息，
        /// 就退回「名字 + 冷却 + 次数 + 条件」的紧凑格式。
        /// </summary>
        private static string Describe(string name)
        {
            switch (name)
            {
                case "break":
                    return "制造断电。用法：/break [电箱ID]（无参 = 自动选最近的）" +
                           "。条件：电力尚未被破坏";
                case "lock":
                    return "锁住附近的门，白方需绕行。用法：/lock";
                case "tp":
                    return "预警 3 秒后传送到目标处（目标是自己时传送到随机其他玩家）。" +
                           "用法：/tp [玩家ID]（无参 = 随机目标）";
                case "list":
                    return "列出全部玩家的 ID 与昵称，供 /tp 使用。用法：/list";
                default:
                    return null;
            }
        }

        private static string BuildHelp(List<CommandDef> defs)
        {
            var sb = new global::System.Text.StringBuilder("【捉迷藏 · 黑方命令】");
            foreach (var d in defs)
            {
                sb.Append("\n/").Append(d.Name).Append(" — ");

                string desc = Describe(d.Name);
                if (desc != null)
                {
                    sb.Append(desc);
                    if (d.Cooldown > 0)
                        sb.Append("。CD ").Append(d.Cooldown).Append('s');
                    if (d.MaxUses > 0)
                        sb.Append("，每局 ").Append(d.MaxUses).Append(" 次");
                }
                else
                {
                    // 自定义命令：没有元信息，只列已知参数
                    sb.Append("（自定义命令）");
                    if (!string.IsNullOrWhiteSpace(d.Condition))
                        sb.Append(" 条件 ").Append(d.Condition);
                    if (d.Cooldown > 0)
                        sb.Append(" CD ").Append(d.Cooldown).Append('s');
                    if (d.MaxUses > 0)
                        sb.Append(" 每局 ").Append(d.MaxUses).Append(" 次");
                }
            }
            sb.Append("\n/help — 显示本列表");
            return sb.ToString();
        }

        /// <summary>以密聊形式回执给该玩家（与触发通道一致，客户端显示在密聊频道）。</summary>
        /// <summary>游戏内聊天框一次最多显示 3 行，超出的部分必须分段发送。</summary>
        private const int MaxLinesPerMessage = 3;

        private static void Reply(GamePlayer player, int deviceId, string text)
        {
            if (player?.Session == null || string.IsNullOrEmpty(text))
                return;

            string[] lines = text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');

            for (int start = 0; start < lines.Length; start += MaxLinesPerMessage)
            {
                int count = lines.Length - start < MaxLinesPerMessage
                    ? lines.Length - start
                    : MaxLinesPerMessage;

                var sb = new global::System.Text.StringBuilder();
                for (int i = start; i < start + count; i++)
                {
                    if (sb.Length > 0)
                        sb.Append('\n');
                    sb.Append(lines[i]);
                }

                SendChunk(player, deviceId, sb.ToString());
            }
        }

        private static void SendChunk(GamePlayer player, int deviceId, string text)
        {
            try
            {
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
