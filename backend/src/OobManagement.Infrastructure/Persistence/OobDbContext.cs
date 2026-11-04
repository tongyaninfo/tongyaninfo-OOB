using Microsoft.EntityFrameworkCore;
using OobManagement.Domain.Models;
using OobManagement.Infrastructure.Persistence.Service;
using OobManagement.Infrastructure.Persistence.Service.impl;

namespace OobManagement.Infrastructure.Persistence;

/// <summary>所有业务表由显式 EF 迁移维护，普通启动不自动创建结构。</summary>
public class OobDbContext : DbContext
{
    public OobDbContext(DbContextOptions<OobDbContext> options) : base(options) { }
    protected OobDbContext(DbContextOptions options) : base(options) { }

    /// <summary>默认保留服务端 PostgreSQL 行为；桌面子类仅替换存储差异，不复制业务服务。</summary>
    public virtual IOobStorageDialect Storage => PostgreSqlStorageDialect.Instance;
    public DbSet<OobUser> Users => Set<OobUser>();
    public DbSet<UserPreference> Preferences => Set<UserPreference>();
    public DbSet<ManagedServer> Servers => Set<ManagedServer>();
    public DbSet<ServerInventory> Inventories => Set<ServerInventory>();
    public DbSet<MetricSeries> Series => Set<MetricSeries>();
    public DbSet<MetricSample> Samples => Set<MetricSample>();
    public DbSet<PollRun> PollRuns => Set<PollRun>();
    public DbSet<PowerOperation> PowerOperations => Set<PowerOperation>();
    public DbSet<SystemExceptionRecord> ExceptionLogs => Set<SystemExceptionRecord>();
    public DbSet<MailConfiguration> MailConfigurations => Set<MailConfiguration>();
    public DbSet<MailMessage> MailMessages => Set<MailMessage>();
    public DbSet<McpSetting> McpSettings => Set<McpSetting>();
    public DbSet<McpClient> McpClients => Set<McpClient>();
    public DbSet<McpClientTool> McpClientTools => Set<McpClientTool>();
    public DbSet<McpClientServer> McpClientServers => Set<McpClientServer>();
    public DbSet<McpClientNetwork> McpClientNetworks => Set<McpClientNetwork>();
    public DbSet<McpCallLog> McpCallLogs => Set<McpCallLog>();
    public DbSet<McpConfigAudit> McpConfigAudits => Set<McpConfigAudit>();

    protected override void OnModelCreating(ModelBuilder model)
    {
        model.Entity<OobUser>(b =>
        {
            b.ToTable("OOB_USER", t =>
            {
                t.HasComment("系统用户");
                t.HasCheckConstraint("CK_OOB_USER_Versions", "\"Version\" > 0 AND \"AuthVersion\" > 0");
                t.HasCheckConstraint("CK_OOB_USER_Username", "length(btrim(\"Username\")) > 0");
            });
            b.HasKey(x => x.Id);
            b.Property(x => x.Username).HasMaxLength(100);
            b.Property(x => x.DisplayName).HasMaxLength(100);
            b.Property(x => x.NormalizedUsername).HasMaxLength(200).HasComputedColumnSql("upper(btrim(\"Username\"))", stored: true);
            b.HasIndex(x => x.NormalizedUsername).IsUnique().HasFilter("\"DeletedAt\" IS NULL");
            b.Property(x => x.Version).IsConcurrencyToken();
            b.Property(x => x.CanManageServers).HasDefaultValue(false);
            b.Property(x => x.CanOperatePower).HasDefaultValue(false);
        });
        model.Entity<UserPreference>(b => { b.ToTable("OOB_USER_PREFERENCE", t => t.HasComment("账号外观与表格设置")); b.HasKey(x => x.Id); b.HasIndex(x => x.UserId).IsUnique(); b.Property(x => x.SettingsJson).HasColumnType("jsonb"); b.HasOne<OobUser>().WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Restrict); });
        model.Entity<ManagedServer>(b => { b.ToTable("OOB_SERVER", t => { t.HasComment("受管理服务器"); t.HasCheckConstraint("CK_SERVER_INTERVAL", "\"SamplingIntervalSeconds\" BETWEEN 1 AND 86400"); }); b.HasKey(x => x.Id); b.HasIndex(x => new { x.Address, x.ControllerType }).IsUnique(); b.Property(x => x.Name).HasMaxLength(100); b.Property(x => x.Address).HasMaxLength(255); b.Property(x => x.IdentityJson).HasColumnType("jsonb"); b.Property(x => x.CapabilitiesJson).HasColumnType("jsonb"); b.Property(x => x.LatestTelemetryJson).HasColumnType("jsonb");
            // 数据库默认满足新增/既有行初始策略；EF 始终写入明确的 false，避免被默认 true 替代。
            b.Property(x => x.IgnoreCertificateErrors).HasDefaultValue(true).ValueGeneratedNever();
        });
        model.Entity<ServerInventory>(b => { b.ToTable("OOB_SERVER_INVENTORY", t => t.HasComment("服务器硬件快照")); b.HasKey(x => x.Id); b.HasIndex(x => new { x.ServerId, x.CollectedAt }); b.Property(x => x.PayloadJson).HasColumnType("jsonb"); b.HasOne<ManagedServer>().WithMany().HasForeignKey(x => x.ServerId).OnDelete(DeleteBehavior.Restrict); });
        model.Entity<MetricSeries>(b => { b.ToTable("OOB_METRIC_SERIES", t => t.HasComment("传感器指标系列")); b.HasKey(x => x.Id); b.HasIndex(x => new { x.ServerId, x.SensorKey, x.MetricCode }).IsUnique(); b.HasOne<ManagedServer>().WithMany().HasForeignKey(x => x.ServerId).OnDelete(DeleteBehavior.Restrict); });
        model.Entity<MetricSample>(b => { b.ToTable("OOB_METRIC_SAMPLE", t => t.HasComment("时序采样数据")); b.HasKey(x => new { x.SeriesId, x.SampledAt }); b.HasIndex(x => new { x.SeriesId, x.SampledAt }).IsDescending(false, true); b.HasOne<MetricSeries>().WithMany().HasForeignKey(x => x.SeriesId).OnDelete(DeleteBehavior.Restrict); });
        model.Entity<PollRun>(b => { b.ToTable("OOB_POLL_RUN", t => t.HasComment("采集执行记录")); b.HasKey(x => x.Id); b.HasIndex(x => new { x.ServerId, x.StartedAt }); b.HasOne<ManagedServer>().WithMany().HasForeignKey(x => x.ServerId).OnDelete(DeleteBehavior.Restrict); });
        model.Entity<PowerOperation>(b => { b.ToTable("OOB_POWER_OPERATION", t => t.HasComment("主机电源操作意图与审计")); b.HasKey(x => x.Id); b.HasIndex(x => x.RequestId).IsUnique(); b.HasIndex(x => new { x.ServerId, x.CreatedAt }); b.HasIndex(x => x.ServerId).IsUnique().HasFilter("\"Status\" IN ('Submitting', 'Submitted', 'Unknown')"); b.HasOne<ManagedServer>().WithMany().HasForeignKey(x => x.ServerId).OnDelete(DeleteBehavior.Restrict); b.HasOne<OobUser>().WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Restrict); });
        model.Entity<SystemExceptionRecord>(b =>
        {
            b.ToTable("OOB_SYSTEM_EXCEPTION", t => t.HasComment("系统、设备与客户端实际故障的脱敏记录"));
            b.HasKey(x => x.Id);
            b.HasIndex(x => new { x.OccurredAt, x.Id }).IsDescending(true, true);
            b.HasIndex(x => new { x.ServerId, x.OccurredAt }).IsDescending(false, true);
            b.HasIndex(x => new { x.Kind, x.OccurredAt }).IsDescending(false, true);
            b.Property(x => x.Kind).HasMaxLength(32); b.Property(x => x.Level).HasMaxLength(16);
            b.Property(x => x.Source).HasMaxLength(64); b.Property(x => x.Operation).HasMaxLength(100);
            b.Property(x => x.ErrorCode).HasMaxLength(100); b.Property(x => x.Message).HasMaxLength(2000);
            b.Property(x => x.ExceptionType).HasMaxLength(300); b.Property(x => x.StackTrace).HasColumnType("text");
            b.Property(x => x.TraceId).HasMaxLength(64); b.Property(x => x.RequestPath).HasMaxLength(512);
            b.Property(x => x.RequestMethod).HasMaxLength(16);
        });

        // 表与字段中文注释随模型进入迁移，新增属性不能悄悄缺失数据库说明。
        var comments = new Dictionary<string, string>
        {
            ["Id"]="统一发号主键", ["UserId"]="所属用户", ["Username"]="账号", ["PasswordHash"]="密码哈希", ["PasswordCiphertext"]="设备密码密文",
            ["NormalizedUsername"]="数据库计算的去首尾空格大写账号，非删除账号唯一", ["DisplayName"]="用户显示姓名", ["DeletedAt"]="逻辑删除时间UTC，空值表示未删除",
            ["IsAdministrator"]="兼容既有系统管理员能力，普通用户管理不可赋予", ["CanManageUsers"]="是否具有用户管理权限", ["CanUseConsole"]="是否具有虚拟控制台及介质操作权限",
            ["CanManageServers"]="是否具有服务器配置、连接测试、UID及手动采集维护权限", ["CanOperatePower"]="是否具有主机电源操作及电源审计核对权限",
            ["AuthVersion"]="身份安全版本，账号密码状态权限变化后使旧会话失效", ["Version"]="编辑并发版本",
            ["Enabled"]="是否启用", ["Name"]="名称", ["Brand"]="品牌", ["ControllerType"]="带外控制器类型", ["Address"]="管理地址", ["CertificateFingerprint"]="可信证书指纹", ["IgnoreCertificateErrors"]="是否忽略当前设备HTTPS证书错误；显式指纹仍优先校验",
            ["SamplingIntervalSeconds"]="采样间隔秒数", ["IdentityJson"]="设备身份", ["CapabilitiesJson"]="设备能力", ["LatestTelemetryJson"]="最近实时采样",
            ["ConnectionStatus"]="连接状态", ["LastCollectedAt"]="最后成功采样时间", ["LastInventoryAt"]="最后库存刷新时间", ["NextPollAt"]="下次采集时间",
            ["ConsecutiveFailures"]="连续失败次数", ["LastError"]="最后安全错误摘要", ["CreatedAt"]="创建时间UTC", ["UpdatedAt"]="更新时间UTC",
            ["SettingsJson"]="设置白名单内容", ["ServerId"]="服务器主键", ["CollectedAt"]="库存采集时间UTC", ["PayloadJson"]="硬件快照",
            ["SensorKey"]="稳定资源标识", ["MetricCode"]="指标编码", ["Unit"]="单位", ["SeriesId"]="指标系列主键", ["SampledAt"]="采样时间UTC",
            ["Value"]="采样值空值表示缺测", ["Availability"]="可用性", ["Source"]="读数来源", ["DeviceTime"]="设备观察时间UTC", ["StartedAt"]="开始时间UTC",
            ["CompletedAt"]="完成时间UTC", ["Status"]="执行状态", ["MetricCount"]="读数数量", ["ErrorMessage"]="安全错误摘要", ["RequestId"]="幂等请求标识",
            ["Action"]="主机电源动作", ["Message"]="安全结果摘要", ["TaskLocation"]="设备任务资源位置",
            ["OccurredAt"]="故障发生时间UTC", ["Kind"]="故障类别", ["Level"]="故障级别", ["Operation"]="故障发生的受控操作名",
            ["ErrorCode"]="稳定故障编码", ["ExceptionType"]="异常类型不含消息", ["StackTrace"]="脱敏且限长的代码位置",
            ["TraceId"]="关联追踪标识", ["RequestPath"]="请求路径不含查询串", ["RequestMethod"]="HTTP请求方法", ["HttpStatusCode"]="HTTP响应状态码"
        };
        foreach (var entity in model.Model.GetEntityTypes())
        {
            if (entity.FindProperty("Id") is not null) model.Entity(entity.ClrType).Property("Id").ValueGeneratedNever();
            foreach (var property in entity.GetProperties())
                property.SetComment(comments.GetValueOrDefault(property.Name, "业务字段"));
        }
        model.Entity<SystemExceptionRecord>().Property(x => x.Source).HasComment("故障来源模块");
        // 邮件字段使用所属模块的准确说明，不被上面的通用字段名说明覆盖。
        MailModel.Configure(model);
        McpModel.Configure(model);
    }
}
