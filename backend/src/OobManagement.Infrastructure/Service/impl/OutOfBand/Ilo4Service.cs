using OobManagement.Application.Models;
using OobManagement.Application.Service;

namespace OobManagement.Infrastructure.Service.impl.OutOfBand;

/// <summary>iLO4 的 Redfish 与 Hp OEM 资源实现，资源能力以实际固件声明为准。</summary>
public sealed class Ilo4Service(Func<OutOfBandConnection, HttpClient>? clientFactory = null, IExceptionRecorder? exceptions = null) : RedfishDriverBase(clientFactory, exceptions)
{
    public override string ControllerType => "iLO4";
}
