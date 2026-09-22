using BepInEx.Configuration;
using HarmonyLib;
using Server.Game;
using HideAndSeek.Core;

namespace HideAndSeek.Features.System
{
    /// <summary>
    /// 修改当前房间在 Steam 大厅里显示的名字。
    ///
    /// 房间名不是游戏内对象，而是 Steam Lobby 的一条元数据：
    ///   创建时 SteamMatchmaking.SetLobbyData(LobbyId, "name", roomName)（:160268；
    ///     名字默认取房主玩家名，见 :34003）
    ///   别人浏览房间列表时 GetLobbyData(lobbyByIndex, "name")（:160335）
    /// 所以本功能改的是**房间列表里显示的名字**。对局内 UI 不实时读这条元数据，
    /// 已经进房的玩家界面不会随之变化 —— 这是原版机制决定的，不是实现取舍。
    ///
    /// 只有房主能改（Steam 侧限制），SetLobbyData 的返回值即成败。
    /// 为避免引用 Steamworks 程序集，LobbyId 与两个 Steam 调用全部走反射：
    /// CSteamID 是 struct，装箱后用 object 传参即可。
    ///
    /// 验证手段：Query 回读大厅里实际存的值。客户端界面不展示这条元数据，
    /// 所以"改完看一眼界面"是验证不了的，只能读回大厅本身。
    /// </summary>
    [PatchFeature(
        section: "RoomName",
        description: "修改当前房间在 Steam 房间列表里显示的名字（只有房主可改；已在房间内的玩家界面不会随之变化）。",
        defaultEnabled: true,
        side: FeatureSide.Host)]
    internal static class RoomNameFeature
    {
        [ConfigField("", "最近一次设置的房间名（留空表示尚未设置过）。")]
        public static ConfigEntry<string> LastName;

        /// <summary>取出 Managers.Network.Lobby.LobbyId（反射，避免引用 Steamworks）。</summary>
        private static bool TryGetContext(out object lobbyId, out string error)
        {
            lobbyId = null;
            error = null;

            var lobby = Traverse.Create(Managers.Network).Property("Lobby").GetValue();
            if (lobby == null)
            {
                error = "尚未创建房间";
                return false;
            }

            lobbyId = Traverse.Create(lobby).Property("LobbyId").GetValue();
            if (lobbyId == null)
            {
                error = "拿不到 LobbyId（可能还没进入大厅）";
                return false;
            }

            return true;
        }

        /// <summary>把房间名写入 Steam 大厅元数据。</summary>
        internal static bool Apply(string name, out string error)
        {
            error = null;

            if (!TryGetContext(out object lobbyId, out string ctxError))
            {
                error = ctxError;
                return false;
            }

            var type = AccessTools.TypeByName("Steamworks.SteamMatchmaking");
            var method = type == null ? null : AccessTools.Method(type, "SetLobbyData");
            if (method == null)
            {
                error = "找不到 SteamMatchmaking.SetLobbyData";
                return false;
            }

            try
            {
                bool ok = (bool)method.Invoke(null, new object[] { lobbyId, "name", name });
                if (!ok)
                {
                    error = "Steam 拒绝了修改（只有房主可以改，且需已创建大厅）";
                    return false;
                }

                if (LastName != null)
                    LastName.Value = name;

                Plugin.Log.LogInfo($"[HS] RoomName：房间名已改为「{name}」。");
                return true;
            }
            catch (global::System.Exception ex)
            {
                error = (ex.InnerException ?? ex).Message;
                return false;
            }
        }

        /// <summary>回读 Steam 大厅里当前实际存的名字 —— 验证改名是否真的落到了大厅上。</summary>
        internal static bool Query(out string current, out string error)
        {
            current = null;
            error = null;

            if (!TryGetContext(out object lobbyId, out string ctxError))
            {
                error = ctxError;
                return false;
            }

            var type = AccessTools.TypeByName("Steamworks.SteamMatchmaking");
            var method = type == null ? null : AccessTools.Method(type, "GetLobbyData");
            if (method == null)
            {
                error = "找不到 SteamMatchmaking.GetLobbyData";
                return false;
            }

            try
            {
                current = (string)method.Invoke(null, new object[] { lobbyId, "name" });
                return true;
            }
            catch (global::System.Exception ex)
            {
                error = (ex.InnerException ?? ex).Message;
                return false;
            }
        }
    }
}
