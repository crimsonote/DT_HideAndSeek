using System.Collections.Generic;
using System.Linq;
using BepInEx.Configuration;
using DummyClient;
using HarmonyLib;
using Protocol;
using Server.Game;
using HideAndSeek.Core;
using HideAndSeek.Features.Weapon;
using GamePlayer = Server.Game.Player;

namespace HideAndSeek.Features.Broadcast
{
    /// <summary>
    /// 播报：进房规则介绍、开局身份提示、死亡通告、拿刀通告。文本全部可在 .cfg 里改。
    ///
    /// 通道：房主自造 S_CHAT_MESSAGE{Type = SecretChat}。游戏里没有能承载任意文字的系统公告
    /// （S_SYSTEM_MESSAGE 只有 Type + Value(int)），而密聊是唯一能携带任意字符串、
    /// 且客户端有现成弹泡 UI（UI_SecretChatOverlay :82596）的包。客户端不做颜色校验，
    /// 所以不需要客户端装本模块。
    ///
    /// 分发方式：
    ///   进房介绍 —— 单发给该玩家，{knife} 按发刀模式替换
    ///   开局提示 —— 单发，且按身份选三种文案之一（自行拿刀 / 黑方 / 白方）
    ///   死亡、拿刀 —— 全员广播
    ///   公开黑方身份（RevealBlackOnKnife）—— 拿刀通告内换行追加；开局发刀模式追加进白方
    ///   开局消息，并把该条消息按 DeviceChat 记进公共发信机（可回看，见 NoticeToWhiteChannel）
    ///
    /// 已知限制（客户端行为，属预期）：死亡玩家收不到弹泡（:82641 的 !IsAlive 会 skip）；
    /// 播报会写进 SecretLog 并在主机迁移时重放（ReplaySecretChatTo :171972）；
    /// 对讲机界面 AddRow（:46801）会用 DeviceId 反查名字，故这里用魔数；
    /// Time 必须填当前 SurviveTime，否则客户端按过期丢弃（:82645）。
    /// </summary>
    [HarmonyPatch]
    [PatchFeature(
        section: "Broadcast",
        description: "播报：进房规则介绍、开局身份提示、死亡通告、拿刀通告。支持 {name} {alive} {total} {knife} {sec} 占位符。",
        defaultEnabled: true,
        side: FeatureSide.Host)]
    internal static class BroadcastFeature
    {
        /// <summary>自定义设备 ID，避开真实 ChatDevice。</summary>
        private const int MagicDeviceId = 999999;

        [ConfigField(true, "启用播报。")]
        public static ConfigEntry<bool> AnnounceEnabled;

        [ConfigField("捉迷藏模式", "标题（进房介绍与开局提示共用）。")]
        public static ConfigEntry<string> IntroTitle;

        [ConfigField(
            "此房间已启用捉迷藏，具有特殊胜负条件\\n" +
            "黑方：在倒计时结束前杀死所有可以杀死的人，视野缩小，刀CD缩短\\n" +
            "白方：在倒计时结束前，避免死亡。通过完成任务可以缩短倒计时，倒计时结束后白方胜利。\\n" +
            "{knife}\\n" +
            "报告功能被禁用，不分配黑幕角色，部分角色的技能效果会有改变。\n" +
            "除此之外，可以在发信站输入/help来获得与使用部分指令以进行某些操作。",
            "进房介绍正文。{knife} 按发刀模式替换为下面两行之一。用 \\n 表示换行。")]
        public static ConfigEntry<string> JoinBody;

        [ConfigField("当前，游戏开始后刷新刀具", "自行拿刀模式下 {knife} 的内容。")]
        public static ConfigEntry<string> JoinKnifeSelfServe;

        [ConfigField("当前，黑方角色将会自动指定。", "自动发刀模式下 {knife} 的内容。")]
        public static ConfigEntry<string> JoinKnifeAuto;

        [ConfigField("在倒计时结束之前，寻找凶器开始追捕，或者完成任务逃离追捕~\n" +
                                "或许也可以前往发信站使用/help来获得一些帮助。两个频道不一样呢~",
            "开局提示：自行拿刀模式（所有人同一句）。")]
        public static ConfigEntry<string> StartBodySelfServe;

        [ConfigField("开始杀戮、开始搜索吧~或许也可以在发信站获得帮助(/help)在倒计时结束之前。",
            "开局提示：自动发刀模式下发给黑方。")]
        public static ConfigEntry<string> StartBodyBlack;

        [ConfigField("躲避杀手，完成任务，或许也可以在发信站获得帮助(/help)。在倒计时结束之前。祝你好运~",
            "开局提示：自动发刀模式下发给白方。")]
        public static ConfigEntry<string> StartBodyWhite;

        [ConfigField("{name} 已经死亡({alive}/{total})",
            "死亡通告。占位符：{name} 死者名 / {alive} 剩余存活白方数（含露娜）/ {total} 开局白方总数。")]
        public static ConfigEntry<string> DeathAnnounce;

        [ConfigField("捉迷藏开始了~", "拿刀通告（仅自行拿刀模式发出；自动指定黑方时不发）。")]
        public static ConfigEntry<string> WeaponTaken;

        /// <summary>
        /// 是否把黑方身份（角色名 + 玩家昵称）公开出去。
        /// 拿刀模式：换行追加进同一条拿刀通告（WeaponTaken）；开局发刀模式：追加进
        /// 开局白方消息的末尾，并把那条消息整体记进公共发信机备查。
        /// </summary>
        [ConfigField(false, "公开黑方身份（角色名 + 玩家昵称）：拿刀通告内换行追加；开局发刀模式追加到白方开局消息末尾，并把该消息记进公共发信机。")]
        public static ConfigEntry<bool> RevealBlackOnKnife;

        [ConfigField(true, "房主用 hs_* 命令改动玩法设置时，向全场播报这次调整（仅在生存阶段播报）。")]
        public static ConfigEntry<bool> AnnounceRuleChanges;
        [ConfigField(true, "有玩家进入房间时，单独向他播报玩法规则。")]
        public static ConfigEntry<bool> WelcomeOnJoin;

        [ConfigField(52f, "聊天栏单行宽度上限（半角单位，中文按 2 计，52 = 26 个汉字）。超出会另起一行；设 0 关闭自动折行。",
            Min = 0f, Max = 200f)]
        public static ConfigEntry<float> MaxLineWidth;
        [ConfigField(2500, "进房介绍每条消息之间的间隔（毫秒）。0 = 一次性发完。仅在会拆条时才有意义。", Min = 0f, Max = 15000f)]
        public static ConfigEntry<int> MessageIntervalMs;

        [ConfigField(10000, "进房介绍的延迟毫秒数（等客户端把场景加载完，过早发送会丢失）。", Min = 0f, Max = 60000f)]

        public static ConfigEntry<int> WelcomeDelayMs;

        [ConfigField(3200, "开局提示的延迟毫秒数（须晚于自动发刀，才能分出黑方）。", Min = 0f, Max = 30000f)]
        public static ConfigEntry<int> StartDelayMs;

        /// <summary>对局期间改动过、待回大厅播报的规则（以文本自身为键，天然去重）。</summary>
        /// <summary>对局中加入（旁观）的人，等回到大厅再补发进房介绍。</summary>
        private static readonly HashSet<int> PendingWelcome = new HashSet<int>();

        private static readonly Dictionary<string, string> PendingRuleChanges = new Dictionary<string, string>();
        private static bool Ready => !ModeRuntime.Bypass && AnnounceEnabled != null && AnnounceEnabled.Value;

        /// <summary>当前是否为"自动指定黑方"模式（开局直接发刀）。</summary>
        private static bool AutoAssignBlack
            => WeaponGrantFeature.GiveAtStart != null && WeaponGrantFeature.GiveAtStart.Value;

        private static S_CHAT_MESSAGE BuildChat(string text) => new S_CHAT_MESSAGE
        {
            Type = EChatType.SecretChat,
            Text = text,
            DeviceId = MagicDeviceId,
            Time = TimeManager.Instance.SurviveTime
        };

        private static void Notice(GameRoom room, string text)
        {
        // 第一条恒为 SecretChat：生存阶段弹泡 + 进密聊记录。
        room.Broadcast(BuildText(text, EChatType.SecretChat));

        // 第二条按阶段自适应 —— 客户端每个阶段只有一条文字链路有效：
        //   大厅/学裁 → NormalChat（聊天面板）；它在生存阶段**不渲染**
        //   生存阶段  → DeviceChat（公共发信机 NormalLog）
        EChatType second = (room.State == EGameState.Lobby || room.State == EGameState.Trial)
            ? EChatType.NormalChat
            : EChatType.DeviceChat;
        room.Broadcast(BuildText(text, second));
        }

        /// <summary>
        /// 播报一条"规则被调整"。由 hs_* 命令在改动配置后调用 ——
        /// 房主改了玩法，在场玩家理应知情，否则只能靠察觉行为异常去猜。
        /// 仅在生存阶段播报：大厅里说这些没有意义。
        /// </summary>
        public static void AnnounceRule(string change)
        {
            if (!Ready || string.IsNullOrEmpty(change))
                return;
            if (AnnounceRuleChanges == null || !AnnounceRuleChanges.Value)
                return;

            var room = GameRoom.Instance;
            if (room == null)
                return;

            // 大厅里改 → 立即播报；对局中改 → 攒着，回大厅再统一播报。
            // 对局中不播的原因：那等于当场告诉所有人"房主正在动规则"，本身就是额外信息。
            if (room.State == EGameState.Lobby)
            {
                Notice(room, "【规则调整】" + change);
                return;
            }

            PendingRuleChanges[change] = change;
        }

        /// <summary>回大厅时，把对局期间改动过的规则一次性播报出来。</summary>
        [HarmonyPatch(typeof(GameRoom), "StartLobby")]
        internal static class LobbyAnnounceHook
        {
            [HarmonyPostfix]
            private static void Postfix(GameRoom __instance)
            {
                // 补发进房介绍：给"对局中以旁观身份加入"的人。
                // 回大厅后客户端又要在场景间切换，所以仍留一点延迟。
                if (PendingWelcome.Count > 0)
                {
                    string joinText = JoinText();
                    var ids = new List<int>(PendingWelcome);
                    PendingWelcome.Clear();
                    __instance.PushAfter(1000, delegate
                    {
                        foreach (int pid in ids)
                        {
                            var p = GameRoom.Instance?.Players?.Find(x => x?.PublicInfo?.PlayerId == pid);
                            if (p != null)
                                SendWrappedTo(p, EChatType.NormalChat, joinText, MessageIntervalMs?.Value ?? 2500);
                        }
                    });
                }

                if (PendingRuleChanges.Count == 0)
                    return;

                if (Ready && AnnounceRuleChanges != null && AnnounceRuleChanges.Value)
                {
                    foreach (var kv in PendingRuleChanges)
                        Notice(__instance, "【规则调整】" + kv.Value);
                }

                PendingRuleChanges.Clear();
            }
        }
        /// <summary>
        /// 单独发给某人的提示（进房介绍走这里）。
        ///
        /// 必须用 **NormalChat** 而不是 SecretChat：
        /// 客户端 UI_SecretChatOverlay.OnSecretChatReceived 有 `State != Survive → skip`，
        /// 而进房介绍是在大厅发的，走 SecretChat 的话弹泡会被直接丢弃，新人什么都看不到。
        /// NormalChat 则进聊天栏，任何阶段都显示。
        ///
        /// 关于长度：SanitizeChat 的 100 字截断只发生在原版 Handle_C_CHAT_MESSAGE 内部；
        /// 我们直接构造包，不经过那个入口，因此不受 100 字限制。
        /// </summary>
        /// <summary>
        /// 只发密聊通道（SecretChat）的全房通知，**不进普通聊天栏**。
        /// 死亡通告用它 —— 黑方需要知道还剩几人，白方不需要看到这条。
        /// </summary>
        private static void NoticeDeath(GameRoom room, string text)
        {
            if (room == null || string.IsNullOrEmpty(text))
                return;

            // SecretChat → 生存阶段即时弹泡 + 进密聊记录（黑方据此判断剩余人数）
            // DeviceChat → 进公共发信机（NormalLog），便于事后回看
            // 不能用 NormalChat：客户端只在 大厅/裁判 阶段渲染它，生存阶段是白发。
            room.Broadcast(BuildText(text, EChatType.SecretChat));
            room.Broadcast(BuildText(text, EChatType.DeviceChat));
        }
        private static void NoticeTo(GamePlayer player, string text)
        {
            if (player?.Session == null || string.IsNullOrEmpty(text))
                return;

            player.Session.Send(BuildText(text, EChatType.NormalChat));
        }

        /// <summary>
        /// 按显示宽度把一行切成多行。中文按 2 个半角单位计。
        /// 聊天栏放不下时会**直接截断**（不是折行），所以长文本必须自己先切开。
        /// </summary>
        private static List<string> WrapLine(string line, int maxWidth)
        {
            var result = new List<string>();
            if (string.IsNullOrEmpty(line)) { result.Add(""); return result; }
            if (maxWidth <= 0) { result.Add(line); return result; }

            var sb = new global::System.Text.StringBuilder();
            int w = 0;
            foreach (char c in line)
            {
                int cw = c > 0x7F ? 2 : 1;
                if (w + cw > maxWidth && sb.Length > 0)
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

        /// <summary>
        /// 一条消息最多合并几行。
        ///
        /// 上限由"单条消息的实际可读字数"反推：实测一条消息约能完整显示 84 个汉字，
        /// 而每行 26 汉字 → 3 行 = 78 字（≤84，安全），4 行 = 104 字（超了）。
        /// 原文里的换行与自动折行都计入行数，所以限定总行数即可保证视觉高度不超标。
        /// </summary>
        /// <summary>
        /// 一条消息最多合并几行。0 = 不拆（整段放进同一颗气泡）。
        ///
        /// 弹泡底层是 TMP_Text，支持 \n 多行 —— 一颗气泡能完整承载整段文本；
        /// 拆成多颗反而互相顶掉（UI_SecretChatOverlay.Spawn 会先 KillImmediate 上一条）。
        /// </summary>
        [ConfigField(3, "一条消息最多合并几行（仅作用于聊天栏，即大厅的进房介绍）。0 = 不拆。", Min = 0f, Max = 20f)]
        public static ConfigEntry<int> MaxLinesPerMessage;
        /// <summary>把整段文本按行宽折好后，逐条发给某人（避免聊天栏截断）。</summary>
        /// <summary>
        /// 原样发给该玩家：不做任何折行与拆分。
        /// 用于**气泡**（SecretChat）—— 底层 TMP_Text 自带换行，
        /// 原文里的 \n 就是作者的分行意图，再折一次反而打乱结构。
        /// </summary>
        private static void SendRawTo(GamePlayer player, EChatType chatType, string text)
        {
            if (player?.Session == null || string.IsNullOrEmpty(text))
                return;
            player.Session.Send(BuildText(text, chatType));
        }

        /// <summary>
        /// 折行后发给该玩家：按 MaxLineWidth 折、按 MaxLinesPerMessage 分组、按 intervalMs 分条。
        /// 用于**聊天栏**（NormalChat）—— 它放不下时是直接截断，不是折行，必须先切好。
        /// 通道、是否间隔都由调用方给定，本函数不做任何场景判断。
        /// </summary>
        private static void SendWrappedTo(GamePlayer player, EChatType chatType, string text, int intervalMs = 0)
        {
            if (player?.Session == null || string.IsNullOrEmpty(text))
                return;

            int width = (int)(MaxLineWidth?.Value ?? 52f);
            var lines = new List<string>();
            foreach (var raw in text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n'))
            {
                foreach (var w in WrapLine(raw, width))
                {
                    if (!string.IsNullOrEmpty(w))
                        lines.Add(w);
                }
            }

            int perMsg = MaxLinesPerMessage?.Value ?? 0;
            if (perMsg <= 0)
                perMsg = lines.Count;              // 0 = 折行后一次发完

            int index = 0;
            for (int i = 0; i < lines.Count; i += perMsg)
            {
                int take = global::System.Math.Min(perMsg, lines.Count - i);
                string chunk = string.Join("\n", lines.GetRange(i, take));
                int delay = intervalMs > 0 ? index * intervalMs : 0;

                if (delay <= 0)
                {
                    player.Session.Send(BuildText(chunk, chatType));
                }
                else
                {
                    string payload = chunk;
                    GameRoom.Instance?.PushAfter(delay, delegate
                    {
                        try { player?.Session?.Send(BuildText(payload, chatType)); }
                        catch (global::System.Exception ex) { Plugin.Log.LogWarning($"[HS] Broadcast：分段发送失败 — {ex.Message}"); }
                    });
                }
                index++;
            }
        }
        /// <summary>构造文字包。chatType 决定显示位置（NormalChat→聊天栏，SecretChat→弹泡/发信机）。</summary>
        private static S_CHAT_MESSAGE BuildText(string text, EChatType chatType) => new S_CHAT_MESSAGE
        {
            Type = chatType,
            Text = text,
            PlayerId = 0,
            DeviceId = (chatType == EChatType.SecretChat || chatType == EChatType.DeviceChat) ? MagicDeviceId : 0,
            Time = (int)(TimeManager.Instance?.SurviveTime ?? 0f)
        };

        private static string Titled(string body)
        {
            string title = IntroTitle?.Value;
            return string.IsNullOrEmpty(title) ? body : $"{title}\n{body}";
        }

        private static string JoinText()
        {
            string knife = AutoAssignBlack
                ? (JoinKnifeAuto?.Value ?? "")
                : (JoinKnifeSelfServe?.Value ?? "");

            return Titled(TextService.Format(JoinBody?.Value, ("knife", knife)));
        }

        // ── 进房介绍：单独发给该玩家 ─────────────────────────────────
        [HarmonyPatch(typeof(GameRoom), "HandleEnterPlayer")]
        internal static class EnterHook
        {
            [HarmonyPostfix]
            private static void Postfix(GameRoom __instance, HostPeerSession session)
            {
                if (!Ready)
                    return;
                if (WelcomeOnJoin == null || !WelcomeOnJoin.Value)
                    return;

                var player = session?.Player;
                if (player == null || player.Session == null)
                    return;

                string text = JoinText();
                if (string.IsNullOrEmpty(text))
                    return;

                // 大厅里进房 → 延迟发送（等客户端把场景加载完）
                if (__instance.State == EGameState.Lobby)
                {
                    int delay = WelcomeDelayMs?.Value ?? 10000;
                    __instance.PushAfter(delay < 0 ? 0 : delay, () => SendWrappedTo(player, EChatType.NormalChat, text, MessageIntervalMs?.Value ?? 2500));
                }
                else
                {
                    // 对局进行中以旁观身份加入：此刻发介绍没有显示链路（客户端弹泡要求 Survive，
                    // 聊天栏在旁观界面也不保证可见），记下来等回到大厅再补发。
                    int pid = player.PublicInfo?.PlayerId ?? 0;
                    if (pid != 0)
                    {
                        PendingWelcome.Add(pid);
                        Plugin.Log.LogInfo($"[HS] Broadcast：#{pid} 在对局中加入，进房介绍延后到回大厅补发。");
                    }
                }
            }
        }

        // ── 开局提示：按身份分别发给每个人 ───────────────────────────
        [HarmonyPatch(typeof(GameRoom), "StartSurvive")]
        [HarmonyPostfix]
        private static void PostfixStartSurvive(GameRoom __instance)
        {
            Diagnostics.Hit("Broadcast");
            if (!Ready)
                return;

            // 必须晚于自动发刀（默认 2500ms），否则分不出谁是黑方
            int delay = StartDelayMs?.Value ?? 3200;
            __instance.PushAfter(delay < 0 ? 0 : delay, () => SendStartTips(__instance));
        }

        /// <summary>是否把黑方身份公开出去。</summary>
        private static bool RevealEnabled
            => RevealBlackOnKnife != null && RevealBlackOnKnife.Value;

        /// <summary>
        /// 角色显示名（如「露娜」）。
        ///
        /// 链：<c>Player.CharacterId</c>（:175506，实为 PublicInfo.CharacterId）
        ///  → <c>DataManager.CharacterDic[id].Name</c>（:33130；**这是英文资源键**，如 "Luna"，
        ///     见 CharacterData.json:18 与 :179750）
        ///  → <c>DataManager.TextDic["Luna"].Text</c>（:33150 / :180003；按当前语言加载，:33225）
        ///     = 本地化显示名「露娜」（已核对 CHS_TextData：DataId "Luna" → Text "露娜"）。
        /// 游戏自己显示角色名走的正是这两步：UI_ShopPopup.RefreshCharacterName（:65402-65408）
        /// → SafeText（:67289-67300）→ Managers.GetText（:41819-41827）。
        ///
        /// 降级（绝不留空、不抛异常）：角色 DataId 无效 → "未知角色"；CharacterDic 里没这个名字 →
        /// "角色&lt;id&gt;"；TextDic 里没有该键 → 用英文键本身（如 "Medelin"）。
        ///
        /// ⚠ **不要"顺手统一"成 <c>Managers.GetText</c>（:41819）**。它是 TextDic.TryGetValue 的
        /// 一层包装，**命中时结果与本函数完全一致**，唯一差别是缺键时会 UnityEngine.Debug.LogError。
        /// 而上游数据就有对不上的键：101 号的 CharacterData.Name 在一份 dump 里是 "Medelin"
        /// （gamedata/CharacterData.json:4、gamedata2/CharacterData__data.json:7），另一份里是
        /// "Madeline"（gamedata2/CharacterData__common_data.json:13），而本地化 TextDic 只有
        /// "Madeline"（CHS_TextData__language.json:5055）。离线无法确定运行时用哪一份 —— 一旦是
        /// "Medelin"，开局发刀每选中一次 101 号黑方就会往 Player.log 刷一条
        /// "[Text] Missing TextData key: Medelin" 的**假告警**：那是上游数据问题，不该由我们触发
        /// 告警，而日志是本模块排障的主要手段。故此处直接读 TextDic，缺键静默降级。
        /// </summary>
        private static string CharacterNameOf(GamePlayer player)
        {
            int id = player?.CharacterId ?? 0;
            if (id <= 0)
                return "未知角色";

            string key = null;
            var characters = Managers.Data?.CharacterDic;
            if (characters != null && characters.TryGetValue(id, out var cd) && cd != null)
                key = cd.Name;

            if (string.IsNullOrEmpty(key))
                return "角色" + id;

            var texts = Managers.Data?.TextDic;
            if (texts != null && texts.TryGetValue(key, out var text) && text != null
                && !string.IsNullOrEmpty(text.Text))
                return text.Text;

            return key;
        }

        /// <summary>
        /// 局内黑方（含 Dark）的标签，形如 <c>「露娜」(昵称)</c> —— 角色名 + 玩家昵称。
        ///
        /// 身份只读 <see cref="GameRoom.Players"/> 的 Color，不向 WeaponGrantFeature 反查：
        /// 两条产生黑方的路径最终都走 ItemManager.InsertWeapon，而它在写 Weapon **之前**
        /// 就已把 Color 定为 Black（:172692-172697），所以调用方拿到的一定是最终值。
        /// </summary>
        private static List<string> BlackLabels(GameRoom room)
        {
            var labels = new List<string>();

            if (room?.Players == null)
                return labels;

            foreach (var p in room.Players)
            {
                if (p?.PublicInfo == null)
                    continue;
                if (p.Color != EPlayerColor.Black && p.Color != EPlayerColor.Dark)
                    continue;

                string nick = string.IsNullOrEmpty(p.Name) ? "某人" : p.Name;
                labels.Add($"「{CharacterNameOf(p)}」({nick})");
            }

            return labels;
        }

        /// <summary>黑方身份一行文本，如 <c>黑方是「露娜」(昵称)</c>；多个黑方同行并列；一个都没有时返回 null。</summary>
        private static string BlackRevealText(GameRoom room)
        {
            var labels = BlackLabels(room);
            return labels.Count == 0 ? null : "黑方是" + string.Join("、", labels);
        }

        private static void SendStartTips(GameRoom room)
        {
            if (room == null)
                return;

            bool auto = AutoAssignBlack;

            // 自动发刀模式下黑方由 WeaponGrantFeature 指定。两条 PushAfter 进的是同一个
            // JobTimer（按 execTick 排出队的优先队列），而发刀默认 2500ms 早于本提示默认
            // 3200ms，所以此处读到的 Color 已是最终值；若用户把 GrantDelayMs 调到大于
            // StartDelayMs，这里会拿不到人 —— 此时不猜、不编，只记一条警告。
            string reveal = auto && RevealEnabled ? BlackRevealText(room) : null;
            if (auto && RevealEnabled && string.IsNullOrEmpty(reveal))
                Plugin.Log.LogWarning(
                    "[HS] Broadcast：开局提示时还没有黑方（GrantDelayMs 是否大于 StartDelayMs？），本次不公开黑方身份。");

            // 白方开局消息（已含黑方身份）。循环外只算一次：所有白方拿到的是同一条文本，
            // 而且该条整体还要作为一条记录进公共发信机。
            string whiteTip = null;
            if (auto && !string.IsNullOrEmpty(reveal))
                whiteTip = Titled(TextService.Format(StartBodyWhite?.Value)) + "\n" + reveal;

            foreach (var player in room.Players)
            {
                if (player?.Session == null || player.PublicInfo == null)
                    continue;

                bool isBlack = player.Color == EPlayerColor.Black || player.Color == EPlayerColor.Dark;

                // 黑方身份追加在**同一条消息内**（白方开局消息末尾），不另发一条气泡 ——
                // UI_SecretChatOverlay 同一时刻只留一条，另发只会把这条顶掉。
                string tip;
                if (!auto)
                    tip = Titled(TextService.Format(StartBodySelfServe?.Value));
                else if (isBlack)
                    tip = Titled(TextService.Format(StartBodyBlack?.Value));
                else
                    tip = whiteTip ?? Titled(TextService.Format(StartBodyWhite?.Value));

                SendRawTo(player, EChatType.SecretChat, tip);   // 局内气泡：原样，不折行
            }

            // 白方频道（公共发信机）留一条同样的记录，供事后回看。
            if (whiteTip != null)
                NoticeToWhiteChannel(room, whiteTip);
        }

        /// <summary>
        /// 把一条文本记进**公共发信机**（聊天频道）。
        ///
        /// 与"气泡"是两条完全不同的链路，这也是不能拿独立 SecretChat 来充当发信机记录的原因：
        ///   SecretChat → 客户端 _secretChatQueue → Voice.SecretLog + OnSecretChatReceived
        ///                → 只有 UI_SecretChatOverlay 订阅（:82622-82623），且新的一条会先
        ///                  KillImmediate 掉上一条（:82666-82671）⇒ 只是"飘过去的一句话"。
        ///   DeviceChat → 客户端 _deviceChatQueue → Voice.NormalLog + OnDeviceChatReceived
        ///                （:41264-41272，仅存活玩家与侦探阶段保留）⇒ 发信机 UI 的一行，
        ///                每次打开发信机都从 NormalLog 全量重放（:46763-46766）⇒ 可回看。
        /// 服务端再记一份 GameRoom.DeviceChatLog（:171984），侦探阶段 ReplayDeviceChatTo
        /// 会把生存阶段错过的内容补发给死者（:171467）—— 游戏对真实发信机消息就是这么做的
        /// （RelayDeviceChat :174783 里的 room.RecordDeviceChat）。
        /// </summary>
        private static void NoticeToWhiteChannel(GameRoom room, string text)
        {
            if (room == null || string.IsNullOrEmpty(text))
                return;

            var packet = BuildText(text, EChatType.DeviceChat);
            room.Broadcast(packet);          // 存活者即时进 NormalLog 与发信机 UI
            room.RecordDeviceChat(packet);   // 服务端留档，侦探阶段补发给死者
        }

        // ── 死亡通告 ────────────────────────────────────────────────
        [HarmonyPatch(typeof(GamePlayer), nameof(GamePlayer.OnDead))]
        [HarmonyPostfix]
        private static void PostfixOnDead(GamePlayer __instance)
        {
            if (!Ready)
                return;

            var room = GameRoom.Instance;
            if (room == null || room.State != EGameState.Survive)
                return;

            int aliveWhites = room.AlivePlayers.Count(p =>
                p?.PublicInfo != null
                && !p.IsSpectator
                && p.Color != EPlayerColor.Black
                && p.Color != EPlayerColor.Dark);

            string name = __instance?.Name ?? "某人";
            // 死亡通告只走密聊通道：黑方据此判断剩余人数，白方不必看到。
            NoticeDeath(room, TextService.Format(
                DeathAnnounce?.Value,
                ("name", name),
                ("alive", aliveWhites.ToString()),
                ("total", TotalWhites(room).ToString())));
        }

        /// <summary>
        /// 开局白方总数。自动发刀模式下开局就有一人被指定为黑方，他不该算进白方基数；
        /// 自行拿刀模式开局全是白方（黑方是后来抢到刀才产生的），不能减。
        /// </summary>
        private static int TotalWhites(GameRoom room)
        {
            int total = room.RoundStartPlayerCount;
            if (AutoAssignBlack)
                total -= 1;

            return total < 0 ? 0 : total;
        }

        // ── 拿刀通告（仅自行拿刀模式）────────────────────────────────
        [HarmonyPatch(typeof(ItemManager), nameof(ItemManager.InsertWeapon))]
        [HarmonyPostfix]
        private static void PostfixInsertWeapon(GamePlayer player)
        {
            if (!Ready)
                return;

            // 自动指定黑方时刀是系统发的，不该说"捉迷藏开始了"（那意味着有人主动拿刀）
            if (AutoAssignBlack)
                return;

            if (player == null || player.Color != EPlayerColor.Black)
                return;

            var room = GameRoom.Instance;
            string text = TextService.Format(WeaponTaken?.Value);

            // 同一条消息内换行追加黑方身份：读完黑方身份仍是当前这一位（Color 已在
            // InsertWeapon 内先于 Weapon 赋值写好），因此不必等下一帧。
            // 拼在 Format 之后 —— 名字里的反斜杠不该被当成 \n 转义。
            if (RevealEnabled)
            {
                string reveal = BlackRevealText(room);
                if (!string.IsNullOrEmpty(reveal))
                    text = string.IsNullOrEmpty(text) ? reveal : text + "\n" + reveal;
            }

            Notice(room, text);
        }
    }
}
