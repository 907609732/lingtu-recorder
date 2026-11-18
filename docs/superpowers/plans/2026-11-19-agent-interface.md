# Recorder Agent Interface Implementation Plan

**Goal:** 为本机智能体提供已选范围的 MCP 与 CLI，统一结构化调用正在运行的桌面程序。

**Architecture:** Application 记录自动化用例；WPF 本机认证 HTTP 适配与生命周期；独立 Agent 宿主提供官方 MCP stdio 和 System.CommandLine CLI，共享受限 HTTP 客户端。

**Spec:** ../specs/2026-11-19-agent-interface.md

## Tasks

- [x] 1. 为记录自动化用例编写失败测试，实现查询、归档记录注释、Excel 队列与活动录制拒绝；仅依赖 Core。
- [x] 2. 为随机回环 HTTP 适配编写认证/边界/连接生命周期测试；完成 Host/Origin/体积/方法限制并接入 WPF 初始化和退出，失败不阻塞录像。
- [x] 3. 以官方 ModelContextProtocol 2.4.0、System.CommandLine 2.0.15（实际还原兼容后固定）实现共用客户端与 8 个 MCP 工具、对应 CLI 子命令和 schema/help。测试连接文件轮换、不可用、代理/重定向禁用、JSON 错误和字段校验。
- [x] 4. 加入真实 stdio MCP/CLI 合成端到端验收；核对工具发现、查询/写入/入队及错误行为，隔离数据与日志。
- [x] 5. 更新 solution、发布脚本、模块图、第三方许可、中英文接入说明与首页入口。确认 Agent 随发布目录交付，不发布新版安装包。
- [ ] 6. Codex 独立审查 diff、执行专项与必需构建检查、检查打包文件和协议输出，修复问题后再整合 GitHub。

## Verification evidence

- First TDD run before implementation: exit 1 (missing Agent projects/types), then 28 passed / 11 failed; fixes were tampered-file test setup, loopback/token test data, and request cancellation during host stop.
- First-round full Debug `scripts/build.ps1 -Configuration Debug`: exit 0; 411 main tests + 3 isolated WPF tests passed. After the shared gate and queue-repair changes the full main suite passed 413 tests and the three isolated WPF tests passed separately.
- First-round Agent-focused Debug and Release tests: exit 0; 47 passed in each configuration.
- First-round Release solution build: exit 0, 0 warnings.
- First-round self-contained Agent publish and `App/Agent` layout smoke test: exit 0; CLI schema/help exit 0, offline get-status exit 2; published Agent E2E passed 6 tests. This round did not rerun publish/installer steps, per the follow-up instruction.
- Second-round reviewer P1/P2 targeted verification: tests build exit 0; `FullyQualifiedName~Agent` 61 passed; `HistoryWindowUiTests|AgentServiceRegistrationTests` 2 passed; no current full-suite claim.
- Detailed commands, failures and remaining limits are in `docs/parallel/agent-interface-20261119.md`.
- Task 6 remains open because no GitHub integration/commit/push is allowed in this worktree and independent review/cloud validation is still pending.

## Review focus

录制中编辑与内存态冲突；请求断开后的未知写入结果；令牌或模型返回中的凭据泄漏；连接文件被篡改导致非本机访问；服务退出后悬挂的请求和另一个实例的连接文件。
