using Npgsql;
using NpgsqlTypes;
using System.Text.Json;
using WebPush;

namespace ImajinationAPI.Controllers
{
    internal static class NotificationSupport
    {
        private static IConfiguration? _configuration;

        public static void Configure(IConfiguration configuration)
        {
            _configuration = configuration;
        }

        public static async Task EnsureNotificationsTableExistsAsync(NpgsqlConnection connection)
        {
            const string sql = @"
                CREATE TABLE IF NOT EXISTS notifications (
                    id uuid PRIMARY KEY,
                    user_id uuid NOT NULL,
                    type varchar(60) NOT NULL,
                    title varchar(150) NOT NULL,
                    message text NOT NULL,
                    related_id uuid NULL,
                    related_type varchar(50) NULL,
                    is_read boolean NOT NULL DEFAULT FALSE,
                    created_at timestamptz NOT NULL DEFAULT NOW()
                );";

            await using var cmd = new NpgsqlCommand(sql, connection);
            await cmd.ExecuteNonQueryAsync();
        }

        private static async Task EnsurePushSchemaAsync(NpgsqlConnection connection)
        {
            const string sql = @"
                CREATE TABLE IF NOT EXISTS push_subscriptions (
                    id uuid PRIMARY KEY,
                    user_id uuid NOT NULL,
                    endpoint text NOT NULL,
                    p256dh text NOT NULL,
                    auth text NOT NULL,
                    user_agent text NULL,
                    created_at timestamptz NOT NULL DEFAULT NOW(),
                    last_seen_at timestamptz NOT NULL DEFAULT NOW(),
                    revoked_at timestamptz NULL,
                    UNIQUE (user_id, endpoint)
                );
                CREATE INDEX IF NOT EXISTS idx_push_subscriptions_user_id ON push_subscriptions(user_id);";
            await using var cmd = new NpgsqlCommand(sql, connection);
            await cmd.ExecuteNonQueryAsync();
        }

        private static async Task SendBrowserPushAsync(
            NpgsqlConnection connection,
            Guid userId,
            string type,
            string title,
            string message,
            Guid? relatedId,
            string? relatedType)
        {
            var publicKey = _configuration?["PushNotifications:VapidPublicKey"]
                ?? _configuration?["PushNotifications__VapidPublicKey"];
            var privateKey = _configuration?["PushNotifications:VapidPrivateKey"]
                ?? _configuration?["PushNotifications__VapidPrivateKey"];
            var subject = _configuration?["PushNotifications:VapidSubject"]
                ?? _configuration?["PushNotifications__VapidSubject"]
                ?? "mailto:support@tugs.local";

            if (string.IsNullOrWhiteSpace(publicKey) || string.IsNullOrWhiteSpace(privateKey))
            {
                return;
            }

            await EnsurePushSchemaAsync(connection);

            var subscriptions = new List<(string Endpoint, string P256dh, string Auth)>();
            const string sql = @"
                SELECT endpoint, p256dh, auth
                FROM push_subscriptions
                WHERE user_id = @userId
                  AND revoked_at IS NULL;";

            await using (var cmd = new NpgsqlCommand(sql, connection))
            {
                cmd.Parameters.Add("@userId", NpgsqlDbType.Uuid).Value = userId;
                await using var reader = await cmd.ExecuteReaderAsync();
                while (await reader.ReadAsync())
                {
                    subscriptions.Add((reader.GetString(0), reader.GetString(1), reader.GetString(2)));
                }
            }

            if (subscriptions.Count == 0)
            {
                return;
            }

            var client = new WebPushClient();
            var vapid = new VapidDetails(subject, publicKey, privateKey);
            var payload = JsonSerializer.Serialize(new
            {
                title,
                body = message,
                type,
                relatedId,
                relatedType,
                url = relatedType?.Equals("booking", StringComparison.OrdinalIgnoreCase) == true
                    ? "/pages/bookings/messages.html"
                    : relatedType?.Equals("event", StringComparison.OrdinalIgnoreCase) == true && relatedId.HasValue
                        ? $"/pages/details/EventDetailPage.html?id={relatedId}"
                        : "/"
            });

            foreach (var sub in subscriptions)
            {
                try
                {
                    await client.SendNotificationAsync(
                        new PushSubscription(sub.Endpoint, sub.P256dh, sub.Auth),
                        payload,
                        vapid);
                }
                catch (WebPushException ex) when ((int)ex.StatusCode == 404 || (int)ex.StatusCode == 410)
                {
                    await using var revokeCmd = new NpgsqlCommand(
                        "UPDATE push_subscriptions SET revoked_at = NOW(), last_seen_at = NOW() WHERE user_id = @userId AND endpoint = @endpoint;",
                        connection);
                    revokeCmd.Parameters.Add("@userId", NpgsqlDbType.Uuid).Value = userId;
                    revokeCmd.Parameters.Add("@endpoint", NpgsqlDbType.Text).Value = sub.Endpoint;
                    await revokeCmd.ExecuteNonQueryAsync();
                }
                catch
                {
                    // In-app notifications should still succeed if a browser push endpoint fails.
                }
            }
        }

        public static async Task InsertNotificationAsync(
            NpgsqlConnection connection,
            Guid userId,
            string type,
            string title,
            string message,
            Guid? relatedId = null,
            string? relatedType = null)
        {
            const string sql = @"
                INSERT INTO notifications (id, user_id, type, title, message, related_id, related_type, is_read, created_at)
                VALUES (@id, @userId, @type, @title, @message, @relatedId, @relatedType, FALSE, NOW());";

            await using var cmd = new NpgsqlCommand(sql, connection);
            cmd.Parameters.Add("@id", NpgsqlDbType.Uuid).Value = Guid.NewGuid();
            cmd.Parameters.Add("@userId", NpgsqlDbType.Uuid).Value = userId;
            cmd.Parameters.Add("@type", NpgsqlDbType.Text).Value = type;
            cmd.Parameters.Add("@title", NpgsqlDbType.Text).Value = title;
            cmd.Parameters.Add("@message", NpgsqlDbType.Text).Value = message;
            cmd.Parameters.Add("@relatedId", NpgsqlDbType.Uuid).Value = (object?)relatedId ?? DBNull.Value;
            cmd.Parameters.Add("@relatedType", NpgsqlDbType.Text).Value = (object?)relatedType ?? DBNull.Value;
            await cmd.ExecuteNonQueryAsync();
            await SendBrowserPushAsync(connection, userId, type, title, message, relatedId, relatedType);
        }

        public static async Task InsertNotificationIfNotExistsAsync(
            NpgsqlConnection connection,
            Guid userId,
            string type,
            string title,
            string message,
            Guid? relatedId = null,
            string? relatedType = null,
            int dedupeHours = 24)
        {
            const string sql = @"
                INSERT INTO notifications (id, user_id, type, title, message, related_id, related_type, is_read, created_at)
                SELECT @id, @userId, @type, @title, @message, @relatedId, @relatedType, FALSE, NOW()
                WHERE NOT EXISTS (
                    SELECT 1
                    FROM notifications
                    WHERE user_id = @userId
                      AND type = @type
                      AND COALESCE(related_id, '00000000-0000-0000-0000-000000000000'::uuid) = COALESCE(@relatedId, '00000000-0000-0000-0000-000000000000'::uuid)
                      AND COALESCE(related_type, '') = COALESCE(@relatedType, '')
                      AND created_at >= NOW() - make_interval(hours => @dedupeHours)
                );";

            await using var cmd = new NpgsqlCommand(sql, connection);
            cmd.Parameters.Add("@id", NpgsqlDbType.Uuid).Value = Guid.NewGuid();
            cmd.Parameters.Add("@userId", NpgsqlDbType.Uuid).Value = userId;
            cmd.Parameters.Add("@type", NpgsqlDbType.Text).Value = type;
            cmd.Parameters.Add("@title", NpgsqlDbType.Text).Value = title;
            cmd.Parameters.Add("@message", NpgsqlDbType.Text).Value = message;
            cmd.Parameters.Add("@relatedId", NpgsqlDbType.Uuid).Value = (object?)relatedId ?? DBNull.Value;
            cmd.Parameters.Add("@relatedType", NpgsqlDbType.Text).Value = (object?)relatedType ?? DBNull.Value;
            cmd.Parameters.Add("@dedupeHours", NpgsqlDbType.Integer).Value = dedupeHours;
            var inserted = await cmd.ExecuteNonQueryAsync();
            if (inserted > 0)
            {
                await SendBrowserPushAsync(connection, userId, type, title, message, relatedId, relatedType);
            }
        }

        public static async Task GenerateEventReminderNotificationsAsync(NpgsqlConnection connection, Guid userId)
        {
            await EnsureNotificationsTableExistsAsync(connection);

            var tomorrowStart = DateTime.UtcNow.Date.AddDays(1);
            var tomorrowEnd = tomorrowStart.AddDays(1);

            await GenerateOrganizerEventRemindersAsync(connection, userId, tomorrowStart, tomorrowEnd);
            await GenerateCustomerTicketRemindersAsync(connection, userId, tomorrowStart, tomorrowEnd);
            await GenerateTalentBookingRemindersAsync(connection, userId, tomorrowStart, tomorrowEnd);
        }

        private static async Task GenerateOrganizerEventRemindersAsync(NpgsqlConnection connection, Guid userId, DateTime start, DateTime end)
        {
            const string sql = @"
                SELECT id, COALESCE(title, 'Your event')
                FROM events
                WHERE organizer_id = @userId
                  AND event_time >= @start
                  AND event_time < @end
                  AND COALESCE(status, 'Upcoming') NOT IN ('Cancelled', 'Suspended', 'Finished');";

            await using var cmd = new NpgsqlCommand(sql, connection);
            cmd.Parameters.Add("@userId", NpgsqlDbType.Uuid).Value = userId;
            cmd.Parameters.Add("@start", NpgsqlDbType.TimestampTz).Value = start;
            cmd.Parameters.Add("@end", NpgsqlDbType.TimestampTz).Value = end;

            await using var reader = await cmd.ExecuteReaderAsync();
            var reminders = new List<(Guid Id, string Title)>();
            while (await reader.ReadAsync())
            {
                reminders.Add((reader.GetGuid(0), reader.IsDBNull(1) ? "Your event" : reader.GetString(1)));
            }

            await reader.CloseAsync();
            foreach (var reminder in reminders)
            {
                await InsertNotificationIfNotExistsAsync(
                    connection,
                    userId,
                    "event_reminder",
                    "Event reminder",
                    $"'{reminder.Title}' is happening tomorrow. Review your lineup, tickets, and event setup.",
                    reminder.Id,
                    "event",
                    36);
            }
        }

        private static async Task GenerateCustomerTicketRemindersAsync(NpgsqlConnection connection, Guid userId, DateTime start, DateTime end)
        {
            const string sql = @"
                SELECT DISTINCT e.id, COALESCE(e.title, 'Your event')
                FROM tickets t
                INNER JOIN events e ON e.id = t.event_id
                WHERE t.customer_id = @userId
                  AND e.event_time >= @start
                  AND e.event_time < @end
                  AND COALESCE(t.payment_method, '') <> 'AwaitingPayment'
                  AND COALESCE(e.status, 'Upcoming') NOT IN ('Cancelled', 'Suspended', 'Finished');";

            await using var cmd = new NpgsqlCommand(sql, connection);
            cmd.Parameters.Add("@userId", NpgsqlDbType.Uuid).Value = userId;
            cmd.Parameters.Add("@start", NpgsqlDbType.TimestampTz).Value = start;
            cmd.Parameters.Add("@end", NpgsqlDbType.TimestampTz).Value = end;

            await using var reader = await cmd.ExecuteReaderAsync();
            var reminders = new List<(Guid Id, string Title)>();
            while (await reader.ReadAsync())
            {
                reminders.Add((reader.GetGuid(0), reader.IsDBNull(1) ? "Your event" : reader.GetString(1)));
            }

            await reader.CloseAsync();
            foreach (var reminder in reminders)
            {
                await InsertNotificationIfNotExistsAsync(
                    connection,
                    userId,
                    "ticket_reminder",
                    "Your event is tomorrow",
                    $"Your ticket for '{reminder.Title}' is ready. Make sure you have it ready for entry tomorrow.",
                    reminder.Id,
                    "event",
                    36);
            }
        }

        private static async Task GenerateTalentBookingRemindersAsync(NpgsqlConnection connection, Guid userId, DateTime start, DateTime end)
        {
            const string sql = @"
                SELECT DISTINCT
                    COALESCE(b.event_id, e.id),
                    COALESCE(NULLIF(b.event_title, ''), e.title, 'Your schedule')
                FROM bookings b
                LEFT JOIN events e ON e.id = b.event_id
                WHERE b.target_user_id = @userId
                  AND COALESCE(b.status, '') IN ('Confirmed', 'Completed')
                  AND COALESCE(e.event_time, b.event_date) >= @start
                  AND COALESCE(e.event_time, b.event_date) < @end;";

            await using var cmd = new NpgsqlCommand(sql, connection);
            cmd.Parameters.Add("@userId", NpgsqlDbType.Uuid).Value = userId;
            cmd.Parameters.Add("@start", NpgsqlDbType.TimestampTz).Value = start;
            cmd.Parameters.Add("@end", NpgsqlDbType.TimestampTz).Value = end;

            await using var reader = await cmd.ExecuteReaderAsync();
            var reminders = new List<(Guid? Id, string Title)>();
            while (await reader.ReadAsync())
            {
                reminders.Add((reader.IsDBNull(0) ? null : reader.GetGuid(0), reader.IsDBNull(1) ? "Your schedule" : reader.GetString(1)));
            }

            await reader.CloseAsync();
            foreach (var reminder in reminders)
            {
                await InsertNotificationIfNotExistsAsync(
                    connection,
                    userId,
                    "schedule_reminder",
                    "Schedule reminder",
                    $"'{reminder.Title}' is scheduled for tomorrow. Review the chat and event details before you go.",
                    reminder.Id,
                    reminder.Id.HasValue ? "event" : "booking",
                    36);
            }
        }
    }
}
