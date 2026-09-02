using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Authorization;
using ImajinationAPI.Services;
using Npgsql;
using NpgsqlTypes;
using System.Security.Claims;

namespace ImajinationAPI.Controllers
{
    public class CreateMessageRequest
    {
        public Guid senderId { get; set; }
        public string? message { get; set; }
        public string? attachmentDataUrl { get; set; }
        public string? attachmentName { get; set; }
        public string? attachmentType { get; set; }
    }

    [Route("api/[controller]")]
    [ApiController]
    [Authorize]
    public class MessageController : ControllerBase
    {
        private readonly string _connectionString;
        private readonly MessageProtectionService _messageProtection;
        private readonly BookingMessageStreamService _messageStream;

        public MessageController(
            IConfiguration configuration,
            MessageProtectionService messageProtection,
            BookingMessageStreamService messageStream)
        {
            _connectionString = ConfigurationFallbacks.GetRequiredSupabaseConnectionString(configuration);
            _messageProtection = messageProtection;
            _messageStream = messageStream;
        }

        private Guid? GetActorUserId() =>
            Guid.TryParse(User.FindFirstValue(ClaimTypes.NameIdentifier), out var parsedUserId) ? parsedUserId : null;

        private bool IsAdmin() =>
            string.Equals(User.FindFirstValue(ClaimTypes.Role), "Admin", StringComparison.OrdinalIgnoreCase);

        private bool CanAccessUserConversation(Guid targetUserId)
        {
            var actorUserId = GetActorUserId();
            return IsAdmin() || (actorUserId.HasValue && actorUserId.Value == targetUserId);
        }

        private async Task<(bool Found, Guid CustomerId, Guid TargetUserId, string ServiceFeeStatus, string PaymentStatus, string Status, DateTime? EventDate, DateTime? EventEndTime, DateTime? UpdatedAt, DateTime? ConversationClosedAt)> GetBookingAccessAsync(
            NpgsqlConnection connection,
            Guid bookingId)
        {
            const string bookingSql = @"
                SELECT customer_id,
                       target_user_id,
                       COALESCE(service_fee_status, ''),
                       COALESCE(payment_status, ''),
                       COALESCE(status, 'Pending'),
                       event_date,
                       event_end_time,
                       updated_at,
                       conversation_closed_at
                FROM bookings
                WHERE id = @bookingId";

            await using var bookingCmd = new NpgsqlCommand(bookingSql, connection);
            bookingCmd.Parameters.Add("@bookingId", NpgsqlDbType.Uuid).Value = bookingId;

            await using var reader = await bookingCmd.ExecuteReaderAsync();
            if (!await reader.ReadAsync())
            {
                return (false, Guid.Empty, Guid.Empty, string.Empty, string.Empty, string.Empty, null, null, null, null);
            }

            return (
                true,
                reader.GetGuid(0),
                reader.GetGuid(1),
                reader.IsDBNull(2) ? string.Empty : reader.GetString(2),
                reader.IsDBNull(3) ? string.Empty : reader.GetString(3),
                reader.IsDBNull(4) ? "Pending" : reader.GetString(4),
                reader.IsDBNull(5) ? (DateTime?)null : reader.GetDateTime(5),
                reader.IsDBNull(6) ? (DateTime?)null : reader.GetDateTime(6),
                reader.IsDBNull(7) ? (DateTime?)null : reader.GetDateTime(7),
                reader.IsDBNull(8) ? (DateTime?)null : reader.GetDateTime(8));
        }

        private static bool IsCancelledConversationClosed(string bookingStatus, DateTime? bookingUpdatedAtUtc)
        {
            if (string.IsNullOrWhiteSpace(bookingStatus) || !bookingStatus.StartsWith("Cancelled", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            if (!bookingUpdatedAtUtc.HasValue)
            {
                return true;
            }

            return bookingUpdatedAtUtc.Value.AddDays(1) <= DateTime.UtcNow;
        }

        private static bool IsConversationExplicitlyClosed(DateTime? conversationClosedAt) =>
            conversationClosedAt.HasValue;

        [HttpGet("conversations/{userId}")]
        public async Task<IActionResult> GetConversations(Guid userId)
        {
            try
            {
                if (!CanAccessUserConversation(userId))
                {
                    return Forbid();
                }

                var conversations = new List<object>();
                await using var connection = new NpgsqlConnection(_connectionString);
                await connection.OpenAsync();
                await EnsureMessagesTableExists(connection);
                await EnsureBookingMessagingColumnsExist(connection);

                const string sql = @"
                    SELECT
                        b.id,
                        b.customer_id,
                        b.target_user_id,
                        COALESCE(b.target_role, ''),
                        COALESCE(b.event_title, ''),
                        COALESCE(c.firstname, '') AS customer_firstname,
                        COALESCE(c.lastname, '') AS customer_lastname,
                        COALESCE(t.firstname, '') AS target_firstname,
                        COALESCE(t.lastname, '') AS target_lastname,
                        COALESCE(t.stagename, '') AS target_stage_name,
                        COALESCE((
                            SELECT m.message_text
                            FROM booking_messages m
                            WHERE m.booking_id = b.id
                            ORDER BY m.created_at DESC
                            LIMIT 1
                        ), b.message, '') AS last_message,
                        COALESCE((
                            SELECT m.created_at
                            FROM booking_messages m
                            WHERE m.booking_id = b.id
                            ORDER BY m.created_at DESC
                            LIMIT 1
                        ), b.created_at) AS last_activity,
                        EXISTS (
                            SELECT 1
                            FROM booking_messages m
                            WHERE m.booking_id = b.id
                        ) AS has_messages,
                        COALESCE(b.status, 'Pending') AS booking_status,
                        b.updated_at
                    FROM bookings b
                    LEFT JOIN users c ON c.id = b.customer_id
                    LEFT JOIN users t ON t.id = b.target_user_id
                    WHERE b.customer_id = @userId OR b.target_user_id = @userId
                    ORDER BY last_activity DESC;";

                using var cmd = new NpgsqlCommand(sql, connection);
                cmd.Parameters.Add("@userId", NpgsqlDbType.Uuid).Value = userId;

                using var reader = await cmd.ExecuteReaderAsync();
                while (await reader.ReadAsync())
                {
                    var customerId = reader.GetGuid(1);
                    var targetUserId = reader.GetGuid(2);
                    var isCustomer = customerId == userId;
                    var customerName = $"{(reader.IsDBNull(5) ? "" : reader.GetString(5))} {(reader.IsDBNull(6) ? "" : reader.GetString(6))}".Trim();
                    var targetStage = reader.IsDBNull(9) ? "" : reader.GetString(9);
                    var targetName = !string.IsNullOrWhiteSpace(targetStage)
                        ? targetStage
                        : $"{(reader.IsDBNull(7) ? "" : reader.GetString(7))} {(reader.IsDBNull(8) ? "" : reader.GetString(8))}".Trim();
                    var bookingStatus = reader.IsDBNull(13) ? "Pending" : reader.GetString(13);
                    var updatedAt = reader.IsDBNull(14) ? (DateTime?)null : reader.GetDateTime(14);
                    if (IsCancelledConversationClosed(bookingStatus, updatedAt))
                    {
                        continue;
                    }

                    var decryptedLastMessage = _messageProtection.Unprotect(reader.IsDBNull(10) ? "" : reader.GetString(10));

                    conversations.Add(new
                    {
                        bookingId = reader.GetGuid(0),
                        counterpartName = isCustomer ? targetName : customerName,
                        counterpartId = isCustomer ? targetUserId : customerId,
                        counterpartRole = isCustomer ? (reader.IsDBNull(3) ? "" : reader.GetString(3)) : "Customer",
                        eventTitle = reader.IsDBNull(4) ? "" : reader.GetString(4),
                        lastMessage = decryptedLastMessage,
                        lastActivity = reader.IsDBNull(11) ? DateTime.UtcNow : reader.GetDateTime(11),
                        hasMessages = !reader.IsDBNull(12) && reader.GetBoolean(12)
                    });
                }

                return Ok(conversations);
            }
            catch (Exception)
            {
                return StatusCode(500, new { message = "Failed to load conversations." });
            }
        }

        [HttpGet("booking/{bookingId}")]
        public async Task<IActionResult> GetMessagesForBooking(Guid bookingId)
        {
            try
            {
                var messages = new List<object>();
                await using var connection = new NpgsqlConnection(_connectionString);
                await connection.OpenAsync();
                await EnsureMessagesTableExists(connection);
                await EnsureBookingMessagingColumnsExist(connection);

                var bookingAccess = await GetBookingAccessAsync(connection, bookingId);
                if (!bookingAccess.Found)
                {
                    return NotFound(new { message = "Booking request not found." });
                }

                var actorUserId = GetActorUserId();
                var isParticipant = actorUserId.HasValue
                    && (actorUserId.Value == bookingAccess.CustomerId || actorUserId.Value == bookingAccess.TargetUserId);
                if (!IsAdmin() && !isParticipant)
                {
                    return Forbid();
                }

                var normalizedServiceFee = NormalizeServiceFeeStatus(bookingAccess.ServiceFeeStatus, bookingAccess.PaymentStatus);
                if (!string.Equals(normalizedServiceFee, "Paid", StringComparison.OrdinalIgnoreCase)
                    && !string.Equals(normalizedServiceFee, "NotRequired", StringComparison.OrdinalIgnoreCase))
                {
                    return StatusCode(403, new { message = "Pay the booking service fee first before opening messages." });
                }

                if (IsCancelledConversationClosed(bookingAccess.Status, bookingAccess.UpdatedAt))
                {
                    return StatusCode(403, new { message = "This cancelled booking thread closed after the 1-day grace period." });
                }

                const string sql = @"
                    SELECT id, sender_id, receiver_id, message_text, created_at, attachment_data_url, attachment_name, attachment_type
                    FROM booking_messages
                    WHERE booking_id = @bookingId
                    ORDER BY created_at ASC;";

                using var cmd = new NpgsqlCommand(sql, connection);
                cmd.Parameters.Add("@bookingId", NpgsqlDbType.Uuid).Value = bookingId;

                using var reader = await cmd.ExecuteReaderAsync();
                while (await reader.ReadAsync())
                {
                    messages.Add(new
                    {
                        id = reader.GetGuid(0),
                        senderId = reader.GetGuid(1),
                        receiverId = reader.GetGuid(2),
                        message = _messageProtection.Unprotect(reader.IsDBNull(3) ? "" : reader.GetString(3)),
                        createdAt = reader.IsDBNull(4) ? DateTime.UtcNow : reader.GetDateTime(4),
                        attachmentDataUrl = reader.IsDBNull(5) ? null : reader.GetString(5),
                        attachmentName = reader.IsDBNull(6) ? null : reader.GetString(6),
                        attachmentType = reader.IsDBNull(7) ? null : reader.GetString(7)
                    });
                }

                return Ok(messages);
            }
            catch (Exception)
            {
                return StatusCode(500, new { message = "Failed to load messages." });
            }
        }

        [HttpPost("booking/{bookingId}")]
        public async Task<IActionResult> SendMessage(Guid bookingId, [FromBody] CreateMessageRequest req)
        {
            try
            {
                var actorUserId = GetActorUserId();
                var hasAttachment = !string.IsNullOrWhiteSpace(req.attachmentDataUrl);
                if (!actorUserId.HasValue || (string.IsNullOrWhiteSpace(req.message) && !hasAttachment))
                {
                    return BadRequest(new { message = "Sender and message or attachment are required." });
                }

                await using var connection = new NpgsqlConnection(_connectionString);
                await connection.OpenAsync();
                await EnsureMessagesTableExists(connection);
                await EnsureBookingMessagingColumnsExist(connection);

                if (req.senderId != Guid.Empty && req.senderId != actorUserId.Value)
                {
                    return Forbid();
                }

                var bookingAccess = await GetBookingAccessAsync(connection, bookingId);
                if (!bookingAccess.Found)
                {
                    return NotFound(new { message = "Booking request not found." });
                }

                var isParticipant = actorUserId.Value == bookingAccess.CustomerId || actorUserId.Value == bookingAccess.TargetUserId;
                if (!isParticipant)
                {
                    return Forbid();
                }

                var normalizedServiceFee = NormalizeServiceFeeStatus(bookingAccess.ServiceFeeStatus, bookingAccess.PaymentStatus);
                if (!string.Equals(normalizedServiceFee, "Paid", StringComparison.OrdinalIgnoreCase)
                    && !string.Equals(normalizedServiceFee, "NotRequired", StringComparison.OrdinalIgnoreCase))
                {
                    return StatusCode(403, new { message = "Pay the booking service fee first before sending messages." });
                }

                if (IsCancelledConversationClosed(bookingAccess.Status, bookingAccess.UpdatedAt))
                {
                    return StatusCode(403, new { message = "This cancelled booking thread is already closed." });
                }

                if (IsConversationExplicitlyClosed(bookingAccess.ConversationClosedAt))
                {
                    return StatusCode(403, new { message = "This booking conversation is closed." });
                }

                var senderId = actorUserId.Value;
                var receiverId = senderId == bookingAccess.CustomerId ? bookingAccess.TargetUserId : bookingAccess.CustomerId;
                var createdAt = DateTime.UtcNow;
                var messageId = Guid.NewGuid();
                var cleanMessage = (req.message ?? string.Empty).Trim();
                var attachmentError = ValidateAttachment(req.attachmentDataUrl, req.attachmentType);
                if (attachmentError is not null)
                {
                    return BadRequest(new { message = attachmentError });
                }
                var cleanAttachmentName = SecuritySupport.SanitizePlainText(req.attachmentName, 180, false);
                var cleanAttachmentType = SecuritySupport.SanitizePlainText(req.attachmentType, 80, false);

                const string sql = @"
                    INSERT INTO booking_messages (id, booking_id, sender_id, receiver_id, message_text, created_at, attachment_data_url, attachment_name, attachment_type)
                    VALUES (@id, @bookingId, @senderId, @receiverId, @messageText, @createdAt, @attachmentDataUrl, @attachmentName, @attachmentType);";

                using var cmd = new NpgsqlCommand(sql, connection);
                cmd.Parameters.Add("@id", NpgsqlDbType.Uuid).Value = messageId;
                cmd.Parameters.Add("@bookingId", NpgsqlDbType.Uuid).Value = bookingId;
                cmd.Parameters.Add("@senderId", NpgsqlDbType.Uuid).Value = senderId;
                cmd.Parameters.Add("@receiverId", NpgsqlDbType.Uuid).Value = receiverId;
                cmd.Parameters.Add("@messageText", NpgsqlDbType.Text).Value = _messageProtection.Protect(cleanMessage);
                cmd.Parameters.Add("@createdAt", NpgsqlDbType.TimestampTz).Value = createdAt;
                cmd.Parameters.Add("@attachmentDataUrl", NpgsqlDbType.Text).Value = (object?)req.attachmentDataUrl ?? DBNull.Value;
                cmd.Parameters.Add("@attachmentName", NpgsqlDbType.Text).Value = string.IsNullOrWhiteSpace(cleanAttachmentName) ? DBNull.Value : cleanAttachmentName;
                cmd.Parameters.Add("@attachmentType", NpgsqlDbType.Text).Value = string.IsNullOrWhiteSpace(cleanAttachmentType) ? DBNull.Value : cleanAttachmentType;
                await cmd.ExecuteNonQueryAsync();

                var messagePayload = new
                {
                    id = messageId,
                    senderId,
                    receiverId,
                    message = cleanMessage,
                    createdAt,
                    attachmentDataUrl = req.attachmentDataUrl,
                    attachmentName = cleanAttachmentName,
                    attachmentType = cleanAttachmentType
                };

                await _messageStream.PublishAsync(bookingId, new
                {
                    type = "message_created",
                    bookingId,
                    message = messagePayload
                }, HttpContext.RequestAborted);

                return Ok(new
                {
                    message = "Message sent successfully.",
                    sentMessage = messagePayload
                });
            }
            catch (Exception)
            {
                return StatusCode(500, new { message = "Failed to send message." });
            }
        }

        [HttpGet("booking/{bookingId}/stream")]
        public async Task StreamMessages(Guid bookingId)
        {
            await using var connection = new NpgsqlConnection(_connectionString);
            await connection.OpenAsync(HttpContext.RequestAborted);
            await EnsureMessagesTableExists(connection);
            await EnsureBookingMessagingColumnsExist(connection);

            var bookingAccess = await GetBookingAccessAsync(connection, bookingId);
            if (!bookingAccess.Found)
            {
                Response.StatusCode = StatusCodes.Status404NotFound;
                await Response.WriteAsJsonAsync(new { message = "Booking request not found." }, HttpContext.RequestAborted);
                return;
            }

            var actorUserId = GetActorUserId();
            var isParticipant = actorUserId.HasValue
                && (actorUserId.Value == bookingAccess.CustomerId || actorUserId.Value == bookingAccess.TargetUserId);
            if (!isParticipant)
            {
                Response.StatusCode = StatusCodes.Status403Forbidden;
                await Response.WriteAsJsonAsync(new { message = "You do not have access to this conversation." }, HttpContext.RequestAborted);
                return;
            }

            var normalizedServiceFee = NormalizeServiceFeeStatus(bookingAccess.ServiceFeeStatus, bookingAccess.PaymentStatus);
            if (!string.Equals(normalizedServiceFee, "Paid", StringComparison.OrdinalIgnoreCase)
                && !string.Equals(normalizedServiceFee, "NotRequired", StringComparison.OrdinalIgnoreCase))
            {
                Response.StatusCode = StatusCodes.Status403Forbidden;
                await Response.WriteAsJsonAsync(new { message = "Pay the booking service fee first before opening messages." }, HttpContext.RequestAborted);
                return;
            }

            if (IsCancelledConversationClosed(bookingAccess.Status, bookingAccess.UpdatedAt))
            {
                Response.StatusCode = StatusCodes.Status403Forbidden;
                await Response.WriteAsJsonAsync(new { message = "This cancelled booking thread closed after the 1-day grace period." }, HttpContext.RequestAborted);
                return;
            }

            if (IsConversationExplicitlyClosed(bookingAccess.ConversationClosedAt))
            {
                Response.StatusCode = StatusCodes.Status403Forbidden;
                await Response.WriteAsJsonAsync(new { message = "This booking conversation is closed." }, HttpContext.RequestAborted);
                return;
            }

            Response.StatusCode = StatusCodes.Status200OK;
            Response.ContentType = "text/event-stream";
            Response.Headers["Cache-Control"] = "no-cache";
            Response.Headers.Append("X-Accel-Buffering", "no");

            await using var subscription = _messageStream.Subscribe(bookingId);
            await Response.WriteAsync("event: connected\ndata: {\"connected\":true}\n\n", HttpContext.RequestAborted);
            await Response.Body.FlushAsync(HttpContext.RequestAborted);

            try
            {
                while (!HttpContext.RequestAborted.IsCancellationRequested)
                {
                    var readTask = subscription.Reader.ReadAsync(HttpContext.RequestAborted).AsTask();
                    var heartbeatTask = Task.Delay(TimeSpan.FromSeconds(20), HttpContext.RequestAborted);
                    var completedTask = await Task.WhenAny(readTask, heartbeatTask);

                    if (completedTask == readTask)
                    {
                        var payload = await readTask;
                        await Response.WriteAsync($"event: booking-message\ndata: {payload}\n\n", HttpContext.RequestAborted);
                    }
                    else
                    {
                        await Response.WriteAsync(": keepalive\n\n", HttpContext.RequestAborted);
                    }

                    await Response.Body.FlushAsync(HttpContext.RequestAborted);
                }
            }
            catch (OperationCanceledException)
            {
            }
            catch (IOException)
            {
            }
            catch (ObjectDisposedException)
            {
            }
        }

        private static async Task EnsureMessagesTableExists(NpgsqlConnection connection)
        {
            const string sql = @"
                CREATE TABLE IF NOT EXISTS booking_messages (
                    id uuid PRIMARY KEY,
                    booking_id uuid NOT NULL,
                    sender_id uuid NOT NULL,
                    receiver_id uuid NOT NULL,
                    message_text text NOT NULL,
                    attachment_data_url text NULL,
                    attachment_name text NULL,
                    attachment_type text NULL,
                    created_at timestamptz NOT NULL DEFAULT NOW()
                );";

            using var cmd = new NpgsqlCommand(sql, connection);
            await cmd.ExecuteNonQueryAsync();

            const string alterSql = @"
                ALTER TABLE booking_messages ADD COLUMN IF NOT EXISTS attachment_data_url text NULL;
                ALTER TABLE booking_messages ADD COLUMN IF NOT EXISTS attachment_name text NULL;
                ALTER TABLE booking_messages ADD COLUMN IF NOT EXISTS attachment_type text NULL;";
            using var alterCmd = new NpgsqlCommand(alterSql, connection);
            await alterCmd.ExecuteNonQueryAsync();
        }

        private static string? ValidateAttachment(string? dataUrl, string? contentType)
        {
            if (string.IsNullOrWhiteSpace(dataUrl)) return null;
            var allowedTypes = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "image/png",
                "image/jpeg",
                "image/webp",
                "application/pdf",
                "text/plain"
            };
            var type = (contentType ?? string.Empty).Trim();
            if (!allowedTypes.Contains(type))
            {
                return "Attachments must be PNG, JPG, WEBP, PDF, or plain text.";
            }
            if (!dataUrl.StartsWith($"data:{type};base64,", StringComparison.OrdinalIgnoreCase))
            {
                return "Attachment data is invalid.";
            }
            if (dataUrl.Length > 3_600_000)
            {
                return "Attachment is too large. Use a file under about 2.5MB.";
            }
            return null;
        }

        private static async Task EnsureBookingMessagingColumnsExist(NpgsqlConnection connection)
        {
            const string sql = @"
                ALTER TABLE bookings ADD COLUMN IF NOT EXISTS payment_status varchar(30) NOT NULL DEFAULT 'Unpaid';
                ALTER TABLE bookings ADD COLUMN IF NOT EXISTS service_fee_status varchar(30) NOT NULL DEFAULT 'Unpaid';
                ALTER TABLE bookings ADD COLUMN IF NOT EXISTS event_end_time timestamptz NULL;
                ALTER TABLE bookings ADD COLUMN IF NOT EXISTS conversation_closed_at timestamptz NULL;
                ALTER TABLE bookings ADD COLUMN IF NOT EXISTS conversation_closed_by_user_id uuid NULL;
                ALTER TABLE bookings ADD COLUMN IF NOT EXISTS conversation_close_reason text NOT NULL DEFAULT '';
                ALTER TABLE bookings ADD COLUMN IF NOT EXISTS updated_at timestamptz NOT NULL DEFAULT NOW();
                UPDATE bookings
                SET updated_at = COALESCE(updated_at, created_at, NOW())
                WHERE updated_at IS NULL;";

            using var cmd = new NpgsqlCommand(sql, connection);
            await cmd.ExecuteNonQueryAsync();
        }

        private static string NormalizeServiceFeeStatus(string? serviceFeeStatus, string? paymentStatus)
        {
            var rawServiceStatus = (serviceFeeStatus ?? string.Empty).Trim();
            var rawPaymentStatus = (paymentStatus ?? string.Empty).Trim();

            if (string.IsNullOrWhiteSpace(rawServiceStatus))
            {
                if (string.Equals(rawPaymentStatus, "ServiceFeePaid", StringComparison.OrdinalIgnoreCase))
                {
                    return "Paid";
                }

                return string.IsNullOrWhiteSpace(rawPaymentStatus) ? "Unpaid" : rawPaymentStatus;
            }

            return string.Equals(rawServiceStatus, "ServiceFeePaid", StringComparison.OrdinalIgnoreCase)
                ? "Paid"
                : rawServiceStatus;
        }
    }
}
