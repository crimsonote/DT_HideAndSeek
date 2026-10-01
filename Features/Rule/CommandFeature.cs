using System.Collections.Generic;
using System.Linq;
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
            "QuotaExhausted = /{name} 的配额已用尽（{win} 秒内最多 {n} 次），请稍候重试。\n" +
            "PerQuotaExhausted = 你在 {win} 秒内已用过 {n} 次 /{name}，请稍候重试。\n" +
            "RoomCooldown = 刚有人用过 /{name}，还需 {sec} 秒。\n" +
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
            "Desc_repair = 强制重启电力系统，预损失{cost}%进度\n" +
            "Desc_maint = 电力系统自检维护，CD{cd}\n" +
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
            // ── 电力系统自检（多阶段流程）──
            // ⚠ 这段文案**不能出现 `[000000]` 之类的格式占位**：实测玩家会照抄它当授权码输入。
            //   要输入的是那条算式的**答案**，所以文案必须把"算什么 + 怎么发"说全。
            // ⚠ 整段折行后要控制在 3 个渲染行内（`Reply` 每 3 行一条，超了会拆成多条）。
            "SelfTestRecv = 系统已接收请求，正在验证操作者权限，请耐心等待。\n" +
            "SelfTestDenied = 权限验证失败。\n" +
            // ⚠ 授权码提示**刻意拆成两条独立文案**（Prompt1 / Prompt2），由调用方用两次
            //   `Reply` + `PushAfter` 分开发 —— **不能合成一条靠 `Reply` 自动分段**：
            //   那个机制按"每 3 个渲染行一条"切，这两段合起来是 4 行，切点正好落在
            //   第二段中间 ⇒ 客户端少渲染一条时，**含算式的那半截就整段消失**
            //   （实测连续踩两次，房主只看到"请输入…"却看不到"请计算…"）。
            "SelfTestPrompt1 = 已验证权限，操作者 {name}#{id}，于{room}进行操作。\n" +
            "SelfTestPrompt2 = 为防止误操作，请计算 {q}，再发送「/maint 答案」完成确认。\n" +
            "SelfTestBadCode = 授权码错误，操作失败。\n" +
            "SelfTestExpired = 操作已过期。\n" +
            "SelfTestBusy = 已有其他操作者正在进行电力系统自检，请稍后再试。\n" +
            "SelfTestBusyElsewhere = 此操作当前正在{room}由{name}#{id}进行操作，请耐心等待。\n" +
            "SelfTestWrongArea = 你需要在{room}进行操作，不可变更操作位置。\n" +
            "SelfTestRunning = 已完成二次确认，电力自检已开始，请耐心等待，预计{sec}s完成自检。\n" +
            "SelfTestOk = 自检完成，当前电力系统工作正常。\n" +
            "SelfTestRestored = 电力系统已排除{n}个故障，电力系统已恢复。\n" +
            "SelfTestUnavailable = 电力自检当前不可用。\n" +
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
            "SodaCooldown = 你暂时不能申领第二瓶汽水\n" +
            // ── 按命令作用域的措辞（三层查找的第 ② 层）──
            // 引擎接管配额/冷却后，通用键（QuotaExhausted/Cooldown）是给没定制的命令用的；
            // 这两条命令的玩家可见文案必须与改造前**逐字一致**，所以在这里逐字挂回原键的原文。
            // 想改某条命令的说法，改这两行即可（不必动上面那些通用键）。
            "fish.QuotaExhausted = 鱼已售罄，请稍候重试。\n" +
            "fish.Cooldown = 超出限额，请稍候重试\n" +
            "soda.QuotaExhausted = 汽水申领超过配额\n" +
            "soda.Cooldown = 你暂时不能申领第二瓶汽水" + "\n" +
            // 鱼的条件失败（elapsed>=10）用它自己的措辞，而不是通用的「条件未满足」。
            // 原文逐字取自本表里的 FishTooEarly，改这里不会波及其他命令。
            "fish.ConditionFailed = 非售货时间，请稍候再来";

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

            // ── 「四层空间」：配额与冷却各分「全房 / 每人」两层，四层互相独立、可任意叠加 ──
            //
            // 为什么不用一个开关概括：原先只有一个 UsesPerPlayer 同时决定「次数按谁算」与
            // 「冷却按谁算」，于是「全房配额 + 每人冷却」这种组合**表达不了** ——
            // fish / soda 正是这种组合，只能逃离引擎自己管，代价是它们在注册表里完全不可配。
            //
            // 现在四层各用一个数表达（0 = 该层不启用）：
            //   Quota<0>      全房滑窗配额：QuotaWindow 秒内全房最多 QuotaMax 次
            //   PerQuotaMax   每人滑窗配额：同上，但按人独立
            //   RoomCooldown  全房固定冷却：全房共用一个间隔
            //   Cooldown      每人固定冷却：每人一个间隔（旧字段，含义不变）
            // 超限时提示要分清是哪一层超的（四个 Texts 键）。

            /// <summary>全房滑窗配额：窗口内全房最多几次。0 = 不启用。</summary>
            public int QuotaMax;

            /// <summary>全房滑窗配额的窗口秒数（QuotaMax &gt; 0 时有意义）。</summary>
            public float QuotaWindow = 300f;

            /// <summary>每人滑窗配额：窗口内每人最多几次。0 = 不启用。</summary>
            public int PerQuotaMax;

            /// <summary>每人滑窗配额的窗口秒数（PerQuotaMax &gt; 0 时有意义）。</summary>
            public float PerQuotaWindow = 300f;

            /// <summary>全房固定冷却秒数：全房共用。0 = 不启用。</summary>
            public int RoomCooldown;

            // ── 命令自己的措辞（在注册表那一行里写，与 cd=/quota= 同级）──
            //
            // 为什么要能"外部注册"而不是只靠全局 Texts：措辞是**这条命令的一部分**，
            // 跟它的冷却、配额一样属于它的定义。写在注册表行里，改一条命令的措辞
            // 不必去翻那张把所有命令混在一起的大文本表。
            //
            // 查找优先级：**注册表字段 > Texts 里的「命令名.键」 > Texts 里的通用键**。
            // 三层都不写就走通用模板 —— 所以旧配置与没定制的命令行为不变。
            //
            // 文案里不要写分号（; 是参数分隔符；要分行用 \n）。

            /// <summary>全房/每人配额用尽时的措辞。空 = 走 Texts。</summary>
            public string QuotaText;

            /// <summary>全房冷却中的措辞。空 = 走 Texts。</summary>
            public string RoomCdText;

            /// <summary>每人冷却中的措辞。空 = 走 Texts。</summary>
            public string CdText;

            /// <summary>次数用尽（uses=）时的措辞。空 = 走 Texts。</summary>
            public string UsesText;

            /// <summary>阶段/存活不满足被拦时的措辞。空 = 走 Texts。</summary>
            public string BlockedText;

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

        // ── 「四层空间」的三个记录容器 ──
        // 配额是**滑窗**（窗口内最多 N 次），所以要存"最近若干次的时刻"，按命令名分：
        // 键用命令名（全房共享）或 CmdKey（每人独立），值是该窗口内的发放时刻表。

        /// <summary>全房滑窗配额：命令名 → 窗口内的发放时刻。</summary>
        private static readonly Dictionary<string, List<float>> QuotaIssued =
            new Dictionary<string, List<float>>();

        /// <summary>每人滑窗配额：CmdKey → 该人窗口内的发放时刻。</summary>
        private static readonly Dictionary<string, List<float>> PerQuotaIssued =
            new Dictionary<string, List<float>>();

        /// <summary>全房固定冷却：命令名 → 全房上次使用的 SurviveTime。</summary>
        private static readonly Dictionary<string, float> RoomLastUse =
            new Dictionary<string, float>();

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

        /// <summary>
        /// 回执被拆成多条时的**段间隔**（毫秒）。
        ///
        /// 不能在同一帧把多条 `S_CHAT_MESSAGE` 一起发 —— 客户端对"同一时刻刷出的多条"
        /// 只保留一条（`DefaultTexts` 里"多条消息一次性刷出、前面的会被顶掉"说的就是这个）。
        /// 实测踩过：自检的授权码提示折行后 4 行 ⇒ 拆 2 条 ⇒ 含算式的那条被顶掉，
        /// 玩家看不到该算什么，只能照抄文案里的格式占位 ⇒ 必然"授权码错误"。
        /// </summary>
        private const int ReplyChunkGapMs = 900;

        private static List<CommandDef> _parsed;
        private static string _parsedFrom;
        private static MethodInfo _disconnectMethod;

        private static string CmdKey(CommandDef def, int playerId)
            => def.UsesPerPlayer ? playerId + ":" + def.Name : def.Name;

        // ══ 入口 ══════════════════════════════════════════════════════

        /// <summary>
        /// **换局清理**：进入新的 `Survive` 时，丢掉上一局残留的"电力系统自检"流程状态。
        ///
        /// 为什么必须有：`_selfTest` 是静态字段，而它原有的 6 个清理点**全在流程内部**
        /// （换位置 / 算错 / 退局 / 黑方 / 超时 / 激活）—— **没有一个是"换局"**。
        /// 于是上一局没走完的流程会留到下一局：
        ///   · 所有人敲 `/maint` 都得到"已有其他操作者…"；
        ///   · 那个已排出的 `SelfTestActivate` 定时器若跨局生效，还会在新局开局
        ///     就"电箱下线 45 秒"或"修好所有电箱"（行为取决于 job 是否被换局清掉，不可依赖）。
        ///
        /// ⚠ `PowerSelfTestFeature` 里的 `_suppressUntil` / `_roomCdUntil` **不需要**在这里清：
        ///   它们以 `Managers.Game.ClientTime` 为基准，而那个值换局会归零（`InitGame` / 回大厅），
        ///   所以"电箱下线"与"冷却"都不会跨局。这里只需要管 `_selfTest`。
        /// </summary>
        [HarmonyPatch(typeof(GameRoom), nameof(GameRoom.ChangeGameState), new[] { typeof(EGameState) })]
        internal static class RoundResetHook
        {
            [HarmonyPostfix]
            private static void Postfix(EGameState state)
            {
                if (state != EGameState.Survive)
                    return;

                // ① 丢上一局残留的流程（静态字段，否则新局所有人敲 /maint 都被"占用"，
                //    而且已排出的 SelfTestActivate 可能在新局开局施加效果）。
                SelfTestClear();

                // ② 丢上一局残留的两个"绝对时刻"。它们**不能**靠 ClientTime 换局归零自愈 ——
                //    归零只会让上一局的 `T0 + 180` / `T0 + 45` 在新局里显得更久：
                //      实测 `_roomCdUntil`：上局 T0=100 ⇒ 280；新局到 60 时读出 220
                //      （而配置的 CD 只有 180）—— 房主看到的正是 "CD220"；
                //      `_suppressUntil` 同理 ⇒ 新局电箱迟迟不派发、`/brk` 无事可做。
                PowerSelfTestFeature.ResetRoundState();
            }
        }

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

            // 「四层空间」的滑窗与全房冷却也必须跨局清 —— 它们记的是 SurviveTime，
            // 而它每局被 ResetSurvival() 设回 420：不清的话上一局记下的时刻在新局
            // 会算出负数，窗口永不过期 / 冷却永不满足（这一类坑今天已经踩过四次）。
            QuotaIssued.Clear();
            PerQuotaIssued.Clear();
            RoomLastUse.Clear();
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

        /// <summary>
        /// 滑窗配额：返回该键在窗口内**已用**的次数（顺手把滑出窗口的旧记录丢掉）。
        ///
        /// 配额是滑窗而不是"每局总数"，所以必须存时刻表而不是计数器 ——
        /// 也正因为存的是时刻，跨局**必须清空**（SurviveTime 每局回退到 420，
        /// 不清就会算出负数、窗口永不过期）。见 <see cref="Reset"/>。
        /// </summary>
        private static int QuotaCount(Dictionary<string, List<float>> table, string k, float now, float window)
        {
            List<float> list;
            if (!table.TryGetValue(k, out list))
                return 0;

            list.RemoveAll(t => now - t > window);
            return list.Count;
        }

        /// <summary>记一次配额消耗。**只在命令真的执行了之后调**（与 Uses 同一时机）。</summary>
        private static void QuotaStamp(Dictionary<string, List<float>> table, string k, float now)
        {
            List<float> list;
            if (!table.TryGetValue(k, out list))
            {
                list = new List<float>();
                table[k] = list;
            }
            list.Add(now);
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
                        Reply(player, deviceId, channel, TReg(def.BlockedText, def.Name, "PhaseBlocked"));
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
                    Reply(player, deviceId, channel, TReg(def.UsesText, def.Name, "UsesExhausted", "name", def.Name, "n", def.MaxUses.ToString()));
                    return;
                }

                float now = TimeManager.Instance?.SurviveTime ?? 0f;

                // ── 四层空间 ①：全房滑窗配额（窗口内全房最多几次）──
                if (def.QuotaMax > 0
                    && QuotaCount(QuotaIssued, def.Name, now, def.QuotaWindow) >= def.QuotaMax)
                {
                    Reply(player, deviceId, channel,
                        TReg(def.QuotaText, def.Name, "QuotaExhausted", "name", def.Name, "n", def.QuotaMax.ToString(),
                            "win", def.QuotaWindow.ToString("F0")));
                    return;
                }

                // ── 四层空间 ②：每人滑窗配额 ──
                if (def.PerQuotaMax > 0
                    && QuotaCount(PerQuotaIssued, key, now, def.PerQuotaWindow) >= def.PerQuotaMax)
                {
                    Reply(player, deviceId, channel,
                        TReg(def.QuotaText, def.Name, "PerQuotaExhausted", "name", def.Name, "n", def.PerQuotaMax.ToString(),
                            "win", def.PerQuotaWindow.ToString("F0")));
                    return;
                }

                // ── 四层空间 ③：全房固定冷却（全房共用一个间隔）──
                if (def.RoomCooldown > 0 && RoomLastUse.TryGetValue(def.Name, out float roomLast)
                    && now - roomLast < def.RoomCooldown)
                {
                    Reply(player, deviceId, channel,
                        TReg(def.RoomCdText, def.Name, "RoomCooldown",
                            "name", def.Name,
                            "sec", (((int)(def.RoomCooldown - (now - roomLast))) + 1).ToString()));
                    return;
                }

                // ── 四层空间 ④：每人固定冷却（旧字段 Cooldown，含义不变）──
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

                    // 措辞按命令走：Texts 里写 `fish.Cooldown = …` 可单独定制，没写的回退通用模板。
                    Reply(player, deviceId, channel,
                        TReg(def.CdText, def.Name, "Cooldown",
                            "name", def.Name,
                            "sec", (((int)(def.Cooldown - (now - last))) + 1).ToString()));
                    return;
                }

                if (!string.IsNullOrWhiteSpace(def.Condition)
                    && !RuleRewriteFeature.MatchesAll(def.Condition, room))
                {
                    Reply(player, deviceId, channel, TReg(null, def.Name, "ConditionFailed", "name", def.Name, "cond", def.Condition));
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

                // 记下这一轮的配额与全房冷却时刻 —— 与 Uses 同一时机（只有真执行了才记，
                // 失败路径在 RunAction 里 return 掉，不会走到这）。
                if (def.QuotaMax > 0)
                    QuotaStamp(QuotaIssued, def.Name, now);
                if (def.PerQuotaMax > 0)
                    QuotaStamp(PerQuotaIssued, key, now);
                if (def.RoomCooldown > 0)
                    RoomLastUse[def.Name] = now;

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

                case "maint":
                    special = true;
                    return DoSelfTest(room, player, deviceId, channel, arg);

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
                    //
                    // 配额与冷却**改由引擎的四层空间执行**（原先在 KeyLockFeature 里自管）。
                    // 值仍取 [KeyLock] 段的配置项 —— 老 .cfg 照旧有效，
                    // 同时注册表（Commands = fish = ... ; quota=4/300 ; cd=150）也能覆盖它们。
                    // 当初自管的两个原因现在都不成立了：引擎能表达「全房配额 + 每人冷却」的混合归属，
                    // 且提示顺序已固定为「配额优先于冷却」。
                    Name = "fish", Aliases = new[] { "lamp", "lantern", "key", "lt" },
                    Side = CommandSide.Any, Channel = CommandChannel.Public,
                    Action = "GiveFish",
                    Condition = "elapsed>=10",           // 开局 10 秒内不可申领（原为 60，按用户要求改）
                    QuotaMax = KeyLockFeature.QuotaMax?.Value ?? 4,                 // 全房：窗口内 4 条
                    QuotaWindow = KeyLockFeature.QuotaWindowSeconds?.Value ?? 300f,
                    Cooldown = (int)(KeyLockFeature.FishCooldown?.Value ?? 150f),   // 每人：150 秒
                    UsesPerPlayer = true,                                          // 冷却按人独立
                    IsAvailable = () => KeyLockFeature.AllowIssue == null || KeyLockFeature.AllowIssue.Value
                },
                new CommandDef
                {
                    // 汽水：公共命令。配额与冷却同样改由引擎执行，值取 [SodaBoost] 段的配置项。
                    Name = "soda", Aliases = new[] { "drink", "can", "cola" },
                    Side = CommandSide.Any, Channel = CommandChannel.Public,
                    Action = "GiveSoda",
                    // 全限定：CommandFeature 在 Features.Rule 下，看不到 Features.Combat。
                    QuotaMax = HideAndSeek.Features.Combat.SodaBoostFeature.QuotaMax?.Value ?? 4,
                    QuotaWindow = HideAndSeek.Features.Combat.SodaBoostFeature.QuotaWindowSeconds?.Value ?? 300f,
                    Cooldown = (int)(HideAndSeek.Features.Combat.SodaBoostFeature.IssueCooldown?.Value ?? 240f),
                    UsesPerPlayer = true
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
                    // 「电力系统自检」——不消耗任务进度，但流程繁琐（3 秒验证 → 一次性授权码 → 10~24 秒自检）。
                    //
                    // ★ `Side = Any`（**公共组**），**不能**设成 `White`：
                    //   `SideAllows` 不匹配时命令直接"找不到"（:606），黑方就敲不进来 ——
                    //   而需求要的正是"黑方敲了、3 秒后得到『权限验证失败』"这个演出。
                    //   所以它是公共命令：黑方看得到、敲得动，只是流程里会被拒。
                    //
                    // ★ 冷却**由本功能自管**（`PowerSelfTestFeature.RemainingCooldown`），
                    //   所以引擎的两个冷却都留 0：
                    //     · 引擎的 `Cooldown` / `RoomCooldown` 都在**命令返回时**记账（:806），
                    //       而本命令"返回"只代表流程**开始**（后面还有授权码与自检两段）；
                    //       真正的 CD 起点是**流程走完、效果落地**那一刻。
                    //     · 而且房主要的是"**全房共享**" —— 那个语义在引擎的 `RoomCooldown` 上，
                    //       而 `Cooldown` 是"每人一个间隔"，用错就变成各算各的。
                    Name = "maint", Aliases = new[] { "maintenance", "mnt" },
                    Side = CommandSide.Any, Channel = CommandChannel.Public,
                    Action = "Maint", UsesPerPlayer = false, MaxUses = 0,
                    QuietWhenBlocked = true,
                    Cooldown = 0, RoomCooldown = 0,
                    IsAvailable = () => PowerSelfTestFeature.Armed
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

                // ── 「四层空间」的三个键（语义见 CommandDef 里那段注释）──
                //   quota=N[/W]     全房滑窗配额：W 秒内全房最多 N 次（省略 /W 用默认窗口）
                //   perQuota=N[/W]  每人滑窗配额
                //   roomCd=N        全房固定冷却
                // 注意 perQuota 与 quota 不冲突：前者以 `perq` 开头，StartsWith("quota=") 判不到它。
                if (p.StartsWith("quota=", global::System.StringComparison.OrdinalIgnoreCase)
                    || p.StartsWith("perquota=", global::System.StringComparison.OrdinalIgnoreCase))
                {
                    bool per = p.StartsWith("perquota=", global::System.StringComparison.OrdinalIgnoreCase);
                    string val = (per ? p.Substring(9) : p.Substring(6)).Trim();

                    int slash = val.IndexOf('/');
                    string left = slash >= 0 ? val.Substring(0, slash) : val;
                    string right = slash >= 0 ? val.Substring(slash + 1) : null;

                    int n = 0;
                    if (int.TryParse(left.Trim(), out int parsed))
                        n = parsed < 0 ? 0 : parsed;

                    float w = 300f;
                    if (right != null
                        && float.TryParse(right.Trim(), global::System.Globalization.NumberStyles.Float,
                                          global::System.Globalization.CultureInfo.InvariantCulture, out float pw)
                        && pw > 0f)
                        w = pw;

                    if (per)
                    {
                        def.PerQuotaMax = n;
                        def.PerQuotaWindow = w;
                    }
                    else
                    {
                        def.QuotaMax = n;
                        def.QuotaWindow = w;
                    }
                    continue;
                }

                if (p.StartsWith("roomcd=", global::System.StringComparison.OrdinalIgnoreCase))
                {
                    if (int.TryParse(p.Substring(7).Trim(), out int rcd))
                        def.RoomCooldown = rcd < 0 ? 0 : rcd;
                    continue;
                }

                // ── 命令自己的措辞（与 cd=/quota= 同级，写在这一行里）──
                // 查找优先级：注册表字段 > Texts 的「命令名.键」 > Texts 的通用键。
                // 值里不能有分号（; 是参数分隔符），要分行用 \n。
                if (p.StartsWith("quotatext=", global::System.StringComparison.OrdinalIgnoreCase))
                {
                    def.QuotaText = p.Substring(10).Trim();
                    continue;
                }

                if (p.StartsWith("roomcdtext=", global::System.StringComparison.OrdinalIgnoreCase))
                {
                    def.RoomCdText = p.Substring(11).Trim();
                    continue;
                }

                if (p.StartsWith("cdtext=", global::System.StringComparison.OrdinalIgnoreCase))
                {
                    def.CdText = p.Substring(7).Trim();
                    continue;
                }

                if (p.StartsWith("usestext=", global::System.StringComparison.OrdinalIgnoreCase))
                {
                    def.UsesText = p.Substring(9).Trim();
                    continue;
                }

                if (p.StartsWith("blockedtext=", global::System.StringComparison.OrdinalIgnoreCase))
                {
                    def.BlockedText = p.Substring(12).Trim();
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
        /// 带**注册表措辞**的查找，三层优先级：
        ///   ① 命令注册表行里的措辞（例如 <c>quotaText=鱼已经卖光了</c>）
        ///   ② Texts 里的「命令名.键」（<c>fish.QuotaExhausted = …</c>）
        ///   ③ Texts 里的通用键（<c>QuotaExhausted = …</c>）
        ///
        /// 三层都不写就走通用模板 —— 所以没定制的命令行为不变。
        /// 让措辞能和 <c>cd=</c>/<c>quota=</c> 一样写在命令那一行里，是因为措辞本身就是
        /// 这条命令定义的一部分；改一条命令的措辞不必去翻那张混着所有命令的大文本表。
        /// </summary>
        private static string TReg(string registryText, string scope, string key, params string[] pairs)
        {
            string text = registryText;
            if (string.IsNullOrEmpty(text) && !string.IsNullOrEmpty(scope))
                text = T(scope + "." + key);
            if (string.IsNullOrEmpty(text))
                text = T(key);

            for (int i = 0; i + 1 < pairs.Length; i += 2)
                text = text.Replace("{" + pairs[i] + "}", pairs[i + 1] ?? "");
            return text;
        }

        /// <summary>
        /// 带**命令作用域**的查找：先找「命令名.键」，找不到再回退到通用键。
        ///
        /// 为什么要它：同一件事（配额用尽 / 冷却中）在不同命令上该有不同说法 ——
        /// 鱼是"鱼已经卖光了"，汽水是"汽水机空了"，时停是"刚用过"。
        /// 若所有命令共用一套固定模板，就没法逐条改措辞。
        ///
        /// 用法：在 Texts 里写一行 `<c>fish.QuotaExhausted = 鱼已经卖光了，{win} 秒后再来。</c>
        /// 即可单独定制；没写的命令继续用通用模板，行为不变。
        /// </summary>
        private static string TScoped(string scope, string key, params string[] pairs)
        {
            string text = string.IsNullOrEmpty(scope) ? "" : T(scope + "." + key);
            if (string.IsNullOrEmpty(text))
                text = T(key);

            for (int i = 0; i + 1 < pairs.Length; i += 2)
                text = text.Replace("{" + pairs[i] + "}", pairs[i + 1] ?? "");
            return text;
        }

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
            // ★ `{cd}` 必须**在这里一次算好**，不能"先统一填 def.Cooldown、再在下面按动作补" ——
            //   那样第一次就把 `{cd}` 换成了 "-"（或数字），后面的按动作替换永远找不到目标。
            //   实测踩过：`/maint` 的冷却由功能自管（引擎字段刻意留 0，好把记账点延后到"流程完成"），
            //   于是 /help 里一直显示 "CD-"，看起来像"这条命令没有冷却"。
            string cdText = action == "maint"
                ? (PowerSelfTestFeature.Cooldown?.Value ?? 180f).ToString("F0")
                : (def.Cooldown > 0 ? def.Cooldown.ToString() : "-");

            string text = template
                .Replace("{cd}", cdText)
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
            // 只对**打命令的人**生效：/rad 是每人独立次数的命令（UsesPerPlayer），
            // 若开成全房雷达，就是"一个人消耗次数、全房白方受益"。
            WhiteRadarFeature.SetActive(true, player?.PublicInfo?.PlayerId ?? 0);

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

        // ══ 电力系统自检（多阶段流程）══════════════════════════════════
        //
        // 口径（房主给的）：**不消耗任务进度**，但流程繁琐 ——
        //     收到命令 → 等 3 秒验证权限 → 要求输入"一次性授权码"（一道随机算式的答案，
        //     难度按"让人至少要在终端前待 10 秒"设计）→ 超时 20 秒 → 答对后等 10~24 秒随机
        //     → 结果分两种：电力正常（电箱下线 45 秒）/ 电力中断（修好电箱）。
        //
        // **同一时间只允许一个玩家处于流程中**，其他人拿到 "已有其他操作者" 的拒绝。
        //
        // 三个定时阶段都用 `GameRoom.PushAfter`，每个回调先核对**代次号** `Gen` ——
        // 因为流程可能被"退局 / 新一轮 / 答对"提前终结，而排出去的定时器无法撤销；
        // 不核代次的话，上一轮的定时器会打断新一轮（或对已结束的流程发消息）。
        //
        // 授权码走**同一个命令的第二次调用**（`/maint <答案>`），所以不需要在聊天入口
        // 做特殊拦截：进到这里时看 `WaitingCode` 就知道这次输入是授权码。

        private sealed class SelfTestSession
        {
            public int PlayerId;
            public int DeviceId;
            public CommandChannel Channel;
            public int Gen;
            public int Answer;
            public bool WaitingCode;

            /// <summary>发起时所在房间（`RoomData.DataId`）—— 地缘约束的判据。</summary>
            public int AreaRoomId;
            /// <summary>发起时的房间显示名（文案/日志用）。</summary>
            public string AreaName;
        }

        /// <summary>某人此刻所在房间的 `DataId`；取不到返回 0。</summary>
        private static int AreaRoomIdOf(GamePlayer player)
        {
            try { return player?.CurrentArea?.Data?.DataId ?? 0; }
            catch { return 0; }
        }

        private static SelfTestSession _selfTest;
        private static int _selfTestGen;
        private static global::System.Random _selfTestRng;

        private static global::System.Random SelfTestRng()
            => _selfTestRng ?? (_selfTestRng = new global::System.Random());

        private static void SelfTestClear()
        {
            _selfTest = null;
            _selfTestGen++;                 // 让所有已排出的回调作废
        }

        private static GamePlayer FindPlayerById(GameRoom room, int id)
        {
            if (room?.Players == null)
                return null;
            foreach (var p in room.Players)
            {
                if (p?.PublicInfo != null && p.PublicInfo.PlayerId == id)
                    return p;
            }
            return null;
        }

        /// <summary>角色显示名 —— 与客户端 `Player.DisplayName`(:15428) 同源。</summary>
        private static string RoleNameOf(GamePlayer player)
        {
            try
            {
                var ch = Managers.Data?.CharacterDic?.Values
                    .FirstOrDefault(x => x.DataId == player.CharacterId);
                if (ch == null)
                    return "#" + player.CharacterId;
                string t = Managers.GetText(ch.Name);
                return string.IsNullOrEmpty(t) ? ch.Name : t;
            }
            catch { return "#" + (player?.CharacterId ?? 0); }
        }

        /// <summary>房间显示名 —— 与客户端 :47082 同源（`GetText(ERoomType 名)`）。</summary>
        private static string AreaNameOf(GamePlayer player)
        {
            try
            {
                var data = player?.CurrentArea?.Data;
                if (data == null)
                    return "未知区域";
                string key = global::System.Enum.GetName(typeof(ERoomType), data.Type);
                string t = Managers.GetText(key);
                return string.IsNullOrEmpty(t) ? key : t;
            }
            catch { return "未知区域"; }
        }

        /// <summary>
        /// 随机出一道算式，并给出答案。
        /// 难度取向：**要心算几步**（两位数加减、或三数运算），让人不能在几秒内随手打完
        /// —— 但答案不超过 200，避免算错得莫名其妙。
        /// </summary>
        private static string BuildQuestion(out int answer)
        {
            var r = SelfTestRng();
            switch (r.Next(3))
            {
                case 0:
                {
                    int a = r.Next(13, 98), b = r.Next(13, 98);
                    answer = a + b;
                    return a + " + " + b + " = ？";
                }
                case 1:
                {
                    int a = r.Next(41, 150), b = r.Next(12, 39);
                    answer = a - b;
                    return a + " - " + b + " = ？";
                }
                default:
                {
                    int a = r.Next(16, 79), b = r.Next(11, 61), c = r.Next(9, 48);
                    answer = a + b - c;
                    return a + " + " + b + " - " + c + " = ？";
                }
            }
        }

        /// <summary>自检耗时：在 [下限, 上限] 之间取一个**真随机**整数（含两端）。</summary>
        private static int RandomActivateSec()
        {
            int lo = (int)(PowerSelfTestFeature.ActivateMinSec?.Value ?? 10f);
            int hi = (int)(PowerSelfTestFeature.ActivateMaxSec?.Value ?? 24f);
            if (hi < lo)
            {
                int t = lo;
                lo = hi;
                hi = t;
            }
            if (hi <= lo)
                return global::System.Math.Max(1, lo);
            return SelfTestRng().Next(lo, hi + 1);
        }

        /// <summary>
        /// 核对并消费授权码 —— **两条输入路径共用**：
        ///   · `/maint <答案>`：`DoSelfTest` 进来后发现 `WaitingCode` ⇒ 走后一段
        ///   · **裸数字**：玩家直接发一条纯数字聊天，由 `ChatMessageHook` 拦下（见 `TryConsumeCodeInput`）
        ///
        /// 为什么要有"裸数字"这条：提示写的是"把答案**发送**过来"，玩家的直觉就是直接发那串数字。
        /// 只认命令形式的话，裸数字会当普通聊天广播出去、而玩家以为已经提交了 ——
        /// 实测房主正是这么踩的（发了 `000000`，等来"授权码错误"）。
        ///
        /// 一次性：无论对错都把 `WaitingCode` 置回 false，不给无限重试。
        /// </summary>
        private static bool HandleCodeInput(GamePlayer player, int deviceId, CommandChannel channel, string arg)
        {
            var s = _selfTest;
            if (s == null || player?.PublicInfo == null
                || s.PlayerId != player.PublicInfo.PlayerId || !s.WaitingCode)
                return false;                       // 没在等 ⇒ 不是授权码，交回调用方按原语义处理

            s.WaitingCode = false;
            int gen = s.Gen;

            // ★ 地缘约束：**操作位置不可变更** —— 授权码只能在发起时的那个房间提交。
            //   游戏里能敲命令的只有四个通讯台所在的位置，所以"换了房间"等于"换了通讯台"。
            //   验证与等待期间不检查（那两阶段本就无事可做），判据只在**提交**这一瞬；
            //   这也让"异区的人替你交码"不可能成立。
            if (PowerSelfTestFeature.RequireSameArea?.Value ?? true)
            {
                if (AreaRoomIdOf(player) != s.AreaRoomId)
                {
                    string area = s.AreaName ?? "原区域";
                    SelfTestClear();
                    Reply(player, deviceId, channel, T("SelfTestWrongArea", "room", area));
                    return true;
                }
            }

            if (!int.TryParse((arg ?? "").Trim(), out int v) || v != s.Answer)
            {
                SelfTestClear();
                Reply(player, deviceId, channel, T("SelfTestBadCode"));
                return true;
            }

            int sec = RandomActivateSec();
            Reply(player, deviceId, channel, T("SelfTestRunning", "sec", sec.ToString()));
            GameRoom.Instance?.PushAfter(sec * 1000, delegate { SelfTestActivate(gen); });
            return true;
        }

        // ⚠ 曾经想在这里加一个"裸数字也算授权码"的入口拦截（玩家直接发纯数字聊天就当提交），
        //   最后**没有采用**：`ChatMessageHook` 是所有聊天的公共入口，为一条命令在那里吞消息，
        //   影响面不成比例（等待期间发个 "123" 会被莫名吃掉），而且那属于"猜玩家意图"。
        //   ⇒ 改为**把输入方式写进文案**（`SelfTestPrompt` 与 `Desc_maint`），
        //     并在帮助里说明"要带 /maint 前缀"。命令形式是引擎既有语义，不会误伤聊天。

        private static bool DoSelfTest(GameRoom room, GamePlayer player, int deviceId,
                                       CommandChannel channel, string arg)
        {
            if (!PowerSelfTestFeature.Armed)
            {
                Reply(player, deviceId, channel, T("SelfTestUnavailable"));
                return false;
            }

            var info = player?.PublicInfo;
            if (info == null)
                return false;

            int pid = info.PlayerId;

            // ⓪ 全房共享冷却 —— 起点是"上一次流程**真正完成**"那一刻（`StartCooldown`），
            //    而不是"上次敲命令"。所以被拒（黑方）/ 超时 / 算错 / 换位置 这些路径
            //    都**不吃 CD**，可以立刻重试；只有效果真的落地了才开始计时。
            //    ⚠ 顺序：在"等这个玩家交授权码"之前判 —— 否则流程走到一半就会被自己的 CD 挡掉。
            //      真正的流程已持有 `_selfTest`，此时 CD 一般也没开始（上一次完成才会置它），
            //      但万一并发（旧流程刚完成、新流程已开）也要让进行中的那次优先。
            if (_selfTest == null)
            {
                float cdLeft = PowerSelfTestFeature.RemainingCooldown();
                if (cdLeft > 0f)
                {
                    int left = (int)global::System.Math.Ceiling(cdLeft);
                    Reply(player, deviceId, channel,
                        T("RoomCooldown", "name", "maint", "sec", left.ToString()));
                    return true;
                }
            }

            // ① 正在等这个玩家交授权码 ⇒ 这次输入就是授权码（一次性：无论对错都作废）
            if (_selfTest != null && _selfTest.PlayerId == pid && _selfTest.WaitingCode)
                return HandleCodeInput(player, deviceId, channel, arg);

            // ② 别人正在流程中（含"别人卡的授权码"）⇒ 拒绝。
            //    ★ 地缘约束开着时，**异区**的拒绝要说清"正在哪个房间、由谁操作"，
            //      让异区玩家明白"不是不让你用，是这里正有人在用"。
            //      同区则沿用普通的"已被占用"（同一时间只能一人，与他站哪儿无关）。
            if (_selfTest != null)
            {
                var s0 = _selfTest;
                bool geo = PowerSelfTestFeature.RequireSameArea?.Value ?? true;

                if (geo && AreaRoomIdOf(player) != s0.AreaRoomId)
                {
                    var owner = FindPlayerById(room, s0.PlayerId);
                    string text = T("SelfTestBusyElsewhere")
                        .Replace("{room}", s0.AreaName ?? "其他区域")
                        .Replace("{name}", owner != null ? RoleNameOf(owner) : "其他操作者")
                        .Replace("{id}", s0.PlayerId.ToString());
                    Reply(player, deviceId, channel, text);
                    return true;
                }

                Reply(player, deviceId, channel, T("SelfTestBusy"));
                return true;
            }

            // ③ 开一轮新的
            string q = BuildQuestion(out int answer);
            var session = new SelfTestSession
            {
                PlayerId = pid,
                DeviceId = deviceId,
                Channel = channel,
                Gen = ++_selfTestGen,
                Answer = answer,
                WaitingCode = false,
                // 地缘约束的两个判据：发起房间 id（提交授权码时核对）
                // 与房间显示名（异区拒绝的消息里要报出来）
                AreaRoomId = AreaRoomIdOf(player),
                AreaName = AreaNameOf(player),
            };
            _selfTest = session;

            Reply(player, deviceId, channel, T("SelfTestRecv"));

            int verifyMs = (int)((PowerSelfTestFeature.VerifySec?.Value ?? 5f) * 1000f);
            int sgen = session.Gen;
            room.PushAfter(verifyMs, delegate { SelfTestVerify(sgen, q); });
            return true;
        }

        /// <summary>阶段②：3 秒后判定阵营。黑方在这里被拒（演出效果，不是权限系统）。</summary>
        private static void SelfTestVerify(int gen, string question)
        {
            var s = _selfTest;
            if (s == null || s.Gen != gen)
                return;

            var room = GameRoom.Instance;
            var player = FindPlayerById(room, s.PlayerId);
            if (player == null)
            {
                SelfTestClear();                        // 退局 ⇒ 释放占用
                return;
            }

            if (player.Color != EPlayerColor.White)
            {
                int dev = s.DeviceId;
                var ch = s.Channel;
                SelfTestClear();
                Reply(player, dev, ch, T("SelfTestDenied"));
                return;
            }

            s.WaitingCode = true;

            // ★ 两句提示**分两条独立回执发**，中间留足间隔。
            //   不要合成一条靠 `Reply` 自动分段：那个机制按"每 3 个渲染行一条"切，
            //   而这两句合起来是 4 行 ⇒ 切点落在第二句中间 ⇒ 客户端少渲染一条时
            //   **含算式的那半截整段消失**（实测连续踩两次）。
            //   间隔也不能太短：客户端对同一秒内刷出的多条消息只保留一条。
            string line1 = T("SelfTestPrompt1")
                .Replace("{name}", RoleNameOf(player))
                .Replace("{id}", s.PlayerId.ToString())
                .Replace("{room}", AreaNameOf(player));

            string line2 = T("SelfTestPrompt2")
                .Replace("{q}", question);

            Reply(player, s.DeviceId, s.Channel, line1);

            // ⚠ 用**本地捕获**而不是在闭包里重新查玩家：玩家中途退局时 `FindPlayerById`
            //   会返回 null，那样第二句就静默丢失；而这里的 `player` 引用仍然有效，
            //   `SendChat` 内部会自己判 `Session` 是否还在。
            //   ⚠ 变量名不能叫 `dev`/`ch` —— 上面那个 `if` 块里已经用过同名的，
            //     C# 不允许内层作用域的名字与方法级局部变量重名（会报 CS0136）。
            var boundPlayer = player;
            int promptDev = s.DeviceId;
            var promptCh = s.Channel;
            room?.PushAfter(ReplyChunkGapMs + 300, delegate
            {
                Reply(boundPlayer, promptDev, promptCh, line2);
            });

            int timeoutMs = (int)((PowerSelfTestFeature.CodeTimeoutSec?.Value ?? 20f) * 1000f);
            room?.PushAfter(timeoutMs, delegate { SelfTestExpire(gen); });
        }

        /// <summary>阶段③超时：长时间没交授权码。</summary>
        private static void SelfTestExpire(int gen)
        {
            var s = _selfTest;
            if (s == null || s.Gen != gen || !s.WaitingCode)
                return;                                 // 已答对 / 已作废 ⇒ 不打扰

            int dev = s.DeviceId;
            var ch = s.Channel;
            int pid = s.PlayerId;
            SelfTestClear();

            var player = FindPlayerById(GameRoom.Instance, pid);
            if (player != null)
                Reply(player, dev, ch, T("SelfTestExpired"));
        }

        /// <summary>阶段④：自检结束，按当前电力状态落地效果。</summary>
        private static void SelfTestActivate(int gen)
        {
            var s = _selfTest;
            if (s == null || s.Gen != gen)
                return;

            int dev = s.DeviceId;
            var ch = s.Channel;
            int pid = s.PlayerId;
            SelfTestClear();

            var player = FindPlayerById(GameRoom.Instance, pid);
            if (player == null)
                return;                                 // 退局 ⇒ 不施加效果

            int broken = 0;
            try { broken = Server.Game.DeviceManager.Instance?.GetDisconnectFuseCount() ?? 0; }
            catch { }

            if (broken > 0)
            {
                // 电力中断 ⇒ 修好所有电箱。文案里的 [1-3] 是房主给的口径，
                // 所以对外只说 1~3，不暴露真实数量（真实值进日志）。
                int n = PowerSelfTestFeature.ApplyRestore();
                int shown = global::System.Math.Min(3, global::System.Math.Max(1, n));
                Reply(player, dev, ch, T("SelfTestRestored", "n", shown.ToString()));
            }
            else
            {
                // 电力正常 ⇒ 电箱下线一段时间（已派发的收回，未派发的把那次派发顺延）
                PowerSelfTestFeature.ApplyNormal();
                Reply(player, dev, ch, T("SelfTestOk"));
            }

            // ★ 全房共享冷却**从这里开始** —— 效果已经落地，才算"一次有效流程完成"。
            //   失败路径（黑方被拒 / 超时 / 算错 / 换位置 / 退局）都不调用它，所以不吃 CD。
            PowerSelfTestFeature.StartCooldown();
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

            // ★ 段与段之间**必须留间隔**：同一帧连发多条 `S_CHAT_MESSAGE` 时，
            //   客户端只保留一条（`DefaultTexts` 里那句"多条消息一次性刷出、前面的会被顶掉"
            //   说的就是这个坑）。
            //   实测踩过：自检的授权码提示折行后是 4 行 ⇒ 拆成 2 条 ⇒ **第 2 条（含算式）被顶掉**，
            //   玩家永远看不到"该算什么"，只能去照抄文案里的格式占位（于是必然报"授权码错误"）。
            //   第 1 条立即发（保持回执的即时感），其余按间隔排出去。
            int delayMs = 0;
            for (int start = 0; start < wrapped.Count; start += MaxLinesPerMessage)
            {
                int count = wrapped.Count - start < MaxLinesPerMessage
                    ? wrapped.Count - start
                    : MaxLinesPerMessage;

                string chunk = string.Join("\n", wrapped.GetRange(start, count));

                if (delayMs <= 0)
                {
                    if (!SendChat(player, type, deviceId, chunk))
                        return;
                }
                else
                {
                    var captured = chunk;
                    int delay = delayMs;
                    try
                    {
                        GameRoom.Instance?.PushAfter(delay, delegate { SendChat(player, type, deviceId, captured); });
                    }
                    catch (global::System.Exception ex)
                    {
                        Plugin.Log.LogWarning($"[HS] 命令：回执延迟发送失败 — {ex.Message}");
                    }
                }

                delayMs += ReplyChunkGapMs;
            }
        }

        /// <summary>一条回执的实际发送。返回 false 表示发送失败（调用方据此中止后续分段）。</summary>
        private static bool SendChat(GamePlayer player, EChatType type, int deviceId, string text)
        {
            if (player?.Session == null || string.IsNullOrEmpty(text))
                return false;
            try
            {
                player.Session.Send(new S_CHAT_MESSAGE
                {
                    Type = type,
                    DeviceId = deviceId,
                    Text = text,
                    PlayerId = player.PublicInfo?.PlayerId ?? 0,
                    Time = (int)(TimeManager.Instance?.SurviveTime ?? 0f),
                    IsDead = false
                });
                return true;
            }
            catch (global::System.Exception ex)
            {
                Plugin.Log.LogWarning($"[HS] 命令：回执失败 — {ex.Message}");
                return false;
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
