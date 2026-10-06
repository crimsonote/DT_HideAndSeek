# 更新日志

本文件按版本倒序排列。每个版本下是那次发布包含的提交（不含合并提交）。

> 版本号来源：`HideAndSeek.csproj` 与 `Plugin.cs`（`verify.ps1` 会校验两者一致）。

## v1.4.6

- chore: 插件 GUID 由 `YumeHatsuyuki.DeadlyTrick.HideAndSeek` 改为 `HideAndSeek`
  —— 旧值借用了 DT_Tools 作者的名义，而上游自己的 GUID 就是 `DT_Tools`；本模块不该挂他人之名
- fix(dt): 适配 DT_Tools v1.0.9.0 —— 命令桥重写、配置入口双探、写盘隔离（新段 `[DtMirror]`）
- fix(dummy): StartPick 钩子钉 `Priority.First+1` —— 上游 SpectatorJoin 会整替该方法
- fix(tools): 修正两个检查器的提取缺陷（别名归一、段名认位置参数）
- chore(verify): 增加行尾检查
- docs: 回写 DT_Tools v1.0.9.0 适配结论；增补「提交纪律」；补 LICENSE（GPL-3.0）
## v1.4.5

- release: v1.4.5 —— 修「降级时黑方被幽灵化」（剪影 IsGhost 判据）
- refactor(replay): 剪影的 IsGhost 判据抽成唯一方法 —— 杜绝「改了一处漏另一处」
- fix(replay): 剪影的 IsGhost 判据在 TapeAssembler 里被无条件覆盖 —— 修「降级时黑方仍被幽灵化」

## v1.4.4

- release: v1.4.4 —— 拿刀幕 after +1 秒；窗口默认值一并定为新值
- tune(replay): 「拿刀」幕 after 1→2 秒；并修正窗口参数的 ConfigField 默认值

## v1.4.3

- Revert "feat(replay): 服务端合成补 AddShot{Corpse} —— 修"回放里尸体进不了水池""
- release: v1.4.3 —— DT 回放修复（客户端录像 + 服务端合成）
- feat(replay): 服务端合成补 AddShot{Corpse} —— 修"回放里尸体进不了水池"
- tune(replay): 杀人幕窗口 before 3→2、after 1→2.2 —— 让 DT 的"尸体进水池"进得来
- fix(replay): DT 刀杀补登记点 —— 它不经过 OnDamaged，所以从未请客户端录过
- diag(replay): 客户端侧只读探针 —— 把「回空带」的三种上游原因切成三段
- diag(replay): 记录「S_RECORD_REPLAY 有没有发出去」—— 定位客户端为何回空带
- fix(replay): 幕顺序改为按「窗口起点」排 —— 修"DT 那一幕跑到最后"

## v1.4.2

- release: v1.4.2 —— 剪影瞬移出画
- fix(replay): 剪影瞬移帧的时间戳改用 `start` —— 修"所有幕被判无效、快速刷过"
- feat(replay): 剪影瞬移出画 —— 用 RespawnShot 把剪影挪到主角视野之外

## v1.4.1

- release: v1.4.1 —— 标记一个可回退的正式版本
- feat(replay): 合成幕补「出刀闪光」与「DT（藏尸）」—— 服务端把广播过的帧记下来再补回
- fix(replay): 快照登记时**回填"事件之前"那几秒** —— 修"第一幕没有出刀动画、没有人死掉"
- feat(replay): 「按需裁剪」第二步 —— Range 优先读服务端采样快照（客户端磁带仍优先）
- feat(replay): 「按需裁剪」第一步 —— 事件发生时开一份独立采样快照（只积累，不改查询）
- Revert "refactor(replay): 缓冲改为「按需裁剪」—— 不再猜一个固定的大窗口"
- refactor(replay): 缓冲改为「按需裁剪」—— 不再猜一个固定的大窗口
- refactor(replay): 删掉 SceneIds 里"取不到采样就不排除"的兜底（过度防御）
- fix(replay): 剪影的 IsGhost 只在「降级到镜头宿主本人」时为 false（其余一律幽灵化）
- refactor(replay): 把"退回镜头宿主本人"定性为原版方案（不是降级、不打警告）
- fix(replay): 距离判据改为「扫遍整个窗口」—— 只看窗口起点正好是最远的那个时刻
- feat(replay): "谁在画面里"改用「位置更新 ∪ 距镜头 < ExitRange」—— 救回不动的人
- fix(replay): 缓冲覆盖整局 + 不再拿"未来帧"冒充过去 —— 修「砍死虚空、凭空冒出尸体」
- refactor(replay): 删掉 Range 的"锚帧兜底" —— 它只会掩盖采样机制损坏
- fix(replay): 采样改为「定时录帧」—— 修合成幕"一闪而过"的真正根因（用户指出）
- fix(replay): Range 补上"窗口起点的锚帧" —— 修自爆/黑方幕"一闪而过"的真根因
- fix(replay): 合成路保证"帧铺满窗口" —— 修自爆/黑方幕"一闪而过"
- diag(replay): 打印"合成取到多少采样 + 缓冲覆盖到哪" —— 定位自爆幕跨度 0.00s
- diag(replay): 服务端合成路也落盘 —— 让一次对局就能同时拿到「日志 + dump」
- diag(replay): 加"每段帧跨度"日志（观测）；顺带把合成路的非稳定排序修掉
- fix(replay): 装配改用稳定排序 —— 修「拿刀幕结束时刀没被拿走」
- fix(replay): 修「在画面里」的判据 —— 从 SpawnShot 改为 MoveShot（根因），并回退上一轮两处绕症状的补丁
- fix(replay): 去掉进 Trial 后那 7 秒等待 —— 它换来的正是用户看到的那张"未初始化界面"
- diag(corpse-report): 加"审判唯一出口"探针 —— 定位「尸体报告没被拦住」
- fix(replay): 位置优先取自磁带 —— 修「拿刀幕很飘」的真凶（并回退上一轮错误的换钩子）
- fix(replay): 「拿刀」幕改用真正的拿刀时刻（HandItemObjectId == 4001）
- diag(replay): 加"首帧位置取自哪一帧"的只读诊断（用户指出问题在后处理，不在源数据）
- revert(replay): 撤回"改拿刀挂点"，改成只加只读探针（我上一轮是在脑补）
- docs(replay): 标注 `_blackId = 0` 的已核实机制与「仍在怀疑、暂不修」
- revert(replay): 删掉剪影候选里"按有没有客户端分档"—— 那是把测试环境写进常态逻辑
- refactor(replay): 剪影候选的注释与日志改成诚实说法 —— 承认"无客户端优先"在正式对局里是空的
- feat(replay): 剪影优先挑"没有客户端的人" —— 让所有机器都避开 `_blackId = 0` 分支
- feat(replay): 在装配出口加硬约束「PlayerId <= 0 的出场帧不进磁带」
- docs(replay): 补一段「C9 反过来解释为什么要补全员出场帧」(附不要拿它换昵称的警告)
- revert(replay): 撤回"过滤幽灵"—— 它用一个卡死风险换几个昵称，不划算
- docs(replay): 核实并写清"三条隐藏通道"，纠正三处传抄来的错误结论
- refactor(replay): 剪影判据改为「整段不可见」这条统一规则，删掉"排除受害者"特例
- fix(replay): 禁止事件参与者被选为剪影 —— 修"死前没人影，死后才出尸体"
- fix(replay): 修"每帧遍历几万帧"导致的卡死（我上一轮引入的）
- chore(replay): 让"参与者兜底"自我报告 —— 区分"录制侧漏录"与"下游过滤错"
- feat(replay): 事件参与者强制进入画面名单 —— 修"第 2 刀之后受害者不在画面里"
- fix(replay): 加回 TapeDump.BeginRound（阶段 6 重写时漏掉）+ SpawnShot 普查诊断
- fix(replay): key 必须在**事件发生那一刻**分配并发给客户端 —— 修"3 次刀杀只播 1 次"
- fix(replay): 修"吞幕" —— 零素材必须降级到服务端合成 + 采样缓冲延长到覆盖整局
- fix(replay): 修 4 个 bug —— 卡死 / TapeHook 漏挂 / 幕序 / 索取前未登记 key
- feat(replay)!: 阶段 6 —— 新实现接管 [EndReplay]，删除旧实现
- fix(replay/shadow): roster 帧的位置改用房主侧采样（用真实磁带对照实测发现）
- refactor(replay/shadow): 「谁在画面里」改为**直接读帧**，删掉房间/AOI 近似；并接线真索带
- fix(replay/shadow): 4 处修正 —— 杀人事件时刻 / roster 同房间 / 输出重复 / 来源判断
- feat(replay): 阶段 5 —— ReplayDirector（编排层）+ 离线验证脚本，实测证明窗口修法
- feat(replay): 阶段 4 —— 影子的决策层（幕表 / 剪影 / 登记钩子），默认开启
- feat(replay): 磁带落盘 —— 把客户端真实回传的磁带 dump 成文本（排查用）
- feat(replay): 阶段 3b —— HostSynth：服务端合成器（把采样铺成帧）
- feat(replay): 阶段 3 —— HostRecorder：服务端采样改挂 Player.Move（追平客户端密度）
- feat(replay): 阶段 2 —— TapeAssembler：唯一的出带出口（不区分素材来源）
- feat(replay): 新回放模块的地基 —— Act（三个角色分开）+ ReplayWindow（唯一窗口计算）
- chore(replay): 等价性尺子 + 实现规格（重写前的准备，零业务改动）
- chore(replay): TrimTape 增加"穿墙观感"两项诊断（瞬移帧数 / 帧密度 / 补密数）
- docs: 回放·磁带生命周期梳理（时间基准 / 三个角色 / 处理链 / 避雷清单 / 回归清单）

## v1.4.0

- fix(replay): 观察者判据改用 roster（修"拿刀那幕黑漆漆"）+ 裁完排序（修"走路穿墙"）
- fix(replay): 索取磁带改为「每个 key 单发一包」—— 修复 6xx/7xx 段全部静默降级到服务端
- fix(replay): 观察者兜底改为「本段不在场的人」，不再用 id 0（用户口径）
- fix(replay): 挑不到隐藏观察者时退回「原版临时玩家(id=0)」—— 修「拿刀的人黑黑的」
- fix(rule): 清掉残留的 MasterMind —— 它导致原版给黑方弹「你的合作黑幕是[自己]」
- fix(console): `hs_panel` 改为「直接放一次」那个特效；回放不再使用它；移除设置页项
- feat(console/ui): 自爆「全屏面板+心跳声」可用 `hs_panel` 开关，并进游戏内设置页
- feat(replay): 「全屏面板+心跳声」恢复为可选（默认关）+ 白胜巡礼缩短到每人 2.5s
- fix(replay): 移除自爆的"全屏面板"特效，只保留一次全屏压暗（并加开关）
- Revert "fix(rule): 白胜会被黑胜覆盖在同一 tick —— 导致"白胜却冒出黑方【特别课程结束】"且白方被项圈自爆"
- feat(replay): 自爆瞬间加上"爆炸观感"（爆炸声 + 全屏面板 + 压暗余韵），全部零崩溃风险
- fix(rule): 白胜会被黑胜覆盖在同一 tick —— 导致"白胜却冒出黑方【特别课程结束】"且白方被项圈自爆
- feat(replay): 自爆幕"从自爆开始起算" + 第一段独占哑期与保底闪烁（让第一个人看得清开始闪）
- feat(replay): 黑方收尾幕与爆炸同时 + 白胜各段改为"同一段时间、依次播"（不再平铺）
- fix(replay): 自爆幕看不到闪烁的真因是"1 秒哑期 + 触发帧落在被切掉的最后一段"（纠正 ff9b6a8 的误判）
- fix(replay): 移除爆炸后的 DespawnShot —— 它掐断了项圈闪烁动画（diff 实测的真正差异）
- feat(replay): 白方各段窗口平铺（最后一段压到爆炸后 0.5s）+ 毒帧只逐帧剔除不整段丢
- fix(killupgrade): 局内任务门槛加成改为纯内存，不再写盘（它把"白方永远赢不了"固化进了 .cfg）
- fix(replay): 假人/无客户端也要登记成片段（交给服务端兜底）+ 幽灵替身撞观众本人时单独补发一份
- fix(replay): 兜底合成也要把"剪影槽位"和"相机目标"分开 —— 否则真人会被涂黑
- feat(replay): 改回原版式"每人一段视角"（8 秒按人数平分），服务端合成降为兜底且不再跨房间取景
- fix(replay): 去重条件用错了变量 —— 黑胜局里 `blackId` 是 0，导致"两个黑方镜头"没被去掉
- fix(replay): 服务端合成的每段磁带必须补一枚 NormalTimeEdit —— 这正是"哑剧"的真凶
- fix(replay): 一个黑方镜头就够 —— 去掉重复的那一幕；并加"出场名单"诊断
- fix(replay): 机位移动帧的 Velocity 不能是 0 —— 这才是"哑剧 + 镜头漂移被拽回"的真凶
- fix(replay): 服务端合成三处硬伤 —— 机位撞车 / 房间来源 / 演员隐形
- fix(replay): 服务端合成磁带必须先按时间戳稳定排序 —— 否则整幕塌成一瞬
- feat(replay): 结算运镜两幕 —— 白方巡礼 8s + 黑方收尾 3s（服务端合成）
- feat(replay): P1 房主侧服务端录制器 —— 假人与已死者也能进回放
- fix(replay): 藏住隐藏观察者的昵称；删掉两个临时诊断
- fix(replay): 毒帧判据改为「按内容」+ 无磁带片段补占位磁带 + 最后时段各自独立 key
- fix(replay): 撤掉客户端兜底；自爆段改由「被处决者 + 存活目击者」共同录制；假人不再排片
- fix(replay): 清掉审判UI字幕行烘死的默认文字 + 让黑方（含本机房主）能看到红名
- feat(replay): 结算拦截点改到 ChangeGameState(TotalResult) + 新增「自爆」片段
- feat(replay): 白胜时黑方的「最后时段」挪到最后 + 回放里黑方显示红名
- fix(replay): 推 Replay 前补一包 VoteResult —— 收掉审判 UI 上那个「投票结果」面板
- diagnostic(replay): 按"文本值"反查"文本键"——用来定位那个「投票结果/논의 시작」界面
- fix(replay): 回放期间把服务端按回 Survive —— 防止原版裁判状态机接管并弹出投票界面
- fix(replay): 观察者改用「他自己的」信息 —— 录像不再污染结算画面
- fix(replay): 观察者筛选放宽 + 出场帧改为「离 start 最近」
- feat(replay): 阶段1「全员出场帧」（防卡死）+ 阶段2「隐藏观察者」（去剪影）
- Revert "feat(replay): 试验「隐藏观察者」主视角 —— 用已死亡玩家的 id + IsGhost 换掉剪影"
- feat(replay): 试验「隐藏观察者」主视角 —— 用已死亡玩家的 id + IsGhost 换掉剪影
- fix(replay): 保留 EditShot —— 找回"击杀慢镜"与最后一段的"揭晓"；观察者试验结论为否
- feat(replay): 试验「虚拟观察者」主视角 —— 用 id=0 的回放临时玩家挡掉黑白遮罩
- fix(replay): 去掉多余的 Discuss 补包 —— 它会让客户端推进到投票阶段
- feat(replay): 发 S_FADE_IN（包 1004）收掉加载页 —— 找到原版关闭加载页的那一包
- fix(replay): 等待盖过"进入审判"的加载页（18s）—— 回放一直被 UI_Loading 盖住
- fix(replay): 照原版先发一包 Discuss 让审判开场字幕正常播完，再推 Replay
- fix(replay): 推 Replay 前等审判开场文字自行隐藏（3.0s），避免开场文字被冻在画面上
- fix(replay): 合成首帧改用"窗口起点那一刻的位置"，修回放画面空白
- fix(replay): 修「结算提前跑掉」与「换状态是异步的、Replay 推太早」
- fix(replay): 只让客户端进 Trial，服务端保持 Survive —— 修「裁判流程接管、回放从未出现」
- feat(replay): 对局结束前播放「真相公开」回放（黑方拿刀 / 每次刀杀 / 最后时段）
- docs(dt): 补齐 `EPlayerState.Hide` 的两个来源；修 verify.ps1 在 PS 5.1 下的崩溃
- docs(verify): 修正"用 UTF-8 搜 dll 验证中文"的错误配方
- feat(compat)!: 兼容上游 v1.0.6.1 + 配置与架构独立化

## v1.3.2

- feat(ui): 旧设置页补一项「启用首刀保护」（UI 重构暂停）

## v1.3.1

- release: v1.3.1 —— 三处修复（AOI 小熊并集 / BlackWin 触发点 / 首刀提示走密聊）
- fix(rule): BlackWin 漏了"无人死亡的路径" —— 小偷偷到露娜能力后不结算
- fix(vision): AOI 小熊模式恢复"并集" —— 放出小熊后不再削掉玩家本体视野

## v1.3.0

- release: v1.3.0 —— 首刀保护 / 小熊地图标记 / AOI 隔墙裁剪
- fix(vision): 隔墙提示的开关判据写反了（null 被当成"开着"）
- refactor(vision): 隔墙提示只在"视野升到 1 级"那一次出现
- feat(vision): AOI 遮挡对称化 —— 走到墙后同样会被剔除（房主指出的"捕获后失效"）
- refactor(skill): 精简小熊地图标记的状态与诊断代码（525 → 477 行）
- docs(skill): 修正把小熊当"黑方功能"的错误注释 —— 阵营与角色相互独立
- diag(skill): 加客户端探针定位"有平板 pin、无世界箭头"
- diag(skill): 加客户端探针定位"有平板 pin、无世界箭头"
- fix(skill): 小熊地图标记出不了世界箭头 —— 客户端没有目标的 Player 对象
- fix(skill): 小熊地图标记不再跳过假人 + 加详细诊断日志
- fix(skill): 小熊地图标记根本不出箭头 —— 判据照客户端写错，服务端的小熊永远"未激活"
- feat(vision): AOI 隔墙裁剪 —— 可关掉，视野升到 1 级后解除
- feat(skill): 小熊地图标记 —— 凛放下的小熊，把范围内的人标到她自己的地图上
- feat(rule): 首刀保护的文字提示加 20 秒限流（音效不限）
- refactor(rule): 抽出 KillBlocker.Reject，消除露娜免疫与首刀保护的重复代码
- feat(rule): 首刀保护 —— 上一局第一个死亡者，在本局有人死亡前不会被杀
- docs(agents): 编码节扩展为「.cs 与 .ps1 都必须带 UTF-8 BOM」
- fix(release): deploy-version.ps1 补 UTF-8 BOM —— .ps1 也必须带 BOM
- chore(release): 新增 deploy-version.ps1 —— 可回退部署到指定版本的快照

## v1.2.0

- chore(release): v1.2.0 —— 捉迷藏阶段版本
- fix(luis): 追踪箭头首包改用 IsForce=true，避免"从原地飘入"
- feat(luis): CD 改为「标记后冻结在 30，目标死亡才开始走」
- feat(luis): 追踪箭头新增 ArrowStyle 配置,默认 Character=红毛同款角色箭头
- fix(luis): 追踪箭头改为「先撤旧再发新」并选不碰地图的通道
- fix(luis): 追踪箭头类型 CharacterArrow → CorpseArrow —— 前者不在客户端贴图 switch(:89629) 里,用的是 prefab 默认图(看着像个黑洞);CorpseArrow 有 arrow_corpse.sprite 且语义就是'指向某处'
- change(keylock): 进度条改为按比例流失 —— 槽位总量固定 100,每次额外 E 减 100/DrainTaps 个百分点(DrainTaps=3 ⇒ 每次约 -34%),减到 0(空)即上锁;日志与条显示都改成百分比
- change(keylock): DrainTaps 回到 3(共 6 次:起始 3 次不显示 + 走条 3 次上锁);新增 GaugeEnabled 开关(默认 true) —— 关掉即回退旧的无进度条行为(按够 SealsNeeded 次直接上锁,全程不写门头那条)
- feat(keylock): 进度条钉住不动 —— 客户端 UI_DeviceCasting.Update 会以 ~1/s 把值爬向 cur+1(写(1,3)会永久停在2/3、写(2,3)约1秒后涨满隐藏);复用每250ms的保护检查任务把同一个值重发,条看起来静止(残余漂移<=1/4格)
- feat(keylock): 保护过时改为**主动撤销** —— 新建 Attempt 时排一个自重排任务(每250ms检查),到点即清 Attempt/GuardUntil/Charging 并把进度条写 (0,0),门恢复任何人可开;不再依赖'有人再碰门'才顺手清
- change(keylock): 进度条总长 DrainTaps 默认 6 → 21（条尽可能慢；Max 放宽到 60），fallback 同步
- change(keylock): 进度条总长拉长 —— DrainTaps 默认 3 → 6(条走得慢一半,Max 放宽到 30);撤回上一版加的'防连发去重'(经 git diff 确认工作树无该编辑任何残留)
- feat(keylock): 上锁尝试加入真正的过期(单调时钟) —— Attempt.Deadline 用 Time.realtimeSinceStartup(新增 NowReal;SurviveTime 是 int 秒且每局回 420,禁用),存活窗口复用 SealWindow(默认6秒);过期即清 Attempt/GuardUntil/Charging + ResetGauge 并把门交还任何人
- fix(keylock): 让持鱼者对门的每一次 E 都真正到达 —— 服务端 DeviceManager.Interact 开头有 player.InteractLock 闸(每次交互后锁 500ms 真毫秒,:163737/:175310-175327),而客户端一次按键一个包且无重发 ⇒ 500ms 内连按的包永久丢失(实测连按6次只计到约2次)。新挂 DeviceManager.Interact Prefix,在闸之前只针对'手持鱼'清掉 InteractLock;空手时行为与原版一致
- fix(keylock): 撤掉'基于时间的进度重置'这个自造机制 —— 它用 SurviveTime(int秒)算窗口,实际窗口不足1秒,进度被反复清零导致鱼完全锁不上门;恢复以前做法:每次有效E只累加,进度清理只挂明确事件(跨局/被别人锁上/本次完成);删除 Attempt.GuardUntil
- feat(keylock): /lock 覆盖鱼的尝试 —— 挂 GameDoor.LockDoor 钩子,门被别人锁上时立即作废 Attempt 与保护(不能等下一次按E:锁定态下客户端不发包,永远没机会清理);自身 TrySeal 用 _sealingSelf 标志排除
- refactor(keylock): 按规格重写为单一模型 —— 每扇门至多一条 LockAttempt{OwnerPid,Taps,GuardUntil} 作唯一真相源;谁先开始门锁归谁(别人拿鱼按E完全无效);前 SealsNeeded 次不显示进度条,之后每次额外有效E让条流失,流失满(DrainTaps)交还原版锁门;保护期到期而无下一次有效操作⇒保护失效+进度清零;删除已作废的 SwingIdleSeconds/Charge/LastSwingAt
- fix(keylock): 上锁过程中不再写进度条 —— 需求是'对着门操作够 need 次(真正上锁)之后'才出现进度条,第1下就冒条是非预期;敲击只记次数与日志,另加 ResetGauge 在停手时清掉残留
- docs(keylock): 把护栏的注释改准 —— 它不阻止上锁(未锁的门照样能被另一条鱼或 /lock 锁上),只防'门已锁住时被我们改写 StateList[2] 而抬高 TrySeal 读到的 existing'
- change(keylock): 上锁进度条方向反转 —— 送进槽位的是「还差几下」(need-已敲),一开始接近满、敲空(0)即上锁,与门自身那把锁(越填越接近解锁)相反;并在门已处于锁定态时不再写槽,避免改坏那把手锁的剩余显示
- docs(lian): EnableTrace 描述改准(只通知莲,不是全场活人)
- refactor(lian): 把原 [SoulSense] 段的「感知死亡=>莲加速」整体并入 [LianAltar] —— 同源能力(莲)归同一段,配置集中;段描述同步。旧段与旧文件删除
- fix(lian): 真正的根因 —— EnableVision(假光照,默认关)的守卫被写在 TickHook 开头,把'箭头到期删除'与'技能槽冷却补发'一起挡住了,删除包从未发出(子会话 e04faa43 定位)。守卫下移到假光照段之前
- fix(keylock): 去掉上锁进度的时间门槛 —— 之前那个'N 秒内连敲 N 下'把已经绕开的服务端 1 秒开合冷却又捞了回来(Prefix 在原方法体前执行,被冷却忽略的按键照样计数)。现在每按一次 E 就 +1,满格即锁;只有停手超 SwingIdleSeconds(默认2秒)才归零
- fix(lian): 尸体预警的坐标必须值拷贝 —— 原先引用 PublicInfo.Pos,尸体被搬动后我们记录的坐标也跟着变,导致到期删除按值匹配不上、骷髅头箭头永远消不掉
- fix(lian): 尸体方向预警的收件人改为「只有莲」(含偷到技能的) —— 原来照抄原版路易斯用了 BroadcastAlivePlayers 发给全场活人;指向尸体是莲的专属情报,箭头与删除包都改成定向发送
- fix(lian): 尸体预警不再每秒重发(客户端 SetArrow 每次都新建箭头对象,重发会累积成'长舌头'且只有最后一个能删掉) + 音效改为可配且默认静音(原来用 WarningSfx,那是原版尸体通报音,吵且混淆)
- fix(killupgrade): SpeedBonusPerLevel 代码默认值 0.1667 → 0.0667(满级 1.2) —— .cfg 早已是 0.0667,代码默认值没跟上(AGENTS:改默认值必须同步两处)
- change(lian): LianAltar 段内拆出两个独立开关 EnableVision(假光照/教室视为开灯) 与 EnableTrace(尸体方向预警) —— 同源能力仍在一段内,但可各自开关,不再逼出'开一个等于开两个'
- docs: 新增《测试清单》—— 把今天踩过的每个坑翻译成可在游戏里逐条验证的通过标准(启动三检查/假人/美幸/莲/路易斯/门锁/跨局残留/配置/编译期/只能实机看的)
- feat(proximity): 黑方接近预警重写为心跳 —— 用 SendWorldSFX 定向(只给范围内白方,黑方不受影响)+带位置(客户端自动算方位与衰减);节拍挂1Hz SurvivalTick,靠每tick发1~2拍表达缓急(0.5/1/2Hz);主拍+延迟回声+可选扫描底;全配置可调
- fix(miyuki): BlackPinRange 代码默认值 900f → -1f(跟随 AOI 视野) —— 判据逻辑(ResolveBlackPinRange 的 <0 分支)本就已实现且热更新,只是默认值没跟上
- fix(luis): ExhaustMs 默认值 5 → 5000 —— 单位是毫秒,写成 5 等于'时停后只力竭 5 毫秒'(子会话审计发现:字段名/范围/默认/描述四者矛盾)
- refactor(soda): 汽水移速默认值收成唯一常量 SodaMulDefault —— 原来同一数字在四处各写一遍(配置默认/申领fallback/日志fallback/结算fallback),漏改一处就会出现配置与实际不一致
- change(soda): 汽水移速默认值 1.8 → 1.5（代码默认与描述同步；.cfg 里用户已是 1.5）
- feat(luis): 追踪箭头的跟随 —— 挂目标 Player.Move(与红毛同做法,10Hz级)在3秒窗口内持续更新同一箭头(UI_Arrow 全场单槽位,重发即更新,无需删除)
- fix(luis): 追踪改用 S_NOTIFY_ARROW+CharacterArrow 世界箭头(完全不碰地图/平板,原 S_PIN_MOVE 必然在平板留标记且 92000+pid 根本没有箭头);目标死亡即撤箭头
- fix(lian): 尸体追踪箭头到期必须主动发 S_REMOVE_ARROW —— SetArrow 无时长参数,停止重发并不会让箭头消失(即此前'只有20秒可见'不成立)
- change(lian): 仪式中断不再文本反馈;复活提示只给当事人与莲(不全房);加 ForceRevive 开关(无幽灵也可强推复活,供测试)
- fix(broadcast): #6 披露文本必须保持原文 —— WeaponTaken 不再被 Titled 包住(原版拿刀那条是裸发原文),标题只加在 StartBodyWhite 上
- feat(lian): N5 复活时补上与「翻DT点尸体」同款的黑洞传送视听(TeleportVfx+BlackholeTeleportSfx),且作用于复活后的人
- fix(lian): 修 DT 点判定的真 bug —— DeadlyTrickStage/Cabinet 解析到了客户端同名类(CS0184 警告),导致 InDtRange 恒假、假光照与仪式全部失效;改用显式别名
- fix(lian): N3 牵引提前到「阶段4点燃第一根蜡烛时」(钩 Occult.InteractOccultCandle,条件=进度差最后一次满且本轮未牵引),去掉原先全亮后的重复牵引
- fix(lian): N4 仪式判定改为「场上任何有莲能力者位于DT点范围内」即可推进(翻尸/点蜡烛可由他人代做),没有莲则不触发;放弃判据同此
- fix: N1时停结束后才清空体力(延迟);N2尸体追踪持续20秒(每秒重发箭头);N6白方开场白改用WeaponTaken捉迷藏开始了~
- docs(lian): 修正两处过期注释（牵引已改为范围约束，不再依赖 MoveLock 钉死）
- fix: 频闪改为默认关的开关(BlinkGauge);灵魂牵引改为「限制在DT点范围内」(跑出半径拉回边界,不再用MoveLock钉死)
- feat(keylock): 上锁进度条频闪 —— 充能期间每秒在「当前进度」与「0」之间交替重发,不依赖客户端是否自走一格
- fix(lian): 按需求原文校正两处 —— (B1)灵魂牵引改在阶段4推进时而非复活后;(B2)钩 CreateHiddenCorpse 使「埋入新尸体」即清零当前仪式
- fix(lian): 「每阶段唤起一根蜡烛」改为给莲本人发 S_NOTIFY_DEAD(触发她的 UI_SoulSence 感知动画),原先误做成推魔法阵那6台蜡烛设备
- feat(lian): 仪式每完成一个阶段就唤起一根蜡烛(推 Occult 的 State=3 并广播;原版6台蜡烛设备)
- feat(lian): 尸体追踪用过一次后,莲的技能槽长期显示冷却值13(每秒补发;-1不可行因客户端Math.Max(0,value)会夹成0)
- feat(lian): 尸体复活仪式4阶段 —— 钩原版 CheckSummonCandles(翻出尸体那一刻)推进阶段,同一尸体+连须在DT点内,第4阶段复活(反射置IsAlive+Move(force)广播S_RESPAWN)并牵引10秒
- feat(lian): DT点能力(1)站范围内仅给本人下发假光照以放行蜡烛交互 + (7)有人死时广播与原版路易斯同款的尸体追踪警告(CorpseArrow+警告音)
- refactor(blackvision): 抽出 SendAreaLight(player, isLight) 作为「假光照」唯一出口，黑灯/恢复都转调它（供莲的假亮灯复用）
- feat(keylock): 上锁改为两段 —— 持鱼在 ArmWindow(默认3秒)内起手连敲激活，之后每敲 E 推进一格，满格上锁
- feat(luis): 念力重写 —— 被标记目标死亡时杀手时停3秒+清空体力（凶手是路易斯则豁免）；技能转30秒周转；每15秒给3秒位置追踪
- change(broadcast): 自动发刀模式下白方开局消息 —— 黑方身份挪到最开头，其后接与自取刀模式一致的开场白
- docs(agents): 厘清「备忘/调查」与「正式文档」的区别 —— 前者放 .tmps 不进 git，docs/ 只放要长期维护的文档
- change(teleport): 预警范围定为黑方 2 级视野上限（基础 ×2，视野为加法叠加）
- feat(keylock): 拿鱼按 E 给门约1秒短保护（每按刷新，黏门者与持鱼者放行），替代原先整段锁死；含跨局清理
- fix(keylock): 胶未干期间任何人都不放行（原先该保护只在锁定态生效，别人可在等胶时开门打断上锁）
- change(teleport): 预警音效与落点特效的范围改为与黑方地图外边界视野等同（AoiCulling.ExitRange）
- fix(radar): /rad 只对触发者生效（原先一人使用、全房白方都看到全图，与每人独立次数冲突）
- fix(dummy): 「随机」原样交给系统（不再自己挑角色），登记即算成功不再反复重试
- fix(soulsense): 加 1Hz 到期兜底 —— 否则站着不动时加速到期不会恢复（RefreshSpeed 只在速度需重算时才被调）
- feat(skill): 莲（灵魂感知）—— 每感知一次死亡获得 30 秒 +50% 移速（相乘叠加，实验性，默认关）
- fix(miyuki): 重发节流改用单调浮点时钟（SurviveTime 是 int/秒，200ms 节流被吃成每整秒一次）+ 默认间隔 200→100
- fix(dummy): 离开对局时就对齐跟踪表与座位（StartLobby 时 IsDummy 已被 vanilla 抹掉，判据失效导致跨局席位不清）
- fix(dummy): 先解析「随机」再调 PickCharacter —— 修「假人不会自动随机」（-2 被原版登记进 40 秒倒计时）
- change(command): fish 开局限制 60s→10s；条件失败改用命令专用措辞（fish 报「非售货时间」）
- feat(miyuki): 加测试开关 ShowSelfOnRadar —— 把自己也画进全图标记，用于判断重发间隔
- feat(ui): 大厅「详细设置」新增 HideAndSeek 设置页签框架
- style(miyuki): 清掉上轮替换挤重的行尾注释
- feat(miyuki): pin 重发加「有人移动时按 ResendIntervalMs 节流重发可动段」
- chore(command): 删掉迁移后确认已死的字段与方法（配额/冷却记录已由引擎承担）
- fix(command): fish/soda 的玩家可见文案逐字挂回原键原文（改造不改措辞）
- feat(command): 6 处提示接到三层措辞查找 —— 每条命令的措辞可单独定制
- feat(command): 三层措辞查找（注册表行 > Texts的「命令名.键」 > Texts通用键）
- feat(command): 命令措辞可在注册表行内注册（quotaText=/roomCdText=/cdText=/usesText=/blockedText=）
- feat(command): soda 的配额与冷却也改由引擎四层空间执行；两侧自管逻辑移除
- feat(command): fish 的配额与冷却改由引擎四层空间执行（值仍取 [KeyLock] 段）
- feat(command): 注册表语法支持 quota= / perQuota= / roomCd=（四层空间可配置）
- feat(command): 引擎加「四层空间」配额与冷却（全房/每人 各分 配额/冷却 两层）
- fix(command): /refresh 失败不再扣次数 —— 延迟结算失败时回滚次数与冷却
- fix(command): help 不再被特判(AllowOutsideSurvive 从此真正生效) + 冷却提示按命令分键
- chore(broadcast): 删掉第二个零调用死函数 NoticeTo
- refactor(chat): 建包收敛到 Core/ChatOut 唯一入口，删两处重复与一处死代码
- fix(debt): 传送保护跨局泄漏(双保险) + 拒绝分支日志 + 假人两段合并 + 美幸残留清理 + 过期注释
- docs(agents): 修正两处失真 + 补上可手写配置绑定的合法例外
- docs(broadcast): CharacterNameOf 补写'为何不复用 Managers.GetText'
- fix(broadcast): 黑方公开按用户五条纠正修订
- feat(broadcast): 公开黑方身份（RevealBlackOnKnife）

## v1.1.0

- ﻿feat(miyuki): 黑方美幸双方案（BlackMode）+ 首包不补间/后续补间 + 修可动段配置失效

## v1.0.7

- fix(rule): elapsed 语义修正 —— 减掉 SurviveTime 的基数 420；新增 survive>= 保留绝对值写法

## v1.0.6

- fix(miyuki): 地图上能看到尸体/幽灵 —— 快照的可见性过滤实际没生效

## v1.0.5

- change(dummy): [Dummy] 改为默认启用 —— 段开关表示「启用假人功能」

## v1.0.4

- fix(dummy): 清理不再依赖段开关；SpawnedIds 判据补 IsDummy；释放假人座位

## v1.0.3

- fix(dummy): 假人残留会让真人被强制随机

## v1.0.2

- fix(quota): 配额与冷却跨局不清导致第二局开局即售罄 / CD 中

## v1.0.1

- fix(soda): 对局结束后速度加成不清除

## v1.0.0

- release: 冻结为 1.0.0 —— 独立化改造前的正式版本

## v0.4.3

- docs(agents)+chore(verify): 固化两条规范，并给 verify.ps1 加"补丁嵌套层级"检查

## v0.4.2

- fix(keylock): 两把锁重叠时严格按「剩余时间更长」取用

## v0.4.1

- feat(command): 别名对称 —— 注册表写内置别名时，打主名也命中同一条

## v0.4.0

- fix(powerrepair): 走原版路径恢复电力 —— 逐个调 Fusebox.ConnetCable

## v0.3.9

- fix(fusebox/rep): 黑方看不到电箱 = 补丁从未挂载；/rep 修不好光照 = 缺 RefreshLight()

## v0.3.8

- fix(texts): FishEmpty 补回后缀 —— 鱼已售罄，请稍候重试。

## v0.3.7

- polish(texts): 删掉 5 条不属于「命令报告」的文案

## v0.3.6

- polish(fish/soda): 按审阅意见收紧文案与判定顺序

## v0.3.5

- feat: 提灯改为"鱼"，汽水改为配额制（用户需求批次）

## v0.3.4

- fix(keylock): 提灯锁自然到期后门打不开 —— 过期记录没清理，伪造状态一直把门写回关

## v0.3.3

- change(soda): 两罐汽水不叠加 —— 有效期内再次使用在服务端吞掉（不播音效/不消耗/不重新计时）

## v0.3.2

- change(keylock): 提灯物品改用 4006 LanternBlue（蓝提灯）

## v0.3.1

- feat(soda): 汽水 —— 补上原版 SpeedUp 只有音效的缺口

## v0.3.0

- feat(keylock): 以太提灯 —— 手持提灯在门口开合两次给门上锁

## v0.2.5

- chore: 部署链路加固 + verify.ps1 静态检查 + 复查发现的行为修正

## v0.2.4

- fix(config): v16 段迁移整段空转 —— 用户定制会被静默丢弃（阻塞级）

## v0.2.3

- fix(mission): MissionBridge 五个读取口缺 Ensure() —— /sta /rep /refresh 会永久失效

## v0.2.2

- refactor(command): 段名去偏为 [Command]；/reload 移出游戏内；白方命令恢复静默

## v0.2.1

- fix(corpsereport): 目标 2 改为与其它三条路径同层同法拦入口

## v0.2.0

- feat(command): 命令文本规则引擎化（desc= 与 Texts 文案表）
- refactor(command): 命令系统合并为单一模块；/refresh 改走原版 ClearMission

## v0.1.1

- fix(mission): 任务进度顶满 100% 时不再直接进审判

## v0.1.0

- chore: 补齐 8 个功能段的 Diagnostics.Hit —— 让 hs_check 真正可用
- fix: /refresh 不再把任务进度顶到 100%（此前会制造原版不存在的状态）
- fix: /refresh 补上 MissionTimePenalty 房规折算（此前它绕过了扣时房规）
- change: /sta 时停音效改为以被冻结的黑方为圆心，不再以命令执行者为圆心
- change: /refresh 改为"刷新网络连接"，两段反馈 + 5 秒联网延迟
- change: /refresh 的弹窗名默认改为 17 = ScFixPc「网络系统维护」
- fix: /refresh 报"任务系统不可用" —— Traverse 解析不出属性；并加上任务完成弹窗
- revert: 撤回 bc1132e（诊断改动的行级替换误删了 TrySpendProgress 等方法，导致 16 个编译错误）
- diag: /refresh 补失败分支日志与进度值，便于定位'任务系统不可用'
- feat: 新增 /refresh（刷新身份记录）—— 黑白双方可用，每人独立 5 分钟 CD
- feat: 白方 /help 改为按配置动态生成；新增 /reload 重读 .cfg
- revert: /lck 与 /tp 只改 .cfg，不动代码默认值
- change: /lck 与 /tp 的冷却 60 → 90（brk 保持 90）
- fix: 强制恢复电箱后补上广播（原来只改服务端字段，客户端不知道）
- fix: 修一个电箱就真的把所有电箱恢复（原来只是"屏幕亮了"）
- fix: 电力恢复后电箱标记残留（"灯亮了但还能继续修"）+ 补诊断日志
- fix: 恢复大厅进房介绍的拆条与间隔（此前误改，把大厅也合并成一条了）
- fix: /brk 的冷却改为 90 秒（配置默认值一直是 0，覆盖了内置的 90）
- fix: 黑方在拿刀瞬间补发可拆电箱/武器架标记（根因是时序）
- refactor: 拆成 SendRawTo / SendWrappedTo，核心函数不再做场景判断
- fix: 原样发送的判据应该是"通道"，不是 MaxLinesPerMessage
- change: 不拆条时原样发送，不做宽度折行
- change: 整段放进同一颗气泡（不拆条）；间隔默认关闭
- refactor: 分条间隔改由调用方决定，通用函数不再猜意图
- tune: 间隔发送仅用于大厅引导，局内播报不延迟
- feat: /cre 加 v|s|t 标注；进房介绍按间隔逐条发送
- revert: 撤回擅自改写的开局播报文案，恢复原文
- fix: 开局播报压成一行（弹泡一次只显示一条，后一行会顶掉前一行）
- feat: 黑方恒黑灯加运行时热更新开关 [BlackVision] BlackVisionEnabled
- fix: 补齐配置迁移 v9 —— 行宽与播报文案在 .cfg 里一直是旧值
- fix: MaxLinesPerMessage 改回 3（按"单条不超过 84 汉字"反推）
- fix: 开局提示改用阶段自适应通道（原来走 NormalChat，生存阶段不渲染）
- tune: 每条消息合并 6 行（原来 3 行），少切消息
- fix: 视野倍率改回 0.5；并让已升级的效果跟随配置热更新
- change: BlackPinRange 默认改为 -1（跟随视野配置），并加迁移
- feat: 黑美幸 pin 范围的判据做成配置项 [MiyukiScan] BlackPinRange
- change: 黑美幸不再独立 —— 与白方共用 pin 通道，但只标 AOI 范围外的人
- fix: 美幸作为黑方时地图只显示 1 秒（UnlockSeconds）→ 改为 3 秒（MarkerSeconds）
- change: 文案「最低任务完成量」→「最低任务完成需求」
- change: 升级播报去掉【黑学分】前缀
- fix: 行宽按汉字计（52）；移速每级 0.1667（满级 1.5）；升级后热更新移速；help 显示最终值
- fix: 进房介绍按"每条消息 3 行"合并，而不是每行发一条
- fix: 播报通道统一 —— 死亡通告进公共发信机；/tp 落点文字改用弹泡；Notice 按阶段自适应
- fix: 修掉子代理复查发现的三个真 bug（第 4 条系误判）
- change: ProximityAlert 默认关闭并静音
- balance: AOI 内圈 700/外圈 900；视野升级倍率 0.5 → 0.6；新增接近预警
- balance: 学分分母改为"可击杀人数"（白方总数 - 1）
- fix: 升级播报改发公共发信机（DeviceChat），NormalChat 在生存阶段是白发
- fix: 学分超发（真 bug）+ 美幸白点提前消失
- change: 死亡通告只进密聊通道；升级播报的"倍"改为百分比
- fix: 进房介绍/开局提示按行宽自动折行，避免聊天栏截断
- refactor: 配置迁移改为"只在仍是旧默认值时才推进"，绝不覆盖用户显式设置
- change: AOI 内圈 750 → 900；修复配置迁移的"只补缺失"语义错误
- feat: 进房介绍延迟改为 10 秒；对局中加入者回大厅补发
- fix: 进房介绍改走聊天栏（原来走 SecretChat，在大厅根本看不到）
- change: 开局提示补充 /help 引导；说明进房介绍的可见性限制
- change: 文案批量调整（进房/开局/帮助/升级）+ 取消非必要公开播报
- change: 白方命令文案用词统一（扫描 / 冻结）；TEXTS.md 改为纯原文清单
- chore: TEXTS.md 重做为"按功能分组"，补齐帮助与升级播报
- chore: TEXTS.md 收紧为"仅用户界面文本"
- fix: 死亡/升级播报改双通道；落点音效纠正为 WarningSfx；导出提示文本清单
- change: /sta 与 /rep 不再全房公告，只有执行者自己知道
- feat: /cre help 显示逐级实际数值（而不是等级）
- change: 命令名缩短至 3 字母内；升级播报改为"提升已至"；击杀不再单独播报
- fix: 落点提示的广播半径 99999 → 1792（原版黑洞值）
- fix: 进度广播换包 + 落点特效纠旧值 + Suppress 屏蔽自我改写 + 速度判断收紧
- fix: 时停改为完全复刻原版 UseTimeStop（EBuffType.TheWorld）
- fix: 时停改为拦截服务端 Player.Move（这才是真正的时停）
- fix: 扣进度基数算错（这是"没扣"的真因）+ 音效换回 WarningSfx + 补特效日志
- change: 回执行宽 28 → 40（20 个汉字）；帮助文案放宽
- fix: 彻底删除 /tp 的箭头逻辑；落点特效改用 BlackHoleVfx；黑洞状态加过期窗口
- fix: /radar 无 CD + /stasis 无效果 + 箭头残留（迁移 v3）
- change: /credit 无参打印完整状态；/credit 支持 v|s|t；hs_debug 可加学分
- fix: /tp 落点提示全部改走世界坐标 + 特效/音效可配 + 删死配置
- fix: 命令回执按显示宽度折行（黑白两侧），解决帮助显示不全
- fix: /stasis 扣进度失败（类型错）+ /tp 落地黑洞特效可关
- feat: 配置迁移机制 [ConfigMigration] —— 解决"旧 .cfg 里的默认值永不更新"
- feat: /tp 预警增加"落点世界特效 + 目标文字提示"
- change: 帮助符号化精简 + 关闭 /tp 预警箭头
- feat: /credit 黑学分命令 + 白方 /stasis 与 /repair
- feat: 黑学分系统 [KillUpgrade] —— 击杀获学分，换取三项强化
- fix: 白方命令"没有任何反应"的真因 —— 回执走错了通道
- feat: hs_debug —— 把非常规测试操作收拢成子命令
- fix: /list 不在帮助列表 + /break 支持两个电箱 + 帮助精简
- fix: 假人秒选失效 —— 根因定案并修复（子代理带实机日志闭环）
- change: 帮助文档重做 —— 补齐用途 / 用法 / 条件 / 冷却
- fix: /tp 目标在柜子（或游戏机）里时会卡进柜体
- fix: 选角失效的根因 —— 处理过就出队，而 PickCharacter 会静默失败
- chore: /help 补全、/list 一行两个、选角与白方命令加诊断日志
- fix: IsForce 恢复 true（去飘动）+ 白方命令加诊断日志
- fix: 美幸扫描的三处缺陷（实测反馈）
- change: 美幸扫描的判据由「角色」改为「技能」—— 支持 Soi 偷取
- feat: 美幸被动 [MiyukiScan] —— 每 15 秒扫描全图（黑方临时解除 AOI）
- change: /radar 回执文案调整
- feat: 白方公开聊天命令通道 [WhiteCommand]
- change: 拆除电箱成功时不再发文字回执
- feat: 黑方密聊命令 /list —— 列出玩家 ID 与昵称，便于 /tp 指定目标
- fix: SkipDummies 默认改为 false —— 这是"雷达完全无效"的真正原因
- fix: IsForce 改回 false —— 上一版把它当成"看不到其他人"的原因是误判
- fix: hs_dummy add 座位 0 的随机角色被误判为非法
- change: 电力恢复判据由「剩余未修数」改为「已修电箱数」(RepairThreshold → RepairCount)
- fix: S_PIN_MOVE 漏设 IsForce → 这是"平板上只有自己"的根因
- fix: 雷达改走 S_SABOTAGE_MISSION 通道 + 跳过自标 pin
- fix: hs_radar 加入命令列表；回大厅/进审判改为直接关闭雷达
- change: WhiteRadar 支持两种外观方案（Badge / PureDot），可热切换
- feat: 白方全图雷达 hs_radar（仅 Host 侧，不区分阵营）
- fix: 内置命令兜底 + 拆电一次拆够两个 + 聊天按 3 行分段 + 电力阈值默认 1
- change: 锁门改为"真假状态"——白方看到原生锁定，黑方看到只是关着
- feat: 新增 /lock 与 /tp 两个密聊命令
- change: break 默认条件改为 fusebox，去掉误导性的示例
- feat: 新增 fusebox 条件；break 默认改为由原版派发节奏决定可用性
- feat: 密聊命令改为配置化注册（条件/CD/次数/效果），不满足即拒绝
- fix: 密聊命令的冷却提示改用手算，修掉构建错误
- feat: 密聊命令改为斜杠前缀，带 CD、帮助回执与无目标反馈
- feat: 动态规则引擎（实验性平衡项 5/6，全部完成）
- feat: 黑方可经密聊拆电（2/6 的第二半）
- feat: 白胜任务进度门槛（4/6）+ 黑方可见可拆电箱（2/6 的第一半）
- feat: hs_grant 支持排除名单（实验性平衡项 6/6 的命令入口）
- feat: 电力恢复阈值（实验性平衡项 3/6）
- feat: 黑方移速倍率 + 发刀排除名单（实验性平衡项 1/6、6/6）
- feat: 尸体报告改写为搬运（一次解决报告/拖尸/交互抢占三个问题）
- feat: 规则改动播报改为"大厅即时 + 回大厅补播"
- fix: .ps1 补 UTF-8 BOM（无 BOM 时 PowerShell 按 GBK 读，中文字符串被截断导致语法错误）
- chore: 新增 redeploy.ps1 —— 一键关游戏 + 部署 + 重启
- fix: 选角重复尝试导致日志刷屏，把命令返回值冲掉
- change: 黑洞改动默认不区分阵营
- fix: 黑洞改动限定为仅对黑方生效
- chore: 删除无效的 CorpseCtorHook
- fix: 黑洞特效改为在"实际落点"显示，而不是拦掉
- feat: 新增 hs_grant —— 控制台热切换发刀模式
- chore: 露娜免疫的拦截日志打印判定依据
- fix: 修复上一个提交的语法错误；hs_tp 去掉 in、所有报错统一带用法
- fix: hs_tp 移除 in 支持；所有相关报错都带完整用法
- fix: 假人未指定角色时改用游戏内置「随机」选项；hs_tp 支持 in
- feat: hs_dummy add 返回假人的实际角色；补规范第 8 条
- fix: 选角钩子独立成 DummyPick 段（默认启用）——这才是假人不选角的真正原因
- fix: 假人选角改为在 PickCharacterTick 重试，并加成败判定日志
- fix: hs_dummy chars 按角色去重；补 Hasung 中文别名
- fix: 兼容 hs tp 空格写法；死亡文案改为"已经死亡"
- feat: 假人角色支持按名字指定，并新增 hs_dummy chars 角色表
- feat: 新增 hs_tp 传送命令
- feat: 自动发刀模式改为从源头锁死武器架（非降级方案）
- fix: 多余武器改为没收；假人改为在选角阶段真正选人
- feat: 播报文案重做、身份定向、防双黑方、击杀加时
- fix: 假人可指定未解锁角色；死亡通告的 total 改为白方总数
- feat: 五人反馈落地 —— 播报与灯效调整
- feat: 新增 hs_roomname 命令（修改房间在 Steam 列表里的名字）
- docs: 新增子项目开发规范 AGENTS.md
- fix: 补回 3 个被 write 覆盖掉 BOM 的源文件
- fix: 源文件补 UTF-8 BOM（中文乱码）；黑胜判定；幽灵可见；大厅黑灯
- perf: AOI 恢复提速；黑灯解除收敛为单一路径
- fix: hs_* 命令同时写入日志流，终端不再"无返回"
- fix: AOI 剔除是单向的，被剔除者永远无法恢复
- fix: 修掉 ChangeGameState 的 Harmony 歧义；补上命令列表缓存刷新
- fix: 假人角色池回退为全部可选角色
- feat: 灯效开关与 hs_flash 命令；自查修复四处
- feat: 假人模块 + 技能与 AOI 兼容 + 黑洞防护
- fix: 清理 AOI 可见时间记录，避免跨局残留
- feat: 新增开局灯效；澄清任务扣时倍率语义
- fix: 修复离开对局后黑灯不恢复；补关键功能验证日志
- fix: 修正 Corpse 同名类型解析，并让 hs_* 出现在命令列表中
- fix: 修复两处 Harmony 目标解析失败
- feat: 增加运行期自检机制
- feat: 捉迷藏玩法模块（房主端独立插件）
