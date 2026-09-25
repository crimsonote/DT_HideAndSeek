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
    /// 黑方美幸有两种方案（[MiyukiScan] BlackMode，白方永远走后者 ——
    /// 原版对白方不画任何他人的原生点（UI_GameScene :74673 早退），pin 是白方唯一来源）：
    ///   WhiteLike（默认） 不解封 AOI，完全靠 S_PIN_MOVE 画点：
    ///     前 UnlockSeconds 秒按真实位置每秒重发（可动段），其余 MarkerSeconds-UnlockSeconds 秒
    ///     用快照重发（静止段），只标 BlackPinRange 之外的人（范围内原版本来就在画）。
    ///   Unseal            扫描期临时解封 AOI（UnlockSeconds 秒）：在 AoiCullingFeature.PrefixAddPlayer
    ///     那个闸门上放行并主动 AddPlayer 所有人 ⇒ 客户端拿到真实 Player 对象（世界模型出现、
    ///     原生小地图点每帧平滑跟随、可索敌）。解封撤销后原生点随即消失，再用快照 pin 把标记留
    ///     MarkerSeconds-UnlockSeconds 秒（"延迟消失"）。解封期间**不发** pin —— 原生点已覆盖全图，
    ///     再发就是同一个人身上两个白点。
    ///
    /// 两个客户端事实（反编译 :74731 RefreshComplyRulesPin / :86096-86118 UI_MinimapSubItem）：
    ///   IsForce=true  → SetLocalPosition：直接 set，**无补间**。新建的控件用它 ⇒ 不会从预制体
    ///                   位置"滑入"（实测过的滑动入场动画）；
    ///   IsForce=false → SetTargetPosition：0.1 秒 DOLocalMove ⇒ 已存在的控件平滑跟随。
    ///   两者坐标换算逐字相同（:86099 vs Util.GetMinimapPosition :26494），所以这个开关只决定
    ///   "补不补间"、不影响位置。故每个 pin **首包用 true、后续包用 false**。
    /// </summary>
    [PatchFeature(
        section: "MiyukiScan",
        description: "美幸被动：每 15 秒扫描全图，在地图上标出所有人的位置（白方与黑方都可用）。",
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
        /// 有人移动时重发 pin 的最小间隔（毫秒）。0 = 关闭该机制，退回原先的每秒一次。
        ///
        /// 为什么需要它：重发原本只挂在 GameRoom.SurvivalTick 上，而那是 **1 Hz** 的节拍
        /// （游戏用 PushAfter(1000) 排的），客户端补间却只有 0.1 秒 ⇒ 表现是"每秒滑一下"。
        /// 挂到 Player.Move 上可以更勤，但有人跑动时 Move 可能每帧触发 ⇒ 必须节流。
        /// </summary>
        [ConfigField(200, "有人移动时重发地图标记的最小间隔（毫秒）；0 = 关闭，退回每秒一次。",
            Min = 0f, Max = 2000f)]
        public static ConfigEntry<int> ResendIntervalMs;

        /// <summary>
        /// 黑美幸 pin 的"范围内"判据：距离 ≤ 本值的人不发 pin（原版地图已经会显示他们，
        /// 再发会多叠一个白色方块）。0 = 关闭该过滤，所有人都发。
        /// 默认 900 对应 AoiCulling.ExitRange —— 想更严格可改为 700（EnterRange）。
        /// </summary>
        [ConfigField(900f, "黑美幸只标记该距离之外的人（0 = 全部标记）。",
            Min = 0f, Max = 3000f)]
        public static ConfigEntry<float> BlackPinRange;

        /// <summary>
        /// 黑方美幸的扫描方案。白方没有这个选择 —— 原版不给白方画他人的原生点，
        /// pin 是白方地图上唯一的信息来源，只能走 WhiteLike。
        /// 默认 WhiteLike（= 与白方一致），Unseal 是可选的原版黑方地图方案。
        /// </summary>
        [ConfigField("WhiteLike",
            "黑方美幸的扫描方案：\n" +
            "WhiteLike = 与白方一致：不解封 AOI，纯 pin（UnlockSeconds 秒可动 + 其余静止），只标 BlackPinRange 之外的人\n" +
            "Unseal    = 原版黑方地图：扫描期临时解封 AOI，解封撤销后 pin 再留一会儿（需 MarkerSeconds > UnlockSeconds）")]
        public static ConfigEntry<string> BlackMode;

        /// <summary>
        /// **测试用**：把自己也画进全图标记里（默认关）。
        ///
        /// 用途：屏幕上多一个"自己也在动"的参照物 —— 一眼能看出 pin 跟不跟得上真实移动，
        /// 用来判断 ResendIntervalMs 该调多少。正常玩法下自己不需要被标记（你本来就知道自己在哪）。
        /// </summary>
        [ConfigField(false, "【测试用】把自己也画进美幸的全图标记。")]
        public static ConfigEntry<bool> ShowSelfOnRadar;
        private const string ModeUnseal = "Unseal";

        /// <summary>黑方是否走「临时解封 AOI」方案。白方与配置缺失/写错时都是 false（= 纯 pin）。</summary>
        private static bool UseUnseal()
            => string.Equals(BlackMode?.Value?.Trim(), ModeUnseal,
                             global::System.StringComparison.OrdinalIgnoreCase);

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

        /// <summary>
        /// 是否要为 <paramref name="other"/> 发/留标记。
        ///
        /// 自己默认不标记（你本来就知道自己在哪），只有测试开关打开时才把自己也算进去 ——
        /// 统一走这一个判据，避免"发的时候算了自己、清的时候不算"这类不一致。
        /// </summary>
        private static bool ShouldMark(GamePlayer miyuki, GamePlayer other)
        {
            if (other == null)
                return false;
            if (other != miyuki)
                return true;
            return ShowSelfOnRadar?.Value ?? false;
        }
        /// <summary>美幸的角色 DataId（CharacterData.json / CharacterDic）。</summary>
        private const int MiyukiCharacterId = 107;

        /// <summary>正在 AOI 解封中的美幸 PlayerId —— 供 AoiCullingFeature 查询。</summary>
        private static readonly HashSet<int> Unlocking = new HashSet<int>();

        private static readonly Dictionary<int, float> NextScanAt = new Dictionary<int, float>();
        private static readonly Dictionary<int, float> UnlockUntil = new Dictionary<int, float>();
        private static readonly Dictionary<int, float> MarkerUntil = new Dictionary<int, float>();

        /// <summary>PlayerId → 实时段结束时刻（此前每秒重发 pin 以跟随真实位置）。</summary>
        private static readonly Dictionary<int, float> LiveUntil = new Dictionary<int, float>();

        /// <summary>上次因"有人移动"而重发 pin 的时刻（SurviveTime）。跨局必须清。</summary>
        private static float _lastMoveResendAt = -9999f;

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
                    if (p?.PublicInfo == null)
                        continue;
                    if (!HasMiyukiAbility(p))
                        continue;                        // 只处理「本人是美幸」或「技能被换成美幸」的人

                    int pid = p.PublicInfo.PlayerId;

                    // 本人在不在场上。**清理分支必须排在这个判据之前**：原先写的是
                    // `!p.IsAlive → continue`，于是美幸在本局窗口内阵亡时，
                    // ClearPins / ReapplyCull 被一起跳过、pin 残留到本局结束
                    //（下面 :267 附近那条兜底注释原本就记着这个坑，现已修掉）。
                    // 清理是"收拾自己留下的东西"，与本人是否还在场上无关；
                    // 而 ②b 起都是给活人看的实时标记，退场的人不该做 —— 守卫放这里正好分开两者。
                    bool present = p.IsAlive && !p.IsSpectator;

                    // ① 解封到点 → 收回 AOI，并抓一次快照供后面的"延迟消失"段重发
                    if (Unlocking.Contains(pid) && UnlockUntil.TryGetValue(pid, out float uu) && now >= uu)
                    {
                        Unlocking.Remove(pid);
                        UnlockUntil.Remove(pid);
                        ReapplyCull(__instance, p);
                        // 解封撤销的这一刻原生点就没了，用这一帧的位置把标记留住
                        //（"延迟消失"；范围内的人 ReapplyCull 保留了原生点，由 BlackPinRange 过滤）。
                        // 本人已退场时后面不会再有重发，快照没有意义 —— 不抓（死人身上不做多余动作）。
                        if (present)
                            SnapshotPins(__instance, p, onlyOutsideAoi: true);
                    }

                    // ② 标记到点 → 撤销 pin
                    if (MarkerUntil.TryGetValue(pid, out float mu) && now >= mu)
                    {
                        MarkerUntil.Remove(pid);
                        ClearPins(__instance, p);
                    }

                    if (!present)
                        continue;                        // ②b 起是给活人看的实时标记，退场的人不做

                    // ②b 可动段（now < LiveUntil）：按真实位置每秒重发 → 地图上跟随；
                    //     静止段：继续重发但用快照位置 —— 客户端控件没有 TTL，不重发会提前消失，
                    //     用快照重发则既存活又静止。
                    if (MarkerUntil.ContainsKey(pid))
                    {
                        if (LiveUntil.TryGetValue(pid, out float live) && now < live)
                        {
                            // 正在解封中的黑方由原生点负责显示（每帧跟随），再发 pin 就是同一个人
                            // 身上两个白点；快照照常记录，解封撤销后按它重发。
                            if (!Unlocking.Contains(pid))
                                SendAllPins(__instance, p, onlyOutsideAoi: p.Color == EPlayerColor.Black);
                            SnapshotPins(__instance, p, onlyOutsideAoi: p.Color == EPlayerColor.Black);
                        }
                        else
                        {
                            SendSnapshotPins(__instance, p);   // 用已过滤的快照，无需再判
                        }
                    }

                    // ③ 到点触发下一轮扫描
                    if (!NextScanAt.TryGetValue(pid, out float next) || now >= next)
                    {
                        NextScanAt[pid] = now + (ScanIntervalSeconds?.Value ?? 15);
                        Diagnostics.Hit("MiyukiScan");
                        TriggerScan(__instance, p, now);
                    }
                }
            }
        }

        /// <summary>
        /// 有人移动时更勤地重发 pin —— 补 TickHook 那 1 Hz 的短板，让可动段跟得上。
        ///
        /// 只做「可动段」的重发：静止段的位置本来就不变，多发无意义。
        /// 与 TickHook 的 ②b 保持一致地跳过「解封中」的黑方 —— 那期间原生点每帧跟随，
        /// 再发 pin 就是同一个人身上叠两个白点（见 TickHook 里的既有注释）。
        ///
        /// 节流是必须的：有人跑动时 Move 可能每帧触发，不节流就是每帧发包。
        /// </summary>
        [HarmonyPatch(typeof(GamePlayer), nameof(GamePlayer.Move), new[] { typeof(PosInfo), typeof(bool) })]
        internal static class MoveResendHook
        {
            [HarmonyPostfix]
            private static void Postfix(GamePlayer __instance)
            {
                if (ModeRuntime.Bypass)
                    return;

                int interval = ResendIntervalMs?.Value ?? 200;
                if (interval <= 0)
                    return;                              // 关闭该机制

                var room = GameRoom.Instance;
                if (room?.Players == null)
                    return;

                float now = TimeManager.Instance?.SurviveTime ?? 0f;
                if (now - _lastMoveResendAt < interval / 1000f)
                    return;                              // 节流
                _lastMoveResendAt = now;

                var all = room.Players;
                for (int i = 0; i < all.Count; i++)
                {
                    var p = all[i];
                    if (p?.PublicInfo == null || !EnableForMiyuki.Value)
                        continue;
                    if (p.CharacterId != MiyukiCharacterId)
                        continue;

                    int pid = p.PublicInfo.PlayerId;
                    if (!MarkerUntil.ContainsKey(pid))
                        continue;                        // 当前没有标记，不需要重发

                    if (!LiveUntil.TryGetValue(pid, out float live) || now >= live)
                        continue;                        // 静止段：位置不变，交给 TickHook

                    if (Unlocking.Contains(pid))
                        continue;                        // 解封中：由原生点负责跟随

                    SendAllPins(room, p, onlyOutsideAoi: p.Color == EPlayerColor.Black);
                }
            }
        }
        private static void TriggerScan(GameRoom room, GamePlayer miyuki, float now)
        {
            int pid = miyuki.PublicInfo.PlayerId;
            bool isBlack = miyuki.Color == EPlayerColor.Black;
            bool unseal = isBlack && UseUnseal();          // 方案 A 只对黑方有意义
            float live = LiveSecs();

            if (unseal)
            {
                // 方案 A：扫描期临时解封 AOI。闸门放行后主动把所有人介绍给黑方 ——
                // 客户端因此拿到真实 Player 对象：世界模型出现、原生小地图点每帧平滑跟随、可索敌。
                Unlocking.Add(pid);
                UnlockUntil[pid] = now + live;

                var all = room.Players;
                for (int i = 0; i < all.Count; i++)
                {
                    var other = all[i];
                    if (!ShouldMark(miyuki, other))
                        continue;
                    if (other.State == EPlayerState.Hide)     // 幽灵/死亡/躲柜子跳过，与原版一致
                        continue;
                    other.AddPlayer(miyuki);
                }
            }

            // 新一轮扫描开始：丢掉上一轮遗留的"客户端已有该控件"标记。
            // 正常路径下 ClearPins 已经清过；这里是兜底。
            //（TickHook 的存活守卫曾把 ClearPins 一起跳过 —— 美幸在窗口内阵亡时 pin 残留到本局结束；
            //  该缺陷已修：清理分支现在排在存活判据之前。本条兜底保留，仍防"清理没跑到"的其它路径。）
            PinAlive.Remove(pid);

            // 可动段 = 解封期（方案 A）或 pin 跟随段（白方 / 方案 B），两端都是 UnlockSeconds。
            LiveUntil[pid] = now + live;
            MarkerUntil[pid] = now + TotalSecs();

            if (!unseal)
            {
                // 纯 pin 方案：立刻发首包并记录快照。黑方只标 AOI 范围外的
                //（范围内原版地图已经会画，再发会在同一个人身上叠一个白色方块）。
                SendAllPins(room, miyuki, onlyOutsideAoi: isBlack);
                SnapshotPins(room, miyuki, onlyOutsideAoi: isBlack);
            }
            // 方案 A 这里不发 pin：原生点已覆盖全图，再发就是两个白点。
            // 标记由 ① 撤销解封那一 tick 抓的快照 + 静止段重发完成。

            Plugin.Log.LogInfo(
                $"[HS] MiyukiScan：美幸 #{pid} 扫描（{(isBlack ? (unseal ? "黑方：解封 AOI" : "黑方：纯 pin") : "白方：纯 pin")}）。");
        }

        /// <summary>把所有人的位置发给该美幸（pin 通道与白方雷达一致）。</summary>
        private static void SendAllPins(GameRoom room, GamePlayer miyuki, bool onlyOutsideAoi = false)
        {
            var all = room.Players;
            for (int i = 0; i < all.Count; i++)
            {
                var other = all[i];
                if (!ShouldMark(miyuki, other))
                    continue;

                bool visible = other.IsAlive && other.State != EPlayerState.Hide && !other.IsSpectator;

                // 黑方只补 AOI 范围外的标记 —— 范围内原版地图已经会画，
                // 再发 pin 会在那些人身上多叠一个白色方块。
                if (visible && onlyOutsideAoi && miyuki.PublicInfo?.Pos != null && other.PublicInfo.Pos != null)
                {
                    float __range = ResolveBlackPinRange();   // <0 跟随视野；0 不过滤
                    if (__range > 0f
                        && Util.CalculateDistanceSquared(other.PublicInfo.Pos, miyuki.PublicInfo.Pos) <= __range * __range)
                    {
                        // 走进视野的人身上若还留着上一秒发的 pin，必须显式撤销 ——
                        // 只 `continue` 的话那个点会冻在他上一秒的位置（人已经进范围），
                        // 地图上就多一个位置对不上的"幽灵点"。
                        // 但只在 pin 确实还在时才发：客户端对"删不存在的 pin"会先 CreatePin
                        // 再 DeletePin（:74734-74742），每秒空发就是每秒一次 Instantiate/Destroy。
                        if (PinExists(miyuki, other.PublicInfo.PlayerId))
                            SendPinTracked(miyuki, other.PublicInfo.PlayerId, null);
                        continue;
                    }
                }
                SendPinTracked(miyuki, other.PublicInfo.PlayerId,
                    visible ? other.PublicInfo.Pos : null);
            }
        }

        /// <summary>
        /// 客户端"这个 pin 控件已经存在"的集合：miyukiPid → 目标 pid。
        /// 首包必须 isForce=true（控件是 CreatePin 新建的，直接 set 才不滑入）；
        /// 已存在的控件再传 false 才是 0.1 秒补间。删除哨兵会把控件 Destroy，故必须同步移除。
        /// </summary>
        private static readonly Dictionary<int, HashSet<int>> PinAlive = new Dictionary<int, HashSet<int>>();

        /// <summary>客户端此刻是否还有这个 pin 控件。</summary>
        private static bool PinExists(GamePlayer miyuki, int targetPid)
        {
            int mpid = miyuki.PublicInfo?.PlayerId ?? 0;
            return mpid != 0
                && PinAlive.TryGetValue(mpid, out var set)
                && set.Contains(targetPid);
        }

        /// <summary>发一个玩家标记：首包直接定位（无入场动画），后续包走 0.1 秒补间（平滑跟随）。</summary>
        private static void SendPinTracked(GamePlayer miyuki, int targetPid, PosInfo pos)
        {
            int mpid = miyuki.PublicInfo?.PlayerId ?? 0;
            if (mpid == 0)
                return;

            if (pos == null)
            {
                // 删除哨兵：客户端在 IsForce 分支之前就 DeletePin 并 return，isForce 无意义
                if (PinAlive.TryGetValue(mpid, out var alive))
                    alive.Remove(targetPid);
                WhiteRadarFeature.SendPin(miyuki, WhiteRadarFeature.PinIdBase + targetPid, null);
                return;
            }

            if (!PinAlive.TryGetValue(mpid, out var set))
            {
                set = new HashSet<int>();
                PinAlive[mpid] = set;
            }

            bool first = set.Add(targetPid);       // true = 客户端还没有这个控件 ⇒ 首包
            WhiteRadarFeature.SendPin(miyuki, WhiteRadarFeature.PinIdBase + targetPid, pos,
                isForce: first);
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
                if (!ShouldMark(miyuki, other))
                    continue;
                bool visible = other.IsAlive && other.State != EPlayerState.Hide && !other.IsSpectator;

                // ⚠ 这里以前是 `if (visible)` 后面紧跟另一个 `if (visible && …) { … continue; }`，
                // 而 `snap[…] = …` 排在两个 if **之外**、缩进却像是被包住的 —— C# 不看缩进，
                // 所以那一行是 for 体的最后一句，**无条件执行** ⇒ 死者 / 躲藏者 / 观战者
                // 全都被写进快照，再由 SendSnapshotPins 重发成 pin
                //（玩家实测：地图上能看到尸体或幽灵）。
                if (!visible)
                    continue;

                // 黑美幸只标记该距离之外的人；__range <= 0 表示不过滤
                if (onlyOutsideAoi && miyuki.PublicInfo?.Pos != null && other.PublicInfo.Pos != null)
                {
                    float __range = ResolveBlackPinRange();
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
                    SendPinTracked(miyuki, kv.Key, kv.Value);
            }

            // 快照里没有的人，确保其 pin 已清除（可能在这 2 秒内死亡/躲进柜子）
            var all = room.Players;
            for (int i = 0; i < all.Count; i++)
            {
                var other = all[i];
                if (!ShouldMark(miyuki, other))
                    continue;
                if (!snap.ContainsKey(other.PublicInfo.PlayerId))
                    SendPinTracked(miyuki, other.PublicInfo.PlayerId, null);
            }
        }
        private static void ClearPins(GameRoom room, GamePlayer miyuki)
        {
            var all = room.Players;
            for (int i = 0; i < all.Count; i++)
            {
                var other = all[i];
                if (!ShouldMark(miyuki, other))
                    continue;

                SendPinTracked(miyuki, other.PublicInfo.PlayerId, null);
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
                if (!ShouldMark(miyuki, other))
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
            PinAlive.Clear();
            _lastMoveResendAt = -9999f;   // 移动重发的节流时刻 —— 它记的也是 SurviveTime
        }
    }
}
