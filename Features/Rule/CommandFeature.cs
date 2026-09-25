using System.Collections.Generic;
using System.Reflection;
using BepInEx.Configuration;
using DummyClient;
using HarmonyLib;
using Protocol;
using Server.Game;
using HideAndSeek.Core;
using HideAndSeek.Features.Vision;
using GameFusebox = Server.Game.Fusebox;
using GameDeviceManager = Server.Game.DeviceManager;
using GamePlayer = Server.Game.Player;

namespace HideAndSeek.Features.Rule
{
    /// <summary>
    /// 命令系统（全模组唯一实现）：一份注册表 + 三个分层 + 两条频道。
    ///
    /// 分层（side）—— 谁能用：
    ///   any    公共 —— 黑白都能用（/help /refresh）
    ///   white  白方 —— 只有白方（/rad /sta /rep）
    ///   black  黑方 —— 只有黑方（/brk /lck /tp /ls /cre）
    /// 频道（ch）—— 从哪发：
    ///   pub    公开聊天 / 设备聊天（EChatType.NormalChat / DeviceChat）
    ///   secret 密聊（EChatType.SecretChat）
    ///   both   两个频道都收（只有 /help 需要）
    ///
    /// 为什么白方走公开聊天、黑方走密聊：原版密聊在服务端有硬性颜色闸门
    /// （RelayDeviceChat :174755 丢弃非 Black/Dark），白方根本发不出去；而密聊是唯一能承载
    /// 任意字符串、且服务端已内建「仅 Black/Dark」闸门的通道。两条通道的命令包都会被吞掉
    /// （return false），因此**不会**被转发给同阵营 —— 黑幕看不到黑方在用命令，回执只发给本人。
    ///
    /// 校验顺序对所有命令一致：可用开关 → 阶段/存活 → 次数 → 冷却 → 条件 → 效果，
    /// 任一不过即拒绝（并说明原因）。条件复用 RuleRewriteFeature.MatchesAll。
    ///
    /// 自定义命令写在 [Command].Commands 里（格式见该配置项）。内置命令即使配置里没注册
    /// 也始终可用 —— 让用户为了拿到 /lck 去手改文件是设计缺陷。
    ///
    /// 段名 [Command] 是无偏的：此前黑白各有一个段（[BreakCommand] / [WhiteCommand]），
    /// 合并成一份实现之后段名也该只有一份。旧段的值由 ConfigMigration v16 原地搬运，
    /// 因此用户手改过的注册表（brk→break、lck→lock、cd 调过）不会丢。
    ///
    /// 这里只放玩家在游戏内该看到的命令。运维/调试动作（例如重读 .cfg）不放这里，
    /// 走控制台的 hs_* 命令。
    /// </summary>
    [PatchFeature(
        section: "Command",
        description: "命令系统：公共 / 白方 / 黑方三层，注册表可热改。含拆电、锁门、传送、雷达、时停、秒修、刷新等。",
        defaultEnabled: true,
        side: FeatureSide.Host)]
    internal static class CommandFeature
    {
        // ══ 通道开关 ═══════════════════════════════════════════════════

        [ConfigField(true, "启用密聊命令通道（黑方）。关闭后以 / 开头的密聊会被当作普通聊天。")]
        public static ConfigEntry<bool> AllowSecretChat;

        [ConfigField(true, "启用公开聊天命令通道（公共 + 白方）。关闭后公开聊天里的 / 命令不再被吞掉。")]
        public static ConfigEntry<bool> AllowPublicChat;

        [ConfigField("brk = fusebox -> Disconnect ; cd=90\n" +
                     "lck = -> Lock ; cd=60\n" +
                     "tp  = -> Teleport ; cd=60",
            "命令注册表。每条一行，格式：\n" +
            "    <命令名> = <条件> -> <效果> ; cd=<秒> ; uses=<每局次数> ; side=<any|white|black> ; ch=<pub|secret|both> ; alias=<别名,别名> ; per=<room|player>\n" +
            "条件可留空（= 无条件）；可用 fusebox（地图上有可拆电箱）\n" +
            "      time<=N（剩余秒）kills>=N（黑方击杀）alive<=N（白方存活），多个用 & 连接。\n" +
            "效果：配置键=值（键同规则引擎：SpeedMul/EnterRange/ExitRange/Cooldown/KillLimit/RepairCount/MinProgress），\n" +
            "      或特殊动作：Disconnect（拆最近可拆电箱）/ Lock（锁住附近的门）/ Teleport（预警数秒后传送，可跟玩家 ID）/\n" +
            "      ListPlayers / Credit / Help / Refresh / Radar / Stasis / Repair。多个动作用 , 连接。\n" +
            "cd / uses 可省略，0 或省略 = 不限。行首 # 为注释。\n" +
            "side / ch 省略时按黑方密聊处理（与旧格式一致）：side=black、ch=secret。\n" +
            "例（给白方加一条公开频道的命令）：scan = -> Radar ; side=white ; ch=pub ; cd=120\n" +
            "行尾可加 desc=<说明> 覆盖该命令在 /help 里的说明文案（必须写在行尾，其值一路吃到行尾，\n" +
            "因此说明里可以出现 ; | , 等符号）。不写则按动作取 Texts 里的 Desc_<动作名>。\n" +
            "默认的 fusebox 条件已隐含原版派发节奏（断电归零后 60 秒才重新派发目标），通常不必再设 cd。")]
        public static ConfigEntry<string> Commands;

        // 全部默认文案集中在这里；下面的 Texts 配置项直接引用它，因此文案只有一份定义。
        // 用户在 .cfg 里删掉某一行不会让提示变成空白 —— GetTexts 会把用户配置叠加在内置默认之上。
        private const string DefaultTexts =
            // ── 引擎闸门 ──
            "PhaseBlocked = 当前阶段无法使用命令。\n" +
            "UsesExhausted = /{name} 本局已用完（上限 {n} 次）。\n" +
            "Cooldown = /{name} 冷却中，还需 {sec} 秒。\n" +
            "ConditionFailed = /{name} 条件未满足（{cond}）。\n" +
            "Executed = /{name} 已执行。\n" +
            "ExecFailed = /{name} 执行失败，请查看房主日志。\n" +
            "BadAction = 命令配置有误：{a}\n" +
            "UnknownCommand = 未知命令 /{name}。\n" +
            // ── 帮助 ──
            "HelpTitle = 【命令帮助】\n" +
            "GroupAny = 公共\n" +
            "GroupWhite = 白方\n" +
            "GroupBlack = 黑方\n" +
            "HelpCondition = 条件:{cond}\n" +
            // ── 帮助：每条命令的说明，键 = Desc_<动作名小写> ──
            "Desc_help = 查看这份帮助\n" +
            "Desc_refresh = 刷新网络连接 CD{cd}\n" +
            "Desc_radar = 全图扫描{dur}秒({uses}次) CD{cd}\n" +
            "Desc_stasis = 冻结黑方{sec}秒，耗{cost}%任务进度 CD{cd}\n" +
            "Desc_repair = 立即恢复供电，耗{cost}%任务进度\n" +
            "Desc_lock = 锁住附近的门 CD{cd}\n" +
            "Desc_teleport = 3 秒后传送到目标处 [玩家ID] CD{cd}\n" +
            "Desc_disconnect = 破坏电闸 CD{cd}\n" +
            "Desc_credit = 查看/消耗积分升级 v|s|t|help\n" +
            "Desc_listplayers = 列出玩家 ID 与昵称\n" +
            "Desc_givefish = 拿一条鱼\n" +
            "Desc_givesoda = 申领汽水以感受活力\n" +
            // ── 动作回执 ──
            "LockNone = 附近没有可锁的门。\n" +
            "LockDone = 已锁住 {n} 扇门（半径 {radius}）。\n" +
            "DisconnectBadId = 电箱 ID 必须是数字。\n" +
            "DisconnectNoFuseboxes = 当前没有可拆的电箱。\n" +
            "DisconnectNoFuseIdle = 当前没有可拆的电箱（尚未派发或已全部断电）。\n" +
            "DisconnectNoSuchFuse = 没有可拆的电箱 #{id}（可能未派发、已断电或不在本局目标内）。\n" +
            "DisconnectUnavailable = 拆电功能当前不可用（内部方法未找到）。\n" +
            "TeleportFailed = 传送失败：{err}\n" +
            "PlayerListTitle = 玩家列表（/tp 可用 ID）：\n" +
            "PlayerListEmpty = 当前没有玩家\n" +
            "TagDummy = [假人]\n" +
            "TagSpectator = [观战]\n" +
            "TagDead = [死亡]\n" +
            "CreditUsage = 用法 /cre v|s|t|help\n" +
            "RadarOn = (实验性)全图扫描已开启({n}/{uses})。\n" +
            "StasisDone = 已冻结黑方 {sec} 秒。\n" +
            "RepairNoOutage = 当前没有断电，无需修复。\n" +
            "RepairDone = 已立即恢复供电（修复 {n} 处）。\n" +
            "RefreshPending = 网络刷新中\n" +
            "RefreshDone = [刷新完成]\n" +
            "RefreshFailed = 网络连接异常，刷新失败。\n" +
            "SpendNoProgress = 当前任务进度为 0\n" +
            "SpendInsufficient = 进度不足（现有 {x}，需要 {y}）\n" +
            "SpendUnavailable = 读不到任务进度\n" +
            // ── 鱼（KeyLockFeature 复用本表；同一张表才能统一热改）──
            // 只保留"命令执行结果"类的文案：打 /fish 会看到什么。
            // 门被黏住 / 鱼被消耗 / 胶未干 / 两把锁咬死 这些**不是命令结果**，一律静默。
            "FishTaken = 一条普通的鱼，或许可以把门黏住\n" +
            "FishEmpty = 鱼已售罄，请稍候重试。\n" +
            "FishTooEarly = 非售货时间，请稍候再来\n" +
            "FishCooldown = 超出限额，请稍候重试\n" +
            // ── 汽水（SodaBoostFeature 复用本表）──
            // 同理：只留 /soda 的结果。喝下汽水、上一罐还没过 这些**不是命令结果**，静默。
            "SodaTaken = 汽水申领成功\n" +
            "SodaHowTo = 按使用键喝掉：{sec} 秒内移速提升到 {mul}%\n" +
            "SodaQuota = 汽水申领超过配额\n" +
            "SodaCooldown = 你暂时不能申领第二瓶汽水";

        [ConfigField(DefaultTexts,
            "命令对玩家显示的全部文案。格式：每条一行 `<键> = <文本>`，行首 # 为注释。\n" +
            "占位符（按出现的键自动填充）：{name} 命令名 / {n} 数量 / {uses} 次数上限 / {sec} 秒数 /\n" +
            "      {cd} 冷却 / {cond} 条件 / {dur} 雷达秒数 / {cost} 进度百分比 / {radius} 半径 /\n" +
            "      {id} 电箱 ID / {x} {y} 进度数值 / {a} 出错的动作串 / {err} 失败原因\n" +
            "把某条的值留空 = 该提示不再发送（例如不想看到\"已执行\"就写 `Executed = `）。\n" +
            "删掉整行 = 回到内置默认文案。\n" +
            "每条命令自己的帮助说明在 Commands 里用行尾 desc= 覆盖（见该配置项说明）。")]
        public static ConfigEntry<string> Texts;

        // ══ 白方命令的运行参数 ═════════════════════════════════════════
        // 冷却与次数不在这里 —— 它们和黑方一样写在注册表行上（cd= / uses=），
        // 内置命令的默认值见 BuiltinCommand。

        [ConfigField(2, "/rad 每局可用次数（每人独立）。", Min = 0f, Max = 20f)]
        public static ConfigEntry<int> RadarUsesPerPlayer;

        [ConfigField(75, "/rad 的冷却秒数（每人独立）。", Min = 0f, Max = 600f)]
        public static ConfigEntry<int> RadarCooldownSeconds;

        [ConfigField(15, "/rad 单次持续的秒数。", Min = 1f, Max = 300f)]
        public static ConfigEntry<int> RadarDurationSeconds;

        [ConfigField(true, "允许白方用 /sta 消耗任务进度时停黑方。")]
        public static ConfigEntry<bool> AllowStasis;

        [ConfigField(5f, "/sta 消耗的任务进度百分比。", Min = 0f, Max = 100f)]
        public static ConfigEntry<float> StasisCostPercent;

        [ConfigField(5f, "/sta 时停黑方的秒数。", Min = 1f, Max = 60f)]
        public static ConfigEntry<int> StasisSeconds;

        [ConfigField(90f, "/sta 的冷却秒数（每人独立）。", Min = 0f, Max = 600f)]
        public static ConfigEntry<int> StasisCooldown;

        [ConfigField(896f, "/sta 时停音效的半径（游戏单位，224 = 1 格）。" +
            "以**被冻结的黑方**为圆心广播；默认 896 = 4 格。", Min = 0f, Max = 10000f)]
        public static ConfigEntry<float> StasisSfxRange;

        [ConfigField(true, "允许白方用 /rep 消耗任务进度立即恢复供电（仅断电时可用）。")]
        public static ConfigEntry<bool> AllowRepair;

        [ConfigField(10f, "/rep 消耗的任务进度百分比。", Min = 0f, Max = 100f)]
        public static ConfigEntry<float> RepairCostPercent;

        // ══ /refresh ══════════════════════════════════════════════════

        [ConfigField(true, "允许用 /refresh 刷新网络连接（黑白双方都可用）。")]
        public static ConfigEntry<bool> AllowRefresh;

        [ConfigField(300, "/refresh 的冷却秒数（每人独立）。", Min = 0f, Max = 3600f)]
        public static ConfigEntry<int> RefreshCooldown;

        [ConfigField(3, "/refresh 的奖励点数（原版 MissionData.Point）。原版真任务只有 7/5/4/3/2 五档，" +
            "3 分占 61.3% 为最常见档位。它直接决定进度增量 |Point|×EscapeGaugeWeight×0.8 与加时量，" +
            "0 = 只推进任务链不给奖励。", Min = 0f, Max = 20f)]
        public static ConfigEntry<int> RefreshPoint;

        [ConfigField(5000, "/refresh 的联网耗时（毫秒）。发起时回\"网络刷新中\"，延迟结束后结算并回\"[刷新完成]\"。",
            Min = 0f, Max = 30000f)]
        public static ConfigEntry<int> RefreshDelayMs;

        [ConfigField(17, "弹窗用哪个任务的名称（ESchoolMission 枚举值）。默认 17 = ScFixPc 网络系统维护。" +
            "客户端弹窗文本取自本地化表，Host 端无法自定义，只能借用某个真实任务名。",
            Min = 0f, Max = 60f)]
        public static ConfigEntry<int> RefreshPopupType;

        [ConfigField(true, "为 /refresh 弹任务完成弹窗。关掉则只有全房 +N SEC 浮字与进度条变化。")]
        public static ConfigEntry<bool> RefreshPopup;

        // ══ 命令模型 ═══════════════════════════════════════════════════

        internal enum CommandSide { Any, White, Black }

        internal enum CommandChannel { Public, Secret, Both }

        private sealed class CommandDef
        {
            public string Name;
            public string[] Aliases = new string[0];
            public CommandSide Side = CommandSide.Black;
            public CommandChannel Channel = CommandChannel.Secret;
            public string Condition = "";
            public string Action = "";

            /// <summary>注册表行尾 desc= 给出的帮助说明；为空则按动作查 Texts 的 Desc_&lt;动作&gt;。</summary>
            public string Desc;
            public int Cooldown;
            public int MaxUses;

            /// <summary>true = 每人独立计数与冷却；false = 全房共用（原版黑方命令的语义）。</summary>
            public bool UsesPerPlayer;

            /// <summary>
            /// true = 不受「必须在生存阶段且存活」这道闸门约束。
            /// 只有 /help 需要：它在旧实现里走的是 Handle 的 switch，从来没有阶段检查 ——
            /// 这里是保留原行为，而不是新增一个闸门再豁免它。
            /// </summary>
            public bool AllowOutsideSurvive;

            /// <summary>
            /// 阶段/存活不满足时保持静默（不回执）。
            /// 白方命令（/rad /sta /rep）是旧实现里就静默的那几条 —— 非生存阶段客户端不处理
            /// 这类消息，发了也是冗余；黑方密聊命令与 /refresh 在旧实现里本来就回执，故默认 false。
            /// </summary>
            public bool QuietWhenBlocked;

            /// <summary>该命令当前是否可用（由功能开关决定）。null = 始终可用。</summary>
            public global::System.Func<bool> IsAvailable;
        }

        /// <summary>命令名 → 本局已用次数 / 上次使用时的 SurviveTime。键见 <see cref="CmdKey"/>。</summary>
        private static readonly Dictionary<string, int> Uses = new Dictionary<string, int>();
        private static readonly Dictionary<string, float> LastUse = new Dictionary<string, float>();

        /// <summary>
        /// 「冷却中」提示的节流键 → 上次提示时间。每人每条命令 10 秒最多提示一次，防连点刷屏。
        ///
        /// 键必须**带上命令名**（用 CmdKey 的形态）：以前只按 PlayerId 记，
        /// 于是 10 秒内换一条命令（先 /rad 再 /sta）时，第二条命令的冷却提示会被一起吞掉 ——
        /// 玩家只看到"命令没反应"，看不到原因。
        /// </summary>
        private static readonly Dictionary<string, float> CdNotice = new Dictionary<string, float>();

        private const float CdNoticeInterval = 10f;

        /// <summary>单条回执最多 3 行（聊天框上限）。</summary>
        private const int MaxLinesPerMessage = 3;

        /// <summary>单行最大显示宽度（半角单位，中文按 2 计，40 = 20 个汉字）。超了客户端会自动折行。</summary>
        private const int MaxWidthPerLine = 40;

        private static List<CommandDef> _parsed;
        private static string _parsedFrom;
        private static MethodInfo _disconnectMethod;

        private static string CmdKey(CommandDef def, int playerId)
            => def.UsesPerPlayer ? playerId + ":" + def.Name : def.Name;

        // ══ 入口 ══════════════════════════════════════════════════════

        /// <summary>
        /// 两条频道共用一个钩子：按 EChatType 分流。
        /// 密聊 → 只认 ch=secret/both 的命令；公开/设备聊天 → 只认 ch=pub/both。
        /// </summary>
        [HarmonyPatch(typeof(HostPacketHandler), "Handle_C_CHAT_MESSAGE")]
        internal static class ChatMessageHook
        {
            [HarmonyPrefix]
            private static bool Prefix(IPacketSink session, Packet packet)
            {
                Diagnostics.Hit("Command");
                if (ModeRuntime.Bypass)
                    return true;

                try
                {
                    var msg = packet?.Pkt as C_CHAT_MESSAGE;
                    if (msg == null)
                        return true;

                    CommandChannel channel;
                    if (msg.Type == EChatType.SecretChat)
                    {
                        if (AllowSecretChat == null || !AllowSecretChat.Value)
                            return true;
                        channel = CommandChannel.Secret;
                    }
                    else if (msg.Type == EChatType.NormalChat || msg.Type == EChatType.DeviceChat)
                    {
                        if (AllowPublicChat == null || !AllowPublicChat.Value)
                            return true;
                        channel = CommandChannel.Public;
                    }
                    else
                    {
                        return true;
                    }

                    string text = (msg.Text ?? "").Trim();
                    if (string.IsNullOrEmpty(text) || text[0] != '/')
                        return true;                     // 不是命令 → 正常聊天

                    string[] parts = text.Substring(1)
                        .Split(new[] { ' ', '\t' }, global::System.StringSplitOptions.RemoveEmptyEntries);
                    if (parts.Length == 0)
                        return true;

                    string name = parts[0].ToLowerInvariant();
                    string arg = parts.Length > 1
                        ? string.Join(" ", parts, 1, parts.Length - 1)
                        : "";

                    var peer = session as HostPeerSession;
                    var player = peer?.Player;
                    if (player == null)
                        return true;

                    // 密聊的原始闸门在 RelayDeviceChat(:174755)；吞包绕过了它，必须自己复刻
                    if (channel == CommandChannel.Secret && player.Color != EPlayerColor.Black)
                    {
                        Plugin.Log.LogWarning(
                            $"[HS] 密聊命令：非黑方 #{player.PublicInfo?.PlayerId} 试图使用 /{name}，已忽略。");
                        return true;
                    }

                    var room = GameRoom.Instance;
                    if (room == null)
                        return true;

                    int deviceId = msg.DeviceId;
                    room.Push(delegate { Handle(room, player, name, arg, deviceId, channel); });
                    return false;                        // 吞掉命令，不当聊天广播
                }
                catch (global::System.Exception ex)
                {
                    Plugin.Log.LogWarning($"[HS] 命令：解析失败 — {ex.Message}");
                    return true;
                }
            }
        }

        /// <summary>每局重置计数与冷却，否则新局开局就判"冷却中"。</summary>
        [HarmonyPatch(typeof(GameRoom), "StartSurvive")]
        internal static class StartHook
        {
            [HarmonyPostfix]
            private static void Postfix() => Reset();
        }

        [HarmonyPatch(typeof(GameRoom), "StartLobby")]
        internal static class LobbyHook
        {
            [HarmonyPostfix]
            private static void Postfix() => Reset();
        }

        private static void Reset()
        {
            Uses.Clear();
            LastUse.Clear();
            CdNotice.Clear();
        }

        /// <summary>
        /// 调试入口（hs_debug exec 用）：以指定玩家身份执行一条密聊命令。
        /// 走与真实密聊完全相同的 Handle 路径，因此开关 / 条件 / CD / 次数都会真实触发。
        /// </summary>
        internal static void ExecForDebug(GameRoom room, GamePlayer player, string text)
        {
            if (room == null || player == null || string.IsNullOrWhiteSpace(text))
                return;

            string body = text.Trim();
            if (body.StartsWith("/"))
                body = body.Substring(1);

            string[] parts = body.Split(new[] { ' ', '\t' }, global::System.StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 0)
                return;

            string name = parts[0].ToLowerInvariant();
            string arg = parts.Length > 1
                ? string.Join(" ", parts, 1, parts.Length - 1)
                : "";

            Plugin.Log.LogInfo($"[HS] Debug：以玩家 #{player.PublicInfo?.PlayerId} 身份执行 /{name} {arg}");
            Handle(room, player, name, arg, 0, CommandChannel.Secret);
        }

        // ══ 解析与分发 ════════════════════════════════════════════════

        private static void Handle(GameRoom room, GamePlayer player, string name,
                                   string arg, int deviceId, CommandChannel channel)
        {
            var def = Resolve(name, channel, player);

            if (def == null)
            {
                // 公开频道对未知命令保持静默 —— 那是"打了个 / 开头的普通聊天"，
                // 回复等于给所有人一个探测命令表的手段。密聊则给出提示，方便黑方自己纠错。
                if (channel == CommandChannel.Secret)
                    Reply(player, deviceId, channel,
                        T("UnknownCommand", "name", name) + "\n" + BuildHelp(player, channel));
                return;
            }

            // 不要把 help 在这里特判掉。
            //
            // 以前这里是 `if (PrimaryAction(def.Action) == "help") { 回帮助; return; }`，两个后果：
            //   ① `help` 靠"被特判绕过 Execute"才能在大厅里用，于是 CommandDef.AllowOutsideSurvive
            //      这个字段**从未真正生效过** —— 阶段闸门在 Execute 里，特判根本走不到它；
            //   ② `Action = "Help,Lock"` 这种多动作写法会被整体当成 help，**后面的动作被吞掉**。
            // 现在统一走 Execute：help 由 RunAction 的 "help" 分支处理，
            // 它自己的 AllowOutsideSurvive = true 会正确地放行阶段闸门。
            Execute(room, player, deviceId, channel, def, arg);
        }

        /// <summary>
        /// 按「频道 + 分层」找出该玩家此刻能用的命令定义。
        /// 注册表优先于内置命令 —— 用户可以在注册表里覆盖任何内置命令的参数。
        /// 分层不匹配（白方用黑方命令等）一律返回 null，由调用方静默吞掉：不响应也不回执。
        /// </summary>
        private static CommandDef Resolve(string name, CommandChannel channel, GamePlayer player)
        {
            // 先用内置别名把长名归一成主名，再查注册表 —— 否则会出现这种不对称：
            // 注册表写 `brk = ... ; uses=2` 时，`/brk` 命中注册表，而 `/break` 在注册表里
            // 查不到（那一行没写 alias=），于是落到内置 —— 同一个命令两个名字两套参数，
            // 而且 uses= 被绕过。旧实现是在查表**之前**做 Normalize(name)，这里保持一致。
            string canonical = CanonicalName(name);

            var def = FindIn(GetCommands(), name, channel, player)
                   ?? FindIn(GetCommands(), canonical, channel, player)
                   ?? FindIn(Builtins(), name, channel, player)
                   ?? FindIn(Builtins(), canonical, channel, player);

            if (def == null)
                return null;

            // 命令自带的可用开关（例如 AllowStasis 关掉后 /sta 直接不存在）
            if (def.IsAvailable != null && !def.IsAvailable())
                return null;

            return def;
        }

        /// <summary>用内置命令的别名表把长名归一成主名（break→brk、lock→lck、list→ls…）；不是别名则原样返回。</summary>
        private static string CanonicalName(string name)
        {
            foreach (var def in Builtins())
            {
                foreach (var alias in def.Aliases)
                {
                    if (alias == name)
                        return def.Name;
                }
            }
            return name;
        }

        private static CommandDef FindIn(List<CommandDef> defs, string name,
                                         CommandChannel channel, GamePlayer player)
        {
            foreach (var def in defs)
            {
                if (!SameCommand(def, name))
                    continue;
                if (def.Channel != channel && def.Channel != CommandChannel.Both)
                    continue;
                if (!SideAllows(def.Side, player))
                    continue;
                return def;
            }
            return null;
        }

        private static bool MatchesName(CommandDef def, string name)
        {
            if (def.Name == name)
                return true;
            foreach (var alias in def.Aliases)
            {
                if (alias == name)
                    return true;
            }
            return false;
        }

        /// <summary>
        /// def 与玩家输入的名字是否指向**同一个命令**。
        ///
        /// 除了字面量比较，还要跨到内置表判一次：注册表里可能写的是内置命令的**别名**
        /// （例如注册表写 `break = ...`，而 `break` 是内置 `brk` 的别名），
        /// 玩家却可能打主名 `/brk`。只比字面量的话，`/break` 命中注册表、`/brk` 落到内置，
        /// 同一个命令两个名字两套参数（实测就是 CD 0 与 CD 90 的差别）。
        ///
        /// 判据：把 def 的名字与 name **都归一到内置主名**再比；两边都不属于任何内置命令时，
        /// 归一结果等于原值，于是退回纯字面量比较（自定义命令名不受影响）。
        /// </summary>
        private static bool SameCommand(CommandDef def, string name)
        {
            if (MatchesName(def, name))
                return true;

            string a = CanonicalName(def.Name);
            string b = CanonicalName(name);

            // a == b 且确实发生过归一（否则就是两个不相干的自定义名，交给上面的字面量比较）
            return a == b && (a != def.Name || b != name);
        }

        private static bool SideAllows(CommandSide side, GamePlayer player)
        {
            switch (side)
            {
                case CommandSide.Any:
                    return true;
                case CommandSide.White:
                    return player.Color == EPlayerColor.White;
                case CommandSide.Black:
                    return player.Color == EPlayerColor.Black;
                default:
                    return false;
            }
        }

        /// <summary>依次校验阶段 / 次数 / 冷却 / 条件，任一不过即拒绝并回执原因。</summary>
        private static void Execute(GameRoom room, GamePlayer player, int deviceId,
                                    CommandChannel channel, CommandDef def, string arg)
        {
            try
            {
                if (!def.AllowOutsideSurvive
                    && (room.State != EGameState.Survive || !player.IsAlive))
                {
                    // 白方命令静默：非生存阶段客户端不处理这类消息，发了也是冗余。
                    if (!def.QuietWhenBlocked)
                        Reply(player, deviceId, channel, T("PhaseBlocked"));
                    return;
                }

                int pid = player.PublicInfo?.PlayerId ?? 0;
                string key = CmdKey(def, pid);

                // 记下"引擎写入之前"的冷却状态，供延迟结算型命令在失败时回滚
                // （见 DoRefresh / RollbackRefreshCount）。必须在 :620 写 LastUse 之前取。
                bool hadLast = LastUse.TryGetValue(key, out float prevLast);

                int used = Uses.TryGetValue(key, out int u) ? u : 0;
                if (def.MaxUses > 0 && used >= def.MaxUses)
                {
                    Reply(player, deviceId, channel, T("UsesExhausted", "name", def.Name, "n", def.MaxUses.ToString()));
                    return;
                }

                float now = TimeManager.Instance?.SurviveTime ?? 0f;
                if (def.Cooldown > 0 && LastUse.TryGetValue(key, out float last)
                    && now - last < def.Cooldown)
                {
                    // 只有"每人独立"的命令才做提示节流：那是会被连点的白方命令；
                    // 黑方命令全房共用一个冷却，连点的人本来就该看到剩余秒数。
                    if (def.UsesPerPlayer)
                    {
                        // 键用 key（带命令名），不要用 pid —— 同一人在 10 秒内换一条命令时，
                        // 新命令的冷却提示不该被旧命令的节流吞掉。
                        float lastNotice = CdNotice.TryGetValue(key, out float ln) ? ln : -9999f;
                        if (now - lastNotice < CdNoticeInterval)
                            return;
                        CdNotice[key] = now;
                    }

                    Reply(player, deviceId, channel,
                        T("Cooldown", "name", def.Name,
                            "sec", (((int)(def.Cooldown - (now - last))) + 1).ToString()));
                    return;
                }

                if (!string.IsNullOrWhiteSpace(def.Condition)
                    && !RuleRewriteFeature.MatchesAll(def.Condition, room))
                {
                    Reply(player, deviceId, channel, T("ConditionFailed", "name", def.Name, "cond", def.Condition));
                    return;
                }

                bool special = false;
                foreach (string action in def.Action.Split(','))
                {
                    string a = action.Trim();
                    if (a.Length == 0)
                        continue;

                    if (!RunAction(room, player, deviceId, channel, def, a, arg, ref special,
                                   key, prevLast, hadLast))
                        return;                      // 没做成 → 不计次数、不写冷却
                }

                Uses[key] = used + 1;
                LastUse[key] = now;

                if (!special)
                    Reply(player, deviceId, channel, T("Executed", "name", def.Name));
            }
            catch (global::System.Exception ex)
            {
                Plugin.Log.LogWarning($"[HS] 命令 /{def.Name} 执行失败 — {ex.Message}");
                Reply(player, deviceId, channel, T("ExecFailed", "name", def.Name));
            }
        }

        /// <summary>
        /// 执行单个动作。返回 false 表示"这次没做成"（调用方不计次数、不写冷却）。
        /// 特殊动作会把 <paramref name="special"/> 置 true —— 它们有自己的回执，不需要"已执行"。
        /// </summary>
        private static bool RunAction(GameRoom room, GamePlayer player, int deviceId,
                                      CommandChannel channel, CommandDef def, string a, string arg,
                                      ref bool special,
                                      string key, float prevLast, bool hadLast)
        {
            switch (a.ToLowerInvariant())
            {
                case "help":
                    special = true;
                    Reply(player, deviceId, channel, BuildHelp(player, channel));
                    return true;

                case "givefish":
                    special = true;
                    return GiveFish(player, deviceId, channel);

                case "givesoda":
                    special = true;
                    return GiveSoda(player, deviceId, channel);

                case "refresh":
                    special = true;
                    // 把「引擎写入前的次数/冷却状态」一并传下去 —— 它是延迟结算型命令，
                    // 结算失败时必须把引擎已经扣掉的还回去（见 DoRefresh 的注释）。
                    return DoRefresh(room, player, deviceId, channel, key, prevLast, hadLast);

                case "radar":
                    special = true;
                    return DoRadar(player, deviceId, channel, def);

                case "stasis":
                    special = true;
                    return DoStasis(room, player, deviceId, channel);

                case "repair":
                    special = true;
                    return DoRepair(room, player, deviceId, channel);

                case "lock":
                    special = true;
                    return LockNearby(player, deviceId, channel);

                case "credit":
                    special = true;
                    HandleCredit(player, deviceId, channel, arg);
                    return true;

                case "listplayers":
                    special = true;
                    Reply(player, deviceId, channel, BuildPlayerList(room));
                    return true;

                case "teleport":
                {
                    special = true;
                    int wantPid = 0;
                    if (!string.IsNullOrEmpty(arg))
                        int.TryParse(arg, out wantPid);
                    if (!TeleportCommandFeature.Begin(room, player, wantPid, out string tpErr))
                    {
                        Reply(player, deviceId, channel, T("TeleportFailed", "err", tpErr));
                        return false;
                    }
                    return true;
                }

                case "disconnect":
                    special = true;
                    return Disconnect(player, deviceId, channel, arg);

                default:
                {
                    int eq = a.IndexOf('=');
                    if (eq <= 0)
                    {
                        Reply(player, deviceId, channel, T("BadAction", "a", a));
                        return false;
                    }

                    string k = a.Substring(0, eq).Trim();
                    string v = a.Substring(eq + 1).Trim();
                    if (!RuleRewriteFeature.SetValue(k, v))
                    {
                        Reply(player, deviceId, channel, T("BadAction", "a", a));
                        return false;
                    }
                    Plugin.Log.LogInfo($"[HS] 命令 /{def.Name}：{k} → {v}");
                    return true;
                }
            }
        }

        // ══ 内置命令表 ════════════════════════════════════════════════

        /// <summary>
        /// 内置命令：即使配置里没注册也始终可用。
        /// 冷却 / 次数直接取自各自的配置项 —— 这样 DT CONFIG 页面上仍有带范围与说明的输入框，
        /// 而不是逼用户去改注册表字符串。
        /// </summary>
        private static List<CommandDef> Builtins()
        {
            return new List<CommandDef>
            {
                new CommandDef
                {
                    Name = "help", Aliases = new[] { "h", "?" },
                    Side = CommandSide.Any, Channel = CommandChannel.Both,
                    Action = "Help", AllowOutsideSurvive = true
                },
                new CommandDef
                {
                    Name = "refresh",
                    Side = CommandSide.Any, Channel = CommandChannel.Public,
                    Action = "Refresh", UsesPerPlayer = true,
                    Cooldown = RefreshCooldown?.Value ?? 300,
                    IsAvailable = () => AllowRefresh == null || AllowRefresh.Value
                },
                new CommandDef
                {
                    // 鱼：公共命令，黑白都能申领。
                    // 配额（全房 300 秒 4 条）与每人冷却（150 秒，防一个人拿光）都在
                    // KeyLockFeature 里自己管 —— 不走引擎的 uses=/cd=，因为冷却提示要用
                    // 自定义文案（"超出限额，请稍候重试"），而且总配额卖空要优先于冷却提示。
                    Name = "fish", Aliases = new[] { "lamp", "lantern", "key", "lt" },
                    Side = CommandSide.Any, Channel = CommandChannel.Public,
                    Action = "GiveFish",
                    Condition = "elapsed>=60",           // 开局 60 秒内不可申领
                    IsAvailable = () => KeyLockFeature.AllowIssue == null || KeyLockFeature.AllowIssue.Value
                },
                new CommandDef
                {
                    // 汽水：公共命令。配额与冷却都在 SodaBoostFeature 里自己管（要用自定义文案）。
                    Name = "soda", Aliases = new[] { "drink", "can", "cola" },
                    Side = CommandSide.Any, Channel = CommandChannel.Public,
                    Action = "GiveSoda"
                },
                new CommandDef
                {
                    Name = "rad", Aliases = new[] { "radar" },
                    Side = CommandSide.White, Channel = CommandChannel.Public,
                    Action = "Radar", UsesPerPlayer = true, QuietWhenBlocked = true,
                    Cooldown = RadarCooldownSeconds?.Value ?? 75,
                    MaxUses = RadarUsesPerPlayer?.Value ?? 2,
                    // 次数配成 0 时该命令直接不存在 —— 原版把它当"次数已用尽"，
                    // 而引擎的 uses 语义里 0 = 不限，两者对不上，所以在入口挡掉。
                    IsAvailable = () => (RadarUsesPerPlayer?.Value ?? 2) > 0
                },
                new CommandDef
                {
                    Name = "sta", Aliases = new[] { "stasis" },
                    Side = CommandSide.White, Channel = CommandChannel.Public,
                    Action = "Stasis", UsesPerPlayer = true, QuietWhenBlocked = true,
                    Cooldown = StasisCooldown?.Value ?? 90,
                    IsAvailable = () => AllowStasis == null || AllowStasis.Value
                },
                new CommandDef
                {
                    Name = "rep", Aliases = new[] { "repair" },
                    Side = CommandSide.White, Channel = CommandChannel.Public,
                    Action = "Repair", UsesPerPlayer = true, QuietWhenBlocked = true,
                    IsAvailable = () => AllowRepair == null || AllowRepair.Value
                },
                new CommandDef
                {
                    Name = "brk", Aliases = new[] { "break" },
                    Side = CommandSide.Black, Channel = CommandChannel.Secret,
                    Condition = "fusebox", Action = "Disconnect", Cooldown = 90
                },
                new CommandDef
                {
                    Name = "lck", Aliases = new[] { "lock" },
                    Side = CommandSide.Black, Channel = CommandChannel.Secret,
                    Action = "Lock", Cooldown = 60
                },
                new CommandDef
                {
                    Name = "cre", Aliases = new[] { "credit" },
                    Side = CommandSide.Black, Channel = CommandChannel.Secret,
                    Action = "Credit"
                },
                new CommandDef
                {
                    Name = "ls", Aliases = new[] { "list" },
                    Side = CommandSide.Black, Channel = CommandChannel.Secret,
                    Action = "ListPlayers"
                },
                new CommandDef
                {
                    Name = "tp",
                    Side = CommandSide.Black, Channel = CommandChannel.Secret,
                    Action = "Teleport", Cooldown = 60
                }
            };
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

        /// <summary>
        /// 解析一行：<c>name = cond -&gt; action ; cd=N ; uses=N ; side= ; ch= ; alias=</c>
        /// 参数分隔用 <c>;</c> 或 <c>|</c>。
        /// </summary>
        private static CommandDef ParseLine(string text)
        {
            int eq = text.IndexOf('=');
            if (eq <= 0)
                return null;

            var def = new CommandDef { Name = text.Substring(0, eq).Trim().ToLowerInvariant() };
            if (def.Name.Length == 0)
                return null;

            string rest = text.Substring(eq + 1).Trim();

            // desc= 必须写在**行尾**，其值一路吃到行尾（因此可以含 ; | , 等分隔符，方便写说明）。
            // 这也是唯一能让 desc 含分隔符的做法 —— 否则说明里的标点会把参数段切坏。
            int descAt = rest.IndexOf("desc=", global::System.StringComparison.OrdinalIgnoreCase);
            if (descAt >= 0)
            {
                def.Desc = rest.Substring(descAt + 5).Trim();
                rest = rest.Substring(0, descAt).Trim();
            }

            // 行内切出参数段（cd= / uses= / side= / ch= / alias= / per=），其余内容原样并入主体
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

                if (p.StartsWith("side=", global::System.StringComparison.OrdinalIgnoreCase))
                {
                    def.Side = ParseSide(p.Substring(5).Trim());
                    continue;
                }

                if (p.StartsWith("ch=", global::System.StringComparison.OrdinalIgnoreCase))
                {
                    def.Channel = ParseChannel(p.Substring(3).Trim());
                    continue;
                }

                if (p.StartsWith("alias=", global::System.StringComparison.OrdinalIgnoreCase))
                {
                    def.Aliases = SplitAliases(p.Substring(6));
                    continue;
                }

                if (p.StartsWith("per=", global::System.StringComparison.OrdinalIgnoreCase))
                {
                    def.UsesPerPlayer = p.Substring(4).Trim()
                        .StartsWith("p", global::System.StringComparison.OrdinalIgnoreCase);
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

        private static CommandSide ParseSide(string s)
        {
            switch (s.ToLowerInvariant())
            {
                case "any": case "all": case "both": return CommandSide.Any;
                case "white": case "w": return CommandSide.White;
                default: return CommandSide.Black;
            }
        }

        private static CommandChannel ParseChannel(string s)
        {
            switch (s.ToLowerInvariant())
            {
                case "pub": case "public": return CommandChannel.Public;
                case "both": case "any": return CommandChannel.Both;
                default: return CommandChannel.Secret;
            }
        }

        private static string[] SplitAliases(string s)
        {
            var parts = s.Split(new[] { ',', '/', ' ' },
                global::System.StringSplitOptions.RemoveEmptyEntries);
            var list = new List<string>();
            foreach (string one in parts)
            {
                string a = one.Trim().ToLowerInvariant();
                if (a.Length > 0)
                    list.Add(a);
            }
            return list.ToArray();
        }

        // ══ 文案表 ════════════════════════════════════════════════════

        private static Dictionary<string, string> _defaultTexts;
        private static Dictionary<string, string> _texts;
        private static string _textsFrom;

        private static Dictionary<string, string> ParseTexts(string raw)
        {
            var dict = new Dictionary<string, string>(global::System.StringComparer.OrdinalIgnoreCase);
            foreach (string line in (raw ?? "").Split(new[] { '\n', '\r' },
                global::System.StringSplitOptions.RemoveEmptyEntries))
            {
                string t = line.Trim();
                if (t.Length == 0 || t.StartsWith("#"))
                    continue;

                int eq = t.IndexOf('=');
                if (eq <= 0)
                    continue;

                string key = t.Substring(0, eq).Trim();
                if (key.Length == 0)
                    continue;

                dict[key] = t.Substring(eq + 1).Trim();
            }
            return dict;
        }

        /// <summary>
        /// 取一条文案。用户配置是**叠加**在内置默认之上的，所以删掉某行只会回到内置文案，
        /// 不会让提示变成空白；把值留空才是"这条不发送"。
        /// </summary>
        private static string T(string key)
        {
            string raw = Texts?.Value ?? "";
            if (_texts == null || _textsFrom != raw)
            {
                if (_defaultTexts == null)
                    _defaultTexts = ParseTexts(DefaultTexts);

                var merged = new Dictionary<string, string>(_defaultTexts,
                    global::System.StringComparer.OrdinalIgnoreCase);
                foreach (var kv in ParseTexts(raw))
                    merged[kv.Key] = kv.Value;

                _texts = merged;
                _textsFrom = raw;
            }

            return _texts.TryGetValue(key, out string value) ? value : "";
        }

        private static string T(string key, string k1, string v1)
            => T(key).Replace("{" + k1 + "}", v1 ?? "");

        private static string T(string key, string k1, string v1, string k2, string v2)
            => T(key, k1, v1).Replace("{" + k2 + "}", v2 ?? "");

        private static string T(string key, string k1, string v1, string k2, string v2, string k3, string v3)
            => T(key, k1, v1, k2, v2).Replace("{" + k3 + "}", v3 ?? "");

        /// <summary>
        /// 供同程序集内其它功能复用这张文案表（目前是 KeyLockFeature 的提灯文案）。
        /// 用法：<c>Text("LampTaken", "n", "2")</c>。
        /// </summary>
        internal static string Text(string key, params string[] pairs)
        {
            string text = T(key);
            if (pairs == null)
                return text;

            for (int i = 0; i + 1 < pairs.Length; i += 2)
                text = text.Replace("{" + pairs[i] + "}", pairs[i + 1] ?? "");

            return text;
        }

        // ══ 帮助 ══════════════════════════════════════════════════════

        /// <summary>动作串里的第一个动作（帮助文案按它归类）。</summary>
        private static string PrimaryAction(string action)
        {
            if (string.IsNullOrEmpty(action))
                return "";
            int comma = action.IndexOf(',');
            string first = comma < 0 ? action : action.Substring(0, comma);
            return first.Trim().ToLowerInvariant();
        }

        /// <summary>
        /// 每条命令的帮助说明：注册表行尾的 <c>desc=</c> 优先，否则按**动作**查 Texts 的
        /// <c>Desc_&lt;动作名&gt;</c>。
        ///
        /// 按动作而不是按命令名查：命令名可以被用户改（注册表里 brk→break、lck→lock），
        /// 改完若还按名字查表，帮助就会退化成"只有条件、没有说明"。
        /// </summary>
        private static string Describe(CommandDef def)
        {
            string action = PrimaryAction(def.Action);
            string template = string.IsNullOrEmpty(def.Desc) ? T("Desc_" + action) : def.Desc;
            if (string.IsNullOrEmpty(template))
                return null;                         // 没写说明 → 帮助里退回显示条件

            return FillDesc(template, def, action);
        }

        /// <summary>把说明模板里的占位符换成实际值。</summary>
        private static string FillDesc(string template, CommandDef def, string action)
        {
            string text = template
                .Replace("{cd}", def.Cooldown > 0 ? def.Cooldown.ToString() : "-")
                .Replace("{uses}", def.MaxUses > 0 ? def.MaxUses.ToString() : "-")
                .Replace("{name}", def.Name);

            if (action == "radar")
                return text.Replace("{dur}", (RadarDurationSeconds?.Value ?? 15).ToString());

            if (action == "stasis")
                return text.Replace("{sec}", (StasisSeconds?.Value ?? 5).ToString())
                           .Replace("{cost}", (StasisCostPercent?.Value ?? 5f).ToString("F0"));

            if (action == "repair")
                return text.Replace("{cost}", (RepairCostPercent?.Value ?? 10f).ToString("F0"));

            return text;
        }

        /// <summary>
        /// 生成该玩家此刻能用的命令帮助，按分层分组。
        /// 白方只看到公共 + 白方，黑方只看到公共 + 黑方；其它颜色（黑幕）只看到公共 ——
        /// 帮助本身不该成为探测对方命令表的手段。
        /// </summary>
        private static string BuildHelp(GamePlayer player, CommandChannel channel)
        {
            bool white = player.Color == EPlayerColor.White;
            bool black = player.Color == EPlayerColor.Black;

            var sb = new global::System.Text.StringBuilder(T("HelpTitle"));
            AppendGroup(sb, T("GroupAny"), CommandSide.Any, channel);

            if (white)
                AppendGroup(sb, T("GroupWhite"), CommandSide.White, channel);
            else if (black)
                AppendGroup(sb, T("GroupBlack"), CommandSide.Black, channel);

            return sb.ToString();
        }

        private static void AppendGroup(global::System.Text.StringBuilder sb, string title,
                                        CommandSide side, CommandChannel channel)
        {
            var list = new List<CommandDef>();

            // 注册表里的自定义命令按名字去重后并入（用户可能覆盖了某个内置命令的参数）
            foreach (var def in GetCommands())
            {
                if (def.Side != side)
                    continue;
                if (def.Channel != channel && def.Channel != CommandChannel.Both)
                    continue;
                list.Add(def);
            }

            foreach (var def in Builtins())
            {
                if (def.Side != side)
                    continue;
                if (def.Channel != channel && def.Channel != CommandChannel.Both)
                    continue;
                if (def.IsAvailable != null && !def.IsAvailable())
                    continue;
                if (Contains(list, def.Name))
                    continue;
                bool shadowed = false;
                foreach (var alias in def.Aliases)
                {
                    if (Contains(list, alias))
                    {
                        shadowed = true;
                        break;
                    }
                }
                if (shadowed)
                    continue;                        // 用户改名后的同义命令已经在表里了
                list.Add(def);
            }

            if (list.Count == 0)
                return;

            sb.Append("\n· ").Append(title).Append(" ·");
            foreach (var def in list)
            {
                sb.Append("\n/").Append(def.Name);

                string desc = Describe(def);          // 占位符已在 FillDesc 里填好
                if (desc != null)
                {
                    sb.Append("  ").Append(desc);
                }
                else if (!string.IsNullOrWhiteSpace(def.Condition))
                {
                    sb.Append("  ").Append(T("HelpCondition", "cond", def.Condition));
                }
            }
        }

        private static bool Contains(List<CommandDef> list, string name)
        {
            foreach (var def in list)
            {
                if (def.Name == name)
                    return true;
            }
            return false;
        }

        // ══ 动作实现 ══════════════════════════════════════════════════

        /// <summary>
        /// 申领一条鱼。发放配额（全房滑窗）、"手上有东西就落地"、以及用鱼黏门的逻辑都在 KeyLockFeature 里。
        /// 没领到就返回 false —— 引擎据此不计次数、不写冷却。
        /// </summary>
        private static bool GiveFish(GamePlayer player, int deviceId, CommandChannel channel)
        {
            bool ok = KeyLockFeature.TryIssue(player, out string text);
            if (!string.IsNullOrEmpty(text))
                Reply(player, deviceId, channel, text);
            return ok;
        }

        /// <summary>
        /// 申领一瓶汽水。配额（全房滑窗）与每人冷却都在 SodaBoostFeature 里自己管 ——
        /// 不走引擎的 uses=/cd=，因为冷却提示要用自定义文案（"你暂时不能申领第二瓶汽水"）。
        /// </summary>
        private static bool GiveSoda(GamePlayer player, int deviceId, CommandChannel channel)
        {
            bool ok = HideAndSeek.Features.Combat.SodaBoostFeature.TryIssue(player, out string text);
            if (!string.IsNullOrEmpty(text))
                Reply(player, deviceId, channel, text);
            return ok;
        }

        /// <summary>列出全部玩家（ID + 昵称 + 状态），便于 /tp 指定目标。</summary>
        private static string BuildPlayerList(GameRoom room)
        {
            if (room?.Players == null)
                return T("PlayerListEmpty");

            // 一行并排两个：聊天框每行字符有限，而昵称假定不超过 12 字母 / 6 汉字，
            // 两个并排正好用满一行，条目多时能省一半行数。
            var items = new List<string>();
            foreach (var p in room.Players)
            {
                if (p?.PublicInfo == null)
                    continue;

                var one = new global::System.Text.StringBuilder();
                one.Append('#').Append(p.PublicInfo.PlayerId);
                one.Append(' ').Append(p.Name ?? "?");
                if (p.IsDummy) one.Append(T("TagDummy"));
                if (p.IsSpectator) one.Append(T("TagSpectator"));
                else if (!p.IsAlive) one.Append(T("TagDead"));

                items.Add(one.ToString());
            }

            var sb = new global::System.Text.StringBuilder(T("PlayerListTitle"));
            for (int i = 0; i < items.Count; i += 2)
            {
                sb.Append('\n').Append(items[i]);
                if (i + 1 < items.Count)
                    sb.Append("  |  ").Append(items[i + 1]);
            }

            return sb.ToString();
        }

        /// <summary>黑学分：无参查看余额与等级，带方向则升级。方向用英文（vision/speed/task）。</summary>
        private static void HandleCredit(GamePlayer player, int deviceId, CommandChannel channel, string arg)
        {
            string dir = (arg ?? "").Trim().ToLowerInvariant();

            if (string.IsNullOrEmpty(dir))
            {
                Reply(player, deviceId, channel, HideAndSeek.Features.Combat.KillUpgradeFeature.Status());
                return;
            }

            if (dir == "help" || dir == "?")
            {
                Reply(player, deviceId, channel, HideAndSeek.Features.Combat.KillUpgradeFeature.HelpText());
                return;
            }

            int idx;
            switch (dir)
            {
                case "v": case "vision": case "视野":
                    idx = HideAndSeek.Features.Combat.KillUpgradeFeature.DirVision; break;
                case "s": case "speed": case "移速":
                    idx = HideAndSeek.Features.Combat.KillUpgradeFeature.DirSpeed; break;
                case "t": case "task": case "任务":
                    idx = HideAndSeek.Features.Combat.KillUpgradeFeature.DirTask; break;
                default:
                    Reply(player, deviceId, channel, T("CreditUsage"));
                    return;
            }

            HideAndSeek.Features.Combat.KillUpgradeFeature.TryUpgrade(GameRoom.Instance, idx, out string msg);
            Reply(player, deviceId, channel, msg);
        }

        /// <summary>以黑方为圆心锁住附近的门。返回是否真的锁到了门。</summary>
        private static bool LockNearby(GamePlayer player, int deviceId, CommandChannel channel)
        {
            float radius = LockDoorFeature.GetRadius();
            int count = LockDoorFeature.LockAround(player.PublicInfo?.Pos, radius, player);

            if (count == 0)
            {
                Reply(player, deviceId, channel, T("LockNone"));
                return false;
            }

            Reply(player, deviceId, channel, T("LockDone", "n", count.ToString(), "radius", radius.ToString("F0")));
            return true;
        }

        /// <summary>拆离自己最近的可拆电箱；地图上没有则拒绝。返回是否真的拆了。</summary>
        private static bool Disconnect(GamePlayer player, int deviceId,
                                       CommandChannel channel, string arg)
        {
            // 接受最多两个电箱 ID（空格/逗号分隔）。不足两个时自动补随机目标：
            // 原版断电需要两个电箱同时被拆，只拆一个不会全黑。
            string[] ids = string.IsNullOrWhiteSpace(arg)
                ? new string[0]
                : arg.Split(new[] { ' ', ',', ';', '，' },
                            global::System.StringSplitOptions.RemoveEmptyEntries);

            int want = ids.Length > 2 ? ids.Length : 2;
            int done = 0;

            for (int i = 0; i < want; i++)
            {
                string one = i < ids.Length ? ids[i] : null;
                if (!DisconnectOne(player, deviceId, channel, one))
                    break;                           // 拆不动就停（失败提示已由 DisconnectOne 发出）
                done++;
            }

            return done > 0;
        }

        private static bool DisconnectOne(GamePlayer player, int deviceId,
                                          CommandChannel channel, string arg)
        {
            int wantId = 0;
            if (!string.IsNullOrEmpty(arg) && !int.TryParse(arg, out wantId))
            {
                Reply(player, deviceId, channel, T("DisconnectBadId"));
                return false;
            }

            var manager = GameDeviceManager.Instance;
            if (manager?.Fuseboxes == null)
            {
                Reply(player, deviceId, channel, T("DisconnectNoFuseboxes"));
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
                if (info.MissionType != -1)          // 只拆被标记的目标（DisconnetCable 的唯一守卫）
                    continue;
                if (info.StateList[0] != 0)          // 已断电的不重复拆
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
                Reply(player, deviceId, channel, wantId > 0
                    ? T("DisconnectNoSuchFuse", "id", wantId.ToString())
                    : T("DisconnectNoFuseIdle"));
                return false;
            }

            // 反射复用原版拆电：灯光/箭头/音效/线索/OnBlackout 奖励/黑幕通知全走原版。注意拼写少一个 c。
            if (_disconnectMethod == null)
                _disconnectMethod = AccessTools.Method(typeof(GameFusebox), "DisconnetCable");

            if (_disconnectMethod == null)
            {
                Plugin.Log.LogWarning("[HS] 命令：找不到 Fusebox.DisconnetCable。");
                Reply(player, deviceId, channel, T("DisconnectUnavailable"));
                return false;
            }

            _disconnectMethod.Invoke(target, new object[] { player });
            Plugin.Log.LogInfo($"[HS] 命令：黑方 #{player.PublicInfo?.PlayerId} 拆除了电箱 #{target.ID}。");
            // 成功不回复：断电本身就有全图黑 + FuseOffSfx + 电箱箭头，文字是噪音
            return true;
        }

        /// <summary>
        /// /rad —— 复用雷达本体：把限时设成本次要求的时长，再开启。
        /// 由雷达自身在超时后关闭（它已经处理了 Survive 阶段限制与清理）。
        /// </summary>
        private static bool DoRadar(GamePlayer player, int deviceId, CommandChannel channel, CommandDef def)
        {
            if (WhiteRadarFeature.DurationSeconds != null)
                WhiteRadarFeature.DurationSeconds.Value = RadarDurationSeconds?.Value ?? 15;
            WhiteRadarFeature.SetActive(true);

            // 引擎在动作全部成功后才写回 Uses，所以这里读到的是"本次之前"的计数。
            string key = CmdKey(def, player.PublicInfo?.PlayerId ?? 0);
            int used = Uses.TryGetValue(key, out int u) ? u : 0;

            Reply(player, deviceId, channel, T("RadarOn", "n", (used + 1).ToString(), "uses", def.MaxUses.ToString()));
            // 不公告：/rad 的开启不对外播报
            return true;
        }

        /// <summary>
        /// /sta —— 时停黑方。复刻原版 UseTimeStop（:177532）：给黑方施加 TheWorld buff。
        /// 客户端 :14917 / :17153 的落地效果是 SkeletonAnim.timeScale = 0f（动画冻结）
        /// + PlayGray + _lockControlStack++ + CancelAllInteract —— 这才是真正的时停。
        /// 只对**黑方逐个**施加，避免波及白方。
        /// </summary>
        private static bool DoStasis(GameRoom room, GamePlayer player, int deviceId, CommandChannel channel)
        {
            if (!TrySpendProgress(StasisCostPercent?.Value ?? 5f, out string why))
            {
                Reply(player, deviceId, channel, why);
                return false;
            }

            int stasisMs = (StasisSeconds?.Value ?? 5) * 1000;
            foreach (var p in room.Players)
            {
                if (p?.PublicInfo == null || p.Color == EPlayerColor.White)
                    continue;
                try
                {
                    p.BuffComponent?.AddBuff(EBuffType.TheWorld, stasisMs);

                    // 音效以**被冻结的黑方**为中心，而不是命令执行者。时停的表现发生在黑方身上，
                    // 声音自然该在"被冻住的人"附近响起；以执行者为圆心会让站在白方身边的人先听到
                    // —— 等于暴露了谁用了命令。
                    room.BroadcastWorldSFX(ESoundType.TheWorldSfx, p.PublicInfo.Pos,
                        StasisSfxRange?.Value ?? 896f);
                }
                catch (global::System.Exception ex)
                {
                    Plugin.Log.LogWarning($"[HS] 命令：施加时停失败 — {ex.Message}");
                }
            }
            Plugin.Log.LogInfo($"[HS] 命令：时停已施加（TheWorld {stasisMs}ms）。");

            Reply(player, deviceId, channel, T("StasisDone", "sec", (StasisSeconds?.Value ?? 5).ToString()));
            // 不公开：只有执行者自己知道（回执已发给他），避免向黑方暴露白方动用了消耗手段。
            return true;
        }

        /// <summary>/rep —— 消耗任务进度立即恢复供电（仅断电时可用）。</summary>
        private static bool DoRepair(GameRoom room, GamePlayer player, int deviceId, CommandChannel channel)
        {
            int broken = 0;
            try { broken = Server.Game.DeviceManager.Instance?.GetDisconnectFuseCount() ?? 0; }
            catch { }

            if (broken <= 0)
            {
                Reply(player, deviceId, channel, T("RepairNoOutage"));
                return false;
            }

            if (!TrySpendProgress(RepairCostPercent?.Value ?? 10f, out string why))
            {
                Reply(player, deviceId, channel, why);
                return false;
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
                Plugin.Log.LogWarning($"[HS] 命令：修电失败 — {ex.Message}");
            }

            Reply(player, deviceId, channel, T("RepairDone", "n", fixedCount.ToString()));
            // 不公开：只有执行者自己知道（回执已发给他），避免向黑方暴露白方动用了消耗手段。
            return true;
        }

        // ══ 任务进度读写 ══════════════════════════════════════════════

        /// <summary>
        /// 扣减任务进度。返回 false 表示不足或读不到进度。
        /// 百分比的基数是 GoalPoint（任务总量）而不是 CurrentPoint（当前值）——
        /// 按当前值算的话，进度 5/45 时 5% 只有 0.25，被下限抬到 1，扣完 5→4 在进度条上
        /// 根本看不出来，用户会认为"完全没扣"。
        /// </summary>
        private static bool TrySpendProgress(float percent, out string why)
        {
            why = null;

            float cur = MissionBridge.CurrentPoint;
            if (cur <= 0f) { why = T("SpendNoProgress"); return false; }

            float goal = MissionBridge.GoalPoint;
            float effective = goal > 0f ? goal : cur;
            float cost = effective * percent / 100f;
            if (cost < 1f) cost = 1f;
            if (cost > cur) cost = cur;              // 不能扣成负数
            if (cur < cost) { why = T("SpendInsufficient", "x", cur.ToString("F0"), "y", cost.ToString("F0")); return false; }

            if (!MissionBridge.SetCurrentPoint(cur - cost))
            {
                why = T("SpendUnavailable");
                return false;
            }

            // 只改服务端字段，客户端的任务进度条不会动 —— 必须广播一次百分比。
            BroadcastMissionPercent();

            Plugin.Log.LogInfo($"[HS] 命令：消耗任务进度 {cost:F0}（{cur:F0} → {cur - cost:F0}）。");
            return true;
        }

        /// <summary>
        /// 把当前任务进度百分比广播给全房，驱动客户端进度条。
        /// 唯一能驱动进度条的包是 S_MISSION_PROGRESS_PERCENT：
        ///   客户端 Handle_S_MISSION_PROGRESS_PERCENT(:42889) → BroadcastSceneEvent(ChangeMissionPercent)
        ///   → UI_GameScene.ChangeMissionPercent(:75129) → DOValue
        /// 而 S_MISSION_STATE 第一行就是 `if (!Managers.Host.IsHost) return;`，**房主机直接丢弃**，
        /// 且它会清空客户端的任务列表镜像 —— 不能用它。
        /// </summary>
        private static void BroadcastMissionPercent()
        {
            float goal = MissionBridge.GoalPoint;
            if (goal <= 0f)
                return;

            GameRoom.Instance?.Broadcast(new S_MISSION_PROGRESS_PERCENT
            {
                Percent = (int)(MissionBridge.CurrentPoint / goal * 100f)
            });
        }

        // ══ /refresh ══════════════════════════════════════════════════

        /// <summary>
        /// /refresh —— 刷新网络连接：把它当作一次「人工任务」交给原版结算。
        ///
        /// 分两段反馈：发起时回"网络刷新中"，延迟 RefreshDelayMs 后再回"[刷新完成]"并结算，
        /// 让这个动作看起来像一次真实的联网过程，而不是凭空加进度。
        ///
        /// 冷却与次数由引擎统一处理（UsesPerPlayer = true），这里只管"发起 → 延迟 → 结算"。
        /// </summary>
        /// 冷却与次数由引擎统一处理（UsesPerPlayer = true），这里只管"发起 → 延迟 → 结算"。
        ///
        /// ⚠ 延迟结算的计数契约：引擎在 Execute 里是**发起成功就写 Uses/LastUse**（:610-611），
        /// 于是延迟到 5 秒后才失败时，次数与冷却已经被扣掉了 —— 违反引擎自己的
        /// 「没做成 → 不计次数、不写冷却」。两条路分开处理：
        ///   · delay <= 0（同步结算）⇒ 直接把结算结果返回，失败时引擎自然不会计数；
        ///   · delay >  0（延迟结算）⇒ 先报"发起成功"让引擎计数，结算失败时再还回去。
        /// </summary>
        private static bool DoRefresh(GameRoom room, GamePlayer player, int deviceId, CommandChannel channel,
                                      string key, float prevLast, bool hadLast)
        {
            int delay = RefreshDelayMs?.Value ?? 5000;
            Reply(player, deviceId, channel, T("RefreshPending"));

            if (delay <= 0)
                return CompleteAsMission(player);      // 同步：如实上报结算结果

            room.PushAfter(delay, delegate
            {
                CompleteRefresh(room, player, deviceId, channel, key, prevLast, hadLast);
            });
            return true;                               // 延迟：发起成功，等结算
        }

        /// <summary>
        /// 延迟结束后的第二次校验与结算。阶段/存活在等待期间可能已变化，需重新确认。
        /// **每一条失败路径都要把引擎已经扣掉的次数与冷却还回去**（否则就是"失败也扣次数"）。
        /// </summary>
        private static void CompleteRefresh(GameRoom room, GamePlayer player, int deviceId, CommandChannel channel,
                                            string key, float prevLast, bool hadLast)
        {
            try
            {
                if (room == null || player?.PublicInfo == null)
                {
                    RollbackRefreshCount(key, prevLast, hadLast);
                    return;
                }
                if (room.State != EGameState.Survive || !player.IsAlive)
                {
                    RollbackRefreshCount(key, prevLast, hadLast);   // 等待期间阶段变了或阵亡
                    return;
                }

                if (CompleteAsMission(player))
                {
                    Reply(player, deviceId, channel, T("RefreshDone"));
                }
                else
                {
                    RollbackRefreshCount(key, prevLast, hadLast);   // 结算本身失败
                    Reply(player, deviceId, channel, T("RefreshFailed"));
                }
            }
            catch (global::System.Exception ex)
            {
                RollbackRefreshCount(key, prevLast, hadLast);
                Plugin.Log.LogWarning($"[HS] /refresh 延迟结算失败 — {ex.Message}");
            }
        }

        /// <summary>
        /// 把引擎因"发起成功"而写下的次数与冷却还回去，等价于"这次没发生过"。
        ///
        /// 只用于**延迟结算型**命令：同步命令失败时引擎本来就不会计数，不需要回滚。
        /// 冷却要恢复成**原值**而不是直接删掉 —— 否则会把更早那次成功调用留下的冷却一起清掉。
        /// </summary>
        private static void RollbackRefreshCount(string key, float prevLast, bool hadLast)
        {
            if (string.IsNullOrEmpty(key))
                return;

            if (Uses.TryGetValue(key, out int used) && used > 0)
                Uses[key] = used - 1;

            if (hadLast)
                LastUse[key] = prevLast;
            else
                LastUse.Remove(key);
        }

        /// <summary>
        /// /refresh 的结算：读配置，交给 <see cref="MissionBridge.CompleteSynthetic"/> 用原版
        /// ClearMission 完成 —— 机制、为什么必须插在表头、为什么 finally 里必须移除，全部注释在那里。
        /// </summary>
        private static bool CompleteAsMission(GamePlayer player)
        {
            // 弹窗关掉时用 ScNone(0)：它在 MissionList 与本地化表里都不存在，
            // 客户端 ShowMissionClear 的 ContainsKey 会早退 → 只有全房 "+N SEC" 浮字与进度条变化。
            bool popup = RefreshPopup == null || RefreshPopup.Value;
            int type = popup ? (RefreshPopupType?.Value ?? 17) : 0;
            int point = RefreshPoint?.Value ?? 3;

            if (!MissionBridge.CompleteSynthetic(type, point, player, out string error))
            {
                Plugin.Log.LogWarning($"[HS] /refresh：结算失败 — {error}");
                return false;
            }

            Plugin.Log.LogInfo(
                $"[HS] /refresh：以 {point} 分的人工任务（借用类型 {type} = {(ESchoolMission)type}）交原版结算，" +
                $"进度 {MissionBridge.CurrentPoint:F1}/{MissionBridge.GoalPoint:F1}。");
            return true;
        }

        // ══ 回执 ══════════════════════════════════════════════════════

        /// <summary>
        /// 以命令来源的频道回执给该玩家。
        /// 关键：**按来源频道回** —— 白方多半是在设备（发信机）上发命令，那种情况下回执若走
        /// NormalChat，设备界面根本不显示，表现就是"命令没反应"。
        /// 回执只发给本人，所以黑方在公开频道问 /help 不会把黑方命令表泄露给全房。
        /// </summary>
        private static void Reply(GamePlayer player, int deviceId, CommandChannel channel, string text)
        {
            if (player?.Session == null || string.IsNullOrEmpty(text))
                return;

            EChatType type;
            if (channel == CommandChannel.Secret)
                type = EChatType.SecretChat;
            else
                type = deviceId > 0 ? EChatType.DeviceChat : EChatType.NormalChat;

            // 先按宽度折行，再按 3 行分段 —— 否则一行过长会被客户端自动折行，
            // 实际渲染行数超出 3 行，超出部分直接被截掉。
            var wrapped = new List<string>();
            foreach (var raw in text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n'))
                wrapped.AddRange(WrapByWidth(raw));

            for (int start = 0; start < wrapped.Count; start += MaxLinesPerMessage)
            {
                int count = wrapped.Count - start < MaxLinesPerMessage
                    ? wrapped.Count - start
                    : MaxLinesPerMessage;

                string chunk = string.Join("\n", wrapped.GetRange(start, count));

                try
                {
                    player.Session.Send(new S_CHAT_MESSAGE
                    {
                        Type = type,
                        DeviceId = deviceId,
                        Text = chunk,
                        PlayerId = player.PublicInfo?.PlayerId ?? 0,
                        Time = (int)(TimeManager.Instance?.SurviveTime ?? 0f),
                        IsDead = false
                    });
                }
                catch (global::System.Exception ex)
                {
                    Plugin.Log.LogWarning($"[HS] 命令：回执失败 — {ex.Message}");
                    return;
                }
            }
        }

        /// <summary>按显示宽度把一个逻辑行切成不超过 MaxWidthPerLine 的若干行。</summary>
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
    }
}
