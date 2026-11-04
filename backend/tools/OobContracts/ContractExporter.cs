using System.Diagnostics;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ApiExplorer;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.AspNetCore.Mvc.Infrastructure;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using OobManagement.Api.Common;
using OobManagement.Api.Controllers;
using OobManagement.Application.Models;

namespace OobContracts;

/// <summary>正式 Controller、端点元数据和序列化模型的独立离线导出。</summary>
internal static class ContractExporter
{
    public static async Task ExportAsync(string outputDirectory)
    {
        var services = new ServiceCollection();
        var environment = new ExportEnvironment();
        services.AddSingleton<IWebHostEnvironment>(environment);
        services.AddSingleton<IHostEnvironment>(environment);
        services.AddSingleton(new DiagnosticListener("OobContracts"));
        services.AddSingleton<DiagnosticSource>(sp => sp.GetRequiredService<DiagnosticListener>());
        services.AddLogging();
        services.AddControllers().AddApplicationPart(typeof(AuthController).Assembly);
        using var provider = services.BuildServiceProvider();
        var routeBuilder = new ExportRouteBuilder(provider);
        routeBuilder.MapControllers();
        var endpoints = routeBuilder.DataSources.SelectMany(source => source.Endpoints).OfType<RouteEndpoint>().ToArray();
        var descriptions = provider.GetRequiredService<IApiDescriptionGroupCollectionProvider>()
            .ApiDescriptionGroups.Items.SelectMany(group => group.Items).ToArray();
        var actions = provider.GetRequiredService<IActionDescriptorCollectionProvider>()
            .ActionDescriptors.Items.OfType<ControllerActionDescriptor>().ToArray();
        var schemaExporter = new ContractSchemas();
        var routes = endpoints.SelectMany(endpoint =>
        {
            var action = endpoint.Metadata.GetMetadata<ControllerActionDescriptor>()
                ?? throw new InvalidOperationException("控制器端点缺少描述");
            var methods = endpoint.Metadata.GetMetadata<HttpMethodMetadata>()?.HttpMethods
                ?? throw new InvalidOperationException("公开端点必须明确 HTTP 方法：" + endpoint.RoutePattern.RawText);
            return methods.Select(method => Describe(endpoint, action, descriptions.FirstOrDefault(api =>
                api.ActionDescriptor.Id == action.Id && api.HttpMethod == method), method, schemaExporter));
        }).OrderBy(route => (string?)route["path"], StringComparer.Ordinal).ThenBy(route => (string?)route["method"], StringComparer.Ordinal).ToArray();
        if (actions.Any(action => !endpoints.Any(endpoint => endpoint.Metadata.GetMetadata<ControllerActionDescriptor>()?.Id == action.Id)))
            throw new InvalidOperationException("存在未纳入导出的控制器操作");

        // SignalR 推送类型不是 MVC 返回值，但与 PC 共享同一正式 DTO。
        schemaExporter.Add(typeof(ServerTelemetryDto));
        schemaExporter.Add(typeof(ApiResult<JsonElement>));
        var protocols = new JsonArray
        {
            Protocol("GET", OobProtocolRoutes.Health, "health", "application/json", "进程健康，不代表数据库或设备就绪"),
            Protocol("GET", OobProtocolRoutes.Telemetry, "signalr-websocket", "application/octet-stream", "Cookie授权，仅WebSocket；TelemetryUpdated(ServerTelemetryDto)，UTC与长ID字符串"),
            Protocol("POST", OobProtocolRoutes.Mcp, "mcp", "application/json, text/event-stream", "独立Bearer认证；协议响应不使用ApiResult"),
            Protocol("GET", OobProtocolRoutes.ProtectedResourceMetadata, "oauth-metadata", "application/json", "公开受保护资源发现"),
            Protocol("GET,POST", OobProtocolRoutes.Authorization, "oauth-authorization", "text/html, application/json", "OAuth授权码与PKCE；绑定用户及客户端白名单"),
            Protocol("POST", OobProtocolRoutes.Token, "oauth-token", "application/json", "OAuth标准响应，不套ApiResult"),
            Protocol("POST", OobProtocolRoutes.Revocation, "oauth-revocation", "application/json", "OAuth标准撤销响应"),
            Protocol("GET", "/.well-known/openid-configuration", "oauth-metadata", "application/json", "OpenIddict标准发现"),
            Protocol("GET", "/.well-known/oauth-authorization-server", "oauth-metadata", "application/json", "OAuth授权服务器标准发现")
        };
        var manifest = new JsonObject
        {
            ["format"] = "oob-rest-contracts.v1",
            ["source"] = "MVC ApiExplorer + EndpointDataSource；非MVC协议由正式路由常量登记",
            ["routes"] = new JsonArray(routes.Cast<JsonNode?>().ToArray()),
            ["protocols"] = protocols
        };
        RelocateSchemaReferences(manifest);
        Directory.CreateDirectory(outputDirectory);
        await File.WriteAllTextAsync(Path.Combine(outputDirectory, "routes.json"), manifest.ToJsonString(ContractSchemas.PrettyJson) + "\n");
        await File.WriteAllTextAsync(Path.Combine(outputDirectory, "schemas.json"), schemaExporter.Export().ToJsonString(ContractSchemas.PrettyJson) + "\n");
        Console.WriteLine($"已离线导出 {routes.Length} 个MVC方法和 {schemaExporter.Count} 个命名模型；未运行应用宿主、监听、配置或业务服务。");
    }

    private static JsonObject Describe(RouteEndpoint endpoint, ControllerActionDescriptor action, ApiDescription? api,
        string method, ContractSchemas schemas)
    {
        var authorization = endpoint.Metadata.GetOrderedMetadata<IAuthorizeData>();
        // ApiExplorer会展开[FromQuery]对象；仍保留其正式类型供PC筛选器引用。
        foreach (var parameter in action.MethodInfo.GetParameters().Where(parameter => parameter.ParameterType != typeof(CancellationToken))) schemas.Add(parameter.ParameterType);
        var parameters = new JsonArray();
        foreach (var parameter in api?.ParameterDescriptions ?? [])
        {
            if (parameter.Type == typeof(CancellationToken) || parameter.Source?.Id == "Services") continue;
            var entry = new JsonObject
            {
                ["name"] = parameter.Name, ["in"] = parameter.Source?.Id.ToLowerInvariant(),
                ["required"] = parameter.IsRequired, ["schema"] = schemas.Reference(parameter.Type)
            };
            if (parameter.DefaultValue is not null && parameter.DefaultValue != DBNull.Value)
                entry["default"] = JsonSerializer.SerializeToNode(parameter.DefaultValue);
            parameters.Add(entry);
        }
        // IgnoreApi的二进制/控制台入口仍由EndpointDataSource纳入，不静默漏掉。
        if (api is null)
            foreach (var parameter in action.MethodInfo.GetParameters().Where(p => p.ParameterType != typeof(CancellationToken)))
                parameters.Add(new JsonObject { ["name"] = parameter.Name, ["in"] = endpoint.RoutePattern.Parameters.Any(p => p.Name == parameter.Name) ? "path" : "query", ["required"] = !parameter.IsOptional, ["schema"] = schemas.Reference(parameter.ParameterType) });
        var response = Unwrap(action.MethodInfo.ReturnType);
        var responses = new JsonArray();
        if (response != typeof(void) && response != typeof(IActionResult))
            responses.Add(new JsonObject { ["status"] = 200, ["contentType"] = "application/json", ["schema"] = schemas.Reference(response) });
        foreach (var item in api?.SupportedResponseTypes ?? [])
            if (item.Type is not null && item.Type != typeof(void) && item.StatusCode != 200)
                responses.Add(new JsonObject { ["status"] = item.StatusCode, ["schema"] = schemas.Reference(item.Type) });
        var contract = new JsonObject
        {
            ["method"] = method, ["path"] = "/" + endpoint.RoutePattern.RawText?.TrimStart('/'),
            ["operation"] = action.ControllerName + "." + action.ActionName,
            ["anonymous"] = endpoint.Metadata.GetMetadata<IAllowAnonymous>() is not null,
            ["authorization"] = new JsonArray(authorization.Select(auth => (JsonNode?)new JsonObject { ["policy"] = auth.Policy, ["roles"] = auth.Roles, ["schemes"] = auth.AuthenticationSchemes }).ToArray()),
            ["csrf"] = endpoint.Metadata.GetMetadata<ValidateAntiForgeryTokenAttribute>() is not null,
            ["requestContentTypes"] = new JsonArray((api?.SupportedRequestFormats.Select(format => format.MediaType) ?? action.MethodInfo.GetCustomAttributes<ConsumesAttribute>().SelectMany(attribute => attribute.ContentTypes)).Distinct().Order().Select(value => (JsonNode?)JsonValue.Create(value)).ToArray()),
            ["parameters"] = parameters, ["responses"] = responses,
            ["businessFailures"] = new JsonObject { ["contentType"] = "application/json", ["schema"] = schemas.Reference(typeof(ApiResult<JsonElement>)), ["statusCodes"] = new JsonArray(400, 401, 403, 404, 409, 413, 429, 500, 502, 503, 504), ["note"] = "通用错误范围；各ServiceResult实际分支及机器码以服务实现和接口说明为准，不声称每条路由均返回全部状态" }
        };
        if (action.MethodInfo.GetCustomAttribute<RequestSizeLimitAttribute>() is Microsoft.AspNetCore.Http.Metadata.IRequestSizeLimitMetadata size) contract["maximumRequestBytes"] = size.MaxRequestBodySize;
        if (action.ControllerTypeInfo.AsType() == typeof(VirtualMediaController) && action.ActionName == "Content")
        {
            contract.Remove("businessFailures");
            contract["stream"] = new JsonObject { ["contentTypes"] = new JsonArray("application/octet-stream", "application/x-iso9660-image"), ["statusCodes"] = new JsonArray(200, 206, 404, 416), ["requestHeaders"] = new JsonArray("Range"), ["responseHeaders"] = new JsonArray("Accept-Ranges", "Content-Range", "Content-Length", "Cache-Control", "Referrer-Policy"), ["note"] = "HEAD无正文，GET支持单段Range；accessKey不得写入日志" };
        }
        if (action.ControllerTypeInfo.AsType() == typeof(ConsoleGatewayController))
            contract["stream"] = new JsonObject { ["protocol"] = action.ActionName == "Stream" ? "websocket" : "firmware-resource", ["note"] = "控制台绑定会话、来源、路径与设备能力校验；资源内容类型来自已审核固件，不套JSON信封；101只用于已升级WebSocket" };
        if (action.ControllerTypeInfo.AsType() == typeof(McpAuthorizationController))
        {
            contract.Remove("businessFailures");
            contract["protocol"] = "oauth2.1";
            contract["responseContentTypes"] = new JsonArray("application/json", "text/html");
            contract["note"] = "使用OAuth原生响应和重定向，不套ApiResult；授权POST的人工同意分支由处理器验证CSRF，token由OAuth客户端认证和PKCE校验";
        }
        return contract;
    }

    private static Type Unwrap(Type type)
    {
        while (type.IsGenericType && (type.GetGenericTypeDefinition() == typeof(Task<>) || type.GetGenericTypeDefinition() == typeof(ActionResult<>))) type = type.GetGenericArguments()[0];
        return type == typeof(Task) ? typeof(void) : type;
    }
    private static JsonObject Protocol(string method, string path, string protocol, string contentType, string note) =>
        new() { ["method"] = method, ["path"] = path, ["protocol"] = protocol, ["contentType"] = contentType, ["note"] = note };

    private static void RelocateSchemaReferences(JsonNode? node)
    {
        if (node is JsonObject obj)
        {
            if (obj["$ref"] is JsonValue value && value.TryGetValue<string>(out var reference) && reference.StartsWith("#/definitions/", StringComparison.Ordinal))
                obj["$ref"] = "schemas.json" + reference;
            foreach (var property in obj) RelocateSchemaReferences(property.Value);
        }
        else if (node is JsonArray array) foreach (var item in array) RelocateSchemaReferences(item);
    }

    private sealed class ExportRouteBuilder(IServiceProvider provider) : IEndpointRouteBuilder
    {
        public IServiceProvider ServiceProvider => provider;
        public ICollection<EndpointDataSource> DataSources { get; } = [];
        public IApplicationBuilder CreateApplicationBuilder() => new ApplicationBuilder(provider);
    }
    private sealed class ExportEnvironment : IWebHostEnvironment
    {
        public string ApplicationName { get; set; } = typeof(AuthController).Assembly.GetName().Name!;
        public string EnvironmentName { get; set; } = "Contracts";
        public string WebRootPath { get; set; } = AppContext.BaseDirectory;
        public IFileProvider WebRootFileProvider { get; set; } = new NullFileProvider();
        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
