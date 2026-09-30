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
    /// ★ **统一的判据只有一条：他在本幕「整段」都不可见。** 具体分两类：
    ///
    ///   ① **不在画面名单里**（不会被 `ApplySpawn` 装配）⇒ 一定看不见 ⇒ 可直接用。
    ///   ② **在画面名单里的已死者** ⇒ 只有当**窗口起点就已经是幽灵**（`IsGhost = true`）才算数：
    ///      客户端回放期间 `RefreshGhostVisual()` 走 case 2 把幽灵替身与骨架一起关掉 ⇒ 看不见。
    ///      ⚠ 反例正是**本幕的受害者**：窗口 `[T-3, T+1]` 里他在 `T` 才死 ⇒ 起点时还活着
    ///      ⇒ 一整段本色可见 ⇒ 拿他当剪影就变成"死前没人影、死后才冒出尸体"。
    ///      （实测：三幕里恰好只有"受害者被选中"的那一幕坏，另外两幕正常。）
    ///   ③ 都不行 ⇒ **当事人自己**（接受他被涂黑）。
    ///
    /// ⚠ 这里**没有**"事件参与者黑名单"之类的特例 —— 受害者是被上面这条**通用规则**排除的
    ///   （他不满足"整段不可见"）。特例会随场景繁殖，规则不会。
    ///
    /// ⚠ **不能返回 0**：id 0 是"本机用户"的特殊值（客户端会把 `PlayerId == _myPlayerId` 的帧改写成 0，
    ///   而 0 号替身是 `ForceSpawnReplayTemp` 造的半成品），每台机器含义不同、互相冲突。
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

            bool Banned(int id) => id == subjectId;

            try
            {
                var room = GameRoom.Instance;
                if (room == null)
                    return Fallback(subjectId, "房间不存在");

                // ① 已死 + 仍在房间（分成"在本幕画面里"与"不在"两档，各自取离主角最远的）
                // ★ 候选取舍里还有一层**比"在不在画面里"更硬**的判据：
                //   **这个人有没有客户端**。
                //
                //   首帧的 id 会成为每台客户端的 `_blackId`：
                //       _blackId = (tape[0].Spawn.PlayerId != _myPlayerId) ? tape[0].Spawn.PlayerId : 0;
                //   所以**若剪影槽位正好是某台机器的"自己"，那台机器就会拿到 `_blackId = 0`**。
                //   那个分支不会崩（`ChangeSilhouette` 找的是 `Players[0]`，而 0 号替身只在 `_cache`
                //   里 ⇒ 整句不执行），但会留下两个残留：
                //     ① `ApplySpawn(首帧)` 因 `Players.ContainsKey(0)` 为假而 `Spawn(0)`
                //        ⇒ 场上多出一个 0 号玩家；
                //     ② 那个玩家的 `IsGhost` 虽然为 true（身体不可见），**昵称却会显示**。
                //
                //   ⇒ 而"没有客户端的人（假人）"**不可能**成为任何一台机器的 `_myPlayerId`
                //     ⇒ 剪影落在他身上，**所有机器都走正常路径**。
                //   所以把"无客户端"排在最前，它比"离主角远"重要得多。
                var noClientInScene = new List<int>();
                var noClientOffScene = new List<int>();
                var inScene = new List<int>();
                var offScene = new List<int>();

                foreach (var p in room.DeadPlayers)
                {
                    int id = p?.PublicInfo?.PlayerId ?? 0;
                    if (id <= 0 || Banned(id))
                        continue;
                    // 本机客户端缓存里还有他 ⇒ 其他客户端的 `_cache` 里也有
                    //（"加入"包发过、没发过"离开"包）⇒ `ChangeMyPlayer` 的直接索引不会抛。
                    if (Managers.Player.GetPlayerCache(id) == null)
                        continue;

                    bool noClient = p.Session == null;

                    if (rosterIds == null || !rosterIds.Contains(id))
                    {
                        // 他不会出现在画面里（没被 ApplySpawn 装配）⇒ 一定看不见
                        (noClient ? noClientOffScene : offScene).Add(id);
                        continue;
                    }

                    // ★ 他在画面里 ⇒ 还必须确认"他**整段**都不可见"：
                    //   已死者能当剪影靠的是 `IsGhost = true` ⇒ 客户端 `RefreshGhostVisual`
                    //   走 case 2 把他关掉。而"起点时还活着"的人不满足这个前提
                    //   —— 典型就是**本幕的受害者**（窗口 = [T-3, T+1]，他在 T 才死）
                    //   ⇒ 他一整段本色可见，剪影落上去就是"死前没人影、死后才冒出尸体"。
                    var at = HostRecorder.At(id, windowStart);
                    if (at != null && at.IsGhost)
                        (noClient ? noClientInScene : inScene).Add(id);
                }

                int pick = Farthest(noClientInScene, subjectId, windowStart);
                if (pick > 0)
                    return new Result { Id = pick, Why = "起点即幽灵·本幕出场·**无客户端**（取离主角最远）" };

                pick = Farthest(noClientOffScene, subjectId, windowStart);
                if (pick > 0)
                    return new Result { Id = pick, Why = "已死·不在画面·**无客户端**（取离主角最远）" };

                pick = Farthest(inScene, subjectId, windowStart);
                if (pick > 0)
                    return new Result { Id = pick, Why = "起点即幽灵·本幕出场（他有客户端，若正是他自己则该机走 _blackId=0 分支）" };

                pick = Farthest(offScene, subjectId, windowStart);
                if (pick > 0)
                    return new Result { Id = pick, Why = "已死·不在画面（同上，可能有客户端）" };

                // ② 本幕 roster 之外的人 —— 不会被 ApplySpawn 装配到画面里
                var outside = new List<int>();
                foreach (var p in room.Players)
                {
                    int id = p?.PublicInfo?.PlayerId ?? 0;
                    if (id <= 0 || Banned(id))
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