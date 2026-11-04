namespace OobManagement.Infrastructure.Common;

/// <summary>脱敏缓冲放应用数据目录，独立于源码、数据库和凭据；不提供自动删除或保留策略。</summary>
public sealed record ExceptionLogOptions
{
    public string SpoolDirectory { get; init; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "OutOfBandManagement", "exception-spool");
    public TimeSpan DatabaseTimeout { get; init; } = TimeSpan.FromSeconds(2);
}
