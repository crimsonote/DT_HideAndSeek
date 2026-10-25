using System.Collections.Generic;
using System.Linq;
using BepInEx.Configuration;
using HarmonyLib;
using Protocol;
using Server.Game;
using HideAndSeek.Core;
using GamePlayer = Server.Game.Player;
using GameItem = Server.Game.Item;
using GameDeviceManager = Server.Game.DeviceManager;

namespace HideAndSeek.Features.Disguise
{
    /// <summary>
    /// 物品伪装（实验）。
    ///
    /// 目标行为：
    ///   ① 白方从地面捡起物品后，**原地静止 N 秒**（默认 3）⇒ 伪装成该物品；
    ///   ② 手持物**不掉**；自己客户端仍按正常状态显示、可以移动；
    ///   ③ 其他玩家收到的是「隐藏」：看不到人，只看到一个 `ItemHolder` 假物品；
    ///   ④ 假物品跟随玩家位置（纯服务端通过 remove/spawn 跳步跟随）；
    ///   ⑤ 黑方/其他玩家靠近按 E ⇒ 假物品消失、玩家现形；
    ///   ⑥ 移动**不解除**（本版口径）；玩家可继续移动，假物品跟随；
    ///   ⑦ 伪装期间禁止捡别的物品、用物品、丢物品、用技能、操作设备。
    ///
    /// 关键实现：
    ///   · 服务端 `State = Hide` 用于目标判定（刀/DT 选不中）与观察者 despawn；
    ///   · 单独给**本人**发 `S_MODIFY_PLAYER(Idle)`，让本人客户端以为自己是正常态，
    ///     这样客户端会真实移动并发送带新坐标的 `C_MOVE`（真 Hide 时客户端速度被归零）；
    ///   · `GameRoom.HandleMove` 的 Prefix 在 Hide 状态下自行处理移动，并同步假物品；
    ///   · `Player.ModifyPlayer` 忽略伪装者自己的状态变更，避免原版「当前 Hide 拒绝离开」
    ///     把 Hide 回发给本人，破坏客户端本地 Idle；
    ///   · 假物品只是 `DeviceInfo`（`EDeviceType.ItemHolder`），不加入 `ItemManager.ItemHolders`，
    ///     因此正常拾取不会真的拿走它；E 请求由 `AcquireItem` Prefix 拦截。
    /// </summary>
    [PatchFeature(
        section: "ItemDisguise",
        description: "物品伪装：白方捡起物品后原地静止 N 秒，伪装成该物品；手持物不掉，自己可移动，黑方/其他人按 E 翻出。实验功能，默认关闭。",
        defaultEnabled: false,
        side: FeatureSide.Host)]
    internal static class ItemDisguiseFeature
    {
        // ══ 配置 ══════════════════════════════════════════════════════

        [ConfigField(3,
            "捡起物品后需要原地静止多少秒才会伪装。",
            Min = 1f, Max = 10f)]
        public static ConfigEntry<int> StandSeconds;

        [ConfigField(450f,
            "黑方/其他玩家按 E 翻出伪装的最大距离。",
            Min = 100f, Max = 1000f)]
        public static ConfigEntry<float> RevealDistance;

        [ConfigField(0.3f,
            "假物品跟随玩家的刷新间隔（秒）。纯服务端只能 remove/spawn，越小越跟手但越闪。",
            Min = 0.1f, Max = 2f)]
        public static ConfigEntry<float> FollowInterval;

        [ConfigField(48f,
            "假物品跟随的位移阈值（像素）：超过这个距离会立刻同步一次。",
            Min = 8f, Max = 256f)]
        public static ConfigEntry<float> FollowDistance;

        [ConfigField(1.8f,
            "伪装期间给自己刷虚影的间隔（秒）。0 表示不刷。",
            Min = 0.5f, Max = 5f)]
        public static ConfigEntry<float> GhostInterval;

        [ConfigField(5f,
            "解除伪装后的重伪装冷却（秒），防止刚被翻出又立刻蹲下。",
            Min = 0f, Max = 30f)]
        public static ConfigEntry<float> ReDisguiseCooldown;

        [ConfigField(true,
            "是否允许黑方以外的人（包括伪装者自己）按 E 翻出。")]
        public static ConfigEntry<bool> AllowOthersReveal;

        // ══ 状态 ══════════════════════════════════════════════════════

        private const int FakeIdBase = -1000000;
        private const float PositionEpsilon = 0.25f;

        private sealed class DisguiseState
        {
            public GamePlayer Player;
            public int PlayerId;
            public int FakeId;
            public int ItemDataId;
            public DeviceInfo FakeInfo;
            public bool HasFake;
            public PosInfo LastPos;
            public float StableSince;
            public float NextFollow;
            public float NextGhost;
            public bool PrevKeepItemOnHide;
        }

        private static readonly Dictionary<int, DisguiseState> ByPlayer =
            new Dictionary<int, DisguiseState>();

        private static readonly Dictionary<int, DisguiseState> ByFakeId =
            new Dictionary<int, DisguiseState>();

        private static readonly Dictionary<int, float> CooldownUntil =
            new Dictionary<int, float>();

        private static bool Active =>
            !ModeRuntime.Bypass
            && GameRoom.Instance != null
            && GameRoom.Instance.State == EGameState.Survive;

        // ══ 生命周期 ══════════════════════════════════════════════════

        /// <summary>地面物品进背包后开始计时。只处理白方、存活、非 Hide/Sit。</summary>
        private static void BeginArming(GamePlayer player)
        {
            if (!Active || player == null || player.PublicInfo == null)
                return;
            if (!player.IsAlive || player.IsSpectator || player.IsDummy)
                return;
            if (player.Color == EPlayerColor.Black || player.Color == EPlayerColor.Dark)
                return;
            if (player.State == EPlayerState.Hide || player.State == EPlayerState.Sit)
                return;
            if (player.Hand == null)
                return;

            int pid = player.PublicInfo.PlayerId;
            if (CooldownUntil.TryGetValue(pid, out float until) && UnityEngine.Time.time < until)
                return;

            int dataId = player.Hand.Info.DataId;
            if (Managers.Data?.ItemDic == null || !Managers.Data.ItemDic.ContainsKey(dataId))
                return;

            if (!ByPlayer.TryGetValue(pid, out DisguiseState st))
            {
                st = new DisguiseState { PlayerId = pid };
                ByPlayer[pid] = st;
            }

            // 已经伪装的人不会再进入 Arming（AcquireItem 的 Prefix 会拦掉别的拾取）。
            if (st.HasFake)
                return;

            st.Player = player;
            st.ItemDataId = dataId;
            st.LastPos = player.PublicInfo.Pos.Clone();
            st.StableSince = UnityEngine.Time.time;
            Plugin.Log.LogInfo(
                $"[HS] ItemDisguise：#{pid} 捡到物品 data={dataId}，开始 {StandSeconds.Value} 秒静止计时。");
        }

        /// <summary>静止足够久 ⇒ 进入伪装。</summary>
        private static void StartDisguise(DisguiseState st)
        {
            GamePlayer player = st.Player;
            if (player == null || !player.IsAlive || player.Hand == null)
            {
                RemoveState(st);
                return;
            }
            if (player.State != EPlayerState.Idle && player.State != EPlayerState.Run)
            {
                RemoveState(st);
                return;
            }
            if (Managers.Data?.ItemDic == null || !Managers.Data.ItemDic.ContainsKey(st.ItemDataId))
            {
                RemoveState(st);
                return;
            }

            st.PrevKeepItemOnHide = player.KeepItemOnHide;
            st.FakeId = FakeIdBase - st.PlayerId;
            st.HasFake = true;

            // 服务端逻辑状态：Hide。StartState(Hide) 会锁技能、从观察者列表移除；
            // KeepItemOnHide 必须提前置 true，否则手持物会被 DropItem。
            player.KeepItemOnHide = true;
            player.State = EPlayerState.Hide;

            // 骗本人客户端：让它以为自己是 Idle，这样它会真实移动并发送 C_MOVE。
            SendLocalNormal(player);

            CreateFakeDevice(st);
            ByFakeId[st.FakeId] = st;
            st.NextFollow = UnityEngine.Time.time + 0.1f;
            st.NextGhost = UnityEngine.Time.time;

            Plugin.Log.LogInfo(
                $"[HS] ItemDisguise：#{st.PlayerId} 伪装成物品 data={st.ItemDataId}，假 DeviceId={st.FakeId}。");
        }

        /// <summary>创建假 ItemHolder。只加 DeviceInfo，不加入 ItemManager.ItemHolders。</summary>
        private static void CreateFakeDevice(DisguiseState st)
        {
            GamePlayer player = st.Player;
            if (player == null)
                return;

            var info = new DeviceInfo
            {
                Type = EDeviceType.ItemHolder,
                DeviceId = st.FakeId,
                Pos = player.PublicInfo.Pos.Clone()
            };
            info.StateList.Add(st.ItemDataId);
            st.FakeInfo = info;

            Area area = AreaManager.Instance?.GetArea(info.Pos);
            area?.Info.DeviceInfos.Add(info);
            GameRoom.Instance.Broadcast(new S_SPAWN_DEVICE { Device = info });
        }

        /// <summary>移除假物品（如果有）。</summary>
        private static void RemoveFake(DisguiseState st)
        {
            if (st == null || !st.HasFake || st.FakeInfo == null)
                return;

            st.HasFake = false;
            DeviceInfo info = st.FakeInfo;
            st.FakeInfo = null;
            ByFakeId.Remove(st.FakeId);

            Area area = AreaManager.Instance?.GetArea(info.Pos);
            area?.Info.DeviceInfos.Remove(info);
            if (GameRoom.Instance != null)
                GameRoom.Instance.Broadcast(new S_REMOVE_DEVICE { DeviceId = info.DeviceId });
        }

        /// <summary>同步假物品位置。纯服务端只能 remove + spawn，按间隔/距离节流。</summary>
        private static void SyncFake(DisguiseState st, bool force)
        {
            if (st == null || !st.HasFake || st.FakeInfo == null || st.Player == null)
                return;

            PosInfo playerPos = st.Player.PublicInfo.Pos;
            float dx = st.FakeInfo.Pos.X - playerPos.X;
            float dy = st.FakeInfo.Pos.Y - playerPos.Y;
            float threshold = FollowDistance.Value;
            bool farEnough = dx * dx + dy * dy >= threshold * threshold;
            if (!force && !farEnough && UnityEngine.Time.time < st.NextFollow)
                return;

            Area oldArea = AreaManager.Instance?.GetArea(st.FakeInfo.Pos);
            oldArea?.Info.DeviceInfos.Remove(st.FakeInfo);
            GameRoom.Instance.Broadcast(new S_REMOVE_DEVICE { DeviceId = st.FakeId });

            st.FakeInfo.Pos = playerPos.Clone();
            Area newArea = AreaManager.Instance?.GetArea(st.FakeInfo.Pos);
            newArea?.Info.DeviceInfos.Add(st.FakeInfo);
            GameRoom.Instance.Broadcast(new S_SPAWN_DEVICE { Device = st.FakeInfo });

            st.NextFollow = UnityEngine.Time.time + FollowInterval.Value;
        }

        /// <summary>解除伪装：移除假物品、恢复服务端 Idle、保持手持物、重新 spawn。</summary>
        private static void Reveal(DisguiseState st, string reason)
        {
            if (st == null)
                return;

            int pid = st.PlayerId;
            ByPlayer.Remove(pid);
            RemoveFake(st);

            GamePlayer player = st.Player;
            if (player != null && player.Session != null)
            {
                // ⚠ 顺序不能反：必须在 KeepItemOnHide 仍为 true 时切 Idle，
                // 否则 EndState(Hide) 会把刚保住的手持物掉出来。
                if (player.IsAlive && player.State == EPlayerState.Hide)
                    player.State = EPlayerState.Idle;

                player.KeepItemOnHide = st.PrevKeepItemOnHide;

                if (player.IsAlive)
                    player.Move(player.PublicInfo.Pos);
            }

            CooldownUntil[pid] = UnityEngine.Time.time + ReDisguiseCooldown.Value;

            Plugin.Log.LogInfo(
                $"[HS] ItemDisguise：#{pid} 解除伪装（{reason}）。");
        }

        /// <summary>只清理状态，不恢复玩家逻辑状态（用于死亡/离开）。</summary>
        private static void RemoveState(DisguiseState st)
        {
            if (st == null)
                return;

            ByPlayer.Remove(st.PlayerId);
            RemoveFake(st);
            if (st.Player != null)
                st.Player.KeepItemOnHide = st.PrevKeepItemOnHide;
        }

        private static void ClearAll()
        {
            if (ByPlayer.Count == 0)
                return;

            foreach (DisguiseState st in ByPlayer.Values.ToList())
            {
                RemoveFake(st);
                GamePlayer player = st.Player;
                if (player != null && player.Session != null)
                {
                    if (player.IsAlive && player.State == EPlayerState.Hide)
                        player.State = EPlayerState.Idle;
                    player.KeepItemOnHide = st.PrevKeepItemOnHide;
                }
            }

            ByPlayer.Clear();
            ByFakeId.Clear();
            CooldownUntil.Clear();
        }

        private static void SendLocalNormal(GamePlayer player)
        {
            player.Session.Send(new S_MODIFY_PLAYER
            {
                PlayerId = player.PublicInfo.PlayerId,
                Type = EModifyPlayerEvent.ChangePlayerState,
                Value = (int)EPlayerState.Idle
            });
        }

        /// <summary>自嗨虚影：只发给本人，客户端会把它挂到 MyPlayer 身上。</summary>
        private static void PulseGhost(DisguiseState st)
        {
            GamePlayer player = st.Player;
            if (player?.Session == null)
                return;

            player.Session.Send(new S_PLAY_EFFECT
            {
                Type = EEffectType.ShadowPartnetVfx,
                DeviceId = player.PublicInfo.PlayerId,
                Pos = player.PublicInfo.Pos.Clone()
            });
            st.NextGhost = UnityEngine.Time.time + GhostInterval.Value;
        }

        private static bool IsBlack(GamePlayer player)
        {
            return player != null
                && (player.Color == EPlayerColor.Black || player.Color == EPlayerColor.Dark);
        }

        /// <summary>只判断“已经伪装”；Arming 阶段不锁用物品/技能/设备。</summary>
        private static bool IsDisguised(GamePlayer player)
        {
            return player != null
                && player.PublicInfo != null
                && ByPlayer.TryGetValue(player.PublicInfo.PlayerId, out DisguiseState st)
                && st.HasFake;
        }

        private static float DistanceSquared(GamePlayer player, PosInfo pos)
        {
            float dx = player.PublicInfo.Pos.X - pos.X;
            float dy = player.PublicInfo.Pos.Y - pos.Y;
            return dx * dx + dy * dy;
        }

        // ══ 补丁 ══════════════════════════════════════════════════════

        /// <summary>拾取后开始计时；伪装者捡别的物品直接拒绝；假物品 E 由 Prefix 翻出。</summary>
        [HarmonyPatch(typeof(ItemManager), nameof(ItemManager.AcquireItem))]
        internal static class AcquireHook
        {
            [HarmonyPrefix]
            private static bool Prefix(GamePlayer player, int itemId)
            {
                if (player == null)
                    return true;

                if (ByFakeId.TryGetValue(itemId, out DisguiseState st))
                {
                    if (st.HasFake && st.FakeInfo != null
                        && DistanceSquared(player, st.FakeInfo.Pos)
                        <= RevealDistance.Value * RevealDistance.Value)
                    {
                        bool allowed = IsBlack(player)
                            || player.PublicInfo.PlayerId == st.PlayerId
                            || AllowOthersReveal.Value;
                        if (allowed)
                        {
                            Reveal(st, "interact");
                            return false;
                        }
                    }
                    return false;
                }

                if (IsDisguised(player))
                    return false;

                return true;
            }

            [HarmonyPostfix]
            private static void Postfix(GamePlayer player)
            {
                BeginArming(player);
            }
        }

        /// <summary>伪装者不能使用/丢弃手持物，避免假物品图标和真实手部脱节。</summary>
        [HarmonyPatch(typeof(GamePlayer), nameof(GamePlayer.UseItem), new[] { typeof(GameItem), typeof(int) })]
        internal static class UseItemHook
        {
            [HarmonyPrefix]
            private static bool Prefix(GamePlayer __instance)
            {
                return __instance == null
                    || !IsDisguised(__instance);
            }
        }

        [HarmonyPatch(typeof(ItemManager), nameof(ItemManager.DropItem))]
        internal static class DropItemHook
        {
            [HarmonyPrefix]
            private static bool Prefix(GamePlayer player)
            {
                return player == null
                    || !IsDisguised(player);
            }
        }

        /// <summary>伪装者不能操作设备。</summary>
        [HarmonyPatch(typeof(GameDeviceManager), nameof(GameDeviceManager.Interact))]
        internal static class DeviceInteractHook
        {
            [HarmonyPrefix]
            private static bool Prefix(GamePlayer player)
            {
                return player == null
                    || !IsDisguised(player);
            }
        }

        [HarmonyPatch(typeof(GameDeviceManager), nameof(GameDeviceManager.HandleEvent))]
        internal static class DeviceEventHook
        {
            [HarmonyPrefix]
            private static bool Prefix(GamePlayer player)
            {
                return player == null
                    || !IsDisguised(player);
            }
        }

        /// <summary>伪装者不能使用主动技能。</summary>
        [HarmonyPatch(typeof(GameRoom), nameof(GameRoom.UseSkill))]
        internal static class UseSkillHook
        {
            [HarmonyPrefix]
            private static bool Prefix(GamePlayer player)
            {
                return player == null
                    || !IsDisguised(player);
            }
        }

        /// <summary>忽略本人客户端的状态切换，避免原版把 Hide 回发、破坏本地 Idle。</summary>
        [HarmonyPatch(typeof(GamePlayer), nameof(GamePlayer.ModifyPlayer))]
        internal static class ModifyPlayerHook
        {
            [HarmonyPrefix]
            private static bool Prefix(GamePlayer __instance, C_MODIFY_PLAYER pkt)
            {
                if (__instance == null || pkt == null)
                    return true;
                if (pkt.Type != EModifyPlayerEvent.ChangePlayerState)
                    return true;
                return !IsDisguised(__instance);
            }
        }

        /// <summary>伪装者可以移动；服务端自行更新位置并同步假物品，不解除伪装。</summary>
        [HarmonyPatch(typeof(GameRoom), nameof(GameRoom.HandleMove))]
        internal static class MoveHook
        {
            [HarmonyPrefix]
            private static bool Prefix(GamePlayer player, C_MOVE pkt)
            {
                if (!Active || player == null || pkt == null)
                    return true;
                if (!ByPlayer.TryGetValue(player.PublicInfo.PlayerId, out DisguiseState st))
                    return true;
                if (!st.HasFake || !player.IsAlive || player.State != EPlayerState.Hide)
                    return true;
                if (player.MoveLock)
                    return false;
                if (!AreaManager.Instance.ValidPosition(pkt.Pos))
                    return false;

                if (player.PublicInfo.Pos != pkt.Pos)
                {
                    player.DirectionAngle = Util.GetZRotation(
                        pkt.Pos.X - player.PublicInfo.Pos.X,
                        pkt.Pos.Y - player.PublicInfo.Pos.Y);
                }
                player.PublicInfo.LookLeft = pkt.LookLeft;
                player.Velocity = pkt.Velocity;
                player.IsMove = pkt.IsMove;
                player.Move(pkt.Pos);
                SyncFake(st, false);
                return false;
            }
        }

        /// <summary>每秒检查静止计时、假物品跟随、虚影刷新。</summary>
        [HarmonyPatch(typeof(GameRoom), "SurvivalTick")]
        internal static class TickHook
        {
            [HarmonyPostfix]
            private static void Postfix()
            {
                if (!Active)
                {
                    ClearAll();
                    return;
                }

                foreach (DisguiseState st in ByPlayer.Values.ToList())
                {
                    GamePlayer player = st.Player;
                    if (player == null || player.Session == null || !player.IsAlive || player.IsSpectator)
                    {
                        RemoveState(st);
                        continue;
                    }

                    if (st.HasFake)
                    {
                        if (player.State != EPlayerState.Hide)
                        {
                            RemoveState(st);
                            continue;
                        }
                        SyncFake(st, false);
                        if (GhostInterval.Value > 0f && UnityEngine.Time.time >= st.NextGhost)
                            PulseGhost(st);
                        continue;
                    }

                    if (player.State == EPlayerState.Hide || player.State == EPlayerState.Sit)
                    {
                        RemoveState(st);
                        continue;
                    }

                    // 捡起物品时可能短暂处于 Interact/Attack 等状态；先等回到
                    // Idle/Run 再开始/继续 3 秒计时，别因为拾取动作直接丢掉 Arming。
                    if (player.State != EPlayerState.Idle && player.State != EPlayerState.Run)
                    {
                        st.LastPos = player.PublicInfo.Pos.Clone();
                        st.StableSince = UnityEngine.Time.time;
                        continue;
                    }

                    if (player.Hand == null || player.Hand.Info.DataId != st.ItemDataId)
                    {
                        RemoveState(st);
                        continue;
                    }

                    PosInfo pos = player.PublicInfo.Pos;
                    if (st.LastPos == null
                        || UnityEngine.Mathf.Abs(pos.X - st.LastPos.X) > PositionEpsilon
                        || UnityEngine.Mathf.Abs(pos.Y - st.LastPos.Y) > PositionEpsilon)
                    {
                        st.LastPos = pos.Clone();
                        st.StableSince = UnityEngine.Time.time;
                    }
                    else if (UnityEngine.Time.time - st.StableSince >= StandSeconds.Value)
                    {
                        StartDisguise(st);
                    }
                }
            }
        }

        /// <summary>死亡时清掉假物品，避免客户端残留。</summary>
        [HarmonyPatch(typeof(GamePlayer), "OnDead")]
        internal static class DeadHook
        {
            [HarmonyPostfix]
            private static void Postfix(GamePlayer __instance)
            {
                if (__instance == null)
                    return;
                if (ByPlayer.TryGetValue(__instance.PublicInfo.PlayerId, out DisguiseState st))
                    RemoveState(st);
            }
        }

        [HarmonyPatch(typeof(GameRoom), "StartLobby")]
        internal static class LobbyHook
        {
            [HarmonyPostfix]
            private static void Postfix() => ClearAll();
        }

        [HarmonyPatch(typeof(GameRoom), nameof(GameRoom.ChangeGameState), new[] { typeof(EGameState) })]
        internal static class StateHook
        {
            [HarmonyPostfix]
            private static void Postfix(EGameState state)
            {
                if (state != EGameState.Survive)
                    ClearAll();
            }
        }
    }
}
