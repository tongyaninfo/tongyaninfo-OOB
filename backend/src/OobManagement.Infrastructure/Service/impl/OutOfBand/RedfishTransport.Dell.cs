using System.Globalization;
using System.Net.Http.Headers;
using System.Text;
using System.Xml;
using System.Xml.Linq;
using OobManagement.Application.Models;

namespace OobManagement.Infrastructure.Service.impl.OutOfBand;

/// <summary>Dell HTTPS RACADM 只读补充通道；复用当前设备的 TLS、指纹及有界响应读取。</summary>
internal sealed partial class RedfishTransport
{
    public bool AllowsSupplementalAuthentication => _connection.UseSessionAuthentication;

    public async Task<RedfishResult<IReadOnlyDictionary<string, double?>>> ReadDellFanSensorsAsync(CancellationToken cancellationToken)
    {
        string? session = null;
        try
        {
            var login = await SendDellXmlAsync("/cgi-bin/login", new XElement("LOGIN", new XElement("REQ",
                new XElement("USERNAME", _connection.Username), new XElement("PASSWORD", _connection.Password))), null, cancellationToken);
            if (!login.Success) return RedfishResult<IReadOnlyDictionary<string, double?>>.Fail(login.Failure!);
            var response = login.Data.Name == "LOGIN" ? login.Data.Element("RESP") : null;
            var candidate = response?.Element("SID")?.Value;
            // 会话标识只能作为本源 Cookie 使用，拒绝响应头注入，且不记录或持久化其内容。
            if (candidate is { Length: > 0 and <= 256 } && candidate.All(character => char.IsAsciiLetterOrDigit(character) || character is '-' or '_'))
                session = candidate;
            if (!IsDellSuccess(response?.Element("RC")?.Value))
                return Fail<IReadOnlyDictionary<string, double?>>("RacadmAuthenticationFailed", "Dell 补充读取认证未被设备接受。");
            if (session is null)
                return Fail<IReadOnlyDictionary<string, double?>>("InvalidRacadmSession", "Dell 未返回可核验的补充读取会话。", "DeviceFailure");

            // 命令是固定常量，无调用方输入入口；不接收任意命令，也不发送配置或电源动作。
            // 本期真机固件不接受 -c，使用已经核验的普通格式，独立解析 PWM 列。
            var executed = await SendDellXmlAsync("/cgi-bin/exec", new XElement("EXEC", new XElement("REQ",
                new XElement("CMDINPUT", "racadm getsensorinfo"), new XElement("MAXOUTPUTLEN", "0x10000"))), session, cancellationToken);
            if (!executed.Success) return RedfishResult<IReadOnlyDictionary<string, double?>>.Fail(executed.Failure!);
            var command = executed.Data.Name == "EXEC" ? executed.Data.Element("RESP") : null;
            if (!IsDellSuccess(command?.Element("RC")?.Value) || !IsDellSuccess(command?.Element("CMDRC")?.Value))
                return Fail<IReadOnlyDictionary<string, double?>>("RacadmCommandRejected", "Dell 未确认风扇传感器读取成功。");
            var output = command?.Element("CMDOUTPUT")?.Value;
            if (output is null || Encoding.UTF8.GetByteCount(output) >= 65536)
                return Fail<IReadOnlyDictionary<string, double?>>("InvalidRacadmOutput", "Dell 风扇传感器输出缺失或达到截断上限。", "DeviceFailure");
            return RedfishResult<IReadOnlyDictionary<string, double?>>.Ok(DellFanSensorParser.Parse(output));
        }
        finally
        {
            if (session is not null)
            {
                // 只注销本轮拥有的 RACADM 会话；不操作 Redfish/浏览器会话，调用取消后也单独清理。
                using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                var logout = await SendDellXmlAsync("/cgi-bin/logout", null, session, cleanup.Token, cleanup: true);
                if (!logout.Success || logout.Data.Name != "LOGOUT" || !IsDellSuccess(logout.Data.Element("RESP")?.Element("RC")?.Value))
                {
                    Warnings.Add("本轮 Dell 补充读取会话未确认注销，请由设备超时回收。");
                    var diagnostic = logout.Failure is { Category: "DeviceFailure" } failure ? failure
                        : new DeviceDiagnostic("RacadmSessionCleanupFailed", "Dell 补充读取会话未确认注销。", "DeviceFailure", logout.Failure?.Cause);
                    if (!Diagnostics.Contains(diagnostic)) Diagnostics.Add(diagnostic);
                    // 取消路径没有普通返回值，清理故障仍需立即进入现有异常记录器。
                    if (_recordCleanupFailure is not null)
                    {
                        try { await _recordCleanupFailure(diagnostic); }
                        catch { try { Console.Error.WriteLine("设备会话清理异常记录暂不可用。"); } catch { } }
                    }
                }
            }
        }
    }

    private static bool IsDellSuccess(string? value) => value is not null &&
        (value.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
            ? long.TryParse(value.AsSpan(2), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out var hexadecimal) && hexadecimal == 0
            : long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var number) && number == 0);

    private async Task<RedfishResult<XElement>> SendDellXmlAsync(string path, XElement? body, string? session,
        CancellationToken cancellationToken, bool cleanup = false)
    {
        try
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            deadline.CancelAfter(TimeSpan.FromSeconds(15));
            using var request = new HttpRequestMessage(body is null ? HttpMethod.Get : HttpMethod.Post, new Uri(_origin, path));
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/xml"));
            if (session is not null) request.Headers.TryAddWithoutValidation("Cookie", "sid=" + session);
            if (body is not null) request.Content = new StringContent("<?xml version=\"1.0\"?>" + body.ToString(SaveOptions.DisableFormatting), Encoding.UTF8, "application/xml");
            using var response = await _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, deadline.Token);
            // 不跟随重定向，不从响应中选新地址，账号和会话均只发往已核验的当前来源。
            if (StatusFailure(response.StatusCode) is { } status) return Failed<XElement>(status);
            var bytes = await ReadBoundedAsync(response.Content, deadline.Token);
            if (!bytes.Success) return RedfishResult<XElement>.Fail(bytes.Failure!);
            using var input = new MemoryStream(bytes.Data, writable: false);
            using var reader = XmlReader.Create(input, new XmlReaderSettings
            {
                DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = 256 * 1024
            });
            return RedfishResult<XElement>.Ok(XElement.Load(reader));
        }
        catch (Exception error) when (error is HttpRequestException or IOException or XmlException or OperationCanceledException
            && (cleanup || !cancellationToken.IsCancellationRequested))
        {
            var code = error switch
            {
                OperationCanceledException => "Timeout", XmlException => "InvalidResponse",
                HttpRequestException { HttpRequestError: HttpRequestError.SecureConnectionError } => "TlsConnectionFailed", _ => "ConnectionFailed"
            };
            var diagnostic = new DeviceDiagnostic(code, cleanup ? "Dell 补充读取会话清理未完成。" : "Dell 风扇 PWM 补充资源读取失败。", "DeviceFailure", error);
            Diagnostics.Add(diagnostic);
            return RedfishResult<XElement>.Fail(diagnostic);
        }
    }
}
