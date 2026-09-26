using System.Collections.Generic;
using BepInEx.Configuration;
using HarmonyLib;
using Protocol;
using Server.Game;
using HideAndSeek.Core;
using HideAndSeek.Features.Vision;
using GamePlayer = Server.Game.Player;
using GameDtStage = Server.Game.DeadlyTrickStage;      // AGENTS 坑 #1：同名类，必须显式限定
using GameCabinet = Server.Game.Cabinet;
using GameCorpse = Server.Game.Corpse;
using GameDeviceManager = Server.Game.DeviceManager;
using GameOccult = Server.Game.Occult;                  // 教室魔法阵/蜡烛（服务端）

namespace HideAndSeek.Features.Skill
{
    /// <summary>
    /// 莲 · 尸体复活仪式（4 阶段）。
    ///
    /// 玩法（需求原文）：把一具尸体丢进教室 DT 点（魔法阵），点燃六根蜡烛把它翻出来 ——
    /// **每这样翻出一次算一个阶段**，攒满 4 次，这具尸体的主人复活。
    ///
    ///   阶段一：将遗骸与其灵魂通过仪式建立联系(1/4)
    ///   阶段二：将遗骸与其灵魂通过仪式建立联系(2/4)
    ///   阶段三：呼引灵魂的到来，呼引灵魂的回归(3/4)
    ///   阶段四：呼引灵魂的回归(4/4)
    ///   没有灵魂 / 下线：灵魂已然远去
    ///   复活成功：【角色名】重回于世间
    ///
    /// 约束（按需求）：
    ///   · **第二、三、四阶段必须连自己待在 DT 点内**；离开 ⇒ 视为放弃、进度清零
    ///   · **两次必须是同一具尸体**；中途换成另一具 ⇒ 从新那具的第 1 阶段重来
    ///
    /// 挂点说明：原版"点六根蜡烛把尸体翻出来"是一条现成的链 ——
    ///   `Occult.InteractOccultCandle`(:167865) 逐根点火 → 全为 State 3 时
    ///   `MissionManager.CheckSummonCandles`(:166842) 取 `GetSubmergedOccultCorpse()`
    ///   并调 `Surface()`(:166855) 把它翻出来。
    /// 所以**钩 CheckSummonCandles 就等于钩到"翻出尸体"这一刻**，不必自己去数蜡烛。
    ///
    /// 复活怎么落地（Round 7 查证）：
    ///   服务端把 `IsAlive` 设回 true（`OnDead` 里正是设 false 的 :175974），
    ///   再 `Move(pos, force: true)`(:175883) —— 它会 `Broadcast(_pkt_respawn)`
    ///   把 `S_RESPAWN{PlayerId, Pos}` 发出去，客户端据此把角色搬回去并复活外观；
    ///   然后由"范围约束"把他限制在 DT 点半径内（见 <see cref="HoldSoul"/>）——
///   **不是**钉死原地：`Move(force)` 顺带置的 `MoveLock` 会立刻解除。
    /// </summary>
    [PatchFeature("LianRitual",
        "莲·尸体复活仪式：把尸体丢进教室 DT 点并用六根蜡烛翻出（每翻出一次算一阶段），攒满 4 阶段复活该尸体；连必须在 DT 点内，且两次须为同一具尸体。",
        defaultEnabled: false, side: FeatureSide.Host)]
    internal static class LianRitualFeature
    {
        [ConfigField(4, "需要翻出几次尸体才能复活（阶段总数）。", Min = 1f, Max = 8f)]
        public static ConfigEntry<int> Stages;

        [ConfigField(10f, "复活后灵魂不能离开 DT 点的秒数。", Min = 0f, Max = 120f)]
        public static ConfigEntry<float> SoulHoldSeconds;

        /// <summary>尸体 ID → 已经完成的阶段数。</summary>
        private static readonly Dictionary<int, int> Progress = new Dictionary<int, int>();

        /// <summary>尸体 ID → 正在主持仪式的莲 PlayerId。</summary>
        private static readonly Dictionary<int, int> Host = new Dictionary<int, int>();

        /// <summary>尸体 ID → 该仪式是否"曾经推进过"（用于离开时判定要不要播报放弃）。</summary>
        private static readonly HashSet<int> Active = new HashSet<int>();

        /// <summary>
        /// 已经"牵引过灵魂"的尸体 —— 每轮蜡烛只牵引一次。
        ///
        /// 需求：牵引发生在**阶段 4 进行、点燃第一根蜡烛时**（不是六根全亮之后）。
        /// 所以钩在 `Occult.InteractOccultCandle`（每点一根都会走）上，
        /// 条件是"当前仪式差最后一次满"（即这一轮就是第 4 阶段），并且这一轮还没牵引过。
        /// 每推进一次阶段就清空它，下一轮重新计数。
        /// </summary>
        private static readonly HashSet<int> PulledThisRound = new HashSet<int>();

        /// <summary>`Player.IsAlive` 的 setter（private ⇒ 只能反射拿）。</summary>
        private static readonly global::System.Reflection.MethodInfo AliveSetter =
            AccessTools.PropertySetter(typeof(GamePlayer), "IsAlive");

        private static float Now => TimeManager.Instance?.SurviveTime ?? 0f;

        private static bool IsLian(GamePlayer p)
            => p?.SkillComponent?.Data != null && p.SkillComponent.Data.Type == ESkillType.SoulSense;

        /// <summary>
        /// 场上是否有一位**有莲能力、且正站在 DT 点范围内**的人。
        ///
        /// 需求原文：「莲(或有莲能力的小偷与其他人)在 DT 点范围，上述动作发生
        /// (不一定需要是能力拥有者行动，可以其他人帮忙点蜡烛脱尸体)即可复活。
        /// **但是只有其他人不触发**。」
        ///
        /// ⇒ 判据是"**有人在**"，不是"**是他在翻**" —— 翻尸体/点蜡烛可以是别人干的。
        /// 返回第一位符合条件的人（用它来播报阶段文本），没有则返回 null。
        /// </summary>
        private static GamePlayer FindLianInDtRange()
        {
            var room = GameRoom.Instance;
            if (room?.Players == null)
                return null;
            for (int i = 0; i < room.Players.Count; i++)
            {
                var p = room.Players[i];
                if (p?.PublicInfo == null || !p.IsAlive)
                    continue;
                if (!IsLian(p))
                    continue;                       // IsLian 已覆盖"偷到灵魂感知"的情况
                if (InDtRange(p))
                    return p;
            }
            return null;
        }

        /// <summary>DT 点 = 能藏尸的设备：DeadlyTrickStage（魔法阵那类）与 Cabinet（柜子）。</summary>
        private static bool IsDtDevice(Device d) => d is GameDtStage || d is GameCabinet;

        private static bool InDtRange(GamePlayer player)
        {
            var pos = player?.PublicInfo?.Pos;
            var devices = GameDeviceManager.Instance?.Objects;
            if (pos == null || devices == null)
                return false;

            float r = LianAltarFeature.DtRadius?.Value ?? 350f;
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

        // ══ B2：有人把尸体"埋"进 DT 点 ⇒ 当前仪式作废，改从新那具从头开始 ══
        //
        // 需求原文：「第一次 DT 点蜡烛翻出来 A 尸体，然后**埋 B 尸体**，会改为从 B 尸体从头开始复活。」
        // 所以"埋"这个动作本身就该清零 —— 钩 `DeviceManager.CreateHiddenCorpse`
        // （:162030 藏尸、:164252 藏到水里都会走它）。
        // 这里不清"某个具体尸体"，而是清掉**当前所有进行中的仪式**：
        // 埋进去的那具还没被翻出来，谈不上登记进度，等它被翻出时自然从 0 开始。
        [HarmonyPatch(typeof(GameDeviceManager), "CreateHiddenCorpse")]
        internal static class BuryHook
        {
            [HarmonyPostfix]
            private static void Postfix()
            {
                if (ModeRuntime.Bypass || Progress.Count == 0)
                    return;

                Progress.Clear();
                Host.Clear();
                Active.Clear();
                Plugin.Log.LogInfo("[HS] LianRitual：有尸体被埋入 DT 点，进行中的仪式已清零（下次翻出从头开始）。");
            }
        }

        // ══ N3：阶段 4 进行中、**点燃第一根蜡烛时**就把灵魂牵引过来 ══
        //
        // 需求：「在**阶段4进行(点燃第一根蜡烛)时**，灵魂将被牵引至 DT 点」——
        // 不是"六根全亮、尸体被翻出之后"。而 `Occult.InteractOccultCandle`(:167865)
        // 正是"每点燃一根蜡烛"都会走的服务端方法（private，所以用字符串定位）。
        //
        // 判定"这一轮就是阶段 4"的办法：当前进度 == stages - 1（差最后一次就满）。
        // 每轮只牵引一次（`PulledThisRound`），阶段推进时重置。
        [HarmonyPatch(typeof(GameOccult), "InteractOccultCandle")]
        internal static class CandleHook
        {
            [HarmonyPostfix]
            private static void Postfix()
            {
                if (ModeRuntime.Bypass || Progress.Count == 0)
                    return;

                int stages = Stages?.Value ?? 4;
                List<int> todo = null;
                foreach (var kv in Progress)
                {
                    if (kv.Value != stages - 1)
                        continue;                       // 只有"差最后一次满"的那一轮才算阶段 4
                    if (PulledThisRound.Contains(kv.Key))
                        continue;                       // 本轮已经牵引过
                    (todo ?? (todo = new List<int>())).Add(kv.Key);
                }
                if (todo == null)
                    return;

                var lian = FindLianInDtRange();
                foreach (int corpseId in todo)
                {
                    PulledThisRound.Add(corpseId);
                    var corpse = FindCorpse(corpseId);
                    if (corpse != null)
                        PullSoul(corpse, lian);
                }
            }
        }

        /// <summary>按设备 ID 找尸体（尸体的 ID 就是死者的 PlayerId）。</summary>
        private static GameCorpse FindCorpse(int corpseId)
        {
            var list = GameDeviceManager.Instance?.Corpses;
            if (list == null)
                return null;
            for (int i = 0; i < list.Count; i++)
            {
                var c = list[i];
                if (c != null && c.ID == corpseId)
                    return c;
            }
            return null;
        }

        // ══ 阶段推进：原版"蜡烛全亮 ⇒ 尸体被翻出"的那一刻 ══
        // MissionManager 是**内部类型**（不允许直接 typeof），按 AGENTS 坑 #3
        // 用 [HarmonyTargetMethod] + AccessTools.TypeByName 定位。
        [HarmonyPatch]
        internal static class SummonHook
        {
            [HarmonyTargetMethod]
            private static global::System.Reflection.MethodBase TargetMethod()
            {
                var t = AccessTools.TypeByName("Server.Game.MissionManager")
                        ?? AccessTools.TypeByName("MissionManager");
                return t == null ? null : AccessTools.Method(t, "CheckSummonCandles");
            }

            [HarmonyPostfix]
            private static void Postfix(GamePlayer player)
            {
                if (ModeRuntime.Bypass || player?.PublicInfo == null)
                    return;

                var room = GameRoom.Instance;
                if (room?.State != EGameState.Survive)
                    return;

                // 被翻出来的那具尸体（原版紧接着就对它调 Surface）
                GameCorpse corpse = null;
                try { corpse = GameDeviceManager.Instance?.GetSubmergedOccultCorpse(); }
                catch { /* 拿不到就当没有 */ }
                if (corpse == null || corpse.ID <= 0)
                    return;

                int corpseId = corpse.ID;

                // 需求：触发条件是"**有一位有莲能力的人在 DT 点范围内**"，而不是"翻尸体的人就是莲"。
                // 翻尸体/点蜡烛可以是别人干的；但只要没有莲在范围内，就**不触发**。
                var lian = FindLianInDtRange();
                if (lian?.PublicInfo == null)
                {
                    Plugin.Log.LogInfo(
                        "[HS] LianRitual：有尸体被翻出，但 DT 点范围内没有莲 —— 不推进仪式。");
                    return;
                }
                int pid = lian.PublicInfo.PlayerId;

                // 换尸体 ⇒ 从新那具的第 1 阶段重来（需求明确要求）
                if (Host.TryGetValue(corpseId, out int hostPid) && hostPid != pid)
                {
                    Progress.Remove(corpseId);
                    Host.Remove(corpseId);
                    Active.Remove(corpseId);
                }
                // 若这个人手上另有一具未完成的仪式 ⇒ 那具作废（同一时刻只主持一具）
                List<int> stale = null;
                foreach (var kv in Host)
                {
                    if (kv.Value == pid && kv.Key != corpseId)
                        (stale ?? (stale = new List<int>())).Add(kv.Key);
                }
                if (stale != null)
                {
                    foreach (int c in stale)
                    {
                        Progress.Remove(c);
                        Host.Remove(c);
                        Active.Remove(c);
                    }
                }

                int stages = Stages?.Value ?? 4;
                int done = (Progress.TryGetValue(corpseId, out int d) ? d : 0) + 1;
                Progress[corpseId] = done;
                PulledThisRound.Remove(corpseId);      // 新的一轮开始，重新允许牵引
                Host[corpseId] = pid;
                Active.Add(corpseId);

                Plugin.Log.LogInfo(
                    $"[HS] LianRitual：尸体 #{corpseId} 仪式阶段 {done}/{stages}（主持者 #{pid}）。");

                PulseSoulCandle(player);

                // 牵引已经在"阶段 4 点燃第一根蜡烛"时做过了（见 CandleHook），这里不重复。
                StageText(player, corpse, done, stages);

                if (done < stages)
                    return;

                // ── 满阶段 ⇒ 复活 ──
                Progress.Remove(corpseId);
                Host.Remove(corpseId);
                Active.Remove(corpseId);
                Revive(corpse, player);
            }
        }

        /// <summary>
        /// 每完成一个阶段就"唤起一根蜡烛" —— 指的是**莲自己的感知死亡动画**，
        /// 不是魔法阵那 6 台蜡烛设备。
        ///
        /// 原版那条通道很直接：`Player.SendDeadNotify()`（`Assembly-CSharp:176080`）
        /// 只把 `S_NOTIFY_DEAD` 发给 `Data.Type == SoulSense` 的**存活**玩家（:176085），
        /// 客户端 `Handle_S_NOTIFY_DEAD`(:42597) 收到就
        /// `ShowMiddleUI<UI_SoulSence>().StartAnimation()`(:42600) ——
        /// 即那个 Candle / Smoke / Fire 的无参动画（`UI_SoulSence` :61773）。
        ///
        /// ⇒ 我们只要**单独给莲补一份 `S_NOTIFY_DEAD`**，就能在她客户端上再唤起一根蜡烛。
        /// </summary>
        private static void PulseSoulCandle(GamePlayer lian)
        {
            if (lian?.Session == null)
                return;
            try
            {
                lian.Session.Send(new S_NOTIFY_DEAD());
                Plugin.Log.LogInfo($"[HS] LianRitual：已为莲 #{lian.PublicInfo?.PlayerId ?? 0} 唤起一根灵魂蜡烛。");
            }
            catch (global::System.Exception ex)
            {
                Plugin.Log.LogWarning($"[HS] LianRitual：唤起灵魂蜡烛失败 — {ex.Message}");
            }
        }
        /// <summary>阶段文本（需求给定原文；复活/失败另有两条）。</summary>
        private static void StageText(GamePlayer host, GameCorpse corpse, int done, int stages)
        {
            string text;
            if (done >= stages)
                text = "呼引灵魂的回归(4/4)";
            else if (done == 3)
                text = "呼引灵魂的到来，呼引灵魂的回归(3/4)";
            else
                text = $"将遗骸与其灵魂通过仪式建立联系({done}/{stages})";

            try { Core.ChatOut.ToPlayer(host, text); }
            catch (global::System.Exception ex)
            {
                Plugin.Log.LogWarning($"[HS] LianRitual：阶段文本发送失败 — {ex.Message}");
            }
        }

        /// <summary>
        /// 阶段 4 推进时把"灵魂"牵引到 DT 点：只搬人 + 锁住，**不复活**。
        /// 复活是阶段 4 走完之后的事（<see cref="Revive"/>）。
        /// </summary>
        private static void PullSoul(GameCorpse corpse, GamePlayer host)
        {
            if (corpse == null)
                return;
            var owner = FindPlayer(corpse.ID);
            if (owner?.PublicInfo == null || owner.Session == null)
                return;                                 // 没有灵魂（下线）——复活那一步会播报"灵魂已然远去"

            var pos = corpse.DeviceInfo?.Pos ?? owner.PublicInfo.Pos;
            try
            {
                // 只把角色搬过去（Move(force) 会广播 S_RESPAWN）。
                // 不去动 MoveLock —— 牵引后仍要在范围内自由活动。
                owner.PublicInfo.Pos = pos;
                owner.Move(pos, force: true);
                // 立刻解除 Move(force:true) 顺带置上的移动锁，改由范围约束管
                try { owner.MoveLock = false; } catch { }
                HoldSoul(owner, pos, SoulHoldSeconds?.Value ?? 10f);
                Plugin.Log.LogInfo($"[HS] LianRitual：阶段 4 —— 灵魂已被牵引至 DT 点（#{corpse.ID}）。");
            }
            catch (global::System.Exception ex)
            {
                Plugin.Log.LogWarning($"[HS] LianRitual：牵引灵魂失败 — {ex.Message}");
            }
        }

        /// <summary>按 PlayerId 在房里找玩家。</summary>
        private static GamePlayer FindPlayer(int pid)
        {
            var room = GameRoom.Instance;
            if (room?.Players == null)
                return null;
            for (int i = 0; i < room.Players.Count; i++)
            {
                var p = room.Players[i];
                if (p?.PublicInfo != null && p.PublicInfo.PlayerId == pid)
                    return p;
            }
            return null;
        }

        /// <summary>把该尸体的主人复活：先在原位站起来，再广播 S_RESPAWN；并锁住 10 秒。</summary>
        private static void Revive(GameCorpse corpse, GamePlayer host)
        {
            // Corpse 的 ID 就是死者的 PlayerId（构造函数 base.ID = playerInfo.PlayerId, :168794）
            int ownerPid = corpse.ID;
            GamePlayer owner = null;
            var room = GameRoom.Instance;
            if (room?.Players != null)
            {
                for (int i = 0; i < room.Players.Count; i++)
                {
                    var p = room.Players[i];
                    if (p?.PublicInfo != null && p.PublicInfo.PlayerId == ownerPid) { owner = p; break; }
                }
            }
            if (owner?.PublicInfo == null || owner.Session == null)
            {
                // 没有灵魂（下线/离场）——按需求播报这一条
                try { Core.ChatOut.ToPlayer(host, "灵魂已然远去"); } catch { }
                Plugin.Log.LogInfo("[HS] LianRitual：该尸体没有对应的在线玩家，复活取消。");
                return;
            }

            var pos = corpse.DeviceInfo?.Pos ?? owner.PublicInfo.Pos;
            try
            {
                // 服务端 Player.IsAlive 是 private set（:175304），外部设不了 ⇒ 反射写它。
                // 这是本功能唯一"碰内部状态"的地方，实机要重点验。
                AliveSetter?.Invoke(owner, new object[] { true });
                owner.PublicInfo.Pos = pos;
                owner.Move(pos, force: true);
                try { owner.MoveLock = false; } catch { }   // 不钉死，交给范围约束
                owner.State = EPlayerState.Idle;

                string name = owner.Name ?? ("#" + owner.PublicInfo.PlayerId);
                Core.ChatOut.Broadcast($"{name} 重回于世间", EChatType.DeviceChat);

                // 需求：灵魂被牵引至 DT 点，10 秒内不能离开
                float hold = SoulHoldSeconds?.Value ?? 10f;
                if (hold > 0f)
                    HoldSoul(owner, pos, hold);

                Plugin.Log.LogInfo($"[HS] LianRitual：已复活 #{owner.PublicInfo.PlayerId}（{name}）。");
            }
            catch (global::System.Exception ex)
            {
                Plugin.Log.LogWarning($"[HS] LianRitual：复活失败 — {ex.Message}");
            }
        }

        /// <summary>
        /// 把灵魂限制在 DT 点**范围内**（hold 秒）。
        ///
        /// 注意语义：是"**不能离开这个范围**"，不是"钉死在原地" ——
        /// 玩家在半径内仍可自由走动。所以这里**不设 `MoveLock`**
        /// （那会让角色完全动不了，是上一版写错的地方），
        /// 改为每秒检查一次：跑出半径就把他拉回边界上。
        /// </summary>
        private static void HoldSoul(GamePlayer player, PosInfo pos, float hold)
        {
            float until = Now + hold;
            SoulHolds[player.PublicInfo.PlayerId] = new SoulHold { Pos = pos, Until = until };
        }

        private sealed class SoulHold
        {
            public PosInfo Pos;
            public float Until;
        }

        private static readonly Dictionary<int, SoulHold> SoulHolds = new Dictionary<int, SoulHold>();

        // ══ 每秒：① 连离开 DT 点 ⇒ 放弃仪式 ② 灵魂牵引到期 ⇒ 解锁 ══
        [HarmonyPatch(typeof(GameRoom), "SurvivalTick")]
        internal static class TickHook
        {
            [HarmonyPostfix]
            private static void Postfix(GameRoom __instance)
            {
                if (ModeRuntime.Bypass || __instance?.Players == null)
                    return;

                // ① 仪式放弃判定：只在生存阶段
                if (__instance.State == EGameState.Survive && Active.Count > 0)
                {
                    // 需求："离开 DT 点也视为放弃进度"。判据是"**还有没有莲在 DT 点内**"，
                    // 而不是"当初那位主持者还在不在"（可以是别人替她翻尸体的）。
                    List<int> giveUp = null;
                    if (FindLianInDtRange() == null)
                        giveUp = new List<int>(Active);

                    if (giveUp != null)
                    {
                        foreach (int corpseId in giveUp)
                        {
                            int hostPid = Host.TryGetValue(corpseId, out int h) ? h : 0;
                            Progress.Remove(corpseId);
                            Host.Remove(corpseId);
                            Active.Remove(corpseId);

                            for (int i = 0; i < __instance.Players.Count; i++)
                            {
                                var p = __instance.Players[i];
                                if (p?.PublicInfo != null && p.PublicInfo.PlayerId == hostPid)
                                { try { Core.ChatOut.ToPlayer(p, "仪式中断：莲已离开 DT 点。"); } catch { } }
                            }
                            Plugin.Log.LogInfo($"[HS] LianRitual：尸体 #{corpseId} 的仪式已放弃（主持者离开）。");
                        }
                    }
                }

                // ② 灵魂牵引：到期就放人
                if (SoulHolds.Count > 0)
                {
                    float now = Now;
                    List<int> done = null;
                    foreach (var kv in SoulHolds)
                    {
                        if (now < kv.Value.Until)
                        {
                            // 未到期：跑出 DT 点半径就拉回边界（范围内可自由活动）
                            var soul = FindPlayer(kv.Key);
                            var cur = soul?.PublicInfo?.Pos;
                            if (soul == null || cur == null)
                                continue;

                            float r = LianAltarFeature.DtRadius?.Value ?? 350f;
                            float dx = cur.X - kv.Value.Pos.X, dy = cur.Y - kv.Value.Pos.Y;
                            float d2 = dx * dx + dy * dy;
                            if (d2 <= r * r)
                                continue;                   // 还在范围内：不干预

                            float d = (float)global::System.Math.Sqrt(d2);
                            if (d <= 0.001f)
                                continue;
                            float k = r / d;
                            soul.Move(new PosInfo
                            {
                                X = kv.Value.Pos.X + dx * k,
                                Y = kv.Value.Pos.Y + dy * k
                            }, force: true);
                            continue;
                        }
                        (done ?? (done = new List<int>())).Add(kv.Key);
                    }
                    if (done != null)
                    {
                        foreach (int pid in done)
                        {
                            SoulHolds.Remove(pid);
                            // 到期只是"不再限制范围"，人本来就是自由的 —— 不需要解锁任何东西
                        }
                    }
                }
            }
        }

        // ══ 跨局清理 ══
        [HarmonyPatch(typeof(GameRoom), "StartSurvive")]
        internal static class StartHook
        {
            [HarmonyPostfix]
            private static void Postfix() => Clear();
        }

        [HarmonyPatch(typeof(GameRoom), "StartLobby")]
        internal static class LobbyHook
        {
            [HarmonyPostfix]
            private static void Postfix() => Clear();
        }

        private static void Clear()
        {
            Progress.Clear();
            Host.Clear();
            Active.Clear();
            SoulHolds.Clear();
            PulledThisRound.Clear();
        }
    }
}
