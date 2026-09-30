using System;
using System.Collections.Generic;
using Protocol;
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
    ///   所以"镜头要拍的人"和"被涂黑的人"**必须是两个不同的人**，而这一点由首帧一个字段承担。
    ///
    ///   旧实现把这个决策写在**两个地方**（`FindGhostStandIn` 与 `ResolveObserverId`），
    ///   判据还不一样 ⇒ 同一件事有两套规则。这里合并成一处。
    ///
    /// 候选优先级（每条都必须保证"他不会被观众看见"）：
    ///   ① **已死 + 仍在房间 + 客户端缓存里还有他** —— 最可靠：服务端 `MakeSpectatorGhost` 已把他
    ///      标成 `IsGhost = true`，客户端回放期间 `RefreshGhostVisual()` 走 case 2 把幽灵替身与骨架
    ///      **一起关掉** ⇒ 他不可见。
    ///   ② **本幕 roster 之外的人** —— 不会被 `ApplySpawn` 装配到画面里。
    ///   ③ 都不行 ⇒ **当事人自己**（接受他被涂黑）。
    ///
    /// ⚠ **不能返回 0**：id 0 是"本机用户"的特殊值（客户端会把 `PlayerId == _myPlayerId` 的帧改写成 0，
    ///   而 0 号替身是 `ForceNewReplayTemp` 造的半成品），每台机器含义不同、互相冲突。
    ///
    /// ⚠ **已知限制**：客户端的 `NameTag` **不受服务端控制**（`PublicPlayerInfo` 只有 8 个字段，
    ///   没有名字/显示开关）。旧实现用一个只对房主本机生效的客户端补丁把它关掉 —— 那是掩盖，
    ///   已删除。现在能做的只是**挑离主角最远的人**，让他的昵称尽量落在镜头之外。
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
        /// <param name="rosterIds">本幕"会出现在画面里的人"的 id（含主角）。</param>
        /// <param name="windowStart">窗口起点 —— 用来取各人当时的位置（算"离镜头多远"）。</param>
        public static Result Resolve(int subjectId, ICollection<int> rosterIds, float windowStart)
        {
            if (subjectId <= 0)
                return new Result { Id = 0, Why = "主角无效" };

            try
            {
                var room = GameRoom.Instance;
                if (room == null)
                    return Fallback(subjectId, "房间不存在");

                // ① 已死 + 仍在房间（分成"在本幕画面里"与"不在"两档，各自取离主角最远的）
                var inScene = new List<int>();
                var offScene = new List<int>();
                foreach (var p in room.DeadPlayers)
                {
                    int id = p?.PublicInfo?.PlayerId ?? 0;
                    if (id <= 0 || id == subjectId)
                        continue;
                    // 本机客户端缓存里还有他 ⇒ 其他客户端的 `_cache` 里也有
                    //（"加入"包发过、没发过"离开"包）⇒ `ChangeMyPlayer` 的直接索引不会抛。
                    if (Managers.Player.GetPlayerCache(id) == null)
                        continue;
                    if (rosterIds != null && rosterIds.Contains(id))
                        inScene.Add(id);
                    else
                        offScene.Add(id);
                }

                int pick = Farthest(inScene, subjectId, windowStart);
                if (pick > 0)
                    return new Result { Id = pick, Why = "已死·在房间·本幕出场（取离主角最远）" };

                pick = Farthest(offScene, subjectId, windowStart);
                if (pick > 0)
                    return new Result { Id = pick, Why = "已死·在房间（取离主角最远）" };

                // ② 本幕 roster 之外的人 —— 不会被 ApplySpawn 装配到画面里
                var outside = new List<int>();
                foreach (var p in room.Players)
                {
                    int id = p?.PublicInfo?.PlayerId ?? 0;
                    if (id <= 0 || id == subjectId)
                        continue;
                    if (rosterIds != null && rosterIds.Contains(id))
                        continue;                       // 他在本幕画面里 ⇒ 剪影落上去会多一个黑块
                    if (Managers.Player.GetPlayerCache(id) == null)
                        continue;
                    outside.Add(id);
                }
                pick = Farthest(outside, subjectId, windowStart);
                if (pick > 0)
                    return new Result { Id = pick, Why = "本幕不在场（取离主角最远）" };
            }
            catch (Exception ex)
            {
                return Fallback(subjectId, $"挑剪影失败（{ex.Message}）");
            }

            // ③ 所有人都要在场 ⇒ 只能当事人自己
            return Fallback(subjectId, "所有人都要在场");
        }

        /// <summary>在候选里挑"离主角最远"的那个（昵称越不容易被看到）。取不到位置时退用第一个。</summary>
        private static int Farthest(List<int> ids, int subjectId, float t)
        {
            if (ids == null || ids.Count == 0)
                return 0;
            if (ids.Count == 1)
                return ids[0];

            var subject = HostRecorder.At(subjectId, t)?.Pos;
            if (subject == null)
                return ids[0];

            int best = 0;
            float bestD = -1f;
            foreach (int id in ids)
            {
                var pos = HostRecorder.At(id, t)?.Pos;
                float d = pos == null
                    ? float.MaxValue
                    : (pos.X - subject.X) * (pos.X - subject.X) + (pos.Y - subject.Y) * (pos.Y - subject.Y);
                if (d > bestD)
                {
                    bestD = d;
                    best = id;
                }
            }
            return best;
        }

        private static Result Fallback(int subjectId, string why)
            => new Result { Id = subjectId, Why = why + " ⇒ 退回当事人自己", FellBackToSubject = true };
    }
}