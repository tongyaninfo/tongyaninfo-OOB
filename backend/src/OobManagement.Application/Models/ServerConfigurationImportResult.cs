namespace OobManagement.Application.Models;

/// <summary>仅返回配置处理数量；跳过的记录没有数据库更新或推送副作用。</summary>
public sealed record ServerConfigurationImportResult(int Added, int Updated, int Skipped);
