using System.Collections.Generic;

namespace HideAndSeek.Features.UI
{
    /// <summary>一个页签及其设置项（由 <see cref="SettingRegistry.Register"/> 产出）。</summary>
    internal sealed class LobbySettingTab
    {
        private readonly List<LobbySettingItem> _items = new List<LobbySettingItem>();

        /// <summary>页签标题（如「捉迷藏」）。</summary>
        public string Title { get; private set; }

        /// <summary>排序权重，小的在前。原版两个页签不在本注册表内，恒在最左侧。</summary>
        public int Order { get; private set; }

        /// <summary>本页签的设置项，按声明顺序。</summary>
        public IList<LobbySettingItem> Items
        {
            get { return _items; }
        }

        internal LobbySettingTab(string title, int order)
        {
            Title = title ?? "";
            Order = order;
        }

        internal void Add(LobbySettingItem item)
        {
            if (item != null) _items.Add(item);
        }
    }

    /// <summary>
    /// 页签 / 设置项注册表 —— <b>本框架唯一的扩展点</b>。
    ///
    /// 扩展方式：
    /// <list type="bullet">
    /// <item>新增设置项：在 <see cref="HideAndSeekSettingsContent.RegisterAll"/> 里多写一行
    /// <c>LobbySettingItem.Toggle(...)</c>，构建器与补丁都不动。</item>
    /// <item>新增页签：多调一次 <c>Register(...)</c> 即可 —— 宿主按注册表循环建页签与面板，
    /// 三页互斥（原版两页 + 我的第 N 页）由 LobbySettingHost 统一维护，不需要重构。</item>
    /// </list>
    ///
    /// 注册必须发生在 UI 懒加载之前；现行调用点是 <c>Plugin.Start()</c> 里、<c>PatchLoader.Load</c> 之前，
    /// 必然早于首次 <c>UI_LobbyPreset</c> 实例化。
    /// </summary>
    internal static class SettingRegistry
    {
        private static readonly List<LobbySettingTab> TabList = new List<LobbySettingTab>();

        /// <summary>已登记的页签，按 <see cref="LobbySettingTab.Order"/> 升序。</summary>
        public static IList<LobbySettingTab> Tabs
        {
            get { return TabList; }
        }

        /// <summary>登记一个页签及其设置项。可重复调用（每次产出一个新页签）。</summary>
        public static LobbySettingTab Register(string title, int order, params LobbySettingItem[] items)
        {
            var tab = new LobbySettingTab(title, order);
            if (items != null)
            {
                foreach (var item in items) tab.Add(item);
            }

            TabList.Add(tab);
            TabList.Sort((a, b) => a.Order.CompareTo(b.Order));
            return tab;
        }

        /// <summary>清空注册表。仅供测试／热重载；正常流程不需要。</summary>
        public static void Clear()
        {
            TabList.Clear();
        }
    }
}
