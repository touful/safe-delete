# SafeDelete 审计日志 Schema

## 当前版本：v2

## 字段清单
| JSON key | 类型 | 说明 | 是否必现 | 引入版本 |
|:---|:---|:---|:---:|:---:|
| `schema_version` | int | 恒 2 | ✅ | v2 |
| `ts` | string | UTC ISO-8601 毫秒精度带 Z，如 `2026-07-01T14:21:59.099Z` | ✅ | v2 |
| `op` | string | 12 位小写 hex，取自 GUID(N)[..12] | ✅ | v2 |
| `event` | enum | `decision` 或 `result` | ✅ | v2 |
| `user` | string | WindowsIdentity.Name（Windows 下为 `MACHINE\user` 或 `DOMAIN\user`） | ✅ | v2 |
| `pid` | int | 进程 ID | ✅ | v2 |
| `cwd` | string | 绝对路径，作为 path 字段的锚点 | ✅ | v2 |
| `args` | string[] | 原始命令行参数（含 `--reason` 原文） | ✅ | v2 |
| `path` | string | 相对 cwd 的相对路径；跨盘或异常时回退绝对路径 | ✅ | v2 |
| `input` | string? | 用户键入的原始路径 | 否 | v2 |
| `exists` | bool? | 目标是否存在 | 否 | v2 |
| `kind` | enum | `file` 或 `dir` | 否 | v2 |
| `files` | long? | 文件数（目录时） | 否 | v2 |
| `dirs` | long? | 子目录数（目录时） | 否 | v2 |
| `bytes` | long? | 总字节数 | 否 | v2 |
| `dry_run` | bool? | 仅 `true` 时出现 | 否 | v2 |
| `yes` | bool? | 仅 `true` 时出现 | 否 | v2 |
| `reason` | string? | 用户提供的动机文本 | 否 | v2 |
| `decision` | enum | `allowed` 或 `rejected` | 否 | v2 |
| `deny` | string? | 仅拒绝时出现 | 否 | v2 |
| `result` | enum | 见下方 result 值语义 | 否 | v2 |
| `ex_type` | string? | 异常类型全名，仅异常时出现 | 否 | v2 |
| `ex_msg` | string? | 异常消息，仅异常时出现 | 否 | v2 |
| `exit` | int? | 退出码 | 否 | v2 |

## result 值语义
- `dry_run`：dry-run 分支成功结束
- `execute_started`：pre-delete 已批准，删除即将执行（本条事件写入成功是"敢删"的前置条件）
- `deleted_to_recycle_bin`：删除已成功进入回收站
- `delete_failed`：删除操作抛异常
- `policy_rejected`：策略拒绝（超出工作目录、通配符、敏感目录等）
- `not_found`：目标不存在
- `argument_error`：参数解析错误（含空路径）
- `confirmation_rejected`：用户未确认删除
- `pre_delete_revalidation_failed`：删除前重新验证失败（目标变更或策略状态变化）

## 退出码
| 退出码 | 语义 | 触发条件 |
|:---:|:---|:---|
| 0 | 成功 | 删除成功 或 dry-run 完成 或 --help |
| 2 | argument_error | 参数解析层拦截（含空路径） |
| 3 | policy_rejected / confirmation_rejected | 策略拒绝或用户未确认 |
| 4 | not_found | 目标不存在 |
| 5 | delete_failed | 删除操作本身抛异常 |
| 10 | pre-delete 审计写入失败 | 删除未发生 |
| 11 | post-delete 审计**全部**写入失败 | 删除已发生但结果事件未持久化 |

## 审计闭环语义（F-1 应答）
- **pre-delete 事件**（`result=execute_started`）采用 **Strict** 写入：任一日志失败即拒绝执行删除，返回 exit 10。
- **post-delete 事件**（`result=deleted_to_recycle_bin` 或 `delete_failed`）采用 **BestEffort** 写入：至少一个日志成功即视为闭环；若发生部分失败，向 stderr 输出 `Warning: audit partial write failure: ...`；仅当**全部**日志失败时返回 exit 11。
- **组合语义**：只要看到某 `op` 的 `execute_started` 事件，无论后续是否有 `result` 事件，都可从 exit code 判断最终状态：
  - exit 0 → 删除成功
  - exit 5 → 删除失败
  - exit 11 → 删除已发生但审计全部写入失败（需运维介入回收站校验）

## v1 → v2 字段映射
| v1 字段 | v2 字段 | 变更说明 |
|:---|:---|:---|
| `timestamp_utc` | `ts` | 精度从微秒降到毫秒 |
| `operation_id` | `op` | 32 字符截短为 12 |
| `event_type` | `event` | 字段名缩短 |
| `working_directory` | `cwd` | 字段名缩短 |
| `normalized_path` | `path` | 改为相对路径 |
| `target_kind` | `kind` | 字段名缩短 |
| `target_exists` | `exists` | 字段名缩短 |
| `input_path` | `input` | 字段名缩短 |
| `file_count` | `files` | 字段名缩短 |
| `directory_count` | `dirs` | 字段名缩短 |
| `total_bytes` | `bytes` | 字段名缩短 |
| `denial_reason` | `deny` | 字段名缩短 |
| `exception_type` | `ex_type` | 字段名缩短 |
| `exception_message` | `ex_msg` | 字段名缩短 |
| `exit_code` | `exit` | 字段名缩短 |
| `raw_arguments` | `args` | 字段名缩短 |
| `tool` | — | 删除 |
| `process_path` | — | 删除 |
| `command_line` | — | 删除 |
| `audit_log_paths` | — | 删除 |
| `machine` | — | 删除 |
| `user` (`MACHINE\user@MACHINE`) | `user` (`MACHINE\user`) | 去除重复机器名后缀 |

## 版本演进策略
- 破坏性变更必须 bump `schema_version` 并在本文件维护映射表。
- JSONL 按行追加，旧版本行保留不改写。
- 下游解析方应先读 `schema_version` 字段再解析。

## 隐私披露声明（F-3 应答）
审计日志将包含以下本地/身份信息，请勿上传至不可信环境：
- `args`：包括用户 `--reason` 中的原文。
- `reason`：用户提供的动机文本。
- `cwd`：本机工作目录绝对路径（可能暴露目录结构）。
- `user`：Windows 身份标识（可能包含机器名或域名前缀）。
- `input` / `path`：目标路径信息。

未来版本可能通过环境变量提供脱敏选项（尚未实现）。

## 已知限制（F-6/F-7 应答）
- 空路径 / 全空白路径将在参数解析层被 `argument_error` 拦截（exit 2），不会进入 policy 层判定。此为设计决策，已在参数解析层完成防御。
- 交互式确认分支（无 `--yes` 时的 TTY 输入）在自动化环境中无法覆盖，需人工终端验收。
