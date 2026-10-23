using System.Net;
using System.Net.Mail;
using Bookline.Api.Security;
using Microsoft.Extensions.Options;

namespace Bookline.Api.Services.Notifications;

public interface IEmailSender
{
    Task SendAsync(string recipient, string subject, string body, CancellationToken ct);
}

/// <summary>
/// Writes the email to the log instead of sending it.
///
/// This is the default so the project runs with no SMTP credentials, and so a
/// demo never accidentally emails a real address with test data. The reminder
/// pipeline is fully exercised either way — only the final hop differs.
/// </summary>
public class LoggingEmailSender(ILogger<LoggingEmailSender> logger) : IEmailSender
{
    public Task SendAsync(string recipient, string subject, string body, CancellationToken ct)
    {
        logger.LogInformation(
            "Email (not sent, logging provider)\n  To: {Recipient}\n  Subject: {Subject}\n{Body}",
            recipient, subject, body);

        return Task.CompletedTask;
    }
}

public class SmtpEmailSender(IOptions<NotificationOptions> options) : IEmailSender
{
    private readonly NotificationOptions _options = options.Value;

    public async Task SendAsync(string recipient, string subject, string body, CancellationToken ct)
    {
        using var client = new SmtpClient(_options.SmtpHost, _options.SmtpPort)
        {
            EnableSsl = true,
            Credentials = string.IsNullOrWhiteSpace(_options.SmtpUser)
                ? null
                : new NetworkCredential(_options.SmtpUser, _options.SmtpPassword),
        };

        using var message = new MailMessage(_options.FromAddress, recipient, subject, body);

        await client.SendMailAsync(message, ct);
    }
}
