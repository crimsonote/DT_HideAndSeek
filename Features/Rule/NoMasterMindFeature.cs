using System.Reflection;
using HarmonyLib;
using Protocol;
using HideAndSeek.Core;
using GamePlayer = Server.Game.Player;

namespace HideAndSeek.Features.Rule
{
    /// <summary>
    /// 开局不分配黑幕（Dark）。
    ///
    /// 原版 GameRoom.StartPick（:171011）在全员变白后随机挑一人：
    ///     Players[random].Color = EPlayerColor.Dark;
    ///     SetMasterMind(Players[random]);
    /// 本功能拦截 SetMasterMind —— MasterMind 的唯一赋值入口（主机迁移恢复 :173720 亦经此处），
    /// 把刚涂成 Dark 的玩家改回 White，并拒绝记录 MasterMind。
    /// 改 Color 会走 set_Color，正常下发 S_MODIFY_PLAYER，客户端表现与"从未变黑"一致。
    ///
    /// ★★ **还要把 MasterMind 字段清空**（`SetMasterMind` 的拒绝只挡"新赋值"，挡不住**旧值残留**）：
    ///   原版 `MasterMind` 是 `GameRoom` 上的字段、**跨局不自动清**；只要它非 null，
    ///   一旦有人变成 `Black`（`Server.Game.Player.set_Color` :175364），原版就会走
    ///   `:175367-175379`：
    ///       masterMind.Session.Send(S_NOTIFY_BLACK{ PlayerId = 黑方 });      // 告诉黑幕"黑方是谁"
    ///       Session.Send(S_NOTIFY_BLACK{ PlayerId = masterMind.PlayerId });  // ★ 告诉黑方"你的黑幕是谁"
    ///   而客户端 `Handle_S_NOTIFY_BLACK`（:42511-42519）里 `if (myPlayer.Color == Dark)` 就会弹
    ///   「你的合作黑幕是 X」—— 实测出现过「协助你的黑幕是**自己**」：那一刻 MasterMind 恰好指向他本人。
    ///   ⇒ 拒绝分配的同时把字段置 null，才能让"没有黑幕"这个前提在**任何路径**下都成立。
    /// </summary>
    [HarmonyPatch(typeof(Server.Game.GameRoom), nameof(Server.Game.GameRoom.SetMasterMind))]
    [PatchFeature(
        section: "NoMasterMind",
        description: "开局不分配黑幕（Dark）：跳过原版随机指定，改由玩家自行跑刀转黑方。",
        defaultEnabled: true,
        side: FeatureSide.Host)]
    internal static class NoMasterMindFeature
    {
        /// <summary>`MasterMind` 的自动属性 backing field —— 用字段而不是 setter：
        /// 走 setter 会再次进入本 Prefix（`player == null` 也照样拦），形成无限递归。</summary>
        private static readonly FieldInfo MasterMindField =
            AccessTools.Field(typeof(Server.Game.GameRoom), "<MasterMind>k__BackingField");

        // DT_Tools 的 MastermindWeight 也挂同一个 SetMasterMind：显式先跑，
        // 最终结果由 Priority.Last 的 Postfix 归一，不依赖挂载顺序。
        [HarmonyPriority(Priority.First)]
        [HarmonyPrefix]
        private static bool Prefix(GamePlayer player)
        {
            Diagnostics.Hit("NoMasterMind");
            if (ModeRuntime.Bypass)
                return true;

            if (player != null && player.Color == EPlayerColor.Dark)
                player.Color = EPlayerColor.White;

            ClearMasterMind(Server.Game.GameRoom.Instance);
            Plugin.Log.LogInfo("[HS] NoMasterMind：已跳过黑幕分配，该玩家保持白方（并清空 MasterMind）。");
            return false;
        }

        /// <summary>
        /// 在所有 Prefix 之后做最终归一。
        /// </summary>
        /// <remarks>
        /// Harmony 2.2 起，Prefix 返回 false 只跳过原方法体，<b>不会再阻止其它 Prefix</b>。
        /// DT_Tools 的 MastermindWeight 同样挂 <c>SetMasterMind</c>，且会把全员重设成 White、
        /// 再把选中者设成 Dark；若它排在本类之后执行，就会出现「有人是 Dark，但 MasterMind
        /// 已被清空」的半分配状态。Prefix 里改一次只能处理它之前的补丁，无法覆盖这种顺序，
        /// 所以必须用 <c>Priority.Last</c> 的 Postfix 在所有 Prefix 结束后再收口一次。
        /// </remarks>
        [HarmonyPostfix]
        [HarmonyPriority(Priority.Last)]
        private static void Postfix(Server.Game.GameRoom __instance)
        {
            // Harmony 的实例注入必须叫 __instance；写成 room 会被当成原方法参数，
            // SetMasterMind 里找不到同名参数 ⇒ 整个功能补丁 IL 编译失败、静默跳过。
            var room = __instance;
            if (ModeRuntime.Bypass || room == null)
                return;

            bool corrected = false;
            var players = room.Players;
            if (players != null)
            {
                for (int i = 0; i < players.Count; i++)
                {
                    var p = players[i];
                    if (p != null && p.Color == EPlayerColor.Dark)
                    {
                        p.Color = EPlayerColor.White;
                        corrected = true;
                    }
                }
            }

            ClearMasterMind(room);

            if (corrected)
            {
                Diagnostics.Hit("NoMasterMind");
                Plugin.Log.LogInfo(
                    "[HS] NoMasterMind：检测到之后执行的补丁重新分配了黑幕，已在最终归一阶段撤销。");
            }
        }

        /// <summary>把 `GameRoom.MasterMind` 置 null（绕开 setter，避免递归进本 Prefix）。</summary>
        private static void ClearMasterMind(Server.Game.GameRoom room)
        {
            try
            {
                if (room != null && MasterMindField != null && MasterMindField.GetValue(room) != null)
                    MasterMindField.SetValue(room, null);
            }
            catch (global::System.Exception ex)
            {
                Plugin.Log.LogWarning($"[HS] NoMasterMind：清空 MasterMind 失败 — {ex.Message}");
            }
        }
    }
}
