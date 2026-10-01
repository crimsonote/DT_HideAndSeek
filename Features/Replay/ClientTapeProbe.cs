using System.Collections.Generic;
using System.Reflection;
using DummyClient;
using HarmonyLib;
using HideAndSeek.Core;
using Protocol;

namespace HideAndSeek.Features.Replay
{
    /// <summary>
    /// 【只读诊断】客户端侧探针 —— 查清"我们发的 `S_RECORD_REPLAY` 到底有没有落到客户端手里"。
    ///
    /// ★ 为什么这个功能破了本模块"只 patch 服务端类型"的规矩、而仍然成立：
    ///   那条规矩的理由有两条 ——（1）客户端补丁**只在房主进程生效**，房主看到的 ≠ 别人看到的；
    ///   （2）它会**掩盖服务端方案的不完善**。本功能两条都不触犯：
    ///   它**只写日志、不改任何游戏状态**；而它存在的目的恰恰是**暴露**问题。
    ///   所以它被单独放成一个功能（`ReplayProbe`），不混进 `ReplayFeature`，排查完直接关掉即可。
    ///
    /// 背景：连续多局出现"客户端磁带 0 帧 ⇒ 全部降级成服务端合成"，客户端自己报
    ///   `[Lobby] S_REQUEST_TAPE: no tape for RecordTime=N. Sending empty tape.`
    /// 而这句话有**三种**上游原因，光看服务端分不清：
    ///   A. 我们**没发** `S_RECORD_REPLAY`（`recorderId` 解析不到人 / `Session` 为空）；
    ///   B. 发了、客户端也收到了，但 `ReserveSaveTape` 的 **9 秒回调没跑成**；
    ///   C. 回调跑了，但 `RecordingTape` 在索取前被 **`Clear()` 清掉**。
    /// 下面三个钩子把 A→B→C 切成三段，逐段留痕 —— 哪一段断了，日志里一眼可见。
    ///
    /// ⚠ 客户端这两个类型都在**全局命名空间**（`PacketHandler` / `RecordManager`），
    ///   与服务端的 `Server.Game.HostPacketHandler` 不同名 ⇒ 用 `TypeByName` 不会误挂。
    /// </summary>
    [PatchFeature(
        section: "ReplayProbe",
        description: "【只读诊断】客户端侧探针：记录 S_RECORD_REPLAY 是否到达、快照是否登记、回传时有没有内容。" +
            "只写日志、不改任何游戏行为。用于定位「客户端为何回空带」；排查完可关掉。",
        defaultEnabled: true,
        side: FeatureSide.Client)]
    internal static class ClientTapeProbe
    {
        private static bool Armed => !ModeRuntime.Bypass && Diagnostics.IsLoaded("ReplayProbe");

        /// <summary>① 收到 `S_RECORD_REPLAY` ⇒ A 段成立（服务端确实发出来了、客户端确实收到了）。</summary>
        [HarmonyPatch]
        internal static class RecordReplayHook
        {
            [HarmonyTargetMethod]
            private static MethodBase TargetMethod()
                => AccessTools.Method(AccessTools.TypeByName("PacketHandler"), "Handle_S_RECORD_REPLAY");

            [HarmonyPostfix]
            private static void Postfix(Packet packet)
            {
                if (!Armed)
                    return;
                if (!(packet?.Pkt is S_RECORD_REPLAY pkt))
                    return;
                Plugin.Log.LogInfo($"[HS/客户端探针] ① 收到 S_RECORD_REPLAY key={pkt.RecordTime}"
                    + " ⇒ 服务端确实发到了（A 段排除）");
            }
        }

        /// <summary>② `ReserveSaveTape` ⇒ 客户端开始为这个 key 计时（9 秒后把当时的缓冲拍成快照）。</summary>
        [HarmonyPatch]
        internal static class ReserveHook
        {
            [HarmonyTargetMethod]
            private static MethodBase TargetMethod()
                => AccessTools.Method(AccessTools.TypeByName("RecordManager"), "ReserveSaveTape");

            [HarmonyPostfix]
            private static void Postfix(int killTime)
            {
                if (!Armed)
                    return;
                Plugin.Log.LogInfo($"[HS/客户端探针] ② ReserveSaveTape key={killTime}"
                    + " ⇒ 已登记，9 秒后应拍成快照");
            }
        }

        /// <summary>
        /// ③ `BuildUploadTape` ⇒ 结算索取时那个 key 到底有没有内容。
        ///
        /// 这一步是**判决点**：它返回 null/0 帧，就说明"快照没拍成（B）或被清掉了（C）"；
        /// 而若 ② 有、③ 却是空 ⇒ B/C 成立，且与 A（服务端没发）无关。
        /// </summary>
        [HarmonyPatch]
        internal static class UploadHook
        {
            [HarmonyTargetMethod]
            private static MethodBase TargetMethod()
                => AccessTools.Method(AccessTools.TypeByName("RecordManager"), "BuildUploadTape");

            [HarmonyPostfix]
            private static void Postfix(int killTime, List<SnapShot> __result)
            {
                if (!Armed)
                    return;
                int n = __result?.Count ?? -1;
                if (n > 0)
                {
                    Plugin.Log.LogInfo($"[HS/客户端探针] ③ BuildUploadTape key={killTime} ⇒ {n} 帧 ✓");
                    return;
                }
                Plugin.Log.LogWarning($"[HS/客户端探针] ③ BuildUploadTape key={killTime} ⇒ "
                    + (n < 0 ? "**null（快照不存在）**" : "**0 帧**")
                    + " —— 快照没拍成（B）或在索取前被 Clear 掉了（C）");
            }
        }
    }
}
