# Agent 接口实现交接（2026-11-19）

分支：`feature/agent-interface-20261119`  
工作副本：`C:\Users\90760\.codex\worktrees\lingtu-ecosystem-20261117\lingtu-recorder`  
状态：源码实现、独立审查、本地全套与 GitHub Release 配置构建验收完成；未发布新安装包、未改 Codex MCP 配置。

## Codex 最终验收

- 最后修复：重复写入时不再把旧版本的 Succeeded 当作当前记录已同步；备注、加标签、移除标签的三个回归用例均先失败，再随修复通过。
- 本机 `scripts/build.ps1 -Configuration Debug`：退出码 0，430 项主测试和 3 项独立 WPF 测试通过。
- [GitHub Agent validation 44359051130](https://github.com/907609732/lingtu-recorder/actions/runs/44359051130)：提交 `92d2ef433876e139706943d7af13560625d37489`，Release 构建 0 警告/0 错误，430+3 测试及已发布目录中的 7 项 CLI/MCP 进程测试通过。
- 同次构建检查自包含桌面输出含 Kestrel、Agent.Shared 和 Application 依赖；未执行安装器或硬件验收。
- 下载该 run 的 `agent-win-x64-92d2ef433876e139706943d7af13560625d37489` 到本机，以 `UNPACKVISION_AGENT_EXE` 指向下载程序，7 项真实 CLI/MCP 进程测试再次通过。所有业务记录仍为合成数据。
- 下载的 `UnpackVision.Agent.exe` SHA256：`77D865DF1E7022D72AB241F95441BC1D9DA8173176129E6E111C6C22940671CD`。
- 官方 DeepSeek headless 后续会话退出码 0，最终事件 `reason.kind=completed`；独立审查没有剩余已确认 P1/P2。

## 交付范围

- 新增 `src/UnpackVision.Agent.Shared`：连接文件契约、严格 loopback 客户端、官方 MCP/CLI 共用传输、退出码/错误类型。
- 新增 `src/UnpackVision.Agent`：`ModelContextProtocol` 2.4.0 stdio 服务器、`System.CommandLine` 2.0.15 CLI、8 个工具、离线 `schema`。
- 新增 `src/UnpackVision.Application/Agent`：记录自动化用例，显式 DTO，归档完成/导入写限制，备注/标签/队列语义。
- 新增 `src/UnpackVision.Core/History/RecordMutationGate.cs`：桌面实例共享异步 mutation gate。
- 新增 `src/UnpackVision.App/AgentApiHost.cs`：随机 IPv4 loopback Kestrel、Bearer、Host/Origin/Content-Type/长度/方法校验、连接文件生命周期。
- 新增 `src/UnpackVision.App/MainWindow.Agent.cs`：初始化后启动、退出/卸载停止、运行态与标签不可变快照、成功写入后的 recent/History 刷新。
- 修改 `MainWindow.xaml.cs`、`HistoryWindow.xaml.cs`、`SyncDispatcher.cs`、`Program.cs`（Service DI）及发布/架构/许可/README 文档。
- 新增合成测试、真实官方 SDK stdio E2E、CLI 子进程 JSON、跨进程锁、DI resolve 等测试。

## 第二轮 reviewer 修复

1. **共享 mutation gate**
   - `MainWindow` 的两个 `SyncDispatcher`、`RecordAutomationService`、`HistoryWindow` 使用同一个 `_recordMutationGate`。
   - `SyncDispatcher` 在每笔 delivery 的 `TryClaim → GetAsync → PushRecordAsync → Complete/Fail` 全程持 gate；due 列表在锁外，锁内重新 claim。
   - `HistoryWindow` 在确认对话框关闭后、读取/修改标签备注/重命名/入队前持 gate；`LoadAsync` 在锁外。
   - `AgentSyncCoordinationTests` 验证 Push 阻塞时 Agent 写入等待，旧 Push 完成后新 delivery 保持 Pending，而不是旧完成覆盖新 Pending。
   - 边界：只协调当前桌面实例，不宣称协调另一个直接写同一 SQLite 的进程。

2. **CurrentRecordId 尾窗拒绝**
   - `RecordAutomationService` 新增 `currentRecordIdProvider`，在 gate 内读取最新当前记录 id。
   - 即使 DB 记录已是 `Completed`，只要 id 等于 `CurrentRecordId` 也返回 conflict。
   - `AgentApiHost` 每个请求只捕获一次 runtime 快照；写入时由 Application 在 gate 内再次查询当前 id。
   - 测试：`SetRecordNote_RejectsCurrentRecordIdEvenWhenDatabaseIsCompleted`、`CurrentRecordId_IsRejectedEvenWhenDatabaseStateIsCompleted`。

3. **提交阶段独立有界生命周期**
   - gate 获得后，备注/标签/重试的持久化与入队使用独立 10 秒 persistence CTS，不随 HTTP 请求断开取消。
   - 入队失败后，同一备注/标签重试或同一已移除 assignment 重试会重新入队当前记录，包括旧 delivery 仍为 Succeeded 的情况；旧状态不能证明修改后版本已同步。
   - 测试：`SetRecordNote_RequestCancellationAfterCommitStillQueuesDelivery`、`*_RetryAfterQueueFailure*`。

4. **客户端完整 timeout**
   - `AgentApiClient` 使用 linked CTS，`CancelAfter(_timeout)` 覆盖连接文件读取、`SendAsync`、响应正文 `ReadLimitedAsync`；`HttpClient.Timeout` 设为 infinite，避免 header 后正文悬挂。
   - 用户主动取消仍抛 `OperationCanceledException`；超时映射 `AgentUnavailableException`。
   - 测试：立即响应头但正文永久 stall、用户主动取消、既有单次请求超时不重试。

5. **连接文件跨进程锁**
   - `WriteAtomicAsync` 与 `TryDeleteIfOwnedAsync` 共用 `${connection}.lock` 的 `FileShare.None` 跨进程 sidecar 锁；锁内重读所有者再替换/删除，锁等待有界。
   - 测试：持有锁时发布等待、清理/新 owner 发布竞争最终保留新 owner、锁超时有界。
   - 客户端每次调用仍重读连接文件；不依赖文件存在推断进程就绪。

6. **CLI parser 错误 JSON**
   - 所有 `System.CommandLine` parse errors 统一在 stdout 输出 `{"ok":false,"error":{"code":"invalid_argument",...}}`，退出码保持 1；help/schema 仍离线且正常。
   - 测试：未知命令、未知选项、`--limit` 非数字、缺少 `--note`。

7. **慢读 body / shutdown**
   - Host 读 body 使用累计字节上限和独立 5 秒请求体超时；超时返回 408 JSON；Content-Length/分块/方法/类型限制保留。
   - Kestrel `HostOptions.ShutdownTimeout` 与 `StopAsync` 使用可配置有界值；`DisposeAsync`/`MainWindow.StopAgentApiAsync` 捕获失败，不让 Agent 退出异常挡住用户退出。
   - 测试：慢 body 超时、带未完成请求的 dispose 有界结束。

8. **UI 刷新与运行态/标签快照**
   - `MainWindow` 在 WPF 线程发布不可变 `RecordAutomationRuntime` 与标签目录快照，后台请求不逐项读 UI。
   - 成功变更后 host 回调，Dispatcher 刷新 recent 列表和已打开的 HistoryWindow；不调用 `UpdateIssueUi`，不改当前另一条录像备注输入框。
   - `HistoryWindow` 增加外部刷新入口，加载中收到的刷新请求在加载结束后补一次。
   - 测试：成功变更新增回调、冲突不回调；History UI 专项通过。

9. **P1 Service DI**
   - 新增 `SyncServiceRegistration.AddSyncDispatcherServices()`，注册 `IRecordMutationGate` 单例和 `SyncDispatcher`；`src/UnpackVision.Service/Program.cs` 使用该扩展。
   - `AgentServiceRegistrationTests` 用 `ServiceCollection` + `ValidateOnBuild` resolve `SyncDispatcher`，不启动真实服务。

10. **note 缺失 P2**
    - CLI `--note` Required=true；共享规则拒绝 null；HTTP/MCP/Application 拒绝缺失 note；显式空字符串保留清空语义。
    - 测试：HTTP 缺 note 不写库不入队；Application 缺 note 拒绝；显式空字符串清空并入队；CLI 缺 note parser JSON。

## 代码 diff 概览

`git diff --stat`（已跟踪文件）：

```text
 README.en.md                                       |   2 +
 README.md                                          |   2 +
 THIRD_PARTY_NOTICES.md                             |  17 +++-
 UnpackVision.slnx                                  |   2 +
 docs/architecture/README.md                        |   8 ++
 docs/architecture/module-map.md                    |   2 +
 scripts/publish.ps1                                |  11 ++
 src/UnpackVision.App/HistoryWindow.xaml.cs         | 113 +++++++++++++++------
 src/UnpackVision.App/MainWindow.xaml.cs            |  45 ++++++--
 src/UnpackVision.App/UnpackVision.App.csproj       |   2 +
 src/UnpackVision.Infrastructure/SyncDispatcher.cs  |   9 ++-
 src/UnpackVision.Service/Program.cs                |   2 +-
 tests/UnpackVision.Tests/TestDoubles.cs            | 101 ++++++++++++++++--
 tests/UnpackVision.Tests/UnpackVision.Tests.csproj |  19 ++++
 14 files changed, 286 insertions(+), 49 deletions(-)
```

未跟踪新增：Agent/Agent.Shared、Application Agent、Core gate、App host/wiring、Agent 测试、`docs/agent`、许可证文本、`SyncServiceRegistration.cs`。  
未跟踪但非本次实现写入：`.github/workflows/agent-validation.yml`（已按要求保留，未覆盖；测试助手支持其 `UNPACKVISION_AGENT_EXE`），以及此前存在的 `docs/superpowers/` 和 reviewer notes。

## 实际执行命令与退出码

### 第二轮（reviewer 修复增量）

```powershell
dotnet build tests\UnpackVision.Tests\UnpackVision.Tests.csproj --no-restore --nologo
# exit 0, 0 warnings

dotnet test tests\UnpackVision.Tests\UnpackVision.Tests.csproj --no-build `
  --filter "FullyQualifiedName~Agent" --nologo
# exit 0, 61 passed

dotnet test tests\UnpackVision.Tests\UnpackVision.Tests.csproj --no-build `
  --filter "FullyQualifiedName~HistoryWindowUiTests|FullyQualifiedName~AgentServiceRegistrationTests" --nologo
# exit 0, 2 passed
```

### 第一轮保留证据

- 首轮 TDD：实现前 `dotnet test ... --filter Agent` exit 1（缺项目/类型）。
- 首轮首次实现后：exit 1，28 通过 / 11 失败；修复 tampered-file 测试写入、token 长度/loopback 测试数据、host 停止时的 linked cancellation 后通过。
- 首轮完整 Debug：`scripts/build.ps1 -Configuration Debug` exit 0；411 main + 3 isolated WPF 通过。后续 gate/queue 修改后 full main 413 + isolated WPF 3 分别通过。
- 首轮 Release：solution build exit 0，0 warnings；Agent 测试 47 通过。
- 首轮 self-contained Agent publish/App/Agent 布局 smoke：exit 0；CLI schema/help exit 0，offline get-status exit 2；published Agent E2E 6 通过。
- 本轮按后续指令未重复发布/安装器，未重跑 current full suite；root 负责最终 full/cloud 验证。

## 剩余限制 / 未验收项

- 当前发布版 `v2.6.3` 不包含本功能；本分支不发布 release。
- 第一版仍不提供开始/停止录像、删除记录、设备配对、任意 shell、任意文件读取或直接 Excel 写入。
- gate 仅覆盖当前桌面进程；独立兼容 Service 同时直接写同一 SQLite 的跨进程一致性未解决（Service 自身使用独立 gate）。
- 写备注/标签与 enqueue 仍不是同一个仓储事务/outbox；请求断开后的队列由独立 persistence CTS + 重试补建缓解，但进程崩溃窗口不能完全消除。文档已明确“queued != synced”。
- 连接文件 token 依赖当前用户 LocalAppData 的 ACL/继承；同一用户恶意进程仍可读取。未新增 ACL 强化。
- 未执行真实业务桌面、相机、录像、Excel、设备或安装器验收；所有测试使用合成临时数据。公开 `v2.6.3` 安装包没有被本次源码更新替换。

## 测试助手 / 环境

- `tests/UnpackVision.Tests/AgentProcess.cs` 的 `ExecutablePath` 支持 `UNPACKVISION_AGENT_EXE`；未设置时退回已有 `agent/UnpackVision.Agent.exe`。该变量仅测试用。
- 本轮未写 Codex MCP 配置；`codex mcp add --help` 仅核对语法。
- 实现 worker 未提交或推送；最终源码与文档由 Codex 审查后整合。未 reset/clean，未触碰正式数据库、录像、Excel 或相机。
