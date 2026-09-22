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
        public static ConfigEntry<bool> Enabled;

        [ConfigField("捉迷藏模式", "标题（进房介绍与开局提示共用）。")]
        public static ConfigEntry<string> IntroTitle;

        [ConfigField(
            "此房间已启用捉迷藏，具有特殊胜负条件\\n" +
            "黑方：在倒计时结束前杀死所有可以杀死的人，视野缩小，刀CD缩短\\n" +
            "白方：在倒计时结束前，避免死亡。通过完成任务可以缩短倒计时，倒计时结束后白方胜利。\\n" +
            "{knife}\\n" +
            "报告功能被禁用，不分配黑幕角色，部分角色的技能效果会有改变。",
            "进房介绍正文。{knife} 按发刀模式替换为下面两行之一。用 \\n 表示换行。")]
        public static ConfigEntry<string> JoinBody;

        [ConfigField("当前，游戏开始后刷新刀具", "自行拿刀模式下 {knife} 的内容。")]
        public static ConfigEntry<string> JoinKnifeSelfServe;

        [ConfigField("当前，黑方角色将会自动指定。", "自动发刀模式下 {knife} 的内容。")]
        public static ConfigEntry<string> JoinKnifeAuto;

        [ConfigField("在倒计时结束之前，寻找刀具开始杀戮，或者完成任务逃离杀戮~",
            "开局提示：自行拿刀模式（所有人同一句）。")]
        public static ConfigEntry<string> StartBodySelfServe;

        [ConfigField("开始杀戮、开始搜索吧~在倒计时结束之前",
            "开局提示：自动发刀模式下发给黑方。")]
        public static ConfigEntry<string> StartBodyBlack;

        [ConfigField("躲避杀手，完成任务，在倒计时结束之前。祝你好运~",
            "开局提示：自动发刀模式下发给白方。")]
        public static ConfigEntry<string> StartBodyWhite;

        [ConfigField("{name} 已经死亡({alive}/{total})",
            "死亡通告。占位符：{name} 死者名 / {alive} 剩余存活白方数（含露娜）/ {total} 开局白方总数。")]
        public static ConfigEntry<string> DeathAnnounce;

        [ConfigField("捉迷藏开始了~", "拿刀通告（仅自行拿刀模式发出；自动指定黑方时不发）。")]
        public static ConfigEntry<string> WeaponTaken;

        [ConfigField(true, "有玩家进入房间时，单独向他播报玩法规则。")]
        public static ConfigEntry<bool> WelcomeOnJoin;

        [ConfigField(2500, "进房介绍的延迟毫秒数（等客户端就绪）。", Min = 0f, Max = 30000f)]
        public static ConfigEntry<int> WelcomeDelayMs;

        [ConfigField(3200, "开局提示的延迟毫秒数（须晚于自动发刀，才能分出黑方）。", Min = 0f, Max = 30000f)]
        public static ConfigEntry<int> StartDelayMs;

        private static bool Ready => !ModeRuntime.Bypass && Enabled != null && Enabled.Value;

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
            if (room == null || string.IsNullOrEmpty(text))
                return;

            room.Broadcast(BuildChat(text));
        }

        private static void NoticeTo(GamePlayer player, string text)
        {
            if (player?.Session == null || string.IsNullOrEmpty(text))
                return;

            player.Session.Send(BuildChat(text));
        }

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

                int delay = WelcomeDelayMs?.Value ?? 2500;
                __instance.PushAfter(delay < 0 ? 0 : delay, () => NoticeTo(player, text));
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

                NoticeTo(player, Titled(TextService.Format(body)));
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
            Notice(room, TextService.Format(
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
