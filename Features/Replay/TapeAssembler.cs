using System;
using System.Collections.Generic;
using Protocol;

namespace HideAndSeek.Features.Replay
{
    /// <summary>
    /// 装配报告 —— 一幕**只打这一条**日志（规格 P4）。
    ///
    /// 现有实现一幕要打 5~10 条散落日志（"登记片段" / "锚点=" / "留 N 帧" / "主视角=" …），
    /// 出问题时要在几百行里把它们拼起来。这里把全部关键事实收进一个结构，由 <see cref="Line"/> 一次输出。
    /// </summary>
    internal sealed class AssemblyReport
    {
        public ActKind Kind;
        public int Key;
        public TapeOrigin Origin;
        public ReplayWindow.Span Window;
        /// <summary>首帧玩家 = **剪影落点**（客户端 `ChangeSilhouette` 只打给 `_blackId`，而首帧决定它）。</summary>
        public int HeadId;
        /// <summary>本幕主角（镜头要拍的人）。</summary>
        public int SubjectId;
        public int FramesIn;
        public int FramesOut;
        /// <summary>窗口内**来自素材的**帧数（不含首帧/出场帧/时间编辑）。为 0 ⇒ 这一段其实没有素材。</summary>
        public int RawInWindow;
        public int RosterAdded;
        public int ToxicDropped;
        /// <summary>排序前时间戳逆序的处数（&gt;0 说明排序在救场）。</summary>
        public int InvertedBefore;
        /// <summary>是否补了一枚 `NormalTimeEdit`（没有它，客户端会继承上一段遗留的 `TimeScale = 0.25`）。</summary>
        public bool TimeEditAdded;
        /// <summary>非空 = 装配失败的原因（调用方据此决定是否退回占位磁带）。</summary>
        public string Fail;

        public string Line()
        {
            if (!string.IsNullOrEmpty(Fail))
                return $"【{Kind}#{Key}】装配失败：{Fail}（主角=#{SubjectId} 窗口={Window}）";

            string origin = Origin switch
            {
                TapeOrigin.Client => "客户端磁带",
                TapeOrigin.HostSynth => "服务端合成",
                TapeOrigin.Placeholder => "占位磁带",
                _ => "无",
            };

            return $"【{Kind}#{Key}】{origin} 窗口={Window} {FramesIn}→{FramesOut} 帧"
                 + $"（全员出场帧 +{RosterAdded} 剔毒帧 -{ToxicDropped} 排序前逆序 {InvertedBefore} 处"
                 + (TimeEditAdded ? " 补时间编辑" : "") + $")"
                 + $" 主角=#{SubjectId} 首帧(剪影)=#{HeadId}";
        }
    }

    /// <summary>
    /// 【唯一的出带出口】—— 把"一串帧"变成**客户端能安全播放**的一段磁带。
    ///
    /// ★ 它**不区分素材来源**（用户确认的设计）。客户端回传的磁带、房主侧合成的帧、
    ///   占位磁带，进来都只是 `List&lt;SnapShot&gt;`；出去都要满足同一组硬约束。
    ///   "来源"只影响帧的**质量**（有没有设备帧、位置滞不滞后），不影响**正确性** ——
    ///   正确性要求是完全一样的（见 docs/回放-规格.md §1 与 §4·S4）。
    ///
    /// 这样做的直接收益：现有实现把同一组硬约束在两条路上各写了一遍
    /// （`TrimTape` 与 `ComposeHostTape`），而两条路的窗口基准还不一样（那个 6 秒偏移）。
    /// 统一之后，"合规"只有一份定义，"来源"退化成一个标注。
    ///
    /// 出厂保证（对应规格 §1）：
    ///   · 首帧是 `SpawnShot`（C1）
    ///   · 帧数 ≥ 2，否则返回 null（C1）
    ///   · 时间戳非递减，且首帧固定在 index 0（C2）
    ///   · index 1 是一枚 `NormalTimeEdit`（C7）—— `BeginTape` 会把 `_currentTime` 置成 `tape[1].TimeStamp`，
    ///     所以它必须在第一次 `Update` 就被执行，即时间戳 = 窗口起点
    ///   · 不含 `AddShot(Corpse)` 毒帧（C8）
    /// </summary>
    internal static class TapeAssembler
    {
        /// <summary>占位磁带用的"极大时间戳" —— `BeginTape` 会立刻把它当作"早就该放"而一闪而过。</summary>
        public const float PlaceholderTime = 1e6f;

        /// <summary>
        /// 装配一段磁带。
        /// </summary>
        /// <param name="act">这一幕（窗口 / 主角 / 剪影落点）。</param>
        /// <param name="frames">
        /// 素材帧（来源不限）。调用方保证：**不含首帧**、可含窗口外的帧（本方法自己过滤）。
        /// </param>
        /// <param name="head">
        /// 首帧的玩家信息 —— 调用方已把它的**位置改成主角在窗口起点的位置**（镜头必须落在现场）。
        /// 本方法只负责把它标成幽灵并钉在 index 0。
        /// </param>
        /// <param name="roster">
        /// "本幕会出现在画面里的人"，每人一份**窗口起点时的样子**。
        ///
        /// ★ "谁在画面里"由调用方用 <see cref="ActTable.VisibleIn"/> **从帧里读**（不做 AOI/房间近似）；
        ///   但**位置必须取自房主侧采样**，不能从磁带里那个人的 `SpawnShot` 取 ——
        ///   实测：录制者自己的帧是 `SurvivalTime` 基准（每轮第一枚），从磁带里取会拿到
        ///   **几秒前**的位置（某个真实样本里首帧位置 (3565,6665)、roster 帧却取到 (3368,5912)），
        ///   而客户端会先把角色放在 roster 帧的位置、再被后续 MoveShot 拉回来 ⇒ 抖动/瞬移。
        ///
        /// 本方法会为其中每个人补一枚出场帧（时间戳盖成窗口起点）——
        /// 每台客户端靠**自己那一枚**把自己 id 0 的替身装配好，缺了会让回放里
        /// "本机自己的换道具/换状态镜头"打在未装配的替身上 ⇒ 空引用 ⇒ 整段卡死。
        /// </param>
        /// <param name="report">装配报告（无论成败都会填好）。</param>
        /// <returns>合规磁带；失败返回 null（原因见 <see cref="AssemblyReport.Fail"/>）。</returns>
        internal static List<SnapShot> Assemble(
            Act act,
            List<SnapShot> frames,
            PublicPlayerInfo head,
            IReadOnlyList<PublicPlayerInfo> roster,
            out AssemblyReport report)
        {
            report = new AssemblyReport
            {
                Kind = act.Kind,
                Key = act.Key,
                Origin = act.Origin,
                Window = act.Window,
                SubjectId = act.SubjectId,
                FramesIn = frames?.Count ?? 0,
            };

            if (head == null)
            {
                report.Fail = "没有首帧玩家信息（剪影落点没解析出来）";
                return null;
            }
            if (!act.Window.IsValid)
            {
                report.Fail = "窗口非法（长度 ≤ 0）";
                return null;
            }

            float start = act.Window.From;

            // ── ① 首帧：剪影落点 ─────────────────────────────────────────
            // 客户端 `BeginTape` 用它做三件事：`_blackId`（⇒ 剪影打谁）、`ChangeMyPlayer`、`ApplySpawn`。
            // 标成幽灵是"让它不可见"的唯一服务端手段：回放期间 `Player.Update` 每帧刷
            // `RefreshGhostVisual()`，走 case 2 把幽灵替身与骨架**一起关掉**。
            var headInfo = head.Clone();
            headInfo.State = EPlayerState.Idle;   // 别让"躲柜 / 死亡幽灵"这类状态驱动这个看不见的角色
            headInfo.IsGhost = true;

            report.HeadId = headInfo.PlayerId;

            var result = new List<SnapShot>(1 + (roster?.Count ?? 0) + (frames?.Count ?? 0))
            {
                new SnapShot
                {
                    Type = ESnapShotType.SpawnShot,
                    TimeStamp = start,
                    Spawn = headInfo,
                },
            };

            // ── ② 全员出场帧 ─────────────────────────────────────────────
            // 为什么必须有（这是"防卡死"而不是"防穿帮"）：
            //   客户端靠每轮"我自己 + 全体"的出场帧把**每台机器自己的 0 号替身**装配好。
            //   我们重裁时会把原磁带那一轮裁掉，于是"本机自己的换道具/换状态镜头"可能打在
            //   还没装配的替身上（`BaseMaterial` 为 null）⇒ `EquipItem` 空引用 ⇒ 回放永久卡死。
            //   ⚠ 时间戳一律盖成窗口起点：`BeginTape` 把 `_playIndex` 置 1、`_currentTime` 置第 2 帧时间戳，
            //     所以它们会在**第一次 Update 连着执行完** ⇒ 先装配好，再开始演动作。
            var seen = new HashSet<int> { headInfo.PlayerId };
            if (roster != null)
            {
                foreach (var info in roster)
                {
                    if (info == null)
                        continue;
                    int id = info.PlayerId;
                    if (id <= 0 || !seen.Add(id))
                        continue;

                    var copy = info.Clone();
                    copy.State = EPlayerState.Idle;
                    result.Add(new SnapShot
                    {
                        Type = ESnapShotType.SpawnShot,
                        TimeStamp = start,
                        Spawn = copy,
                    });
                    report.RosterAdded++;
                }
            }

            // ── ③ 窗口过滤 + ④ 剔毒帧 ───────────────────────────────────
            int rawInWindow = 0;
            if (frames != null)
            {
                foreach (var s in frames)
                {
                    if (s == null)
                        continue;
                    // 左闭右开，与现有实现的窗口语义一致
                    if (s.TimeStamp < act.Window.From || s.TimeStamp >= act.Window.To)
                        continue;

                    // ⚠ 项圈自爆尸体的 `AddShot` —— 原版自身的"二次 SetInfo"缺陷，只能绕开。
                    //   客户端 `Corpse.SetInfo` 的 bomb 分支第一句就是
                    //   `GetComponentInChildren<SkeletonAnimation>().gameObject.SetActive(false)`，
                    //   而 Unity 这个 API **默认跳过未激活对象**：这具尸体已经被"加入"包处理过一次、
                    //   骨架已被它自己关掉 ⇒ 回放里再执行一次必然取到 null ⇒ 空引用。
                    //   而 `_playIndex++` 在 switch **之后** ⇒ 同一枚帧每帧重试 ⇒ **整段回放卡死**。
                    //   判据用**内容**（`DeviceInfo.StateList[3] != 0` 正是 `_isBombCorpse` 的来源）
                    //   而不是片段种类：露娜系白方可能**中途**自爆，那种帧会落进「杀人」窗口。
                    if (s.Type == ESnapShotType.AddShot && IsBombCorpseShot(s))
                    {
                        report.ToxicDropped++;
                        continue;
                    }

                    // ⚠ **不要在这里过滤"幽灵"的出场帧**。曾经试过：想让幽灵不显示昵称，
                    //   就把 `IsGhost` 的 `SpawnShot` 丢掉 ⇒ 他不被 spawn ⇒ 昵称确实没了。
                    //   但**代价是卡死风险**：磁带里可能带着指向他的
                    //   `EffectShot{FlashVfx/ScopeVfx/ArmbandVfx}`，而
                    //   `PlayFlashEffect` / `PlayScopeEffect` / `PlayArmbandEffect`
                    //   **无条件解引用** `GetPlayerCache(effect.DeviceId)`
                    //   （`GetPlayerCache` 找不到就返回 null，这三个都不做检查）⇒ NRE
                    //   ⇒ 而 `_playIndex++` 在 `Update` 的 switch **之后** ⇒ 同一帧每帧重试
                    //   ⇒ **整段回放永久卡住**。
                    //   ⇒ "全员出场帧"真正的用途就是保证 `EffectShot` 引用的 id 一定在 `_cache` 里。
                    //   昵称是观感问题、卡死是能不能玩的问题 —— 不值得换。（2026-10 核实）

                    // ★ **硬约束：绝不让 `PlayerId <= 0` 的出场帧进入磁带。**
                    //
                    //   id 0 在每台客户端上含义不同 —— 它是"**接收方自己的角色**"：
                    //   客户端会把 `PlayerId == _myPlayerId` 的帧**在本地改写**成 0
                    //   （`ApplySpawn` / `ApplyMove` / `ApplyState` 各有一句）。
                    //   所以服务端只能发**真实 id**，一份包让每台机器各自解释。
                    //
                    //   为什么必须挡：`ForceSpawnReplayTemp` 造的 0 号替身**只进 `_cache`**，
                    //   不在 `Players` 里；而 `ApplySpawn` 的守卫查的正是 `Players.ContainsKey`
                    //   ⇒ 收到 `PlayerId = 0` 的帧会**新建一个多余的 0 号玩家**
                    //   ⇒ 每台机器场上多出一个"自己模样的幽灵"，还可能被选成剪影
                    //   ⇒ 各机器看到的黑块都不同（不崩，但画面错）。
                    //
                    //   来源上本不该出现（`HostRecorder.NoteMove` 拒绝 id<=0；客户端录的是真实 id），
                    //   但**不能依赖上游永远正确** —— 这里是唯一的出口，硬约束要落在出口上。
                    //   （只需挡 `SpawnShot`：它是唯一会触发 `Spawn` 的类型；
                    //     `MoveShot{PlayerId=0}` 走 `HandleMove` 的 `TryGetValue` 会安全跳过。）
                    if (s.Type == ESnapShotType.SpawnShot && s.Spawn != null && s.Spawn.PlayerId <= 0)
                        continue;

                    result.Add(s);
                    rawInWindow++;
                }
            }
            report.RawInWindow = rawInWindow;

            // ★ 窗口里**一帧素材都没有** ⇒ 判失败，让调用方降级到服务端合成。
            //   为什么必须显式判：客户端磁带是"最近约 14 秒"的环形缓冲，而结算是整局结束那一刻
            //   才发生的 ⇒ **早期事件的窗口根本不在磁带里**。此时若照常返回"首帧 + 出场帧 + 时间编辑"
            //   这几帧（它们本身是合规的），调用方会以为这一段有素材 ⇒ 那一幕就变成空转（实测"吞幕"）。
            if (rawInWindow == 0)
            {
                report.Fail = $"窗口内没有素材帧（素材给了 {frames?.Count ?? 0} 帧，但都不在窗口内 —— "
                    + "客户端缓冲只保留最近约 14 秒，早期事件拿不到）";
                return null;
            }

            // ── ⑤ 排序（首帧固定）────────────────────────────────────────
            // 客户端 `Update` 是 `while (…) { if (shot.TimeStamp > _currentTime) break; … }`，
            // 一旦输出里出现"大值夹在小值中间"，它会**在那一帧 break、跳过后面所有帧**。
            // 而磁带本身混着两种时间基准（每轮第 1 帧是 `SurvivalTime` 基准、其余是 `ClientTime`，差几百），
            // 裁到窗口内就可能大小交错 —— 必须在这里排一次序。
            // 副作用也是有益的：把 `SurvivalTime` 基准的帧（数值很大）推到末尾，
            // 而 `_currentTime` 从窗口起点出发、根本涨不到那个值 ⇒ 它们被上面那个 break 自然丢弃。
            for (int i = 2; i < result.Count; i++)
            {
                if (result[i].TimeStamp < result[i - 1].TimeStamp)
                    report.InvertedBefore++;
            }

            if (result.Count > 2)
            {
                var headShot = result[0];

                // ★ 必须用**稳定排序**（`OrderBy`），不能用 `List<T>.Sort`（内省排序，不稳定）。
                //   原版磁带里**同一时间戳的帧顺序是有意义的**，不稳定排序会把它打乱。
                //   实测（"拿刀"幕，同一卷磁带的原始段）：
                //       [79] t=64.370  Armory StateList=[1, 0, 90, 44, …]            ← 刀还在
                //       [87] t=64.370  Armory StateList=[2, 0, 90, 91, 1, 465, …]    ← 刀被取走
                //   而排序后变成了"[取走] 在前、[还在] 在后" ⇒ 客户端最后执行的是**旧状态**
                //   ⇒ 用户看到的现象正是"拿刀幕结束时刀没被拿走"。
                var ordered = global::System.Linq.Enumerable.ToList(
                    global::System.Linq.Enumerable.OrderBy(
                        global::System.Linq.Enumerable.Skip(result, 1),
                        delegate (SnapShot s) { return s.TimeStamp; }));

                result.Clear();
                result.Add(headShot);
                result.AddRange(ordered);
            }

            // ── ⑥ 时间编辑（index 1）────────────────────────────────────
            // 必须在排序**之后**插，且时间戳 = 窗口起点：
            //   `ApplyEdit(NormalTimeEdit)` ⇒ `Managers.Game.TimeScale = 1f` + `Camera.main.DOOrthoSize(480f, 1f)`，
            //   而客户端放带子的推进是 `_currentTime += Managers.Game.DeltaTime`，
            //   `DeltaTime => TimeScale * Time.deltaTime`。
            //   ⇒ 没有它就会继承上一段遗留的 `TimeScale = 0.25` ⇒ 8 秒的带子要走 32 秒，
            //     而回放有总预算/超时 ⇒ 只有开头几帧被执行（实测的"哑剧"）。
            //   `BeginTape` 把 `_currentTime` 置成 `tape[1].TimeStamp`，所以放在 index 1 才会在第一次 Update 生效。
            //   （客户端磁带往往自带一枚，但可能在窗口末尾甚至被裁掉 ⇒ 一律补，幂等无害。）
            result.Insert(1, new SnapShot
            {
                Type = ESnapShotType.EditShot,
                TimeStamp = start,
                Edit = new EditSnapShot { Type = EEditShotType.NormalTimeEdit },
            });
            report.TimeEditAdded = true;

            report.FramesOut = result.Count;

            // ── 出厂自检 ────────────────────────────────────────────────
            // 这几个不变量是客户端硬要求，违反了表现为"静默跳过整段"或"整段卡死"，
            // 属于"出了问题完全看不出来"的那类 ⇒ 在这里就地抓，不要等实机。
            if (result.Count < 2)
            {
                report.Fail = $"装配后仅 {result.Count} 帧（客户端要求 ≥ 2）";
                return null;
            }
            if (result[0].Type != ESnapShotType.SpawnShot)
            {
                report.Fail = "首帧不是 SpawnShot";
                return null;
            }
            for (int i = 1; i < result.Count; i++)
            {
                if (result[i].TimeStamp < result[i - 1].TimeStamp)
                {
                    report.Fail = $"第 {i} 帧时间戳逆序（{result[i].TimeStamp:F3} < {result[i - 1].TimeStamp:F3}）";
                    return null;
                }
            }

            return result;
        }

        /// <summary>
        /// 造一段**占位磁带**（2 帧）—— 让客户端自己走一遍"这段没录到"的转场，
        /// 而不是让这一幕从回放里凭空消失。
        ///
        /// 两条约束决定了它的形状（都来自客户端 `RecordManager`）：
        ///   · `AddPlayTape` 对**空**磁带直接丢弃（日志 `빈 테이프 무시`）⇒ 0 帧 = 等于没发；
        ///   · `BeginTape` 要求 `Count >= 2` 且首帧是 `SpawnShot`，不满足会**静默跳过**（连转场都不播）。
        /// 满足要求时会正常"开始 → 立刻播完 → `FinishTape()`"⇒ 走一次转场演出。
        ///
        /// 时间戳取一个极大值：`BeginTape` 把 `_currentTime` 置成第 2 帧的时间戳，于是这 2 帧
        /// 立刻被执行完；`PrepareDevices(eventTime)` 拿它判断"临时设备是否在该时刻之后才生成"，
        /// 给大值 ⇒ 不额外藏任何设备（一闪而过，不露馅）。
        /// </summary>
        internal static List<SnapShot> Placeholder(int key, PublicPlayerInfo head)
        {
            if (head == null)
                return null;

            var info = head.Clone();
            info.State = EPlayerState.Idle;
            info.IsGhost = true;

            return new List<SnapShot>(2)
            {
                new SnapShot { Type = ESnapShotType.SpawnShot, TimeStamp = PlaceholderTime, Spawn = info },
                new SnapShot
                {
                    Type = ESnapShotType.EditShot,
                    TimeStamp = PlaceholderTime,
                    Edit = new EditSnapShot { Type = EEditShotType.NormalTimeEdit },
                },
            };
        }


        /// <summary>
        /// 这枚 `AddShot` 是不是「项圈自爆尸体」。
        /// 判据就是客户端 `Corpse.SetInfo` 里那句 `_isBombCorpse = info.StateList[3] != 0` ——
        /// 磁带里的 `DeviceInfo` 字节与客户端将要执行的完全一样，所以房主能提前判出来。
        ///
        /// 故意用 `var` 而不写类型名：本程序集里 `DeviceInfo` 有**两个同名类型**
        /// （服务端 `Protocol.DeviceInfo` 与客户端 `DeviceBase.Info` 那一套），写名字容易解析错。
        /// </summary>
        private static bool IsBombCorpseShot(SnapShot s)
        {
            var st = s?.Add?.Device?.StateList;
            return st != null && st.Count > 3 && st[3] != 0;
        }
    }
}
