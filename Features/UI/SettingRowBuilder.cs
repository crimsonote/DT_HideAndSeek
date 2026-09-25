using System;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace HideAndSeek.Features.UI
{
    /// <summary>
    /// 「克隆行模板 → 换标题 → 重绑开关」的唯一入口。
    ///
    /// 扩展方式（与 <see cref="LobbySettingKind"/> 一一对应）：
    /// 新增一种控件形态 = 在这里加一个 <c>BuildXxxRow</c>，模板取交接文档 §4.5 里对应的原版行
    /// （滑条 = WeaponMove 行，下拉 = Difficulty 行的 DifficultyDropdown），
    /// 然后在 <see cref="LobbySettingHost"/> 的分发处加一个 case。
    /// <b>补丁代码永远不需要改。</b>
    /// </summary>
    internal static class SettingRowBuilder
    {
        /// <summary>
        /// 克隆一个开关行并绑定设置项。
        /// </summary>
        /// <param name="content">内容容器（ETCPresetContent 的克隆），带 VerticalLayoutGroup，会自动排布。</param>
        /// <param name="rowTemplate">原版开关行模板（MastermindVoteLoss）。</param>
        /// <param name="item">设置项声明。</param>
        /// <param name="onValueChanged">用户拨动开关时的回调（写配置 + Save）。</param>
        public static GameObject BuildToggleRow(
            Transform content,
            GameObject rowTemplate,
            LobbySettingItem item,
            Action<bool> onValueChanged)
        {
            var row = UnityEngine.Object.Instantiate(rowTemplate, content);
            row.name = "HSRow_" + item.Key;
            row.SetActive(true);   // 模板可能正被原版逻辑关掉（见交接文档 R2）

            // 标题：模板行的标题子物体名带原版前缀，这里只按类型取第一个 TMP_Text。
            var title = row.GetComponentInChildren<TMP_Text>(true);
            if (title != null)
            {
                title.text = item.Title;
                // 换文案后必须重设字体材质，否则与相邻控件不是同一套（一眼看出是外来的）。
                Util.SetFontAndMaterial(title, Define.EFontMaterialType.Normal);
                title.overflowMode = TextOverflowModes.Overflow;
            }
            else
            {
                Log.Warn($"行模板里找不到标题 TMP_Text，设置项 [{item.Key}] 的标题将不会显示。");
            }

            var toggle = row.GetComponentInChildren<Toggle>(true);
            if (toggle == null)
            {
                Log.Error($"行模板里找不到 Toggle，设置项 [{item.Key}] 不可交互。");
                return row;
            }

            // ⚠ Instantiate 会把原版挂在同一个 Toggle 上的监听器一起复制过来
            // （这里就是 OnMastermindToggleChanged），不清掉的话拨我的开关会连带改原版的
            // _working.MastermindVoteLoss —— 最易踩、最难查的一类问题（交接文档 R1）。
            toggle.onValueChanged.RemoveAllListeners();

            var entry = ReadEntry(item);
            if (entry != null)
            {
                toggle.SetIsOnWithoutNotify(entry.Value);
            }

            if (onValueChanged != null)
            {
                toggle.onValueChanged.AddListener(v => onValueChanged(v));
            }

            ApplyEditable(row, item);
            return row;
        }

        /// <summary>把行同步成配置里的当前值（不改配置）。重开弹窗时用。</summary>
        public static void SyncToggleRow(GameObject row, LobbySettingItem item)
        {
            if (row == null) return;

            var entry = ReadEntry(item);
            if (entry == null) return;

            var toggle = row.GetComponentInChildren<Toggle>(true);
            if (toggle != null) toggle.SetIsOnWithoutNotify(entry.Value);
        }

        /// <summary>
        /// 应用 <see cref="LobbySettingItem.IsEditable"/>：不可编辑时行变半透明且不响应点击。
        /// 语义照抄原版 <c>UI_LobbyPreset.SetRowEditable</c>（ACS:57129-57135）。
        /// </summary>
        public static void ApplyEditable(GameObject row, LobbySettingItem item)
        {
            if (row == null || item == null || item.IsEditable == null) return;

            bool editable;
            try
            {
                editable = item.IsEditable();
            }
            catch (Exception ex)
            {
                Log.Warn($"设置项 [{item.Key}] 的可编辑判定抛异常，按不可编辑处理：{ex.Message}");
                editable = false;
            }

            var group = Util.GetOrAddComponent<CanvasGroup>(row);
            group.alpha = editable ? 1f : 0.4f;
            group.interactable = editable;
            group.blocksRaycasts = editable;
        }

        /// <summary>惰性取配置项；未绑定（理论上不会发生，UI 是懒加载的）时记一条错误。</summary>
        private static BepInEx.Configuration.ConfigEntry<bool> ReadEntry(LobbySettingItem item)
        {
            var entry = item.BoolEntry != null ? item.BoolEntry() : null;
            if (entry == null)
            {
                Log.Error($"设置项 [{item.Key}] 的配置项尚未绑定，该行不会同步配置值。");
            }
            return entry;
        }
    }
}
