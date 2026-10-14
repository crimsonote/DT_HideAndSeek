using BepInEx.Configuration;
using HarmonyLib;
using System.Linq;
using Protocol;
using Server.Game;
using HideAndSeek.Core;

namespace HideAndSeek.Features.System
{
    /// <summary>
    /// 地图查找兜底：给原版 <c>AreaManager.GetArea</c> 补上边界检查。
    ///
    /// 病因（2026-10-15 实机定位：Player.log 堆栈 + 回放磁带 + Cecil 读运行时 IL）：
    ///   原版 GetArea（ACS:173442）直接 <c>Managers.Data.MapArray[x][y]</c>，**无任何边界检查**；
    ///   而它的姊妹方法 <c>ValidPosition</c>（ACS:173450）有全套检查
    ///   （num&lt;0 ‖ num&gt;=MapSize ‖ 格子为 0）、<c>ClampToMapBounds</c>（:173466）就在楼下 ——
    ///   原版自己就知道坐标会越界，唯独 GetArea 这个消费者忘了防（MapArray 是锯齿 List&lt;List&lt;byte&gt;&gt;，
    ///   越界时抛的正是 ArgumentOutOfRangeException: Parameter name: index）。
    ///
    ///   触发路径：受害者站在地图最左/最右一列（合法格 —— HandleMove 的 ValidPosition 放行，
    ///   ACS:171747），被刀杀 ⇒ OnDeadMurder → DeviceManager.CreateCorpse → 尸体构造函数
    ///   **自带 ±100 朝向偏移**（CheckDownDirection 探针 + DeviceInfo.Pos.X ±= 100，ACS:168814-815）
    ///   ⇒ 探针位超出网格一格 ⇒ 抛异常 ⇒ 死亡回调被拦腰截断（半死：S_DEAD 已发、尸体没建、
    ///   ExitPlayer 没跑、人还在 AlivePlayers，系统按活人结算）。
    ///
    /// 方案：Finalizer 兜底 —— 只在原方法**已经抛出**索引越界时接管：把坐标夹回合法范围、
    /// 必要时朝地图中心逐格找最近的非空格，复刻原版的查找逻辑给出一个合法 Area 并吞掉异常。
    /// 正常入参路径零改动（__exception == null 立即返回）。
    ///
    /// 影响面：GetArea 全游戏共 15 处调用（设备进出区域登记 /  corpse 探针 / 观战区域推送 /
    /// 移动取区…），全部在 Assembly-CSharp，本模块自己没有一处调用它。越界路径原本必崩，
    /// 兜底只是把"整个任务回调崩掉"换成"拿到一个相邻的合法区域"。
    /// </summary>
    [PatchFeature(
        section: "AreaGuard",
        description: "地图查找兜底：原版 GetArea 的网格查找没有边界检查（姊妹方法 ValidPosition 有、它没有）——贴边刀杀时尸体定位会越界抛异常、把死亡回调拦腰截断成「半死」。本功能只在异常路径上把坐标夹取到合法区域并吞掉异常，正常路径零改动。",
        defaultEnabled: true,
        side: FeatureSide.Host)]
    internal static class AreaGuardFeature
    {
        /// <summary>运行期热开关。段开关 [AreaGuard].Enabled 决定补丁挂不挂（改它要重启游戏）。</summary>
        [ConfigField(true, "运行期热开关（不需要重启游戏）：关掉即完全恢复原版行为（坐标越界时照旧抛异常）。段开关 [AreaGuard].Enabled 只决定补丁挂不挂。")]
        public static ConfigEntry<bool> Active;

        /// <summary>
        /// GetArea 只有一个重载（IL 已复核：Cecil 读运行时程序集，仅 <c>GetArea(PosInfo)</c>），
        /// 仍显式写参数类型防将来加 overload 时歧义。
        /// Finalizer 返回 Exception：返回 null = 吞掉待抛异常；返回原异常 = 原样传播。
        /// </summary>
        [HarmonyPatch(typeof(AreaManager), "GetArea", new[] { typeof(PosInfo) })]
        internal static class GetAreaHook
        {
            [HarmonyFinalizer]
            private static global::System.Exception Finalizer(AreaManager __instance, PosInfo pos, ref Area __result, global::System.Exception __exception)
            {
                Diagnostics.Hit("AreaGuard");
                if (__exception == null) return null;                  // 正常路径：什么都不做
                if (ModeRuntime.Bypass) return __exception;            // 模式关闭 = 纯原版
                if (Active == null || !Active.Value) return __exception;
                if (pos == null) return __exception;
                if (!(__exception is global::System.ArgumentOutOfRangeException)) return __exception;   // 只管索引越界

                try
                {
                    Area safe = SafeLookup(__instance, pos);
                    if (safe == null) return __exception;              // 兜不住：保留原异常
                    __result = safe;
                    Plugin.Log.LogWarning(
                        $"[HS] AreaGuard：GetArea 网格越界 pos=({pos.X:F0},{pos.Y:F0}) → 已夹取到安全区域，" +
                        $"原异常 {__exception.GetType().Name}: {__exception.Message}");
                    return null;                                       // 吞掉
                }
                catch (global::System.Exception ex)
                {
                    Plugin.Log.LogWarning($"[HS] AreaGuard：兜底查找自身失败（保留原异常）— {ex}");
                    return __exception;
                }
            }
        }

        /// <summary>
        /// 复刻原版 GetArea 的查找逻辑，但坐标先夹到合法范围、必要时朝地图中心找最近非空格。
        /// 自身保证不抛索引异常；彻底找不到时返回 null（由调用方保留原异常）。
        /// ⚠ 成员归属（原版自相矛盾的两处，照抄）：<c>MapArray</c> 在 <c>Managers.Data</c> 上
        /// （ACS:173447），而 <c>MapOffset/MapSize</c> 在 <c>MapData</c> 实例上（:173445-446）。
        /// </summary>
        private static Area SafeLookup(AreaManager am, PosInfo pos)
        {
            var map = Managers.Data?.MapData;
            var grid = Managers.Data?.MapArray;
            if (am == null || map == null || grid == null || am.Areas == null || am.Areas.Count == 0)
                return null;

            int rows = grid.Count;
            if (rows <= 0) return am.Areas[0];

            // 与原版同款的网格换算（ACS:173445-173446）
            int x = (int)(pos.X / 224f) - (int)map.MapOffset.X;
            int y = (int)(pos.Y / 224f) - (int)map.MapOffset.Y;

            // ① 夹到合法范围（行内长度可能不齐 —— MapArray 是锯齿数组）
            int cx = x < 0 ? 0 : (x >= rows ? rows - 1 : x);
            int rowLen = grid[cx].Count;
            if (rowLen <= 0) return am.Areas[0];
            int cy = y < 0 ? 0 : (y >= rowLen ? rowLen - 1 : y);

            // ② 从夹取点朝地图中心逐格找第一个非空格（外缘可能有 0 = 虚空格）
            int ctrX = (int)(map.MapSize.X / 2f);
            int ctrY = (int)(map.MapSize.Y / 2f);
            int budget = rows + rowLen + 8;
            for (int step = 0; step < budget; step++)
            {
                if (cx >= 0 && cx < rows && cy >= 0 && cy < grid[cx].Count)
                {
                    byte cell = grid[cx][cy];
                    if (cell != 0)
                    {
                        Area found = am.Areas.FirstOrDefault(a => a.Data != null && a.Data.Type == (ERoomType)cell);
                        if (found != null) return found;
                    }
                }
                if (cx == ctrX && cy == ctrY) break;
                cx += cx < ctrX ? 1 : (cx > ctrX ? -1 : 0);
                cy += cy < ctrY ? 1 : (cy > ctrY ? -1 : 0);
            }

            // ③ 兜底：任意一个区域也胜过再抛一次（调用方大多要非 null）
            return am.Areas[0];
        }
    }
}
