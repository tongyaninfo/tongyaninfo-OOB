using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.DependencyInjection;
using OobManagement.Application.Models;
using OobManagement.Application.Service;
using Xunit;

namespace OobManagement.Desktop.Tests;

/// <summary>真实OpenIddict安全边界；仅使用新建SQLite与合成证书，不读取或轮换现有秘密。</summary>
[Collection("DesktopSqlite")]
public sealed class McpOAuthSecurityTests
{
    private static readonly CancellationToken Ct=CancellationToken.None;
    [Fact]
    public async Task Confidential_client_requires_explicit_secret_before_enable_and_authenticates_after_rotation()
    {
        await using var f=await McpProtocolTests.Create();await McpProtocolTests.Register(f);
        var management=f.Services.GetRequiredService<IMcpManagementService>();var actor=new UserSessionContext(f.Administrator.Id,f.Administrator.AuthVersion);
        var created=await management.SaveClientAsync(actor,null,new(){Name="Confidential fixture",ClientType="confidential",UserId=f.Administrator.Id.ToString(),
            ServerScope="AllServers",Tools=["oob_list_servers"],Networks=["127.0.0.1/32"],RedirectUris=["http://127.0.0.1:45239/callback"]},Ct);
        Assert.True(created.Success,created.ErrorCode);Assert.False(created.Data!.Enabled);
        var id=long.Parse(created.Data.Id);
        var noSecret=await management.SetClientEnabledAsync(actor,id,true,new(created.Data.Version),Ct);
        Assert.Equal("MCP_SECRET_REQUIRED",noSecret.ErrorCode);
        Assert.False((await management.GetClientAsync(actor,id,Ct)).Data!.Enabled);
        var rotated=await management.RotateSecretAsync(actor,id,new(created.Data.Version),Ct);
        Assert.True(rotated.Success,rotated.ErrorCode);Assert.NotEmpty(rotated.Data!.Secret);
        var current=(await management.GetClientAsync(actor,id,Ct)).Data!;
        Assert.False(current.Enabled);
        var enabled=await management.SetClientEnabledAsync(actor,id,true,new(current.Version),Ct);Assert.True(enabled.Success,enabled.ErrorCode);
        var code=await Code(f,enabled.Data!);
        using var issued=await Exchange(f,enabled.Data!,code.Code,code.Verifier,rotated.Data.Secret);
        Assert.Equal(HttpStatusCode.OK,issued.StatusCode);
        using var tokenJson=JsonDocument.Parse(await issued.Content.ReadAsStringAsync());var token=tokenJson.RootElement.GetProperty("access_token").GetString()!;
        using var request=McpProtocolTests.Protocol("tools/list",token);using var call=await f.Client.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK,call.StatusCode);
        var nextCode=await Code(f,enabled.Data!);using var invalid=await Exchange(f,enabled.Data!,nextCode.Code,nextCode.Verifier,null);
        Assert.Equal(HttpStatusCode.Unauthorized,invalid.StatusCode);
        Assert.DoesNotContain(rotated.Data.Secret,await invalid.Content.ReadAsStringAsync());
    }
    [Fact]
    public async Task Consent_post_without_CSRF_and_wrong_PKCE_are_rejected_without_granting_access()
    {
        await using var f=await McpProtocolTests.Create();var client=await McpProtocolTests.Register(f);
        var verifier=WebEncoders.Base64UrlEncode(RandomNumberGenerator.GetBytes(32));
        var fields=await Consent(f,client,verifier);
        var csrf=fields.Keys.Single(x=>x.Contains("RequestVerificationToken",StringComparison.Ordinal));
        fields.Remove(csrf);fields["decision"]="allow";
        using var noCsrf=await f.Client.PostAsync("/connect/authorize",new FormUrlEncodedContent(fields));
        Assert.Equal(HttpStatusCode.BadRequest,noCsrf.StatusCode);
        var issuedCode=await Code(f,client);
        using var wrong=await Exchange(f,client,issuedCode.Code,WebEncoders.Base64UrlEncode(RandomNumberGenerator.GetBytes(32)),null);
        Assert.Equal(HttpStatusCode.BadRequest,wrong.StatusCode);
        using var json=JsonDocument.Parse(await wrong.Content.ReadAsStringAsync());Assert.Equal("invalid_grant",json.RootElement.GetProperty("error").GetString());
    }
    [Fact]
    public async Task Changing_public_client_to_confidential_disables_it_until_an_explicit_secret_is_created()
    {
        await using var f=await McpProtocolTests.Create();var client=await McpProtocolTests.Register(f);
        var management=f.Services.GetRequiredService<IMcpManagementService>();var actor=new UserSessionContext(f.Administrator.Id,f.Administrator.AuthVersion);
        var changed=await management.SaveClientAsync(actor,long.Parse(client.Id),new(){ExpectedVersion=client.Version,Name=client.Name,ClientType="confidential",
            UserId=client.UserId,ServerScope=client.ServerScope,Tools=client.Tools,ServerIds=client.ServerIds,Networks=client.Networks,
            Origins=client.Origins,RedirectUris=client.RedirectUris},Ct);
        Assert.True(changed.Success,changed.ErrorCode);Assert.False(changed.Data!.Enabled);Assert.Equal("confidential",changed.Data.ClientType);
        var noSecret=await management.SetClientEnabledAsync(actor,long.Parse(client.Id),true,new(changed.Data.Version),Ct);
        Assert.Equal("MCP_SECRET_REQUIRED",noSecret.ErrorCode);
    }
    [Fact]
    public async Task Unregistered_resource_is_rejected_and_rotated_refresh_tokens_cannot_be_replayed()
    {
        await using var f=await McpProtocolTests.Create();var client=await McpProtocolTests.Register(f);
        var challenge=WebEncoders.Base64UrlEncode(SHA256.HashData(RandomNumberGenerator.GetBytes(32)));
        var uri=QueryHelpers.AddQueryString("/connect/authorize",new Dictionary<string,string?>{["response_type"]="code",["client_id"]=client.OAuthClientId,
            ["redirect_uri"]=client.RedirectUris[0],["scope"]="mcp.read",["resource"]="https://foreign.invalid/mcp",["code_challenge"]=challenge,["code_challenge_method"]="S256"});
        using var foreign=await f.Client.GetAsync(uri);Assert.Equal(HttpStatusCode.BadRequest,foreign.StatusCode);
        var tokens=await McpProtocolTests.Token(f,client);
        async Task<HttpResponseMessage> Refresh(string value)=>await f.Client.PostAsync("/connect/token",new FormUrlEncodedContent(new Dictionary<string,string>{
            ["grant_type"]="refresh_token",["refresh_token"]=value,["client_id"]=client.OAuthClientId,["resource"]="https://localhost/mcp"}));
        using var first=await Refresh(tokens.RefreshToken!);Assert.Equal(HttpStatusCode.OK,first.StatusCode);
        using var replay=await Refresh(tokens.RefreshToken!);Assert.Equal(HttpStatusCode.BadRequest,replay.StatusCode);
        using var replayJson=JsonDocument.Parse(await replay.Content.ReadAsStringAsync());Assert.Equal("invalid_grant",replayJson.RootElement.GetProperty("error").GetString());
    }
    [Fact]
    public async Task Standard_revocation_endpoint_invalidates_previously_issued_access_token()
    {
        await using var f=await McpProtocolTests.Create();var client=await McpProtocolTests.Register(f);var token=await McpProtocolTests.Token(f,client);
        using var revoked=await f.Client.PostAsync("/connect/revoke",new FormUrlEncodedContent(new Dictionary<string,string>{
            ["client_id"]=client.OAuthClientId,["token"]=token.AccessToken,["token_type_hint"]="access_token"}));
        Assert.Equal(HttpStatusCode.OK,revoked.StatusCode);
        using var request=McpProtocolTests.Protocol("tools/list",token.AccessToken);using var call=await f.Client.SendAsync(request);
        Assert.Equal(HttpStatusCode.Unauthorized,call.StatusCode);
    }
    private static async Task<Dictionary<string,string>> Consent(SqliteApplicationFactory f,McpClientDto client,string verifier)
    {
        var uri=QueryHelpers.AddQueryString("/connect/authorize",new Dictionary<string,string?>{["response_type"]="code",["client_id"]=client.OAuthClientId,
            ["redirect_uri"]=client.RedirectUris[0],["scope"]="mcp.read offline_access",["resource"]="https://localhost/mcp",["state"]="security-fixture",
            ["code_challenge"]=WebEncoders.Base64UrlEncode(SHA256.HashData(Encoding.ASCII.GetBytes(verifier))),["code_challenge_method"]="S256"});
        using var response=await f.Client.GetAsync(uri);Assert.Equal(HttpStatusCode.OK,response.StatusCode);
        var html=await response.Content.ReadAsStringAsync();
        return Regex.Matches(html,"<input type=\"hidden\" name=\"([^\"]+)\" value=\"([^\"]*)\">")
            .ToDictionary(m=>WebUtility.HtmlDecode(m.Groups[1].Value),m=>WebUtility.HtmlDecode(m.Groups[2].Value));
    }
    private static async Task<(string Code,string Verifier)> Code(SqliteApplicationFactory f,McpClientDto client)
    {
        var verifier=WebEncoders.Base64UrlEncode(RandomNumberGenerator.GetBytes(32));var fields=await Consent(f,client,verifier);fields["decision"]="allow";
        using var response=await f.Client.PostAsync("/connect/authorize",new FormUrlEncodedContent(fields));Assert.Equal(HttpStatusCode.Redirect,response.StatusCode);
        return(QueryHelpers.ParseQuery(response.Headers.Location!.Query)["code"].ToString(),verifier);
    }
    private static Task<HttpResponseMessage> Exchange(SqliteApplicationFactory f,McpClientDto client,string code,string verifier,string? secret)
    {
        var fields=new Dictionary<string,string>{["grant_type"]="authorization_code",["client_id"]=client.OAuthClientId,["code"]=code,
            ["code_verifier"]=verifier,["redirect_uri"]=client.RedirectUris[0],["resource"]="https://localhost/mcp"};
        if(secret is not null)fields["client_secret"]=secret;
        return f.Client.PostAsync("/connect/token",new FormUrlEncodedContent(fields));
    }
}
