using System.Collections.Generic;
using BepInEx.Configuration;
using HarmonyLib;
using HideAndSeek.Core;
using Protocol;
using Server.Game;
using UnityEngine;
using GameDeviceManager = Server.Game.DeviceManager;
using GamePlayer = Server.Game.Player;
using GameSummon = Server.Game.Summon;

namespace HideAndSeek.Features.Skill
{
    /// <summary>
    /// 小熊地图标记：凛（<c>Rin</c>，内部代号 Benjamin）放下的**小熊**，
    /// 把**落在它范围内的人**标到**她自己的地图**上 —— 与红毛的追踪同一套机制。
    ///
    /// 机制来源于源码（逐条核对过）：
    /// · 小熊是 <c>Server.Game.Summon</c>（ACS:163257，继承 <c>Device</c>），
    ///   由凛的 <c>Marionette</c> 技能放下：主人交互会 <c>ResetMarionette()</c> 收回（:163279）。
    ///   <c>DeviceManager.Instance.Summons</c>（:163713）可枚举全部小熊；
    ///   <c>DeviceInfo.StateList[1]</c> 是**主人的 PlayerId**（:163271）。
    /// · 原版"靠近小熊的提醒"在客户端 <c>Summon.DetectNearbyPlayer</c>（:7290）：
    ///     范围是**椭圆**（X 半径 448、Y 半径 410.6667，:7313-7315），**不是圆**；
    ///     并且要求**视线不被墙挡住**（<c>Physics2D.Raycast(..., 4096)</c>，:7318，4096 = Block 层）；
    ///     命中后放 <c>RuneSfx</c> 并给屏幕加描边（:7283 / :7287）。
    ///   本功能把同一套范围判定搬到**服务端**（房主权威），数值默认照原版，可配置。
    /// · 显示用红毛那条通道：<c>Player.SendTraceTarget</c>（:175923）以
    ///     <c>S_PIN_MOVE{ Type = 目标的真实 PlayerId, Pos = 目标位置 }</c> **单发给主人自己**
    ///     （<c>Session.Send</c>，:175932），离开范围时发 <c>Pos = (0,0)</c> 删除。
    ///   ⇒ 只有凛的地图上会出现这些点，其它人看不到。
    ///
    /// ⚠️ 只在 <c>Survive</c> 阶段发 <c>S_PIN_MOVE</c>：教学关里
    /// <c>GetSceneUI&lt;UI_GameScene&gt;()</c> 为 null、<c>Managers.Tablet.Tablet</c> 从未赋值，
    /// 客户端 <c>Handle_S_PIN_MOVE</c>（:42242-42248）会抛 NRE 且没有 try/catch
    /// （见 .tmps/场景箭头-调查.md §0.5）。
    /// </summary>
    [PatchFeature("BearMapMark",
        "小熊地图标记：凛放下的小熊，把范围内的人标到她自己的地图上（类似红毛的追踪；范围照原版靠近提醒的椭圆，可配置）。",
        defaultEnabled: false, side: FeatureSide.Host)]
    internal static class BearMapMarkFeature
    {
        /// <summary>原版 <c>Summon.DetectNearbyPlayer</c> 用的遮挡层（4096 = Block）。</summary>
        private const int BlockLayerMask = 4096;

        [ConfigField(448f,
            "范围半径 X（原版靠近提醒的数值：448）。范围是**椭圆**，不是圆。",
            Min = 50f, Max = 3000f)]
        public static ConfigEntry<float> RangeX;

        [ConfigField(410.6667f,
            "范围半径 Y（原版靠近提醒的数值：410.6667）。",
            Min = 50f, Max = 3000f)]
        public static ConfigEntry<float> RangeY;

        [ConfigField(true,
            "要求视线不被挡住（照原版：中间隔着墙就不算在范围内）。关掉则只看距离。")]
        public static ConfigEntry<bool> RequireLineOfSight;

        [ConfigField(0.5f,
            "标记位置的重发间隔（秒）。红毛是「每次移动推一次」，这里额外限流，避免高频移动时刷包。",
            Min = 0f, Max = 2f)]
        public static ConfigEntry<float> ResendInterval;

        /// <summary>主人 PlayerId → 已被标记的目标 PlayerId 集合（用于发删除哨兵）。</summary>
        private static readonly Dictionary<int, HashSet<int>> _marked = new Dictionary<int, HashSet<int>>();

        /// <summary>主人 PlayerId → 各目标上次发包时刻（限流用）。</summary>
        private static readonly Dictionary<int, Dictionary<int, float>> _lastSent = new Dictionary<int, Dictionary<int, float>>();

        private static bool Enabled()
            => !ModeRuntime.Bypass && RangeX != null && RangeY != null;

        private static float RangeXValue() => RangeX != null ? RangeX.Value : 448f;
        private static float RangeYValue() => RangeY != null ? RangeY.Value : 410.6667f;

        private static bool LosRequired() => RequireLineOfSight == null || RequireLineOfSight.Value;

        private static float Interval()
            => ResendInterval != null ? Mathf.Max(0f, ResendInterval.Value) : 0.5f;

        private static bool InSurvive()
            => GameRoom.Instance?.State == EGameState.Survive;

        // ───────────────────────── 判定 ─────────────────────────

        /// <summary>
        /// 该玩家是否**够格**被标记 —— 照抄原版 <c>DetectNearbyPlayer</c> 的过滤（ACS:7308）：
        /// 跳过自己、旁观、假人、幽灵，以及 Hide（藏进柜子/通风管）与 Sit 状态。
        /// </summary>
        private static bool IsEligible(GamePlayer p)
        {
            if (p?.PublicInfo == null) return false;
            if (p.IsSpectator || p.IsDummy) return false;
            if (p.PublicInfo.IsGhost) return false;
            if (p.State == EPlayerState.Hide || p.State == EPlayerState.Sit) return false;
            return true;
        }

        /// <summary>小熊的主人 PlayerId（<c>StateList[1]</c>，ACS:163271）。</summary>
        private static int OwnerOf(GameSummon s)
        {
            var list = s?.DeviceInfo?.StateList;
            if (list == null || list.Count < 2) return 0;
            return list[1];
        }

        /// <summary>小熊是否已激活（<c>StateList[0] == 1</c>，与客户端 <c>ShowRange</c> 的判据一致）。</summary>
        private static bool IsActive(GameSummon s)
        {
            var list = s?.DeviceInfo?.StateList;
            return list != null && list.Count > 0 && list[0] == 1;
        }

        private static Vector2 PositionOf(GameSummon s)
        {
            var pos = s?.DeviceData?.Pos;
            return pos == null ? Vector2.zero : new Vector2(pos.X, pos.Y);
        }

        private static Vector2 PositionOf(GamePlayer p)
        {
            var pos = p?.PublicInfo?.Pos;
            return pos == null ? Vector2.zero : new Vector2(pos.X, pos.Y);
        }

        /// <summary>椭圆范围判定 —— 照原版 ACS:7312-7315。</summary>
        private static bool InsideRange(Vector2 summonPos, Vector2 playerPos)
        {
            float rx = RangeXValue();
            float ry = RangeYValue();
            if (rx <= 0f || ry <= 0f) return false;

            float nx = (playerPos.x - summonPos.x) / rx;
            float ny = (playerPos.y - summonPos.y) / ry;
            return nx * nx + ny * ny <= 1f;
        }

        /// <summary>视线是否通畅 —— 照原版 ACS:7318（服务端本来就在用同一个 mask 做遮挡）。</summary>
        private static bool HasLineOfSight(Vector2 from, Vector2 to)
        {
            Vector2 dir = to - from;
            float mag = dir.magnitude;
            if (mag <= 0.01f) return true;                 // 重叠，视为可见

            return Physics2D.Raycast(from, dir, mag, BlockLayerMask).collider == null;
        }

        // ───────────────────────── 发包 ─────────────────────────

        private static GamePlayer FindPlayer(int playerId)
        {
            var players = GameRoom.Instance?.Players;
            if (players == null) return null;

            for (int i = 0; i < players.Count; i++)
            {
                if (players[i]?.PublicInfo?.PlayerId == playerId) return players[i];
            }
            return null;
        }

        /// <summary>
        /// 照红毛 <c>SendTraceTarget</c>（ACS:175923）单发给主人自己：
        /// 记住「谁」用真实 <c>PlayerId</c>（自定义 id 只能产生地图 pin、不跟随），
        /// 删除时发 <c>(0,0)</c>。
        /// </summary>
        private static void SendPin(GamePlayer owner, GamePlayer target, bool remove)
        {
            if (owner?.Session == null || target?.PublicInfo == null) return;

            try
            {
                owner.Session.Send(new S_PIN_MOVE
                {
                    Type = target.PublicInfo.PlayerId,
                    Pos = remove
                        ? new PosInfo { X = 0f, Y = 0f }
                        : target.EffectivePosition,
                    IsForce = false
                });
            }
            catch (global::System.Exception ex)
            {
                Plugin.Log.LogWarning($"[HS] BearMapMark：发 pin 失败 — {ex.Message}");
            }
        }

        // ───────────────────────── 标记账本 ─────────────────────────

        private static bool IsMarked(int ownerPid, int targetPid)
            => _marked.TryGetValue(ownerPid, out var set) && set.Contains(targetPid);

        private static void SetMarked(int ownerPid, int targetPid, bool marked)
        {
            if (!_marked.TryGetValue(ownerPid, out var set))
            {
                set = new HashSet<int>();
                _marked[ownerPid] = set;
            }

            if (marked) set.Add(targetPid);
            else if (set.Count == 0) _marked.Remove(ownerPid);
            else set.Remove(targetPid);
        }

        private static bool ResendAllowed(int ownerPid, int targetPid)
        {
            float interval = Interval();
            if (interval <= 0f) return true;

            if (!_lastSent.TryGetValue(ownerPid, out var map))
            {
                map = new Dictionary<int, float>();
                _lastSent[ownerPid] = map;
            }

            float now = Time.realtimeSinceStartup;
            if (map.TryGetValue(targetPid, out float last) && now - last < interval) return false;

            map[targetPid] = now;
            return true;
        }

        /// <summary>给某个主人清掉某个目标的标记（发删除哨兵）。</summary>
        private static void ClearMark(GamePlayer owner, int ownerPid, int targetPid)
        {
            SetMarked(ownerPid, targetPid, false);
            _lastSent.TryGetValue(ownerPid, out var map);
            map?.Remove(targetPid);

            if (owner != null)
            {
                var target = FindPlayer(targetPid);
                if (target != null) SendPin(owner, target, remove: true);
            }
        }

        /// <summary>清空全部标记（局结束 / 阶段切换）。原版在重开与死亡时会自行清箭头，
        /// 但**地图 pin 不会自己消失**（见 .tmps/场景箭头-调查.md §2），所以这里要发删除哨兵。</summary>
        private static void ClearAll()
        {
            foreach (var kv in _marked)
            {
                var owner = FindPlayer(kv.Key);
                if (owner == null) continue;

                foreach (int targetPid in kv.Value)
                {
                    var target = FindPlayer(targetPid);
                    if (target != null) SendPin(owner, target, remove: true);
                }
            }

            _marked.Clear();
            _lastSent.Clear();
        }

        // ───────────────────────── 主流程 ─────────────────────────

        /// <summary>对单个玩家重新评估他与**全部小熊**的关系（进入 / 离开 / 位置更新）。</summary>
        private static void Evaluate(GamePlayer target)
        {
            if (!Enabled() || !InSurvive()) return;
            if (!IsEligible(target)) return;

            var summons = GameDeviceManager.Instance?.Summons;
            if (summons == null) return;

            Vector2 targetPos = PositionOf(target);
            int targetPid = target.PublicInfo.PlayerId;

            for (int i = 0; i < summons.Count; i++)
            {
                var summon = summons[i];
                if (summon == null || !IsActive(summon)) continue;

                int ownerPid = OwnerOf(summon);
                if (ownerPid <= 0 || ownerPid == targetPid) continue;   // 不标记主人自己

                Vector2 summonPos = PositionOf(summon);
                bool inside = InsideRange(summonPos, targetPos);
                if (inside && LosRequired() && !HasLineOfSight(summonPos, targetPos)) inside = false;

                bool marked = IsMarked(ownerPid, targetPid);

                if (inside && !marked)
                {
                    var owner = FindPlayer(ownerPid);
                    if (owner == null) continue;

                    SetMarked(ownerPid, targetPid, true);
                    SendPin(owner, target, remove: false);
                    ResendAllowed(ownerPid, targetPid);
                    Plugin.Log.LogInfo($"[HS] BearMapMark：主人 #{ownerPid} 的小熊进入目标 #{targetPid}（{target.Name}），已标记。");
                }
                else if (!inside && marked)
                {
                    var owner = FindPlayer(ownerPid);
                    ClearMark(owner, ownerPid, targetPid);
                    if (owner != null)
                    {
                        Plugin.Log.LogInfo($"[HS] BearMapMark：目标 #{targetPid}（{target.Name}）离开主人 #{ownerPid} 的小熊范围，已取消标记。");
                    }
                }
                else if (inside && marked)
                {
                    // 位置更新（限流）：红毛是每次移动推一次，这里再加一道最小间隔
                    if (!ResendAllowed(ownerPid, targetPid)) continue;
                    var owner = FindPlayer(ownerPid);
                    if (owner != null) SendPin(owner, target, remove: false);
                }
            }
        }

        /// <summary>
        /// 兜底巡检：处理"没人移动但状态变了"的情况 ——
        /// 小熊刚被放下/收回、主人换人、玩家死亡，以及清理已失效的标记。
        /// </summary>
        private static void SweepAll()
        {
            if (!Enabled()) { if (_marked.Count > 0) ClearAll(); return; }
            if (!InSurvive()) { if (_marked.Count > 0) ClearAll(); return; }

            var players = GameRoom.Instance?.Players;
            if (players == null) return;

            for (int i = 0; i < players.Count; i++)
            {
                var p = players[i];
                if (p?.PublicInfo == null) continue;
                if (!p.IsAlive) continue;                 // 死者由下面的失效清理负责
                Evaluate(p);
            }

            // 清理失效：目标死了/走了/不再够格，或主人的小熊已经没了
            var owners = new List<int>(_marked.Keys);
            foreach (int ownerPid in owners)
            {
                if (!_marked.TryGetValue(ownerPid, out var set)) continue;

                var owner = FindPlayer(ownerPid);
                bool ownerHasBear = false;
                var summons = GameDeviceManager.Instance?.Summons;
                if (summons != null)
                {
                    for (int i = 0; i < summons.Count; i++)
                    {
                        var s = summons[i];
                        if (s != null && IsActive(s) && OwnerOf(s) == ownerPid) { ownerHasBear = true; break; }
                    }
                }

                var targets = new List<int>(set);
                foreach (int targetPid in targets)
                {
                    var target = FindPlayer(targetPid);
                    bool invalid = owner == null || !ownerHasBear || target == null || !target.IsAlive || !IsEligible(target);
                    if (invalid) ClearMark(owner, ownerPid, targetPid);
                }
            }
        }

        // ───────────────────────── 补丁 ─────────────────────────

        /// <summary>
        /// 玩家移动 ⇒ 立刻重评（红毛也是挂在 <c>Player.Move</c> 末尾，ACS:175914）。
        /// 这里只重评**移动的那个人**：他进出别人小熊的范围只与自己有关。
        /// </summary>
        [HarmonyPatch(typeof(GamePlayer), nameof(GamePlayer.Move), new[] { typeof(PosInfo), typeof(bool) })]
        internal static class MoveHook
        {
            [HarmonyPostfix]
            private static void Postfix(GamePlayer __instance)
            {
                Diagnostics.Hit("BearMapMark");
                Evaluate(__instance);
            }
        }

        /// <summary>兜底巡检（项目里已有两处用 <c>SurvivalTick</c> 做同类的每秒节奏）。</summary>
        [HarmonyPatch(typeof(GameRoom), "SurvivalTick")]
        internal static class SweepHook
        {
            [HarmonyPostfix]
            private static void Postfix() => SweepAll();
        }

        /// <summary>新局开始 / 回大厅 ⇒ 清账本并撤销 pin。</summary>
        [HarmonyPatch(typeof(GameRoom), "StartSurvive")]
        internal static class StartHook
        {
            [HarmonyPostfix]
            private static void Postfix() => ClearAll();
        }

        [HarmonyPatch(typeof(GameRoom), "StartLobby")]
        internal static class LobbyHook
        {
            [HarmonyPostfix]
            private static void Postfix() => ClearAll();
        }
    }
}
