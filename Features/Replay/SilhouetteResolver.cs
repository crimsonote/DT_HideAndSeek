using System;
using System.Collections.Generic;
using Server.Game;
using GamePlayer = Server.Game.Player;

namespace HideAndSeek.Features.Replay
{
    /// <summary>
    /// 【唯一的剪影决策】—— 决定"磁带首帧给谁"，也就是客户端把黑剪影打在谁身上。
    ///
    /// 为什么需要它（docs/回放-规格.md §5）：
    ///   首帧的 id 同时决定三件事 —— 客户端 `BeginTape` 把它折成 `_blackId`，
    ///   然后 `ChangeMyPlayer(_blackId)`（镜头给谁）、`ChangeSilhouette(true)`（剪影打谁）。
    ///   所以"镜头要拍的人"和"被涂黑的人"**必须是两个不同的人**，这一点由首帧的 id 一个字段承担。
    ///
    ///   现有实现把这个决策写在**两个地方**（`FindGhostStandIn` 与 `ResolveObserverId`），
    ///   判据还不一样 ⇒ 同一件事有两套规则。这里合并成一处。
    ///
    /// 候选优先级（每条都必须保证"他不会被观众看见"）：
    ///   ① **已死 + 仍在房间 + 客户端缓存里还有他** —— 最可靠：
    ///      服务端 `MakeSpectatorGhost` 已经把他标成 `IsGhost = true`，
    ///      而客户端回放期间 `RefreshGhostVisual()` 走 case 2 把幽灵替身与骨架**一起关掉** ⇒ 他不可见。
    ///   ② **本幕 roster 之外的人** —— "不会被 `ApplySpawn` 装配到画面里"（roster 只含窗内活动过的人 + 主角）。
    ///   ③ 都不行 ⇒ **当事人自己**（接受他被涂黑）。
    ///
    /// ⚠ **不能返回 0**：id 0 是"本机用户"的特殊值 —— 客户端会把 `PlayerId == _myPlayerId`
    ///   的帧改写成 0，而 0 号替身是 `ForceSpawnReplayTemp` 造的半成品。
    ///   它在每台机器上含义不同，互相冲突（用户口径）。
    /// </summary>
    internal static class SilhouetteResolver
    {
        internal struct Result
        {
            /// <summary>剪影槽位的 id；一定会 &gt; 0。</summary>
            public int Id;
            /// <summary>为什么挑了他（写进日志）。</summary>
            public string Why;
            /// <summary>是否是"退回当事人自己"（他会变成黑块，属于降级）。</summary>
            public bool FellBackToSubject;

            public override string ToString()
                => $"#{Id}（{Why}{(FellBackToSubject ? " ⚠降级" : "")}）";
        }

        /// <summary>
        /// 挑一个剪影槽位。
        /// </summary>
        /// <param name="subjectId">本幕主角（镜头要拍的人）—— 他**不能**被选中。</param>
        /// <param name="rosterIds">本幕"会出现在画面里的人"（含主角）。</param>
        public static Result Resolve(int subjectId, ICollection<int> rosterIds)
        {
            if (subjectId <= 0)
                return new Result { Id = 0, Why = "主角无效" };

            try
            {
                var room = GameRoom.Instance;
                if (room == null)
                    return Fallback(subjectId, "房间不存在");

                // ① 已死 + 仍在房间（优先"在 roster 里"的 —— 他在客户端那边是已知的幽灵）
                int looseDead = 0;
                foreach (var p in room.DeadPlayers)
                {
                    int id = p?.PublicInfo?.PlayerId ?? 0;
                    if (id <= 0 || id == subjectId)
                        continue;
                    // 本机客户端缓存里还有他 ⇒ 其他客户端的 `_cache` 里也有（"加入"包发过、
                    // 没发过"离开"包）⇒ `ChangeMyPlayer` 的 `_cache[id]` 直接索引不会抛。
                    if (Managers.Player.GetPlayerCache(id) == null)
                        continue;

                    if (rosterIds != null && rosterIds.Contains(id))
                        return new Result { Id = id, Why = "已死·在房间·本幕出场" };
                    if (looseDead == 0)
                        looseDead = id;
                }
                if (looseDead > 0)
                    return new Result { Id = looseDead, Why = "已死·在房间" };

                // ② 本幕 roster 之外的人 —— 不会被 ApplySpawn 装配到画面里
                foreach (var p in room.Players)
                {
                    int id = p?.PublicInfo?.PlayerId ?? 0;
                    if (id <= 0 || id == subjectId)
                        continue;
                    if (rosterIds != null && rosterIds.Contains(id))
                        continue;                       // 他在本幕画面里 ⇒ 剪影落在他身上会多一个黑块
                    if (Managers.Player.GetPlayerCache(id) == null)
                        continue;

                    return new Result { Id = id, Why = "本幕不在场" };
                }
            }
            catch (Exception ex)
            {
                return Fallback(subjectId, $"挑剪影失败（{ex.Message}）");
            }

            // ③ 所有人都要在场 ⇒ 只能当事人自己
            return Fallback(subjectId, "所有人都要在场");
        }

        private static Result Fallback(int subjectId, string why)
            => new Result { Id = subjectId, Why = why + " ⇒ 退回当事人自己", FellBackToSubject = true };
    }
}
