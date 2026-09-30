using System;

namespace HideAndSeek.Features.Replay
{
    /// <summary>
    /// 回放里的"幕"类型。
    /// ★ **枚举顺序 = 播放顺序**：客户端 `_playTapes` 是 `Dictionary&lt;int,…&gt;`，播放顺序就是 key 顺序。
    ///   现有实现靠 `+1 / +100 / +200` 手工偏移维持这个顺序（脆弱）；重写改为由幕表下标派生 key。
    /// </summary>
    internal enum ActKind
    {
        /// <summary>黑方拿刀那一刻。</summary>
        Knife = 0,
        /// <summary>每一次刀杀（原版自己就会请凶手录一段，我们只是登记）。</summary>
        Kill = 1,
        /// <summary>结算前的"最后时段"，每个存活者各一段。</summary>
        Final = 2,
        /// <summary>白胜：幸存者各一段 —— **同一段时间、视角不同、依次播**（用户口径，不平铺）。</summary>
        Tour = 3,
        /// <summary>黑胜：自爆幕 —— 每个被处决者各一段，**总窗口按人数平铺**（他们同时开始闪）。</summary>
        SelfDestruct = 4,
        /// <summary>黑方收尾（断电视野）。白胜时窗口落在判定之前；黑胜时与爆炸同时。</summary>
        BlackTail = 5,
    }

    /// <summary>这一幕的素材从哪来 —— 决定"能信它多少"（见 docs/回放-规格.md §4）。</summary>
    internal enum TapeOrigin
    {
        /// <summary>还没有素材。</summary>
        None = 0,
        /// <summary>真人客户端回传的磁带：**有设备帧**（门/传送门），但覆盖面只有 AOI 内。</summary>
        Client = 1,
        /// <summary>房主侧合成：无设备帧，但**覆盖面是全员**（代价是采样精度）。</summary>
        HostSynth = 2,
        /// <summary>占位磁带（2 帧）：只为让客户端走一次转场，而不是让这一幕凭空消失。</summary>
        Placeholder = 3,
    }

    /// <summary>
    /// 一幕 —— 回放的最小单位。
    ///
    /// ★ **三个角色必须分开**。现有实现用 `Clip.RecorderId` 一个字段同时表示三者，
    ///   是反复出错的根源（docs/回放-规格.md §5）：
    ///
    ///   · <see cref="RecorderId"/>   —— **谁能提供磁带**。只有真人可能是录制者；假人是 0。
    ///   · <see cref="SubjectId"/>    —— **镜头要拍谁**（本段主角）。相机只能跟着他，原版没有跨房间机位。
    ///   · <see cref="SilhouetteId"/> —— **首帧给谁** = 剪影落点（客户端 `ChangeSilhouette` 只打给 `_blackId`，
    ///                                   而 `_blackId` 由首帧决定）。不能是 SubjectId（他会变黑块），
    ///                                   也不能是 0（"本机用户"的特殊值，各机解释不同、会互相冲突）。
    ///
    /// ★ <see cref="Window"/> 是**绝对时间区间**（房主时钟量纲），**不是**"相对事件的前后秒数"。
    ///   现有实现的两条路各自解释同一组相对偏移、而锚点不同（一个含 `atOffset`、一个不含）
    ///   ⇒ 自爆幕差了 6 秒。绝对区间没有这个自由度：算完就是死的，谁用都一样。
    /// </summary>
    internal sealed class Act
    {
        public ActKind Kind;

        /// <summary>
        /// RecordTime —— 索取磁带、以及客户端 `_playTapes` 的键。
        /// ⚠ **同 key 会互相覆盖**（后到者赢），所以每一幕必须有唯一 key。
        /// 结算时刻 `SurviveTime` 已停走 ⇒ 所有请求会拿到同一个值 ⇒ key 必须由幕表下标派生。
        /// </summary>
        public int Key;

        /// <summary>镜头要拍谁（本段主角）。</summary>
        public int SubjectId;

        /// <summary>谁能提供磁带；<c>0</c> = 没有人能提供 ⇒ 只能房主侧合成（假人就是这种）。</summary>
        public int RecorderId;

        /// <summary>首帧给谁（剪影落点）；<c>0</c> = 还没决定。</summary>
        public int SilhouetteId;

        /// <summary>这一幕抓取的时间区间（房主时钟 = <c>Managers.Game.ClientTime</c> 量纲）。</summary>
        public ReplayWindow.Span Window;

        /// <summary>素材来源。</summary>
        public TapeOrigin Origin = TapeOrigin.None;

        /// <summary>素材是否已就绪。</summary>
        public bool Filled => Origin != TapeOrigin.None;

        /// <summary>这一段的自由备注（写进日志，方便排查）。</summary>
        public string Note;

        public override string ToString()
            => $"{Kind}#{Key} 主角=#{SubjectId} 录制=#{RecorderId} 窗口={Window}";
    }
}
