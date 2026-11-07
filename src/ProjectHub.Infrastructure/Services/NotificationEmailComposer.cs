using System.Net;
using ProjectHub.Application.Contracts;
using ProjectHub.Domain.Entities;

namespace ProjectHub.Infrastructure.Services;

/// <summary>生成兼容常见邮件客户端的轻量 HTML 通知邮件。</summary>
internal static class NotificationEmailComposer
{
    internal static NotificationEmailMessage Compose(
        Notification notification,
        User recipient,
        string publicWebUrl)
    {
        string title = WebUtility.HtmlEncode(notification.Title);
        string content = WebUtility.HtmlEncode(notification.Content).Replace("\n", "<br>", StringComparison.Ordinal);
        string? actionLink = BuildActionLink(publicWebUrl, notification.ActionUrl);
        string actionMarkup = actionLink is null
            ? string.Empty
            : $"<a href=\"{WebUtility.HtmlEncode(actionLink)}\" style=\"display:inline-block;margin-top:22px;padding:11px 20px;border-radius:8px;background:#3157d5;color:#fff;text-decoration:none;font-size:14px;font-weight:600\">在 ProjectHub 中查看</a>";
        string body = $"""
            <!doctype html>
            <html lang="zh-CN">
            <body style="margin:0;padding:0;background:#f4f6fb;font-family:'Microsoft YaHei',Arial,sans-serif;color:#1f2937">
              <table role="presentation" width="100%" cellspacing="0" cellpadding="0" style="background:#f4f6fb;padding:32px 12px">
                <tr><td align="center">
                  <table role="presentation" width="600" cellspacing="0" cellpadding="0" style="max-width:600px;width:100%;background:#fff;border-radius:14px;overflow:hidden;box-shadow:0 8px 28px rgba(31,41,55,.08)">
                    <tr><td style="padding:22px 28px;background:linear-gradient(135deg,#2546b8,#4c6ee8);color:#fff">
                      <div style="font-size:12px;letter-spacing:1.8px;opacity:.82">PROJECTHUB NOTIFICATION</div>
                      <div style="margin-top:6px;font-size:21px;font-weight:700">{title}</div>
                    </td></tr>
                    <tr><td style="padding:28px">
                      <div style="font-size:14px;color:#6b7280">{WebUtility.HtmlEncode(recipient.DisplayName)}，您好：</div>
                      <div style="margin-top:15px;font-size:15px;line-height:1.8;color:#374151">{content}</div>
                      {actionMarkup}
                      <div style="margin-top:28px;padding-top:18px;border-top:1px solid #eef0f5;font-size:12px;line-height:1.7;color:#9ca3af">
                        此邮件由 ProjectHub 自动发送。即使未配置邮箱，相关信息仍会保留在站内通知中心。
                      </div>
                    </td></tr>
                  </table>
                </td></tr>
              </table>
            </body>
            </html>
            """;
        return new NotificationEmailMessage(
            recipient.Email ?? string.Empty,
            recipient.DisplayName,
            $"[ProjectHub] {notification.Title}",
            body);
    }

    internal static string? BuildActionLink(string publicWebUrl, string? actionUrl)
    {
        if (string.IsNullOrWhiteSpace(publicWebUrl) || string.IsNullOrWhiteSpace(actionUrl))
        {
            return null;
        }

        return $"{publicWebUrl.TrimEnd('/')}/{actionUrl.TrimStart('/')}";
    }
}
