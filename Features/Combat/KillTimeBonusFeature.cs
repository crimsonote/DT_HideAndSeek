using BepInEx.Configuration;
using HarmonyLib;
using Protocol;
using Server.Game;
using HideAndSeek.Core;
using GamePlayer = Server.Game.Player;

namespace HideAndSeek.Features.Combat
{
    /// <summary>
    /// 黑方每杀死一人，为倒计时**增加**若干秒。
    ///
    /// 挂在 Player.OnDeadMurder（:176033，private）—— 这是"被黑方刀杀"的专属路径，
    /// 因此不会把项圈自爆、自杀、其他死因算进来。
    ///
    /// 与 MissionTimePenaltyFeature 的方向相反（那个是完成任务扣时），两者互不干扰：
    /// 这里加时、那里减时，最终以 UpdateRemainTime 的净变化为准。
    ///
    /// 设为 0 即关闭本效果（不改动时间）。
    /// </summary>
    [PatchFeature(
        section: "KillTimeBonus",
        description: "黑方每杀死一人给倒计时增加若干秒（设为 0 关闭）。",
        defaultEnabled: true,
        side: FeatureSide.Host)]
    internal static class KillTimeBonusFeature
    {
        [ConfigField(30, "每次击杀给倒计时增加的秒数。0 = 关闭本效果。", Min = 0f, Max = 600f)]
        public static ConfigEntry<int> BonusSeconds;

        [ConfigField(true, "击杀加时时向全场播报一行提示。")]
        public static ConfigEntry<bool> Announce;

        [ConfigField("击杀成功，倒计时增加 {sec} 秒",
            "击杀加时的播报文本。占位符：{sec} 增加秒数。")]
        public static ConfigEntry<string> AnnounceText;

        [HarmonyPatch(typeof(GamePlayer), "OnDeadMurder")]
        [HarmonyPostfix]
        private static void Postfix()
        {
            Diagnostics.Hit("KillTimeBonus");
            if (ModeRuntime.Bypass)
                return;

            int bonus = BonusSeconds?.Value ?? 30;
            if (bonus == 0)
                return;                                  // 配置为 0 = 关闭

            var room = GameRoom.Instance;
            if (room == null || room.State != EGameState.Survive)
                return;

            TimeManager.Instance.UpdateRemainTime(bonus);

            // 击杀加时不再公开播报：按需求，除 /tp 之外的功能都不对外播报
            // （击杀本身已有死亡通告，加时不必再单独通知全场）。

            Plugin.Log.LogInfo($"[HS] KillTimeBonus：击杀成功，倒计时 +{bonus} 秒。");
        }
    }
}
