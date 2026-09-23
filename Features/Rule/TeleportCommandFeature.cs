using BepInEx.Configuration;
using HarmonyLib;
using Protocol;
using Server.Game;
using HideAndSeek.Core;
using GamePlayer = Server.Game.Player;

namespace HideAndSeek.Features.Rule
{
    /// <summary>
    /// 传送命令：传送到**指定或随机玩家在命令执行那一刻的位置**，
    /// 先给对方预警（音效 + 指向传送者的箭头）让他有机会逃离，延迟数秒后才落地。
    ///
    /// 实现要点：
    ///   · 落点在发起时 Clone 成快照 —— 目标之后跑掉也无效，与他"被盯上"的体感一致
    ///   · Player.Move(pos, force: true) 会把位置同步从 S_MOVE 升级为 S_RESPAWN 全服广播，
    ///     客户端立即硬落位（PlayManager.HandleRespawn :31378）；force 还置 MoveLock 100ms
    ///     压掉紧随的旧 C_MOVE 回拉
    ///   · Player.Move **不做任何地图/碰撞校验**（:175883），所以落点必须自己判：
    ///     ValidPosition 必要但不充分，兜底用 ClampToMapBounds
    ///   · PushAfter 不随阶段清空 → 回调内必须自校验 State/IsAlive，
    ///     否则会在结算或侦探阶段把人瞬移
    /// </summary>
    [PatchFeature(
        section: "TeleportCommand",
        description: "传送命令：预警数秒后传送到指定或随机玩家在发起时的位置。",
        defaultEnabled: true,
        side: FeatureSide.Host)]
    internal static class TeleportCommandFeature
    {
        [ConfigField(3000, "预警到落地之间的毫秒数（留给目标的逃跑时间）。", Min = 0f, Max = 15000f)]
        public static ConfigEntry<int> WarnDelayMs;

        [ConfigField(true, "预警时给目标播放警示音效（原版警示用的 WarningSfx）。")]
        public static ConfigEntry<bool> PlayWarningSfx;

        [ConfigField(true, "预警时给目标一个指向传送者的箭头，便于判断该往哪躲。")]
        public static ConfigEntry<bool> ShowWarningArrow;

        /// <summary>
        /// 发起一次传送。targetId &lt;= 0 表示随机挑一个存活玩家。
        /// 返回 false 时 error 给出原因，调用方不应计次数与冷却。
        /// </summary>
        internal static bool Begin(GameRoom room, GamePlayer caster, int targetId, out string error)
        {
            error = null;

            if (room == null || caster == null)
            {
                error = "没有可用的传送者";
                return false;
            }
            if (room.State != EGameState.Survive || !caster.IsAlive)
            {
                error = "当前阶段无法传送";
                return false;
            }

            // 选目标：指定 ID，或从存活玩家里随机（排除自己）
            GamePlayer target = null;

            if (targetId > 0)
            {
                foreach (var p in room.Players)
                {
                    if (p?.PublicInfo != null && p.PublicInfo.PlayerId == targetId)
                    {
                        target = p;
                        break;
                    }
                }
                if (target == null)
                {
                    error = $"没有玩家 #{targetId}";
                    return false;
                }
            }
            else
            {
                int alive = 0;
                foreach (var p in room.Players)
                {
                    if (p?.PublicInfo == null || !p.IsAlive || p == caster)
                        continue;
                    alive++;
                    // 蓄水池抽样：一次遍历即可等概率取一个，无需先建列表
                    if (Util.GetRandomNumber(0, alive) == 0)
                        target = p;
                }
                if (target == null)
                {
                    error = "没有其他存活玩家可作为目标";
                    return false;
                }
            }

            if (target == caster)
            {
                error = "不能传送到自己身上";
                return false;
            }
            if (!target.IsAlive)
            {
                error = $"玩家 #{target.PublicInfo.PlayerId} 已死亡";
                return false;
            }

            // 落点快照 + 安全兜底（Move 不做任何校验）
            var dest = target.PublicInfo?.Pos?.Clone();
            if (dest == null)
            {
                error = "目标位置不可用";
                return false;
            }

            var areas = AreaManager.Instance;
            if (areas != null && !areas.ValidPosition(dest))
                dest = areas.ClampToMapBounds(dest);

            int targetPid = target.PublicInfo.PlayerId;
            var arrowPos = caster.PublicInfo?.Pos?.Clone();
            bool showArrow = ShowWarningArrow?.Value ?? true;

            // ① 立刻预警
            if ((PlayWarningSfx?.Value ?? true))
                room.SendSystemSFX(ESoundType.WarningSfx, target);

            if (showArrow && arrowPos != null && target.Session != null)
            {
                target.Session.Send(new S_NOTIFY_ARROW
                {
                    Type = EArrowType.CharacterArrow,
                    Pos = arrowPos
                });
            }

            Plugin.Log.LogInfo(
                $"[HS] Teleport：黑方 #{caster.PublicInfo?.PlayerId} 锁定 #{targetPid}，" +
                $"{(WarnDelayMs?.Value ?? 3000)}ms 后落地 ({dest.X:F0},{dest.Y:F0})。");

            // ② 延迟落地
            int delay = WarnDelayMs?.Value ?? 3000;
            room.PushAfter(delay < 0 ? 0 : delay, delegate
            {
                // PushAfter 不随阶段清空，必须自校验
                if (room.State != EGameState.Survive || !caster.IsAlive)
                {
                    Plugin.Log.LogInfo("[HS] Teleport：阶段已变化或传送者已死亡，取消落地。");
                    if (showArrow && arrowPos != null && target.Session != null)
                        target.Session.Send(new S_REMOVE_ARROW { Type = EArrowType.CharacterArrow, Pos = arrowPos });
                    return;
                }

                caster.Move(dest, force: true);

                room.BroadcastWorldVFX(EEffectType.TeleportVfx, caster.PublicInfo?.PlayerId ?? 0, caster.PublicInfo?.Pos);

                if (showArrow && arrowPos != null && target.Session != null)
                    target.Session.Send(new S_REMOVE_ARROW { Type = EArrowType.CharacterArrow, Pos = arrowPos });

                Plugin.Log.LogInfo($"[HS] Teleport：已落地到 #{targetPid} 的位置。");
            });

            return true;
        }
    }
}
