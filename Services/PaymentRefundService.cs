using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Npgsql;
using NpgsqlTypes;

namespace ImajinationAPI.Services
{
    public sealed record RefundAttemptResult(
        bool Success,
        string Status,
        string? RefundId,
        string? ProviderStatus,
        string? ErrorCode,
        string? ErrorMessage);

    public static class PaymentRefundService
    {
        public static async Task EnsureSchemaAsync(NpgsqlConnection connection)
        {
            const string sql = @"
                CREATE TABLE IF NOT EXISTS refund_requests (
                    id uuid PRIMARY KEY,
                    refund_scope varchar(30) NOT NULL,
                    payment_scope varchar(80) NOT NULL,
                    booking_id uuid NULL,
                    ticket_id uuid NULL,
                    requester_user_id uuid NULL,
                    beneficiary_user_id uuid NULL,
                    requester_role varchar(30) NULL,
                    reason_code varchar(60) NOT NULL,
                    reason_details text NULL,
                    amount numeric(12,2) NOT NULL DEFAULT 0,
                    currency varchar(10) NOT NULL DEFAULT 'PHP',
                    provider varchar(40) NOT NULL DEFAULT 'PayMongo',
                    provider_payment_id text NULL,
                    provider_refund_id text NULL,
                    status varchar(40) NOT NULL DEFAULT 'Requested',
                    provider_status varchar(40) NULL,
                    error_code varchar(80) NULL,
                    error_message text NULL,
                    metadata jsonb NOT NULL DEFAULT '{}'::jsonb,
                    processed_at timestamptz NULL,
                    created_at timestamptz NOT NULL DEFAULT NOW(),
                    updated_at timestamptz NOT NULL DEFAULT NOW()
                );

                CREATE INDEX IF NOT EXISTS idx_refund_requests_booking_id
                    ON refund_requests(booking_id);

                CREATE INDEX IF NOT EXISTS idx_refund_requests_ticket_id
                    ON refund_requests(ticket_id);

                CREATE INDEX IF NOT EXISTS idx_refund_requests_requester
                    ON refund_requests(requester_user_id);

                CREATE INDEX IF NOT EXISTS idx_refund_requests_status
                    ON refund_requests(status);";

            await using var cmd = new NpgsqlCommand(sql, connection);
            await cmd.ExecuteNonQueryAsync();
        }

        public static async Task<Guid> CreateRefundRequestAsync(
            NpgsqlConnection connection,
            string refundScope,
            string paymentScope,
            Guid? bookingId,
            Guid? ticketId,
            Guid? requesterUserId,
            Guid? beneficiaryUserId,
            string? requesterRole,
            string reasonCode,
            string? reasonDetails,
            decimal amount,
            string? providerPaymentId,
            object? metadata = null)
        {
            await EnsureSchemaAsync(connection);

            const string sql = @"
                INSERT INTO refund_requests (
                    id, refund_scope, payment_scope, booking_id, ticket_id,
                    requester_user_id, beneficiary_user_id, requester_role,
                    reason_code, reason_details, amount, currency, provider,
                    provider_payment_id, metadata, status, created_at, updated_at
                )
                VALUES (
                    @id, @refundScope, @paymentScope, @bookingId, @ticketId,
                    @requesterUserId, @beneficiaryUserId, @requesterRole,
                    @reasonCode, @reasonDetails, @amount, 'PHP', 'PayMongo',
                    @providerPaymentId, @metadata::jsonb, 'Requested', NOW(), NOW()
                );";

            var id = Guid.NewGuid();
            await using var cmd = new NpgsqlCommand(sql, connection);
            cmd.Parameters.Add("@id", NpgsqlDbType.Uuid).Value = id;
            cmd.Parameters.Add("@refundScope", NpgsqlDbType.Text).Value = refundScope;
            cmd.Parameters.Add("@paymentScope", NpgsqlDbType.Text).Value = paymentScope;
            cmd.Parameters.Add("@bookingId", NpgsqlDbType.Uuid).Value = (object?)bookingId ?? DBNull.Value;
            cmd.Parameters.Add("@ticketId", NpgsqlDbType.Uuid).Value = (object?)ticketId ?? DBNull.Value;
            cmd.Parameters.Add("@requesterUserId", NpgsqlDbType.Uuid).Value = (object?)requesterUserId ?? DBNull.Value;
            cmd.Parameters.Add("@beneficiaryUserId", NpgsqlDbType.Uuid).Value = (object?)beneficiaryUserId ?? DBNull.Value;
            cmd.Parameters.Add("@requesterRole", NpgsqlDbType.Text).Value = (object?)requesterRole ?? DBNull.Value;
            cmd.Parameters.Add("@reasonCode", NpgsqlDbType.Text).Value = reasonCode;
            cmd.Parameters.Add("@reasonDetails", NpgsqlDbType.Text).Value = (object?)reasonDetails ?? DBNull.Value;
            cmd.Parameters.Add("@amount", NpgsqlDbType.Numeric).Value = amount;
            cmd.Parameters.Add("@providerPaymentId", NpgsqlDbType.Text).Value = (object?)providerPaymentId ?? DBNull.Value;
            cmd.Parameters.Add("@metadata", NpgsqlDbType.Jsonb).Value = JsonSerializer.Serialize(metadata ?? new { });
            await cmd.ExecuteNonQueryAsync();
            return id;
        }

        public static async Task UpdateRefundRequestAsync(
            NpgsqlConnection connection,
            Guid refundRequestId,
            string status,
            string? providerRefundId,
            string? providerStatus,
            string? errorCode,
            string? errorMessage)
        {
            const string sql = @"
                UPDATE refund_requests
                SET status = @status,
                    provider_refund_id = COALESCE(@providerRefundId, provider_refund_id),
                    provider_status = COALESCE(@providerStatus, provider_status),
                    error_code = @errorCode,
                    error_message = @errorMessage,
                    processed_at = CASE WHEN @status IN ('Refunded', 'ManualReview', 'Failed') THEN NOW() ELSE processed_at END,
                    updated_at = NOW()
                WHERE id = @id;";

            await using var cmd = new NpgsqlCommand(sql, connection);
            cmd.Parameters.Add("@id", NpgsqlDbType.Uuid).Value = refundRequestId;
            cmd.Parameters.Add("@status", NpgsqlDbType.Text).Value = status;
            cmd.Parameters.Add("@providerRefundId", NpgsqlDbType.Text).Value = (object?)providerRefundId ?? DBNull.Value;
            cmd.Parameters.Add("@providerStatus", NpgsqlDbType.Text).Value = (object?)providerStatus ?? DBNull.Value;
            cmd.Parameters.Add("@errorCode", NpgsqlDbType.Text).Value = (object?)errorCode ?? DBNull.Value;
            cmd.Parameters.Add("@errorMessage", NpgsqlDbType.Text).Value = (object?)errorMessage ?? DBNull.Value;
            await cmd.ExecuteNonQueryAsync();
        }

        public static async Task<RefundAttemptResult> CreatePayMongoRefundAsync(
            string secretKey,
            string paymentId,
            decimal amount,
            string reasonCode,
            string? notes)
        {
            if (string.IsNullOrWhiteSpace(secretKey))
            {
                return new RefundAttemptResult(false, "Failed", null, null, "missing_secret_key", "PayMongo is not configured yet.");
            }

            if (string.IsNullOrWhiteSpace(paymentId))
            {
                return new RefundAttemptResult(false, "Failed", null, null, "missing_payment_id", "This payment is missing its PayMongo payment id.");
            }

            var notesText = string.IsNullOrWhiteSpace(notes)
                ? null
                : (notes!.Length > 255 ? notes[..255] : notes);
            var amountInCentavos = Math.Max(1, (int)Math.Round(amount * 100m, MidpointRounding.AwayFromZero));

            var payload = new
            {
                data = new
                {
                    attributes = new
                    {
                        amount = amountInCentavos,
                        payment_id = paymentId,
                        reason = MapRefundReason(reasonCode),
                        notes = notesText
                    }
                }
            };

            using var client = new HttpClient();
            var plainTextBytes = Encoding.UTF8.GetBytes(secretKey);
            var base64Auth = Convert.ToBase64String(plainTextBytes);
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic", base64Auth);

            var content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");
            var response = await client.PostAsync("https://api.paymongo.com/v1/refunds", content);
            var responseString = await response.Content.ReadAsStringAsync();

            if (!response.IsSuccessStatusCode)
            {
                return ParseRefundError(responseString, (int)response.StatusCode);
            }

            using var doc = JsonDocument.Parse(responseString);
            if (!doc.RootElement.TryGetProperty("data", out var data))
            {
                return new RefundAttemptResult(false, "ManualReview", null, null, "invalid_response", "PayMongo returned an incomplete refund response.");
            }

            var refundId = data.TryGetProperty("id", out var refundIdProp) ? refundIdProp.GetString() : null;
            var attributes = data.TryGetProperty("attributes", out var attrProp) ? attrProp : default;
            var providerStatus = attributes.ValueKind == JsonValueKind.Object && attributes.TryGetProperty("status", out var statusProp)
                ? statusProp.GetString()
                : null;

            var normalizedProviderStatus = (providerStatus ?? string.Empty).Trim().ToLowerInvariant();
            var localStatus = normalizedProviderStatus == "succeeded"
                ? "Refunded"
                : normalizedProviderStatus == "pending" || normalizedProviderStatus == "processing"
                    ? "Refund Pending"
                    : "ManualReview";

            return new RefundAttemptResult(
                true,
                localStatus,
                refundId,
                providerStatus,
                null,
                null);
        }

        public static async Task<string?> ResolveCheckoutPaymentIdAsync(string secretKey, string checkoutId)
        {
            if (string.IsNullOrWhiteSpace(secretKey) || string.IsNullOrWhiteSpace(checkoutId))
            {
                return null;
            }

            using var client = new HttpClient();
            var plainTextBytes = Encoding.UTF8.GetBytes(secretKey);
            var base64Auth = Convert.ToBase64String(plainTextBytes);
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic", base64Auth);

            var response = await client.GetAsync($"https://api.paymongo.com/v1/checkout_sessions/{checkoutId}");
            if (!response.IsSuccessStatusCode)
            {
                return null;
            }

            var responseString = await response.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(responseString);
            if (!doc.RootElement.TryGetProperty("data", out var data) ||
                !data.TryGetProperty("attributes", out var attributes) ||
                !attributes.TryGetProperty("payments", out var payments) ||
                payments.ValueKind != JsonValueKind.Array ||
                payments.GetArrayLength() == 0)
            {
                return null;
            }

            var firstPayment = payments[0];
            return firstPayment.TryGetProperty("id", out var idProp) ? idProp.GetString() : null;
        }

        public static async Task<RefundAttemptResult> GetPayMongoRefundStatusAsync(string secretKey, string refundId)
        {
            if (string.IsNullOrWhiteSpace(secretKey))
            {
                return new RefundAttemptResult(false, "Failed", null, null, "missing_secret_key", "PayMongo is not configured yet.");
            }

            if (string.IsNullOrWhiteSpace(refundId))
            {
                return new RefundAttemptResult(false, "Failed", null, null, "missing_refund_id", "This refund is missing its PayMongo refund id.");
            }

            using var client = new HttpClient();
            var plainTextBytes = Encoding.UTF8.GetBytes(secretKey);
            var base64Auth = Convert.ToBase64String(plainTextBytes);
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic", base64Auth);

            var response = await client.GetAsync($"https://api.paymongo.com/v1/refunds/{refundId}");
            var responseString = await response.Content.ReadAsStringAsync();

            if (!response.IsSuccessStatusCode)
            {
                return ParseRefundError(responseString, (int)response.StatusCode);
            }

            using var doc = JsonDocument.Parse(responseString);
            if (!doc.RootElement.TryGetProperty("data", out var data))
            {
                return new RefundAttemptResult(false, "ManualReview", refundId, null, "invalid_response", "PayMongo returned an incomplete refund response.");
            }

            var attributes = data.TryGetProperty("attributes", out var attrProp) ? attrProp : default;
            var providerStatus = attributes.ValueKind == JsonValueKind.Object && attributes.TryGetProperty("status", out var statusProp)
                ? statusProp.GetString()
                : null;

            var normalizedProviderStatus = (providerStatus ?? string.Empty).Trim().ToLowerInvariant();
            var localStatus = normalizedProviderStatus == "succeeded"
                ? "Refunded"
                : normalizedProviderStatus == "pending" || normalizedProviderStatus == "processing"
                    ? "Refund Pending"
                    : "ManualReview";

            return new RefundAttemptResult(
                localStatus == "Refunded" || localStatus == "Refund Pending",
                localStatus,
                refundId,
                providerStatus,
                null,
                null);
        }

        private static RefundAttemptResult ParseRefundError(string responseString, int statusCode)
        {
            string? code = null;
            string? message = null;

            try
            {
                using var doc = JsonDocument.Parse(responseString);
                if (doc.RootElement.TryGetProperty("errors", out var errors) &&
                    errors.ValueKind == JsonValueKind.Array &&
                    errors.GetArrayLength() > 0)
                {
                    var firstError = errors[0];
                    if (firstError.TryGetProperty("code", out var codeProp))
                    {
                        code = codeProp.GetString();
                    }

                    if (firstError.TryGetProperty("detail", out var detailProp))
                    {
                        message = detailProp.GetString();
                    }
                }
            }
            catch
            {
            }

            var normalizedCode = (code ?? string.Empty).Trim().ToLowerInvariant();
            var localStatus = normalizedCode is "available_balance_insufficient" or "payment_method_not_allowed" or "allowed_date_exceeded"
                ? "ManualReview"
                : "Failed";

            return new RefundAttemptResult(
                false,
                localStatus,
                null,
                null,
                code ?? $"http_{statusCode}",
                message ?? "PayMongo could not process the refund right now.");
        }

        private static string MapRefundReason(string reasonCode)
        {
            return (reasonCode ?? string.Empty).Trim().ToLowerInvariant() switch
            {
                "duplicatepayment" => "duplicate",
                "fraudulent" => "fraudulent",
                "customercancelled" => "requested_by_customer",
                "talentcancelled" => "requested_by_customer",
                "talentnoshow" => "requested_by_customer",
                "customernoshow" => "others",
                _ => "others"
            };
        }
    }
}
