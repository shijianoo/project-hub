using System.Net;
using System.Net.Mail;
using Microsoft.Extensions.Options;
using ProjectHub.Application.Abstractions;
using ProjectHub.Application.Contracts;
using ProjectHub.Infrastructure.Configuration;

namespace ProjectHub.Infrastructure.Services;

/// <summary>使用标准 SMTP 协议发送 ProjectHub 通知邮件。</summary>
public sealed class SmtpNotificationEmailSender(IOptionsMonitor<SmtpOptions> smtpOptions) : INotificationEmailSender
{
    public async Task SendAsync(NotificationEmailMessage message, CancellationToken cancellationToken)
    {
        SmtpOptions options = smtpOptions.CurrentValue;
        if (!options.IsConfigured())
        {
            throw new InvalidOperationException("SMTP 尚未启用或必要参数不完整");
        }

        using MailMessage mail = new()
        {
            From = new MailAddress(options.FromAddress.Trim(), options.FromName.Trim()),
            Subject = message.Subject,
            Body = message.HtmlBody,
            IsBodyHtml = true,
            BodyEncoding = System.Text.Encoding.UTF8,
            SubjectEncoding = System.Text.Encoding.UTF8
        };
        mail.To.Add(new MailAddress(message.RecipientAddress, message.RecipientName));

        using SmtpClient client = new(options.Host.Trim(), options.Port)
        {
            DeliveryMethod = SmtpDeliveryMethod.Network,
            EnableSsl = options.EnableSsl,
            UseDefaultCredentials = options.UseDefaultCredentials,
            Timeout = Math.Clamp(options.TimeoutSeconds, 5, 300) * 1000
        };
        if (!options.UseDefaultCredentials && !string.IsNullOrWhiteSpace(options.UserName))
        {
            client.Credentials = new NetworkCredential(options.UserName, options.Password);
        }

        await client.SendMailAsync(mail, cancellationToken);
    }
}
