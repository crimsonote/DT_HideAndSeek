using BepInEx.Configuration;
using HarmonyLib;
using Protocol;
using Server.Game;
using HideAndSeek.Core;
using HideAndSeek.Features.Vision;      // AoiCullingFeature.ExitRange（预警范围与黑方视野等同）
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

        [ConfigField(false, "传送落地时播放原版的 TeleportVfx（黑洞状特效）。默认关闭 —— 观感突兀。")]
        public static ConfigEntry<bool> ShowTeleportVfx;
        /// <summary>
        /// 落点提示的广播半径。原版黑洞技能用的是 1792f —— 只覆盖现场附近的人，
        /// 而不是全图（此前传 99999f，等于全房都能听到/看到）。
        /// </summary>
        private const float RadarDistance = 1792f;

        [ConfigField("BlackHoleVfx",
            "落点特效（EEffectType 名）：TeleportVfx / BlackHoleVfx / MineBombVfx / FlashVfx / ScopeVfx / none")]
        public static ConfigEntry<string> LandingVfxType;

        [ConfigField("WarningSfx",
            "落点音效（ESoundType 名）：TeleportSfx / WarningSfx / ExplosionSfx / AirHornSfx / BlackholeSfx / none")]
        public static ConfigEntry<string> LandingSfxType;

        [ConfigField(true, "预警期间在落点播一个世界特效（闪光），让目标看清黑方将从哪里出现。")]

        public static ConfigEntry<bool> ShowLandingVfx;

        [ConfigField("黑方即将传送到标记处",
            "预警时发给目标的一行文字（出现在其聊天栏）。留空则不发。")]
        public static ConfigEntry<string> LandingText;
        /// <summary>
        /// 发起一次传送。targetId &lt;= 0 表示随机挑一个存活玩家。
        /// 返回 false 时 error 给出原因，调用方不应计次数与冷却。
        /// </summary>
        /// <summary>
        /// 解析安全落点。目标在柜子 / 游戏机座椅里时不能直接落到他身上 ——
        /// 那种状态下 PublicInfo.Pos 指向容器内部，直接落过去会卡在柜体里。
        /// 复刻 SkillComponent.TryGetSafeLandingPos(:177479)：
        ///   柜子   → cabinet.DeviceData.Positions[0]（出口）
        ///   游戏机 → nintendo.DeviceData.Positions[1]（退场点）
        ///   其余   → 目标当前位置
        /// </summary>
        private static PosInfo ResolveSafeLanding(GamePlayer target)
        {
            if (target?.PublicInfo == null)
                return null;

            // ① 柜子（DeviceManager 是 public，无需反射）
            try
            {
                var cabinet = Server.Game.DeviceManager.Instance?.GetCabinet(target.PublicInfo.PlayerId);
                var cpos = cabinet?.DeviceData?.Positions;
                if (cpos != null && cpos.Count > 0 && cpos[0] != null)
                {
                    Plugin.Log.LogInfo($"[HS] Teleport：目标 #{target.PublicInfo.PlayerId} 在柜子里，落点改用柜子出口。");
                    return cpos[0].Clone();
                }
            }
            catch (global::System.Exception ex)
            {
                Plugin.Log.LogWarning($"[HS] Teleport：查询柜子失败 — {ex.Message}");
            }

            // ② 游戏机座椅（MissionManager 是 internal，走反射）
            try
            {
                var mmType = AccessTools.TypeByName("Server.Game.MissionManager");
                var inst = mmType == null ? null : AccessTools.PropertyGetter(mmType, "Instance")?.Invoke(null, null);
                var list = inst == null ? null : AccessTools.PropertyGetter(mmType, "NintendoList")?.Invoke(inst, null);

                if (list is global::System.Collections.IEnumerable seq)
                {
                    foreach (var item in seq)
                    {
                        if (item == null)
                            continue;

                        var playing = AccessTools.PropertyGetter(item.GetType(), "PlayingPlayer")?.Invoke(item, null);
                        if (!ReferenceEquals(playing, target))
                            continue;

                        var data = AccessTools.PropertyGetter(item.GetType(), "DeviceData")?.Invoke(item, null);
                        var positions = data == null
                            ? null
                            : AccessTools.Field(data.GetType(), "Positions")?.GetValue(data) as global::System.Collections.IList;

                        if (positions != null && positions.Count > 1 && positions[1] is PosInfo p)
                        {
                            Plugin.Log.LogInfo($"[HS] Teleport：目标 #{target.PublicInfo.PlayerId} 在游戏机上，落点改用退场点。");
                            return p.Clone();
                        }
                        break;
                    }
                }
            }
            catch (global::System.Exception ex)
            {
                Plugin.Log.LogWarning($"[HS] Teleport：查询游戏机失败 — {ex.Message}");
            }

            return target.PublicInfo.Pos?.Clone();
        }
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
            // 安全落点：目标可能正在柜子里或游戏机上，那种状态下 PublicInfo.Pos
            // 指向容器内部，直接落过去会卡在柜体里。
            // 复刻原版 SkillComponent.TryGetSafeLandingPos(:177479) 的两条分支。
            var dest = ResolveSafeLanding(target);
            if (dest == null)
            {
                error = "目标位置不可用";
                return false;
            }

            var areas = AreaManager.Instance;
            if (areas != null && !areas.ValidPosition(dest))
                dest = areas.ClampToMapBounds(dest);

            int targetPid = target.PublicInfo.PlayerId;

            // ① 立刻预警
            // 注意：SendSystemSFX(type, player) 会把效果挂在**玩家身上** —— 那不是落点指示。
            // 落点相关的视听提示一律走 BroadcastWorld*（带 Pos 的那几个重载）。
            string sfxName = LandingSfxType?.Value;

            // 旧 .cfg 里可能仍是 TeleportSfx；按需求统一用 WarningSfx（原版警告音）。
            if (!string.IsNullOrEmpty(sfxName)
                && sfxName.Equals("TeleportSfx", global::System.StringComparison.OrdinalIgnoreCase))
            {
                sfxName = "WarningSfx";
            }
            if (!string.IsNullOrEmpty(sfxName)
                && !sfxName.Equals("none", global::System.StringComparison.OrdinalIgnoreCase))
            {
                if (global::System.Enum.TryParse(sfxName, true, out ESoundType sfx))
                {
                    // 预警范围与「黑方地图外边界视野」等同（AoiCulling.ExitRange）——
                    // 原先用 RadarDistance（原版 896），比黑方的实际视野小，会出现
                    // "刚看见黑方冒出来，但警告音没响"的情况。
                    float warnRange = AoiCullingFeature.ExitRange?.Value ?? RadarDistance;
                    try { room.BroadcastWorldSFX(sfx, dest, warnRange); }
                    catch (global::System.Exception ex) { Plugin.Log.LogWarning($"[HS] Teleport：落点音效失败 — {ex.Message}"); }
                }
                else
                {
                    Plugin.Log.LogWarning($"[HS] Teleport：未知音效名 {sfxName}");
                }
            }

            // 落点世界特效：我们无法在客户端渲染世界空间文字，但可以用世界坐标的特效
            // 把"黑方将从哪里出现"直接画在地上。distance 给大值以确保目标一定收到
            // （默认 896 只覆盖附近）。
            // 落点世界特效：客户端无法渲染世界空间文字，所以用世界坐标的特效把
            // "黑方将从哪里出现"画在地上。distance 给大值以确保目标一定收到
            // （默认 896 只覆盖附近）。特效类型可配，便于不进代码就换观感。
            if (ShowLandingVfx == null || ShowLandingVfx.Value)
            {
                string vfxName = LandingVfxType?.Value;
                if (!string.IsNullOrEmpty(vfxName)
                    && !vfxName.Equals("none", global::System.StringComparison.OrdinalIgnoreCase))
                {
                    // 旧 .cfg 里可能是 TeleportVfx —— 它在客户端**没有任何世界坐标渲染分支**
    // （落到 PlayCommonEffect，完全不使用 effect.Pos），deviceId=0 时只打一条
    // Log.Assert。这里是唯一支持"按包里坐标渲染"的类型，故对旧值做一次纠正。
                if (vfxName.Equals("TeleportVfx", global::System.StringComparison.OrdinalIgnoreCase))
                {
                    Plugin.Log.LogWarning("[HS] Teleport：LandingVfxType 为 TeleportVfx（无世界坐标渲染分支），已自动改用 BlackHoleVfx。");
                    vfxName = "BlackHoleVfx";
                }

                if (global::System.Enum.TryParse(vfxName, true, out EEffectType vfx))
                {
                    // 我们自己发的这一发必须屏蔽 TeleportGuardFeature.VfxHook：
                    // 它拦的是"黑洞技能的两发特效"，而 _casterId 是上次施法者 id（≥1）、
                    // 我们传 0 ⇒ 不相等 ⇒ 不会被跳过，pos 会被改写到上一次技能的旧落点。
                    HideAndSeek.Features.Skill.TeleportGuardFeature.Suppress++;
                    try
                    {
                        // 与音效同范围：黑方地图外边界视野（AoiCulling.ExitRange）——
                        // 保证"能看见黑方冒出来的距离"上一定也看得见落点特效。
                        room.BroadcastWorldVFX(vfx, 0, dest, AoiCullingFeature.ExitRange?.Value ?? RadarDistance);
                        Plugin.Log.LogInfo($"[HS] Teleport：落点特效 {vfx} @ ({dest.X:F0},{dest.Y:F0})。");
                    }
                    catch (global::System.Exception ex)
                    {
                        Plugin.Log.LogWarning($"[HS] Teleport：落点特效失败 — {ex.Message}");
                    }
                    finally
                    {
                        HideAndSeek.Features.Skill.TeleportGuardFeature.Suppress--;
                    }
                }
                    else
                    {
                        Plugin.Log.LogWarning($"[HS] Teleport：未知特效名 {vfxName}");
                    }
                }
            }

            // 文字提示走目标自己的聊天栏（NormalChat 单人送达）
            string landingText = LandingText?.Value;
            if (!string.IsNullOrEmpty(landingText) && target.Session != null)
            {
                try
                {
                        // 生存阶段 NormalChat 不渲染（等于白发），改用：
                        //   SecretChat → 弹泡（发给被传送的目标本人）
                        //   DeviceChat → 进他的公共发信机存档
                        // 设备 ID 由 Core/ChatOut 统一给（避开真实 ChatDevice，显示为无署名系统消息）。
                        foreach (var ct in new[] { EChatType.SecretChat, EChatType.DeviceChat })
                        {
                            target.Session.Send(ChatOut.Broadcast(landingText, ct));
                        }
                }
                catch (global::System.Exception ex)
                {
                    Plugin.Log.LogWarning($"[HS] Teleport：落点文字失败 — {ex.Message}");
                }
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
                    return;
                }

                caster.Move(dest, force: true);

                if (ShowTeleportVfx != null && ShowTeleportVfx.Value)
                    room.BroadcastWorldVFX(EEffectType.TeleportVfx, caster.PublicInfo?.PlayerId ?? 0, caster.PublicInfo?.Pos);


                Plugin.Log.LogInfo($"[HS] Teleport：已落地到 #{targetPid} 的位置。");
            });

            return true;
        }
    }
}
