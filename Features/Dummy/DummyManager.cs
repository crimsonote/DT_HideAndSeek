using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using HarmonyLib;
using Protocol;
using Server;
using Server.Game;
using HideAndSeek.Core;
using GamePlayer = Server.Game.Player;
using GameRoom = Server.Game.GameRoom;

namespace HideAndSeek.Features.Dummy
{
    /// <summary>
    /// 假人玩家的创建与管理（房主端，测试用）。
    ///
    /// 关键设计依据（0.1.14b，均经源码核对）：
    ///   - **不复用 GameRoom.HandleEnterPlayer**：它开头用 Steam 大厅成员数据做 build 比对
    ///     （:170480/:170512），合成 session 的 SteamId=0 取不到 member data，必然被判
    ///     ErrorBuildMismatch 并连锁 HandleLeavePlayer。正确范本是主机迁移的 RestorePlayers
    ///     （:173928）：手工 new Player + Players.Add。
    ///   - `session.PlayerID` 必须在构造 Player **之前**设好 —— Player 构造里
    ///     `AccountID = session.PlayerID`（:175716），否则 AccountID 为 null。
    ///   - `session.SteamId` 保持 CSteamID(0)：这是"不向官方服上报"的天然开关
    ///     （CosmeticSightingReporter.Record 要求 GetRosterSteamId != 0，:33019/:30989），
    ///     同时避开 P2P.All 遍历与 HandlePeerDisconnect 误匹配。
    ///   - `Ready = true` 必须设：HandleStart 的 CheckAllPlayerReady（:170999）不通过
    ///     会让房主按开始毫无反应，现象极易误诊。
    ///   - `CharacterId` 必须是合法值（102..113）：客户端 Trial UI 用
    ///     `CharacterDic[player.PublicInfo.CharacterId].Name` 裸下标（:72903/:73249），
    ///     留 -1 会抛 KeyNotFoundException。且必须走 CharacterId setter（:175506），
    ///     它才会 AllocateSkill + 广播。
    ///   - 人数上限 12：GameStart（:170080）洗牌后用 `Players[i]` 直接取
    ///     `StartPosList[i]`，而该表实测只有 12 项，越界即 ArgumentOutOfRangeException。
    ///   - `ConvertToDummy()`（:175653）：IsDummy 的假人会被 CompleteWaitCount（:169798）、
    ///     投票/跳过计数（:179342/:178779）、上行 watchdog 自动排除，阶段切换零延迟；
    ///     而 UseWeapon（:176263）里**没有任何 IsDummy 过滤**，所以它照样能被刀死。
    ///     代价是 StartLobby（:170202-170250）每局回大厅会清掉所有 IsDummy，
    ///     因此 DummyFeature 里另有重建钩子。
    /// </summary>
    internal static class DummyManager
    {
        /// <summary>MapData.StartPosList 的实测条数（12）。超过它 GameStart 会越界。</summary>
        private const int StartPositionCapacity = 12;

        private static readonly List<int> SpawnedIds = new List<int>();
        private static int _seq;

        /// <summary>假人 ID → 期望角色 ID。用于在选角阶段替他"选人"。</summary>
        private static readonly Dictionary<int, int> DesiredCharacter = new Dictionary<int, int>();

        public static IReadOnlyList<int> Ids => SpawnedIds;

        public static int ActiveCount => SpawnedIds.Count;

        /// <summary>查某假人当前的实际角色 ID（0 = 未知）。</summary>
        public static int GetCharacterId(int id)
        {
            var room = GameRoom.Instance;
            var p = room?.Players?.FirstOrDefault(x =>
                x?.PublicInfo != null && x.PublicInfo.PlayerId == id);
            return p?.CharacterId ?? 0;
        }

        /// <summary>造一个假人。wantedId&lt;=0 时自动取空位；charaId&lt;=0 时按配置或随机。</summary>
        public static bool Spawn(int wantedId, int charaId, out int actualId, out string error)
        {
            actualId = 0;
            error = null;

            var room = GameRoom.Instance;
            if (room == null)
            {
                error = "当前没有活动房间";
                return false;
            }

            var seats = ObjectUtils.PlayerSeatList;
            if (seats == null || seats.Count == 0)
            {
                error = "座位表尚未初始化（还没进过大厅）";
                return false;
            }

            // 真人数 + 已有假人数 必须留出出生点，否则 GameStart 越界
            int realCount = room.Players.Count(p => !p.IsDummy);
            if (realCount + SpawnedIds.Count >= StartPositionCapacity)
            {
                error = $"人数已达出生点上限 {StartPositionCapacity}（MapData.StartPosList 只有这么多）";
                return false;
            }

            // 占座：指定 ID 或取第一个空位
            int id = wantedId;
            if (id <= 0 || id > seats.Count || seats[id - 1])
            {
                id = -1;
                for (int i = 0; i < seats.Count; i++)
                {
                    if (!seats[i]) { id = i + 1; break; }
                }
                if (id < 0)
                {
                    error = "没有空闲座位（上限 16）";
                    return false;
                }
            }
            seats[id - 1] = true;

            try
            {
                // 不显式写 SteamId：CSteamID 是 struct，保持默认即全零（= SteamID 0），
                // 这样也无需在本项目引用 Steamworks 程序集。SteamId=0 正是
                // "不向官方服上报外观"的天然开关。
                var session = new HostPeerSession(null)
                {
                    PlayerID = "hs-dummy-" + id + "-" + (++_seq)        // 必须在构造 Player 前设好
                };

                string name = (DummyFeature.NamePrefix?.Value ?? "假人") + id;
                var player = new GamePlayer(id, session, name);
                session.Player = player;

                // 先定角色，再把它并进「已拥有」列表 —— 否则未解锁角色（如露娜 103）
                // 不在 DEFAULT_OWNED_CHARACTER_IDS 里，会被后续逻辑当作"未拥有"而拒绝。
                int resolved = ResolveCharacter(charaId, SpawnedIds.Count);
                var owned = new List<int>(Define.DEFAULT_OWNED_CHARACTER_IDS);
                if (resolved > 0 && !owned.Contains(resolved))
                    owned.Add(resolved);
                player.OwnedCharacterIds = owned;
                player.CharacterId = resolved;                          // 大厅阶段先给个合法值（避免 Trial UI 裸下标）
                DesiredCharacter[id] = charaId > 0 ? charaId : -2;   // -2 = 游戏内置「随机」选项（:171152），由游戏在选角结束后统一分配，不会重复                        // 记下来，选角阶段再正式选
                player.Ready = true;                                    // 否则开始键没反应

                room.Players.Add(player);
                // 对局中途生成时也必须计入存活，否则 AliveCount / 胜负判定看不到它
                if (room.State == EGameState.Survive || room.State == EGameState.Detective)
                    room.AlivePlayers.Add(player);
                InvokeInitLobby(player);
                InvokeMarkRosterDirty(room);

                // 让已有客户端先认识这个人（客户端 _cache 里必须有条目，S_SPAWN 才能实体化）
                var addPacket = new S_ADD_PLAYER
                {
                    PlayerId = id,
                    Name = player.Name,
                    AccountId = player.AccountID,
                    CharacterId = player.CharacterId
                };
                foreach (var other in room.Players)
                {
                    if (other != null && other != player)
                        other.Session?.Send(addPacket);
                }

                // 标记为掉线假人：阶段等待/投票/watchdog 全部自动排除，但仍可被刀死
                player.ConvertToDummy();

                SpawnedIds.Add(id);
                actualId = id;

                Plugin.Log.LogInfo(
                    $"[HS] Dummy：已生成假人 #{id}（{name}，角色 {player.CharacterId}，AccountID={player.AccountID}）。");
                return true;
            }
            catch (Exception ex)
            {
                seats[id - 1] = false;
                error = (ex.InnerException ?? ex).Message;
                Plugin.Log.LogError($"[HS] Dummy：生成假人失败 — {error}");
                return false;
            }
        }

        /// <summary>移除一个假人（释放座位、移出玩家表）。</summary>
        public static bool Remove(int id, out string error)
        {
            error = null;
            var room = GameRoom.Instance;
            if (room == null)
            {
                error = "当前没有活动房间";
                return false;
            }

            var player = room.Players?.FirstOrDefault(p => p?.PublicInfo != null && p.PublicInfo.PlayerId == id);
            if (player == null)
            {
                error = $"没有假人 #{id}";
                return false;
            }

            try
            {
                int seat = player.PublicInfo.PlayerId;
                room.Players.Remove(player);
                room.AlivePlayers.Remove(player);
                room.DeadPlayers.Remove(player);
                InvokeMarkRosterDirty(room);

                var seats = ObjectUtils.PlayerSeatList;
                if (seats != null && seat >= 1 && seat <= seats.Count)
                    seats[seat - 1] = false;

                SpawnedIds.Remove(id);
                DesiredCharacter.Remove(id);          // 否则同一座位号复用时，新假人会沿用旧角色
                Plugin.Log.LogInfo($"[HS] Dummy：已移除假人 #{id}。");
                return true;
            }
            catch (Exception ex)
            {
                error = ex.Message;
                return false;
            }
        }

        /// <summary>清空由本模块生成的假人。</summary>
        public static int Clear()
        {
            int n = 0;
            foreach (int id in SpawnedIds.ToList())
            {
                if (Remove(id, out _))
                    n++;
            }
            SpawnedIds.Clear();
            DesiredCharacter.Clear();
            return n;
        }

        /// <summary>
        /// 把跟踪列表与房间实际状态对齐。
        /// vanilla 的 StartLobby（:170202-170250）会用 HandleLeavePlayer 直接删掉 IsDummy 玩家，
        /// 我们收不到通知，SpawnedIds 会残留失效 ID —— 不先对齐的话，"是否已被清空"的判断永远为假，
        /// 回大厅后就不会重建假人。
        /// </summary>
        public static void ResyncTracking()
        {
            var room = GameRoom.Instance;
            if (room == null)
            {
                SpawnedIds.Clear();
                return;
            }

            SpawnedIds.RemoveAll(id =>
                !room.Players.Any(p => p?.PublicInfo != null && p.PublicInfo.PlayerId == id));
        }

        /// <summary>
        /// 让假人真正走一遍选角。只设 CharacterId 是不够的 —— 那只是大厅阶段的展示值，
        /// 对局内实际使用的角色由选角阶段决定，必须调用 GameRoom.PickCharacter（:171136）。
        /// 顺带满足 CheckPickAllDone（:171188）的人头计数，选角阶段不必空等。
        /// </summary>
        public static void ApplyPickedCharacters(GameRoom room)
        {
            if (room == null || DesiredCharacter.Count == 0)
                return;

            foreach (var kv in DesiredCharacter)
            {
                int id = kv.Key;
                int chara = kv.Value;
                if (chara == 0)
                    continue;

                var player = room.Players.FirstOrDefault(p =>
                    p?.PublicInfo != null && p.PublicInfo.PlayerId == id);
                if (player == null)
                    continue;

                try
                {
                    room.PickCharacter(player, chara);
                    if (player.CharacterId == chara) Plugin.Log.LogInfo($"[HS] Dummy：假人 #{id} 已选角 {chara}。");
                    else Plugin.Log.LogWarning($"[HS] Dummy：假人 #{id} 选角 {chara} 未生效（State={room.State}，当前角色={player.CharacterId}）。");
                }
                catch (global::System.Exception ex)
                {
                    Plugin.Log.LogWarning($"[HS] Dummy：假人 #{id} 选角失败 — {ex.Message}");
                }
            }
        }

        /// <summary>角色别名 → 角色 ID。数字 ID 由调用方先处理。</summary>
        private static readonly (string Key, int Id)[] CharacterAliases =
        {
            ("rin", 102), ("benjamin", 102), ("小熊", 102),
            ("luna", 103), ("露娜", 103),
            ("jeremy", 104), ("杰瑞米", 104),
            ("hasung", 105), ("河成", 105),
            ("kaho", 106), ("红毛", 106),
            ("miyuki", 107), ("美雪", 107),
            ("liliana", 108), ("莉莉安娜", 108),
            ("seol", 109), ("雪", 109),
            ("louis", 110), ("路易斯", 110),
            ("soi", 111),
            ("noel", 112), ("诺艾尔", 112),
            ("lian", 113),
        };

        /// <summary>把角色名（英文或常见中文）解析成角色 ID。</summary>
        public static bool TryParseCharacterName(string text, out int characterId)
        {
            characterId = 0;
            if (string.IsNullOrWhiteSpace(text))
                return false;

            string key = text.Trim().ToLowerInvariant();
            foreach (var alias in CharacterAliases)
            {
                if (alias.Key == key)
                {
                    characterId = alias.Id;
                    return true;
                }
            }
            return false;
        }

        /// <summary>角色清单（供命令展示）。</summary>
        public static string CharacterList()
        {
            var dic = Managers.Data?.CharacterDic;
            var sb = new StringBuilder("{\"ok\":true,\"characters\":[");

            bool first = true;
            var seen = new HashSet<int>();
            foreach (var alias in CharacterAliases)
            {
                if (!seen.Add(alias.Id))
                    continue;
                if (dic == null || !dic.TryGetValue(alias.Id, out var cd) || cd == null)
                    continue;
                if (!first) sb.Append(',');
                first = false;

                sb.Append("{\"id\":").Append(alias.Id)
                  .Append(",\"name\":\"").Append(cd.Name).Append('"')
                  .Append(",\"alias\":\"").Append(alias.Key).Append('"')
                  .Append('}');
            }

            sb.Append("]}");
            return sb.ToString();
        }

        /// <summary>按配置或随机解析一个合法角色 ID（绝不留 -1）。</summary>
        private static int ResolveCharacter(int wanted, int ordinal)
        {
            var dic = Managers.Data?.CharacterDic;

            if (wanted > 0 && dic != null && dic.TryGetValue(wanted, out var cd)
                && cd != null && cd.Type != ECharacterType.Madeline)
                return wanted;

            var configured = DummyFeature.ConfiguredCharacters;
            if (configured.Count > 0)
            {
                int pick = configured[ordinal % configured.Count];
                if (dic != null && dic.TryGetValue(pick, out var c2)
                    && c2 != null && c2.Type != ECharacterType.Madeline)
                    return pick;
            }

            if (dic == null || dic.Count == 0)
                return 102;   // Rin

            // 假人的定位是"模拟真实玩家"，所以要覆盖玩家可能选到的**全部**角色 —— 包含露娜。
            // 露娜免疫普通刀杀是玩法本身的一部分，黑方刀不动她是正常现象，不该回避；
            // 回避了反而测不到"只剩露娜系判黑胜"那条路径。
            // 只排除 Madeline：原版 BuildPickCandidates（:171119）本就不允许选她。
            var ids = dic.Values
                .Where(c => c != null && c.Type != ECharacterType.Madeline)
                .Select(c => c.DataId)
                .ToList();

            return ids.Count == 0 ? 102 : ids[Util.GetRandomNumber(0, ids.Count)];
        }

        // InitLobby / MarkRosterDirty 在发行程序集里是 private，走反射。
        private static void InvokeInitLobby(GamePlayer player)
        {
            var m = AccessTools.Method(typeof(GamePlayer), "InitLobby");
            if (m == null)
            {
                Plugin.Log.LogWarning("[HS] Dummy：找不到 Player.InitLobby，假人的事件委托未绑定（可能不影响测试）。");
                return;
            }
            m.Invoke(player, null);
        }

        private static void InvokeMarkRosterDirty(GameRoom room)
        {
            var m = AccessTools.Method(typeof(GameRoom), "MarkRosterDirty");
            m?.Invoke(room, null);
        }

        public static string ListJson()
        {
            var room = GameRoom.Instance;
            var sb = new StringBuilder();
            sb.Append("{\"ok\":true,\"dummies\":[");

            for (int i = 0; i < SpawnedIds.Count; i++)
            {
                int id = SpawnedIds[i];
                var p = room?.Players?.FirstOrDefault(x => x?.PublicInfo != null && x.PublicInfo.PlayerId == id);

                if (i > 0) sb.Append(',');
                sb.Append("{\"id\":").Append(id)
                  .Append(",\"name\":\"").Append(p?.Name ?? "").Append('"')
                  .Append(",\"charId\":").Append(p?.CharacterId ?? 0)
                  .Append(",\"alive\":").Append((p?.IsAlive ?? false) ? "true" : "false")
                  .Append('}');
            }

            sb.Append("],\"count\":").Append(SpawnedIds.Count).Append('}');
            return sb.ToString();
        }
    }
}
