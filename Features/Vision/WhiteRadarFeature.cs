using System.Collections.Generic;
using BepInEx.Configuration;
using HarmonyLib;
using Protocol;
using Server.Game;
using HideAndSeek.Core;
using GamePlayer = Server.Game.Player;

namespace HideAndSeek.Features.Vision
{
    /// <summary>
    /// 白方全图雷达：让指定白方在地图上看到"这里有人"。
    ///
    /// 需求侧的硬约束：**只要位置，不要红、不要暴露是谁**。这决定了通道选择 ——
    /// 先搞清两种 pin 是什么：
    ///     白点 = minimap_player.sprite（13×13，纯白 #FFFFFF）
    ///     红点 = minimap_black.sprite（同几何，填 #FF2E70）
    /// 二者由 RefreshPlayerPin（UI_GameScene :74704）绘制，而它前面有硬门控：
    ///     :74673  if (myPlayer.Color == EPlayerColor.White) return;
    /// 即**白方永远进不到这段循环**，服务端也没有"下发白点"的包。
    ///
    /// 于是只有两条路，本功能把两者做成可热切换的 Mode：
    ///
    /// 【Badge】走 S_PIN_MOVE，但用**非玩家 id**（90000+pid）
    ///     客户端 GetPlayerCache(id) 必然为 null → TurnComplyRules 不设头像
    ///     → 徽章保留预制体默认贴图 Louis_Map_White（绿环，非粉红）
    ///     → 且 SetComplyRulesArrow 直接 return，不生成世界箭头、不抢 Kaho 的槽位
    ///     外观 = 白点 + 绿环徽章 + 镜像小箭头。零副作用，不暴露身份。
    ///
    /// 【PureDot】发 S_MODIFY_MY_PLAYER{ChangeColor, 3} 让客户端"以为自己不是白方"
    ///     门控放行 → 原生 RefreshPlayerPin 画出纯净白点；
    ///     且不主动发 S_NOTIFY_BLACK → KnownBlackIds 恒空 → 结构上不可能出现红点。
    ///     ⚠️ 代价（客户端硬编码，绕不开）：StatusWhite 面板消失、目标文本空白、
    ///     **雷达期间无法与武器库交互取武器**；结算前必须复原，否则胜负不记录。
    ///     因此配合 DurationSeconds 做限时脉冲。
    ///
    /// 两个必须守住的边界：
    ///   1) 只在 Survive 下发 —— GetSceneUI&lt;UI_GameScene&gt;() 是 as 转换，审判阶段返回 null → NRE
    ///   2) 阵亡/躲藏/旁观必须**撤销**而不是跳过 —— 白方阵亡瞬间会收到全量 S_NOTIFY_BLACK，
    ///      若 pin 还在就会被 RefreshBlackPin 命中并 TurnBlack（那就成红点了）
    /// </summary>
    [PatchFeature(
        section: "WhiteRadar",
        description: "白方全图雷达：白方地图显示所有存活玩家的位置（不区分阵营，可热切换两种外观方案）。",
        defaultEnabled: true,
        side: FeatureSide.Host)]
    internal static class WhiteRadarFeature
    {
        // 字段名不能叫 Enabled —— 会与 PatchLoader 生成的段级 Enabled 冲突
        [ConfigField(false, "是否启用白方全图雷达。可用控制台 hs_radar on|off，或规则引擎的 Radar 动作改写。")]
        public static ConfigEntry<bool> RadarOn;

        [ConfigField("Badge",
            "外观方案（可热切换，改后下一 tick 生效）：\n" +
            "Badge   = 白点 + 绿环徽章（用非玩家 id 走 S_PIN_MOVE，零副作用，不暴露身份）\n" +
            "PureDot = 纯净白点（发 ChangeColor=3 放行客户端原生绘制；" +
            "副作用：状态面板消失、目标文本空白、雷达期间无法从武器库取武器）")]
        public static ConfigEntry<string> Mode;

        [ConfigField(30, "自动关闭的秒数（0 = 一直开启）。PureDot 模式强烈建议保持限时。", Min = 0f, Max = 600f)]
        public static ConfigEntry<int> DurationSeconds;

        [ConfigField(false, "跳过假人。默认 false —— 测试房里往往只有假人，跳过会导致雷达看起来完全无效。")]
        public static ConfigEntry<bool> SkipDummies;

        /// <summary>非玩家 pin id 的基数：避开真实 PlayerId，让客户端查不到 PlayerCache。</summary>
        internal const int PinIdBase = 90000;

        /// <summary>雷达用的任务类型。避开 38(ScFusebox)/39(ScWeapon)，走 Define.MissionPinType 的其它分支。</summary>
        private const int RadarMissionType = 99;

        /// <summary>PureDot 模式使用的"非白方"枚举值。取未定义的 3，避开 Black(1)/Dark(2) 分支。</summary>
        private const int FakeColorValue = 3;

        private static float _endAt;
        private static bool _pureDotApplied;

        internal static bool IsActive => RadarOn != null && RadarOn.Value;

        private static bool UsePureDot =>
            string.Equals(Mode?.Value?.Trim(), "PureDot", global::System.StringComparison.OrdinalIgnoreCase);

        /// <summary>开关雷达。</summary>
        internal static void SetActive(bool on)
        {
            if (RadarOn == null)
                return;

            if (!on)
            {
                RestorePureDot();
                ClearAllPins();
                RadarOn.Value = false;
                Plugin.Log.LogInfo("[HS] WhiteRadar：已关闭。");
                return;
            }

            RadarOn.Value = true;

            int seconds = DurationSeconds?.Value ?? 0;
            _endAt = seconds > 0 ? (TimeManager.Instance?.SurviveTime ?? 0f) + seconds : 0f;

            Plugin.Log.LogInfo(
                $"[HS] WhiteRadar：已开启（模式 {Mode?.Value}）{(seconds > 0 ? $"，{seconds} 秒后自动关闭" : "")}。");
        }

        /// <summary>撤销所有已下发的 pin（两种模式都要清）。</summary>
        internal static void ClearAllPins()
        {
            var room = GameRoom.Instance;
            if (room?.Players == null)
                return;

            foreach (var white in room.Players)
            {
                if (white?.PublicInfo == null || white.Color != EPlayerColor.White || white.Session == null)
                    continue;

                foreach (var other in room.Players)
                {
                    if (other?.PublicInfo == null)
                        continue;
                    SendPin(white, PinIdBase + other.PublicInfo.PlayerId, null);   // Pos=null → 删除哨兵
                }
            }
        }

        /// <summary>把 PureDot 的假颜色还原成真值。结算/离开对局前必须调用。</summary>
        internal static void RestorePureDot()
        {
            if (!_pureDotApplied)
                return;

            var room = GameRoom.Instance;
            if (room?.Players != null)
            {
                foreach (var white in room.Players)
                {
                    if (white?.Session == null)
                        continue;
                    if (white.Color != EPlayerColor.White && !WasFaked(white))
                        continue;

                    SendColor(white, (int)white.Color);
                }
            }

            _pureDotApplied = false;
            Plugin.Log.LogInfo("[HS] WhiteRadar：PureDot 假颜色已还原。");
        }

        private static readonly HashSet<int> Faked = new HashSet<int>();

        private static bool WasFaked(GamePlayer p)
            => p?.PublicInfo != null && Faked.Contains(p.PublicInfo.PlayerId);

        private static void SendColor(GamePlayer player, int value)
        {
            try
            {
                player.Session?.Send(new S_MODIFY_MY_PLAYER
                {
                    Type = EModifyMyPlayerEvent.ChangeColor,
                    Value = value
                });
            }
            catch (global::System.Exception ex)
            {
                Plugin.Log.LogWarning($"[HS] WhiteRadar：发送颜色失败 — {ex.Message}");
            }
        }

        /// <summary>下发或撤销一个 pin。pos 为 null 表示撤销（(0,0) 是客户端的删除哨兵）。</summary>
        internal static void SendPin(GamePlayer to, int pinId, PosInfo pos)
        {
            try
            {
                // IsForce = true 是关键：客户端 RefreshComplyRulesPin 在 isForce 时走
                // SetLocalPosition（立即定位），否则走 SetTargetPosition（平滑补间）——
                // 而补间靠 LateUpdate 驱动，白方的 LateUpdate 在 :74673（小地图）与
                // :53579（平板）都提前 return，补间永远不会执行，pin 就停在初始位置看不见。
                                // IsForce 必须为 false，与原版 SendTraceTarget 一致：
                //   false → SetTargetPosition(:86096)：position*0.094 - (896,1456)*0.094 + DOLocalMove
                //   true  → SetLocalPosition(:86104)：改走 Util.GetMinimapPosition
                // 两者坐标系不同，传 true 会让位置算错、pin 反而全部不可见。
                // 补间由 DOTween 驱动，不受白方 LateUpdate 早退影响 ——
                // 那个"延迟跟随"正是这段 0.1s DOLocalMove 正常工作的表现。
                to.Session?.Send(new S_PIN_MOVE
                {
                    Type = pinId,
                    Pos = pos ?? new PosInfo(),          // (0,0) = 删除哨兵
                    IsForce = true                      // SetLocalPosition：换算与 SetTargetPosition 完全相同（Util.GetMinimapPosition），且无 0.1s 补间 → 不会飘
                });
            }
            catch (global::System.Exception ex)
            {
                Plugin.Log.LogWarning($"[HS] WhiteRadar：发送 pin 失败 — {ex.Message}");
            }
        }

        [HarmonyPatch(typeof(GameRoom), "SurvivalTick")]
        internal static class TickHook
        {
            [HarmonyPostfix]
            private static void Postfix(GameRoom __instance)
            {
                if (ModeRuntime.Bypass || __instance == null || !IsActive)
                    return;

                // 非 Survive 阶段下发会让客户端 NRE，必须停手并清干净
                if (__instance.State != EGameState.Survive)
                {
                    RestorePureDot();
                    ClearAllPins();
                    return;
                }

                if (_endAt > 0f && (TimeManager.Instance?.SurviveTime ?? 0f) >= _endAt)
                {
                    SetActive(false);
                    return;
                }

                Refresh(__instance);
            }
        }

        private static void Refresh(GameRoom room)
        {
            bool skipDummy = SkipDummies?.Value ?? false;
            bool pureDot = UsePureDot;

            foreach (var white in room.Players)
            {
                if (white?.PublicInfo == null || white.Session == null)
                    continue;
                if (white.Color != EPlayerColor.White || !white.IsAlive || white.IsSpectator)
                    continue;

                if (pureDot)
                {
                    // 让客户端"以为自己不是白方"，放行原生白点绘制
                    if (Faked.Add(white.PublicInfo.PlayerId))
                        SendColor(white, FakeColorValue);
                    _pureDotApplied = true;
                    continue;                             // PureDot 不需要下发 pin
                }

                foreach (var other in room.Players)
                {
                    if (other?.PublicInfo == null)
                        continue;

                    // 死人/躲藏/旁观要**撤销**而不是跳过：尤其阵亡瞬间会收到 S_NOTIFY_BLACK，
                    // pin 若还在就会被 RefreshBlackPin 命中并 TurnBlack
                    // 跳过自己：给白方本人建 pin 会让他看到一个跟着自己延迟移动的点 +
                    // 头上的白色方块（Target 无贴图），而那本来就不是我们要传达的信息。
                    if (other == white)
                        continue;

                    bool visible = other.IsAlive
                        && other.State != EPlayerState.Hide
                        && !other.IsSpectator
                        && !(skipDummy && other.IsDummy);

                    SendPin(white, PinIdBase + other.PublicInfo.PlayerId,
                        visible ? other.PublicInfo.Pos : null);
                }
            }

            // 关掉 PureDot 或人已离开时，把假颜色收回来
            if (!pureDot)
                RestorePureDot();
        }

        // ── 生命周期：任何离开对局的时机都必须复原 ────────────────────
        [HarmonyPatch(typeof(GameRoom), "StartLobby")]
        internal static class LobbyHook
        {
            [HarmonyPostfix]
            private static void Postfix()
            {
                // 回大厅即关闭雷达本身，而不是只还原颜色后让它继续开着
                if (IsActive)
                {
                    ClearAllPins();
                    RadarOn.Value = false;
                }
                RestorePureDot();
                Faked.Clear();
            }
        }

        [HarmonyPatch(typeof(GameRoom), "StartDetective")]
        internal static class DetectiveHook
        {
            [HarmonyPostfix]
            private static void Postfix()
            {
                // 进入审判阶段同样关闭雷达：该阶段下发 S_PIN_MOVE 会让客户端 NRE
                if (IsActive)
                {
                    ClearAllPins();
                    RadarOn.Value = false;
                }
                RestorePureDot();
            }
        }
    }
}
