using System.Net;
using System.Text;
using MailKit;
using MailKit.Security;
using Microsoft.Extensions.Options;
using MimeKit;
using OobManagement.Application.Models;
using OobManagement.Infrastructure.Common;
using OobManagement.Infrastructure.Service.impl;
using Xunit;

namespace OobManagement.Driver.Tests;

/// <summary>协议对话全部在内存中执行，不读取配置密码、不连接真实 SMTP 或放宽生产 TLS 策略。</summary>
public sealed class MailTransportTests
{
    private static readonly MailTransportConfiguration Configuration = new("smtp.example.test", 465, "SslOnConnect",
        "sender@example.test", "sender@example.test", "synthetic-password");
    private static readonly MailTransportMessage Message = new("stable-request@example.test", "recipient@example.test",
        "测试主题", "纯文本正文\n第二行", new DateTimeOffset(2026, 1, 2, 3, 4, 5, TimeSpan.Zero));

    [Theory]
    [InlineData("127.0.0.1")]
    [InlineData("0.0.0.0")]
    [InlineData("169.254.169.254")]
    [InlineData("224.0.0.1")]
    [InlineData("255.255.255.255")]
    [InlineData("::1")]
    [InlineData("::")]
    [InlineData("::ffff:127.0.0.1")]
    [InlineData("fe80::1")]
    [InlineData("ff02::1")]
    public void Forbidden_networks_remain_blocked_even_on_private_allowlist(string address)
    {
        var guard = Guard(new MailOptions { AllowedPrivateHosts = ["allowed.example.test"] });
        Assert.Throws<ArgumentException>(() => guard.ValidateAddresses("allowed.example.test", [IPAddress.Parse(address)]));
    }

    [Theory]
    [InlineData("10.0.0.1")]
    [InlineData("172.16.0.1")]
    [InlineData("192.168.0.1")]
    [InlineData("100.64.0.1")]
    [InlineData("fc00::1")]
    public void Private_networks_require_exact_host_allowlist(string address)
    {
        var guard = Guard(new MailOptions { AllowedPrivateHosts = ["mail.example.test"] });
        Assert.Throws<ArgumentException>(() => guard.ValidateAddresses("other.mail.example.test", [IPAddress.Parse(address)]));
        guard.ValidateAddresses("MAIL.EXAMPLE.TEST", [IPAddress.Parse(address)]);
    }

    [Fact]
    public void All_resolved_addresses_must_pass_and_empty_answers_fail_closed()
    {
        var guard = Guard();
        guard.ValidateAddresses("mail.example.test", [IPAddress.Parse("8.8.8.8")]);
        Assert.Throws<ArgumentException>(() => guard.ValidateAddresses("mail.example.test", []));
        Assert.Throws<ArgumentException>(() => guard.ValidateAddresses("mail.example.test",
            [IPAddress.Parse("8.8.8.8"), IPAddress.Parse("127.0.0.1")]));
    }

    [Theory]
    [InlineData("None")]
    [InlineData("Auto")]
    [InlineData("StartTlsWhenAvailable")]
    public void Cleartext_and_opportunistic_TLS_are_rejected(string mode)
        => Assert.Throws<ArgumentException>(() => MailEndpointGuard.TlsOption(mode));

    [Fact]
    public async Task Production_guard_rejects_loopback_without_sending_or_exposing_credentials()
    {
        var result = await new MailSmtpTransport(Guard()).ProbeAsync(Configuration with { Host = "127.0.0.1" }, CancellationToken.None);
        Assert.Equal("Failed", result.Status);
        Assert.Equal("InvalidEndpointOrMessage", result.ErrorCode);
        Assert.DoesNotContain(Configuration.Password, result.Message);
    }

    [Fact]
    public async Task Production_guard_rejects_unapproved_port_before_DNS()
    {
        var result = await new MailSmtpTransport(Guard()).ProbeAsync(Configuration with { Port = 12345 }, CancellationToken.None);
        Assert.Equal("Failed", result.Status);
        Assert.Equal("InvalidEndpointOrMessage", result.ErrorCode);
    }

    [Fact]
    public void MIME_keeps_stable_headers_and_single_plain_text_recipient()
    {
        using var mime = MailSmtpTransport.CreateMime(Configuration, Message);
        Assert.Equal(Message.MessageId, mime.MessageId);
        Assert.Equal(Message.CreatedAt, mime.Date);
        Assert.Equal(Message.ToAddress, Assert.Single(mime.To.Mailboxes).Address);
        Assert.Equal(Configuration.FromAddress, Assert.Single(mime.From.Mailboxes).Address);
        Assert.Empty(mime.Cc);
        Assert.Empty(mime.Bcc);
        Assert.Equal(Message.TextBody, Assert.IsType<TextPart>(mime.Body).Text);
        Assert.Null(mime.HtmlBody);
        Assert.DoesNotContain(Configuration.Password, Configuration.ToString());
        Assert.DoesNotContain(Message.TextBody, Message.ToString());
    }

    [Fact]
    public void Report_MIME_contains_plain_and_html_alternatives_in_order()
    {
        const string html = "<!doctype html><html><body><p>运行报表</p></body></html>";
        using var mime = MailSmtpTransport.CreateMime(Configuration, Message with { HtmlBody = html });
        var alternatives = Assert.IsType<MultipartAlternative>(mime.Body);
        Assert.Equal(2, alternatives.Count);
        Assert.Equal("plain", Assert.IsType<TextPart>(alternatives[0]).ContentType.MediaSubtype);
        Assert.Equal("html", Assert.IsType<TextPart>(alternatives[1]).ContentType.MediaSubtype);
        Assert.Equal(Message.TextBody, mime.TextBody);
        Assert.Equal(html, mime.HtmlBody);
        Assert.Equal(Message.MessageId, mime.MessageId);
        Assert.Single(mime.To.Mailboxes);
        Assert.DoesNotContain(html, (Message with { HtmlBody = html }).ToString());
    }

    [Fact]
    public void Report_png_becomes_inline_CID_resource_with_original_bytes_and_frozen_html_unchanged()
    {
        const string png = "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+/l1sAAAAASUVORK5CYII=";
        var html = $"<html><body><img alt=\"Chart\" src=\"data:image/png;base64,{png}\"></body></html>";
        var report = Message with { HtmlBody = html };
        using var mime = MailSmtpTransport.CreateMime(Configuration, report);
        var alternatives = Assert.IsType<MultipartAlternative>(mime.Body);
        Assert.Equal(Message.TextBody, Assert.IsType<TextPart>(alternatives[0]).Text);
        var related = Assert.IsType<MultipartRelated>(alternatives[1]);
        var resource = Assert.Single(related.OfType<MimePart>(), x => x.ContentType.MimeType == "image/png");
        Assert.Equal(ContentDisposition.Inline, resource.ContentDisposition!.Disposition);
        Assert.False(resource.IsAttachment);
        Assert.Contains("src=\"cid:" + resource.ContentId + "\"", mime.HtmlBody);
        Assert.DoesNotContain("data:image", mime.HtmlBody);
        using var bytes = new MemoryStream();
        resource.Content!.DecodeTo(bytes);
        Assert.Equal(Convert.FromBase64String(png), bytes.ToArray());
        Assert.Equal(html, report.HtmlBody);
        using var serialized = new MemoryStream();
        mime.WriteTo(serialized);
        serialized.Position = 0;
        using var roundTrip = MimeMessage.Load(serialized);
        Assert.Contains("cid:" + resource.ContentId, roundTrip.HtmlBody);
        var decoded = Assert.Single(roundTrip.BodyParts.OfType<MimePart>(), x => x.ContentType.MimeType == "image/png");
        using var roundTripBytes = new MemoryStream();
        decoded.Content!.DecodeTo(roundTripBytes);
        Assert.Equal(Convert.FromBase64String(png), roundTripBytes.ToArray());
    }

    [Fact]
    public async Task Report_images_reject_invalid_format_count_size_and_dimensions_before_connecting()
    {
        const string png = "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+/l1sAAAAASUVORK5CYII=";
        using var script = new SmtpScriptStream();
        var connector = new ScriptConnector(script);
        var transport = new MailSmtpTransport(connector);
        var invalid = new List<string>
        {
            "<img src=\"data:image/png;base64,not-base64\">",
            "<img src=\"data:image/png;base64," + Convert.ToBase64String([1, 2, 3]) + "\">",
            "<img src=\"data:image/svg+xml;base64,PHN2Zz4=\">",
            string.Concat(Enumerable.Repeat($"<img src=\"data:image/png;base64,{png}\">", MailInlineImages.MaximumCount + 1)),
            "<img src=\"data:image/png;base64," + Convert.ToBase64String(new byte[MailInlineImages.MaximumImageBytes + 1]) + "\">"
        };
        var hugeDimensions = Convert.FromBase64String(png);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(hugeDimensions.AsSpan(16, 4), 4097);
        invalid.Add("<img src=\"data:image/png;base64," + Convert.ToBase64String(hugeDimensions) + "\">");
        // 两张各160001字节图片的HTML仍小于500000字符，但图片总量必须独立受限。
        var oversizedTotalImage = new byte[160001];
        var source = Convert.FromBase64String(png);
        source.CopyTo(oversizedTotalImage, 0);
        source.AsSpan(source.Length - 12).CopyTo(oversizedTotalImage.AsSpan(oversizedTotalImage.Length - 12));
        var image = "<img src=\"data:image/png;base64," + Convert.ToBase64String(oversizedTotalImage) + "\">";
        invalid.Add(image + image);
        foreach (var html in invalid)
        {
            Assert.True(html.Length < MailRules.MaxHtmlBodyLength);
            var result = await transport.SendAsync(Configuration, Message with { HtmlBody = html }, CancellationToken.None);
            Assert.Equal("Failed", result.Status);
            Assert.Equal("InvalidInlineImage", result.ErrorCode);
        }
        Assert.Equal(0, connector.Connections);
    }

    [Fact]
    public void Report_inline_conversion_does_not_fetch_or_attach_remote_or_local_sources()
    {
        const string html = "<html><body><img src=\"https://example.invalid/chart.png\"><img src='file:///synthetic/nonexistent.png'></body></html>";
        using var mime = MailSmtpTransport.CreateMime(Configuration, Message with { HtmlBody = html });
        Assert.Equal(html, mime.HtmlBody);
        Assert.IsType<MultipartAlternative>(mime.Body);
        Assert.DoesNotContain(mime.BodyParts.OfType<MimePart>(), x => x.ContentType.MimeType == "image/png");
    }

    [Fact]
    public async Task Invalid_html_is_rejected_before_connecting_and_maximum_is_accepted()
    {
        using var maximum = MailSmtpTransport.CreateMime(Configuration, Message with { HtmlBody = new string('h', 500000) });
        Assert.Equal(500000, maximum.HtmlBody!.Length);
        using var script = new SmtpScriptStream();
        var connector = new ScriptConnector(script);
        var transport = new MailSmtpTransport(connector);
        foreach (var html in new[] { "", " ", "<p>bad\0</p>", new string('h', 500001) })
        {
            var result = await transport.SendAsync(Configuration, Message with { HtmlBody = html }, CancellationToken.None);
            Assert.Equal("Failed", result.Status);
            Assert.Equal("InvalidEndpointOrMessage", result.ErrorCode);
        }
        Assert.Equal(0, connector.Connections);
    }

    [Fact]
    public async Task Report_revoke_after_authentication_blocks_submission_and_lost_reply_stays_unknown()
    {
        var report = Message with { HtmlBody = "<p>运行报表</p><img src=\"data:image/png;base64,iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+/l1sAAAAASUVORK5CYII=\">" };
        using var revokedScript = new SmtpScriptStream();
        var revoked = await Transport(revokedScript).SendAsync(Configuration, report, CancellationToken.None,
            _ => Task.FromResult(ServiceResult<bool>.Fail("MAIL_REPORT_DISABLED", "计划已关闭", 409)));
        Assert.Equal("Failed", revoked.Status);
        Assert.Equal("MAIL_REPORT_DISABLED", revoked.ErrorCode);
        Assert.Equal(0, revokedScript.MailCommands);
        using var lostScript = new SmtpScriptStream(dropFinalReply: true);
        var lost = await Transport(lostScript).SendAsync(Configuration, report, CancellationToken.None);
        Assert.Equal("Unknown", lost.Status);
        Assert.Equal(1, lostScript.MailCommands);
        Assert.True(lostScript.DataCompleted);
    }

    [Fact]
    public async Task SMTP_acceptance_is_reported_without_claiming_delivery()
    {
        using var script = new SmtpScriptStream();
        var result = await Transport(script).SendAsync(Configuration, Message, CancellationToken.None);
        Assert.Equal("Accepted", result.Status);
        Assert.Null(result.ErrorCode);
        Assert.True(script.DataCompleted);
        Assert.Equal(1, script.MailCommands);
        Assert.Contains("不代表", result.Message);
    }

    [Fact]
    public async Task Lost_reply_after_data_is_unknown_and_never_resent()
    {
        using var script = new SmtpScriptStream(dropFinalReply: true);
        var result = await Transport(script).SendAsync(Configuration, Message, CancellationToken.None);
        Assert.Equal("Unknown", result.Status);
        Assert.Equal("SubmissionUnknown", result.ErrorCode);
        Assert.True(script.DataCompleted);
        Assert.Equal(1, script.MailCommands);
        Assert.DoesNotContain("synthetic", result.Message);
    }

    [Fact]
    public async Task Explicit_recipient_rejection_never_submits_body_or_exposes_server_text()
    {
        using var script = new SmtpScriptStream(rejectRecipient: true);
        var result = await Transport(script).SendAsync(Configuration, Message, CancellationToken.None);
        Assert.Equal("Failed", result.Status);
        Assert.Equal("SmtpRejected", result.ErrorCode);
        Assert.False(script.DataCompleted);
        Assert.DoesNotContain("synthetic-remote-secret", result.Message);
    }

    [Fact]
    public async Task Explicit_data_rejection_is_failed_instead_of_unknown()
    {
        using var script = new SmtpScriptStream(rejectData: true);
        var result = await Transport(script).SendAsync(Configuration, Message, CancellationToken.None);
        Assert.Equal("Failed", result.Status);
        Assert.Equal("SmtpRejected", result.ErrorCode);
        Assert.True(script.DataCompleted);
        Assert.Equal(1, script.MailCommands);
    }

    [Fact]
    public async Task Probe_authenticates_without_creating_mail_transaction()
    {
        using var script = new SmtpScriptStream();
        var result = await Transport(script).ProbeAsync(Configuration, CancellationToken.None);
        Assert.Equal("Accepted", result.Status);
        Assert.Equal(0, script.MailCommands);
        Assert.False(script.DataCompleted);
    }

    [Fact]
    public async Task Probe_server_rejection_returns_safe_failure_instead_of_raw_exception()
    {
        using var script = new SmtpScriptStream(rejectHandshake: true);
        var result = await Transport(script).ProbeAsync(Configuration, CancellationToken.None);
        Assert.Equal("Failed", result.Status);
        Assert.Equal("SmtpRejected", result.ErrorCode);
        Assert.Equal(0, script.MailCommands);
        Assert.DoesNotContain("synthetic-remote-secret", result.Message);
    }

    [Fact]
    public async Task Authentication_rejection_is_safe_and_does_not_submit_mail()
    {
        using var script = new SmtpScriptStream(rejectAuthentication: true);
        var result = await Transport(script).SendAsync(Configuration, Message, CancellationToken.None);
        Assert.Equal("Failed", result.Status);
        Assert.Equal("AuthenticationFailed", result.ErrorCode);
        Assert.Equal(0, script.MailCommands);
        Assert.DoesNotContain("synthetic-remote-secret", result.Message);
        Assert.DoesNotContain(Configuration.Password, result.Message);
    }

    [Fact]
    public async Task Revocation_after_authentication_prevents_SMTP_submission()
    {
        using var script = new SmtpScriptStream();
        var calls = 0;
        var result = await Transport(script).SendAsync(Configuration, Message, CancellationToken.None, _ =>
        {
            calls++;
            return Task.FromResult(ServiceResult<bool>.Fail("AUTHORIZATION_REVOKED", "private subject", 403));
        });
        Assert.Equal(1, calls);
        Assert.True(script.Authenticated);
        Assert.Equal("Failed", result.Status);
        Assert.Equal("AUTHORIZATION_REVOKED", result.ErrorCode);
        Assert.DoesNotContain("private subject", result.Message);
        Assert.Equal(0, script.MailCommands);
    }

    [Fact]
    public async Task Authorization_lookup_exception_fails_before_submission()
    {
        using var script = new SmtpScriptStream();
        var result = await Transport(script).SendAsync(Configuration, Message, CancellationToken.None,
            _ => throw new IOException("synthetic-private-database-error"));
        Assert.Equal("Failed", result.Status);
        Assert.Equal("AUTHORIZATION_UNAVAILABLE", result.ErrorCode);
        Assert.Equal(0, script.MailCommands);
        Assert.DoesNotContain("synthetic-private-database-error", result.Message);
    }

    [Theory]
    [InlineData("Header\r\nBcc: stolen@example.test")]
    [InlineData("")]
    public async Task Invalid_subject_is_rejected_before_connecting(string subject)
    {
        using var script = new SmtpScriptStream();
        var connector = new ScriptConnector(script);
        var result = await new MailSmtpTransport(connector).SendAsync(Configuration, Message with { Subject = subject }, CancellationToken.None);
        Assert.Equal("Failed", result.Status);
        Assert.Equal("InvalidEndpointOrMessage", result.ErrorCode);
        Assert.Equal(0, connector.Connections);
    }

    [Fact]
    public async Task Length_boundaries_match_business_contract_and_fail_before_connecting()
    {
        using var maximum = MailSmtpTransport.CreateMime(Configuration,
            Message with { Subject = new string('s', 200), TextBody = new string('b', 20000) });
        Assert.Equal(20000, maximum.TextBody!.Length);
        using var script = new SmtpScriptStream();
        var connector = new ScriptConnector(script);
        var transport = new MailSmtpTransport(connector);
        var longSubject = await transport.SendAsync(Configuration, Message with { Subject = new string('s', 201) }, CancellationToken.None);
        var longBody = await transport.SendAsync(Configuration, Message with { TextBody = new string('b', 20001) }, CancellationToken.None);
        Assert.Equal("InvalidEndpointOrMessage", longSubject.ErrorCode);
        Assert.Equal("InvalidEndpointOrMessage", longBody.ErrorCode);
        Assert.Equal(0, connector.Connections);
    }

    private static MailEndpointGuard Guard(MailOptions? options = null) => new(Options.Create(options ?? new()));
    private static MailSmtpTransport Transport(Stream stream) => new(new ScriptConnector(stream));

    private sealed class ScriptConnector(Stream script) : MailEndpointGuard(Options.Create(new MailOptions()))
    {
        public int Connections { get; private set; }
        public override Task ConnectAsync(MailKit.MailService client, MailTransportConfiguration configuration, CancellationToken ct)
        {
            Connections++;
            // 仅测试替身使用内存明文对话；生产连接器没有跳过 TLS 的入口。
            return client.ConnectAsync(script, configuration.Host, configuration.Port, SecureSocketOptions.None, ct);
        }
    }

    private sealed class SmtpScriptStream(bool rejectRecipient = false, bool dropFinalReply = false,
        bool rejectData = false, bool rejectAuthentication = false, bool rejectHandshake = false) : Stream
    {
        private readonly Queue<byte> _responses = new(Encoding.ASCII.GetBytes("220 test ESMTP ready\r\n"));
        private readonly StringBuilder _incoming = new();
        private bool _data;
        public bool DataCompleted { get; private set; }
        public bool Authenticated { get; private set; }
        public int MailCommands { get; private set; }
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override Task FlushAsync(CancellationToken ct) => Task.CompletedTask;
        public override int Read(byte[] buffer, int offset, int count)
        {
            if (_responses.Count == 0) throw new IOException("Synthetic disconnected stream.");
            var read = 0;
            while (read < count && _responses.TryDequeue(out var value)) buffer[offset + read++] = value;
            return read;
        }
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
        {
            var bytes = new byte[buffer.Length];
            var read = Read(bytes, 0, bytes.Length);
            bytes.AsMemory(0, read).CopyTo(buffer);
            return ValueTask.FromResult(read);
        }
        public override void Write(byte[] buffer, int offset, int count) => Process(Encoding.UTF8.GetString(buffer, offset, count));
        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken ct = default)
        { Process(Encoding.UTF8.GetString(buffer.Span)); return ValueTask.CompletedTask; }
        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken ct)
        { Write(buffer, offset, count); return Task.CompletedTask; }
        private void Process(string text)
        {
            _incoming.Append(text);
            if (_data)
            {
                if (!_incoming.ToString().EndsWith("\r\n.\r\n", StringComparison.Ordinal)) return;
                DataCompleted = true;
                _data = false;
                _incoming.Clear();
                if (!dropFinalReply) Reply(rejectData ? "550 synthetic-remote-secret\r\n" : "250 accepted\r\n");
                return;
            }
            while (_incoming.ToString().Contains("\r\n", StringComparison.Ordinal))
            {
                var input = _incoming.ToString();
                var end = input.IndexOf("\r\n", StringComparison.Ordinal);
                var command = input[..end];
                _incoming.Remove(0, end + 2);
                if (command.StartsWith("EHLO", StringComparison.Ordinal)) Reply(rejectHandshake ? "550 synthetic-remote-secret\r\n" : "250-test\r\n250 AUTH PLAIN\r\n");
                else if (command.StartsWith("HELO", StringComparison.Ordinal)) Reply("550 synthetic-remote-secret\r\n");
                else if (command.StartsWith("AUTH", StringComparison.Ordinal))
                {
                    Authenticated = !rejectAuthentication;
                    Reply(rejectAuthentication ? "535 synthetic-remote-secret\r\n" : "235 authenticated\r\n");
                }
                else if (command.StartsWith("MAIL", StringComparison.Ordinal)) { MailCommands++; Reply("250 sender accepted\r\n"); }
                else if (command.StartsWith("RCPT", StringComparison.Ordinal)) Reply(rejectRecipient ? "550 synthetic-remote-secret\r\n" : "250 recipient accepted\r\n");
                else if (command == "DATA") { _data = true; Reply("354 continue\r\n"); break; }
                else if (command == "RSET") Reply("250 reset\r\n");
                else if (command == "QUIT") Reply("221 bye\r\n");
                else throw new IOException("Unexpected synthetic SMTP command.");
            }
        }
        private void Reply(string value) { foreach (var b in Encoding.ASCII.GetBytes(value)) _responses.Enqueue(b); }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
    }
}
