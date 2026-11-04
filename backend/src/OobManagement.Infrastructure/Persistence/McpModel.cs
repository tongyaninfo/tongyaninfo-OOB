using Microsoft.EntityFrameworkCore;
using OobManagement.Domain.Models;

namespace OobManagement.Infrastructure.Persistence;

/// <summary>双 Provider 共用七张业务表；组件 OAuth 表由独立上下文维护。</summary>
public static class McpModel
{
    public static void Configure(ModelBuilder model)
    {
        model.Entity<McpSetting>(b =>
        {
            b.ToTable("OOB_MCP_SETTING", t => { t.HasComment("MCP 单例配置"); t.HasCheckConstraint("CK_OOB_MCP_SETTING_Slot", "\"Slot\" = 1"); });
            b.HasKey(x => x.Id); b.HasIndex(x => x.Slot).IsUnique(); b.Property(x => x.Version).IsConcurrencyToken();
            b.Property(x => x.EnabledToolsJson).HasColumnType("jsonb");
        });
        model.Entity<McpClient>(b =>
        {
            b.ToTable("OOB_MCP_CLIENT", t => t.HasComment("MCP 调用方及策略")); b.HasKey(x => x.Id);
            b.HasIndex(x => x.OAuthClientId).IsUnique(); b.Property(x => x.Name).HasMaxLength(100);
            b.Property(x => x.OAuthClientId).HasMaxLength(100); b.Property(x => x.ClientType).HasMaxLength(20);
            b.Property(x => x.ServerScope).HasMaxLength(32); b.Property(x => x.Version).IsConcurrencyToken();
            b.Property(x => x.OriginsJson).HasColumnType("jsonb"); b.Property(x => x.RedirectUrisJson).HasColumnType("jsonb");
            b.HasOne<OobUser>().WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Restrict);
        });
        model.Entity<McpClientTool>(b =>
        {
            b.ToTable("OOB_MCP_CLIENT_TOOL", t => t.HasComment("MCP 调用方工具白名单")); b.HasKey(x => x.Id);
            b.Property(x => x.ToolName).HasMaxLength(100); b.HasIndex(x => new { x.ClientId, x.ToolName }).IsUnique();
            b.HasOne<McpClient>().WithMany().HasForeignKey(x => x.ClientId).OnDelete(DeleteBehavior.Restrict);
        });
        model.Entity<McpClientServer>(b =>
        {
            b.ToTable("OOB_MCP_CLIENT_SERVER", t => t.HasComment("MCP 调用方设备白名单")); b.HasKey(x => x.Id);
            b.HasIndex(x => new { x.ClientId, x.ServerId }).IsUnique(); b.HasIndex(x => x.ServerId);
            b.HasOne<McpClient>().WithMany().HasForeignKey(x => x.ClientId).OnDelete(DeleteBehavior.Restrict);
            b.HasOne<ManagedServer>().WithMany().HasForeignKey(x => x.ServerId).OnDelete(DeleteBehavior.Restrict);
        });
        model.Entity<McpClientNetwork>(b =>
        {
            b.ToTable("OOB_MCP_CLIENT_NETWORK", t => t.HasComment("MCP 来源网络白名单")); b.HasKey(x => x.Id);
            b.Property(x => x.Network).HasMaxLength(100); b.Property(x => x.AddressFamily).HasMaxLength(16);
            b.HasIndex(x => new { x.ClientId, x.Network }).IsUnique();
            b.HasOne<McpClient>().WithMany().HasForeignKey(x => x.ClientId).OnDelete(DeleteBehavior.Restrict);
        });
        model.Entity<McpCallLog>(b =>
        {
            b.ToTable("OOB_MCP_CALL_LOG", t => t.HasComment("MCP 独立调用审计")); b.HasKey(x => x.Id);
            b.HasIndex(x => x.CallId).IsUnique(); b.HasIndex(x => new { x.OccurredAt, x.Id }).IsDescending(true, true);
            b.HasIndex(x => new { x.ClientId, x.OccurredAt }); b.HasIndex(x => new { x.Tool, x.OccurredAt });
            b.HasIndex(x => new { x.ServerId, x.OccurredAt }); b.HasIndex(x => new { x.Result, x.OccurredAt }); b.HasIndex(x => x.TraceId);
            b.Property(x => x.CallId).HasMaxLength(64); b.Property(x => x.ClientName).HasMaxLength(100);
            b.Property(x => x.Method).HasMaxLength(100); b.Property(x => x.Tool).HasMaxLength(100);
            b.Property(x => x.SourceIp).HasMaxLength(64); b.Property(x => x.ProtocolVersion).HasMaxLength(32);
            b.Property(x => x.Result).HasMaxLength(32); b.Property(x => x.ErrorCode).HasMaxLength(100);
            b.Property(x => x.TraceId).HasMaxLength(128); b.Property(x => x.RequestId).HasMaxLength(128);
            b.Property(x => x.ParametersSummary).HasMaxLength(4096); b.Property(x => x.OutputSummary).HasMaxLength(4096);
            b.Property(x => x.PolicySummary).HasMaxLength(1000);
        });
        model.Entity<McpConfigAudit>(b =>
        {
            b.ToTable("OOB_MCP_CONFIG_AUDIT", t => t.HasComment("MCP 配置与授权变更审计")); b.HasKey(x => x.Id);
            b.HasIndex(x => new { x.OccurredAt, x.Id }).IsDescending(true, true);
            b.Property(x => x.Action).HasMaxLength(100); b.Property(x => x.ObjectId).HasMaxLength(100); b.Property(x => x.Summary).HasMaxLength(4096);
        });
        var comments = new Dictionary<string, string>
        {
            ["Id"]="统一发号主键", ["Slot"]="单例配置槽位", ["Enabled"]="是否启用", ["Version"]="策略并发版本",
            ["RequestsPerMinute"]="每调用方每分钟请求限额", ["ClientConcurrency"]="每调用方并发上限", ["GlobalConcurrency"]="全局并发上限",
            ["QueryTimeoutSeconds"]="查询超时秒数", ["MaxRequestBytes"]="请求字节上限", ["MaxResponseBytes"]="响应字节上限",
            ["EnabledToolsJson"]="全局启用工具集合", ["UpdatedByUserId"]="最后配置操作人", ["UpdatedAt"]="更新时间UTC",
            ["Name"]="调用方名称", ["OAuthClientId"]="OAuth 客户端公开标识", ["ClientType"]="公共或保密客户端", ["UserId"]="绑定用户主键",
            ["ExpiresAt"]="到期时间UTC", ["ServerScope"]="全部或指定设备范围", ["OriginsJson"]="浏览器Origin白名单", ["RedirectUrisJson"]="精确OAuth回调地址",
            ["CreatedAt"]="创建时间UTC", ["LastCalledAt"]="最近调用时间UTC", ["ClientId"]="调用方业务主键", ["ToolName"]="获准工具名",
            ["ServerId"]="设备主键", ["Network"]="规范化IP或CIDR", ["AddressFamily"]="地址族", ["CallId"]="稳定平台调用标识",
            ["OccurredAt"]="发生时间UTC", ["CompletedAt"]="完成时间UTC", ["ClientName"]="调用方名称快照", ["Method"]="协议方法",
            ["Tool"]="工具名称", ["SourceIp"]="可信来源地址", ["ProtocolVersion"]="MCP协议版本", ["Result"]="实际执行结果",
            ["ErrorCode"]="稳定错误码", ["HttpStatusCode"]="实际HTTP状态", ["DurationMilliseconds"]="耗时毫秒", ["TraceId"]="追踪标识",
            ["RequestId"]="协议请求标识安全摘要", ["ParametersSummary"]="参数字段白名单摘要", ["OutputSummary"]="输出安全摘要",
            ["PolicySummary"]="命中策略摘要", ["ExceptionId"]="关联故障记录", ["Action"]="配置操作", ["ObjectId"]="目标标识",
            ["PreviousVersion"]="操作前版本", ["CurrentVersion"]="操作后版本", ["Summary"]="脱敏配置变更说明"
        };
        foreach (var entity in model.Model.GetEntityTypes().Where(x => x.ClrType.Name.StartsWith("Mcp", StringComparison.Ordinal)))
        {
            model.Entity(entity.ClrType).Property("Id").ValueGeneratedNever();
            foreach (var property in entity.GetProperties()) property.SetComment(comments[property.Name]);
        }
    }
}
