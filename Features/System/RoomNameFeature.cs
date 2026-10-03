using BepInEx.Configuration;
using HarmonyLib;
using Server.Game;
using HideAndSeek.Core;

namespace HideAndSeek.Features.System
{
    /// <summary>
    /// 修改当前房间的名字。它有两个落点（2026-10 新版起是**两个**，缺一不可）：
    ///
    ///   ① **Steam Lobby 元数据** —— 键 `"name"`（新版常量 `LOBBY_DATA_NAME_KEY`，值仍是 `"name"`）：
    ///      创建时 `SteamMatchmaking.SetLobbyData(LobbyId, "name", roomName)`；
    ///      别人浏览房间列表时 `GetLobbyData(lobbyByIndex, "name")`。
    ///      ⇒ 改这里，**房间列表**里的名字才会变。
    ///   ② **游戏内缓存** `NetworkManager.RoomName` —— 新版新增：由 `SteamLobbyManager.OnLobbyEnter`
    ///      （进房时读一次元数据）与 `CreateLobby` 写入，而 **`UI_GameScene.Init` 显示的就是它**。
    ///      ⇒ 只改 ①，**房主自己游戏内看到的名字不会变**。
    ///
    /// 已在房内的**其他人**不会自动变（`OnLobbyDataUpdate` 不刷新名字缓存）⇒ 他们要重新进房。
    ///
    /// 只有房主能改（Steam 侧限制），`SetLobbyData` 的返回值即成败。
    /// 为避免引用 Steamworks 程序集，LobbyId 与 Steam 调用走反射；缓存那句也走反射
    /// （同一文件风格一致，且不受它将来改可见性的影响）。
    ///
    /// 验证手段：`hs_roomname` 无参数时**同时回读**两处（`lobbyName` / `cachedName`）——
    /// 两者不一致就说明只写成了一边。
    /// </summary>
    [PatchFeature(
        section: "RoomName",
        description: "修改房间名（Steam 房间列表 + 游戏内显示两处一起改；只有房主可改；已在房内的其他人需重新进房才看到新名）。",
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

        /// <summary>
        /// 把房间名写入 Steam 大厅元数据，**并同步游戏内的本地缓存**。
        ///
        /// ★ 2026-10-02 新版新增了缓存（IL 核对过，见 `.tmps/tools/dump-lobbyname*.ps1`）：
        ///   · 缓存 = `NetworkManager.RoomName`，由 `SteamLobbyManager.OnLobbyEnter`
        ///     （进房时从大厅元数据读一次）与 `NetworkManager.CreateLobby` 写入；
        ///   · **`UI_GameScene.Init` 显示的就是这个缓存**（`UI_GameScene::Init → get_RoomName`）
        ///     ⇒ 只写 Steam 的话，**房主自己游戏内看到的名字不会变**（别人看房间列表会变）。
        ///   · 游戏自己的入口 `NetworkManager.SetRoomName(string)` **只写这个字段、不发 Steam**
        ///     （IL 就是 `set_RoomName(value ?? "")`）⇒ **两边都要做**。
        ///
        /// 机制本身**没变**：键仍是 `"name"`（`LOBBY_DATA_NAME_KEY`），写入仍是
        /// `SteamMatchmaking.SetLobbyData`；新版新增的 `RepublishOwnedMetadata()`
        /// **只重发 `members`**，不会覆盖房间名。
        /// ⚠ 已在房内的**其他人**不会自动变：`OnLobbyDataUpdate` 不刷新名字缓存，
        ///   他们要等重新进房（原版行为，不是我们的取舍）。
        /// </summary>
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

                // ★ 同步游戏内缓存（新版 UI 显示的是它）。走反射：与上面的 Steam 调用同一风格，
                //   也不受该方法将来改可见性的影响。
                bool cacheOk = TrySetLocalCache(name, out string cacheError);

                Plugin.Log.LogInfo($"[HS] RoomName：房间名已改为「{name}」"
                    + (cacheOk ? "（Steam 元数据 + 游戏内缓存均已更新）。"
                               : $"（Steam 元数据已更新，但游戏内缓存未更新：{cacheError}）。"));
                return true;
            }
            catch (global::System.Exception ex)
            {
                error = (ex.InnerException ?? ex).Message;
                return false;
            }
        }

        /// <summary>
        /// 把名字写进 `NetworkManager.RoomName`（新版游戏内 UI 读的就是它）。
        /// 游戏自己的 `SetRoomName(string)` 只写字段、不发 Steam ⇒ 与上面的 `SetLobbyData` 互补。
        /// </summary>
        private static bool TrySetLocalCache(string name, out string error)
        {
            error = null;
            try
            {
                var net = Managers.Network;
                if (net == null)
                {
                    error = "Managers.Network 为空";
                    return false;
                }

                var m = AccessTools.Method(net.GetType(), "SetRoomName", new[] { typeof(string) });
                if (m == null)
                {
                    error = "这个游戏版本没有 NetworkManager.SetRoomName（旧版无缓存，属正常）";
                    return false;
                }

                m.Invoke(net, new object[] { name });
                return true;
            }
            catch (global::System.Exception ex)
            {
                error = (ex.InnerException ?? ex).Message;
                return false;
            }
        }

        /// <summary>
        /// 回读"当前实际生效的名字"：
        /// <paramref name="current"/> = Steam 大厅元数据里的值（别人看房间列表看到的）；
        /// <paramref name="cached"/> = 游戏内缓存 `NetworkManager.RoomName`（房主自己 UI 显示的）。
        /// 两者不一致就说明只写成了一边（新版才有的情况）。
        /// </summary>
        internal static bool Query(out string current, out string cached, out string error)
        {
            current = null;
            cached = null;
            error = null;

            // 游戏内缓存（新版才有；取不到不算错，旧版没有这个字段）
            try
            {
                var net = Managers.Network;
                if (net != null)
                {
                    var p = AccessTools.Property(net.GetType(), "RoomName");
                    if (p != null)
                        cached = p.GetValue(net) as string;
                }
            }
            catch
            {
                cached = null;
            }

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
