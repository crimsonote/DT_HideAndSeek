using System;

namespace HideAndSeek.Core
{
    /// <summary>
    /// 标记一个功能补丁类。PatchLoader 扫描此特性：按 <see cref="Section"/> 生成 Enabled 项，
    /// 并按 DefaultEnabled 决定是否在本机挂载补丁。
    /// 同段子项用 <see cref="ConfigFieldAttribute"/> 声明，由 ConfigBinder 自动 Bind。
    ///
    /// 写法与 DT_Tools 的声明式功能保持同构（本插件独立实现，不引用其程序集）。
    /// </summary>
    [AttributeUsage(AttributeTargets.Class, Inherited = false)]
    public sealed class PatchFeatureAttribute : Attribute
    {
        /// <summary>配置段名（写入 .cfg 的 [Section]），请保持稳定以免旧配置失效。</summary>
        public string Section { get; }

        /// <summary>Enabled 项的说明（写入 .cfg 注释）。</summary>
        public string Description { get; }

        /// <summary>默认是否启用该功能。</summary>
        public bool DefaultEnabled { get; }

        /// <summary>作用面（Client / Host / Both），用于文档与 UI 标签。</summary>
        public FeatureSide Side { get; }

        /// <summary>作者；有值时在 .cfg 注释顶部生成 Author 行。</summary>
        public string Author { get; }

        public PatchFeatureAttribute(
            string section,
            string description,
            bool defaultEnabled = false,
            FeatureSide side = FeatureSide.Host,
            string author = null)
        {
            Section = section ?? throw new ArgumentNullException(nameof(section));
            Description = description ?? "";
            DefaultEnabled = defaultEnabled;
            Side = side;
            Author = author;
        }
    }
}
