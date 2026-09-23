using HarmonyLib;
using Server.Game;
using HideAndSeek.Core;

namespace HideAndSeek.Features.Dummy
{
    /// <summary>
    /// 让假人按指定角色真正参与选角。
    ///
    /// 独立成段（而不是并入 DummyFeature）的原因：DummyFeature 默认关闭，
    /// 而 PatchLoader 对未启用的段会整体跳过 PatchAll ——
    /// 于是手动用 hs_dummy 生成的假人虽然能进房间，选角钩子却从未挂上，
    /// 表现为"指定了角色却不遵守、一路拖到选角结束"
    ///（日志里既没有"已选角"也没有"[Pick] PickCharacter ignored"，
    /// 因为那段代码根本没运行）。本段默认启用，保证手动生成的假人也能选角。
    ///
    /// 为什么挂 PickCharacterTick 而不是 StartPick 的 Postfix：
    ///   GameRoom.PickCharacter（:171136）有两道前置 ——
    ///   State == EGameState.PickCharacter 且 _pickReady == true，
    ///   不满足时直接 return（仅 !_pickReady 时打一条 Debug.Log）。
    ///   StartPick 内部先置 false（:171013）再置 true（:171032），
    ///   其 Postfix 的时机恰好落在这个窗口，调用会被静默丢弃。
    ///   PickCharacterTick 是该阶段的每秒回调（:171085 PushAfter(1000, ...)），
    ///   那时状态稳定，可可靠重试（ApplyPickedCharacters 幂等）。
    /// </summary>
    [PatchFeature(
        section: "DummyPick",
        description: "让假人按指定角色真正参与选角（含手动用 hs_dummy 生成的假人）。",
        defaultEnabled: true,
        side: FeatureSide.Host)]
    internal static class DummyPickFeature
    {
        [HarmonyPatch(typeof(GameRoom), "StartPick")]
        internal static class StartPickHook
        {
            [HarmonyPostfix]
            private static void Postfix(GameRoom __instance)
            {
                Diagnostics.Hit("DummyPick");
                if (ModeRuntime.Bypass)
                    return;

                DummyManager.ApplyPickedCharacters(__instance);
            }
        }

        [HarmonyPatch(typeof(GameRoom), "PickCharacterTick")]
        internal static class PickTickHook
        {
            [HarmonyPostfix]
            private static void Postfix(GameRoom __instance)
            {
                Diagnostics.Hit("DummyPick");
                if (ModeRuntime.Bypass)
                    return;

                DummyManager.ApplyPickedCharacters(__instance);
            }
        }
    }
}