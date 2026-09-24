using BepInEx.Configuration;
using HarmonyLib;
using Protocol;
using Server.Game;
using HideAndSeek.Core;
using GameRoom = Server.Game.GameRoom;
using GamePlayer = Server.Game.Player;
using GameDeviceManager = Server.Game.DeviceManager;

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
    ///
    /// ⚠ 层次约束：PatchLoader 只扫**一层**嵌套类型（Core/Patching/PatchLoader.cs），
    /// 所以本类的所有补丁类必须**直接**嵌在下面，**不许再套一层** —— 套了就永远不会挂载，
    /// 而且日志里的"失败 0"也不会反映出来（FailedCount 只统计已扫到的那一层）。
    /// verify.ps1 有一条检查专门盯这个。
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

        private static bool Enabled => RevealToBlack == null || RevealToBlack.Value;

        /// <summary>
        /// 转交给所有黑方（原版只发 MasterMind）。
        ///
        /// 放在最外层是**必需**的：它同时被下面两个平级的补丁类调用，
        /// 而 C# 里嵌套类能访问外层类的 private，**兄弟嵌套类之间不能**。
        /// </summary>
        private static void BroadcastToBlack(ESchoolMission type, int deviceId, PosInfo pos, bool isAdd)
        {
            var room = GameRoom.Instance;
            if (room?.Players == null || pos == null)
                return;

            var masterMind = room.MasterMind;
            int sent = 0;

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
                sent++;
            }

            if (sent == 0 && isAdd)
                Plugin.Log.LogInfo($"[HS] FuseboxReveal：{type} #{deviceId} 派发时场上无黑方，已跳过（黑方诞生后会补发）。");
        }

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
                Diagnostics.Hit("FuseboxReveal");
                if (ModeRuntime.Bypass)
                    return;
                if (!Enabled)
                    return;

                BroadcastToBlack(type, deviceId, pos, isAdd);
            }
        }

        /// <summary>
        /// 黑方刚诞生时补发当前所有可拆目标。
        ///
        /// 必需：电箱任务只在 GameRoom.GameStart（回合第一帧）派发一次，
        /// 而那时全员仍是 White（StartPick 设定，且 NoMasterMind 拒绝了唯一会变 Dark 的人），
        /// 所以 BroadcastToBlack 的 foreach 全员跳过、一个包都发不出去。
        /// 黑方要到玩家去武器架拿刀（ItemManager.InsertWeapon 染黑）时才存在，
        /// 因此必须在"变黑瞬间"补一次，否则他整局看不到任何可拆目标。
        ///
        /// ⚠ 这个类曾经嵌在 SendSabotageMissionHook **里面**（二级嵌套），
        /// 而 PatchLoader 只扫一层 → 它从来没被挂载过，于是"黑方看不到电箱"一直存在，
        /// 日志里连下面那行 LogInfo 都没有。别再套进去。
        /// </summary>
        [HarmonyPatch(typeof(GamePlayer), "set_Color")]
        internal static class BecomeBlackHook
        {
            [HarmonyPostfix]
            private static void Postfix(GamePlayer __instance, EPlayerColor value)
            {
                if (value != EPlayerColor.Black)
                    return;
                if (ModeRuntime.Bypass)
                    return;
                if (!Enabled)
                    return;

                var dm = GameDeviceManager.Instance;
                if (dm == null)
                    return;

                // 只有 MissionType == -1 才是"待拆"（DisconnetCable 会置 0）
                if (dm.Fuseboxes != null)
                {
                    foreach (var fb in dm.Fuseboxes)
                    {
                        if (fb?.DeviceInfo == null)
                            continue;
                        if (fb.DeviceInfo.MissionType != -1)
                            continue;
                        BroadcastToBlack(ESchoolMission.ScFusebox, fb.ID, fb.DeviceInfo.Pos, true);
                    }
                }

                var armory = dm.CurrentArmory;
                if (armory?.DeviceInfo != null && armory.DeviceInfo.MissionType == -1)
                    BroadcastToBlack(ESchoolMission.ScWeapon, armory.ID, armory.DeviceInfo.Pos, true);

                Plugin.Log.LogInfo($"[HS] FuseboxReveal：黑方 #{__instance.PublicInfo?.PlayerId} 诞生，已补发可拆目标。");
            }
        }
    }
}
