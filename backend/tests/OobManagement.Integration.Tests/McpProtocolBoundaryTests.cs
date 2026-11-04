using System.Net;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Http;
using ModelContextProtocol.Protocol;
using OobManagement.Api.Common;
using OobManagement.Api.Service.impl;
using OobManagement.Application.Models;
using OobManagement.Application.Service;
using Xunit;

namespace OobManagement.Integration.Tests;

/// <summary>协议边界纯内存验证，不加载业务宿主或外部依赖。</summary>
public sealed class McpProtocolBoundaryTests
{
    [Theory]
    [InlineData(-32601, "MCP_METHOD_NOT_FOUND", "InvalidArguments")]
    [InlineData(-32602, "MCP_INVALID_ARGUMENTS", "InvalidArguments")]
    [InlineData(-32603, "MCP_INTERNAL_ERROR", "SystemFailed")]
    public void JsonRpc_error_with_successful_HTTP_is_not_a_success_audit(int code, string expected, string result)
    {
        var state = State();
        McpProtocolAuditFilter.Observe(state, new JsonRpcError { Id = new RequestId("call-1"), Error = new() { Code = code, Message = "任何原始错误都不能抄入日志" } });
        Assert.Equal(result, state.Result);
        Assert.Equal(expected, state.ErrorCode);
    }

    [Fact]
    public void SDK_tool_error_is_recorded_and_precise_business_denial_is_preserved()
    {
        var state = State();
        McpProtocolAuditFilter.Observe(state, new JsonRpcResponse { Id = new RequestId("call-2"), Result = new JsonObject { ["isError"] = true } });
        Assert.Equal("BusinessFailed", state.Result);
        state.Result = "ToolDenied"; state.ErrorCode = "MCP_TOOL_DENIED";
        McpProtocolAuditFilter.Observe(state, new JsonRpcError { Id = new RequestId("call-2"), Error = new() { Code = -32603, Message = "error" } });
        Assert.Equal("ToolDenied", state.Result);
        Assert.Equal("MCP_TOOL_DENIED", state.ErrorCode);
    }

    [Fact]
    public void Revocation_cancels_only_selected_client_and_completed_requests_are_removed()
    {
        var registry = new McpActiveRequests();
        using var first = new CancellationTokenSource();
        using var second = new CancellationTokenSource();
        using var completed = new CancellationTokenSource();
        using var firstLease = registry.Register(1, first);
        using var secondLease = registry.Register(2, second);
        registry.Register(1, completed).Dispose();
        registry.CancelClient(1);
        Assert.True(first.IsCancellationRequested);
        Assert.False(second.IsCancellationRequested);
        Assert.False(completed.IsCancellationRequested);
        registry.CancelAll();
        Assert.True(second.IsCancellationRequested);
    }

    [Fact]
    public async Task Anonymous_limit_is_audited_once_without_creating_unbounded_rejection_logs()
    {
        var limiter = new McpAdmissionLimiter();
        var audit = new RecordingAudit();
        var middleware = new McpGuardMiddleware(_ => throw new InvalidOperationException("限流后不得进入SDK或业务"));
        // 先用与守卫一致的限额耗尽计数，不等待时间窗口或访问任何存储。
        for (var index = 0; index < 120; index++) limiter.TryAcquire("ip:127.0.0.1", 120, 16, 128, false)?.Dispose();
        for (var index = 0; index < 20; index++)
        {
            var http = new DefaultHttpContext();
            http.Request.Path = "/mcp"; http.Request.Method = "POST";
            http.Connection.RemoteIpAddress = IPAddress.Loopback;
            http.Response.Body = new MemoryStream();
            await middleware.InvokeAsync(http, null!, audit, new(), limiter, new McpActiveRequests());
            Assert.Equal(429, http.Response.StatusCode);
        }
        Assert.Single(audit.Started);
        Assert.Single(audit.Completed);
        Assert.Equal("transport/rate-limit", audit.Started[0].Method);
        Assert.Equal("RateLimited", audit.Completed[0].Result);
    }

    private static McpRequestState State() => new() { Access = null!, Configuration = null! };
    private sealed class RecordingAudit : IMcpAuditService
    {
        public List<McpCallStart> Started { get; } = [];
        public List<McpCallCompletion> Completed { get; } = [];
        public Task<ServiceResult<string>> StartAsync(McpCallStart request, CancellationToken ct) { Started.Add(request); return Task.FromResult(ServiceResult<string>.Ok("1")); }
        public Task CompleteAsync(string callId, McpCallCompletion result, CancellationToken ct) { Completed.Add(result); return Task.CompletedTask; }
        public Task ReplayAsync(CancellationToken ct) => Task.CompletedTask;
    }
}
