using Npgsql;
using ImajinationAPI.Controllers;

namespace ImajinationAPI.Services
{
    /// <summary>
    /// Background service that runs once per hour and sends email reminders
    /// to ticket holders: 48h before an event and 24h before an event.
    /// </summary>
    public class EventReminderService : BackgroundService
    {
        private readonly IServiceScopeFactory _scopeFactory;
        private readonly ILogger<EventReminderService> _logger;
        private readonly string _connectionString;
        private readonly EmailService _emailService;

        public EventReminderService(
            IConfiguration configuration,
            ILogger<EventReminderService> logger,
            EmailService emailService)
        {
            _logger = logger;
            _emailService = emailService;
            _connectionString = ConfigurationFallbacks.GetRequiredSupabaseConnectionString(configuration);
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            _logger.LogInformation("[EventReminder] Service started.");

            // Stagger startup by 60s so the app can finish booting first
            await Task.Delay(TimeSpan.FromSeconds(60), stoppingToken);

            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    await SendPendingRemindersAsync(stoppingToken);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "[EventReminder] Error during reminder cycle.");
                }

                // Run every 60 minutes
                await Task.Delay(TimeSpan.FromHours(1), stoppingToken);
            }
        }

        private async Task SendPendingRemindersAsync(CancellationToken ct)
        {
            await using var connection = new NpgsqlConnection(_connectionString);
            await connection.OpenAsync(ct);

            await EnsureSchemaAsync(connection);

            // Find ticket holders for events happening in 24-49h that haven't been reminded yet
            const string sql = @"
                SELECT DISTINCT
                    t.id AS ticket_id,
                    t.customer_id,
                    COALESCE(u.email, '') AS customer_email,
                    COALESCE(u.firstname, '') AS customer_name,
                    e.id AS event_id,
                    COALESCE(e.title, 'Event') AS event_title,
                    e.event_time,
                    COALESCE(e.location, '') AS location,
                    COALESCE(e.city, '') AS city,
                    EXTRACT(EPOCH FROM (e.event_time - NOW())) / 3600 AS hours_until
                FROM tickets t
                INNER JOIN events e ON e.id = t.event_id
                LEFT JOIN users u ON u.id = t.customer_id
                LEFT JOIN event_reminder_log rl ON rl.ticket_id = t.id AND rl.reminder_type = CASE
                    WHEN EXTRACT(EPOCH FROM (e.event_time - NOW())) / 3600 BETWEEN 23 AND 25 THEN '24h'
                    ELSE '48h'
                END
                WHERE EXISTS (
                          SELECT 1 FROM payment_records pr
                          WHERE pr.ticket_id = t.id AND LOWER(pr.status) = 'paid'
                      )
                  AND LOWER(COALESCE(e.status, '')) NOT IN ('finished', 'cancelled')
                  AND e.event_time > NOW()
                  AND e.event_time <= NOW() + INTERVAL '49 hours'
                  AND u.email IS NOT NULL
                  AND u.email <> ''
                  AND rl.id IS NULL
                ORDER BY e.event_time ASC
                LIMIT 100;";

            await using var cmd = new NpgsqlCommand(sql, connection);
            var reminders = new List<(Guid ticketId, Guid customerId, string email, string name,
                Guid eventId, string title, DateTime eventTime, string location, string city, double hours)>();

            await using var rdr = await cmd.ExecuteReaderAsync(ct);
            while (await rdr.ReadAsync(ct))
            {
                reminders.Add((
                    rdr.GetGuid(0), rdr.GetGuid(1),
                    rdr.GetString(2), rdr.GetString(3),
                    rdr.GetGuid(4), rdr.GetString(5),
                    rdr.GetDateTime(6), rdr.GetString(7),
                    rdr.GetString(8), rdr.GetDouble(9)
                ));
            }
            await rdr.CloseAsync();

            _logger.LogInformation("[EventReminder] Sending {Count} reminders.", reminders.Count);

            foreach (var r in reminders)
            {
                ct.ThrowIfCancellationRequested();
                var reminderType = r.hours <= 25 ? "24h" : "48h";
                var hoursBeforeEvent = r.hours <= 25 ? 24 : 48;

                try
                {
                    await _emailService.SendEventReminderEmailAsync(
                        r.email, r.name, r.title, r.eventTime,
                        r.location, r.city, hoursBeforeEvent);

                    // Record that this reminder was sent
                    const string logSql = @"
                        INSERT INTO event_reminder_log (ticket_id, event_id, reminder_type, sent_at)
                        VALUES (@ticketId, @eventId, @type, NOW())
                        ON CONFLICT DO NOTHING;";
                    await using var logCmd = new NpgsqlCommand(logSql, connection);
                    logCmd.Parameters.AddWithValue("@ticketId", r.ticketId);
                    logCmd.Parameters.AddWithValue("@eventId", r.eventId);
                    logCmd.Parameters.AddWithValue("@type", reminderType);
                    await logCmd.ExecuteNonQueryAsync(ct);

                    _logger.LogDebug("[EventReminder] Sent {Type} reminder for '{Title}' to {Email}.", reminderType, r.title, r.email);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "[EventReminder] Failed to send reminder for ticket {TicketId}.", r.ticketId);
                }
            }
        }

        private static async Task EnsureSchemaAsync(NpgsqlConnection connection)
        {
            const string sql = @"
                CREATE TABLE IF NOT EXISTS event_reminder_log (
                    id uuid PRIMARY KEY DEFAULT gen_random_uuid(),
                    ticket_id uuid NOT NULL,
                    event_id uuid NOT NULL,
                    reminder_type varchar(10) NOT NULL,
                    sent_at timestamptz NOT NULL DEFAULT NOW(),
                    UNIQUE (ticket_id, reminder_type)
                );";
            await using var cmd = new NpgsqlCommand(sql, connection);
            await cmd.ExecuteNonQueryAsync();
        }
    }
}
