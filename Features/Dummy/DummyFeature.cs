using System.Collections.Generic;
using System.Linq;
using BepInEx.Configuration;
using HarmonyLib;
using Server.Game;
using HideAndSeek.Core;

namespace HideAndSeek.Features.Dummy
{
    /// <summary>
    /// 假人玩家（测试用）：在单人房里造出可被刀死的白方靶子，
    /// 让房主自己拿刀当黑方跑完对抗流程（AOI 裁剪、露娜免疫、胜负判定等）。
    ///
    /// 设计要点：
    ///   - 假人只是"花名册里的普通白方玩家"，没有 AI、不会移动，也不需要真实网络端点
    ///     （HostPeerSession(null) 的 Send 静默丢弃）。
    ///   - 生成时机必须在 GameStart 之前：原版 GameStart（:170080）会对 Players 里每个玩家
    ///     分配 StartPosList 出生点，晚于它加入就没有出生点。
    ///     因此自动生成挂在 GameRoom.StartPick 的 Prefix 上。
    ///   - 手动操作走 hs_dummy 命令（在大厅或选角阶段执行）。
    ///
    /// 默认关闭：它只用于测试，正式玩法不该带着一群假人。
    /// </summary>
    [PatchFeature(
        section: "Dummy",
        description: "假人玩家（测试用）：在单人房造出可被刀死的白方靶子，供房主自己当黑方验证对抗流程。",
        defaultEnabled: false,
        side: FeatureSide.Host)]
    internal static class DummyFeature
    {
        [ConfigField(0, "进入选角阶段时自动生成的假人数量（0 = 不自动生成，改用 hs_dummy 命令）。", Min = 0f, Max = 8f)]
        public static ConfigEntry<int> AutoSpawnCount;

        [ConfigField("假人", "假人名字前缀，实际名字 = 前缀 + 座位号。")]
        public static ConfigEntry<string> NamePrefix;

        [ConfigField("", "默认角色 ID 列表（英文逗号分隔，按假人顺序循环取用）。留空则每个假人随机。")]
        public static ConfigEntry<string> DefaultCharacters;

        [ConfigField(false, "拦截假人引发的对官方服务器的上报（战绩 / 加星 / 成就）。开启前请确认已在测试环境。")]
        public static ConfigEntry<bool> BlockOfficialRequests;

        /// <summary>解析后的默认角色列表（供 DummyManager 循环取用）。</summary>
        internal static List<int> ConfiguredCharacters
        {
            get
            {
                var raw = DefaultCharacters?.Value;
                if (string.IsNullOrWhiteSpace(raw))
                    return new List<int>();

                return raw
                    // 注意：本程序集存在 HideAndSeek.Features.System 命名空间，
                    // 直接写 System.Xxx 会被优先解析到它，必须加 global:: 限定
                    .Split(new[] { ',', '，', ' ' }, global::System.StringSplitOptions.RemoveEmptyEntries)
                    .Select(s => int.TryParse(s.Trim(), out int v) ? v : 0)
                    .Where(v => v > 0)
                    .ToList();
            }
        }

        // ── 自动生成：选角开始前入场，确保 GameStart 会给它们分出生点 ──────────
        // StartPick 是 private，nameof 取不到，用字符串定位。
        [HarmonyPatch(typeof(GameRoom), "StartPick")]
        internal static class StartPickHook
        {
            [HarmonyPrefix]
            private static void Prefix()
            {
                Diagnostics.Hit("Dummy");
                if (ModeRuntime.Bypass)
                    return;

                int want = AutoSpawnCount?.Value ?? 0;
                if (want <= 0)
                    return;

                int ok = 0;
                for (int i = 0; i < want; i++)
                {
                    if (DummyManager.Spawn(0, 0, out _, out string err))
                        ok++;
                    else
                        Plugin.Log.LogWarning($"[HS] Dummy：自动生成第 {i + 1} 个失败 — {err}");
                }

                Plugin.Log.LogInfo($"[HS] Dummy：选角前自动生成 {ok}/{want} 个假人。");
            }
        }

        // ── 生命周期：vanilla 回大厅会清掉所有 IsDummy 玩家，需要重建 ──────────
        // StartLobby（:170202-170289）会 HandleLeavePlayer + 归还座位 + 发 S_LEAVE_GAME，
        // 把 IsDummy 的假人全部清掉；不重建的话第二局就没有靶子。
        [HarmonyPatch(typeof(GameRoom), "StartLobby")]
        internal static class StartLobbyHook
        {
            [HarmonyPostfix]
            private static void Postfix()
            {
                if (ModeRuntime.Bypass)
                    return;

                int want = AutoSpawnCount?.Value ?? 0;
                if (want <= 0)
                    return;
                if (DummyManager.ActiveCount > 0)
                    return;   // 还没被清，不重复建

                int ok = 0;
                for (int i = 0; i < want; i++)
                {
                    if (DummyManager.Spawn(0, 0, out _, out _))
                        ok++;
                }

                Plugin.Log.LogInfo($"[HS] Dummy：回大厅后重建 {ok}/{want} 个假人。");
            }
        }

        // ── 官方上报拦截（可选）：测试局不兑换免费货币 ─────────────────────
        // 说明：假人并不会"额外"触发对官方服的请求 —— 我们让 session.SteamId=0，
        // CosmeticSightingReporter.Record 要求 GetRosterSteamId != 0 才入队（:33019/:30989），
        // 外观上报天然不会发生。全项目也不存在成就/Steam 统计上报。
        // 唯一的官方后台写是结算时的免费货币兑换（TiamatPayClient → PAY_BACKEND_URL
        // + /v1/free-currency/grant，:38436，由 UI_TotalResult.TryGrantFreeCurrencyFromResult :76697 触发）。
        // 它只读房主自己的战绩，但因为房主每局稳赢，等于稳定刷取 —— 所以给一个开关。
        [HarmonyPatch]
        internal static class OfficialReportHook
        {
            private static global::System.Reflection.MethodBase Target()
            {
                var type = AccessTools.TypeByName("UI_TotalResult");
                return type == null ? null : AccessTools.Method(type, "TryGrantFreeCurrencyFromResult");
            }

            [HarmonyPrepare]
            private static bool Prepare() => Target() != null;

            [HarmonyTargetMethod]
            private static global::System.Reflection.MethodBase TargetMethod() => Target();

            [HarmonyPrefix]
            private static bool Prefix()
            {
                if (ModeRuntime.Bypass)
                    return true;
                if (BlockOfficialRequests == null || !BlockOfficialRequests.Value)
                    return true;

                Plugin.Log.LogInfo("[HS] Dummy：已拦截结算界面的免费货币兑换请求（测试局不刷货币）。");
                return false;
            }
        }
    }
}
