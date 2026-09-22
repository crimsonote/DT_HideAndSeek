using System;

namespace HideAndSeek.Core
{
    /// <summary>
    /// 标记功能类上的 <c>public static ConfigEntry&lt;T&gt;</c> 字段。
    /// ConfigBinder 在启动时按所属 <see cref="PatchFeatureAttribute.Section"/> 自动 Bind；
    /// 字段名默认即配置键，可用 <see cref="Key"/> 覆盖以保持旧 .cfg 兼容。
    /// </summary>
    [AttributeUsage(AttributeTargets.Field, Inherited = false)]
    public sealed class ConfigFieldAttribute : Attribute
    {
        /// <summary>默认值（类型需与 ConfigEntry&lt;T&gt; 的 T 一致）。</summary>
        public object Default { get; }

        /// <summary>.cfg 注释 / UI 描述。</summary>
        public string Description { get; }

        /// <summary>覆盖配置键；null 时使用字段名。</summary>
        public string Key { get; set; }

        /// <summary>可选下界（仅数值类型生效）。</summary>
        public float Min
        {
            get => _min ?? float.NaN;
            set => _min = value;
        }

        /// <summary>可选上界（仅数值类型生效）。</summary>
        public float Max
        {
            get => _max ?? float.NaN;
            set => _max = value;
        }

        public bool HasMin => _min.HasValue;
        public bool HasMax => _max.HasValue;

        private float? _min;
        private float? _max;

        public ConfigFieldAttribute(object defaultValue, string description = null)
        {
            Default = defaultValue;
            Description = description ?? "";
        }
    }
}
