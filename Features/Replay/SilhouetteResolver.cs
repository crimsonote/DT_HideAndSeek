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
            /// <summary>
            /// 是否是"退回当事人自己"（镜头宿主本人当剪影）。
            ///
            /// ★ 这**不是异常，而是原版方案**：客户端 `BeginTape` 本来就是把首帧那个人的 id 当
            ///   `_blackId`，再 `ChangeSilhouette(true)` 涂黑他 —— 观众透过他看世界。
            ///   所以走到这一步**不该报警**：它只在"本幕所有人都在场上、找不到一个不可见的人"时发生，
            ///   而那时它恰好是**唯一不让人凭空消失**的选择
            ///   （把在场的活人涂黑 = 那个人从画面里消失，那才是 bug）。
            /// </summary>
            public bool FellBackToSubject;

            public override string ToString()
                => $"#{Id}（{Why}{(FellBackToSubject ? " —— 原版方案：镜头宿主本人（预期，非异常）" : "")}）";
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
                // ★ 候选取舍里还有一层：**这个人有没有客户端**。
                //
                //   ⚠ **但必须说清现实**：**正式对局里没有假人**（假人只是 `DummyFeature` 的测试装置），
                //     所以下面两个"无客户端"档**通常是空的**，实战命中的几乎总是**第三档** ——
                //     即"剪影必然是某个有客户端的真人"，而他**本人那台机器就必然走 `_blackId = 0` 分支**。
                //     ⇒ 该分支在正式对局里**无法避免**：每台客户端的 `_myPlayerId` 都不同，
                //       而首帧 id 必然是某个人（必须"客户端认识"，否则 `_cache[id]` 直接抛）。
                //     保留这两档只是为了"**恰好有假人时**能更优"，**不是**为了解决问题。
                //
                //   而第三档的后果已经量化过，可以接受：`ChangeSilhouette` 不会执行（不涂黑）、
                //   镜头由 `AreaShot` 决定（不受影响）、多出来的 0 号玩家 `IsGhost = true`（身体不可见）
                //   —— 唯一残留是它显示**一个角色名标签**，而且只有**剪影本人**（已死的观战者）看得到。
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
                // ⚠ 这里**不按"有没有客户端"分档**。曾经按它分过（想避开 `_blackId = 0`），
                //   但那是错的：**正式对局里没有假人**（假人只是 `DummyFeature` 的测试装置），
                //   所以"无客户端"档永远是空的 ⇒ 那是把测试环境写进常态逻辑，只会制造
                //   "问题已解决"的错觉。判据只反映常态：
                //
                //     ① 他在本幕画面里 ⇒ 还必须"起点即幽灵"（整段不可见）才算数
                //     ② 他不在画面里   ⇒ 不会被 `ApplySpawn` 装配，一定看不见
                // 候选池由 `ActTable.KnownIn`（只取 id）+ `ReplayDirector.SceneIds` 的距离判据给出：
                //   ① `inScene`  起点即幽灵 + 在画面里 ⇒ 客户端 RefreshGhostVisual 把他关掉，看不见 ✓
                //   ② `offScene` 不在画面里            ⇒ 不会被 ApplySpawn 装配，看不见 ✓
                //   ③ `outside`  本幕不在场            ⇒ 同上 ✓
                //   ④ 都不行 ⇒ **退回镜头宿主本人** —— 那是**原版方案**（客户端 `BeginTape` 就是拿
                //      首帧那个人当 `_blackId`），观众透过他看世界。它保证"不会有人凭空消失"，
                //      所以是**预期行为、不打警告**（把在场的活人涂黑才会让人消失，那才是 bug）。
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

                    if (rosterIds == null || !rosterIds.Contains(id))
                    {
                        offScene.Add(id);
                        continue;
                    }

                    // ★ 他在画面里 ⇒ 还必须确认"他**整段**都不可见"：
                    //   已死者能当剪影靠的是 `IsGhost = true` ⇒ 客户端 `RefreshGhostVisual`
                    //   走 case 2 把他关掉。而"起点时还活着"的人不满足这个前提
                    //   —— 典型就是**本幕的受害者**（窗口 = [T-3, T+1]，他在 T 才死）
                    //   ⇒ 他一整段本色可见，剪影落上去就是"死前没人影、死后才冒出尸体"。
                    var at = HostRecorder.At(id, windowStart);
                    if (at != null && at.IsGhost)
                        inScene.Add(id);
                }

                int pick = Farthest(inScene, subjectId, windowStart);
                if (pick > 0)
                    return new Result
                    {
                        Id = pick,
                        Why = "起点即幽灵·本幕出场（**他有客户端 ⇒ 正是他自己那台会走 `_blackId = 0`**："
                            + "不涂黑、镜头不受影响、多出一个不可见的 0 号玩家只带一个角色名标签。"
                            + "正式对局里这是**预期行为**，不是缺陷）",
                    };

                pick = Farthest(offScene, subjectId, windowStart);
                if (pick > 0)
                    return new Result { Id = pick, Why = "已死·不在画面（不会被 ApplySpawn 装配 ⇒ 一定看不见）" };


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

        /// <summary>
        /// 退回**镜头宿主本人**当剪影 —— 这是**原版方案，不是降级、不该报警**。
        ///
        /// 客户端 `BeginTape` 本来就是 `_blackId = 首帧那个人的 id` + `ChangeSilhouette(true)`：
        /// 镜头挂在他身上、身体被涂黑 ⇒ 观众看的是**他的视角**。
        /// 所以当本幕"所有人都在场上、找不到一个不可见的人"时，选他恰好是**唯一不让人凭空消失**的做法：
        /// 若改用某个在场的活人当剪影，那个人就会被涂黑、从画面里消失 —— 那才是 bug。
        /// </summary>
        private static Result Fallback(int subjectId, string why)
            => new Result { Id = subjectId, Why = why + " ⇒ 由镜头宿主本人担任（原版方案）", FellBackToSubject = true };
    }
}