using OobManagement.Application.Models;
using Microsoft.AspNetCore.DataProtection;
using OobManagement.Domain.Models;
using OobManagement.Infrastructure.Service.impl;
using Xunit;

namespace OobManagement.Driver.Tests;

/// <summary>覆盖发信入口实际危险边界：头注入、多人误填、幂等跨模式复用和重启未知结果。</summary>
public sealed class MailRulesTests
{
    [Theory]
    [InlineData("recipient@example.test,other@example.test")]
    [InlineData("recipient@example.test;other@example.test")]
    [InlineData("Recipient <recipient@example.test>")]
    [InlineData("recipient@example.test\r\nBcc:other@example.test")]
    public void Single_recipient_boundary_rejects_lists_and_header_injection(string address)
    {
        var result = MailRules.Validate(new MailSendRequest("stable-intent", address, "测试", "正文"));
        Assert.False(result.Success); Assert.Equal("MAIL_INVALID_ADDRESS", result.ErrorCode);
    }

    [Theory]
    [InlineData("主题\r\nBcc:other@example.test")]
    [InlineData("主题\0后文")]
    public void Subject_cannot_create_additional_headers(string subject) =>
        Assert.Equal("MAIL_INVALID_SUBJECT", MailRules.Validate(new MailSendRequest("stable-intent", "recipient@example.test", subject, "正文")).ErrorCode);

    [Fact]
    public void Stable_intent_hash_rejects_other_content_sender_and_mode()
    {
        var request = new MailSendRequest("stable-intent", "recipient@example.test", "测试", "正文");
        var original = MailRules.RequestHash(request, "Manual", 9007199254740993L);
        Assert.Equal(original, MailRules.RequestHash(request with { RequestId = "same-content" }, "Manual", 9007199254740993L));
        Assert.NotEqual(original, MailRules.RequestHash(request with { TextBody = "其他正文" }, "Manual", 9007199254740993L));
        Assert.NotEqual(original, MailRules.RequestHash(request, "Test", 9007199254740993L));
        Assert.NotEqual(original, MailRules.RequestHash(request, "Manual", 9007199254740994L));
        Assert.NotEqual(original, MailRules.RequestHash(request, "System", null));
    }

    [Fact]
    public void Html_content_is_part_of_frozen_intent_and_list_does_not_leak_either_body()
    {
        var request = new MailSendRequest("stable-report", "recipient@example.test", "Report", "Plain report");
        var plainHash = MailRules.RequestHash(request, "System", null);
        Assert.Equal("634d1f9b750a01263565dcc545ec0faf477053796a41c5c1ed94a621206ae825", plainHash);
        var htmlHash = MailRules.RequestHash(request, "System", null, "<p>Report</p>");
        Assert.Equal(plainHash, MailRules.RequestHash(request, "System", null, null));
        Assert.NotEqual(plainHash, htmlHash);
        Assert.NotEqual(htmlHash, MailRules.RequestHash(request, "System", null, "<p>Changed report</p>"));
        var message = new MailMessage { Status = "Accepted", TextBody = request.TextBody, HtmlBody = "<p>Report</p>" };
        var list = MailRules.View(message, false, false);
        Assert.Null(list.TextBody);
        Assert.Null(list.HtmlBody);
        Assert.Equal(message.HtmlBody, MailRules.View(message, true, false).HtmlBody);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  ")]
    [InlineData("<p>report\0</p>")]
    public void Html_body_rejects_missing_or_null_containing_content(string? htmlBody) =>
        Assert.Equal("MAIL_INVALID_HTML_BODY", MailRules.ValidateHtml(htmlBody).ErrorCode);

    [Fact]
    public void Html_body_has_separate_bounded_size()
    {
        Assert.True(MailRules.ValidateHtml(new string('h', 500000)).Success);
        Assert.False(MailRules.ValidateHtml(new string('h', 500001)).Success);
    }

    [Fact]
    public void Restarted_sending_is_unknown_without_rewriting_the_persisted_record()
    {
        var message = new MailMessage { Id = 9007199254740993L, CreatedByUserId = 9007199254740994L, Status = "Sending", TextBody = "受控正文" };
        var state = new MailSendState();
        Assert.Equal("Unknown", MailRules.View(message, false, state.IsActive(message.Id)).Status);
        Assert.Equal("Sending", message.Status);
        Assert.Null(MailRules.View(message, false, false).TextBody);
        state.Start(message.Id);
        Assert.Equal("Sending", MailRules.View(message, false, state.IsActive(message.Id)).Status);
        state.Finish(message.Id);
        var detail = MailRules.View(message, true, state.IsActive(message.Id));
        Assert.Equal("Unknown", detail.Status); Assert.Equal("受控正文", detail.TextBody);
        Assert.Equal("9007199254740993", detail.Id); Assert.Equal("9007199254740994", detail.CreatedByUserId);
    }

    [Fact]
    public void Accepted_remains_only_accepted_and_request_debug_text_hides_secrets()
    {
        var view = MailRules.View(new MailMessage { Status = "Accepted" }, true, false);
        Assert.Equal("Accepted", view.Status);
        Assert.DoesNotContain("synthetic-password", new MailConfigurationRequest { Password = "synthetic-password" }.ToString());
        Assert.DoesNotContain("synthetic-body", new MailSendRequest("stable-intent", "recipient@example.test", "主题", "synthetic-body").ToString());
        Assert.False(MailRules.View(null).Configured); Assert.Equal(465, MailRules.View(null).Port);
    }

    [Fact]
    public void Password_limit_fits_the_ciphertext_column_after_unicode_encryption()
    {
        var configuration = new MailConfigurationRequest { Host = "smtp.example.test", FromAddress = "sender@example.test", Password = new string('密', 512) };
        Assert.True(MailRules.Validate(configuration).Success);
        Assert.False(MailRules.Validate(configuration with { Password = new string('密', 513) }).Success);
        var protector = new EphemeralDataProtectionProvider().CreateProtector("OobManagement.MailCredentials.v1");
        Assert.True(protector.Protect(configuration.Password!).Length <= 4096);
    }
}
