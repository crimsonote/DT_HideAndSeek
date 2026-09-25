using Protocol;
using Server.Game;
using GamePlayer = Server.Game.Player;

namespace HideAndSeek.Core
{
    /// <summary>
    /// 全模块**唯一**的聊天/通讯建包入口。
    ///
    /// 为什么要有它：原先全仓 8 处各自 `new S_CHAT_MESSAGE`，其中"给玩家发一条普通聊天回执"
    /// 在两处（鱼锁 / 汽水）**逐字节相同**，另有多处把设备 ID 写成魔数 —— 改一处漏一处。
    /// 现在所有收发口都从这里建包，语义集中在这两个方法里。
    ///
    /// ⚠ **只负责"建包"，不负责"投递"**。要发给谁、走 Broadcast 还是 Session.Send、
    /// 要不要分段/延迟，仍由调用方决定 —— 那是第二步的事（把"阶段 → 第二通道"的规则也收进来）。
    ///
    /// 另一个刻意的不动点：`CommandFeature` 的回复**不接进来**。它的包多带 deviceId 与
    /// 来源频道，语义是"按来源频道回执"，与这里两个入口不同构，硬合会丢语义。
    /// </summary>
    internal static class ChatOut
    {
        /// <summary>
        /// 自定义设备 ID，**避开真实的 ChatDevice**（原版 `RelayDeviceChat` 要求发送者本人
        /// 正占着那台真实设备，用这个假 ID 才能由房主代发）。
        /// </summary>
        internal const int MagicDeviceId = 999999;

        /// <summary>
        /// 广播类建包：弹泡（SecretChat）/ 发信机（DeviceChat）。
        ///
        /// `DeviceId` 只在 SecretChat / DeviceChat 下取 <see cref="MagicDeviceId"/>，
        /// 其余频道为 0 —— 与原有实现逐字一致。
        /// </summary>
        internal static S_CHAT_MESSAGE Broadcast(string text, EChatType chatType) => new S_CHAT_MESSAGE
        {
            Type = chatType,
            Text = text,
            PlayerId = 0,
            DeviceId = (chatType == EChatType.SecretChat || chatType == EChatType.DeviceChat)
                       ? MagicDeviceId
                       : 0,
            Time = (int)(TimeManager.Instance?.SurviveTime ?? 0f)
        };

        /// <summary>
        /// 给**某一个玩家**发普通聊天（进他的聊天栏）。发信人是他自己，所以带上他的 PlayerId；
        /// `IsDead = false` 表示这条不是死亡播报（原版据此走不同显示）。
        ///
        /// 这个方法合掉了原先鱼锁与汽水各写一遍、逐字节相同的两份实现。
        /// </summary>
        internal static S_CHAT_MESSAGE ToPlayer(GamePlayer player, string text) => new S_CHAT_MESSAGE
        {
            Type = EChatType.NormalChat,
            DeviceId = 0,
            Text = text,
            PlayerId = player?.PublicInfo?.PlayerId ?? 0,
            Time = (int)(TimeManager.Instance?.SurviveTime ?? 0f),
            IsDead = false
        };
    }
}
