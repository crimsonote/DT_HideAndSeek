using System;
using BepInEx.Configuration;
using HarmonyLib;
using Protocol;
using Server.Game;
using HideAndSeek.Core;
using GameDeviceManager = Server.Game.DeviceManager;
using GameRoom = Server.Game.GameRoom;

namespace HideAndSeek.Features.Rule
{
    /// <summary>
    /// 「电力系统自检」的两个**结果动作**（流程状态机在 <c>CommandFeature</c> 里，本类只管落地效果）。
    ///
    /// 结果一 —— **电力系统正常** ⇒ **电箱下线 N 秒**（默认 45）：
    ///   口径（房主给的）："如果已经派发电箱，则让电箱消失 45 秒无法破坏；如果电箱还没派发，则额外延后 45 秒"。
    ///   两者共用一条路径：
    ///     · 已派发（`Fusebox.DeviceInfo.MissionType == -1`）⇒ 先 `ClearFuseboxSabotage()` 立刻收回
    ///       （地图标记消失 ⇒ 期间无法破坏）；
    ///     · 然后**排一次归还预约**（<see cref="ArmRestore"/>）—— N 秒后重新派发。
    ///   ⇒ "下线"由收回实现，"恢复"由预约实现；**不依赖原版那条只在电缆事件时才会排上的 60 秒重派发**。
    ///
    /// 结果二 —— **电力中断** ⇒ **修好所有电箱**：直接复用 <see cref="PowerRepairFeature"/> 的
    ///   既有实现（走原版 `Fusebox.ConnetCable` 路径，不手工复刻它的副作用）。
    /// </summary>
    [PatchFeature(
        section: "PowerSelfTest",
        description: "电力系统自检命令的结果落地：电力正常时让电箱下线一段时间；电力中断时修好所有电箱。",
        defaultEnabled: true,
        side: FeatureSide.Host)]
    internal static class PowerSelfTestFeature
    {
        [ConfigField(45f, "「电力系统正常」时电箱下线/派发延后的秒数。", Min = 0f, Max = 300f)]
        public static ConfigEntry<float> BlackoutSeconds;

        [ConfigField(10f, "自检耗时下限（秒）。真正的等待时长在 [下限, 上限] 之间随机。", Min = 1f, Max = 120f)]
        public static ConfigEntry<float> ActivateMinSec;

        [ConfigField(24f, "自检耗时上限（秒）。", Min = 1f, Max = 300f)]
        public static ConfigEntry<float> ActivateMaxSec;

        [ConfigField(20f, "等待授权码输入的秒数，超时即「操作已过期」。", Min = 5f, Max = 300f)]
        public static ConfigEntry<float> CodeTimeoutSec;

        [ConfigField(5f, "「正在验证操作者权限」的演出时长（秒）。", Min = 0f, Max = 60f)]
        public static ConfigEntry<float> VerifySec;

        [ConfigField(180f, "命令冷却（秒）。**全房共享**。", Min = 0f, Max = 3600f)]
        public static ConfigEntry<float> Cooldown;

        [ConfigField(true,
            "地缘约束：游戏里能敲命令的只有四个通讯台所在的位置，" +
            "所以**整个自检流程不可变更操作位置** —— 授权码必须回到发起时的那个房间提交，" +
            "换了位置则拒绝（『你需要在 X 进行操作，不可变更操作位置』）。" +
            "异区玩家敲命令会被告知『正在 X 房间由 Y 操作』。" +
            "关 = 不检查位置，他人一律得到『已被占用』。")]
        public static ConfigEntry<bool> RequireSameArea;

        /// <summary>
        /// **归还预约** —— "电箱下线 N 秒"唯一的状态。
        ///
        /// ★ 为什么是**句柄**而不是一个绝对时刻（2026-10 改掉的关键）：
        ///   旧实现存 `_suppressUntil = Now() + N`（绝对时刻），判据是"比时间"，于是
        ///     · 与 `ClientTime`（受 `TimeScale` 缩放的**本机**时钟）绑死；
        ///     · 窗口内收到派发请求时用"真实毫秒重排"，**两个时钟不同源 ⇒ 可能永不收敛**
        ///       （实测症状："预期 45 秒后恢复，实际永久不恢复"）；
        ///     · 换局必须显式清，漏了就把新局前几分钟全抑制掉。
        ///   ⇒ 现在"何时归还"交给**原版自己的通道** `TimeManager.PushSurvivalJob` ——
        ///     与原版 `RefreshLight` 里那句 `PushSurvivalJob(60, StartFuseboxSabotage)`（:173511）
        ///     同源，基准是 `SurviveTime`（全网同步、每局重置为 420）。
        /// </summary>
        private static JobElem _restore;

        /// <summary>
        /// 排下 <see cref="ArmRestore"/> 那一刻的 `SurviveTime` —— 用途只有**换局自愈**。
        ///
        /// 为什么需要第二个判据：`ResetSurvival()` 会把 `SurviveTime` 重设为 420，
        /// 队列里的陈旧预约被原版 `ClearSurvivalJob()` 清掉时**不会** `Kill()` 元素
        /// （`JobElem.IsValid()` 仍为真）⇒ 单看 `IsValid()` 无法察觉。
        /// 而 `SurviveTime` 局内单调增、换局归 420 ⇒ `SurviveTime >= _armedAt` 必然转假。
        /// ⚠ 方向必须是"失败即放行"：转假只会让派发恢复正常，绝不会把派发永久吃掉。
        /// ⚠ **不要**改成 `SurviveTime < _restore.execTick` 之类 —— 陈旧 `execTick` 恒大于
        ///   新局的 `SurviveTime`，那会让陈旧句柄显得**更**"pending"（方向反了）。
        /// </summary>
        private static int _armedAt;

        /// <summary>
        /// **全房共享冷却**的截止时刻（房主时钟）。
        ///
        /// 为什么不让命令引擎管：引擎的 `Cooldown`/`RoomCooldown` 都在**命令返回时**记账，
        /// 而本命令返回只代表流程**开始** —— 后面还有"等授权码"和"等自检"两段，
        /// 中途可能被拒/超时/算错而根本没产生效果。房主的口径是
        /// "**一次有效流程完成后**才进 CD"，所以起点必须由 <see cref="StartCooldown"/> 手动打。
        ///
        /// ⚠ **不能靠"换局时 `ClientTime` 归零"来失效** —— 那是反的：
        ///   这两个字段存的是**绝对时刻**（`Now() + 秒数`），而归零意味着
        ///   上一局的 `T0 + 180` 在新局里要等 `ClientTime` 涨过 180 才作废。
        ///   实测：上一局 T0=100 ⇒ `_roomCdUntil=280`，新局跑到 60 时读出
        ///   `280 − 60 = 220` 秒冷却（配置只有 180）—— 房主看到的正是这个。
        ///   ⇒ 必须由 <see cref="ResetRoundState"/> 在换局时显式清掉。
        /// </summary>
        private static float _roomCdUntil = -1f;

        /// <summary>
        /// **换局清理**：把"绝对时刻"形态的残留状态归零。
        ///
        /// 不清会怎样（实测过的症状）：
        ///   · `_roomCdUntil` 残留 ⇒ 新局 `/maint` 报出**比配置值更大**的冷却
        ///     （上一局 T0 越大、残留越久，因为 `ClientTime` 归零后要从 0 重新爬）；
        ///
        /// ⚠ 抑制那半边**已经不需要在这里清了**：它从"绝对时刻 `_suppressUntil`"改成了
        ///   "归还预约 `_restore`"，而后者靠 `SurviveTime >= _armedAt` 换局自愈
        ///   （`SurviveTime` 换局归 420 ⇒ 判据自动转假，且方向是失败即放行）。
        /// </summary>
        public static void ResetRoundState()
        {
            _roomCdUntil = -1f;
        }

        /// <summary>剩余冷却秒数；返回 0 表示不在冷却中。</summary>
        public static float RemainingCooldown()
        {
            float left = _roomCdUntil - Now();
            return left > 0f ? left : 0f;
        }

        /// <summary>流程**真正完成**（效果已落地）时才开始计时。失败/中止的路径不该调用它。</summary>
        public static void StartCooldown()
        {
            float cd = Cooldown?.Value ?? 180f;
            if (cd > 0f)
                _roomCdUntil = Now() + cd;
        }

        internal static bool Armed => !ModeRuntime.Bypass && Diagnostics.IsLoaded("PowerSelfTest");

        private static float Now()
        {
            try { return Managers.Game.ClientTime; }
            catch { return 0f; }
        }

        /// <summary>
        /// **排一次归还**：N 秒后（`SurviveTime` 轴）重新派发电箱。
        ///
        /// ★ 这是"下线 N 秒"里**解除下线**的唯一来源。旧实现只设了一个抑制窗口，却假设
        ///   "原版会在断电归零后自己重派发"—— 而那个重派发在
        ///   `RefreshLight` 的 `case 0`（`:173511` 的 `PushSurvivalJob(60, StartFuseboxSabotage)`），
        ///   而 `RefreshLight` **只有两个调用点**：`Fusebox.ConnetCable`(:162756) 与
        ///   `Fusebox.DisconnetCable`(:162775) ⇒ 它由**电缆事件**驱动，不是定时器。
        ///   自检这条路上没有任何电缆事件 ⇒ 那个假设不成立 ⇒ 电箱本局再也不回来
        ///   （实测："预期推迟 45 秒，实际永久推迟"）。
        /// </summary>
        private static void ArmRestore(float sec)
        {
            var tm = TimeManager.Instance;
            if (tm == null)
                return;

            _restore?.Kill();                       // 幂等：重复自检只保留最后一次预约
            _armedAt = tm.SurviveTime;
            _restore = tm.PushSurvivalJob((int)Math.Ceiling(sec), Restore);
        }

        /// <summary>
        /// 预约到点：重新派发电箱。照抄原版对 `_jobElem` 的既有写法（`:168336-168339`）——
        /// `Kill()` 旧的、`PushSurvivalJob` 拿新句柄、回调里把字段置空。
        /// </summary>
        private static void Restore()
        {
            _restore = null;
            try
            {
                if (GameRoom.Instance?.State != EGameState.Survive)
                    return;                         // 已换局/已进审判 ⇒ 不在这一局里补派发
                GameDeviceManager.Instance?.StartFuseboxSabotage();
            }
            catch (global::System.Exception ex)
            {
                Plugin.Log.LogWarning($"[HS] 电力自检：归还电箱失败 — {ex.Message}");
            }
        }

        /// <summary>是否已有一次归还预约在排队（见 <see cref="_restore"/> / <see cref="_armedAt"/>）。</summary>
        private static bool ReturnPending()
        {
            var tm = TimeManager.Instance;
            return _restore != null && _restore.IsValid()
                && tm != null && tm.SurviveTime >= _armedAt;
        }

        /// <summary>
        /// 结果：**电力系统正常** ⇒ 电箱下线 N 秒。
        /// 返回"期间是否收回了已派发的电箱"（供命令端选文案，两套文案房主都给了）。
        /// </summary>
        public static bool ApplyNormal()
        {
            float sec = BlackoutSeconds?.Value ?? 45f;

            if (sec <= 0f)
                return false;                       // 配成 0 = 不生效

            bool dispatched = false;
            try
            {
                var dm = GameDeviceManager.Instance;
                if (dm?.Fuseboxes != null)
                {
                    foreach (var fb in dm.Fuseboxes)
                    {
                        // `MissionType == -1` 是原版 `Fusebox.StartMission` 打上的"已派发"标记
                        // （`ClearSabotage` 只在它等于 -1 时才清理）—— 这是判断"地图上有没有
                        // 待破坏目标"的唯一可靠依据，不要用箭头/任务列表去猜。
                        if (fb?.DeviceInfo != null && fb.DeviceInfo.MissionType == -1)
                        {
                            dispatched = true;
                            break;
                        }
                    }

                    if (dispatched)
                        dm.ClearFuseboxSabotage();
                }
            }
            catch (global::System.Exception ex)
            {
                Plugin.Log.LogWarning($"[HS] 电力自检：收回电箱失败 — {ex.Message}");
            }

            ArmRestore(sec);
            Plugin.Log.LogInfo($"[HS] 电力自检：电箱下线 {sec:F0} 秒（已派发={dispatched}）——"
                + $"归还已按原版 `PushSurvivalJob` 排定。");
            return dispatched;
        }

        /// <summary>结果：**电力中断** ⇒ 修好所有电箱（复用既有实现）。</summary>
        public static int ApplyRestore()
        {
            int broken = 0;
            try { broken = GameDeviceManager.Instance?.GetDisconnectFuseCount() ?? 0; }
            catch { }

            PowerRepairFeature.ForceRepairAll();
            Plugin.Log.LogInfo($"[HS] 电力自检：已恢复供电（原有 {broken} 个断电电箱）。");
            return broken;
        }

        /// <summary>
        /// 归还预约在排队时，其它派发请求一律**作废**。
        ///
        /// 保留这个钩子是为了挡住一个仍然存在的情形：**自检之前就已经排好的原版派发**
        /// （某人修好电缆 ⇒ `RefreshLight` 的 `case 0` 排了 60 秒任务）落在下线窗口内
        /// ⇒ 电箱会提前回来。作废它即可，因为"重派发"这个语义已由
        /// <see cref="ArmRestore"/> 的预约接管。
        ///
        /// ★ 为什么是"作废"而不是旧实现的"顺延到窗口末尾"：
        ///   · 归还已经由预约负责 ⇒ 再顺延一次会造成同一时刻**两次**派发；
        ///   · 而"丢弃"本来并不可怕 —— 旧注释担心的"永久吃掉"恰恰是**旧实现自己的 bug**：
        ///     它把归还的责任外包给原版，而原版那条路只在电缆事件时才会被排上。
        /// </summary>
        [HarmonyPatch(typeof(GameDeviceManager), "StartFuseboxSabotage")]
        internal static class SuppressStartHook
        {
            [HarmonyPrefix]
            private static bool Prefix(GameDeviceManager __instance)
            {
                if (!Armed || __instance == null)
                    return true;

                if (!ReturnPending())
                    return true;                    // 没有待归还的预约 ⇒ 正常派发

                Plugin.Log.LogInfo("[HS] 电力自检：已有一次归还预约在排队，本次派发作废。");
                return false;
            }
        }
    }
}
