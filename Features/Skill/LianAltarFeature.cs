using System.Collections.Generic;
using BepInEx.Configuration;
using HarmonyLib;
using Protocol;
using Server.Game;
using HideAndSeek.Core;
using HideAndSeek.Features.Vision;
using GamePlayer = Server.Game.Player;
using GameDeviceManager = Server.Game.DeviceManager;    // AGENTS 坑 #1：同名类，服务端那个才有 Instance

namespace HideAndSeek.Features.Skill
{
    /// <summary>
    /// 莲 · DT 点（藏尸点）相关能力 —— **本次只实现第 ① 项**。
    ///
    /// ① 站在 DT 点范围内超过 <see cref="ArmSeconds"/> 秒时，若此时真的停电（或黑方处于
    ///    捉迷藏的黑灯视野下），就**只给莲本人**下发一份"这里没断电"的假光照，
    ///    让他的客户端放行断电时被禁用的交互 —— 具体说就是**能点蜡烛**。
    ///    离开 DT 点超过 <see cref="ReleaseSeconds"/> 秒后发回真实光照。
    ///
    /// 为什么"假光照"能解禁：客户端所有设备走同一条判据
    /// （`Assembly-CSharp:8098`）：
    ///     `if (State == Survive && !CanUseDarkness && Managers.Game.Darkness) → "DarknessError"`
    /// 而 `Darkness` 只由 `S_AREA_PUBLIC.IsLight` 驱动（客户端 :29592 无条件 `Darkness = !IsLight`）。
    /// ⇒ 单独给他一份 `IsLight = true`，他的 `Darkness` 就变 false，蜡烛类设备放行；
    ///   服务端 `Area.IsLight` 与其它玩家的视野**完全不受影响**。
    ///
    /// "DT 点"在代码里的真身：`Server.Game.DeadlyTrickStage`（`CanHideCorpse => true`，:162413）
    /// 与 `Cabinet`（:162011）—— 即魔法阵/柜子这类能藏尸的设备。坐标取 `DeviceInfo.Pos`。
    ///
    /// ⚠ 本类**不做**"解除"以外的收尾：一旦不满足条件（离开超时 / 不再停电 / 离开生存阶段），
    ///   必须把真实光照发回去，否则莲会永久看得见（假状态残留）。
    /// </summary>
    [PatchFeature("LianAltar",
        "莲·DT 点：站在藏尸点范围内满足条件时，只给他本人下发「这里没断电」的假光照（用于点蜡烛）；离开后自动恢复真实光照。",
        defaultEnabled: false, side: FeatureSide.Host)]
    internal static class LianAltarFeature
    {
        [ConfigField(3f, "站进 DT 点多少秒后开始生效。", Min = 0f, Max = 60f)]
        public static ConfigEntry<float> ArmSeconds;

        [ConfigField(1f, "离开 DT 点多少秒后失效并恢复真实光照。", Min = 0f, Max = 60f)]
        public static ConfigEntry<float> ReleaseSeconds;

        [ConfigField(350f, "DT 点的判定半径（游戏单位）。", Min = 50f, Max = 2000f)]
        public static ConfigEntry<float> DtRadius;

        /// <summary>
        /// 尸体追踪用掉一次之后，莲的技能槽上**长期挂着**的冷却值。
        ///
        /// 需求原话是"长期显示为 -1（如果可行），或 13"。**-1 不可行** ——
        /// 客户端 `SkillCooltime` 的 setter 是 `_skillCooltime = Math.Max(0, value)`
        /// （`Assembly-CSharp:28836`），负数会被夹成 0，看不到 -1。
        /// 所以用备选的 13；并且每秒补发一次，否则它会按客户端本地计时一路减到 0。
        /// </summary>
        [ConfigField(13f, "尸体追踪用过一次后，莲技能槽长期显示的冷却值（0 = 不显示）。", Min = 0f, Max = 999f)]
        public static ConfigEntry<float> UsedCooldown;

        /// <summary>莲 PlayerId → 进入 DT 点范围的时刻。用于判"站够 ArmSeconds"。</summary>
        private static readonly Dictionary<int, float> EnteredAt = new Dictionary<int, float>();

        /// <summary>莲 PlayerId → 离开 DT 点范围的时刻。用于判"离开够 ReleaseSeconds"。</summary>
        private static readonly Dictionary<int, float> LeftAt = new Dictionary<int, float>();

        /// <summary>当前正被施加"假光照"的莲 PlayerId 集合。</summary>
        private static readonly HashSet<int> Faked = new HashSet<int>();

        /// <summary>已经用掉一次尸体追踪的莲 PlayerId —— 他们的技能槽要长期挂冷却值。</summary>
        private static readonly HashSet<int> TraceUsed = new HashSet<int>();

        private static float Now => TimeManager.Instance?.SurviveTime ?? 0f;

        /// <summary>莲：本人是莲，或技能被换成了灵魂感知（Soi 的偷取）。与原版 :176085 同判据。</summary>
        private static bool IsLian(GamePlayer p)
            => p?.SkillComponent?.Data != null && p.SkillComponent.Data.Type == ESkillType.SoulSense;

        /// <summary>"DT 点"= 能藏尸的设备：DeadlyTrickStage（魔法阵那类）与 Cabinet（柜子）。</summary>
        private static bool IsDtDevice(Device d)
            => d is DeadlyTrickStage || d is Cabinet;

        /// <summary>该玩家是否站在某个 DT 点的半径内。</summary>
        private static bool InDtRange(GamePlayer player)
        {
            var pos = player?.PublicInfo?.Pos;
            var devices = GameDeviceManager.Instance?.Objects;
            if (pos == null || devices == null)
                return false;

            float r = DtRadius?.Value ?? 350f;
            float r2 = r * r;

            for (int i = 0; i < devices.Count; i++)
            {
                var d = devices[i];
                if (d == null || !IsDtDevice(d))
                    continue;

                var dp = d.DeviceInfo?.Pos;
                if (dp == null)
                    continue;

                float dx = dp.X - pos.X, dy = dp.Y - pos.Y;
                if (dx * dx + dy * dy <= r2)
                    return true;
            }
            return false;
        }

        /// <summary>撤掉假光照，恢复真实值（无论之前是否施加过，都可安全调用）。</summary>
        private static void Release(GamePlayer player)
        {
            BlackVisionFeature.SendAreaLight(player, player?.CurrentArea?.IsLight ?? true);
        }

        // ── 有人死亡 ⇒ 与**原版路易斯**同款的"尸体追踪警告" ──
        //
        // 原版（:176033 OnDeadMurder）对被路易斯标记过的死者做两件事：
        //     BroadcastSystemSFX(WarningSfx)                          ← 全房警告音
        //     BroadcastAlivePlayers(S_NOTIFY_ARROW{CorpseArrow, Pos})  ← 指向**尸体**的箭头，给所有活人
        // 需求要莲的死亡提示与它一致（"因为莲追踪的是尸体，而不是人"），
        // 所以这里对**任何**死亡都做同样两件事，条件是"场上有一位莲"。
        //
        // 挂 OnDeadMurder 而不是 OnDead：前者是"尸体已经建立"的时刻，
        // 且它的 PublicInfo.Pos 就是尸体位置。
        [HarmonyPatch(typeof(GamePlayer), "OnDeadMurder")]
        internal static class CorpseArrowHook
        {
            [HarmonyPostfix]
            private static void Postfix(GamePlayer __instance)
            {
                if (ModeRuntime.Bypass || __instance?.PublicInfo == null)
                    return;

                var room = GameRoom.Instance;
                if (room?.Players == null || room.State != EGameState.Survive)
                    return;

                // 场上得有莲（技能持有者，含被偷取的）才触发
                bool hasLian = false;
                for (int i = 0; i < room.Players.Count; i++)
                {
                    if (IsLian(room.Players[i])) { hasLian = true; break; }
                }
                if (!hasLian)
                    return;

                try
                {
                    room.BroadcastSystemSFX(ESoundType.WarningSfx);
                    room.BroadcastAlivePlayers(new S_NOTIFY_ARROW
                    {
                        Type = EArrowType.CorpseArrow,
                        Pos = __instance.PublicInfo.Pos
                    });
                    // 这次追踪"用掉"了：所有莲进入"技能槽长期显示冷却值"的状态
                    for (int i = 0; i < room.Players.Count; i++)
                    {
                        var lian = room.Players[i];
                        if (IsLian(lian) && lian.PublicInfo != null)
                            TraceUsed.Add(lian.PublicInfo.PlayerId);
                    }

                    Plugin.Log.LogInfo(
                        $"[HS] LianAltar：死者 #{__instance.PublicInfo.PlayerId} 的尸体追踪警告已广播（莲在场上）。");
                }
                catch (global::System.Exception ex)
                {
                    Plugin.Log.LogWarning($"[HS] LianAltar：尸体追踪警告失败 — {ex.Message}");
                }
            }
        }

        // ── 每秒检查一次：进入/满足条件/离开三段 ──
        [HarmonyPatch(typeof(GameRoom), "SurvivalTick")]
        internal static class TickHook
        {
            [HarmonyPostfix]
            private static void Postfix(GameRoom __instance)
            {
                if (ModeRuntime.Bypass || __instance?.Players == null)
                    return;
                if (__instance.State != EGameState.Survive)
                    return;                          // 只在生存阶段干预；其它阶段交还原版

                // 尸体追踪用过一次 ⇒ 技能槽长期挂着冷却值。
                // 必须**每秒补发**：客户端收到后会按本地计时一路递减（:42319-42321），
                // 只发一次的话几秒后就归零了，"长期显示"就没了。
                if (TraceUsed.Count > 0)
                {
                    float cd = UsedCooldown?.Value ?? 13f;
                    if (cd > 0f)
                    {
                        for (int i = 0; i < __instance.Players.Count; i++)
                        {
                            var lp = __instance.Players[i];
                            if (lp?.PublicInfo == null || lp.Session == null)
                                continue;
                            if (!TraceUsed.Contains(lp.PublicInfo.PlayerId) || !IsLian(lp))
                                continue;
                            try
                            {
                                lp.CanUseSkill = false;
                                lp.Session.Send(new S_COOLTIME_SKILL { Cooltime = (int)cd });
                            }
                            catch { /* 单个失败不影响其它人 */ }
                        }
                    }
                }

                float now = Now;
                float arm = ArmSeconds?.Value ?? 3f;
                float release = ReleaseSeconds?.Value ?? 1f;

                for (int i = 0; i < __instance.Players.Count; i++)
                {
                    var p = __instance.Players[i];
                    if (p?.PublicInfo == null || p.Session == null || !IsLian(p))
                        continue;

                    int pid = p.PublicInfo.PlayerId;
                    bool inside = InDtRange(p);

                    if (inside)
                    {
                        LeftAt.Remove(pid);
                        if (!EnteredAt.ContainsKey(pid))
                            EnteredAt[pid] = now;

                        // 站够了 + 确实处在"看不见"的状态（真停电，或被黑灯压着）才需要解禁
                        if (now - EnteredAt[pid] < arm)
                            continue;
                        if (!GameRefs.IsRealBlackout(p) && !Faked.Contains(pid))
                            continue;                // 本来就亮着：什么都不用做

                        if (Faked.Add(pid))
                        {
                            BlackVisionFeature.SendAreaLight(p, true);
                            Plugin.Log.LogInfo($"[HS] LianAltar：莲 #{pid} 在 DT 点内，已下发假光照（可点蜡烛）。");
                        }
                    }
                    else
                    {
                        EnteredAt.Remove(pid);
                        if (!Faked.Contains(pid))
                            continue;                // 没被施加过，不需要收尾

                        if (!LeftAt.ContainsKey(pid))
                            LeftAt[pid] = now;
                        if (now - LeftAt[pid] < release)
                            continue;

                        LeftAt.Remove(pid);
                        Faked.Remove(pid);
                        Release(p);
                        Plugin.Log.LogInfo($"[HS] LianAltar：莲 #{pid} 已离开 DT 点，恢复真实光照。");
                    }
                }
            }
        }

        // ── 跨局清理，并给仍在"假光照"里的人收尾 ──
        [HarmonyPatch(typeof(GameRoom), "StartSurvive")]
        internal static class StartHook
        {
            [HarmonyPostfix]
            private static void Postfix() => Cleanup(restore: false);
        }

        [HarmonyPatch(typeof(GameRoom), "StartLobby")]
        internal static class LobbyHook
        {
            [HarmonyPostfix]
            private static void Postfix() => Cleanup(restore: true);
        }

        private static void Cleanup(bool restore)
        {
            if (restore)
            {
                var room = GameRoom.Instance;
                if (room?.Players != null)
                {
                    for (int i = 0; i < room.Players.Count; i++)
                    {
                        var p = room.Players[i];
                        if (p?.PublicInfo != null && Faked.Contains(p.PublicInfo.PlayerId))
                            Release(p);
                    }
                }
            }

            // 三个表记的都是 PlayerId 与 SurviveTime；SurviveTime 每局被 ResetSurvival() 设回 420，
            // 不清会算出负数 ⇒ 判定永久成立（这一类坑今天已踩过多次）。
            EnteredAt.Clear();
            LeftAt.Clear();
            Faked.Clear();
            TraceUsed.Clear();
        }
    }
}
