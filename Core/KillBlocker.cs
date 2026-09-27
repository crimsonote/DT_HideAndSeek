using HarmonyLib;
using Protocol;
using Server.Game;
using GamePlayer = Server.Game.Player;

namespace HideAndSeek.Core
{
    /// <summary>
    /// 「吞刀」的统一出口 —— 复刻原版拒绝一次攻击时的动作。
    ///
    /// 为什么要抽出来：<c>LunaImmunityFeature</c>（露娜免疫）与 <c>FirstDeathProtectFeature</c>
    /// （首刀保护）都要在 <c>GamePlayer.UseWeapon</c> 的 Prefix 里拒绝一次攻击，
    /// 而"收刀 + 失败音效 + 日志"这三步原本是逐字拷贝的两份。
    /// 判据与生命周期两边**本质不同**（一个是角色属性、一个是跨局状态），所以只共用这一段。
    ///
    /// 音效为什么用 <c>FailedSfx</c>：原版护盾音效 <c>RunaShieldSfx</c> 是客户端
    /// <c>Managers.Sound.PlaySystem("RunaShieldSfx")</c>（ACS:13640）按**名字**播的，
    /// 它不在 <c>ESoundType</c> 枚举里 ⇒ 服务端（<c>SendSystemSFX</c> 只吃枚举）播不了。
    /// </summary>
    internal static class KillBlocker
    {
        /// <summary>
        /// 拒绝一次攻击：收刀 + （可选）给攻击者播失败音效 + 统一格式的日志。
        /// </summary>
        /// <param name="tag">日志前缀，用功能名（如 <c>LunaImmunity</c> / <c>FirstDeathProtect</c>）。</param>
        /// <param name="playSfx">是否播放失败音效（各功能自己的配置项决定）。</param>
        /// <param name="extra">附加到日志末尾的补充信息（可为 null）。</param>
        public static void Reject(
            GamePlayer attacker,
            GamePlayer target,
            string tag,
            bool playSfx,
            string extra = null)
        {
            // 复刻原版拒绝路径的收刀动作
            AccessTools.Method(typeof(GamePlayer), "HolsterWeaponAfterSwing")?.Invoke(attacker, null);

            if (playSfx)
            {
                try
                {
                    GameRoom.Instance?.SendSystemSFX(ESoundType.FailedSfx, attacker);
                }
                catch (global::System.Exception ex)
                {
                    Plugin.Log.LogWarning($"[HS] {tag}：失败音效播放异常 — {ex.Message}");
                }
            }

            int attackerPid = attacker?.PublicInfo?.PlayerId ?? 0;
            int targetPid = target?.PublicInfo?.PlayerId ?? 0;
            Plugin.Log.LogInfo($"[HS] {tag}：拦截 #{attackerPid} → #{targetPid}{extra}");
        }
    }
}
