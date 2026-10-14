using BepInEx.Configuration;
using HarmonyLib;
using Protocol;
using Server.Game;
using HideAndSeek.Core;
using GamePlayer = Server.Game.Player;

namespace HideAndSeek.Features.Combat
{
    /// <summary>
    /// 柜子自爆：当且仅当「死者是 Jeremy」且「处决建筑是柜子」时，在死亡点炸一次，晕眩附近所有人。
    ///
    /// 两个判据都取**原版自己写下的标记**，不自己记状态：
    ///   · 处决建筑 —— <see cref="GamePlayer.DeathCabinet"/>。
    ///     它由 DT 柜子处决 <c>ExecuteCabinetTrick</c>（ACS-0.1.17a:178906
    ///     <c>target.DeathCabinet = cabinet</c>）写入；池塘 / 神秘处决写的是另外两组字段
    ///     （<c>DeathPondStageId</c> :178962 / <c>DeathOccultStageId</c>），
    ///     所以「DeathCabinet 非空」恰好等价于「这次 DT 走的是柜子」。
    ///   · 角色 —— <c>PublicInfo.CharacterId == 104</c>。
    ///     104 = Jeremy（柜子，房主确认口径；Noel 是黑洞传送 <c>UseTeleport</c>，别混）。
    ///
    /// 为什么挂 <c>OnDeadMurder</c> 的 **Prefix**：原版在这个方法里会读 <c>DeathCabinet</c>
    /// 并**当场清空**（:178375-178378），Postfix 里已经读不到。
    /// 本钩子只读标记 + 发广播包，返回 void 不改原版行为。
    ///
    /// 会不会打断 DT 动作 / 刀人动作：不会。DT 柜子处决的链路是
    ///     ExecuteCabinetTrick(:178861) → StartDeadlyTrickCommon
    ///     → PushAfter(1767ms) → DeathCabinet 赋值 → OnDead → OnDeadMurder
    /// 到达本钩子时演出已放完、尸体已建立；凶手的挥刀早在 <c>OnDamaged</c>（:178271 的
    /// PushAfter(400)）里一次性结算完，原版在那个回调里只给凶手发包与 Footprint debuff，
    /// 没有任何状态锁。本钩子发的是两个广播包（音效 + Buff），不改任何玩家状态。
    /// <c>EBuffType.Stun</c> 只是往 BuffList 加一项并让客户端播眩晕特效，不重置状态机 ——
    /// 原版矿山自爆（Mineral.HandleEvent 失败分支 :168094）就是这么用的，黑方站旁边也照常挥刀。
    ///
    /// 参数对标原版矿山自爆：
    ///     BroadcastWorldSFX(ESoundType.ExplosionSfx, pos, 672f)
    ///     + BroadcastBuff(EBuffType.Stun, 3000, pos, 224f)
    /// 本功能默认 5000ms / 半径 256 是设计加码（房主定的是「无差别 AOE 换更长控制时间」）。
    /// </summary>
    [PatchFeature("JeremyBurst",
        "柜子自爆：Jeremy 被 DT 柜子处决时引发类矿山自爆，晕眩附近所有人（不分阵营）。",
        defaultEnabled: true, side: FeatureSide.Host)]
    internal static class JeremyBurstFeature
    {
        [ConfigField(104, "触发角色 ID。104 = Jeremy（柜子）。", Min = 0f, Max = 999f)]
        public static ConfigEntry<int> CharacterId;

        [ConfigField(5000f,
            "晕眩毫秒数。原版矿山自爆为 3000；5~6 秒是「够黑方刀一个人、不够清场」的量级，10 秒起进团灭窗口。",
            Min = 0f, Max = 10000f)]
        public static ConfigEntry<float> StunMs;

        [ConfigField(256f, "爆炸半径（游戏单位）。原版为 224。", Min = 32f, Max = 1024f)]
        public static ConfigEntry<float> Radius;

        [ConfigField(true, "是否播放爆炸音效（传播半径 672，与原版一致）。")]
        public static ConfigEntry<bool> PlaySfx;

        /// <summary>
        /// 钉 <c>Priority.First</c>：上游 DT_Tools 在新版里也 hook 了 <c>OnDeadMurder</c> 的
        /// Prefix（check-upstream-overlap 实测），按 AGENTS.md 第 9 条必须显式定序。
        /// 取向往"最先结算"：本钩子是纯读标记 + 发广播包，与上游互不读写；
        /// 而清空 <c>DeathCabinet</c> 的只有**原版方法体**（不是任何 Prefix），
        /// 所以无论上游那个 Prefix 是否 return false，我们都能读到标记。
        /// 先跑的好处是：即便原版方法体被上游整替跳过，自爆也已经生效过了。
        /// </summary>
        [HarmonyPatch(typeof(GamePlayer), "OnDeadMurder")]
        [HarmonyPriority(Priority.First)]
        internal static class BurstHook
        {
            [HarmonyPrefix]
            private static void Prefix(GamePlayer __instance)
            {
                Diagnostics.Hit("JeremyBurst");
                if (ModeRuntime.Bypass)
                    return;

                if (__instance?.PublicInfo == null)
                    return;

                // 判据一：处决建筑是柜子（原版写下的标记，这一刻还未被清空）
                if (__instance.DeathCabinet == null)
                    return;

                // 判据二：死者是 Jeremy
                int want = CharacterId?.Value ?? 104;
                if (want > 0 && __instance.PublicInfo.CharacterId != want)
                    return;

                var room = GameRoom.Instance;
                if (room == null)
                    return;

                var pos = __instance.PublicInfo.Pos;
                if (pos == null)
                    return;

                float ms = StunMs?.Value ?? 5000f;
                float radius = Radius?.Value ?? 256f;

                if (PlaySfx?.Value ?? true)
                    room.BroadcastWorldSFX(ESoundType.ExplosionSfx, pos, 672f);

                // 无差别 AOE：不分阵营，落在半径内的 AlivePlayers 都吃（与矿山自爆同款语义）。
                // 柜子本人此刻 IsAlive 已是 false（OnDead 第二行设的 :175974），不会吃到自己的爆炸。
                if (ms > 0f && radius > 0f)
                    room.BroadcastBuff(EBuffType.Stun, (int)ms, pos, radius);

                Plugin.Log.LogInfo(
                    $"[HS] JeremyBurst：柜子 #{__instance.PublicInfo.PlayerId} 死于柜子处决，" +
                    $"爆炸晕眩 {ms:F0}ms / r={radius:F0}。");
            }
        }
    }
}
