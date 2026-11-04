using Microsoft.EntityFrameworkCore;
using OobManagement.Domain.Models;

namespace OobManagement.Infrastructure.Persistence;

/// <summary>两张邮件表的共享映射；由 PostgreSQL 与桌面 SQLite 各自的显式迁移链维护。</summary>
public static class MailModel
{
    public static void Configure(ModelBuilder model)
    {
        model.Entity<MailConfiguration>(b =>
        {
            b.ToTable("OOB_MAIL_CONFIGURATION", t =>
            {
                t.HasComment("唯一SMTP发件配置；密码由宿主密钥以邮件独立用途加密");
                t.HasCheckConstraint("CK_OOB_MAIL_CONFIGURATION_Slot", "\"Slot\" = 1");
                t.HasCheckConstraint("CK_OOB_MAIL_CONFIGURATION_Options", "\"Port\" BETWEEN 1 AND 65535 AND \"Version\" > 0 AND \"SecurityMode\" IN ('SslOnConnect','StartTls')");
            });
            b.HasKey(x => x.Id); b.Property(x => x.Id).ValueGeneratedNever().HasComment("统一发号主键");
            b.HasIndex(x => x.Slot).IsUnique();
            b.Property(x => x.Slot).HasComment("固定为1的唯一配置槽，禁止创建多个SMTP配置");
            b.Property(x => x.Host).HasMaxLength(253).HasComment("SMTP主机名或地址，不含协议和路径");
            b.Property(x => x.Port).HasComment("SMTP端口，默认465");
            b.Property(x => x.SecurityMode).HasMaxLength(32).HasComment("SslOnConnect或强制StartTls，严格验证证书");
            b.Property(x => x.FromAddress).HasMaxLength(320).HasComment("发件邮箱地址");
            b.Property(x => x.Username).HasMaxLength(320).HasComment("SMTP认证账号，空值使用发件邮箱");
            b.Property(x => x.PasswordCiphertext).HasMaxLength(4096).HasComment("宿主DataProtection邮件专用purpose保护的密码密文");
            b.Property(x => x.Version).IsConcurrencyToken().HasComment("配置并发版本，保存及发送前核验");
            b.Property(x => x.ReportSchedulesJson).HasColumnType("text").HasDefaultValue("[]").HasComment("日周月运行报表计划JSON，默认全部关闭，启用起点由服务端维护");
            b.Property(x => x.UpdatedAt).HasComment("最近保存时间UTC");
        });
        model.Entity<MailMessage>(b =>
        {
            b.ToTable("OOB_MAIL_MESSAGE", t =>
            {
                t.HasComment("本系统发件意图与安全结果；SMTP受理不等于已送达");
                t.HasCheckConstraint("CK_OOB_MAIL_MESSAGE_Status", "\"Status\" IN ('Sending','Accepted','Failed','Unknown')");
                t.HasCheckConstraint("CK_OOB_MAIL_MESSAGE_Kind", "(\"Kind\" IN ('Test','Manual') AND \"CreatedByUserId\" IS NOT NULL) OR (\"Kind\" = 'System' AND \"CreatedByUserId\" IS NULL)");
            });
            b.HasKey(x => x.Id); b.Property(x => x.Id).ValueGeneratedNever().HasComment("统一发号主键");
            b.HasIndex(x => x.RequestId).IsUnique();
            b.HasIndex(x => new { x.CreatedAt, x.Id }).IsDescending(true, true);
            b.Property(x => x.RequestId).HasMaxLength(100).HasComment("调用方稳定请求标识，重复请求不得再次发送");
            b.Property(x => x.RequestHash).HasMaxLength(64).HasComment("调用人、种类、收件人和内容摘要，阻止同标识不同内容");
            b.Property(x => x.Kind).HasMaxLength(16).HasComment("Test测试、Manual手动、System可信内部发件");
            b.Property(x => x.FromAddress).HasMaxLength(320).HasComment("本次发送冻结的发件邮箱");
            b.Property(x => x.ToAddress).HasMaxLength(320).HasComment("唯一收件邮箱");
            b.Property(x => x.Subject).HasMaxLength(200).HasComment("本次邮件主题");
            b.Property(x => x.TextBody).HasColumnType("text").HasComment("本次纯文本正文，仅详情鉴权后读取");
            b.Property(x => x.HtmlBody).HasColumnType("text").HasComment("可信内部报表的HTML快照，普通邮件为空，仅详情鉴权后读取");
            b.Property(x => x.Status).HasMaxLength(16).HasComment("发送中、服务器受理、明确失败或结果未知；重启遗留发送中按未知展示");
            b.Property(x => x.ErrorCode).HasMaxLength(100).HasComment("固定安全错误编码，不保存服务器自由文本");
            b.Property(x => x.Message).HasMaxLength(500).HasComment("固定安全中文结果，不含凭据或协议原文");
            b.Property(x => x.CreatedByUserId).HasComment("明确操作的真实用户，可信系统发信为空");
            b.Property(x => x.CreatedAt).HasComment("发送意图创建时间UTC");
            b.Property(x => x.CompletedAt).HasComment("本地记录结果时间UTC，不代表远端送达时间");
            b.HasOne<OobUser>().WithMany().HasForeignKey(x => x.CreatedByUserId).OnDelete(DeleteBehavior.Restrict);
        });
    }
}
