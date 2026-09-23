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

        [ConfigField(true, "房主用 hs_* 命令改动玩法设置时，向全场播报这次调整（仅在生存阶段播报）。")]
        public static ConfigEntry<bool> AnnounceRuleChanges;
        [ConfigField(true, "有玩家进入房间时，单独向他播报玩法规则。")]
        public static ConfigEntry<bool> WelcomeOnJoin;

        [ConfigField(28f, "聊天栏单行宽度上限（半角单位，中文按 2 计）。超出会另起一行；设 0 关闭自动折行。",
            Min = 0f, Max = 200f)]
        public static ConfigEntry<float> MaxLineWidth;
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
                                NoticeToWrapped(p, joinText);
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

        /// <summary>把整段文本按行宽折好后，逐条发给某人（避免聊天栏截断）。</summary>
        private static void NoticeToWrapped(GamePlayer player, string text)
        {
            if (player?.Session == null || string.IsNullOrEmpty(text))
                return;

            int width = (int)(MaxLineWidth?.Value ?? 28f);
            var lines = new List<string>();
            foreach (var raw in text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n'))
                lines.AddRange(WrapLine(raw, width));

            foreach (var line in lines)
            {
                if (string.IsNullOrEmpty(line))
                    continue;
                player.Session.Send(BuildText(line, EChatType.NormalChat));
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
                    __instance.PushAfter(delay < 0 ? 0 : delay, () => NoticeToWrapped(player, text));
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

        private static void SendStartTips(GameRoom room)
        {
            if (room == null)
                return;

            bool auto = AutoAssignBlack;

            foreach (var player in room.Players)
            {
                if (player?.Session == null || player.PublicInfo == null)
                    continue;

                string body;
                if (!auto)
                    body = StartBodySelfServe?.Value;
                else if (player.Color == EPlayerColor.Black || player.Color == EPlayerColor.Dark)
                    body = StartBodyBlack?.Value;
                else
                    body = StartBodyWhite?.Value;

                NoticeToWrapped(player, Titled(TextService.Format(body)));
            }
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

            Notice(GameRoom.Instance, TextService.Format(WeaponTaken?.Value));
        }
    }
}
