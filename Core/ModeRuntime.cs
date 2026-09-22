using System;
using BepInEx.Configuration;

namespace HideAndSeek.Core
{
    /// <summary>
    /// 模式总开关。作为 extraSection 绑定（不经 [PatchFeature]），
    /// 每个补丁的 Prefix 首行读 <see cref="Bypass"/> 短路，因此关掉模式即时生效、无需重启。
    /// 后续阶段的“关闭回滚”（AOI 还原、光照还原）也挂在这里。
    /// </summary>
    internal static class ModeRuntime
    {
        public const string Section = "HS_Mode";

        public static ConfigEntry<bool> Enabled { get; private set; }

        /// <summary>模式是否启用。</summary>
        public static bool Active => Enabled != null && Enabled.Value;

        /// <summary>补丁入口守卫：模式未启用时一律放行原版逻辑。</summary>
        public static bool Bypass => !Active;

        /// <summary>供 PatchLoader 的 extraSections 使用的绑定动作。</summary>
        public static Action BindAction(ConfigFile config) => () =>
        {
            Enabled = config.Bind(
                Section, "Enabled", false,
                "捉迷藏模式总开关。关闭时所有补丁放行原版逻辑；改后即时生效，无需重启。");
        };
    }
}