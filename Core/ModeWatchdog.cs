using Server.Game;

namespace HideAndSeek.Core
{
    /// <summary>
    /// 模式开关的运行期监视。
    ///
    /// HS_Mode.Enabled 是热开关（补丁常驻、Prefix 首行短路），关闭后新逻辑立即停止，
    /// 但**已经产生副作用的状态**需要还原，否则会残留半关闭局面：
    ///   - 黑方视野裁剪：远处玩家仍不在黑方的观察列表里（表现为"看不见人"）
    ///   - 黑方黑灯：客户端仍停留在 Darkness（表现为"一直是黑的"）
    ///
    /// 因此检测到"从开变关"时执行一次回滚：重发真实区域光照 + 重建全员可见性。
    /// 由 ModeWatchdogFeature 挂在 GameRoom.SurvivalTick 上驱动。
    /// </summary>
    internal static class ModeWatchdog
    {
        private static bool _lastActive;

        public static void Tick()
        {
            bool now = ModeRuntime.Active;
            if (_lastActive && !now)
                Rollback();
            _lastActive = now;
        }

        private static void Rollback()
        {
            var room = GameRoom.Instance;
            if (room == null)
            {
                Plugin.Log.LogInfo("[HS] 模式已关闭（当前无活动房间，无需回滚）。");
                return;
            }

            foreach (var player in room.Players)
            {
                if (player?.PublicInfo == null)
                    continue;

                // 恢复真实光照：重播所在区域的光照状态
                var area = player.CurrentArea;
                if (area != null)
                    area.SendAreaInfo(player);

                // 重建可见性：把"自己能看到的人"恢复为原版的全量
                AreaManager.Instance?.SearchAndUpdatePlayer(player);
            }

            Plugin.Log.LogInfo("[HS] 模式已关闭：已回滚黑方可见性与光照。");
        }
    }
}
