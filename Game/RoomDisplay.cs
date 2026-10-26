using Protocol;
using GamePlayer = Server.Game.Player;

namespace HideAndSeek.Game
{
    /// <summary>
    /// 房间显示名。两处以上要读（命令回执 / 传统模式拿刀通告），
    /// 按 AGENTS.md §4 上浮到 <c>Game/</c>，避免各写一份。
    ///
    /// 口径与客户端 <c>:47082</c> 同源：<c>Managers.GetText(ERoomType 名)</c>。
    /// 取不到 <see cref="GamePlayer.CurrentArea"/> 时返回「未知区域」，
    /// 不抛异常 —— 通告/回执不能因为一个未赋值的房间而中断。
    /// </summary>
    internal static class RoomDisplay
    {
        /// <summary>某人此刻所在房间的显示名；未知时返回「未知区域」。</summary>
        public static string AreaNameOf(GamePlayer player)
        {
            try
            {
                var data = player?.CurrentArea?.Data;
                if (data == null)
                    return "未知区域";

                string key = global::System.Enum.GetName(typeof(ERoomType), data.Type);
                string text = Managers.GetText(key);
                return string.IsNullOrEmpty(text) ? key : text;
            }
            catch
            {
                return "未知区域";
            }
        }
    }
}
