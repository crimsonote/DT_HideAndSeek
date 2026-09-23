using BepInEx.Configuration;
using HarmonyLib;
using Protocol;
using Server.Game;
using HideAndSeek.Core;
using GameRoom = Server.Game.GameRoom;

namespace HideAndSeek.Features.Rule
{
    /// <summary>
    /// 让黑方也能看到「哪些电箱可以拆」。
    ///
    /// 原版把 S_SABOTAGE_MISSION 只发给 MasterMind，而 MasterMind 就是那个 Dark（黑幕）：
    ///   GameRoom.SendSabotageMission :171338 → masterMind.Session.Send(...)
    /// 黑方（Black）是另一条路（ItemManager.InsertWeapon :172692 染黑），两者是独立身份。
    /// 再加上客户端 Fusebox.SetMissionInfo :6481 只在 `Color == Dark` 时画标记，
    /// 于是黑方在地图与平板上都看不到可拆目标。
    ///
    /// 而客户端 Handle_S_SABOTAGE_MISSION（:42550 → MapManager.AddSabotageMission :29950）
    /// **完全没有颜色校验** —— 所以房主端只要把这个包也发给黑方的 Session，
    /// 黑方就会拿到小地图图钉与平板任务条目。
    ///
    /// 注意：这只能让黑方「看得到」。真正「拆得动」的入口
    /// （客户端 Fusebox.UseSabotage :6457）只由 Dark 的输入分支到达，
    /// 黑方按 Q 会被 InputInteract :14601 导流成挥刀 —— Host 侧无法替代，
    /// 本模块也不需要黑方亲手拆（可用 hs 命令或让黑幕保留来达成）。
    /// </summary>
    [PatchFeature(
        section: "FuseboxReveal",
        description: "把「可拆电箱」的标记包也发给黑方（原版只发黑幕），让黑方在地图与平板上看得到目标。",
        defaultEnabled: true,
        side: FeatureSide.Host)]
    internal static class FuseboxRevealFeature
    {
        [ConfigField(true, "开：可拆电箱标记同时发给黑方。关：保持原版（仅黑幕可见）。")]
        public static ConfigEntry<bool> RevealToBlack;

        /// <summary>
        /// 补发给黑方。挂 Postfix 而非改原方法，是因为这一处同时覆盖
        /// 加标记（Fusebox.StartMission :162798）与摘标记（DisconnetCable :162782 /
        /// ClearSabotage :162801），不会留下点不掉的死图钉。
        /// </summary>
        [HarmonyPatch(typeof(GameRoom), nameof(GameRoom.SendSabotageMission))]
        internal static class SendSabotageMissionHook
        {
            [HarmonyPostfix]
            private static void Postfix(ESchoolMission type, int deviceId, PosInfo pos, bool isAdd)
            {
                if (ModeRuntime.Bypass)
                    return;
                if (RevealToBlack == null || !RevealToBlack.Value)
                    return;

                var room = GameRoom.Instance;
                if (room?.Players == null || pos == null)
                    return;

                var masterMind = room.MasterMind;

                foreach (var player in room.Players)
                {
                    if (player?.Session == null)
                        continue;
                    if (player.Color != EPlayerColor.Black)
                        continue;
                    if (player == masterMind)
                        continue;                 // 原版已发给他；平板列表不去重，避免重复条目

                    // 每人一份：Pos 是 protobuf 消息，避免多收件人共享同一引用
                    player.Session.Send(new S_SABOTAGE_MISSION
                    {
                        MissionType = type,
                        DeviceId = deviceId,
                        Pos = pos.Clone(),
                        IsAdd = isAdd
                    });
                }
            }
        }
    }
}
