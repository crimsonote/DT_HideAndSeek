using System.Linq;
using Protocol;
using GamePlayer = Server.Game.Player;

namespace HideAndSeek.Core
{
    /// <summary>
    /// 各功能共用的游戏状态判定。放在 Core 下，避免功能之间横向引用。
    /// </summary>
    internal static class GameRefs
    {
        /// <summary>
        /// 是否属于"露娜系"：本人是露娜，或被 RuleBreaker 偷到露娜技能（Catastrophe）的玩家。
        /// 对应客户端 MyPlayer.HasLunaShield（:14414）里除黑灯判断外的两条。
        /// </summary>
        /// <summary>
        /// 是否属于"露娜系"：本人是露娜，或被 RuleBreaker 偷到露娜技能（Catastrophe）的玩家。
        /// 对应客户端 MyPlayer.HasLunaShield（:14414）里除黑灯判断外的两条。
        ///
        /// ★ "偷到的人也算露娜系"是**刻意的**，而且两个方向都用到：
        ///   · 能力判定：他也该有露娜护盾（LunaImmunityFeature 正靠这条）
        ///   · **胜负判定**：BlackWinFeature 的"非露娜系白方全部淘汰"也靠这条 ——
        ///     小偷偷到露娜能力后，场上就不再有"普通白方"，黑方胜利条件当场成立。
        /// </summary>
        public static bool IsLunaSide(GamePlayer player)
        {
            if (player == null)
                return false;

            var character = Managers.Data?.CharacterDic?.Values
                .FirstOrDefault(x => x.DataId == player.CharacterId);
            if (character != null && character.Type == ECharacterType.Luna)
                return true;

            return player.SkillComponent?.Data?.Type == ESkillType.Catastrophe;
        }

        /// <summary>
        /// 服务端视角的"真的黑灯"：以区域真实光照为准。
        /// 这一点很关键 —— 给黑方单发的假黑灯（BlackVisionFeature）不会改变服务端 Area.IsLight，
        /// 因此"假黑灯"与"真停电"在此可以严格区分。
        /// </summary>
        public static bool IsRealBlackout(GamePlayer attacker)
            => attacker?.CurrentArea != null && !attacker.CurrentArea.IsLight;
    }
}
