using BepInEx.Configuration;
using HarmonyLib;
using Protocol;
using Server.Game;
using HideAndSeek.Core;
using HideAndSeek.Features.Replay;
using GamePlayer = Server.Game.Player;
using GameRoom = Server.Game.GameRoom;

namespace HideAndSeek.Features.Rule
{
    /// <summary>
    /// 有人成为黑方时，把原版那条「有人变成了黑」通告**也发给白方**（原版只发黑幕/死者）。
    ///
    /// ── 原版到底发的是什么（decomp 旧版 2026-09-22，行号可查；已用 Mono.Cecil 核对
    ///    2026-10-02 的新版程序集，逻辑逐条一致）──────────────────────────────
    ///   `Server.Game.Player.set_Color`（:175348-175395）里 `value == Black` 分支：
    ///       masterMind.Session.Send(new S_NOTIFY_BLACK{ PlayerId = 新黑方, ByHand });   // → 黑幕
    ///       Session.Send(new S_NOTIFY_BLACK{ PlayerId = masterMind, ByHand });          // → 新黑方本人
    ///       foreach (DeadPlayers) dead.Session.Send(new S_NOTIFY_BLACK{ PlayerId = 新黑方 });// → 死者
    ///   收件人**只有黑幕 / 新黑方本人 / 死者**，活着的白方一包都收不到。
    ///   客户端 `Handle_S_NOTIFY_BLACK`（:42493-42535）里那条条幅被**按本机身份**卡死：
    ///       if (myPlayer.Color == EPlayerColor.Dark) scene.NotifyNewBlack(名字);    // 「XXX 变成了黑。」条幅
    ///       else if (Managers.Game.IsAlive)          scene.SetBlackIntroInfo(...);  // 只是记下来，不弹条幅
    ///   而 `NotifyNewBlack`（:75243）读的文案是 `NotifyNewBlack` 表项
    ///   ＝「<color>{nickname}</color>变成了<color>黑</color>。」。
    ///
    ///   ⇒ **所以"把 S_NOTIFY_BLACK 转发给白方"是错的、也没用**：白方客户端根本不会走条幅那条
    ///     分支（它按 `MyPlayer.Color == Dark` 判），反而会把黑方 id 写进 `KnownBlackIds`
    ///     ⇒ 昵称变红 + 小地图/平板出现红点，破坏 `WhiteRadar` 的「白方恒不出现红点」不变量。
    ///     这正是 `ReplayDirector` 里那段"已移除的 NotifyKnownBlack"踩过的坑，别再走一遍。
    ///
    /// ── 那白方该收哪条 ────────────────────────────────────────────────
    ///   走**系统消息**：`ESystemMessageType.NewBlack`。
    ///   客户端 `ShowSystemMessage`（:75386-75455）对它是**兜底分支**（没有专门 case）：
    ///       textId = Enum.GetName(typeof(ESystemMessageType), type) + "Alert"  ⇒  "NewBlackAlert"
    ///       ShowBroadcastNotice(Managers.GetText("NewBlackAlert"))
    ///   中文文本表里这三条是这样配对的：
    ///       NotifyNewBlack    「{nickname}变成了黑。」   ← 给**黑幕**（走 S_NOTIFY_BLACK 那条路）
    ///       ChangeBlackAlert  「你已成为黑。」           ← 给**新黑方本人**（原版 ShowBlackIntroNotices 已弹）
    ///       NewBlackAlert     「刚才有人成为了黑。」     ← 给**其他人** —— 正是白方该收的这一条
    ///   原版这两个枚举值（`NewBlack` / `ChangeBlack`）**服务端从来没发过**（全程序集只有枚举定义），
    ///   但文案与兜底分支都在 ⇒ 直接复用即"文案一致"，不必自造字符串。
    ///
    /// ── 判据 / 收件人 ─────────────────────────────────────────────────
    ///   · 触发点：`GamePlayer.set_Color` 的 `value == Black`（与 `FuseboxRevealFeature.BecomeBlackHook`
    ///     同点，也与原版同一个点）—— 它同时覆盖"自己去武器架跑刀"（`ItemManager.InsertWeapon`
    ///     :172694 染黑）与"开局发刀"（同一条路）以及 `hs_*` 命令；
    ///   · 收件人：`Color == EPlayerColor.White`。黑幕原版已收到**带名字**那条、新黑方本人原版已收到
    ///     「你已成为黑。」，都不重复发 ⇒ 用 `AlertMessage` 逐个单发，而不是 `AlertMessageAllPlayers`。
    ///   · 要连**名字**一起公开是另一件事：见 `Broadcast` 段的 `RevealBlackOnKnife`
    ///     （走聊天气泡、默认关）。本功能只管"原版那条通告的收件人补齐"。
    ///
    /// ── 适宜时段（为什么必须三条一起查）────────────────────────────────
    ///   · `room.State == Survive`：大厅/选人/审判/总结算都不是"本局有人变黑"；
    ///   · `!EndingRuleFeature.Decided`：结局一旦裁定，`GameOver` 之后还有 7.5 秒、
    ///     白胜还有 13.7 秒**停在 Survive**，那时再弹条幅会盖在收尾画面上；
    ///   · `ReplayDirector.Current == Phase.Idle`：回放期间服务端**被刻意按在 Survive**
    ///     （`EnsureServerSurvive`），只看 State 根本拦不住 ⇒ 条幅会画到回放画面上。
    /// </summary>
    [HarmonyPatch(typeof(GamePlayer), "set_Color")]
    [PatchFeature(
        section: "NewBlackNotice",
        description: "有人成为黑方时，把原版那条「有人变成了黑」通告也发给白方（原版只发黑幕/死者/新黑方本人）。",
        defaultEnabled: true,
        side: FeatureSide.Host)]
    internal static class NewBlackNoticeFeature
    {
        [ConfigField(true,
            "开：有人成为黑方时，白方也收到原版「刚才有人成为了黑。」条幅。" +
            "关：完全保持原版（只有黑幕收到带名字那条、新黑方与死者各收各的）。")]
        public static ConfigEntry<bool> NotifyWhite;

        /// <summary>
        /// 门闩（本模块的既定写法，见 `Core/Patching/PatchLoader.cs:69-77`）：
        ///   · `ModeRuntime.Bypass` ＝ `![HS_Mode].Enabled` —— 模式总开关，改 .cfg 即时生效；
        ///   · `Diagnostics.IsLoaded("NewBlackNotice")` ＝ 本段是否已被 `PatchLoader` 挂上
        ///     （`[NewBlackNotice].Enabled = false` ⇒ **整个类**跳过 PatchAll，补丁根本不挂）；
        ///   · 再加本功能自己的子开关。
        /// ⚠ 这里**不用** `FeatureGate.Enabled(typeof(...))` —— 那是**上游 DT_Tools** 的 API，
        ///   本模块全仓没有这个符号；其余功能一律就是上面三条的组合
        ///   （比照 `BlackWinFeature` / `PowerSelfTestFeature` 的 `Armed`）。
        /// </summary>
        private static bool Armed
            => !ModeRuntime.Bypass
            && Diagnostics.IsLoaded("NewBlackNotice")
            && (NotifyWhite == null || NotifyWhite.Value);

        /// <summary>
        /// 挂 Postfix 而非 Prefix：我们只"再发一条给白方"，一个字节都不改原方法
        /// （原方法里那三条 `S_NOTIFY_BLACK` 照旧发）。
        /// </summary>
        [HarmonyPostfix]
        private static void Postfix(GamePlayer __instance, EPlayerColor value)
        {
            // 入口第一句查门闩：关掉模式/关掉本段/关掉子开关时，本方法什么都不做。
            if (!Armed)
                return;

            Diagnostics.Hit("NewBlackNotice");

            if (value != EPlayerColor.Black)
                return;                  // 变白、变黑幕（NoMasterMind 会把人从 Dark 改回 White）都不算

            var room = GameRoom.Instance;
            if (room == null || room.State != EGameState.Survive)
                return;                  // 大厅/选人/审判/总结算阶段：不是"本局有人成为黑方"

            if (EndingRuleFeature.Decided)
                return;                  // 结局已裁定 ⇒ 不是适宜时段（收尾期还停在 Survive）

            if (ReplayDirector.Current != ReplayDirector.Phase.Idle)
                return;                  // 回放序列进行中/收尾中 ⇒ 不是适宜时段

            int id = __instance?.PublicInfo?.PlayerId ?? 0;
            int sent = SendToWhitePlayers(room, __instance, id);

            Plugin.Log.LogInfo($"[HS] NewBlackNotice：#{id} 成为黑方 ⇒ 已把「刚才有人成为了黑。」条幅单发给 {sent} 名白方。");
        }

        /// <summary>
        /// 逐个单发给**白方**。
        ///
        /// 不用 `AlertMessageAllPlayers`：那会把黑幕与新黑方本人一起发一遍 ——
        /// 黑幕原版已经收到**带名字**的那条（「XXX 变成了黑。」），再补一条无名条幅只是噪声；
        /// 而需求也只要求"也发给白方"。
        ///
        /// 死者/旁观不排除：他们的 `Color` 仍是 White，原版也照样把 `S_NOTIFY_BLACK` 发给死者
        /// ⇒ 白方阵营（含已阵亡者）口径与 `Broadcast` 段一致。
        /// </summary>
        private static int SendToWhitePlayers(GameRoom room, GamePlayer newBlack, int newBlackId)
        {
            int sent = 0;

            foreach (var player in room.Players)
            {
                if (player?.Session == null || player.PublicInfo == null)
                    continue;
                if (player == newBlack)
                    continue;                       // 本人：原版已单独弹「你已成为黑。」
                if (player.Color != EPlayerColor.White)
                    continue;                       // 只发白方（黑幕原版已收到带名字那条）

                // value 传新黑方 id：原版 NewBlack 兜底分支不读它（`NewBlackAlert` 文案里没有占位符），
                // 但语义与 `DiscoverCorpse` / `ReportCorpse` 一致 —— 将来客户端给这句加名字时即插即用。
                // ⚠ 只发系统消息，**绝不**顺手补 `S_NOTIFY_BLACK`（那会让白方记住黑方 id ⇒ 红名 + 红点）。
                room.AlertMessage(player, ESystemMessageType.NewBlack, newBlackId);
                sent++;
            }

            return sent;
        }
    }
}
