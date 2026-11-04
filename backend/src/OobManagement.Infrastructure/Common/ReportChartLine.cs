namespace OobManagement.Infrastructure.Common;

/// <summary>无脚本报表曲线；空值表示真实缺口，不能连接前后两段。</summary>
public sealed record ReportChartLine(string Name, string Color, IReadOnlyList<double?> Values, bool Dashed = false);
