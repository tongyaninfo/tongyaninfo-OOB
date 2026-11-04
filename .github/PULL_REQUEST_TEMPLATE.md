## 变更说明

<!-- 说明本次变更的目的、实现方式与影响范围 -->

## 关联 Issue

<!-- 例如：Closes #123 -->

## 变更类型

- [ ] 缺陷修复
- [ ] 新增功能
- [ ] 文档
- [ ] 重构 / 性能
- [ ] 构建 / 依赖 / 工具
- [ ] 数据库迁移
- [ ] 接口合同变更
- [ ] 部署配置变更

## 验证记录

请填写**实际执行**的命令与结果（通过 / 失败 / 跳过及原因），未执行的不要勾选：

- [ ] `node scripts/doctor.mjs`
- [ ] `node scripts/verify.mjs --scope docs`
- [ ] `dotnet build backend/OobManagement.slnx`
- [ ] `dotnet test backend/tests/OobManagement.Driver.Tests/OobManagement.Driver.Tests.csproj`
- [ ] `npm --prefix frontend/PC run lint`
- [ ] `npm --prefix frontend/PC test`
- [ ] `npm --prefix frontend/PC run build`
- [ ] `npm --prefix desktop run typecheck`
- [ ] `npm --prefix desktop test`
- [ ] `npm --prefix desktop run build`
- [ ] `node scripts/oob-contracts.mjs check`
- [ ] 真实浏览器 / 真实设备 / 数据库验证（请说明范围与结果）

命令与结果：

```text
<!-- 粘贴实际输出摘要 -->
```

## 安全与脱敏确认

- [ ] 本次提交不包含真实凭据、内网地址、真实设备序列号或客户信息
- [ ] 示例数据使用 RFC 5737 文档网段与合成名称
- [ ] 未代填他人评审意见或人工验收结论
