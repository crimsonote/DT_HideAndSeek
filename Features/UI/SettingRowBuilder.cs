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

        /// <summary>
        /// 克隆一个**整数滑条行**并绑定设置项。
        ///
        /// 模板 = 原版 `ETCPresetContent/WeaponMove` 行：`TitleBg` + `WeaponMoveSlider`
        /// （`Slider` + `UI_GaugeSlider`）+ `NumberBg/WeaponMoveValue`（数值文本）。
        /// </summary>
        /// <param name="content">内容容器（带 VerticalLayoutGroup，会自动排布）。</param>
        /// <param name="rowTemplate">原版滑条行模板（WeaponMove）。</param>
        /// <param name="item">设置项声明（用 <see cref="LobbySettingItem.Min"/> / <see cref="LobbySettingItem.Max"/>）。</param>
        /// <param name="onValueChanged">用户拖动滑条时的回调（写配置 + Save）。</param>
        public static GameObject BuildSliderRow(
            Transform content,
            GameObject rowTemplate,
            LobbySettingItem item,
            Action<int> onValueChanged)
        {
            var row = UnityEngine.Object.Instantiate(rowTemplate, content);
            row.name = "HSRow_" + item.Key;
            row.SetActive(true);

            // 标题：**按路径取**（`TitleBg` 下那个），不要用"第一个 TMP_Text"——
            // 滑条行的数值文本也是 TMP_Text，取错就会把标题写进数值位。
            var title = row.transform.Find("TitleBg")?.GetComponentInChildren<TMP_Text>(true)
                        ?? row.GetComponentInChildren<TMP_Text>(true);
            if (title != null)
            {
                title.text = item.Title;
                Util.SetFontAndMaterial(title, Define.EFontMaterialType.Normal);
                title.overflowMode = TextOverflowModes.Overflow;
            }
            else
            {
                Log.Warn($"滑条行模板里找不到标题 TMP_Text，设置项 [{item.Key}] 的标题将不会显示。");
            }

            var valueText = row.transform.Find("NumberBg/WeaponMoveValue")?.GetComponent<TMP_Text>()
                            ?? row.transform.Find("NumberBg")?.GetComponentInChildren<TMP_Text>(true);

            var slider = row.GetComponentInChildren<Slider>(true);
            if (slider == null)
            {
                Log.Error($"滑条行模板里找不到 Slider，设置项 [{item.Key}] 不可交互。");
                return row;
            }

            // ⚠ 与开关同理：Instantiate 会把原版挂在同一个 Slider 上的监听器一起复制过来
            // （那个会改原版的 WeaponMove 参数）⇒ 必须清掉（铁律 #5）。
            slider.onValueChanged.RemoveAllListeners();

            int min = item.Min;
            int max = item.Max;
            slider.minValue = min;
            slider.maxValue = max;
            slider.wholeNumbers = true;      // 整数设置 ⇒ 步进 1，避免出现 12.34 秒

            var entry = ReadIntEntry(item);
            int current = entry != null ? Mathf.Clamp(entry.Value, min, max) : min;
            slider.SetValueWithoutNotify(current);
            if (valueText != null) valueText.text = current.ToString();

            if (onValueChanged != null)
            {
                slider.onValueChanged.AddListener(v =>
                {
                    int iv = Mathf.Clamp(Mathf.RoundToInt(v), min, max);
                    // 数值文本由原版 `UI_GaugeSlider` 可能也会写；这里显式写一次，保证与我们夹取后的值一致。
                    if (valueText != null) valueText.text = iv.ToString();
                    onValueChanged(iv);
                });
            }

            ApplyEditable(row, item);
            return row;
        }

        /// <summary>把滑条行同步成配置里的当前值（不改配置）。重开弹窗时用。</summary>
        public static void SyncSliderRow(GameObject row, LobbySettingItem item)
        {
            if (row == null) return;

            var entry = ReadIntEntry(item);
            if (entry == null) return;

            var slider = row.GetComponentInChildren<Slider>(true);
            if (slider == null) return;

            int v = Mathf.Clamp(entry.Value, item.Min, item.Max);
            slider.SetValueWithoutNotify(v);

            var valueText = row.transform.Find("NumberBg/WeaponMoveValue")?.GetComponent<TMP_Text>()
                            ?? row.transform.Find("NumberBg")?.GetComponentInChildren<TMP_Text>(true);
            if (valueText != null) valueText.text = v.ToString();
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

        /// <summary>惰性取**整数**配置项（滑条用）。</summary>
        private static BepInEx.Configuration.ConfigEntry<int> ReadIntEntry(LobbySettingItem item)
        {
            var entry = item.IntEntry != null ? item.IntEntry() : null;
            if (entry == null)
            {
                Log.Error($"设置项 [{item.Key}] 的配置项尚未绑定，该行不会同步配置值。");
            }
            return entry;
        }
    }
}
