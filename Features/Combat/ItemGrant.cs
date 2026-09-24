using Server.Game;
using GamePlayer = Server.Game.Player;

namespace HideAndSeek.Features.Combat
{
    /// <summary>
    /// 把物品发给玩家。提灯与汽水共用 —— 两处都要"手上已有东西时落在脚下"，
    /// 逻辑一模一样，写两遍迟早会分叉。
    /// </summary>
    internal static class ItemGrant
    {
        /// <summary>
        /// 发放。<paramref name="dropped"/> 表示因为手上已有物品而落在了脚下。
        ///
        /// 注意**不能**一律用 <c>CreateAndInsertInven</c>：它的内部（ItemManager.InsertInven :172713）
        /// 会先 <c>DropItem</c> 把玩家原本手上的东西丢到地上、再把新物品塞进手 ——
        /// 那是"挤掉玩家手上的东西"，与需求"道具落地、手上的不动"正好相反。
        /// </summary>
        internal static bool Give(GamePlayer player, int dataId, out bool dropped)
        {
            dropped = false;

            var manager = ItemManager.Instance;
            if (manager == null || player?.PublicInfo == null)
                return false;

            // HandItemObjectId != -1 才是"手上有东西"的准确判据：
            // 手机(4001) 走 ObjectId 语义、此时 Hand 为空，用 player.Hand 判会漏。
            if (player.HandItemObjectId != -1)
            {
                manager.CreateAndDropItem(dataId, player.PublicInfo.Pos);
                dropped = true;
            }
            else
            {
                manager.CreateAndInsertInven(player, dataId);
            }

            return true;
        }
    }
}
