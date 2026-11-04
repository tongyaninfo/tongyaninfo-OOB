using Microsoft.EntityFrameworkCore;
using OobManagement.Infrastructure.Persistence;
using OobManagement.Infrastructure.Service.impl;
using Xunit;

namespace OobManagement.Driver.Tests;

/// <summary>输入或发布资源错误须明确失败，在此之前不创建数据库访问或任何发送意图。</summary>
public sealed class OperationReportBuilderTests
{
    private static readonly DateTimeOffset Start = new(2026, 11, 1, 0, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(367)]
    public async Task Invalid_period_is_rejected_before_resource_or_database_access(int days)
    {
        var builder = new OperationReportBuilder(new UnusedFactory());
        var result = await builder.BuildAsync(Start, Start.AddDays(days), "运行日报", CancellationToken.None);
        Assert.Equal("REPORT_INVALID_RANGE", result.ErrorCode);
        Assert.Equal(400, result.StatusCode);
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("日报\r\nBcc:other@example.com")]
    public async Task Invalid_subject_is_rejected_before_database_access(string title)
    {
        var result = await new OperationReportBuilder(new UnusedFactory())
            .BuildAsync(Start, Start.AddDays(1), title, CancellationToken.None);
        Assert.Equal("REPORT_INVALID_TITLE", result.ErrorCode);
    }

    [Fact]
    public async Task Missing_published_template_is_unavailable_instead_of_an_empty_report()
    {
        // 驱动工程不引用API发布资源，模拟漏打包Resources的实际运行目录。
        Assert.False(File.Exists(Path.Combine(AppContext.BaseDirectory, "Resources", "Mail", "OperationReport.html")));
        var result = await new OperationReportBuilder(new UnusedFactory())
            .BuildAsync(Start, Start.AddDays(1), "运行日报", CancellationToken.None);
        Assert.Equal("REPORT_TEMPLATE_UNAVAILABLE", result.ErrorCode);
        Assert.Equal(503, result.StatusCode);
        Assert.Null(result.Data);
    }

    private sealed class UnusedFactory : IDbContextFactory<OobDbContext>
    {
        public OobDbContext CreateDbContext() => throw new InvalidOperationException("此失败场景不应访问数据库");
    }
}
