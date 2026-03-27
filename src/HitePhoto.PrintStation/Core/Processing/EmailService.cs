using MailKit.Net.Smtp;
using MailKit.Security;
using MimeKit;
using HitePhoto.PrintStation.Core.Services;
using HitePhoto.Shared.Models;

namespace HitePhoto.PrintStation.Core.Processing;

public class EmailResult
{
    public bool Success { get; set; }
    public string? ErrorMessage { get; set; }
}

public class EmailTemplate
{
    public string Name    { get; set; } = "";
    public string Subject { get; set; } = "";
    public string Body    { get; set; } = "";
    public override string ToString() => Name;
}

/// <summary>
/// Sends customer notification emails via SMTP.
/// Adapted from PrintRouter's NotificationService to use DB Order model.
/// Notification status is tracked in the DB via order_notes (note_type = "notification").
/// </summary>
public class EmailService : IEmailSender
{
    private readonly AppSettings _settings;

    public EmailService(AppSettings settings)
    {
        _settings = settings;
    }

    public async Task<EmailResult> SendOrderReadyEmailAsync(Order order, EmailTemplate? template = null)
    {
        if (!_settings.NotificationsEnabled)
            return new EmailResult { Success = false, ErrorMessage = "Notifications are not enabled." };

        if (string.IsNullOrWhiteSpace(_settings.SmtpUsername) || string.IsNullOrWhiteSpace(_settings.SmtpPassword))
            return new EmailResult { Success = false, ErrorMessage = "SMTP credentials are not configured." };

        if (string.IsNullOrWhiteSpace(order.CustomerEmail))
            return new EmailResult { Success = false, ErrorMessage = "Customer has no email address." };

        if (string.IsNullOrWhiteSpace(_settings.NotificationFromEmail))
            return new EmailResult { Success = false, ErrorMessage = "From email address is not configured." };

        try
        {
            var subjectTemplate = !string.IsNullOrEmpty(template?.Subject) ? template.Subject : _settings.NotificationSubject;
            var bodyTemplate = !string.IsNullOrEmpty(template?.Body) ? template.Body : _settings.NotificationBodyTemplate;

            var subject = ReplacePlaceholders(subjectTemplate, order);
            var body = ReplacePlaceholders(bodyTemplate, order);

            await SendSmtpEmailAsync(_settings.NotificationFromEmail, order.CustomerEmail, subject, body);
            return new EmailResult { Success = true };
        }
        catch (Exception ex)
        {
            AlertCollector.Error(AlertCategory.Network,
                "Failed to send notification email",
                orderId: order.ExternalOrderId,
                detail: $"Attempted: send email to '{order.CustomerEmail}' via {_settings.SmtpHost}:{_settings.SmtpPort}. " +
                        $"Expected: email sent. Found: exception. " +
                        $"State: order {order.ExternalOrderId}, customer {order.CustomerFirstName} {order.CustomerLastName}.",
                ex: ex);
            return new EmailResult { Success = false, ErrorMessage = $"{ex.GetType().Name}: {ex.Message}" };
        }
    }

    private async Task SendSmtpEmailAsync(string from, string to, string subject, string body)
    {
        var message = new MimeMessage();
        message.From.Add(MailboxAddress.Parse(from));
        message.To.Add(MailboxAddress.Parse(to));
        message.Subject = subject;
        message.Body = new TextPart("plain") { Text = body };

        using var client = new SmtpClient();
        client.Timeout = 15000;
        await client.ConnectAsync(_settings.SmtpHost, _settings.SmtpPort, SecureSocketOptions.SslOnConnect);
        await client.AuthenticateAsync(_settings.SmtpUsername, _settings.SmtpPassword);
        await client.SendAsync(message);
        await client.DisconnectAsync(true);
    }

    public static string ReplacePlaceholders(string template, Order order)
    {
        var shortId = OrderHelpers.GetShortId(order.ExternalOrderId);

        return template
            .Replace("{CustomerName}", $"{order.CustomerFirstName} {order.CustomerLastName}".Trim())
            .Replace("{StoreName}", order.StoreName ?? "")
            .Replace("{OrderId}", shortId)
            .Replace("{OrderDate}", order.OrderedAt?.ToString("MM/dd/yyyy") ?? "")
            .Replace("{Email}", order.CustomerEmail ?? "")
            .Replace("{Phone}", order.CustomerPhone ?? "");
    }
}
