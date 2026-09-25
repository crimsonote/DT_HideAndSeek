using HarmonyLib;
using HideAndSeek.Core;

namespace HideAndSeek.Features.UI
{
    /// <summary>
    /// 把 HideAndSeek 的设置页签挂进大厅「详细设置」（<c>UI_LobbyPreset</c>）。
    ///
    /// 三个钩子<b>平级</b>放在 L0 类下（都是 L1）—— 补丁嵌套只有一层，L2 会静默失效且日志不报错：
    /// <list type="bullet">
    /// <item><c>Init</c>：建页签与面板。弹窗实例被缓存，Init 只生效一次（<c>InitBase._init</c> 守卫）。</item>
    /// <item><c>Refresh</c>：每次打开弹窗都走（<c>ACS:76078</c>），同步配置值 + 房主闸门。</item>
    /// <item><c>SwitchTab</c>：原版切回自己的两页时收起我的面板。原版该方法硬编码两页签，<b>不改它</b>。</item>
    /// </list>
    ///
    /// 本类只做"转发"，全部逻辑在 <see cref="LobbySettingHost"/>；
    /// 具体有几个页签、每个页签有哪些设置项，一律由 <see cref="SettingRegistry"/> 决定，与本文件无关。
    /// </summary>
    [PatchFeature(
        section: "UI_LobbyPreset",
        description: "在大厅「详细设置」页加入 HideAndSeek 自己的页签（纯 UI；仅房主可改）。",
        defaultEnabled: true,
        side: FeatureSide.Client)]
    internal static class LobbySettingPatches
    {
        [HarmonyPatch(typeof(UI_LobbyPreset), nameof(UI_LobbyPreset.Init))]
        internal static class InitHook
        {
            [HarmonyPostfix]
            private static void Postfix(UI_LobbyPreset __instance, bool __result)
            {
                // __result == false 表示 InitBase 的 _init 守卫拦下了（此前已初始化过），
                // 此时界面上已经有页签了，不要重复建。
                if (!__result) return;
                LobbySettingHost.OnPresetInit(__instance);
            }
        }

        [HarmonyPatch(typeof(UI_LobbyPreset), "Refresh")]
        internal static class RefreshHook
        {
            [HarmonyPostfix]
            private static void Postfix(UI_LobbyPreset __instance) => LobbySettingHost.OnPresetRefresh(__instance);
        }

        [HarmonyPatch(typeof(UI_LobbyPreset), "SwitchTab")]
        internal static class SwitchTabHook
        {
            [HarmonyPostfix]
            private static void Postfix(UI_LobbyPreset __instance) => LobbySettingHost.OnSwitchTab(__instance);
        }
    }
}
