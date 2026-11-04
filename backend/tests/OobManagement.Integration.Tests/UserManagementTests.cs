using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using OobManagement.Application.Models;
using OobManagement.Domain.Models;
using Xunit;

namespace OobManagement.Integration.Tests;

/// <summary>真实数据库及HTTP验证账号唯一、独立权限和密码会话；只创建本次夹具账号。</summary>
[Collection("PostgresIntegration")]
public sealed class UserManagementTests(OobApplicationFactory factory) : IClassFixture<OobApplicationFactory>
{
    private const string NewPassword = "Changed-Integration-Only-2026";

    [Fact]
    public async Task Read_only_server_detail_does_not_disclose_administrator_power_audit()
    {
        factory.Device.Reset();
        var server = await factory.CreateServerAsync();
        using var power = await factory.SendAsync(HttpMethod.Post, $"/api/oob/servers/{server.Id}/power",
            new PowerRequest(PowerAction.On, server.Name, Guid.NewGuid().ToString("N")));
        await Success<PowerOperationDto>(power);
        using var adminDetail = await factory.Client.GetAsync($"/api/oob/servers/{server.Id}");
        Assert.NotEmpty((await Success<ServerDetailDto>(adminDetail)).PowerOperations);
        using var viewer = Client(await factory.SeedUserAsync());
        using var detail = await viewer.GetAsync($"/api/oob/servers/{server.Id}");
        Assert.Empty((await Success<ServerDetailDto>(detail)).PowerOperations);
        using var audit = await viewer.GetAsync($"/api/oob/servers/{server.Id}/power-operations");
        Assert.Equal(HttpStatusCode.Forbidden, audit.StatusCode);
        factory.Device.Reset();
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task Permissions_are_independent_and_never_grant_device_administration(bool users, bool console)
    {
        var user = await factory.SeedUserAsync(canManageUsers: users, canUseConsole: console);
        using var client = Client(user);
        using var current = await client.GetAsync("/api/auth/current-user");
        var identity = await Success<CurrentUserDto>(current);
        Assert.Equal(users, identity.CanManageUsers);
        Assert.Equal(console, identity.CanUseConsole);
        Assert.False(identity.IsAdministrator);
        Assert.Equal("user", identity.Access);
        using var dashboard = await client.GetAsync("/api/oob/dashboard");
        Assert.Equal(HttpStatusCode.OK, dashboard.StatusCode);
        using var list = await client.GetAsync("/api/users");
        Assert.Equal(users ? HttpStatusCode.OK : HttpStatusCode.Forbidden, list.StatusCode);
        using var control = await client.GetAsync("/api/oob/console-sessions/nonexistent");
        Assert.Equal(console ? HttpStatusCode.NotFound : HttpStatusCode.Forbidden, control.StatusCode);
        using var devices = await factory.SendAsync(HttpMethod.Post, "/api/oob/servers", factory.ServerRequest(), client: client);
        Assert.Equal(HttpStatusCode.Forbidden, devices.StatusCode);
        using var logs = await client.GetAsync("/api/oob/exceptions");
        Assert.Equal(HttpStatusCode.Forbidden, logs.StatusCode);
    }

    [Fact]
    public async Task Disabled_account_reserves_normalized_name_until_logical_deletion_and_reuse_has_new_identity()
    {
        var request = Request() with { Enabled = false };
        var first = await Create(request);
        using var duplicate = await factory.SendAsync(HttpMethod.Post, "/api/users", request with { Username = "  " + request.Username.ToUpperInvariant() + "  " });
        await Failure(duplicate, HttpStatusCode.Conflict, "ACCOUNT_EXISTS");
        using var removed = await factory.SendAsync(HttpMethod.Post, $"/api/users/{first.Id}/delete", new UserVersionRequest(first.Version));
        var deleted = await Success<UserDto>(removed);
        Assert.NotNull(deleted.DeletedAt);
        var replacement = await Create(request with { Enabled = true });
        Assert.NotEqual(first.Id, replacement.Id);
        await using var db = await factory.DatabaseAsync();
        Assert.NotNull((await db.Users.SingleAsync(x => x.Id == Id(first))).DeletedAt);
        Assert.False(await db.Preferences.AnyAsync(x => x.UserId == Id(replacement)));
        using var list = await factory.Client.GetAsync("/api/users?status=deleted&keyword=" + Uri.EscapeDataString(request.Username));
        Assert.Contains((await Success<PageData<UserDto>>(list)).Items, x => x.Id == first.Id);
    }

    [Fact]
    public async Task Concurrent_duplicate_creation_has_one_winner_and_stale_edit_cannot_overwrite()
    {
        var request = Request();
        var results = await Task.WhenAll(Enumerable.Range(0, 2).Select(_ => factory.SendAsync(HttpMethod.Post, "/api/users", request)));
        try
        {
            Assert.Single(results, x => x.StatusCode == HttpStatusCode.OK);
            Assert.Single(results, x => x.StatusCode == HttpStatusCode.Conflict);
            var user = await Success<UserDto>(results.Single(x => x.IsSuccessStatusCode));
            factory.TrackUser(Id(user));
            var edit = Edit(user) with { DisplayName = "更新后的姓名" };
            using var saved = await factory.SendAsync(HttpMethod.Put, $"/api/users/{user.Id}", edit);
            var after = await Success<UserDto>(saved);
            Assert.Equal(user.Version + 1, after.Version);
            using var stale = await factory.SendAsync(HttpMethod.Put, $"/api/users/{user.Id}", edit with { DisplayName = "过期覆盖" });
            await Failure(stale, HttpStatusCode.Conflict, "USER_VERSION_CONFLICT");
            await using var db = await factory.DatabaseAsync();
            Assert.Equal("更新后的姓名", (await db.Users.SingleAsync(x => x.Id == Id(user))).DisplayName);
        }
        finally { foreach (var result in results) result.Dispose(); }
    }

    [Fact]
    public async Task Ordinary_user_manager_cannot_edit_delete_or_reset_system_administrator()
    {
        var manager = await factory.SeedUserAsync(canManageUsers: true);
        using var client = Client(manager);
        var csrf = await Csrf(client);
        var admin = factory.PrimaryUser;
        var requests = new (HttpMethod Method, string Path, object Value)[]
        {
            (HttpMethod.Put, $"/api/users/{admin.Id}", new SaveUserRequest { Username = admin.Username, DisplayName = "越权改名", Enabled = true, CanManageUsers = true, CanUseConsole = true, Version = admin.Version }),
            (HttpMethod.Post, $"/api/users/{admin.Id}/delete", new UserVersionRequest(admin.Version)),
            (HttpMethod.Post, $"/api/users/{admin.Id}/reset-password", new ResetUserPasswordRequest(admin.Version, NewPassword, NewPassword))
        };
        foreach (var request in requests)
        {
            using var response = await Send(client, csrf, request.Method, request.Path, request.Value);
            await Failure(response, HttpStatusCode.Forbidden, "FORBIDDEN");
        }
    }

    [Theory]
    [InlineData("permission")]
    [InlineData("servers")]
    [InlineData("power")]
    [InlineData("disable")]
    [InlineData("delete")]
    [InlineData("account")]
    [InlineData("password")]
    public async Task Security_changes_reject_existing_cookie_and_keep_password_hash_private(string change)
    {
        var user = await Create(Request() with { CanUseConsole = true, CanManageServers = true, CanOperatePower = true });
        await using var db = await factory.DatabaseAsync();
        var stored = await db.Users.AsNoTracking().SingleAsync(x => x.Id == Id(user));
        using var old = Client(stored);
        using var before = await old.GetAsync("/api/auth/current-user");
        Assert.Equal(HttpStatusCode.OK, before.StatusCode);
        using var changed = change switch
        {
            "delete" => await factory.SendAsync(HttpMethod.Post, $"/api/users/{user.Id}/delete", new UserVersionRequest(user.Version)),
            "password" => await factory.SendAsync(HttpMethod.Post, $"/api/users/{user.Id}/reset-password", new ResetUserPasswordRequest(user.Version, NewPassword, NewPassword)),
            _ => await factory.SendAsync(HttpMethod.Put, $"/api/users/{user.Id}", Edit(user) with
            {
                CanUseConsole = change != "permission", Enabled = change != "disable",
                CanManageServers = change != "servers", CanOperatePower = change != "power",
                Username = change == "account" ? user.Username + "-renamed" : user.Username
            })
        };
        await Success<UserDto>(changed);
        var content = await changed.Content.ReadAsStringAsync();
        Assert.DoesNotContain("passwordHash", content, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(NewPassword, content);
        using var stale = await old.GetAsync("/api/oob/dashboard");
        await Failure(stale, HttpStatusCode.Unauthorized, "UNAUTHORIZED");
        if (change == "password")
        {
            stored = await db.Users.AsNoTracking().SingleAsync(x => x.Id == Id(user));
            Assert.NotEqual(PasswordVerificationResult.Failed, new PasswordHasher<OobUser>().VerifyHashedPassword(stored, stored.PasswordHash, NewPassword));
        }
    }

    [Fact]
    public async Task Self_change_requires_old_password_and_confirmation_then_ends_all_old_sessions()
    {
        var user = await factory.SeedUserAsync();
        using var first = Client(user);
        using var second = Client(user);
        var csrf = await Csrf(first);
        using var wrong = await Send(first, csrf, HttpMethod.Post, "/api/auth/change-password", new ChangePasswordRequest("incorrect-password", NewPassword, NewPassword));
        Assert.Equal(HttpStatusCode.BadRequest, wrong.StatusCode);
        using var mismatch = await Send(first, csrf, HttpMethod.Post, "/api/auth/change-password", new ChangePasswordRequest(factory.Password, NewPassword, NewPassword + "x"));
        await Failure(mismatch, HttpStatusCode.BadRequest, "PASSWORD_CONFIRMATION_MISMATCH");
        using var changed = await Send(first, csrf, HttpMethod.Post, "/api/auth/change-password", new ChangePasswordRequest(factory.Password, NewPassword, NewPassword));
        Assert.True(await Success<bool>(changed));
        using var stale = await second.GetAsync("/api/auth/current-user");
        Assert.Equal(HttpStatusCode.Unauthorized, stale.StatusCode);
        using var loginClient = factory.CreateClient(new() { BaseAddress = new("https://localhost"), HandleCookies = true });
        using var login = await Send(loginClient, await Csrf(loginClient), HttpMethod.Post, "/api/auth/login", new LoginRequest(" " + user.Username.ToUpperInvariant() + " ", NewPassword));
        Assert.Equal(user.Id.ToString(CultureInfo.InvariantCulture), (await Success<CurrentUserDto>(login)).Userid);
    }

    [Fact]
    public async Task User_mutations_require_csrf_and_reject_unsupported_sorting()
    {
        using var csrf = await factory.SendAsync(HttpMethod.Post, "/api/users", Request(), csrf: false);
        Assert.Equal(HttpStatusCode.BadRequest, csrf.StatusCode);
        using var sort = await factory.Client.GetAsync("/api/users?sortField=passwordHash");
        await Failure(sort, HttpStatusCode.BadRequest, "INVALID_SORT");
    }

    private SaveUserRequest Request() => new() { Username = factory.RunPrefix + "-account-" + Guid.NewGuid().ToString("N")[..8], DisplayName = "测试用户", Password = factory.Password };
    private static SaveUserRequest Edit(UserDto user) => new() { Username = user.Username, DisplayName = user.DisplayName, Enabled = user.Enabled,
        CanManageUsers = user.CanManageUsers, CanUseConsole = user.CanUseConsole,
        CanManageServers = user.CanManageServers, CanOperatePower = user.CanOperatePower, Version = user.Version };
    private static long Id(UserDto user) => long.Parse(user.Id, CultureInfo.InvariantCulture);
    private async Task<UserDto> Create(SaveUserRequest request)
    {
        using var response = await factory.SendAsync(HttpMethod.Post, "/api/users", request);
        var user = await Success<UserDto>(response);
        factory.TrackUser(Id(user));
        return user;
    }
    private HttpClient Client(OobUser user)
    {
        var client = factory.CreateClient(new() { BaseAddress = new("https://localhost"), HandleCookies = true, AllowAutoRedirect = false });
        client.DefaultRequestHeaders.Add("Cookie", factory.CookieFor(user));
        return client;
    }
    private static async Task<string> Csrf(HttpClient client) => (await client.GetFromJsonAsync<ApiResult<CsrfDto>>("/api/auth/csrf", OobApplicationFactory.JsonOptions))!.Data!.Token;
    private static async Task<HttpResponseMessage> Send(HttpClient client, string csrf, HttpMethod method, string route, object value)
    {
        using var request = new HttpRequestMessage(method, route) { Content = JsonContent.Create(value, options: OobApplicationFactory.JsonOptions) };
        request.Headers.Add("X-CSRF-TOKEN", csrf);
        return await client.SendAsync(request);
    }
    private static async Task<T> Success<T>(HttpResponseMessage response)
    {
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<ApiResult<T>>(OobApplicationFactory.JsonOptions);
        Assert.True(body!.Success, body.ErrorCode);
        return body.Data!;
    }
    private static async Task Failure(HttpResponseMessage response, HttpStatusCode status, string code)
    {
        Assert.Equal(status, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<ApiResult<object>>(OobApplicationFactory.JsonOptions);
        Assert.Equal(code, body!.ErrorCode);
    }
}
