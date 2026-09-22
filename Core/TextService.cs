using System.Text;

namespace HideAndSeek.Core
{
    /// <summary>
    /// 播报文本模板处理。文本全部来自配置段，便于运行时修改。
    /// 支持 <c>{key}</c> 占位符替换，以及配置里书写的字面量 <c>\n</c> 转真实换行
    /// （BepInEx 的 .cfg 单行值里不便直接写换行）。
    /// </summary>
    internal static class TextService
    {
        public static string Format(string template, params (string Key, string Value)[] tokens)
        {
            if (string.IsNullOrEmpty(template))
                return string.Empty;

            var sb = new StringBuilder(template);

            if (tokens != null)
            {
                foreach (var (key, value) in tokens)
                    sb.Replace("{" + key + "}", value ?? string.Empty);
            }

            return sb.Replace("\\n", "\n").ToString();
        }
    }
}
