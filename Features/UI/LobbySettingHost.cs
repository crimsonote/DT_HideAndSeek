using System;
using System.Collections.Generic;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace HideAndSeek.Features.UI
{
    /// <summary>
    /// 「详细设置」里 HideAndSeek 自己页签的运行时宿主。
    ///
    /// 职责：按 <see cref="SettingRegistry"/> 建页签与面板 → 维护"难度 / 其他 / 我的页签"三页互斥的可见性
    /// → 把配置值同步进控件 → 把用户改动写回配置并落盘 → 房主闸门。
    ///
    /// 挂在 <c>UI_LobbyPreset</c> 的 GameObject 上，随弹窗生命周期自动销毁。
    /// 互斥与排布<b>都交给原版预制件自带的组件</b>：
    /// 页签栏 <c>FooterToggleGroup</c> 自带 <c>ToggleGroup</c>（互斥）+
    /// <c>HorizontalLayoutGroup</c>（排布）+ <c>ContentSizeFitter</c>（撑宽）；
    /// 内容容器 <c>ETCPresetContent</c> 自带 <c>VerticalLayoutGroup</c> + <c>ContentSizeFitter</c>。
    /// 因此不需要手写互斥、不需要手工定位、不需要手工 LayoutRebuilder（只在切页时重建一次）。
    /// </summary>
    internal sealed class LobbySettingHost : MonoBehaviour
    {
        // ── prefab 层级常量：全部来自 UI_LobbyPreset 的实测 dump（交接文档 §4.2 / §4.5）──
        private const string TabBarPath = "PresetPanel/FooterToggleGroup";
        private const string PanelRootPath = "PresetPanel/PresetPanels";
        private const string OriginalDifficultyTabName = "Toggle_Difficulty";
        private const string OriginalEtcTabName = "Toggle_ETC";
        private const string OriginalDifficultyPanelName = "Preset_Difficulty";
        private const string OriginalEtcPanelName = "Preset_ETC";
        private const string ContentPath = "Scroll View/Viewport/ETCPresetContent";
        private const string RowTemplatePath = "Scroll View/Viewport/ETCPresetContent/MastermindVoteLoss";

        private const string TabOnSprite = "setting_tab_on.sprite";
        private const string TabOffSprite = "setting_tab_off.sprite";

        /// <summary>选中态文字色，与原版 <c>UI_LobbyPreset._tabSelected</c>（#FF2E70）一致。</summary>
        private static readonly Color TabSelectedColor = new Color32(0xFF, 0x2E, 0x70, 0xFF);

        /// <summary>一个页签的运行时引用。</summary>
        private sealed class TabRuntime
        {
            public LobbySettingTab Tab;
            public Toggle Toggle;
            public Image Background;
            public TMP_Text Label;
            public GameObject Panel;
            public CanvasGroup Gate;
            public readonly List<RowRuntime> Rows = new List<RowRuntime>();
        }

        /// <summary>一行的运行时引用（用于重开弹窗时同步配置值）。</summary>
        private sealed class RowRuntime
        {
            public GameObject Row;
            public LobbySettingItem Item;
        }

        private readonly List<TabRuntime> _tabs = new List<TabRuntime>();

        private GameObject _originalDifficultyPanel;
        private GameObject _originalEtcPanel;
        private Toggle _originalDifficultyToggle;
        private Toggle _originalEtcToggle;
        private Image _originalDifficultyImage;
        private Image _originalEtcImage;
        private TMP_Text _originalDifficultyLabel;
        private TMP_Text _originalEtcLabel;

        // ── 补丁入口（LobbySettingPatches 的三个 Postfix 直接调这三条）──

        /// <summary><c>UI_LobbyPreset.Init</c> 之后：建页签与面板。只应生效一次。</summary>
        internal static void OnPresetInit(UI_LobbyPreset preset)
        {
            try
            {
                if (preset == null) return;

                // InitBase 的 _init 守卫已保证 Init 只生效一次（补丁侧还按 __result 过滤过），
                // 这里再判一次重：万一上游以后改成"每次显示都重建"，也不会重复建出两套页签（R4）。
                if (preset.GetComponent<LobbySettingHost>() != null) return;

                var host = preset.gameObject.AddComponent<LobbySettingHost>();
                host.Build(preset.transform);
            }
            catch (Exception ex)
            {
                Log.Error("构建设置页签时抛异常（本次不显示页签，其余功能不受影响）：\n" + ex);
            }
        }

        /// <summary><c>UI_LobbyPreset.Refresh</c> 之后：同步配置值 + 应用房主闸门。
        /// Refresh 是每次打开弹窗都会走的路径（<c>ACS:76078</c>），所以这里也是"重进后值还在"的验证点。</summary>
        internal static void OnPresetRefresh(UI_LobbyPreset preset)
        {
            var host = GetHost(preset);
            if (host == null) return;

            try
            {
                host.SyncFromConfig();
                host.ApplyGate();
            }
            catch (Exception ex)
            {
                Log.Error("同步设置页签状态时抛异常：\n" + ex);
            }
        }

        /// <summary><c>UI_LobbyPreset.SwitchTab</c> 之后：原版切回它自己的两页时，收起我的面板。
        /// 原版 SwitchTab 硬编码两页签，<b>不改它</b>，只在其后追加自己的可见性同步。</summary>
        internal static void OnSwitchTab(UI_LobbyPreset preset)
        {
            var host = GetHost(preset);
            if (host == null) return;

            try
            {
                host.HideMine();
            }
            catch (Exception ex)
            {
                Log.Error("同步页签可见性时抛异常：\n" + ex);
            }
        }

        private static LobbySettingHost GetHost(UI_LobbyPreset preset)
        {
            try
            {
                return preset != null ? preset.GetComponent<LobbySettingHost>() : null;
            }
            catch
            {
                return null;
            }
        }

        // ── 构建 ──

        private void Build(Transform root)
        {
            Transform tabBar = root.Find(TabBarPath);
            Transform panelRoot = root.Find(PanelRootPath);
            Transform etcPanel = panelRoot != null ? panelRoot.Find(OriginalEtcPanelName) : null;

            Transform tabTemplate = tabBar != null ? tabBar.Find(OriginalEtcTabName) : null;
            Transform contentTemplate = etcPanel != null ? etcPanel.Find(ContentPath) : null;
            Transform rowTemplate = etcPanel != null ? etcPanel.Find(RowTemplatePath) : null;

            if (tabBar == null || panelRoot == null || etcPanel == null ||
                tabTemplate == null || contentTemplate == null || rowTemplate == null)
            {
                Log.Error(
                    "设置页签构建失败：prefab 层级路径未命中（" +
                    $"tabBar={tabBar != null}, panelRoot={panelRoot != null}, etcPanel={etcPanel != null}, " +
                    $"tabTemplate={tabTemplate != null}, contentTemplate={contentTemplate != null}, " +
                    $"rowTemplate={rowTemplate != null}）。" +
                    "若上游改了 prefab 结构，请按交接文档 §8.3 上运行时探针后更新本类的路径常量。");
                return;
            }

            _originalDifficultyPanel = FindGameObject(panelRoot, OriginalDifficultyPanelName);
            _originalEtcPanel = etcPanel.gameObject;

            _originalDifficultyToggle = FindToggle(tabBar, OriginalDifficultyTabName);
            _originalEtcToggle = FindToggle(tabBar, OriginalEtcTabName);
            _originalDifficultyImage = FindImage(tabBar, OriginalDifficultyTabName);
            _originalEtcImage = FindImage(tabBar, OriginalEtcTabName);
            _originalDifficultyLabel = FindLabel(tabBar, OriginalDifficultyTabName);
            _originalEtcLabel = FindLabel(tabBar, OriginalEtcTabName);

            int index = 0;
            foreach (var tab in SettingRegistry.Tabs)
            {
                var runtime = new TabRuntime { Tab = tab };
                BuildTab(tabBar, tabTemplate.gameObject, runtime, index);
                BuildPanel(panelRoot, etcPanel.gameObject, rowTemplate.gameObject, runtime, index);
                _tabs.Add(runtime);
                index++;
            }

            if (_tabs.Count == 0)
            {
                Log.Warn("设置注册表为空，未创建任何页签。");
                return;
            }

            // 建完立刻同步一次：Init 早于 Refresh，而 Refresh 未必一定被调用（例如被别处直接 ShowKeyUI）。
            SyncFromConfig();
            ApplyGate();

            int rowCount = 0;
            foreach (var runtime in _tabs) rowCount += runtime.Rows.Count;
            Log.Info($"设置页签已构建：{_tabs.Count} 个页签 / {rowCount} 个设置项，当前{(IsHost() ? "是房主（可改）" : "非房主（只读）")}。");
        }

        private void BuildTab(Transform tabBar, GameObject template, TabRuntime runtime, int index)
        {
            var tabGo = UnityEngine.Object.Instantiate(template, tabBar);
            tabGo.name = "Toggle_HS_" + index;
            tabGo.SetActive(true);   // 模板可能正被原版 SwitchTab 关掉（R2）

            // ⚠ Instantiate 会把 UI_EventHandler 上原版的点击监听器一起复制过来。
            // 不清掉的话，点我的页签会同时触发原版 SwitchTab(difficulty:false)，
            // 它会把 Toggle_ETC 置为选中 → 两个页签同时高亮（交接文档 R1，最易踩最难查）。
            var handler = tabGo.GetComponent<UI_EventHandler>();
            if (handler != null) handler.Clear();
            UI_Base.BindEvent(tabGo, _ => OnTabClicked(runtime));   // 与原版一致：走 UI_EventHandler，不是 onValueChanged

            runtime.Toggle = tabGo.GetComponent<Toggle>();
            if (runtime.Toggle != null)
            {
                // 原版页签的 onValueChanged 本来是空的（互斥走 ToggleGroup + 手写 SetIsOnWithoutNotify），
                // 这里显式清一次，保证即使上游以后加了监听器也不会被我的克隆继承。
                runtime.Toggle.onValueChanged.RemoveAllListeners();
                runtime.Toggle.SetIsOnWithoutNotify(false);
                // 互斥不需要我做：Instantiate 复制了 m_Group 引用，克隆页签自动加入同一个 ToggleGroup。
            }
            else
            {
                Log.Error($"克隆出的页签 [{runtime.Tab.Title}] 上没有 Toggle，点击不会生效。");
            }

            runtime.Background = tabGo.GetComponent<Image>();
            runtime.Label = tabGo.GetComponentInChildren<TMP_Text>(true);
            if (runtime.Label != null)
            {
                runtime.Label.text = runtime.Tab.Title;
                runtime.Label.overflowMode = TextOverflowModes.Overflow;
                // 换文案后必须重设字体材质，否则字体与相邻页签不是同一套（一眼看出是外来的，R7）。
                Util.SetFontAndMaterial(runtime.Label, Define.EFontMaterialType.Normal);
            }
            else
            {
                Log.Warn($"克隆出的页签 [{runtime.Tab.Title}] 上没有标题文本，页签会显示为空。");
            }

            SetTabSelected(runtime, false);
        }

        private void BuildPanel(Transform panelRoot, GameObject template, GameObject rowTemplate, TabRuntime runtime, int index)
        {
            var panelGo = UnityEngine.Object.Instantiate(template, panelRoot);
            panelGo.name = "Preset_HS_" + index;
            runtime.Panel = panelGo;

            Transform content = panelGo.transform.Find(ContentPath);
            if (content == null)
            {
                Log.Error($"克隆出的面板 [{panelGo.name}] 上找不到 {ContentPath}，该页签的设置项无法显示。");
                panelGo.SetActive(false);
                return;
            }

            // 克隆来的内容是原版 ETC 的七行（滑条 + 开关），本页签只要骨架：
            // 保留 Scroll View / Viewport / ETCPresetContent，清掉里面的行。
            ClearChildren(content);

            foreach (var item in runtime.Tab.Items)
            {
                if (item == null) continue;

                switch (item.Kind)
                {
                    case LobbySettingKind.Toggle:
                        var row = SettingRowBuilder.BuildToggleRow(
                            content, rowTemplate, item, value => OnRowValueChanged(item, value));
                        runtime.Rows.Add(new RowRuntime { Row = row, Item = item });
                        break;

                    default:
                        Log.Error($"设置项 [{item.Key}] 的控件形态 {item.Kind} 还没有对应的行克隆方法，已跳过。" +
                                  "请按 SettingRowBuilder 的注释补一个 BuildXxxRow。");
                        break;
                }
            }

            runtime.Gate = Util.GetOrAddComponent<CanvasGroup>(panelGo);
            panelGo.SetActive(false);   // 默认停在原版的「难度」页
        }

        /// <summary>
        /// 清空内容容器。
        /// 先 <c>SetParent(null)</c> 再 <c>Destroy</c>：Unity 的 Destroy 要到帧末才真正移除，
        /// 不先脱离父物体的话，这些行会在本帧剩余时间里继续参与 VerticalLayoutGroup 的排版。
        /// </summary>
        private static void ClearChildren(Transform content)
        {
            for (int i = content.childCount - 1; i >= 0; i--)
            {
                Transform child = content.GetChild(i);
                child.SetParent(null, false);
                UnityEngine.Object.Destroy(child.gameObject);
            }
        }

        // ── 可见性：难度 / 其他 / 我的页签 三选一 ──

        /// <summary>点我的页签：收起原版两页，只显示我的。</summary>
        private void OnTabClicked(TabRuntime target)
        {
            try
            {
                if (target == null || target.Panel == null) return;

                if (_originalDifficultyPanel != null) _originalDifficultyPanel.SetActive(false);
                if (_originalEtcPanel != null) _originalEtcPanel.SetActive(false);

                // 原版两个页签的底图与文字色只有原版 SwitchTab 会改；我们绕过了它，必须自己复位，
                // 否则会出现"原版页签还亮着 + 我的页签也亮着"。
                ResetOriginalTabs();

                foreach (var runtime in _tabs)
                {
                    SetTabSelected(runtime, ReferenceEquals(runtime, target));
                }

                target.Panel.SetActive(true);
                RebuildLayout(target.Panel);
            }
            catch (Exception ex)
            {
                Log.Error("切换到 HideAndSeek 页签时失败：\n" + ex);
            }
        }

        /// <summary>原版切回它自己两页时，收起我的面板。</summary>
        private void HideMine()
        {
            foreach (var runtime in _tabs)
            {
                if (runtime.Panel != null && runtime.Panel.activeSelf) runtime.Panel.SetActive(false);
                SetTabSelected(runtime, false);
            }
        }

        private void ResetOriginalTabs()
        {
            if (_originalDifficultyToggle != null) _originalDifficultyToggle.SetIsOnWithoutNotify(false);
            if (_originalEtcToggle != null) _originalEtcToggle.SetIsOnWithoutNotify(false);
            if (_originalDifficultyLabel != null) _originalDifficultyLabel.color = Color.white;
            if (_originalEtcLabel != null) _originalEtcLabel.color = Color.white;
            SetTabSprite(_originalDifficultyImage, false);
            SetTabSprite(_originalEtcImage, false);
        }

        private static void SetTabSelected(TabRuntime runtime, bool on)
        {
            if (runtime == null) return;
            if (runtime.Toggle != null) runtime.Toggle.SetIsOnWithoutNotify(on);
            SetTabSprite(runtime.Background, on);
            if (runtime.Label != null) runtime.Label.color = on ? TabSelectedColor : Color.white;
        }

        /// <summary>底图切换，照抄原版 <c>UI_LobbyPreset.SetTabSprite</c>（ACS:56896-56903）。</summary>
        private static void SetTabSprite(Image image, bool on)
        {
            if (image == null) return;
            try
            {
                var sprite = Managers.Resource.Load<Sprite>(on ? TabOnSprite : TabOffSprite);
                if (sprite != null) image.sprite = sprite;
            }
            catch (Exception ex)
            {
                Log.Warn($"加载页签底图失败（{(on ? TabOnSprite : TabOffSprite)}）：{ex.Message}");
            }
        }

        /// <summary>照抄原版 SwitchTab 尾部的布局重建（面板从 inactive 切到 active 时必须做）。</summary>
        private static void RebuildLayout(GameObject panel)
        {
            var rect = panel.GetComponent<RectTransform>();
            if (rect != null) LayoutRebuilder.ForceRebuildLayoutImmediate(rect);

            var texts = panel.GetComponentsInChildren<TMP_Text>(true);
            for (int i = 0; i < texts.Length; i++) texts[i].ForceMeshUpdate();
        }

        // ── 值同步 / 写回 / 闸门 ──

        /// <summary>把配置里的当前值刷进控件（不改配置）。</summary>
        private void SyncFromConfig()
        {
            foreach (var runtime in _tabs)
            {
                foreach (var row in runtime.Rows)
                {
                    SettingRowBuilder.SyncToggleRow(row.Row, row.Item);
                }
            }
        }

        /// <summary>
        /// 房主闸门。语义照抄原版 <c>UI_LobbyPreset.ApplyHostGate</c> / <c>SetPanelGate</c>
        /// （ACS:57113-57127）：非房主进这个页面时面板半透明且不可交互 —— 原版本身就是只读的。
        /// </summary>
        private void ApplyGate()
        {
            bool isHost = IsHost();

            foreach (var runtime in _tabs)
            {
                if (runtime.Panel == null) continue;
                if (runtime.Gate == null) runtime.Gate = Util.GetOrAddComponent<CanvasGroup>(runtime.Panel);

                runtime.Gate.alpha = isHost ? 1f : 0.4f;
                runtime.Gate.interactable = isHost;
                runtime.Gate.blocksRaycasts = true;   // 与原版一致：仍然吃掉点击，避免穿到下层 UI
            }
        }

        private static bool IsHost()
        {
            try
            {
                return Managers.Host != null && Managers.Host.IsHost;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>用户拨动开关：写配置 + <b>显式落盘</b>。</summary>
        private void OnRowValueChanged(LobbySettingItem item, bool value)
        {
            if (item == null) return;

            try
            {
                var entry = item.BoolEntry != null ? item.BoolEntry() : null;
                if (entry == null)
                {
                    Log.Error($"设置项 [{item.Key}] 的配置项尚未绑定，本次修改被丢弃。");
                    return;
                }

                entry.Value = value;

                // ⚠ 必须显式 Save：复用上游 DT_Tools 的 ConfigFile 时它的 SaveOnConfigSet = false，
                // 只改内存的话症状是"房主改完设置、退出就丢"，且没装上游的人一切正常，最难查
                // （交接文档 §5.2、AGENTS 第 6 条）。
                var config = HideAndSeek.Plugin.HsConfig;
                if (config != null)
                {
                    config.Save();
                }
                else
                {
                    Log.Warn($"设置项 [{item.Key}] 已改内存，但 HsConfig 为 null，本次未能落盘。");
                }

                Log.Info($"设置项 [{item.Key}] = {value}（[{entry.Definition.Section}].{entry.Definition.Key}，已落盘）。");
            }
            catch (Exception ex)
            {
                Log.Error($"写入设置项 [{item.Key}] 失败：\n" + ex);
            }
        }

        // ── 层级查找小工具（取不到就返回 null，调用方各自兜底）──

        private static GameObject FindGameObject(Transform parent, string name)
        {
            Transform child = parent.Find(name);
            return child != null ? child.gameObject : null;
        }

        private static Toggle FindToggle(Transform parent, string name)
        {
            Transform child = parent.Find(name);
            return child != null ? child.GetComponent<Toggle>() : null;
        }

        private static Image FindImage(Transform parent, string name)
        {
            Transform child = parent.Find(name);
            return child != null ? child.GetComponent<Image>() : null;
        }

        private static TMP_Text FindLabel(Transform parent, string name)
        {
            Transform child = parent.Find(name);
            return child != null ? child.GetComponentInChildren<TMP_Text>(true) : null;
        }
    }
}
