namespace OobManagement.Application.Models;

/// <summary>标签失焦先撤销服务端输入，重新聚焦由用户界面显式恢复。</summary>
public sealed record ConsoleInputStateRequest(bool Enabled);
