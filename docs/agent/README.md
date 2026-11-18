# 本机智能体接口（MCP + CLI）

本页说明本仓库 **源码开发分支** 中的第一版本机智能体接口。当前公开的
`v2.6.3` 安装包 **尚未包含** 本功能；本分支也没有发布新的安装包。不要把本文
当作已发布安装版的能力说明。

接口只允许本机、当前用户使用：

- 桌面程序 `拆包智录` 必须已经启动，并已完成配置、记录仓储和 Excel 同步队列初始化。
- MCP 使用本机 stdio 命令，不开放远端 HTTP，也不监听局域网地址。
- CLI/MCP 调用的是正在运行的桌面程序，不会自动启动桌面程序、相机或兼容同步服务。
- 查询、备注、标签和 Excel 重试共用 `RecordAutomationService`，不会因为入口不同而产生两套业务规则。

## 交付位置

源码构建后：

```text
src/UnpackVision.Agent/bin/Debug/net10.0/UnpackVision.Agent.exe
```

`scripts/publish.ps1` 会把 self-contained Agent 放到：

```text
artifacts/publish/App/Agent/UnpackVision.Agent.exe
```

发布目录同时包含 `THIRD_PARTY_NOTICES.md` 和 `Agent/LICENSES/` 下的
ModelContextProtocol（Apache-2.0）与 System.CommandLine（MIT）许可证文本。

## 连接边界

桌面程序在初始化完成后启动一个随机 IPv4 回环端口，并写入：

```text
%LOCALAPPDATA%\UnpackVision\ai\connection.json
```

该文件包含本次启动的 Bearer 令牌，权限继承当前用户的本机应用数据目录。发布和所有者
清理都使用同一路径的跨进程 sidecar 锁，锁内重新校验再替换/删除，避免旧实例删除新实例
刚发布的文件。客户端每次调用都会重新读取该文件，因此桌面程序重启并轮换端口/令牌后，
Agent 进程无需重启。测试或隔离环境可通过 `UNPACKVISION_AGENT_CONNECTION` 指向临时文件；
普通使用不要设置。

客户端仅接受字面 `http://127.0.0.1:<port>/`，不接受 `localhost`、IPv6、局域网址、
`https`、路径、用户信息或重定向。客户端禁用系统代理和自动重试，单次超时 10 秒。
桌面退出时取消请求、等待 Kestrel 关闭，并且只删除仍属于自己令牌和 PID 的连接文件。

## CLI

所有业务命令在 stdout 输出一个 JSON 对象；帮助和 `schema` 不读取连接文件，也不需要
桌面程序运行。错误示例：

```json
{"ok":false,"error":{"code":"unavailable","message":"UnpackVision desktop is not running or has not finished initialization."}}
```

成功示例：

```json
{"ok":true,"tool":"get_status","result":{"desktopReady":true,"excelSync":{"connectorId":"excel","queuedIsSynced":false}}}
```

参数解析错误（未知命令、未知选项、`--limit` 非数字、缺少必需参数）也按同一契约向
stdout 输出 `invalid_argument` JSON，并以退出码 1 结束；不会只写 stderr 文本。

命令：

```text
UnpackVision.Agent.exe get-status
UnpackVision.Agent.exe list-records [--tracking-no <文本>] [--limit <1..200>]
UnpackVision.Agent.exe get-record --record-id <GUID>
UnpackVision.Agent.exe list-tags
UnpackVision.Agent.exe set-record-note --record-id <GUID> --note <文本>
UnpackVision.Agent.exe add-record-tag --record-id <GUID> --tag-id <标签ID>
UnpackVision.Agent.exe remove-record-tag --record-id <GUID> --assignment-id <标签分配GUID>
UnpackVision.Agent.exe retry-excel-sync
UnpackVision.Agent.exe schema
UnpackVision.Agent.exe --help
```

退出码：

| 退出码 | 含义 |
| ---: | --- |
| 0 | 成功 |
| 1 | 用法、参数或字段校验失败 |
| 2 | 桌面程序未运行或尚未完成初始化 |
| 3 | 连接令牌失效或认证失败 |
| 4 | 记录、标签或标签分配不存在 |
| 5 | 记录不是已完成/导入状态，或当前状态不允许写入 |
| 6 | 传输失败或服务端返回非预期响应 |
| 7 | 未预期的本地失败 |

参数边界：`recordId`/`assignmentId` 必须是非空 GUID；`limit` 为 1..200，默认 50；
`trackingNo` 最长 64 字符；`note` 必须显式提供且最长 2000 字符，显式空字符串表示清空，
省略 `--note` 或 HTTP/MCP 中的 `null` 会被拒绝；`tagId` 最长 64 字符。

## MCP

启动 stdio 服务器：

```text
UnpackVision.Agent.exe mcp
```

MCP 的 stdout 只承载协议消息；工具调用错误作为 MCP `CallToolResult` 的
`isError=true` 和结构化 `structuredContent` 返回。`initialize`、`tools/list` 和帮助/
schema 不读取连接文件，因此桌面关闭时仍可发现工具；真正调用工具时才报告未就绪。

本机 Codex 的 `mcp add` 语法已用 `codex mcp add --help` 核对（不注册真实配置）：

```text
codex mcp add unpackvision -- "C:\路径\App\Agent\UnpackVision.Agent.exe" mcp
```

上面的命令只是接入示例。请按实际发布目录替换路径；不要使用 `--url`，因为本期不提供
远端 MCP HTTP 入口。把命令加入 Codex 配置属于用户自己的配置变更，本仓库脚本不会自动注册。

## 8 个工具

| 工具 | CLI | 作用 |
| --- | --- | --- |
| `get_status` | `get-status` | 返回 `desktopReady`、录像状态、`excelSync` 同步语义和能力范围 |
| `list_records` | `list-records` | 返回有界记录列表；`hasMore=true` 表示还有匹配项未返回，不宣称完整历史 |
| `get_record` | `get-record` | 返回单条记录、标签、同步状态和该记录拥有的媒体路径 |
| `list_tags` | `list-tags` | 返回已配置异常标签；不返回任何凭据 |
| `set_record_note` | `set-record-note` | 写入已完成/导入记录的备注，并加入 Excel 队列 |
| `add_record_tag` | `add-record-tag` | 给已完成/导入记录添加启用标签，并加入 Excel 队列 |
| `remove_record_tag` | `remove-record-tag` | 移除活动标签分配，并加入 Excel 队列 |
| `retry_excel_sync` | `retry-excel-sync` | 将所有尚未成功的 Excel 任务重新入队，不直接写工作簿 |

## 写入与同步语义

- 只有状态为 `Completed` 或 `Imported` 的记录可写；录制中、启动中、保存中等活动记录
  一律返回冲突，避免跨线程覆盖内存状态。
- 备注/标签写入成功后会调用现有 `EnqueueDeliveryAsync("excel", ...)`。**入队不等于已同步**；
  Excel 可能尚未打开、被占用或稍后重试。结果中的 `excelSync` 明确反映队列状态。
- `retry_excel_sync` 调用现有 `RetryConnectorAsync("excel")`，重新入队所有
  `Pending/Processing/Failed` 的 Excel 任务，不触碰其他连接器，也不强制立即写工作簿。
- 桌面实例使用共享记录变更闸门，将 Agent 备注/标签写入、Excel 队列 claim→读取最新记录→
  推送→完成、历史窗口编辑和录像重命名串行化。这样旧推送完成后不会把新入队的 Pending
  任务改回 Succeeded，旧记录快照也不会覆盖 Agent 新备注。Excel 推送进行时，Agent 写入
  可能需要等待；该闸门只协调当前桌面进程，不宣称协调另一个直接写同一数据库的进程。
- 请求在写入后断线时，调用方可能无法知道写入是否完成。第一版不自动重试写操作；
  请重新查询记录/标签，再决定是否重试。重复设置同一备注、重复添加同一标签，或重试同一
  已移除标签分配，都会重新将当前记录加入 Excel 队列。旧的 Succeeded 可能属于修改前版本，
  因此重复调用也会重新入队，避免保存成功但入队失败后漏同步。
- 持久化成功后，桌面会刷新最近记录和已打开的全部记录窗口；不会改写当前另一条录像的
  备注输入框。运行状态和标签目录由 WPF 线程发布为不可变快照，后台请求不会逐项读取 UI。

## 明确不做

第一版不提供开始/停止录像、删除记录、设备配对、任意 shell 命令、任意文件读取或直接
Excel 写入接口；不读取用户录像内容，不返回相机凭据、手机配对令牌或同步连接器错误原文。
不改动手机协议、StationHost 扫码路由、旧 Service API 或已公开安装包。
