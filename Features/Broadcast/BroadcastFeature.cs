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
    /// 播报系统：开局玩法介绍、新玩家进房介绍、死亡通告、武器被取走的匿名通告。文本全部可在 .cfg 里改。
    ///
    /// 通道：房主自造 S_CHAT_MESSAGE{Type = SecretChat} 后广播（或单发）。
    /// 之所以选它 —— 游戏里并不存在能承载任意文字的系统公告
    /// （S_SYSTEM_MESSAGE 只有 Type + Value(int)，文案由本地化键 枚举名+"Alert" 决定），
    /// 而密聊是唯一能携带任意字符串且客户端有现成弹泡 UI（UI_SecretChatOverlay :82596）的包。
    /// 客户端接收端不做颜色校验，因此发给全员有效，且不需要客户端装本模块。
    ///
    /// 已知限制（均为客户端行为，属预期）：
    ///   - 死亡玩家收不到弹泡（:82641 的 !IsAlive 会 skip），故"全员广播"对死者无效；
    ///   - 播报会写进 SecretLog 并在主机迁移时被重放（ReplaySecretChatTo :171972）；
    ///   - 打开对讲机界面时 AddRow（:46801）会用 DeviceId 反查名字，故这里用魔数，那里显示为未知；
    ///   - Time 必须填当前 SurviveTime，否则客户端按过期丢弃（:82645）。
    /// </summary>
    [HarmonyPatch]
    [PatchFeature(
        section: "Broadcast",
        description: "播报：开局玩法介绍、新玩家进房介绍、死亡通告、武器被取走通告。文本在下方各项配置，支持 {name} {alive} {total} 占位符。",
        defaultEnabled: true,
        side: FeatureSide.Host)]
    internal static class BroadcastFeature
    {
        /// <summary>自定义设备 ID，避开真实 ChatDevice，避免被对讲机界面反查到别的名字。</summary>
        private const int MagicDeviceId = 999999;

        [ConfigField(true, "启用播报。")]
        public static ConfigEntry<bool> Enabled;

        [ConfigField("捉迷藏模式", "介绍标题（开局介绍与进房介绍共用）。")]
        public static ConfigEntry<string> IntroTitle;

        [ConfigField("黑方目标：在时间耗尽前淘汰所有白方。\\n白方目标：撑到限制时间归零，或完成全部任务。\\n黑方视野受限，只能看见附近的玩家。",
            "介绍正文。用 \\n 表示换行。")]
        public static ConfigEntry<string> IntroBody;

        [ConfigField("{name} 已被淘汰（剩余 {alive}/{total}）",
            "死亡通告模板。占位符：{name} 死亡玩家名 / {alive} 剩余存活的白方数（不含黑方）/ {total} 开局人数。")]
        public static ConfigEntry<string> DeathAnnounce;

        [ConfigField("捉迷藏开始了~",
            "武器被取走时的匿名通告（不含玩家名，避免暴露黑方身份）。")]
        public static ConfigEntry<string> WeaponTaken;

        [ConfigField(true, "有玩家进入房间时，单独向他播报一次玩法介绍。")]
        public static ConfigEntry<bool> WelcomeOnJoin;

        [ConfigField(2500, "进房介绍的延迟毫秒数（等客户端就绪）。", Min = 0f, Max = 30000f)]
        public static ConfigEntry<int> WelcomeDelayMs;

        private static bool Ready => !ModeRuntime.Bypass && Enabled != null && Enabled.Value;

        private static S_CHAT_MESSAGE BuildChat(string text) => new S_CHAT_MESSAGE
        {
            Type = EChatType.SecretChat,
            Text = text,
            DeviceId = MagicDeviceId,
            Time = TimeManager.Instance.SurviveTime
        };

        /// <summary>向全体广播一条自定义文本。</summary>
        private static void Notice(GameRoom room, string text)
        {
            if (room == null || string.IsNullOrEmpty(text))
                return;

            room.Broadcast(BuildChat(text));
        }

        /// <summary>只发给单个玩家（用于进房介绍）。</summary>
        private static void NoticeTo(GamePlayer player, string text)
        {
            if (player?.Session == null || string.IsNullOrEmpty(text))
                return;

            player.Session.Send(BuildChat(text));
        }

        private static string IntroText()
        {
            string title = IntroTitle?.Value;
            string body = TextService.Format(IntroBody?.Value);
            return string.IsNullOrEmpty(title) ? body : $"{title}\n{body}";
        }

        // ── 开局介绍 ────────────────────────────────────────────────
        [HarmonyPatch(typeof(GameRoom), "StartSurvive")]
        [HarmonyPostfix]
        private static void PostfixStartSurvive(GameRoom __instance)
        {
            Diagnostics.Hit("Broadcast");
            if (!Ready)
                return;

            string text = IntroText();
            if (string.IsNullOrEmpty(text))
                return;

            // 延迟到与发刀相近的时机，确保客户端已进入对局
            __instance.PushAfter(3000, () => Notice(__instance, text));
        }

        // ── 新玩家进房：单独向他介绍规则 ─────────────────────────────
        // HandleEnterPlayer（:170455）是每个玩家进房的必经路径（对真实客户端而言）。
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

                string text = IntroText();
                if (string.IsNullOrEmpty(text))
                    return;

                int delay = WelcomeDelayMs?.Value ?? 2500;
                __instance.PushAfter(delay < 0 ? 0 : delay, () => NoticeTo(player, text));
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

            // 剩余数只统计白方：玩家关心的是"还剩几个白方"，黑方自己不该计入
            int aliveWhites = room.AlivePlayers.Count(p =>
                p?.PublicInfo != null
                && !p.IsSpectator
                && p.Color != EPlayerColor.Black
                && p.Color != EPlayerColor.Dark);

            string name = __instance?.Name ?? "某人";
            string text = TextService.Format(
                DeathAnnounce?.Value,
                ("name", name),
                ("alive", aliveWhites.ToString()),
                ("total", TotalWhites(room).ToString()));

            Notice(room, text);
        }

        /// <summary>
        /// 开局白方总数。自动发刀模式下开局就有一人被指定为黑方，他不该算进白方基数；
        /// 自行拿刀模式开局全是白方（黑方是后来抢到刀才产生的），不能减。
        /// </summary>
        private static int TotalWhites(GameRoom room)
        {
            int total = room.RoundStartPlayerCount;
            if (WeaponGrantFeature.GiveAtStart != null && WeaponGrantFeature.GiveAtStart.Value)
                total -= 1;

            return total < 0 ? 0 : total;
        }

        // ── 武器被取走的匿名通告 ─────────────────────────────────────
        [HarmonyPatch(typeof(ItemManager), nameof(ItemManager.InsertWeapon))]
        [HarmonyPostfix]
        private static void PostfixInsertWeapon(GamePlayer player)
        {
            if (!Ready)
                return;

            // 只有"玩家拿到武器并因此转为黑方"才算；黑幕(Dark)拿刀不转色
            if (player == null || player.Color != EPlayerColor.Black)
                return;

            Notice(GameRoom.Instance, TextService.Format(WeaponTaken?.Value));
        }
    }
}
