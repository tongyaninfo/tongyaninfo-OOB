namespace OobManagement.Application.Service;

/// <summary>统一发号入口，未来集中 IdServer 可替换实现而不改实体与接口。</summary>
public interface IIdService { long NextId(); }
