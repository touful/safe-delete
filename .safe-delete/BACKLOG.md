# SafeDelete 后续工单

## B-01 [来源 F-3] 审计日志敏感信息脱敏
- **优先级**：Medium
- **描述**：为 args、reason、cwd 等字段提供可选脱敏机制
- **建议方案**：环境变量 `SAFE_DELETE_REDACT=args,reason,cwd,user` 控制字段级脱敏
- **前置**：与用户对齐脱敏策略与场景

## B-02 [来源 F-7] 交互式确认分支自动化测试机制
- **优先级**：Low
- **描述**：当前 TTY 交互确认无法自动化覆盖
- **建议方案**：抽象 `IConfirmationProvider` 接口，测试用例注入替代实现；或引入 `--auto-confirm` 隐藏开关仅用于测试
- **前置**：需评估对 CLI 语义清晰度的影响

## B-03 [来源 F-6] 空路径处理层次统一
- **优先级**：Low / 不紧迫
- **描述**：空路径当前在 Parse 层拦截返回 `argument_error`（exit 2）。若希望统一由 PolicyEvaluator 判定为 `policy_rejected`（exit 3），需调整 Parse 层放行空值
- **决策**：已在 SCHEMA.md 中记录为设计决策，除非有强需求否则维持现状
