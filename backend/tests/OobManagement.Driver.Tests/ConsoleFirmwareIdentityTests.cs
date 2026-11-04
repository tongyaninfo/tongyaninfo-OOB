using OobManagement.Application.Models;
using OobManagement.Infrastructure.Service.impl;
using Xunit;

namespace OobManagement.Driver.Tests;

/// <summary>库存保留固件原文，控制台不能因展示前缀把已审查的实际设备误判为不支持。</summary>
public sealed class ConsoleFirmwareIdentityTests
{
    [Theory]
    [InlineData("2.82", "Available")]
    [InlineData("iLO 4 v2.82", "Available")]
    [InlineData("ILO 4 V2.82", "Available")]
    [InlineData("iLO 4 v2.81", "Unknown")]
    [InlineData("iLO 5 v2.82", "Unknown")]
    [InlineData(null, "Unknown")]
    public async Task Normalizes_only_the_reviewed_ilo4_firmware_identity(string? firmware, string expected)
    {
        var target = new ConsoleTarget(1, "fixture", "iLO4", firmware, new OutOfBandConnection
        { Endpoint = new Uri("https://fixture.invalid"), Username = "fixture", Password = "fixture" });
        var capability = await new ConsoleIlo4Provider().GetCapabilitiesAsync(target, CancellationToken.None);
        Assert.Equal(expected, capability.Status);
    }
}
