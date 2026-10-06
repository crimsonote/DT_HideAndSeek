using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using Protocol;
using Server.Game;
// MissionData / MissionDataLoader 在 namespace Data（不是 Server.Game，也不是全局）——
// 见 Assembly-CSharp 反编译 :179694 起。缺这个 using 会编译不过，而不是静默解析错。
using Data;
using GamePlayer = Server.Game.Player;

namespace HideAndSeek.Features.Rule
{
    /// <summary>
    /// 任务系统（<c>Server.Game.MissionManager</c>）的**唯一**反射入口。
    ///
    /// 它在本程序集外是 internal，且 Harmony 无法把字符串类型名解析到 Assembly-CSharp
    /// （会误落到 0Harmony 上），所以只能反射。集中收口的意义：字段/方法名一旦与游戏版本
    /// 对不上，只需改这一个文件 —— 此前 WhiteWinFeature 与 WhiteCommandFeature 各写了一份。
    ///
    /// 句柄解析失败即熔断（<see cref="_failed"/>），避免每次调用都抛异常刷日志。
    /// </summary>
    internal static class MissionBridge
    {
        private static bool _failed;
        private static global::System.Type _type;

        private static PropertyInfo _instance;
        private static PropertyInfo _currentPoint;
        private static PropertyInfo _goalPoint;
        private static PropertyInfo _progressList;
        private static PropertyInfo _escapeWeight;
        private static PropertyInfo _timeWeight;
        private static PropertyInfo _remainCount;
        private static MethodInfo _clearMission;
        private static MethodInfo _currentPointSetter;

        /// <summary>反射是否已熔断（供命令层打诊断日志，区分"真为 0"与"读不到"）。</summary>
        internal static bool Failed => _failed;

        private static bool Ensure()
        {
            if (_failed)
                return false;
            if (_instance != null)
                return true;

            _type = AccessTools.TypeByName("Server.Game.MissionManager");
            if (_type == null)
            {
                _failed = true;
                Plugin.Log.LogWarning("[HS] 找不到 Server.Game.MissionManager，任务系统相关功能停用。");
                return false;
            }

            _instance = AccessTools.Property(_type, "Instance");
            _currentPoint = AccessTools.Property(_type, "CurrentPoint");
            _goalPoint = AccessTools.Property(_type, "GoalPoint");
            _progressList = AccessTools.Property(_type, "ProgressMissionList");
            _escapeWeight = AccessTools.Property(_type, "EscapeGaugeWeight");
            _timeWeight = AccessTools.Property(_type, "TimeLimitIncreaseWeight");
            _remainCount = AccessTools.Property(_type, "PublicRemainPlayerCount");
            _clearMission = AccessTools.Method(_type, "ClearMission",
                new[] { typeof(ESchoolMission), typeof(GamePlayer), typeof(bool) });
            // CurrentPoint 是 public 属性但 setter 私有 —— PropertyInfo.SetValue 依赖运行时
            // 对非公开访问器的处理，这里直接取 setter 的 MethodInfo 自己 Invoke，行为确定。
            _currentPointSetter = AccessTools.PropertySetter(_type, "CurrentPoint");

            // ⚠ 必须把 CurrentPoint / GoalPoint 也纳入熔断判据：
            //   少了它们时 ReadFloat 会**静默**返回 fallback（0f）⇒ 表现正好是
            //   "命令说任务进度为 0、而界面显示有进度"（房主 2026-10-05 实报，出现在首局）。
            if (_instance == null || _clearMission == null || _progressList == null
                || _currentPoint == null || _goalPoint == null)
            {
                _failed = true;
                Plugin.Log.LogWarning(
                    "[HS] MissionManager 反射不完整（Instance / ClearMission / ProgressMissionList / CurrentPoint / GoalPoint 有缺失），任务系统相关功能停用。");
                return false;
            }
            return true;
        }

        private static object Instance()
        {
            // 必须先 Ensure()：_instance 只在那里赋值。
            // 少了这一步，任何"先读 CurrentPoint/GoalPoint 再碰其它成员"的调用序列
            // （例如本局第一条命令就是 /sta 或 /rep）都会在这里对 null 取属性 → NRE →
            // Fail() 把 _failed 永久置真 → 之后连 Ensure() 都不再跑，任务系统整块失效。
            if (!Ensure())
                return null;

            try
            {
                return _instance.GetValue(null);
            }
            catch (global::System.Exception ex)
            {
                Fail("Instance", ex);
                return null;
            }
        }

        private static void Fail(string member, global::System.Exception ex)
        {
            _failed = true;
            Plugin.Log.LogWarning($"[HS] MissionBridge 读取 {member} 失败，已熔断 — {ex.Message}");
        }

        private static float ReadFloat(PropertyInfo prop, string member, float fallback)
        {
            object inst = Instance();
            if (inst == null || prop == null)
                return fallback;
            try
            {
                object value = prop.GetValue(inst);
                return value == null ? fallback : global::System.Convert.ToSingle(value);
            }
            catch (global::System.Exception ex)
            {
                Fail(member, ex);
                return fallback;
            }
        }

        /// <summary>
        /// <c>ProgressMissionList</c> 的**活引用**（非副本），调用方可直接增删。
        /// 该 List 由 MissionManager 长期持有，运行期只会被 Clear/Add/Remove，不会被整体替换。
        /// </summary>
        internal static List<MissionData> ProgressList
        {
            get
            {
                if (!Ensure())
                    return null;

                object inst = Instance();
                if (inst == null)
                    return null;
                try
                {
                    return _progressList.GetValue(inst) as List<MissionData>;
                }
                catch (global::System.Exception ex)
                {
                    Fail("ProgressMissionList", ex);
                    return null;
                }
            }
        }

        internal static float CurrentPoint => ReadFloat(_currentPoint, "CurrentPoint", 0f);

        /// <summary>
        /// 读 <c>CurrentPoint</c>，**把"读不到"与"读到 0"彻底分开**。
        ///
        /// 为什么必须有它（房主 2026-10-06 实报：界面上还有进度，命令却说"任务进度为 0"）：
        /// <see cref="ReadFloat"/> 在拿不到实例/属性时**静默返回 fallback 0f**，
        /// 而调用方无法区分"读不到"与"真的是 0" ⇒ 把读取失败也报成"进度为 0"。
        /// 返回 false 时调用方应当报"读不到任务进度"，而不是"进度为 0"。
        /// </summary>
        internal static bool TryCurrentPoint(out float value)
            => TryReadFloat(_currentPoint, "CurrentPoint", out value);

        /// <summary>
        /// 读 <c>GoalPoint</c>（同上：区分"读不到"与"为 0"）。
        /// 它还有一个独立用途 —— `MissionAllocator`(:166613) 负责给它赋值
        /// （`GoalPoint = 存活人数 × 15`），所以 **`GoalPoint &lt;= 0` 就是"本局任务系统尚未就绪"**
        /// 的确定性判据（首局在 `GameStart()` 之前读到的正是 0）。
        /// </summary>
        internal static bool TryGoalPoint(out float value)
            => TryReadFloat(_goalPoint, "GoalPoint", out value);

        private static bool TryReadFloat(PropertyInfo prop, string member, out float value)
        {
            value = 0f;

            object inst = Instance();
            if (inst == null || prop == null)
                return false;                 // 读不到 —— 调用方必须区别对待，不能再当成 0

            try
            {
                object raw = prop.GetValue(inst);
                if (raw == null)
                    return false;
                value = global::System.Convert.ToSingle(raw);
                return true;
            }
            catch (global::System.Exception ex)
            {
                Fail(member, ex);
                return false;
            }
        }

        /// <summary>
        /// 直接改写 CurrentPoint（原版 setter 是 private）。
        /// 走 setter 的 MethodInfo 而不是 PropertyInfo.SetValue —— 行为与访问器可见性解耦。
        /// </summary>
        internal static bool SetCurrentPoint(float value)
        {
            if (!Ensure())
                return false;

            object inst = Instance();
            if (inst == null || _currentPointSetter == null)
                return false;

            try
            {
                _currentPointSetter.Invoke(inst, new object[] { value });
                return true;
            }
            catch (global::System.Exception ex)
            {
                Fail("CurrentPoint(set)", ex);
                return false;
            }
        }

        internal static float GoalPoint => ReadFloat(_goalPoint, "GoalPoint", 0f);

        internal static float EscapeGaugeWeight => ReadFloat(_escapeWeight, "EscapeGaugeWeight", 1f);

        internal static float TimeLimitIncreaseWeight => ReadFloat(_timeWeight, "TimeLimitIncreaseWeight", 1f);

        internal static int RemainPlayerCount
        {
            get
            {
                int n = (int)ReadFloat(_remainCount, "PublicRemainPlayerCount", 1f);
                return n < 1 ? 1 : n;
            }
        }

        /// <summary>
        /// 调原版 <c>ClearMission(ESchoolMission, Player, bool)</c>。
        /// 反射不可用或调用抛异常时返回 false，并把原因写进 <paramref name="error"/>。
        /// </summary>
        internal static bool Clear(ESchoolMission mission, GamePlayer completer, bool isInfected, out string error)
        {
            error = null;
            if (!Ensure())
            {
                error = "任务系统不可用";
                return false;
            }

            object inst = Instance();
            if (inst == null)
            {
                error = "拿不到 MissionManager.Instance";
                return false;
            }

            try
            {
                _clearMission.Invoke(inst, new object[] { mission, completer, isInfected });
                return true;
            }
            catch (global::System.Exception ex)
            {
                error = ex.InnerException?.Message ?? ex.Message;
                Plugin.Log.LogWarning($"[HS] MissionBridge.ClearMission({mission}) 抛出 — {error}");
                return false;
            }
        }

        /// <summary>
        /// 以一次「人工任务」的方式结算：临时合成一个 MissionData 插进 ProgressMissionList 表头，
        /// 调原版 ClearMission，再兜底移除。
        ///
        /// 机制：原版 ClearMission(:166690) 第二行要求目标 MissionData **已经**在
        /// ProgressMissionList 里（`FirstOrDefault(x => x.Type == (int)mission)`），
        /// 找到后立刻 Remove，之后才广播 S_MISSION_STATE / S_MISSION_CLEAR。
        /// 因此插进去就能让原版完整走完它自己的结算（进度 / 加时 / 扣时房规 / 成功音效 /
        /// 累计分 / AllClear 判定 / "派下一个任务"），调用方一行公式都不需要。
        ///
        /// 合成实例不会外泄：原版第二行就把它移除，而所有广播都在那之后。何况客户端可见的
        /// 任务列表与地图图钉只由**设备的 MissionType** 驱动
        /// （DeviceBase.SetMissionInfo → MapManager.AddMissionPin → UI_GameTablet.AddMissionPin），
        /// S_MISSION_STATE.ProgressMissionTypes 在客户端只进 MissionMirror，且仅供房主迁移恢复。
        ///
        /// 为什么插在 0 号位：结算弹窗要显示中文任务名，而客户端文本取自本地化表、Host 无法自定义，
        /// 所以只能借用某个真实任务类型。若表里已有一个同 Type 的真实任务在跑，
        /// FirstOrDefault 会先命中它 —— 那等于白送玩家完成一个真任务；插到表头可保证先命中合成实例。
        ///
        /// finally 里的移除是必需的：原版首行 `if (CurrentPoint >= GoalPoint) return;` 会在进度已满时
        /// 提前返回，那时合成实例还留在表里，不清掉会被当成一个在跑的任务
        /// （IsChainInProgress 与房主迁移恢复都会读到它）。
        /// </summary>
        internal static bool CompleteSynthetic(int missionType, int point, GamePlayer completer, out string error)
        {
            error = null;

            var list = ProgressList;
            if (list == null)
            {
                error = "任务系统不可用";
                return false;
            }

            // 原版 ClearMission 的首行是 `if (CurrentPoint >= GoalPoint) return;` —— 它不抛异常，
            // 反射 Invoke 会正常返回，于是调用方以为成功、回执"刷新完成"，而实际什么都没发生
            // （冷却却已经写掉了）。这里按同一条件先判一次，让"没做成"能被如实报告。
            float goal = GoalPoint;
            if (goal > 0f && CurrentPoint >= goal)
            {
                error = "任务进度已满";
                return false;
            }

            var synthetic = new MissionData
            {
                Type = missionType,
                ShouldEnqueue = false,   // 不进 WaitMissionQueue —— 它只是这一次结算的凭据
                Point = point,           // 原版公式直接读这个字段，因此奖励完全由调用方决定
                PrevType = 0,            // 不属于任何任务链
                NextType = 0             // 完成后按原版走 StartNextFromQueue()
            };

            list.Insert(0, synthetic);
            try
            {
                return Clear((ESchoolMission)missionType, completer, false, out error);
            }
            finally
            {
                list.Remove(synthetic);  // 原版正常路径已移除；提前 return / 抛异常时在这里兜底
            }
        }
    }
}
