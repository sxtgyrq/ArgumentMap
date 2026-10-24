# Argument Map 测试

LogicalNodeInstanceConjunctionTests / LogicalNodeInstanceConjunctionMySqlTests 验证 LN10 用 ∧ 连接自动生成的命题，保留参数和别名内的字面 ∧/⋂，覆盖编辑/剪贴板/导入、真实 v34 数据及全部撤销快照升级、父类改名/移动/删除后撤销、其他 LN/道路/Cross/IsChanged 保持不变、版本仅增加一次、数据库 INSERT/UPDATE 拒绝旧连接符，以及数据/历史/升级标记/发布四类故障回滚或续接。权限测试使用独立 GUID 测试账号和数据库，验证 binlog 开启且 trust 关闭时在写入升级数据前明确拒绝，不改变 MySQL 全局设置。

ConnectionExtensionCrossTests / ConnectionExtensionCrossMySqlTests：前往与寻找各覆盖单向/双向旧线。接入点因旧节点占用而外延 1 单位，外延端恰好与另一条线路相交；验证唯一选定 Cross 为 I，外延额外 Cross 为 O、两个方向均不能换线，且真实 HTTP/MySQL、重启、后续 K 和一次撤销保持正确。

LogicalNodeFindTests / LogicalNodeFindMySqlTests 及 LogicalNodeGoToUiTests 的寻找分支覆盖从 LN1 顺向接出、只穿过 LN2、六种 LN 尺寸、最短新增长度、双向道路、分支/循环、旧 Cross 单向权限、避开 LN/已有两线交点、Cross 端不超过 1 单位调整、伪造通路拒绝和并发改线后的重新校验。真实 HTTP/MySQL 验证新 Cross 的普通 I 接出、其他 O、无关 IsChanged 保留、后续 K、重启/幂等重试/一次撤销，以及 Cross、节点、投影、撤销日志写入失败整事务回滚。实际窗口从“寻找”按钮进入，生成方向预览、Enter 保存并 Ctrl+Z 恢复；渲染为 .artifacts/renders/ln-find-preview.png。

CrossPendingVisibilityTests / CrossPendingVisibilityMySqlTests：对照旧项目 `IsChanged >= 0` 查询，修改/缩短线路后隐藏负标记 Cross，保留数据库中的通行设置供 K 复用；逐段 K 恢复仍有效的交点，最终清除失效记录。覆盖整线删除后的 WPF 缓存刷新、两台 Server 读取、重启、数据库撤销，以及导入/粘贴/框选/撤销返回地图不会重新显示无关的待算 Cross。只读查询不改动待算标记或撤销历史。

现有 ArgumentMap.Tests 已接入 MSTest，可在 Visual Studio 测试资源管理器中按测试、类别或类运行。项目已在 ConsoleApp1/AIMap.sln 中，无须重复建项目。

框选移动：MapAreaMoveTests / MapAreaMoveMySqlTests / MapAreaMoveUiTests 覆盖完整线路及 LN 框选、Cross 缺少所属线路拒绝、平移后五种 Cross 状态恢复、节点序号与比例、LN 0/多位置更新、未选中 LN 的关联增减、无变化幂等、权限/锁定/待 K 计算、源对象及 Cross 设置版本冲突、客户端几何不可信、目标线路/LN/Cross 全部约束。真实 HTTP 与 MySQL 覆盖两个独立服务并发、提交时目标新增冲突、九个 SQL 写入故障点五表全部回滚并重试、重启与后续 K/J 幂等，以及 18 位原始 DECIMAL 比例保留。WPF 检查 F7、菜单、按钮、Enter、Esc、预览失败重试、忙碌与重复提交保护和三类缓存一起更新；渲染附件为 .artifacts/renders/map-area-move-preview.png。仅创建独立测试库，正式地图不加入测试对象。

框选删除：MapAreaDeleteTests / MapAreaDeleteMySqlTests / MapAreaDeleteUiTests 验证 F7 完成框选后 R 和菜单删除原对象，移动预览不改变删除范围；完整线路/LN、Cross 与位置清理，释放唯一开始节点，保留 LN 清理已删道路关联并递增版本，LN 单独删除保留道路，保留无关待 K/J 数据。伪造/缺失/重复选择及权限、锁定、源成员和 Cross 版本变化均拒绝。真实 HTTP/MySQL 覆盖过期会话、双服务移动与删除竞争、Cross/线路/位置/LN 删除和保留 LN 更新的五类 SQL 故障整事务回滚（含第二条线路删除失败）、重试及重启/K/J 稳定；新接口也纳入自动发现的全接口 LoginCode 审计。WPF 检查无选区、三类缓存同步、忙碌时 R/Enter/Esc/F7 保护和失败后重试。

LN 标签显示：LogicalNodeLabelVisibilityTests 使用实际渲染像素检查背景混合后保留 70% 下方蓝色线路颜色（含边长 4 和开始节点 18），隐藏后恢复原始像素、重新显示恢复文字。检查隐藏的标签不能命中、LN 方形仍可选择；菜单与工具栏默认显示及双向同步，刷新、定位和重置不改变显示选择，不产生数据或重算写入。对照图为 .artifacts/renders/ln-labels-visible.png 与 ln-labels-hidden.png。

边长 4：SmallLogicalNodeTests / SmallLogicalNodeMySqlTests 检查 F5 新增入口、编辑窗口尺寸、是／否节点由原路径穿过、17→18 约束升级与发布失败重试，以及缩小四个已存分支时第二次写入失败的整事务回滚。尺寸 4 同时加入半整数与边界、与其他尺寸重叠/接触、连续新增、编辑移动和倒角渲染回归。

LN 逻辑语言与示例图：LogicLanguageTests / LogicLanguageMySqlTests / LogicLanguageUiTests 覆盖 0–255 字符、Unicode 公式、旧调用保留字段、权限和版本、编辑/移动保留、16→17 迁移与发布失败重试、真实 HTTP、故障回滚和界面保存。DiagnosisDiagramTests / DiagnosisDiagramMySqlTests 使用工具数据脚本生成十个普通 LN（三条单向路、两个不连通 Cross、十二条位置），覆盖重复导入拒绝、保留旧重算任务及四张表故障下整图回滚。LogicalNodeBranchEditTests / LogicalNodeBranchRepairMySqlTests 验证四个是／否 LN 的编辑、移动、删除和重算，浏览状态 7 → E、方块/文字点击选择和按钮编辑，以及旧数据补充四个 LN 时的整事务回滚与幂等。工具脚本不编入业务或 WPF 应用，不增加模型或表。渲染图为 .artifacts/renders/equipment-diagnosis-diagram.png。

Cross 的 IsChanged 流程另有专项回归：真实 MySQL 触发器记录框选移动先标 -1、恢复 0、删除剩余负数的写入顺序；两个交点平移后复用旧主键时分别恢复各自设置。恢复为 0 的写入故障必须回滚五张地图表、临时标记及事务内审计记录；不影响其他线路原有待算 Cross、PendingSegments 和 DirtyLines。框选移动写入故障覆盖九个基础写入点，另含恢复阶段故障。

## 运行

流程图文本导入：FlowDiagramTests / FlowDiagramMySqlTests / FlowDiagramUiTests 覆盖 JSON 格式、未知/重复字段、别名与引用、相对锚点、半整数、五种 Cross 状态及随机编码方向换算、全部几何约束、F6 自动细分、旧任务保留、预览只读与重复导入拒绝。真实 HTTP/MySQL 覆盖五表写入故障整图 rollback、服务重启、预览后并发新增冲突、双服务同位置导入只成功一次、登录和负责人注入拒绝、后续 K/J 幂等。WPF 检查菜单入口、粘贴、地图选点、预览与确认、编辑后预览失效、Esc、忙碌保护与重试、三类缓存同步及窗口/地图渲染。语言文档为根目录 FlowDiagramLanguage.md，示例在 WpfArgumentMap/Examples/equipment-diagnosis.flow.json。

流程图逻辑语言必填：FlowDiagramLogicTests / FlowDiagramLogicMySqlTests 覆盖四种尺寸的 logic 缺失、null、空串及 Unicode 空白，1/255/256 字符边界、批次末尾节点非法时整批不变、中文逻辑符号及原文保存。WPF 在选点和预览时显示具体 LN id、使旧预览失效、禁止确认，并支持修复后重试；独立测试库的真实 HTTP 验证绕过 WPF 仍拒绝无逻辑的数据、数据库原样不变、会话校验优先及重开仓库后的持久化。FlowDiagramGuideTests 核对嵌入说明、完整协议与任务模板的必填要求和审核稿要求；文档中的三份完整 JSON 均实际经过导入验证。

流程图制作默认值：FlowDiagramDefaultsTests / FlowDiagramDefaultsMySqlTests 验证省略 speed 时预览、导入和数据库持久化均为 100，显式 5/100/500 不被覆盖，已有线路速度保持不变。WPF 提示及载入示例、三份完整说明内示例都使用速度 100；推荐分支 Cross 为 connected，重算保留双向换路。原有五种 Cross 状态专项用例继续验证单向状态可显式导入。关联到已有 LN 时检查位置的 RoadVersion 与最终线路版本一致、J 幂等，而不假定事务内几何和属性两次更新只递增一次 LN 版本。

LN 标记与 K/J 分工：LogicalNodeChangedLifecycleTests / LogicalNodeChangedMySqlTests / LogicalNodeChangedUiTests 覆盖 J 恢复 -1 为 1、删除失效位置、插入缺失关系、零位置保留 LN、重复重算幂等与资料保留；框选移动保留同一 LN 上其他线路的待算位置。真实 MySQL 审计触发器验证移动和 J 的标记/恢复/删除顺序，四个阶段的 SQL 故障整体回滚、重启、双服务 J 与线路编辑并发。WPF 浏览状态 J 与 K 分别调用 LN 和 Cross 接口，互不清理对方的标记。

在 E:\ProjectYrq\ArgumentMap 的 PowerShell 中：

```powershell
.\Test.ps1                 # 全部测试
.\Test.ps1 -Suite Unit     # 几何、业务与内存事务，不依赖 MySQL
.\Test.ps1 -Suite Http     # 真实 HTTP 与编辑流程；LN 持久化用例也使用独立测试库
.\Test.ps1 -Suite MySql    # 数据库事务、重启、迁移、故障和多人并发
.\Test.ps1 -Suite Rendering # WPF STA 渲染与面板检查
```

也可以直接运行：

```powershell
dotnet test .\ArgumentMap.Tests\ArgumentMap.Tests.csproj --filter "TestCategory=Unit"
```

VS 中打开解决方案，选择“测试 → 测试资源管理器”，生成后即可逐项运行。程序集禁用测试用例之间的并行，避免 WPF Application 和旧回归计数的全局状态冲突；多人测试在单个用例内主动启动并发请求，仍验证真实竞争。

## 覆盖范围

| 类别 / 文件 | 验证内容 |
| --- | --- |
| GeometryUnitTests | 八方向、零长度及非法斜率、重叠、交点精度、45° 锐角与 90° / 135°、节点优先后段、半整数经过判断、整数吸附 |
| ViewportUnitTests | 默认中心、鼠标锚定缩放、坐标转换、Y 向上、边界、缩放限值与动态刻度 |
| LineSaveUnitTests | 节点数量和范围、重复点、序号、全图占用、线段重叠、锐角、成功保存、版本和负责人 / 锁定校验 |
| OrthogonalRoadTests / OrthogonalRoadMySqlTests | 不同 Road 的相交线段必须正交；八方向组合、端点与折点两侧、后续线段、有限线段、半整数及大坐标；HTTP 失败保留草稿、两个服务实例并发保存、SQL 故障回滚和重试 |
| LineTopologyTests / LineTopologyMySqlTests / LineTopologyUiTests | D 在节点之间拆分、B 选择两线后按方向预览并确认；原段方向内收缩/延长、135° 优先及 90° 备选；整数端点、地图边界、LN 节点/斜线约束、Cross 第三线限制、重叠及交角；旋转/镜像和斜线整数奇偶下对所有有界端点组合核对最小方案；Cross 编码侧重排保留通行方向、LN 位置分配及间隙清理；单事务复制/删除故障回滚、多人冲突和版本检查；真实窗口菜单/按钮/快捷键及方案预览渲染 |
| FrontAdditionTests / FrontAdditionMySqlTests | 5 切换前端/末端，A 连续补点；八方向整数吸附与预览共用前端锚点，原节点顺序保留；取消/重新编辑恢复末端，保存中禁止切换与加点，失败保留草稿及重试；F6 拒绝 LN 内节点、斜穿 LN、经过 Cross、锐角、重叠、斜交及占用节点；实际主窗口数字键/数字键盘/输入法/按钮/菜单入口；HTTP/MySQL 保存重新编号但保留编码与单向属性、更新 LN 位置及版本、K 保留 Cross 通行状态、SQL 故障回滚与重启/K/J 幂等 |
| LineSubdivisionTests / LineSubdivisionMySqlTests | F6 按实际长度 500 自动反复近中点细分；横竖线 499/500/501、45° 斜线 353/354 格临界、八方向及半整数中点、三种 LN 边长避让、节点占用与自身交叉处的新增点去重、LN 顶点擦过、合法 Cross 后段 percent 0、候选全部被占用的完整失败、非法几何不能靠插点修复、全地图长线和重复保存幂等；WPF 使用服务端返回的细分节点；真实 MySQL 节点/LN位置/Cross标记写入故障回滚与重试、K 保留状态、重启及 J 幂等、双服务实例与新增 LN 或节点占用竞争 |
| StoredSegmentLengthTests / StoredSegmentLengthMySqlTests | 长度上限适用于所有几何写入：新增/编辑、合并连接段及延长末段、拆分和反转；旧长段通过操作也细分，两侧结果及接口返回均使用最终节点；合并预览验证可细分，无合法整数点时不提供方案并拒绝提交；Cross/LN 位置、版本、通行设置与重新计算幂等；HTTP/MySQL 对三种操作的成功及新增节点写到一半时故障回滚，直接 SQL 检查库中超长段数量为零 |
| NodeEditTests / NodeEditUiTests / NodeEditMySqlTests | 绘制时 R 删除最近节点、E 锁定最近节点，移动鼠标再 E/Enter 确认，Esc 取消，草稿 F6 才入库；端点八方向及中间节点两侧同时约束，世界两端与半整数奇偶条件下对照小范围穷举完整保存校验，按距离和固定平局规则选择最近合法整数点；避让 LN、第三线 Cross、占用、重复、重叠、斜交、锐角并验证可细分；无解、无权限、过期会话/版本、锁定、非法输入不修改数据；真实窗口键盘/输入法/菜单/按钮、选线 E 属性与 LN E 功能保留、忙碌及失败重试、防止错误节点响应覆盖草稿、节点专用删除入口不删整线；HTTP/MySQL 候选搜索只读、F6 节点/LN位置/Cross标记写入失败完整回滚、多人在候选确认后新增 LN/Cross 再次拦截、重试和重启/K/J 状态保留 |
| CrossUnitTests | F6 后 K 再生成交点、逐段事务与最终清理、增删节点保留 Cross 编码对＋坐标标识及设置、后段位置 0、交点消失、整线删除、第三条线经过的端点 / 中间节点 / 线段、旧数据 K 拒绝及回滚、失效坐标不误拦截 |
| CrossDirectionTests / CrossDirectionMySqlChecks | P 重置、状态 2/3 的单向换路、Enter 记住出口与【流程、Shift 保存但跳过重新拉取、失败保留本地状态、方向随道路编码且独立于节点反转、重算保留、整线删除、第三线限制、双用户 HTTP 竞争与 SQL 回滚、半整数、9→10 约束迁移及中断重试 |
| LoginUnitTests | 无效账号、登录会话轮换、旧会话不能操作或退出新会话、授权业务失败时账号与地图一同回滚 |
| SpeedUnitTests | 速度默认 5，5–500 边界，非法值拒绝及回滚，面板输入校验，接口与控件删除通行方式属性 |
| DirectionUnitTests | 独立单向 / 双向字段、正向固定 1、非法方向拒绝、草稿及已保存线路反转、版本 / 负责人 / 锁定 / 会话校验、旧非法几何拒绝、多交点序号及非对称位置比例更新、两次反转恢复、Cross 标识及设置保留及属性面板提交 |
| CodeIdentityTests / CodeMigrationChecks | 10 位大写字母编码、非法编码拒绝、并发创建唯一性、Road1 编码较大、两侧字段跟随道路、按编码对＋坐标保留通行设置、数字表迁移及备份、复制失败回滚及重试、删除数字列和序列、复合主键、半整数及重启稳定 |
| ArrowRenderingTests | 八个方向的单向 / 双向箭头实际像素、保存方向属性和反转后的箭头、隐藏恢复原图、锁定绿色双向箭头 |
| LogicalNodeTests / LogicalNodeUiTests | LN 中心半整数、边长 6/12、整个方形边界；所有线路节点占用校验；允许线段穿过；已有 Cross 仅不连通可新增 LN、尚未计算 Cross 也拒绝；方形内新 Cross 自动不连通；F6 / K / Cross 修改校验；J 单事务重算全部位置、线路修改及反转自动更新、移除无效位置并保留 LN；权重、权限、版本；F5/A/7/E 工作流和界面失败保留输入 |
| LogicalNodeMySqlTests | LN 表与管理权限迁移、中文及半整数、数据库约束和重启；两个服务实例的 LN 新增与 F6 / Cross 状态竞争；LN 写入、线路修改 / 反转 / 删除、K / J 和 LN 删除故障回滚及重试；认证接口和失效会话拒绝 |
| LogicalNodePositionTests | LN 的 0/1/多位置、同一路多段、中心垂足、四个横纵方向的比例、增删节点和反转后的即时更新、移出方形清理位置、删除线路保留 LN、删除 LN 清理明细、J 一次提交且幂等 |
| LogicalNodePositionMySqlTests | 真实 11→12 一对多表迁移及移除旧主表字段；复制失败保留原表、发布中断重试；明细表 SQL 故障下 LN 新增、F6、反转、道路/LN 删除和全图 J 回滚；两个服务器并发新增/修改/删除不丢位置；复合主键、外键级联、坐标约束及重启持久化 |
| LogicalNodeReviewTests | 非法及边界属性、返回数组隔离、两种边长在坐标范围两端的密集横纵网格、长线段半整数垂足精度、6000 个有限斜线与独立整数区间算法对照、无变化保存及重算幂等、过期明细清理、账号权限、删除 LN 保留道路及 Cross 规则 |
| LogicalNodeEditorReviewTests | 刷新失败保留选择和明细、删除后刷新清除选择、J 已提交但刷新失败的正确提示、重入阻止、失效会话释放忙碌状态、服务端权限与多位置只读列表、版本冲突保留值和说明输入、重复点击及保存中关闭拦截 |
| LogicalNodeWindowEntryTests | 实际显示主窗口，通过 A、中文输入法处理后的 A、按钮和菜单直接提交新增 LN，无属性弹窗；菜单关闭但仍保留焦点时快捷键照常工作，菜单展开时保留原生导航；已有 LN 的编辑窗口仍可正常打开 |
| LogicalNodeDirectCreateTests | 直接保存中重复点击不重复提交；失败保留落点状态并允许重试，不显示虚构节点；客户端缓存过期时仍由服务端拒绝节点占用、斜穿、非法 Cross 及重复开始；成功立即显示全部返回位置；点击工具栏 A 时保留最后悬停的地图坐标 |
| LogicalNodeReviewMySqlTests | 位置表缺失时完整重建及失败重试、明细写完后出现 SQL 错误的整事务回滚、属性/锁定/解锁版本同步和失败回滚、中文属性极限值、所有 LN 接口拒绝旧会话、非 admin 管理权限及身份伪造拒绝、并发保存/反转/J 下读取一致性 |
| LogicalNodeDiagonalTests | LN 内仅允许横 / 竖线穿过；两种边长及四个斜向均拒绝穿入；四角擦过允许，有限线段不误拦截；F6、K、J 旧数据拒绝及回滚 |
| LogicalNodeRenderingTests | 6 / 12 / 18 三种边长、不同缩放下四角半径 0.5 的实际像素；圆角保留方形范围，仅影响显示 |
| LogicalNodeStartTests / LogicalNodeStartEntryTests | 18 为开始节点且全图唯一，6/12 可重复新增；删除后可重新放置；沿用几何、节点及 Cross 校验；F5 实际打开边长选择窗口，选择后才进入落点，半整数吸附、越界拒绝、Esc 取消及正确显示选定边长 |
| LogicalNodeStartMySqlTests | 两个独立服务器并发创建仅一人成功；数据库唯一约束拒绝绕过业务校验的重复开始节点；明细写入失败回滚节点及开始名额；12→13 迁移保留原数据、发布失败重试及重启验证 |
| LogicalNodeSeparationTests | 三种边长全部组合的重叠、包含、四边/四角接触拒绝与 1 单位间隔放行；不同负责人共享全图规则；地图两端精确判断；删除后重新新增；历史违规 LN 阻止 F6/K/J，删除可修复；WPF 缓存校验及过期缓存下服务端拒绝 |
| LogicalNodeSeparationMySqlTests | 两个独立服务器同时新增重叠、共边或共顶点 LN 时仅一个提交；拒绝时原数据和位置不变；明细写入故障整事务回滚、合法位置重试、删除后新增及重启验证 |
| LogicalNodeMetadataTests / LogicalNodeMetadataMySqlTests | 只读 X/Y/边长、可编辑值/说明、界面与接口移除类型和权重；值 0–32、说明 0–255 字符边界和 null 拒绝；编辑保留几何、开始标记和位置；13→14 改名删列迁移及发布失败重试；多人版本冲突、夹带几何字段也无法修改坐标、SQL 故障回滚及重启 |
| LogicalNodeContinuousPlacementTests | F5 选定边长后，A 与左键连续新增；成功或非法位置失败都保留边长和预览；切换边长、Esc 结束、选择模式退出；18 仍全图唯一，保存中重复点击仍拦截 |
| LogicalNodeMoveTests / LogicalNodeMoveUiTests / LogicalNodeMoveMySqlTests | LN 编辑窗口提供移动按钮，鼠标预览、左键 / A / Enter 确认、Esc 取消；保留编码、边长、负责人和值/说明。校验半整数、边界、其他 LN 接触、线路节点、斜穿及 Cross 状态；0/1/多条线路位置全部重建；权限、版本冲突、无变化幂等、四类 SQL 写入故障回滚、两个服务实例与新增线路/LN/Cross 设置/资料编辑竞争；重启持久化及 WPF 渲染 |
| CrossRoadNamingTests / CrossRoadNamingMySqlTests | Cross 使用 RoadCode1/2、RoadOrder1/2、Percent1/2 和 RoadVersion1/2；LN 位置使用 RoadCode/RoadOrder/RoadPercent；真实 HTTP 字段核对和旧键拒绝；14→15 迁移保留通行状态、序号、完整 DOUBLE 比例及标记；复制失败回滚与发布中断重试；迁移后的 CHECK 名称恢复兼容；主键、外键删除及约束验证 |
| PercentDecimalMySqlTests | Cross 的 Percent1/2 和 LN 的 RoadPercent 均为 DECIMAL(20,18)；18 位小数及 0–1 约束、循环比例和长线段的小比例、Decimal 参数读写；15→16 双表事务复制与一起发布、原数据及标记保留、第二张表复制失败时两表回滚、发布中断重试、两个初始化实例协调、重启和级联删除 |
| PercentPrecisionTests / PercentDecimalReviewTests | 计算、比较与写入统一 18 位小数精度，存取循环稳定；小比例 K/J 重算不增版本或更新时间；只改状态/资料保留原有 18 位小数；中文/法文/德文环境 HTTP 创建与刷新一致；定点写入故障回滚及重试；16 版缺表恢复、部分字段提前转换的迁移；长线路增删节点和两次反转后位置及 Cross 设置保留 |
| RegressionTests | 原有随机几何、完整 HTTP 编辑流程、登录权限、属性 / 锁定、Cross 重算恢复、旧数据选择及界面回归 |
| MySqlIntegrationTests | 中文与半整数持久化、服务重启、K 中断恢复、SQL 故障 rollback / 重试、两个独立服务实例与账号竞争、Cross 设置不丢失、属性 / 登录事务、表结构迁移、删除通行方式并迁移速度、独立方向字段与反转事务 / 并发、多交点反转后的重启恢复，以及第三条线限制 |

## 数据库与结果

FlowDiagramGuideTests 覆盖嵌入说明与根文档一致，最小/分支复制示例经真实业务预览只读、导入成功及全部几何规则；菜单必须紧挨导入入口，无登录也能打开，重复打开复用窗口，Esc 仅关闭说明；四个复制按钮内容、剪贴板忙碌失败、目录跳转、普通/小窗口和字段表渲染；导入窗口说明按钮/F1 保留文本、插入点及已通过的预览，且不调用保存。复制测试使用替代写入函数，不改动用户剪贴板。

完整协议补测：资料/检索任务表从嵌入资源读取，输出包含所有字段、约束、自查公式和资料/来源规则，不依赖原有聊天；协议中三份完整图形示例逐一经真实服务导入，并与软件内示例按 JSON 结构核对，防止文档漂移。新的两路分支样例验证 Cross 在全部 LN 外，main→negative 可以换路且逆向不行，六个 LN 各有一个线路位置，K/J 重算后方向仍保持。

LoginCodeEndpointTests / LoginCodeEndpointMySqlTests 从实际运行的 HTTP 路由表自动获取全部接口，唯一免 LoginCode 的入口是使用用户名密码登录的 /Login。对其余每个接口（含 Logout、只读查询、F6、K/J、拆分合并、框选移动及流程图预览/导入）逐一验证缺省、空串、null、错误、重新登录前的旧码、他人账号的码和不存在的用户名：必须 HTTP 401 + LoginExpired，不返回地图数据，不改变地图、待计算标记或账户会话。MySQL 双实例用例直接查询 DictLogin 核对登录替换和退出清空，检查另一实例立即拒绝旧码；并发登录最终仅一个码有效。测试从运行路由发现新增接口，避免只覆盖旧的固定接口列表。

局域网分层新增 ClientServerDeploymentTests：WPF 依赖闭包不能出现 Server/Business/Storage/MySQL/ASP.NET；客户端配置只存 ServerUrl，拒绝数据库/带密码地址、通配监听地址、非法配置；登录窗口可更改地址、失败重试及忙碌保护，并验证渲染。IndependentServerProcessTests 在独立 GUID 数据库中创建仅本机、仅本库的专用账号，复制独立 Server 的运行依赖后以新进程启动，使用两套 HTTP 客户端验证中文、权限、同位置竞争、SQL 故障原子回滚，以及服务退出后不会回退到本地服务；最终清理专用账号、私密配置和测试库。

合并负责人规则沿用旧项目：预览和确认提交都要求两条线路属于当前登录用户，管理员也不能合并他人的线路。专项用例覆盖两侧负责人不同、选择顺序互换、管理员跨用户请求、本人两条线路成功合并，以及预览后任意一侧负责人变化时在提交事务中拒绝；真实 HTTP/MySQL 用例核对拒绝前后所有地图表、Cross/LN 明细和时间戳完全不变。

合并确认在数据层的同一个 InnoDB 事务内完成：锁定并读取最新地图、验证最终线路与 LN/Cross、重算关联位置并持久化，全部合格才 commit；校验失败或写入异常 rollback。专项双服务实例测试先生成方案，再由另一用户新增 LN 或 Cross，验证最终提交重新拒绝斜穿 LN、节点进入 LN 和第三线经过 Cross，原线路及所有关联表不发生部分修改；写入故障测试另覆盖线路节点、Cross 和 LN 位置表的回滚及重试。

拆分/合并关联数据走查补测：拆分按两侧重新编号和分配 Cross/LN 位置，删除断开段的关系但保留 LN；合并微调端点后，旧段交点可消失、连接段可生成新交点，坐标仍在新线路上的 Cross 保留原通行状态。断言编码、RoadOrder、percent、垂足、RoadVersion、Cross/LN 版本与资料，保留其他线路的 LN 位置，清除受影响数据的过期标记。MySQL 用例覆盖重启持久化与再次 K/J 重算，要求全部地图表及时间戳保持一致。新项目在提交事务内完成重算，成功后不要求额外按 K/J。

MySql 类别读取 ArgumentMap.Server/appsettings.Local.json 的本机连接配置，但每个测试场景创建全新、带 GUID 的 ArgumentMap_Integration_… 测试库，并在 finally 中删除自己创建的库。不会在正式 ArgumentMap 库中建测试数据或故障触发器。需要本机 MySQL 已启动、该账号能创建 / 删除测试库和创建触发器。

统一脚本将构建、临时文件及 TRX 报告保存到根目录 .artifacts 下；报告名称为 test-results/argumentmap-all.trx 或对应类别名称，每次覆盖同名报告。WPF 图片位于 .artifacts/renders，也作为渲染测试附件加入报告。

控制台入口仍保留，可独立执行原有整套断言检查。MSTest 的“测试个数”与控制台的“断言检查次数”是不同指标，不混用。

框架与编写约定： https://learn.microsoft.com/en-us/dotnet/core/testing/unit-testing-mstest-writing-tests

MultiUserBackgroundMySqlTests 使用同一 Server、两个不同账号和真实 MySQL，在 K/J 单元已提交而整轮计算仍运行时，逐项提交另一账号的线路/LN 新增、线路/LN 编辑及移动、框选移动和流程图预览/导入。覆盖导入 SQL 故障整批回滚、Cross 原设置复用、位置重算、重启持久化、权限以及 LoginCode 失效。J 每次只提交一个 LN，故障单元回滚后可重试；后续读取移动、删除和新建后的最新对象。J 只恢复派生位置不使框选失效，真实资料修改仍拒绝旧框选。BackgroundLogicalNodeTests 和 BackgroundMapOperationsTests 补充 WPF 的 K/J 并行、前台优先、重复启动保护、取消/重启、线路草稿及框选/导入预览保护、旧后台读取不可覆盖新缓存。

LogicalNodeCoordinateMySqlTests 覆盖 LN4/6/8/12/18 的中心 X、Y：HTTP 新增/移动、DAL 直接事务、SQL INSERT/UPDATE 均拒绝整数及其他小数，包含旧一位精度会误收的 .49/.51 与高精度近似 .5。验证拒绝后全图及待算标记不变、合法尾零坐标及紧凑返回值、25→26 原子升级、DDL/版本发布故障关闭与重试、合法数据和时间/版本不变、缺少约束拒绝启动及派生位置表恢复。

LogicalNodeClipboardTests / LogicalNodeClipboardMySqlTests 补充 LN6、LN8、LN12 复制粘贴：键盘、编辑菜单、LN 菜单统一按鼠标落点新增，LN12 只给重复值编号并保留逻辑原文，LN6 只给重复逻辑编号且不改原 LN4 引用，LN8 两种符号及逻辑严格一致而可重复。覆盖跳过占用序号、只读来源副本归当前用户、旧 LN4 剪贴板兼容、LN18 排除、格式/身份字段注入拒绝、取消与切换边长清除副本资料、几何在预览后变化仍由 Server 拒绝、K/J 完成后预览保留。真实 MySQL 下两账号并发粘贴同一副本、位置写入故障整体回滚且不消耗序号、LoginCode 失效拒绝写入及重启持久化。测试替换剪贴板读写函数，不改用户系统剪贴板。

MapAreaContainmentTests 覆盖 LN4/6/8/12/18 的完整正方形框选：贴边可选，每条边及每个角少框入 0.01 都不选，LN8 同样检查完整外接正方形。混合选择验证仅部分端点、两端在框内而折点在框外、仅穿过矩形的线路都不选；移动与删除不改变被排除的对象，伪造选择把这些对象加入也被 Server 拒绝。Cross 在框边界也只有两条所属线路都完整选中才选中；缺少任一所属线路则跳过，仍可移动、删除其他完整对象。覆盖 LN 单独选择、一个所属线路移动后旧交点仍成立时复用状态/新交点恢复默认状态、删除清理及后续 K 不复活旧交点。WPF 测试走屏幕到地图转换、双向拖框、松开选择、移动落点与 R 删除的处理路径，并验证单击和零宽拖框不调用接口且可继续拖框重试。MySQL 专项覆盖跳过 Cross 后的移动/删除、关联写入故障整体回滚和重试、失效 LoginCode 拒绝及未选线路保持原样。

LogicalNodeInstanceTests / LogicalNodeInstanceUiTests / LogicalNodeInstanceMySqlTests 覆盖 LN10 重复实例、小写与中文、隶属自动生成、缺少父节点拒绝、LN12 跨所有者改名原子同步和旧版本拒绝、先处理实例再删除父节点、直接 DAL 拒绝无效归属、导入父节点前向引用/同批编号/已有父节点、SQL CHECK 与升级 26→27 及 2601 中断恢复。WPF 验证 F5 选父节点连续落点、切换边长清除模板、只读表达式、Ctrl+C/V 三入口、粘贴时父节点已改名拒绝、内凹圆弧及完整方形框选。MySQL 双实例并发新增同名实例、改名/导入故障完整回滚、登录失效拒绝、IsChanged 标记恢复及已有数据不变。完整协议增加两个同值实例的可导入示例。

LN10 父节点资格补充：仅含至少一个 ASCII 英文字母（A–Z／a–z）的 LN12 可供新增、导入、粘贴或改属使用；H人、HUMAN 可选，全中文、纯数字或符号项不显示。已有实例时父节点不能改为无英文字母的值，Server 和 DAL 拒绝并保持原资料；没有实例则按原规则改名。旧的纯中文实例保留，仍可读、移动、编辑说明和删除，不再允许新增/复制；过渡校验避免旧数据阻断无关事务。数据库架构仍为 27，无需迁移；需更新 Client 和 Server。

LogicalNodeInstanceNavigationTests 覆盖 LN10 的“定位所属 LN12”：按 value 匹配而非自定义 logic、读取父节点移动后的最新坐标、跨所有者只读定位、按 E 返回保留草稿、修改中的实例值、混合文字和旧中文实例、空值禁用、目标缺失/改名/重复、登录失效、单次查询与过期响应拦截。实际按钮及主窗口导航走完整 WPF 路径，确认查询不写地图，并保存渲染图 ln10-locate-parent.png。本项仅改客户端，使用已有 GetLogicalNodes 认证接口，无需 Server 或数据库升级。

LN8 七种符号（=、→、↔、⊇、⊋、⊆、⊊）：LogicalNodeRelationTests / LogicalNodeRelationLogicTests 覆盖值与逻辑语言同步、重复、导入补齐/显式不一致拒绝和七选项 UI；LogicalNodeClipboardTests 覆盖四种新符号的键盘、编辑菜单和 LN 菜单鼠标落点，MySQL 双账号粘贴覆盖故障完整回滚与 LoginCode 失效。LogicalNodeSetRelationMySqlTests 验证 SQL 二进制约束、HTTP 新增修改、导入故障回滚、跨 Server 并发重复/几何冲突，以及 27→28 自动升级、DDL 前中断和发布失败后的 2701 关闭标记与重试，全部使用独立 GUID 测试数据库。

LogicalNodeLabelVisibilityTests 覆盖 LN4/6/8/10/12/18 标签水平居中及中心点下方 0.5em 顶部间距、缩放后段落宽度不超过正方形、长标识符和中文换行、禁用省略及四行截断、70% 透明背景的像素混合、外溢标签的显示/隐藏点击范围、导入预览和菜单开关。渲染图 ln-centered-wrapped-labels.png 与 ln-centered-wrapped-import.png 包含六种尺寸，测试断言节点全部在屏幕内，人工核对水平居中、顶部间距、换行与线路透出。

LogicalNodeEquivalenceMySqlTests 覆盖 28→29 自动升级、2801 的 DDL 前中断和发布失败后重试、地图记录及位置 IsChanged 保持不变、当前版本仍使用六符号约束时拒绝启动、已有 ↔ 节点在派生位置表缺失后完整恢复。关系与逻辑语言测试增加 ↔ 新增/编辑/导入，WPF 通过实际下拉选择和保存；Ctrl+C/V 三入口及 MySQL 双用户并发、位置写入失败回滚和 LoginCode 校验均包含 ↔。

LogicalNodeSingleLineEditTests 覆盖六种 LN 的单行值/逻辑语言、Enter 与含换行粘贴的拦截、说明原生回车及 HTTP 保存/重开、长公式横向滚动、32/255 上限、LN8/LN10 只读和旧多行节点的未保存草稿。渲染图 ln-singleline-editor.png 检查单行字段与多行说明。LogicalNodeSingleLineTests 覆盖 CR/LF 和 Unicode 分隔符在新增/修改、导入及两种剪贴板格式中的拒绝，说明多行保留，内存/MySQL DAL 绕过校验时整笔回滚，以及隔离数据库中的旧多行数据读取、移动、K/J 和显式修正。


MapAreaPasteTests 覆盖 F7 组内所有可复制 LN 的新增编号、LN4/LN6 与 LN10/LN12 引用同步、五种 Cross 状态及双向通行含义、连续粘贴、仅线路／仅 LN、外部引用、跳过 Cross、原对象不变、源失效／权限／目标冲突和未完成 K/J 标记保留。MapAreaPasteUiTests 通过键盘和编辑菜单复制 F7 选区，验证鼠标落点、无提前写入、失败重试、Esc、忙碌重复请求、R 不删除原对象、文字焦点与 C 键隔离、畸形剪贴板及 LN18 拒绝；渲染 area-paste-preview.png 检查整组形状、标签、箭头和内部 Cross 预览。MapAreaPasteMySqlTests 使用 GUID 隔离数据库和两个 Server，覆盖真实认证、四张表故障完整回滚、原 IsChanged 保留／新关联有效、重启持久化、不同用户并发编号和同落点竞争，以及另一账号同时 K/J。

## Ctrl+Z 数据库撤销

MapUndoTests、MapUndoHttpTests、MapUndoMySqlTests、MapUndoUiTests 覆盖按账号持久化的 100 条操作栈、连续撤销、LN4/6/8/10/12/18 规则、引用同步、原编码恢复、移动/删除/整组粘贴/导入及线路和 Cross 操作。SQL 测试验证架构 29→30、中断重试、历史/标记与地图原子提交、四张地图表和历史表故障回滚、重启重登、多人冲突、K/J 版本独立、IsChanged 保留、超限裁剪、响应丢失和重启后的请求去重。UI 测试验证 Ctrl+Z/菜单、文字焦点、修饰键、重复键、草稿保护和忙碌期间拒绝重复请求。仅使用隔离 GUID 测试数据库。

## LN 保护区域

LogicalNodeProtectionTests 通过实际绘图几何与像素验证六种 LN 外围边长、同心、虚线、无填充、缩放与图层顺序；验证 G、面板与菜单同步、输入框/修饰键/长按处理，以及未保存预览不中断。点选与 F7 仍采用原 LN 正方形；保护区域内的合法线路和其他 LN 可保存，显示切换不产生数据写入。参考渲染输出为 .artifacts/renders/ln-protection-reference.png。

## LN6 OperandKind

LogicalNodeOperandTests、LogicalNodeOperandMySqlTests、LogicalNodeOperandUiTests 覆盖 Class/Instance/Custom 大小写、固定英文文化、转换后的全局唯一性及新增编号/修改拒绝、LN4 双分支同步、默认/旧接口保留、自定义原约束、非法类型和跨尺寸拒绝、单个/整组剪贴板、导入引用、Ctrl+Z 恢复、SQL 直写约束、迁移 30→3001→31 中断重试、旧撤销 JSON、多人并发及 UI 草稿。渲染参考 .artifacts/renders/ln6-operand-editor.png。数据库测试仅使用隔离 GUID 库。

## LN 自动连接

`LogicalNodeGoToTests` / `LogicalNodeGoToMySqlTests` / `LogicalNodeGoToUiTests` 覆盖前往模式：旧线有向反向遍历、双向线、已有单向 Cross 的通行限制、未设置/缺失交点不换路、循环、其他 LN 及擦角阻挡；按新增长度选取接入点、避让 LN、避让已占 Cross、半整数/旧节点末端最多 1 单位调整；确认时拒绝旧线反转、旧 Cross 变化或新增 LN 阻挡；新接入 Cross 使用普通连通 I（两个换路方向均允许），其他新 Cross 不连通，旧 Cross 和待算标记保留；真实 K 状态复用、跨 Server 修改、SQL 故障回滚、重试和单次撤销。实际 WPF 点击“前往”并显示已有通路，截图位于 `.artifacts/renders/ln-goto-preview.png`。

LogicalNodeConnectionTests、LogicalNodeConnectionMySqlTests、LogicalNodeConnectionUiTests 覆盖各尺寸 LN、两端精确 5.5 延伸、八方向与 90°/135° 折角、45° 锐角拒绝、避让第三 LN、未计算交点与斜线、超过 500 的细分、无可用尾端、预览不写库、K 后 O 设置新线交点、保留其他 IsChanged 待算任务和旧通行设置、完整撤销、重复响应重试、并发冲突与 SQL 故障整次回滚。WPF 验证两次选点、无效点/同一 LN、真实方式弹窗、其他选项禁用、预览确认、忙碌防重、失败重试、Backspace/Esc 与剪贴板和撤销的草稿保护。界面参考图 .artifacts/renders/ln-connection-preview.png。MySQL 测试只使用隔离 GUID 数据库。

## LN10 括号属性

LogicalNodeInstanceAttributeTests / LogicalNodeInstanceAttributeMySqlTests 覆盖 human(id=1)、human(name=要瑞卿)、human(name=Alice)，仅类名前缀转小写、属性大小写保留、缺失或嵌套括号拒绝、重复实例、父类值与其自定义 logic 区分、220 字符及父类改名长度回滚。验证单节点剪贴板、整组粘贴、同批导入父类编号时保留属性、最新父类定位、连续落点由服务端生成 id=新节点编码，以及多人并发新建。

数据库测试使用隔离 GUID 库模拟真实 v31 的旧 CHECK、裸值和撤销 JSON，验证 31→3101→32 自动补 id=原节点编码、原位置及 IsChanged 保留、旧创建/删除/父类改名记录在升级后仍能撤销；地图修复、历史修复、安装 CHECK、发布版本四阶段故障后保持关闭并可幂等重试。SQL 直接写入验证小写前缀、括号属性、固定隶属表达式和按边长区分的值长度限制，重启检查完整约束。

## LN10 空属性与多属性隶属表达式

LogicalNodeInstanceMembershipTests / LogicalNodeInstanceMembershipMySqlTests 用四个用户示例的完整字面量验证空括号、单属性、多属性和 ⋂ 拼接，包含属性顺序与大小写、空属性项拒绝、界面从多属性切回空括号、复制/导入/父类编号与改名/撤销同步。长中文类名的展开逻辑及 JSON 可超过旧剪贴板上限，仍完整往返。SQL 测试验证直接写入完整表达式、缺失/错误属性表达式拒绝、其他 LN 的逻辑长度仍限 255、触发器缺失关闭启动，以及真实 v32 旧表达式/撤销记录升级、发布失败重试和版本幂等性。

## LN10 可选实例别名

LogicalNodeInstanceAliasTests / LogicalNodeInstanceAliasMySqlTests 验证四个别名示例、中文与大小写、分隔空白、添加/删除别名、父类定位、剪贴板、同批导入/整组粘贴的父类编号、父类改名及撤销保留名字。SQL/HTTP 验证完整表达式、防止绕过、重启持久化和真实 v33 格式约束升级：不改原节点资料与撤销记录，发布失败保持关闭并可重试。
