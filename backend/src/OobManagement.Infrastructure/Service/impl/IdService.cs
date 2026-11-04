using OobManagement.Application.Service;
using Yitter.IdGenerator;

namespace OobManagement.Infrastructure.Service.impl;

/// <summary>复用已锁定发号组件；每个部署实例必须配置不同 WorkerId。</summary>
public sealed class IdService : IIdService
{
    public IdService(ushort workerId)
    {
        if (workerId > 63) throw new ArgumentOutOfRangeException(nameof(workerId));
        YitIdHelper.SetIdGenerator(new IdGeneratorOptions(workerId));
    }
    public long NextId() => YitIdHelper.NextId();
}
