using HideAndSeek.Core;
using HideAndSeek.Features.Broadcast;
using HideAndSeek.Features.Weapon;

namespace HideAndSeek.Features.UI
{
    /// <summary>
    /// 「捉迷藏」页签的设置项声明 —— <b>新增/删除设置项只改这一个文件</b>。
    ///
    /// 每一项都直接复用已有的配置项，本框架不引入任何新的配置键，也不写任何玩法逻辑：
    /// 开关只负责把值写进对应的 <c>ConfigEntry</c> 并落盘，玩法侧照常读它。
    /// 三个键的默认值一律不动（.cfg 里已有的值优先）。
    ///
    /// 配置项一律用 <c>() =&gt; Xxx.Feature</c> 的<b>惰性委托</b>引用：
    /// 本方法在 <c>Plugin.Start()</c> 里、<c>PatchLoader.Load</c> 之前调用，
    /// 那一刻所有 ConfigEntry 都还是 null（UI 是懒加载的，取值时必然已绑定）。
    ///
    /// ⚠ 段开关与参数开关是两回事：这些键属于各自功能的配置段，
    /// 若该段 <c>[Xxx].Enabled = false</c>（PatchLoader 跳过整段补丁），
    /// 这里改的"参数"要等重启并把段启用后才会真正生效 —— 详见 Features/UI/EXTENDING.md「已知限制」。
    /// </summary>
    internal static class HideAndSeekSettingsContent
    {
        /// <summary>页签顺序权重；原版「难度 / 其他」两页签不在本注册表内，恒在最左侧。</summary>
        public const int TabOrder = 100;

        /// <summary>登记本插件的全部页签与设置项。由 <c>Plugin.Start()</c> 调用一次。</summary>
        public static void RegisterAll()
        {
            SettingRegistry.Register(
                "捉迷藏",
                TabOrder,

                // 捉迷藏模式总开关：段 [HS_Mode].Enabled。
                // 改后即时生效 —— 各补丁入口首行读 ModeRuntime.Bypass 短路，无需重启。
                LobbySettingItem.Toggle("hs_mode", "启用捉迷藏", () => ModeRuntime.Enabled),

                // 开局直接发刀：段 [WeaponGrant].GiveAtStart。
                // 与"自行跑刀"互斥；在 GameRoom.StartSurvive 之后延迟 GrantDelayMs 毫秒发刀。
                LobbySettingItem.Toggle("weapon_grant", "启用开局发刀", () => WeaponGrantFeature.GiveAtStart),

                // 公开黑方身份（角色名 + 玩家昵称）：段 [Broadcast].RevealBlackOnKnife。
                // 注意：这不是播报总开关（那是 [Broadcast].AnnounceEnabled），
                // 只控制"是否把黑方身份通告出去"。
                LobbySettingItem.Toggle("reveal_black", "启用自动通告黑幕", () => BroadcastFeature.RevealBlackOnKnife));
        }
    }
}
