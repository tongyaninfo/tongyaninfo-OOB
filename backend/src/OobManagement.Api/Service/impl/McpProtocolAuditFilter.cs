using System.Text.Json;
using System.Text.Json.Nodes;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using OobManagement.Api.Common;

namespace OobManagement.Api.Service.impl;

/// <summary>JSON-RPC失败可通过HTTP200返回；审计必须读取SDK消息而非猜测HTTP状态。</summary>
public static class McpProtocolAuditFilter
{
    public static McpMessageFilter Outgoing => next => async (context, cancellation) =>
    {
        var http = context.Services?.GetService<IHttpContextAccessor>()?.HttpContext;
        if (http?.Items[McpRequestState.Key] is McpRequestState state) Observe(state, context.JsonRpcMessage);
        await next(context, cancellation);
    };

    public static void Observe(McpRequestState state, JsonRpcMessage message)
    {
        // 业务处理器给出的更明确拒绝保持原有分类，不被通用协议错误覆盖。
        if (state.Result != "Succeeded") return;
        if (message is JsonRpcError error)
        {
            state.Result = error.Error.Code == -32603 ? "SystemFailed" : "InvalidArguments";
            state.ErrorCode = error.Error.Code switch
            {
                -32700 => "MCP_PARSE_ERROR", -32600 => "MCP_INVALID_REQUEST", -32601 => "MCP_METHOD_NOT_FOUND",
                -32602 => "MCP_INVALID_ARGUMENTS", -32603 => "MCP_INTERNAL_ERROR", _ => "MCP_PROTOCOL_ERROR"
            };
        }
        else if (message is JsonRpcResponse response && response.Result is JsonObject result
            && result["isError"] is JsonValue failed && failed.TryGetValue<bool>(out var isError) && isError)
        {
            state.Result = "BusinessFailed";
            state.ErrorCode = "MCP_TOOL_FAILED";
        }
    }
}
