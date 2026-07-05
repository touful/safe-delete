# SafeDelete

将文件或目录安全移动到 Windows 回收站（而非永久删除），并记录完整的审计日志。

## 功能特性

- **安全删除**：将目标移入回收站，可随时从回收站还原，避免误删后无法恢复
- **多层安全策略**：禁止删除系统目录、父目录段（`..`）、通配符、符号链接等高风险目标
- **目录隔离**：仅允许删除当前工作目录内的文件，防止越权操作
- **TOCTOU 防护**：删除前重新验证目标快照未变化，防止竞态攻击
- **试运行模式**：`--dry-run` 可预览删除效果而不实际操作
- **双通道审计日志**：项目级和用户级两路 JSONL 日志，满足可追溯性要求
- **审计闭环语义**：删除前日志采用严格写入（失败即拒绝删除），删除后日志采用尽力写入，确保审计完整性
- **自动化友好**：`--yes` 参数跳过交互确认，适配脚本和 CI/CD 场景

## 系统要求

- **操作系统**：Windows（依赖 Windows Shell API 进行回收站操作）
- **运行时**：[.NET 8.0 Runtime](https://dotnet.microsoft.com/zh-cn/download/dotnet/8.0) 或更高版本

## 安装方式

### 从 Release 下载（推荐）

前往 [Releases](https://github.com/touful/SafeDelete/releases) 页面下载最新版本的 `safe-delete.exe`，放置到任意 `PATH` 目录下即可。建议放入 `%USERPROFILE%\.local\bin\` 等用户级可执行目录。

### 从源码编译

```powershell
git clone https://github.com/touful/SafeDelete.git
cd SafeDelete
dotnet build -c Release
```

编译产物位于 `bin\Release\net8.0-windows\safe-delete.exe`，将其复制到 PATH 目录即可使用。

## 使用方法

### 基本语法

```
safe-delete <path> [--dry-run] [--reason <text>] [--yes]
```

### 参数说明

| 参数 | 必需 | 说明 |
|:---|:---:|:---|
| `<path>` | ✅ | 要删除的文件或目录路径（必须在当前工作目录内） |
| `--dry-run` | 否 | 试运行：评估安全策略并预览结果，不实际删除 |
| `--reason <text>` | 否 | 删除原因，记录到审计日志中 |
| `--yes` | 否 | 跳过交互确认，适用于脚本和自动化场景 |
| `-h` / `--help` | 否 | 显示帮助信息 |

### 使用示例

```powershell
# 基本使用 —— 交互式确认后删除
safe-delete .\临时文件.txt

# 带原因说明
safe-delete .\build\ --reason "清理旧的构建产物"

# 试运行 —— 预览而不实际删除
safe-delete .\data\ --dry-run --reason "检查策略是否允许"

# 自动化脚本中使用（跳过确认）
safe-delete .\temp\ --yes --reason "计划任务清理临时目录"
```

## 安全策略

SafeDelete 内置多层安全策略（由 `PolicyEvaluator` 实现），删除操作前逐项检查，任一不通过即拒绝执行：

1. **禁止通配符**：路径中不能包含 `*` 或 `?`
2. **禁止父目录段**：路径中不能包含 `..`
3. **工作目录隔离**：目标必须在当前工作目录（`cwd`）内
4. **禁止删除当前目录**：不允许删除工作目录本身
5. **禁止删除根目录**：不允许删除系统根目录
6. **禁止删除审计日志**：不允许删除 `.safe-delete/` 日志目录
7. **禁止删除系统路径**：用户主目录、`Windows`、`System`、`Program Files` 等敏感路径不可删除
8. **禁止 reparse point**：不会跟踪符号链接、junction、挂载点等（防止误删链接指向的真实数据）
9. **TOCTOU 防护**：策略通过后、实际删除前，重新验证目标快照与评估时一致
10. **二次确认**：交互模式下需输入 "DELETE" 方可执行（除非指定 `--yes`）

## 审计日志

### 双日志机制

SafeDelete 采用双通道写入，确保审计记录的可用性和完整性：

- **项目级日志**：`<工作目录>/.safe-delete/audit.jsonl`
- **用户级日志**：`%LocalAppData%\SafeDelete\audit.jsonl`（可通过环境变量 `SAFE_DELETE_USER_LOG_DIR` 自定义）

### 日志格式

日志采用 JSONL 格式（每行一个 JSON 对象），包含时间戳、操作 ID、事件类型、目标信息、执行结果等字段。详细字段说明和版本演进历史请参阅 [`SCHEMA.md`](./SCHEMA.md)。

### 审计闭环语义

| 阶段 | 写入策略 | 失败行为 |
|:---|:---|:---|
| 删除前（`execute_started`） | **Strict**：任一日志写入失败即停止 | 拒绝执行删除，返回退出码 10 |
| 删除后（`deleted_to_recycle_bin` / `delete_failed`） | **BestEffort**：尽力写入 | 至少一个日志成功即视为闭环；全部失败返回退出码 11 |

## 退出码

| 退出码 | 语义 | 触发条件 |
|:---:|:---|:---|
| 0 | 成功 | 删除成功 / dry-run 完成 / 帮助信息显示 |
| 2 | 参数解析错误 | 路径为空、参数格式错误等 |
| 3 | 策略拒绝或用户未确认 | 违反安全策略 或 用户未输入 "DELETE" |
| 4 | 目标不存在 | 指定路径不存在 |
| 5 | 删除操作失败 | 移入回收站时发生异常 |
| 10 | 删除前审计写入失败 | 日志写入失败，删除未执行 |
| 11 | 删除后审计全部写入失败 | 删除已发生但结果事件未持久化 |

## 开发

### 构建

```powershell
dotnet build -c Release
```

### 项目结构

```
SafeDelete/
├── Program.cs          # 全部源码（CLI 解析、策略评估、审计日志、回收站操作）
├── SafeDelete.csproj    # .NET 项目配置文件
├── SCHEMA.md           # 审计日志 Schema 详细文档
├── README.md           # 项目说明
└── LICENSE             # MIT 许可证
```

项目使用 C# 编写，目标框架 `.NET 8.0 Windows`，无外部 NuGet 依赖，仅使用 .NET 标准库和 Windows Shell API。

### 编译要求

- [.NET 8.0 SDK](https://dotnet.microsoft.com/zh-cn/download/dotnet/8.0) 或更高版本
- 项目启用 `TreatWarningsAsErrors`，确保所有警告均被修复

## 许可证

本项目采用 [MIT 许可证](./LICENSE)。Copyright (c) 2026 touful。
