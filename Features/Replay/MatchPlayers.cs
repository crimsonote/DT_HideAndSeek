using System.Collections.Generic;
using Server.Game;
using GamePlayer = Server.Game.Player;

namespace HideAndSeek.Features.Replay
{
    /// <summary>
    /// 【唯一的「本局参与者」名单】—— 每局开局冻结一次，整局只读。
    ///
    /// 为什么需要它（2026-10-11 实机故障）：
    ///   回放每一幕的「首帧剪影」id 会被客户端折成 `_blackId`，紧接着
    ///   `Managers.Player.ChangeMyPlayer(_blackId)` 用**裸字典索引**取人
    ///   （`Assembly-CSharp-0.1.17a.decompiled.cs:32946-32947` / `:32544`）。
    ///   那个人只要在「磁带装配完」到「这一幕开始播」之间从 `_cache` 里消失，就是
    ///   `KeyNotFoundException` ⇒ `BeginTape` 中止（日志停在 `세션 시작` 之后，
    ///   永远等不到 `tape i/N 시작`）⇒ `_isPlay` 恒 false、永不发 `C_COMPLETE_PACKET`
    ///   ⇒ 房主预算到点强推结算，而客户端还卡在录像画面 ⇒ 房间必须重开。
    ///
    /// 谁会让 `_cache` 少一个人？服务端**只有一条路**：真离开 ——
    ///   `HandleLeavePlayer` → `player.Clear()` + `Broadcast(S_LEAVE_GAME)`（`:173233-173238`）
    ///   ⇒ 客户端 `PlayerManager.Despawn(S_LEAVE_GAME)` → `_cache.Remove(id)`（`:32346-32361`）。
    ///
    /// 而**掉线不算**：局内参与者掉线走 `HandleGuestLinkDead`（`:173116`）→ 10 秒宽限
    ///   → `HandlePeerDisconnect` → `ConvertToDummy()`（`:173107` / `:177970-177982`），
    ///   **保留同一个 `PublicInfo.PlayerId`**、只广播 `S_PLAYER_DUMMY_CHANGED`，不动缓存；
    ///   重连时 `HandlePeerRejoin`（`:173170-173188`）按 steamId 认回那个 dummy 槽并
    ///   `RevertDummy()`（`:177993-178000`）⇒ **pid 与角色都不变**。
    ///
    /// ⇒ 真正危险的是「**不在本局名单上的人**」：中途加入的观战者，服务端给他们
    ///   `IsSpectator = true`（`:172844`，`flag2 = State != Lobby`），而 `IsSpectator`
    ///   的人在 `HandlePeerDisconnect` 里**连 10 秒宽限都没有**，立刻真离开
    ///   （`:173101-173103`）。他们进来又出去，正是最容易被选成剪影、又最容易在
    ///   播放前被删掉缓存的人群 —— 2026-10-11 那次就是一名中途加入的观战者
    ///   （`#7`，开播前 10 秒退房）被选成了第 1 幕的首帧剪影。
    ///
    /// 判据（与游戏自己的口径一致）：
    ///   在 `ChangeGameState(Survive)` 时取 `room.Players`，**只去掉 `IsSpectator`**（不判 `IsDummy`）。
    ///   这与原版 `StartPick` 里 `AlivePlayers.AddRange(Players)`（`:173317-173319`）同源 ——
    ///   大厅/选角阶段掉线是**真离开**（`:173118-173121` 的 `flag`），所以此处不该再有旁观者。
    ///   ⚠ 判据里**不出现任何测试设施**：假人不作特判（既不按它排除，也不按它包含）——
    ///     它本来就是一个占座、有 pid、会被广播的参与者，只按"是不是旁观者"区分。
    ///
    /// ⚠ 名单**只做过滤，不做候选来源**：候选永远来自现场遍历 `room.Players` /
    ///   `room.DeadPlayers`，所以名单里留下的旧 id 不可能被选中（人不在房里就遍历不到）。
    ///
    /// ⚠ 名单为空时**不过滤**（例如主机迁移：新主机没跑过这一局的 `ChangeGameState`）。
    ///   宁可退回旧行为，也不要因为名单缺失把每一幕都逼成"剪影 = 镜头宿主"。
    /// </summary>
    internal static class MatchPlayers
    {
        private static readonly HashSet<int> Ids = new HashSet<int>();
        private static bool _warnedEmpty;

        /// <summary>名单是否可用。不可用时调用方应**放行**（退回旧行为），不要过滤。</summary>
        internal static bool Available => Ids.Count > 0;

        internal static int Count => Ids.Count;

        internal static bool Contains(int id) => id > 0 && Ids.Contains(id);

        /// <summary>
        /// 冻结本局名单。由 <see cref="ReplayFeature"/> 的每局重置调用
        /// （`GameRoom.ChangeGameState(Survive)`，已在 `RoundStartHook` 上挂好 —— 不需要新钩子）。
        /// </summary>
        internal static void Capture(GameRoom room)
        {
            Ids.Clear();
            _warnedEmpty = false;
            if (room == null)
                return;

            int skipped = 0;
            foreach (GamePlayer p in room.Players)
            {
                if (p?.PublicInfo == null)
                    continue;
                if (p.IsSpectator)
                {
                    skipped++;      // 旁观者（中途加入 / 中途重连当观战的人）
                    continue;
                }
                Ids.Add(p.PublicInfo.PlayerId);
            }

            if (Ids.Count == 0)
            {
                Plugin.Log.LogWarning("[HS-Replay] 本局参与者名单为空 —— 剪影候选池不做过滤（退回旧行为）。");
                return;
            }

            Plugin.Log.LogInfo($"[HS-Replay] 本局参与者名单已冻结：{Ids.Count} 人"
                + (skipped > 0 ? $"（另有 {skipped} 个旁观者不计入）" : "。"));
        }

        /// <summary>
        /// 回放开始时的诊断：房间里有哪些人**不在**这份名单上（= 会被剪影候选池排除的人）。
        /// 没有这一行就分不清「本来没有旁观者」和「过滤生效了」。
        /// </summary>
        internal static void LogExcluded(GameRoom room)
        {
            if (!Available)
            {
                if (!_warnedEmpty)
                {
                    _warnedEmpty = true;
                    Plugin.Log.LogWarning("[HS-Replay] 本局参与者名单为空，剪影候选池不做过滤。"
                        + "常见原因：主机在局中接管（新主机没跑过 ChangeGameState(Survive)）。");
                }
                return;
            }

            if (room == null)
                return;

            var outside = new List<int>();
            Collect(room.Players, outside);
            Collect(room.DeadPlayers, outside);

            if (outside.Count == 0)
            {
                Plugin.Log.LogInfo("[HS-Replay] 剪影候选池：房间里没有名单外的人。");
                return;
            }

            var head = new List<string>(outside.Count);
            foreach (int id in outside)
                head.Add("#" + id);

            Plugin.Log.LogInfo($"[HS-Replay] 剪影候选池排除名单外的人 {outside.Count} 个："
                + string.Join(",", head.ToArray()) + "（旁观者等，不作为剪影）。");
        }

        private static void Collect(List<GamePlayer> src, List<int> dst)
        {
            if (src == null)
                return;
            foreach (GamePlayer p in src)
            {
                int id = p?.PublicInfo?.PlayerId ?? 0;
                if (id > 0 && !Ids.Contains(id) && !dst.Contains(id))
                    dst.Add(id);
            }
        }
    }
}
