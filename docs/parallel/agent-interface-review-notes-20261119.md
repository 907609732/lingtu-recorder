# Agent 接口独立审查笔记（2026-11-19）

审查基线：`2d9b5a42bc93a50a4b4dc77265a50a43de6ba977`。只读检查 spec/plan 与既有源码；当前 Agent 实现尚未出现，以下区分已确认的既有接线约束和待验收项。未读取真实配置、数据库、录像；未启动软件。此文件是唯一审查写入。

## 已确认的 P2 数据一致性风险

### 1. 入队与旧同步完成互相覆盖

- `src/UnpackVision.Infrastructure/SyncDispatcher.cs:39-42`：读取记录，异步推送该快照，随后完成 delivery。
- `src/UnpackVision.Infrastructure/SqliteScanRecordRepository.cs:843-849`：同一 `(record_id, connector_id)` 重新入队保留原 delivery id，仅重设 Pending。
- 同文件 `952-957`、`1039-1046`：完成操作按 id 无条件设为 Succeeded，没有版本或 Processing 条件。
- 可重现时序：旧备注快照开始 Push → Agent 保存新备注并入队 → 旧 Push 完成将新 Pending 改为 Succeeded。Excel 保留旧值而状态显示成功。
- `RetryConnectorAsync:974-976` 也会重置 Processing；不能在 ProcessDueAsync 过程中并发执行。

### 2. 后台重命名的全行更新会覆盖 Agent 新备注

- `src/UnpackVision.App/MainWindow.xaml.cs:995-1007`：查询记录快照后检查 metadata、重命名、调用 UpdateAsync。
- `src/UnpackVision.Infrastructure/SqliteScanRecordRepository.cs:409-428`：UpdateAsync 的 SQL 同时覆盖 note/note_updated_at，并非仅更新路径。
- `src/UnpackVision.App/HistoryWindow.xaml.cs:265-273` 也读取快照后重命名并全行更新；`254-264` 的标签差集/备注更新同样可能与 Agent 互相覆盖。
- 可重现时序：后台取到旧快照 → Agent 更新备注 → 后台 UpdateAsync 旧快照覆盖新备注。仅给 Agent 请求加锁不能防止。

### 3. 注释写入与 Excel 入队不是一个事务

- `SqliteScanRecordRepository.cs:627-647` 的 UpdateNoteAsync 独立提交；AddTagAsync/RemoveTagAsync 各自有事务，均不入队。
- `PortableCatalogScanRecordRepository.cs:139-150` 在修改与入队之间还会执行便携目录写入。
- 若 Agent 将请求断开 token 贯穿以上操作，写入提交后取消会跳过入队，客户端只收到取消且 Excel 不会更新。不能把串行锁当成事务。
- 最小处理：开始修改前尊重请求取消；持久化阶段开始后使用独立的有界操作生命周期完成写入与入队，关闭阶段等待这些操作。若入队失败应返回明确的“本地已保存、未入队”结果并提供重试，不能成功宣称已同步。完全抵抗进程崩溃需仓储事务/outbox，不要在文档中超额承诺。

## 最小修正选择

当前没有适合“单条记录仅更新媒体路径”的现成窄接口；`RebaseOwnedPathsAsync` 面向路径迁移，不能代替单条录像重命名。队列 CAS 还需 claim 版本传递，单纯 Complete WHERE status='Processing' 不够（Pending 可能已被再次 claim）。

本期建议使用**一个桌面实例共享异步 gate**，避免扩大仓储协议：

1. Agent 注释/标签写入与入队、Agent retry 全流程获取该 gate。
2. `RunSyncLoopAsync` 中 ProcessDueAsync 与 RetryPendingVideoRenamesAsync 全流程获取同一 gate，覆盖 GetAsync → PushRecordAsync → CompleteDeliveryAsync，不能仅锁入队。
3. 将同一 gate 注入 HistoryWindow，用户确认对话框关闭后、重新读取当前记录之前获取，覆盖标签差集/备注/重命名/入队；不要持锁显示模态对话框。重读最新记录不能自动解决用户编辑窗口中的冲突；本期按明确的最后保存覆盖语义即可。
4. 不要把独立 new 的 SemaphoreSlim 放到多个组件；不要在持锁方法中递归获取。gate 内只完成业务持久化，前台刷新可在释放后执行，避免 Dispatcher 反向等待。
5. 此方案只保障本桌面实例；独立 Service 同时写同一数据库仍是外部并发边界，若当前运行方式允许，须明确限制或升级仓储版本协议，不应宣称解决跨进程一致性。

## 活动记录与前台状态：已确认约束

- `RecordingCoordinator.cs:48-51` 的 State 只是 Starting/Recording/Saving 的投影；CurrentRecord 在 `331` 先变 Completed，`344` 仍 await 完成落库，直到 `364` finally 才清空。
- 归档写入校验应同时拒绝 **id 等于 CurrentRecord.Id** 的记录，不仅检查 CurrentRecord.State 或数据库 State。这样覆盖完成落库尾端仍持有内存快照的窗口。
- 新状态 DTO 应在 Dispatcher 上一次性捕获 State、CurrentRecord.Id、shutdown/ready 等信息；不能后台线程分别读取 UI 控件和多项变化中的字段。
- 成功写入后刷新 MainWindow recent 列表；HistoryWindow 已打开时也需刷新或明确采用重新加载。不要通过 UpdateIssueUi 改当前另一条录像的备注输入。

## HTTP / 客户端 / 退出：待实现后检查

- 既有 `DesktopCommandListener.cs:72` fire-and-forget ProcessAsync，Dispose 只等 accept loop (`130-142`)；新 API 不应照抄。须跟踪所有 accepted request task，停止接收后取消读流并等待，涵盖正在等 gate、Dispatcher 和持久化的请求。
- `MainWindow.xaml.cs:1953-1974` 用户可以取消退出；应在用户确认以后停止 Agent。`1982-1990` 是全局取消与资源释放位置；先停止入口并等待操作，再释放共享资源。Dispose 异常不能使 `_shutdownStarted=true` 后永远卡住不能退出。
- 慢请求、无 Content-Length/分块输入也要有累计字节上限和读超时；仅检查 ContentLength64 不足。取消后不得使用无限等待的错误输出。
- token 严格独立随机且启动轮换；连接文件原子写入，清理时比对本次 instance/owner（防删除另一个实例文件），客户端调用前重读；文件不可允许其他本机用户读取凭据（检查 ACL/继承策略）。
- 只接字面 `127.0.0.1`，禁止代理/redirect；连接文件参数需验证 scheme、host、port、无 userinfo/fragment/任意路径覆盖；不能凭 Connection 文件存在假定进程就绪。
- DTO 显式投影允许字段，不直接序列化 settings/coordinator/exception；完整 ScanRecord 包含 FailureReason、Snapshots、StationId、媒体子结构等，应审查是否属于实际工具需要。错误返回不能直接带异常 message/stack（可能含 workbook 路径、请求头/凭据）。录像路径仅来自目标记录拥有的资产/分段，不接受用户任意路径。
- MCP discovery、CLI help/schema 必须无需连接文件、桌面进程或正式仓储。启动失败独立捕获，不能落入 OnLoaded 总体失败分支影响原录制初始化。
- CLI 无效参数也须符合约定的 JSON 错误契约，避免 System.CommandLine 默认文本错误绕过统一输出；MCP 工具失败须 `IsError=true`，不能仅把错误 JSON 当成功工具内容。
- 发布脚本实际复制 Agent 程序、依赖和许可；检查 publish 目录和启动链不会自动启动 HIK/兼容后台服务。父代理已独立验证 SDK 最小示例（ModelContextProtocol 2.4.0、System.CommandLine 2.0.15），这里不重复将还原成功当成接口验收。

## 验收建议

合成测试至少包含：Push 阻塞时注释入队；重命名拿旧快照时注释写入；CurrentRecord.State=Completed 但仍持有相同 id；请求断开后已提交注释的入队结果；慢读请求时 Dispose 有界结束；另一个实例替换连接文件后旧实例退出；DTO/错误输出不含测试 token。以上是建议验证，未实际运行。

## 结论

方案方向正确：统一桌面仓储入口、归档限制、独立凭据、不控制录像和合成验收都符合范围。当前不能给实现合并结论；P2 并发接线与取消语义须落实后重新审查。

未评判范围：录像控制、删除、远程网络访问、发布安装包、真实设备和工作簿验收，均不在本期授权实现范围；既有手机桥的安全改造也不属于此次接口实现，不要求顺手扩改。

## 增量审查：第一批 Application / Agent.Shared 源码

实现仍在进行，以下不将尚未完成的 WPF 接线算作缺陷。

### 新确认 P2：HTTP 响应正文不受客户端 timeout 约束

`src/UnpackVision.Agent.Shared/AgentApiClient.cs:62-79` 配置 HttpClient.Timeout，但 SendAsync 使用 ResponseHeadersRead。该 timeout 到响应头完成即停止覆盖，ReadLimitedAsync 使用原始调用方 token。服务立即发头后不关闭正文时，默认 CancellationToken.None 的 CLI/MCP 调用可永久悬挂；字节上限无法限制永远不来的字节。

最小修正：CallToolAsync 创建 linked CTS，CancelAfter(_timeout)，其 token 覆盖连接描述文件读取、SendAsync 和正文读取；取消映射仍依据原始调用方 token 区分用户取消和超时。加一个 headers 立即返回、正文读取一直阻塞但尊重 cancellation 的 handler 测试。

### 新确认 P2：所有者清理检查与删除有跨实例 TOCTOU

`src/UnpackVision.Agent.Shared/AgentConnection.cs:160-166` 先 ReadAsync 验 token/pid，再 File.Delete；`WriteAtomicAsync` 可以在这两个动作之间替换文件。旧实例先读自身 → 新实例原子发布 → 旧实例删除新文件，仍违反“只清理自身连接文件”。`AgentApiHostTests.StopAsync_RemovesOnlyItsOwnConnectionFile` 当前只覆盖新文件预先存在的顺序场景。

最小可靠修正：同一连接路径的 WriteAtomic 与 ownership-delete 共用跨进程锁（例如固定 sidecar 文件 FileShare.None 的有界独占锁），锁内重读/校验/替换或删除。不要仅加进程内 static SemaphoreSlim。命名 Mutex 也可，但不可跨 await 持有线程亲和 Mutex。测试须覆盖真正的竞争时序。

### 已看过但暂不判缺陷

- RecordAutomationService 当前 GetWritableRecordAsync 仅按 DB State 判定；runtime.CurrentRecordId 目前只用于状态 DTO。等待最终 host/WPF 接线确认是否在共享 gate 内另有 id 拒绝；此约束仍须落实。
- 当前 service 注释直接使用调用方 cancellationToken 串行修改、入队；等待 host 接线确认是否采用提交阶段独立生命周期，不能据此提前宣称取消语义已完成。
- DTO 显式投影已实现，未返回 settings、token、原始异常、SyncDelivery.LastError/ExternalId；记录拥有的路径及媒体子对象来自仓储端口，当前未发现任意文件读取入口。
- 客户端每次重读连接、禁代理/重定向/cookie、字面 IPv4 loopback 规范化均有明确实现。连接文件上限是读取前 FileInfo.Length 检查，尚非流上限；正常原子发布不会扩容同一文件，本轮不将同用户恶意并发改文件升级为独立 P2。

本轮只检查源码与合成测试内容，未执行测试；代码仍写入中，不给合并结论。

### 最小测试建议（不需要真实桌面或业务数据）

1. **body stall**：自定义 HttpMessageHandler 立即返回 200 + HttpContent；该 content 的底层 Stream.ReadAsync 等待 `Task.Delay(Timeout.Infinite, suppliedToken)`。AgentApiClient timeout 设为 100–300ms、原始调用 token 为 None。断言测试外层 2–3 秒 watchdog 之前抛 AgentUnavailableException，并且错误正文不包含测试 token。分别测试调用方主动取消仍返回 OperationCanceledException。
2. **正文上限**：同一 mock 流提供 MaxResponseBytes+1 个字节，Content-Length 分别设为 null 和错误偏小；断言在达到累计上限后停止读取并返回 unexpected_response，不依赖 Content-Length。当前 ReadLimitedAsync 有累计 buffer.Length+read 检查，静态检查已覆盖该正确性；仍建议保留回归测试。
3. **跨实例锁**：使用同一路径的两个 helper 子进程，仅读写临时目录。进程 A 获得共享发布/删除锁后读取旧 owner，并通过测试 IPC/barrier 暂停；B 尝试发布新 owner，应在同一把锁上等待；A 删除并释放后 B 发布，最终文件必须为 B。反向顺序由 B 先发布、A 后清理，A 不可删除。无需把测试 barrier 暴露到正式公共 API，可提取内部 connection-store/lock 边界给测试使用。纯两次顺序调用只证明比较逻辑，不能证明 TOCTOU 修复。
4. **输出凭据回归**：为合成 host 生成固定 sentinel token，对 8 个工具成功、401、参数错误、冲突、仓储抛含 token 的异常、退出取消收集 HTTP JSON、CLI stdout/stderr、MCP content；全部检查不含 sentinel。CLI/MCP 的诊断不能输出 exception.ToString 或 inner exception。当前 host 通用异常固定文本、DTO 显式投影、AgentConnectionInfo.ToString 隐 token 已实现。

客户端 ParseResponse 目前接受服务端 error.code/message 原样转发（仅截长度）；若要将“即使服务错误回显也不输出当前凭据”作为绝对客户端防线，可在 CallToolAsync 当前 token 可见处净化错误文本。正常受信 host 固定错误未看到回显 token 路径，本轮不把此项作为已确认独立 P2。

## 增量审查：主体 Host / CLI / MCP / 发布接线

### 新确认 P2：省略 note 会清空已有备注

- `src/UnpackVision.Agent/AgentCli.cs:96-115` 的 `--note` 没有 Required；省略参数时传入 null。
- `src/UnpackVision.Agent.Shared/AgentInputRules.cs:57-67` 将 null 转为空字符串并判定有效；HTTP 的 `AgentToolRequest.Note` 默认同样为 null，`AgentApiHost.SetRecordNoteAsync` 未区分缺失。
- 因此 `set-record-note --record-id <有效记录>` 或 HTTP `{ "recordId": "..." }` 会成功删除该记录已有备注；离线 schema 却将 note 定义为 required。
- 最小修正：CLI `--note` Required=true；共享校验拒绝 null，保留显式空字符串作为有意清空；在 HTTP/CLI 合成验收中断言“省略 note”返回 invalid_argument，原备注及队列均不变。MCP SDK 的 required schema 本身不能保护 HTTP/CLI 两条入口。

### 新确认 P2：CLI 解析错误破坏 JSON 输出契约

- `AgentCli.cs:13-20` 对 System.CommandLine parse errors 仅写 stderr 原始文本并返回 1，stdout 没有 JSON。
- `list-records --limit nope`、未知选项/命令等不能被自动调用方按与业务验证错误相同方式解析；当前测试只覆盖格式正确但值不合法的 GUID/长度，未覆盖 parser 层。
- 最小修正：所有 parse errors 输出同一 `ok=false/error.code=invalid_argument` JSON（可使用固定摘要和结构化字段，避免盲目回显整个原始参数）；help 保留正常文本。新增未知选项、未知命令、limit 非数字、必需参数缺失的进程级测试。

### 新确认 P2：Agent 写入成功后前台列表保持旧值

- `MainWindow.Agent.cs:22-26` 仅构建 service/host，没有写入完成通知；`AgentApiHost.cs:282-283` 仅序列化返回；全新 service 也无桌面刷新回调。
- 因此 AI 修改已显示记录的备注/标签/队列状态后，MainWindow recent 列表和已打开 HistoryWindow 不刷新，用户看到旧备注或旧标签，可能据此再次覆盖新值。spec 明确要求复用桌面刷新。
- 最小修正：持久化成功后发 record-changed 通知，UI Dispatcher 安排 RefreshRecentAsync；打开的 HistoryWindow 重载当前页或记录。刷新在业务 gate 释放后进行，不更改当前活动录像输入框。

### 原审查约束现已落到最终接线：仍为阻断项

- `MainWindow.Agent.cs` / `AgentApiHost` / `RecordAutomationService` 未见共享 mutation gate，HistoryWindow 无 diff；旧同步覆盖新 Pending、后台重命名覆盖备注的两项 P2 仍成立。
- `RecordAutomationService.GetWritableRecordAsync` 仅判 DB State；runtime.CurrentRecordId 仍仅用于状态投影，未拒绝活跃对象同 id 的归档尾端写入。
- 修改与 enqueue 仍贯穿同一个请求取消 token；中途取消造成保存后未入队的风险未解决。不要把 host 的 linked shutdown cancellation 当成提交事务保障。

### 已确认的正确接线 / 非阻断观察

- MCP `UnpackVisionAgentTools.Failure` 明确 IsError=true，同时提供 StructuredContent；未发现将工具错误标成功的问题。
- host 最近版本已将 `_requestLifetime.Token` 与 RequestAborted 链接供反序列化/dispatch 使用；Kestrel StopAsync 有 5 秒超时。未重复运行已有关闭测试；最终仍需验证等待 mutation 持久化完成的策略。
- publish.ps1 将 Agent 独立 self-contained 发布到 App/Agent；复制 notices 和两份实际存在的依赖许可文件。Agent 启动链只有 AgentApiClient，无引用 Infrastructure、WPF 或 HIK 服务启动入口；静态检查未发现自动启动后台 HIK 的行为。尚未实际运行 publish，不能宣称产物完整。
- MainWindow Agent 可选启动有独立 catch，正常退出和卸载准备均调用 StopAgentApiAsync；未看到将端口绑定失败直接变为录像初始化失败的正常路径。

本轮不新增网络/设备/安装验收要求；仍只修改本审查交接文件。

## 增量审查：共享 mutation gate 接线

### 新确认 P1 回归：兼容 Service 缺少新增构造依赖注册

- `src/UnpackVision.Infrastructure/SyncDispatcher.cs:12-16` 构造器新增必需的 `IRecordMutationGate`。
- `src/UnpackVision.Service/Program.cs:50` 仍通过 `AddSingleton<SyncDispatcher>()` 让 DI 构造；其全部注册未包含 `IRecordMutationGate`。
- `SyncWorkerService` 构造器依赖 SyncDispatcher，因此启动 hosted services 时会抛 Unable to resolve service for type IRecordMutationGate，兼容同步服务无法启动。全量编译和 Agent 合成测试不能验证该 DI 路径。
- 最小修正：在 Service composition root 注册 `AddSingleton<IRecordMutationGate, RecordMutationGate>()`，保持实例生命周期 singleton。不要用默认 null 构造参数掩盖需要共享 gate 的桌面接线。

### gate 本身的只读核查结果

- MainWindow 两处 SyncDispatcher 构造（首次初始化和设置更新）均使用同一个 `_recordMutationGate`；Application service、HistoryWindow 同样注入该实例。
- SyncDispatcher 的 gate 覆盖 claim → 最新记录读取 → Push → Complete/Fail；获取 due 列表在锁外，但锁内 TryClaim 会重新检查状态，未见由该顺序导致的新回归。
- 后台重命名在获取 gate 后重读最新记录，再做全行更新，已消除之前与 Agent 注释并发覆盖的对应窗口。
- History 确认对话框在锁外，锁内只有仓储/文件重命名 await，LoadAsync 与最终提示在释放后。WPF await 继续回 Dispatcher 是异步调度，不是同步 Wait/Result；未见锁内额外 Dispatcher.Invoke 等待，因此不判死锁。
- Application service 现在使用独立 persistence CTS，客户端断开不直接取消已进入 gate 的写入流程；当前常量实际是 **10 秒**（注释仍写 30s），不将文字不一致列为 P2。活动 CurrentRecordId 拒绝仍未接入，属于已有待修项，不重复增加。

未运行 Service 或设备，未添加泛化测试要求；本轮新增阻断项只有上面的 Service DI 回归。

## 第二轮修复后的最终只读结论

源码已确认关闭：完整响应正文 linked timeout；连接文件 WriteAtomic/delete 共用有界跨进程 sidecar 独占锁；note 缺失拒绝、显式空串清空；CLI parser JSON 错误；Agent 成功写入后异步刷新 recent/已开 History；Service `AddSyncDispatcherServices` 注册 singleton gate；共享 gate 覆盖同步与重命名；CurrentRecordId 传入并与已完成数据库记录一起校验。MCP IsError 仍正确。

关闭链路现有 10 秒独立 persistence timeout、12 秒 Kestrel/host shutdown 等待，慢读请求独立取消；MainWindow StopAgentApiAsync 捕获可选组件清理失败，不因其异常阻止退出。这里是源码结论，不替代 root 最终运行测试、打包和同次产物验收。

**仍阻断 P2（原部分提交恢复问题尚未完全关闭）**：`RecordAutomationService.cs:330-333` 的 EnsureDeliveryQueuedAsync 对任何旧 Succeeded 直接返回。已同步记录修改成功但 enqueue 失败后，重试相同备注/已加标签/已移除标签走 no-op 分支，旧 Succeeded 被误认为新记录版本已同步，永不重新入队；retry_excel_sync 同样排除该行。最小修正是 no-op 重试也入队，或验证 delivery 对应当前记录版本，不能只判断状态。root 已接收并将独立修复。

没有新增已确认 P1/P2；本次未运行测试，不宣称最终通过。

## 最后修复确认

已只读确认 `EnsureDeliveryQueuedAsync:329-338` 不再信任旧 Succeeded，no-op 重试始终重新入队；`AgentQueueRecoveryTests` 的 note/add_tag/remove_tag 三个分支覆盖旧版本成功、首次保存后入队抛 IOException、重试恢复 Pending。该 diff 关闭最后一项已确认 P2。此前审查发现的 P1/P2 均已在源码层面关闭；本审查无剩余确认阻断。未运行测试，最终 Debug/云端构建与同次产物验收由 root 记录。
