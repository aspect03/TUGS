using ImajinationAPI.Services;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Npgsql;
using NpgsqlTypes;

namespace ImajinationAPI.Controllers
{
    [ApiController]
    [AllowAnonymous]
    [Route("api/ticket/paymongo")]
    public sealed class PayMongoWebhookController : ControllerBase
    {
        private const int MaxWebhookBodyBytes = 256 * 1024;
        private static readonly TimeSpan MaxWebhookAge = TimeSpan.FromMinutes(5);
        private readonly string _connectionString;
        private readonly string _webhookSecret;
        private readonly bool _isTestMode;
        private readonly ILogger<PayMongoWebhookController> _logger;

        public PayMongoWebhookController(IConfiguration configuration, ILogger<PayMongoWebhookController> logger)
        {
            _connectionString = ConfigurationFallbacks.GetRequiredSupabaseConnectionString(configuration);
            _webhookSecret = configuration["PayMongo:WebhookSecret"] ?? string.Empty;
            _isTestMode = (configuration["PayMongo:SecretKey"] ?? string.Empty).StartsWith("sk_test_", StringComparison.Ordinal);
            _logger = logger;
        }

        [HttpPost("webhook")]
        [Consumes("application/json")]
        public async Task<IActionResult> Receive(CancellationToken cancellationToken)
        {
            if (string.IsNullOrWhiteSpace(_webhookSecret))
            {
                _logger.LogError("PayMongo webhook received before PayMongo:WebhookSecret was configured.");
                return StatusCode(StatusCodes.Status503ServiceUnavailable);
            }

            if (Request.ContentLength is > MaxWebhookBodyBytes)
            {
                return StatusCode(StatusCodes.Status413PayloadTooLarge);
            }

            await using var body = new MemoryStream();
            await Request.Body.CopyToAsync(body, cancellationToken);
            var rawBody = body.ToArray();
            if (rawBody.Length == 0 || rawBody.Length > MaxWebhookBodyBytes ||
                !TryVerifySignature(Request.Headers["Paymongo-Signature"], rawBody))
            {
                return Unauthorized();
            }

            try
            {
                using var document = JsonDocument.Parse(rawBody);
                var root = document.RootElement;
                var eventId = root.GetProperty("data").GetProperty("id").GetString();
                var attributes = root.GetProperty("data").GetProperty("attributes");
                var eventType = attributes.GetProperty("type").GetString();
                var liveMode = attributes.TryGetProperty("livemode", out var liveModeValue) && liveModeValue.ValueKind == JsonValueKind.True;
                var resourceId = attributes.TryGetProperty("data", out var resource) && resource.ValueKind == JsonValueKind.Object && resource.TryGetProperty("id", out var resourceIdValue)
                    ? resourceIdValue.GetString()
                    : null;

                if (string.IsNullOrWhiteSpace(eventId) || string.IsNullOrWhiteSpace(eventType)) return BadRequest();
                if (liveMode == _isTestMode) return BadRequest(new { message = "Payment environment does not match." });

                await using var connection = new NpgsqlConnection(_connectionString);
                await connection.OpenAsync(cancellationToken);
                await EnsureWebhookSchemaAsync(connection, cancellationToken);

                const string insertSql = @"
                    INSERT INTO paymongo_webhook_events (event_id, event_type, provider_resource_id, livemode, received_at)
                    VALUES (@eventId, @eventType, @resourceId, @livemode, NOW())
                    ON CONFLICT (event_id) DO NOTHING;";
                await using var command = new NpgsqlCommand(insertSql, connection);
                command.Parameters.AddWithValue("@eventId", eventId);
                command.Parameters.Add("@eventType", NpgsqlDbType.Text).Value = eventType;
                command.Parameters.Add("@resourceId", NpgsqlDbType.Text).Value = (object?)resourceId ?? DBNull.Value;
                command.Parameters.Add("@livemode", NpgsqlDbType.Boolean).Value = liveMode;
                await command.ExecuteNonQueryAsync(cancellationToken);

                // Retry-safe reconciliation also runs on duplicate delivery, recovering interrupted attempts.
                if (eventType == "checkout_session.payment.paid" && resource.ValueKind == JsonValueKind.Object)
                    await BookingPaymentReconciliationService.ReconcileAsync(connection, resource);
                else if (eventType is "refund.paid" or "refund.pending" or "refund.failed" && resource.ValueKind == JsonValueKind.Object)
                    await ReconcileRefundEventAsync(connection, eventType, resource, cancellationToken);

                _logger.LogInformation("Verified PayMongo webhook {EventType} ({EventId}) received.", eventType, eventId);
                return Ok(new { received = true });
            }
            catch (JsonException)
            {
                return BadRequest();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Unable to record a verified PayMongo webhook.");
                return StatusCode(StatusCodes.Status500InternalServerError);
            }
        }

        private static async Task ReconcileRefundEventAsync(
            NpgsqlConnection connection,
            string eventType,
            JsonElement resource,
            CancellationToken cancellationToken)
        {
            var refundId = resource.TryGetProperty("id", out var idProp) ? idProp.GetString() : null;
            if (string.IsNullOrWhiteSpace(refundId)) return;

            await EscrowService.EnsureSchemaAsync(connection);
            await PaymentLedgerService.EnsureSchemaAsync(connection);
            await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
            const string lookupSql = @"
                SELECT id, refund_scope, payment_scope, booking_id, ticket_id, metadata, status
                FROM refund_requests
                WHERE provider_refund_id = @refundId AND status IN ('Requested', 'ManualReview', 'Refund Pending', 'Refunded', 'Failed')
                ORDER BY created_at DESC LIMIT 1 FOR UPDATE;";
            Guid requestId;
            string refundScope;
            string paymentScope;
            Guid? bookingId;
            Guid? ticketId;
            bool isPartial;
            await using (var lookupCmd = new NpgsqlCommand(lookupSql, connection))
            {
                lookupCmd.Parameters.Add("@refundId", NpgsqlDbType.Text).Value = refundId;
                await using var reader = await lookupCmd.ExecuteReaderAsync();
                if (!await reader.ReadAsync()) return;
                // Replay completed refunds to recover interrupted local settlement. Older
                // pending/failed deliveries must never undo a successful provider refund.
                var storedStatus = reader.GetString(6);
                if (storedStatus == "Refunded") eventType = "refund.paid";
                else if (storedStatus == "Failed" && eventType != "refund.paid") return;
                requestId = reader.GetGuid(0);
                refundScope = reader.GetString(1);
                paymentScope = reader.GetString(2);
                bookingId = reader.IsDBNull(3) ? null : reader.GetGuid(3);
                ticketId = reader.IsDBNull(4) ? null : reader.GetGuid(4);
                isPartial = false;
                if (!reader.IsDBNull(5))
                {
                    try
                    {
                        using var metadata = JsonDocument.Parse(reader.GetString(5));
                        isPartial = metadata.RootElement.TryGetProperty("partialRefund", out var partialProp) &&
                                    partialProp.ValueKind == JsonValueKind.True;
                    }
                    catch (JsonException)
                    {
                        isPartial = false;
                    }
                }
            }

            string requestStatus;
            string providerStatus;
            switch (eventType)
            {
                case "refund.paid":
                    requestStatus = "Refunded";
                    providerStatus = "succeeded";
                    break;
                case "refund.pending":
                    requestStatus = "ManualReview";
                    providerStatus = "pending";
                    break;
                default:
                    requestStatus = "Failed";
                    providerStatus = "failed";
                    break;
            }

            await PaymentRefundService.UpdateRefundRequestAsync(connection, requestId, requestStatus, refundId, providerStatus,
                eventType is "refund.paid" or "refund.pending" ? null : "provider_refund_failed",
                eventType is "refund.paid" or "refund.pending" ? null : "PayMongo reported this refund as failed.");

            if (string.Equals(refundScope, "booking", StringComparison.OrdinalIgnoreCase) && bookingId.HasValue)
            {
                if (requestStatus == "Refunded")
                {
                    await PaymentLedgerService.MarkRefundedAsync(connection, paymentScope, null, bookingId.Value, "Refunded");
                    if (string.Equals(paymentScope, "booking_talent_fee", StringComparison.OrdinalIgnoreCase))
                        await EscrowService.ApplyRefundOutcomeAsync(connection, bookingId.Value, transaction);
                }
                var componentStatus = requestStatus == "Refunded"
                    ? (isPartial ? "Partially Refunded" : "Refunded")
                    : requestStatus == "ManualReview"
                        ? "Refund Pending"
                        : "Refund Failed";
                await SetBookingComponentRefundStatusAsync(connection, bookingId.Value, paymentScope, componentStatus);
                await BookingController.RecomputeBookingPaymentStatusAsync(connection, bookingId.Value);
            }
            else if (string.Equals(refundScope, "ticket", StringComparison.OrdinalIgnoreCase) &&
                     ticketId.HasValue && requestStatus == "Refunded")
            {
                await PaymentLedgerService.MarkRefundedAsync(connection, paymentScope, ticketId.Value, null, "Refunded");
            }
            await transaction.CommitAsync(cancellationToken);
        }

        private static async Task SetBookingComponentRefundStatusAsync(
            NpgsqlConnection connection,
            Guid bookingId,
            string paymentScope,
            string status)
        {
            var column = paymentScope switch
            {
                "booking_talent_fee" => "talent_fee_status",
                "booking_service_fee" => "service_fee_status",
                "booking_talent_platform_fee" => "talent_platform_fee_status",
                _ => null
            };
            if (column is null) return;
            await using var command = new NpgsqlCommand($"UPDATE bookings SET {column} = @status, updated_at = NOW() WHERE id = @id;", connection);
            command.Parameters.Add("@status", NpgsqlDbType.Text).Value = status;
            command.Parameters.Add("@id", NpgsqlDbType.Uuid).Value = bookingId;
            await command.ExecuteNonQueryAsync();
        }

        private bool TryVerifySignature(string? headerValue, byte[] rawBody)
        {
            if (string.IsNullOrWhiteSpace(headerValue)) return false;

            var parts = headerValue.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(part => part.Split('=', 2, StringSplitOptions.None))
                .Where(pair => pair.Length == 2)
                .ToDictionary(pair => pair[0], pair => pair[1], StringComparer.OrdinalIgnoreCase);

            if (!parts.TryGetValue("t", out var timestampText) ||
                !long.TryParse(timestampText, out var unixTimestamp) ||
                Math.Abs(DateTimeOffset.UtcNow.ToUnixTimeSeconds() - unixTimestamp) > MaxWebhookAge.TotalSeconds)
            {
                return false;
            }

            var signatureKey = _isTestMode ? "te" : "li";
            if (!parts.TryGetValue(signatureKey, out var providedSignature) || string.IsNullOrWhiteSpace(providedSignature)) return false;

            var prefix = Encoding.UTF8.GetBytes(timestampText + ".");
            var signedPayload = new byte[prefix.Length + rawBody.Length];
            Buffer.BlockCopy(prefix, 0, signedPayload, 0, prefix.Length);
            Buffer.BlockCopy(rawBody, 0, signedPayload, prefix.Length, rawBody.Length);

            var expectedSignature = HMACSHA256.HashData(Encoding.UTF8.GetBytes(_webhookSecret), signedPayload);
            try
            {
                var providedBytes = Convert.FromHexString(providedSignature);
                return CryptographicOperations.FixedTimeEquals(expectedSignature, providedBytes);
            }
            catch (FormatException)
            {
                return false;
            }
        }

        private static async Task EnsureWebhookSchemaAsync(NpgsqlConnection connection, CancellationToken cancellationToken)
        {
            const string sql = @"
                CREATE TABLE IF NOT EXISTS paymongo_webhook_events (
                    event_id text PRIMARY KEY,
                    event_type text NOT NULL,
                    provider_resource_id text NULL,
                    livemode boolean NOT NULL,
                    received_at timestamptz NOT NULL DEFAULT NOW()
                );";
            await using var command = new NpgsqlCommand(sql, connection);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
    }
}