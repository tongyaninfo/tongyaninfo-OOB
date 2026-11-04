using System.Net;
using System.Net.Sockets;
using MailKit;
using MailKit.Security;
using Microsoft.Extensions.Options;
using OobManagement.Application.Models;

namespace OobManagement.Infrastructure.Service.impl;

/// <summary>解析后检查全部地址并直连检查过的同一 IP；TLS 仍校验原主机名，避免 DNS 重绑定。</summary>
public class MailEndpointGuard(IOptions<MailOptions> options)
{
    public static SecureSocketOptions TlsOption(string mode) => mode switch
    {
        "SslOnConnect" => SecureSocketOptions.SslOnConnect,
        "StartTls" => SecureSocketOptions.StartTls,
        _ => throw new ArgumentException("只允许 TLS 或强制 STARTTLS。")
    };

    public virtual async Task ConnectAsync(MailKit.MailService client, MailTransportConfiguration configuration, CancellationToken ct)
    {
        var security = TlsOption(configuration.SecurityMode);
        if (!(options.Value.AllowedPorts ?? []).Contains(configuration.Port) || string.IsNullOrWhiteSpace(configuration.Host)
            || configuration.Host.Length > 253 || Uri.CheckHostName(configuration.Host) == UriHostNameType.Unknown)
            throw new ArgumentException("邮件端点或端口未获允许。");
        var addresses = await Dns.GetHostAddressesAsync(configuration.Host, ct);
        ValidateAddresses(configuration.Host, addresses);
        Exception? last = null;
        foreach (var address in addresses)
        {
            var socket = new Socket(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
            try
            {
                await socket.ConnectAsync(new IPEndPoint(address, configuration.Port), ct);
                // 不设置证书回调，不复用带外设备允许自签证书的独立策略。
                await client.ConnectAsync(socket, configuration.Host, configuration.Port, security, ct);
                return;
            }
            catch (SocketException ex) { last = ex; socket.Dispose(); }
            catch { socket.Dispose(); throw; }
        }
        throw last ?? new IOException("邮件端点无法连接。");
    }

    public void ValidateAddresses(string host, IReadOnlyList<IPAddress> addresses)
    {
        var allowPrivate = (options.Value.AllowedPrivateHosts ?? []).Contains(host, StringComparer.OrdinalIgnoreCase);
        if (addresses.Count == 0 || addresses.Any(ip => IsForbidden(ip) || !allowPrivate && IsPrivate(ip)))
            throw new ArgumentException("邮件端点解析到未获允许的网络地址。");
    }

    public static bool IsForbidden(IPAddress value)
    {
        var ip = value.IsIPv4MappedToIPv6 ? value.MapToIPv4() : value;
        if (IPAddress.IsLoopback(ip) || ip.Equals(IPAddress.Any) || ip.Equals(IPAddress.IPv6Any)
            || ip.IsIPv6LinkLocal || ip.IsIPv6Multicast) return true;
        var bytes = ip.GetAddressBytes();
        return ip.AddressFamily == AddressFamily.InterNetwork
            && (bytes[0] is 0 or 127 || bytes[0] >= 224 || bytes[0] == 169 && bytes[1] == 254);
    }

    public static bool IsPrivate(IPAddress value)
    {
        var ip = value.IsIPv4MappedToIPv6 ? value.MapToIPv4() : value;
        var bytes = ip.GetAddressBytes();
        if (ip.AddressFamily == AddressFamily.InterNetworkV6) return (bytes[0] & 0xfe) == 0xfc || ip.IsIPv6SiteLocal;
        return bytes[0] == 10 || bytes[0] == 172 && bytes[1] is >= 16 and <= 31
            || bytes[0] == 192 && bytes[1] == 168 || bytes[0] == 100 && bytes[1] is >= 64 and <= 127;
    }
}
