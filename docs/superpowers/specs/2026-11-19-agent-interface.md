# 本机智能体调用接口

用户已选择第一版范围：查询记录、找录像、状态、备注和标签、同步重试。暂不控制录制，不提供删除录像、设备管理或任意文件/命令执行。

## 方案

采用 MCP + CLI，两种入口共用一个本机客户端。复用官方 C# MCP SDK 和 System.CommandLine，不自行实现协议或命令行解析。CLI/MCP 使用独立 .NET Windows 程序；桌面端在完成配置、记录仓储和同步队列初始化后提供统一的认证本机 API。现有 Service API 能做部分操作，但不保证运行与安装状态；StationHost 与桌面命令桥的职责和认证不同，因此本期不直接拼接这些端点。

## 边界

- Application 仅依赖 Core，承载记录查询与归档记录的注释用例；Host 适配 HTTP、CLI 和 MCP。复用既有仓储、标签定义、Excel 队列及桌面刷新，不绕过业务逻辑直接操作 SQLite。
- 工具为 get_status、list_records、get_record、list_tags、set_record_note、add_record_tag、remove_record_tag、retry_excel_sync。list_records 单次最多 200 条并明确限制；返回对象带结构化字段。定位录像返回记录拥有的媒体路径，不提供任意路径读取。
- 第一版写操作仅支持已完成/导入的记录；活动录制记录拒绝修改，避免跨线程内存状态覆盖。写备注/标签入 Excel 队列不代表已同步完成；retry_excel_sync 明确作用于所有尚未成功的 Excel 任务。
- 独立审查补充：实现时读取 `docs/parallel/agent-interface-review-notes-20261119.md`。Agent 写入与同步完整 Push/Complete、后台重命名及历史窗口注释重命名需共享桌面实例的异步互斥边界，防止旧快照覆盖新备注或新同步任务；该边界不能误称跨进程锁。判断活动记录同时核对 CurrentRecordId，不能只依赖瞬间已标 Completed 的状态。写入已生效后不能因请求取消遗漏后续入队，需有可验证的一致性策略。
- 新 HTTP 入口仅绑定随机 IPv4 loopback 端口，严格 Host/Origin/远端地址/HTTP 方法/Content-Type/请求大小校验，独立随机令牌且启动时轮换。连接文件位于当前用户 LocalAppData/UnpackVision/ai/connection.json，可显式指定测试路径；客户端每次调用重读、禁止代理与重定向、仅连接字面回环地址。
- 不在命令参数、stdout、日志、错误或返回数据中输出令牌。MCP stdout 仅协议；日志 stderr。允许工具发现和 CLI help/schema 在软件关闭时运行；调用时清楚报告未就绪，不自动启动其他版本、摄像头或兼容同步服务。
- 桌面退出时取消并等待 HTTP 请求，按连接文件的所有者清理；启动失败不阻塞录像。新增 API 的凭据不复用手机配对令牌。
- 软件发布产物携带 Agent 程序及依赖许可；本期不改已公开安装包，也不宣称现有安装版已支持。用户数据、配置格式和旧 API 均保留。

## 验收

使用内存仓储、临时连接文件和合成记录，验证真实本机 HTTP + MCP stdio + CLI JSON 链路、认证失败、断线、参数边界、活动录制拒绝、标记/备注/同步排队语义。不得访问正式仓储、真实录像、工作簿或相机。

参考：https://github.com/modelcontextprotocol/csharp-sdk ，https://github.com/dotnet/command-line-api 。具体依赖版本与许可须按实际还原包核对。
