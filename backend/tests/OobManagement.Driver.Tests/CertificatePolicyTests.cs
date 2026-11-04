using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using OobManagement.Application.Models;
using OobManagement.Infrastructure.Service.impl.OutOfBand;
using Xunit;

namespace OobManagement.Driver.Tests;

/// <summary>真实回环 HTTPS 使用内存自签证书，不读设备凭据、不安装证书或修改系统信任。</summary>
public sealed class CertificatePolicyTests
{
    [Fact]
    public async Task Per_device_ignore_and_explicit_pin_keep_their_priority_without_global_bypass()
    {
        await using var fixture = await LocalHttpsFixture.StartAsync();
        var driver = new Ilo4Service();
        var connection = new OutOfBandConnection
        {
            Endpoint = fixture.Endpoint, Username = "fixture-user", Password = "fixture-secret",
            IgnoreCertificateErrors = true
        };
        Assert.True((await driver.TestConnectionAsync(connection)).Success);
        var strict = await driver.TestConnectionAsync(connection with { IgnoreCertificateErrors = false });
        Assert.False(strict.Success);
        Assert.Equal("TlsConnectionFailed", strict.ErrorCode);
        Assert.DoesNotContain("fixture-secret", strict.ErrorMessage ?? "");
        Assert.True((await driver.TestConnectionAsync(connection with
        {
            IgnoreCertificateErrors = false, CertificateFingerprint = fixture.Fingerprint
        })).Success);
        var wrongPin = await driver.TestConnectionAsync(connection with
        {
            IgnoreCertificateErrors = true, CertificateFingerprint = new string('0', 64)
        });
        Assert.False(wrongPin.Success);
        Assert.Equal("TlsConnectionFailed", wrongPin.ErrorCode);
        Assert.True((await driver.TestConnectionAsync(connection with
        {
            IgnoreCertificateErrors = true, CertificateFingerprint = fixture.Fingerprint
        })).Success);
        // 前面的忽略策略只属于其独立 Handler，后来新建的严格客户端仍不能信任该证书。
        using var ordinary = new HttpClient();
        await Assert.ThrowsAsync<HttpRequestException>(() => ordinary.GetAsync(fixture.Endpoint));
        Assert.Equal(0, fixture.NonGetRequests);
    }

    [Fact]
    public async Task New_connection_and_request_default_to_device_certificate_compatibility()
    {
        Assert.True(new SaveServerRequest().IgnoreCertificateErrors);
        Assert.True(new OutOfBandConnection
        {
            Endpoint = new Uri("https://fixture.invalid"), Username = "fixture", Password = "fixture"
        }.IgnoreCertificateErrors);
        await using var fixture = await LocalHttpsFixture.StartAsync(expired: true);
        Assert.True((await new Ilo4Service().TestConnectionAsync(new()
        {
            Endpoint = fixture.Endpoint, Username = "fixture", Password = "fixture"
        })).Success);
        Assert.Equal(0, fixture.NonGetRequests);
    }

    private sealed class LocalHttpsFixture(WebApplication app, X509Certificate2 certificate) : IAsyncDisposable
    {
        public Uri Endpoint { get; private set; } = null!;
        public string Fingerprint => certificate.GetCertHashString(HashAlgorithmName.SHA256);
        public int NonGetRequests;

        public static async Task<LocalHttpsFixture> StartAsync(bool expired = false)
        {
            using var key = RSA.Create(2048);
            var request = new CertificateRequest("CN=fixture.invalid", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
            var names = new SubjectAlternativeNameBuilder();
            names.AddDnsName("fixture.invalid");
            request.CertificateExtensions.Add(names.Build());
            request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, false));
            var now = DateTimeOffset.UtcNow;
            var certificate = request.CreateSelfSigned(now.AddDays(-2), expired ? now.AddDays(-1) : now.AddDays(1));
            var builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions
            {
                EnvironmentName = "Testing", ContentRootPath = AppContext.BaseDirectory,
                ApplicationName = typeof(CertificatePolicyTests).Assembly.FullName
            });
            builder.Logging.ClearProviders();
            builder.WebHost.ConfigureKestrel(options => options.Listen(IPAddress.Loopback, 0, listen => listen.UseHttps(certificate)));
            var app = builder.Build();
            var fixture = new LocalHttpsFixture(app, certificate);
            app.Use(async (context, next) =>
            {
                if (!HttpMethods.IsGet(context.Request.Method)) Interlocked.Increment(ref fixture.NonGetRequests);
                await next(context);
            });
            app.MapGet("/redfish/v1/", () => Results.Text("""{"Systems":{"@odata.id":"/redfish/v1/Systems"}}""", "application/json"));
            app.MapGet("/redfish/v1/Systems", () => Results.Text("""{"Members":[{"@odata.id":"/redfish/v1/Systems/fixture"}]}""", "application/json"));
            app.MapGet("/redfish/v1/Systems/fixture", () => Results.Text("""{"Id":"fixture","Manufacturer":"HPE","Model":"TLS fixture","PowerState":"On"}""", "application/json"));
            await app.StartAsync();
            var server = app.Services.GetRequiredService<IServer>();
            fixture.Endpoint = new Uri(server.Features.Get<IServerAddressesFeature>()!.Addresses.Single());
            return fixture;
        }

        public async ValueTask DisposeAsync()
        {
            await app.DisposeAsync();
            certificate.Dispose();
        }
    }
}
