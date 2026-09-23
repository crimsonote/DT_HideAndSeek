using System.Collections.Generic;
using BepInEx.Configuration;
using HarmonyLib;
using Protocol;
using Server.Game;
using HideAndSeek.Core;
using GamePlayer = Server.Game.Player;

namespace HideAndSeek.Features.Vision
{
    /// <summary>
    /// 美幸（Miyuki, CharacterId 107）的捉迷藏被动：每 15 秒扫描一次全图。
    ///
    /// 为什么黑方必须走「临时解除 AOI」而不是只发地图 pin：
    ///   黑方的小地图/平板只渲染**游戏内可见的人**（有 Player 对象的人才会有 pin 可更新）。
    ///   所以要让远处的人出现在地图上，必须先让他们在游戏内可见 ——
    ///   即在 AoiCullingFeature.PrefixAddPlayer 那个闸门上临时放行。
    ///
    /// 时间线（按需求：1 秒实时 + 约 2 秒静止 + 消失）：
    ///   t=0     触发扫描
    ///           · 记录所有存活玩家的位置快照
    ///           · 黑方：Unlocking.Add → AOI 放行；主动 AddPlayer 所有人
    ///           · 发 pin（复用 WhiteRadarFeature 的 S_PIN_MOVE 通道）
    ///   t=0~1s  每秒重发 pin → 地图上实时跟随
    ///   t=1s    撤销解锁：Unlocking.Remove → 再把超范围的人 RemovePlayer 收回
    ///           · 此后不再发 pin，地图标记停在上一次位置（静止段）
    ///   t=3s    发删除哨兵 → pin 消失
    ///   AOI 范围内的人不受影响：他们本就在视野内，pin 撤销不动他们的正常显示。
    ///
    /// 白方美幸走同一套 pin 通道，但不碰 AOI（白方本来就没有 AOI 裁剪）；
    /// 用 /radar 打开的实时地图会覆盖这段自动扫描（两者共用同一批 pin id）。
    /// </summary>
    [PatchFeature(
        section: "MiyukiScan",
        description: "美幸被动：每 15 秒扫描全图。黑方临时解除 AOI，地图标记 1 秒实时 + 2 秒静止后消失。",
        defaultEnabled: true,
        side: FeatureSide.Host)]
    internal static class MiyukiScanFeature
    {
        [ConfigField(15, "扫描间隔（秒）。", Min = 5f, Max = 300f)]
        public static ConfigEntry<int> ScanIntervalSeconds;

        [ConfigField(1, "实时跟随时长（秒）：标记跟随真实位置；同时也是 AOI 解封时长。", Min = 0f, Max = 10f)]
        public static ConfigEntry<int> UnlockSeconds;


        [ConfigField(3, "地图标记总持续（秒）。前 UnlockSeconds 秒实时跟随，其余时间静止在原地。", Min = 1f, Max = 30f)]
        public static ConfigEntry<int> MarkerSeconds;

        /// <summary>
        /// 黑美幸 pin 的"范围内"判据：距离 ≤ 本值的人不发 pin（原版地图已经会显示他们，
        /// 再发会多叠一个白色方块）。0 = 关闭该过滤，所有人都发。
        /// 默认 900 对应 AoiCulling.ExitRange —— 想更严格可改为 700（EnterRange）。
        /// </summary>
        [ConfigField(900f, "黑美幸只标记该距离之外的人（0 = 全部标记）。",
            Min = 0f, Max = 3000f)]
        public static ConfigEntry<float> BlackPinRange;

        /// <summary>实时跟随时长（秒）。</summary>
        private static float LiveSecs() => UnlockSeconds?.Value ?? 1;

        /// <summary>
        /// 标记总时长（秒）。静止时长 = 本值 − 实时跟随时长（UnlockSeconds），无需单独配置。
        /// 想"2 秒跟随 + 1 秒静止"就设 UnlockSeconds=2、MarkerSeconds=3。
        /// </summary>
        private static float TotalSecs() => MarkerSeconds?.Value ?? 3;
        /// <summary>解析实际生效的判据距离。&lt;0 表示跟随视野配置（热更新）。</summary>
        private static float ResolveBlackPinRange()
        {
            float v = BlackPinRange?.Value ?? -1f;
            if (v < 0f)
                v = AoiCullingFeature.ExitRange?.Value ?? 900f;   // 跟随视野配置
            return v;
        }
        [ConfigField(true, "对美幸启用该被动。")]

        public static ConfigEntry<bool> EnableForMiyuki;

        /// <summary>美幸的角色 DataId（CharacterData.json / CharacterDic）。</summary>
        private const int MiyukiCharacterId = 107;

        /// <summary>正在 AOI 解封中的美幸 PlayerId —— 供 AoiCullingFeature 查询。</summary>
        private static readonly HashSet<int> Unlocking = new HashSet<int>();

        private static readonly Dictionary<int, float> NextScanAt = new Dictionary<int, float>();
        private static readonly Dictionary<int, float> UnlockUntil = new Dictionary<int, float>();
        private static readonly Dictionary<int, float> MarkerUntil = new Dictionary<int, float>();

        /// <summary>PlayerId → 实时段结束时刻（此前每秒重发 pin 以跟随真实位置）。</summary>
        private static readonly Dictionary<int, float> LiveUntil = new Dictionary<int, float>();

        /// <summary>该黑方是否正处于扫描解封期（AOI 闸门据此放行）。</summary>
        private static bool _skillResolved;
        private static ESkillType _miyukiSkill;

        /// <summary>
        /// 是否拥有美幸的扫描被动：本人是美幸，**或**技能被 RuleBreaker 换成了美幸的技能（Soi 偷取）。
        /// 与 GameRefs.IsLunaSide 同思路 —— 服务端权威判据是技能而非角色，否则偷到能力的人拿不到效果。
        /// </summary>
        internal static bool HasMiyukiAbility(GamePlayer player)
        {
            if (player?.PublicInfo == null)
                return false;

            var dic = Managers.Data?.CharacterDic;

            if (dic != null)
            {
                foreach (var kv in dic)
                {
                    var cd = kv.Value;
                    if (cd == null)
                        continue;

                    // ① 本人是美幸
                    if (cd.DataId == player.CharacterId)
                    {
                        if (cd.Type == ECharacterType.Miyuki)
                            return true;
                        break;
                    }
                }
            }

            // ② 技能被换成了美幸的技能（Soi 的 RuleBreaker 复制）
            if (!_skillResolved && dic != null)
            {
                foreach (var kv in dic)
                {
                    var cd = kv.Value;
                    if (cd != null && cd.DataId == MiyukiCharacterId)
                    {
                        _miyukiSkill = cd.Skill;
                        _skillResolved = true;
                        break;
                    }
                }
            }

            return _skillResolved && player.SkillComponent?.Data?.Type == _miyukiSkill;
        }
        internal static bool IsUnlocking(GamePlayer black)
        {
            if (black?.PublicInfo == null)
                return false;
            return Unlocking.Contains(black.PublicInfo.PlayerId);
        }

        [HarmonyPatch(typeof(GameRoom), "SurvivalTick")]
        internal static class TickHook
        {
            [HarmonyPostfix]
            private static void Postfix(GameRoom __instance)
            {
                if (ModeRuntime.Bypass || __instance == null)
                    return;
                if (__instance.State != EGameState.Survive)
                    return;
                if (EnableForMiyuki == null || !EnableForMiyuki.Value)
                    return;

                float now = TimeManager.Instance?.SurviveTime ?? 0f;

                var players = __instance.Players;
                for (int i = 0; i < players.Count; i++)
                {
                    var p = players[i];
                    if (p?.PublicInfo == null || !p.IsAlive || p.IsSpectator)
                        continue;
                    if (!HasMiyukiAbility(p))
                        continue;                        // 只处理「本人是美幸」或「技能被换成美幸」的人

                    int pid = p.PublicInfo.PlayerId;

                    // ① 解封到点 → 收回 AOI
                    if (Unlocking.Contains(pid) && UnlockUntil.TryGetValue(pid, out float uu) && now >= uu)
                    {
                        Unlocking.Remove(pid);
                        UnlockUntil.Remove(pid);
                        ReapplyCull(__instance, p);
                    }

                    // ② 标记到点 → 撤销 pin
                    if (MarkerUntil.TryGetValue(pid, out float mu) && now >= mu)
                    {
                        MarkerUntil.Remove(pid);
                        ClearPins(__instance, p);
                    }

                    // ②b 实时段：解封/标记开始后的前 UnlockSeconds 秒内每秒重发 pin，
                    //     让地图标记跟随真实位置；之后停止发包，标记就静止在原地（需求里的"停留两秒"）。
                    // 实时段：仅白方需要重发 pin（黑方靠 AOI 解封由原版刷新）。
                    // 只在 LiveUntil 之前重发 → 之后停止发包，标记静止在原地（需求里的"停留两秒"）。
                    if (MarkerUntil.ContainsKey(pid))
                    {
                        if (LiveUntil.TryGetValue(pid, out float live) && now < live)
                        {
                            // 实时段：跟随真实位置，并记录快照供静止段复用
                            SendAllPins(__instance, p, onlyOutsideAoi: p.Color == EPlayerColor.Black);
                            SnapshotPins(__instance, p, onlyOutsideAoi: p.Color == EPlayerColor.Black);
                        }
                        else
                        {
                            // 静止段：客户端 pin 有存活时间，不重发就会提前消失
                            // （实测"1 秒刚过白点就没了"）。这里继续重发，但用快照位置，
                            // 于是既能存活，又保持静止 —— 即需求里的"停留两秒"。
                            SendSnapshotPins(__instance, p);   // 用已过滤的快照，无需再判
                        }
                    }

                    // ③ 到点触发下一轮扫描
                    if (!NextScanAt.TryGetValue(pid, out float next) || now >= next)
                    {
                        NextScanAt[pid] = now + (ScanIntervalSeconds?.Value ?? 15);
                        TriggerScan(__instance, p, now);
                    }
                }
            }
        }

        private static void TriggerScan(GameRoom room, GamePlayer miyuki, float now)
        {
            int pid = miyuki.PublicInfo.PlayerId;
            bool isBlack = miyuki.Color == EPlayerColor.Black;

            if (isBlack)
            {
                Unlocking.Add(pid);
                // 解封时长用 MarkerSeconds（默认 3 秒），不是 UnlockSeconds(1)。
                // 黑方没有 pin 通道，"地图上能看到人"完全依赖 AOI 解封；
                // 用 1 秒的话解封一结束人就消失，观感就是"只有一瞬间"。
                UnlockUntil[pid] = now + TotalSecs();

                // 主动把所有人介绍给黑方：AoiCullingFeature 的闸门此刻已放行
                var all = room.Players;
                for (int i = 0; i < all.Count; i++)
                {
                    var other = all[i];
                    if (other == null || other == miyuki)
                        continue;
                    if (other.State == EPlayerState.Hide)     // 幽灵/死亡跳过，与原版一致
                        continue;
                    other.AddPlayer(miyuki);
                }
            }

            // 实时段 = "持续把所有人介绍给美幸"的窗口。黑方必须覆盖整个解封期，
            // 否则中途站定不动的人会因为没有新的 Move 事件而不再被刷新。
            // 白方靠 pin 维持，实时段保持 UnlockSeconds 即可。
            // 黑方需覆盖整个解封期持续重发 AddPlayer（否则站定不动的人会被剔除）；
            // 白方只需覆盖"跟随段"，之后靠快照重发维持静止。
            LiveUntil[pid] = now + (isBlack ? TotalSecs() : LiveSecs());

            // 双方都发 pin；黑方只发 AOI 范围外的（范围内原版地图已经会显示）
            SendAllPins(room, miyuki, onlyOutsideAoi: isBlack);
            MarkerUntil[pid] = now + TotalSecs();

            Plugin.Log.LogInfo(
                $"[HS] MiyukiScan：美幸 #{pid} 扫描（{(isBlack ? "黑方：解封 AOI" : "白方：仅地图")}）。");
        }

        /// <summary>把所有人的位置发给该美幸（pin 通道与白方雷达一致）。</summary>
        private static void SendAllPins(GameRoom room, GamePlayer miyuki, bool onlyOutsideAoi = false)
        {
            var all = room.Players;
            for (int i = 0; i < all.Count; i++)
            {
                var other = all[i];
                if (other?.PublicInfo == null || other == miyuki)
                    continue;

                bool visible = other.IsAlive && other.State != EPlayerState.Hide && !other.IsSpectator;

                // 黑方只补 AOI 范围外的标记 —— 范围内原版地图已经会画，
                // 再发 pin 会在那些人身上多叠一个白色方块。
                if (visible && onlyOutsideAoi && miyuki.PublicInfo?.Pos != null && other.PublicInfo.Pos != null)
                {
                    float __range = ResolveBlackPinRange();   // <0 跟随视野；0 不过滤
                    if (__range > 0f
                        && Util.CalculateDistanceSquared(other.PublicInfo.Pos, miyuki.PublicInfo.Pos) <= __range * __range)
                        continue;
                }
                WhiteRadarFeature.SendPin(miyuki,
                    WhiteRadarFeature.PinIdBase + other.PublicInfo.PlayerId,
                    visible ? other.PublicInfo.Pos : null);
            }
        }

        /// <summary>
        /// 每个美幸一份"静止段"快照：miyukiPid → (targetPid → 位置)。
        /// 实时段结束时记录，静止段照此重发 —— 客户端 pin 有存活时间，
        /// 停止重发会导致白点提前消失（实测 1 秒刚过就没了）。
        /// </summary>
        private static readonly Dictionary<int, Dictionary<int, PosInfo>> PinSnapshot
            = new Dictionary<int, Dictionary<int, PosInfo>>();

        /// <summary>记录当前所有可见目标的位置，供静止段复用。</summary>
        private static void SnapshotPins(GameRoom room, GamePlayer miyuki, bool onlyOutsideAoi = false)
        {
            int mpid = miyuki.PublicInfo?.PlayerId ?? 0;
            if (mpid == 0)
                return;

            var snap = new Dictionary<int, PosInfo>();
            var all = room.Players;
            for (int i = 0; i < all.Count; i++)
            {
                var other = all[i];
                if (other?.PublicInfo == null || other == miyuki)
                    continue;
                bool visible = other.IsAlive && other.State != EPlayerState.Hide && !other.IsSpectator;
                if (visible)
                if (visible && onlyOutsideAoi && miyuki.PublicInfo?.Pos != null && other.PublicInfo.Pos != null)
                {
                    float __range = ResolveBlackPinRange();   // <0 跟随视野；0 不过滤
                    if (__range > 0f
                        && Util.CalculateDistanceSquared(other.PublicInfo.Pos, miyuki.PublicInfo.Pos) <= __range * __range)
                        continue;
                }
                    snap[other.PublicInfo.PlayerId] = other.PublicInfo.Pos?.Clone();
            }
            PinSnapshot[mpid] = snap;
        }

        /// <summary>用快照位置重发 pin：保持存活，同时位置静止。</summary>
        private static void SendSnapshotPins(GameRoom room, GamePlayer miyuki)
        {
            int mpid = miyuki.PublicInfo?.PlayerId ?? 0;
            if (mpid == 0 || !PinSnapshot.TryGetValue(mpid, out var snap))
                return;

            foreach (var kv in snap)
            {
                if (kv.Value != null)
                    WhiteRadarFeature.SendPin(miyuki, WhiteRadarFeature.PinIdBase + kv.Key, kv.Value);
            }

            // 快照里没有的人，确保其 pin 已清除（可能在这 2 秒内死亡/躲进柜子）
            var all = room.Players;
            for (int i = 0; i < all.Count; i++)
            {
                var other = all[i];
                if (other?.PublicInfo == null || other == miyuki)
                    continue;
                if (!snap.ContainsKey(other.PublicInfo.PlayerId))
                    WhiteRadarFeature.SendPin(miyuki, WhiteRadarFeature.PinIdBase + other.PublicInfo.PlayerId, null);
            }
        }
        private static void ClearPins(GameRoom room, GamePlayer miyuki)
        {
            var all = room.Players;
            for (int i = 0; i < all.Count; i++)
            {
                var other = all[i];
                if (other?.PublicInfo == null || other == miyuki)
                    continue;

                WhiteRadarFeature.SendPin(miyuki,
                    WhiteRadarFeature.PinIdBase + other.PublicInfo.PlayerId, null);
            }
        }

        /// <summary>解封结束后，把超出 AOI 的人收回（否则 AddPlayer 是持久的，他们会一直可见）。</summary>
        private static void ReapplyCull(GameRoom room, GamePlayer miyuki)
        {
            float enter = AoiCullingFeature.EnterRange?.Value ?? 750f;
            float enterSq = enter * enter;

            var selfPos = miyuki.PublicInfo?.Pos;
            if (selfPos == null)
                return;

            var all = room.Players;
            for (int i = 0; i < all.Count; i++)
            {
                var other = all[i];
                if (other?.PublicInfo == null || other == miyuki)
                    continue;

                var pos = other.PublicInfo.Pos;
                if (pos == null)
                    continue;

                float dx = pos.X - selfPos.X;
                float dy = pos.Y - selfPos.Y;
                if (dx * dx + dy * dy <= enterSq)
                    continue;                            // 仍在范围内，保持可见

                try
                {
                    other.RemovePlayer(miyuki);          // 收回：客户端 despawn
                }
                catch (global::System.Exception ex)
                {
                    Plugin.Log.LogWarning($"[HS] MiyukiScan：收回 #{other.PublicInfo.PlayerId} 失败 — {ex.Message}");
                }
            }
        }

        // ── 生命周期：离开对局时清空全部状态 ────────────────────────────
        [HarmonyPatch(typeof(GameRoom), "StartLobby")]
        internal static class LobbyHook
        {
            [HarmonyPostfix]
            private static void Postfix() => Reset();
        }

        [HarmonyPatch(typeof(GameRoom), "StartDetective")]
        internal static class DetectiveHook
        {
            [HarmonyPostfix]
            private static void Postfix() => Reset();
        }

        private static void Reset()
        {
            Unlocking.Clear();
            NextScanAt.Clear();
            UnlockUntil.Clear();
            MarkerUntil.Clear();
            LiveUntil.Clear();
            PinSnapshot.Clear();      // 跨局 PlayerId 会复用，不清会读到上一局的快照位置
        }
    }
}
