using System;
using BepInEx.Configuration;

namespace HideAndSeek.Features.UI
{
    /// <summary>
    /// 设置项的控件形态。新增形态时配套两步（见 SettingRowBuilder 的注释）：
    /// 加一个静态工厂，再加一个同名的行克隆方法 —— 补丁代码不动。
    /// </summary>
    internal enum LobbySettingKind
    {
        /// <summary>开关行：占位模板 = MastermindVoteLoss 行。</summary>
        Toggle,

        // 未来扩展位（模板已确认，见交接文档 §4.5）：
        //   Slider   → 占位模板 WeaponMove 行（Slider + UI_GaugeSlider + NumberBg/Value）
        //   Dropdown → 占位模板 Difficulty 行的 DifficultyDropdown（TMP_Dropdown，需自持引用）
        //   Label    → 直接克隆 TitleBg（纯说明文本，无控件）
    }

    /// <summary>
    /// 一个设置项的<b>声明</b>（纯数据，不含任何 UI 逻辑）。
    ///
    /// 为什么配置项用委托而不是 ConfigEntry 实例：
    /// <c>RegisterAll()</c> 在 <c>Plugin.Start()</c> 里、<c>PatchLoader.Load</c> <b>之前</b>调用，
    /// 那一刻所有 <c>ConfigEntry</c> 还没绑定（都是 null）。存取值委托即可与绑定顺序解耦 ——
    /// UI 是懒加载的（首次「详细设置」才实例化），取值时必然已经绑定好了。
    /// </summary>
    internal sealed class LobbySettingItem
    {
        /// <summary>稳定标识，仅用于日志与调试（不写进 .cfg）。</summary>
        public string Key { get; private set; }

        /// <summary>行标题（中文，直接写进 TMP_Text）。</summary>
        public string Title { get; private set; }

        /// <summary>控件形态。</summary>
        public LobbySettingKind Kind { get; private set; }

        /// <summary>Toggle 形态绑定的配置项（惰性取值）。</summary>
        public Func<ConfigEntry<bool>> BoolEntry { get; private set; }

        /// <summary>
        /// 可选：该行此刻是否可编辑（null = 始终可编辑）。
        /// 房主闸门在<b>面板层</b>统一处理（见 LobbySettingHost.ApplyGate），
        /// 这里留给"随其它配置变化的行"（例：只有开局发刀打开时才可改的武器 ID）。
        /// </summary>
        public Func<bool> IsEditable { get; private set; }

        private LobbySettingItem()
        {
        }

        /// <summary>声明一个开关行。</summary>
        public static LobbySettingItem Toggle(
            string key,
            string title,
            Func<ConfigEntry<bool>> entry,
            Func<bool> isEditable = null)
        {
            if (string.IsNullOrEmpty(key)) throw new ArgumentException("key required", nameof(key));
            if (entry == null) throw new ArgumentNullException(nameof(entry));

            return new LobbySettingItem
            {
                Key = key,
                Title = title ?? key,
                Kind = LobbySettingKind.Toggle,
                BoolEntry = entry,
                IsEditable = isEditable
            };
        }
    }
}
