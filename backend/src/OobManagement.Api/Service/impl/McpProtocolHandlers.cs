using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using OobManagement.Api.Common;
using OobManagement.Application.Models;
using OobManagement.Application.Service;

namespace OobManagement.Api.Service.impl;

/// <summary>工具目录和执行共用当前请求授权；协议由官方 SDK 序列化。</summary>
public static class McpProtocolHandlers
{
    private static McpRequestState State(IServiceProvider services) => services.GetRequiredService<IHttpContextAccessor>()
        .HttpContext?.Items[McpRequestState.Key] as McpRequestState ?? throw new McpException("请求尚未通过MCP授权");

    public static async ValueTask<ListToolsResult> List(RequestContext<ListToolsRequestParams> request, CancellationToken ct)
    {
        var state = State(request.Services!);
        var check = await request.Services!.GetRequiredService<IMcpPolicyService>().AuthorizeAsync(state.Access.Request, ct);
        if (!check.Success)
        {
            state.Result = "ScopeDenied"; state.ErrorCode = check.ErrorCode;
            throw new McpException("MCP授权已失效");
        }
        var tools = McpToolCatalog.All.Where(x => check.Data!.Tools.Contains(x.Name)).OrderBy(x => x.Name, StringComparer.Ordinal).Select(x => new Tool
        {
            Name = x.Name, Description = x.Description, InputSchema = x.InputSchema, OutputSchema = x.OutputSchema,
            Annotations = new ToolAnnotations { ReadOnlyHint = true, DestructiveHint = false, IdempotentHint = true, OpenWorldHint = false }
        }).ToList();
        if (Encoding.UTF8.GetByteCount(JsonSerializer.Serialize(tools)) > state.Configuration.MaxResponseBytes - 512)
            throw new McpException("工具目录超过响应限制，请管理员调整配置");
        return new() { Tools = tools };
    }

    public static async ValueTask<CallToolResult> Call(RequestContext<CallToolRequestParams> request, CancellationToken ct)
    {
        var state = State(request.Services!);
        var tool = request.Params?.Name ?? "";
        var args = JsonSerializer.SerializeToElement(request.Params?.Arguments ?? new Dictionary<string, JsonElement>());
        var result = await request.Services!.GetRequiredService<IMcpQueryService>().ExecuteAsync(state.Access, tool, args, ct);
        if (!result.Success)
        {
            state.Result = result.StatusCode is 401 or 403 or 404 ? (result.ErrorCode?.Contains("TOOL", StringComparison.Ordinal) == true ? "ToolDenied" : "ScopeDenied")
                : result.StatusCode == 400 ? "InvalidArguments" : "BusinessFailed";
            state.ErrorCode = result.ErrorCode;
            return new() { IsError = true, Content = [new TextContentBlock { Text = result.ErrorCode + ": " + result.ErrorMessage }] };
        }
        var output = result.Data;
        var raw = output.GetRawText();
        if (Encoding.UTF8.GetByteCount(raw) > state.Configuration.MaxResponseBytes - 2048)
        {
            state.Result = "BusinessFailed"; state.ErrorCode = "MCP_RESPONSE_TOO_LARGE";
            return new() { IsError = true, Content = [new TextContentBlock { Text = "MCP_RESPONSE_TOO_LARGE: 请缩小查询范围" }] };
        }
        state.OutputSummary = "查询完成，结构化数据字节数：" + Encoding.UTF8.GetByteCount(raw);
        return new() { IsError = false, StructuredContent = output, Content = [new TextContentBlock { Text = "查询完成，结果见结构化数据。" }] };
    }
}
