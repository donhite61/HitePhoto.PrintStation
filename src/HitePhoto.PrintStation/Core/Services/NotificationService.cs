using Microsoft.Data.Sqlite;
using HitePhoto.PrintStation.Core.Processing;
using HitePhoto.PrintStation.Data;

namespace HitePhoto.PrintStation.Core.Services;

public class NotificationService : INotificationService
{
    private readonly OrderDb _db;
    private readonly EmailService _emailService;
    private readonly PixfizzNotifier _pixfizzNotifier;
    private readonly AppSettings _settings;

    public NotificationService(OrderDb db, EmailService emailService,
        PixfizzNotifier pixfizzNotifier, AppSettings settings)
    {
        _db = db ?? throw new ArgumentNullException(nameof(db));
        _emailService = emailService ?? throw new ArgumentNullException(nameof(emailService));
        _pixfizzNotifier = pixfizzNotifier ?? throw new ArgumentNullException(nameof(pixfizzNotifier));
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
    }

    public void NotifyCustomer(int orderId, string operatorName)
    {
        using var conn = _db.OpenConnection();

        // Load order info needed for notification
        string source, customerEmail, externalOrderId;
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = """
                SELECT source_code, customer_email, external_order_id
                FROM orders WHERE id = @id
                """;
            cmd.Parameters.AddWithValue("@id", orderId);
            using var reader = cmd.ExecuteReader();
            if (!reader.Read())
                throw new InvalidOperationException($"Order {orderId} not found.");
            source = reader.GetString(0);
            customerEmail = reader.IsDBNull(1) ? "" : reader.GetString(1);
            externalOrderId = reader.GetString(2);
        }

        // Decide notification method
        var usePixfizz = source.Equals("pixfizz", StringComparison.OrdinalIgnoreCase)
            && _settings.PixfizzNotifyMode.Equals("Pixfizz", StringComparison.OrdinalIgnoreCase);

        string method;
        if (usePixfizz)
        {
            // TODO: need pixfizz job_id to call API — load from order or items
            // _pixfizzNotifier.MarkCompletedAsync(jobId);
            method = "Pixfizz";
        }
        else
        {
            if (string.IsNullOrEmpty(customerEmail))
            {
                AlertCollector.Warn(AlertCategory.Network,
                    $"Cannot notify — no email address for order {externalOrderId}",
                    orderId: externalOrderId);
                return;
            }
            // TODO: call _emailService with order details
            method = "Email";
        }

        // Mark notified
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = """
                UPDATE orders SET is_notified = 1, updated_at = datetime('now')
                WHERE id = @id
                """;
            cmd.Parameters.AddWithValue("@id", orderId);
            cmd.ExecuteNonQuery();
        }

        // History note
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = """
                INSERT INTO order_history (order_id, note, created_by, created_at)
                VALUES (@id, @note, @by, datetime('now'))
                """;
            cmd.Parameters.AddWithValue("@id", orderId);
            cmd.Parameters.AddWithValue("@note", $"Customer notified via {method}");
            cmd.Parameters.AddWithValue("@by", operatorName);
            cmd.ExecuteNonQuery();
        }
    }
}
