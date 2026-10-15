using System.Collections.Generic;
using Protocol;
using Server.Game;
using ObjectUtils = Server.ObjectUtils;
using GamePlayer = Server.Game.Player;

namespace HideAndSeek.Core
{
    /// <summary>
    /// 汽水进度条（<c>UI_ShakingSlider</c>）的**唯一**出入口。
    ///
    /// 存在的理由：这个进度条原本只在 <c>MioTeleportFeature</c> 里用（传送时装一下
    /// "剩余次数"），现在小熊破坏也要用它显示耐久度。两边都要的就该抽一层，
    /// 否则 发包序列 + 还原手部 + 打断重申 这套细节会被抄第二遍。
    ///
    /// 客户端为什么会出现进度条（硬约束，与调用方无关）：
    ///   <c>S_ADD_ITEM</c>(:42716) 若物品不是 Weapon ⇒ <c>Inventory.InsertHand</c>(:42741)
    ///   → <c>StartHand</c>(:4774) → <c>if (item.DataId == 1039)</c> ⇒
    ///   ShowItemUI&lt;UI_ShakingSlider&gt; + SetInfo(:4782)。
    ///   ⇒ **只有 1039（SHAKER_BALL）带进度条**；包只单发给本人。
    ///   反过来，光改 <c>PublicInfo.HandItemId</c> 只会让**别人**看到他拿着汽水、
    ///   而他自己的屏幕上没有条。
    ///
    /// 服务端权威手部（<c>Player.Hand</c> / <c>HandItemObjectId</c> /
    /// <c>PublicInfo.HandItemId</c>）全程**不动**，这里动的只有该玩家自己屏幕上的东西：
    ///   · 不写 <c>HandItemObjectId</c> —— 它内部是 BroadcastModifyPlayer(:175550)，会广播给所有人
    ///   · 汽水也不进 <c>ItemManager.Items</c> —— 服务端数据结构零污染
    ///
    /// 刻度：<c>Value</c> 是绝对刻度（0..Max），语义由调用方决定
    /// （MioTeleport 用「剩余次数」，小熊破坏用「剩余耐久度」），本类只负责显示与下发。
    /// </summary>
    internal static class ShakerBar
    {
        /// <summary>PlayerId → 当前正在显示的汽水。</summary>
        private static readonly Dictionary<int, ItemInfo> Active = new Dictionary<int, ItemInfo>();

        /// <summary>当前是否有进度条挂在某玩家屏幕上。</summary>
        public static bool IsActive(int pid) => Active.ContainsKey(pid);

        /// <summary>取出当前刻度（调用方做"值变了才发包"的节流）；没有则返回 -1。</summary>
        public static int Current(int pid)
        {
            ItemInfo info;
            return Active.TryGetValue(pid, out info) && info != null ? info.Value : -1;
        }

        /// <summary>当前汽水的 ObjectId（调试/日志用）；没有则返回 0。</summary>
        public static long ObjectIdOf(int pid)
        {
            ItemInfo info;
            return Active.TryGetValue(pid, out info) && info != null ? info.ObjectId : 0;
        }

        /// <summary>
        /// 在某玩家屏幕上挂出进度条，并置初值。
        ///
        /// 已有时：旧的那条会先被替换（<c>End</c> 的净效果由调用方负责 ——
        /// 正常路径是"先 End 再 Begin"，这里只做防御：直接覆盖并撤回旧的）。
        /// </summary>
        public static bool Begin(GamePlayer player, int itemId, int value)
        {
            if (player?.PublicInfo == null || player.Session == null)
                return false;

            int pid = player.PublicInfo.PlayerId;
            ItemInfo old;
            if (Active.TryGetValue(pid, out old))
                Remove(player, old);          // 防御：同一条不会疊两条

            ItemInfo soda;
            try
            {
                soda = new ItemInfo
                {
                    DataId = itemId,
                    ObjectId = ObjectUtils.GenerateNewId(),   // 全局唯一，避免与真实物品撞 id
                    Value = value
                };
                Active[pid] = soda;
                // 让他本人的客户端真的持有这件汽水 —— 进度条出现的唯一前提
                player.Session.Send(new S_ADD_ITEM { Item = soda });
                return true;
            }
            catch (global::System.Exception ex)
            {
                Active.Remove(pid);
                Plugin.Log.LogWarning($"[HS] ShakerBar：挂进度条失败（{ex.Message}）。");
                return false;
            }
        }

        /// <summary>
        /// 把当前刻度下发给玩家（单发，不重挂物品）。
        ///
        /// ⚠ 不能走 <c>Item.Value</c> 的 setter：它内部读**服务端 Hand**
        /// （<c>SendChangeItemValue</c> :176956），而我们的汽水只是本机幻觉、
        /// 服务端 Hand 根本没变 ⇒ 那样会发出**原物品**的值。
        /// </summary>
        public static void Set(int pid, int value)
        {
            ItemInfo info;
            if (!Active.TryGetValue(pid, out info) || info == null)
                return;
            info.Value = value;

            var player = FindPlayer(pid);
            if (player?.Session == null)
                return;                        // 人已经不在 ⇒ 客户端随之销毁，无需下发

            try
            {
                player.Session.Send(new S_MODIFY_MY_PLAYER
                {
                    Type = EModifyMyPlayerEvent.ChangeItemValue,
                    Value = value
                });
            }
            catch (global::System.Exception ex)
            {
                Plugin.Log.LogWarning($"[HS] ShakerBar：刻度下发失败（{ex.Message}）。");
            }
        }

        /// <summary>
        /// 再把「你手上正拿着这瓶汽水」告诉客户端一次 —— **进度条被打断后的恢复**。
        ///
        /// 什么时候需要：客户端可能在本地先清过手部再发包（最典型的是 <c>DropHand</c>(:4754)：
        /// 它先 <c>InsertHand(null)</c> 才把 <c>C_DROP_ITEM</c> 发出来 —— 哪怕那个包被拦下，
        /// 它自己屏幕上的进度条已经没了）。补发时用的 <c>ItemInfo.Value</c> 是**当前刻度**，
        /// 所以条不会跳回起点，进度看起来是连续的。
        /// </summary>
        public static void Resend(int pid)
        {
            ItemInfo info;
            if (!Active.TryGetValue(pid, out info) || info == null)
                return;

            var player = FindPlayer(pid);
            if (player?.Session == null)
                return;

            try
            {
                player.Session.Send(new S_ADD_ITEM { Item = info });
                player.Session.Send(new S_MODIFY_MY_PLAYER
                {
                    Type = EModifyMyPlayerEvent.ChangeItemValue,
                    Value = info.Value
                });
            }
            catch (global::System.Exception ex)
            {
                Plugin.Log.LogWarning($"[HS] ShakerBar：重申持有失败（{ex.Message}）。");
            }
        }

        /// <summary>
        /// 收尾：撤掉汽水 → 把服务端权威的当前手部重新发给他。
        ///
        /// 服务端权威（<c>Player.Hand</c> / <c>HandItemObjectId</c> /
        /// <c>PublicInfo.HandItemId</c>）全程没动过，所以这里直接读 <c>player.Hand</c>
        /// 作为真值 —— 不需要存档，也不会出现"存档与窗口内发生的其它改动打架"
        /// （例如他在这 1 秒里捡了别的东西）。
        /// 幂等；人已经不在（离开/迁移中）⇒ 客户端随之销毁，无需还原。
        /// </summary>
        public static void End(int pid)
        {
            ItemInfo info;
            if (!Active.TryGetValue(pid, out info))
                return;
            Active.Remove(pid);

            var player = FindPlayer(pid);
            if (player == null || player.Session == null)
                return;

            Remove(player, info);

            // 把他的本地手部对齐回服务端真值（原本空手就不发）
            try
            {
                var hand = player.Hand;
                if (hand != null && hand.Info != null)
                    player.Session.Send(new S_ADD_ITEM { Item = hand.Info });
            }
            catch (global::System.Exception ex)
            {
                Plugin.Log.LogWarning($"[HS] ShakerBar：还原本机手部失败（{ex.Message}）。");
            }
        }

        /// <summary>清掉全部（阶段切换 / 换局）。此时**不**还原手部 —— 阶段一切换，
        /// 客户端手部本来就会被重置，而这里面可能有人已经离开房间。</summary>
        public static void ClearAll()
        {
            Active.Clear();
        }

        /// <summary>当前激活中的玩家数（调试 / <c>hs</c> 总览用）。</summary>
        public static int Count => Active.Count;

        private static void Remove(GamePlayer player, ItemInfo info)
        {
            if (player?.Session == null || info == null)
                return;
            try
            {
                // 客户端 Handle_S_REMOVE_ITEM(:42746) 按 ObjectId 匹配 ⇒ InsertHand(null)
                // → EndHand 关掉进度条 UI
                player.Session.Send(new S_REMOVE_ITEM { ObjectId = info.ObjectId });
            }
            catch (global::System.Exception ex)
            {
                Plugin.Log.LogWarning($"[HS] ShakerBar：撤汽水失败（{ex.Message}）。");
            }
        }

        /// <summary>按 PlayerId 找存活玩家（本工具类自己的查找，不依赖各功能自己的实现）。</summary>
        private static GamePlayer FindPlayer(int pid)
        {
            var room = GameRoom.Instance;
            if (room?.Players == null)
                return null;
            for (int i = 0; i < room.Players.Count; i++)
            {
                var p = room.Players[i];
                if (p?.PublicInfo != null && p.PublicInfo.PlayerId == pid)
                    return p;
            }
            return null;
        }
    }
}
