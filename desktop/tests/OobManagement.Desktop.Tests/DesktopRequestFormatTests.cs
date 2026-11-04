using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using OobManagement.Application.Models;
using Xunit;

namespace OobManagement.Desktop.Tests;

/// <summary>生产环境 Minimal API 绑定拒绝也必须保持业务接口信封，不能只返回空状态码。</summary>
[Collection("DesktopSqlite")]
public sealed class DesktopRequestFormatTests
{
    [Fact]
    public async Task Malformed_initialization_json_returns_the_standard_envelope()
    {
        var directory = DesktopHostTests.NewDirectory();
        await using var host = await DesktopHostProcess.StartAsync(directory);
        using var client = host.CreateClient();
        var csrf = await client.GetFromJsonAsync<ApiResult<CsrfDto>>("/desktop/csrf", SqliteApplicationFactory.JsonOptions);
        using var request = new HttpRequestMessage(HttpMethod.Post, "/desktop/initialize")
        {
            Content = new StringContent("{", Encoding.UTF8, "application/json")
        };
        request.Headers.Add("X-CSRF-TOKEN", csrf!.Data!.Token);
        using var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var result = await response.Content.ReadFromJsonAsync<ApiResult<JsonElement>>(SqliteApplicationFactory.JsonOptions);
        Assert.NotNull(result);
        Assert.False(result.Success);
        Assert.False(File.Exists(Path.Combine(directory, "data", "oob.sqlite")));
    }
}
