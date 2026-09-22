namespace HideAndSeek.Core
{
    /// <summary>
    /// 功能作用面：影响谁、在什么角色下生效。
    /// 与 DT_Tools 的同名枚举保持一致的语义，便于对照阅读双方代码。
    /// </summary>
    public enum FeatureSide
    {
        /// <summary>仅本地客户端逻辑。</summary>
        Client,

        /// <summary>仅房主/主机逻辑（本模块绝大多数功能属此列）。</summary>
        Host,

        /// <summary>客户端与主机均涉及。</summary>
        Both
    }
}
