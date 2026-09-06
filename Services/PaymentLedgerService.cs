using System.Text.Json;
using System.Security.Cryptography;
using System.Text;
using Npgsql;
using NpgsqlTypes;

namespace ImajinationAPI.Services
{
    public static class PaymentLedgerService
    {
        public static async Task EnsureSchemaAsync(NpgsqlConnection connection)
        {
            const string sql = @"
                CREATE TABLE IF NOT EXISTS payment_records (
                    id uuid PRIMARY KEY,
                    payment_scope varchar(80) NOT NULL,
                    provider varchar(40) NOT NULL DEFAULT 'PayMongo',
                    status varchar(40) NOT NULL DEFAULT 'Pending',
                    receipt_status varchar(40) NOT NULL DEFAULT 'Pending',
                    feature_unlock_state varchar(60) NOT NULL DEFAULT 'Locked',
                    user_id uuid NULL,
                    organizer_id uuid NULL,
                    event_id uuid NULL,
                    ticket_id uuid NULL,
                    booking_id uuid NULL,
                    amount numeric(12,2) NOT NULL DEFAULT 0,
                    currency varchar(10) NOT NULL DEFAULT 'PHP',
                    payment_method text NULL,
                    checkout_id text NULL,
                    checkout_reference text NULL,
                    payment_reference text NULL,
                    checkout_id_hash text NULL,
                    checkout_reference_hash text NULL,
                    payment_reference_hash text NULL,
                    description text NULL,
                    metadata jsonb NOT NULL DEFAULT '{}'::jsonb,
                    paid_at timestamptz NULL,
                    created_at timestamptz NOT NULL DEFAULT NOW(),
                    updated_at timestamptz NOT NULL DEFAULT NOW()
                );

                CREATE INDEX IF NOT EXISTS idx_payment_records_user_id
                    ON payment_records(user_id);

                CREATE INDEX IF NOT EXISTS idx_payment_records_booking_id
                    ON payment_records(booking_id);

                CREATE INDEX IF NOT EXISTS idx_payment_records_ticket_id
                    ON payment_records(ticket_id);

                CREATE INDEX IF NOT EXISTS idx_payment_records_status
                    ON payment_records(status);

                CREATE UNIQUE INDEX IF NOT EXISTS uq_payment_records_scope_ticket
                    ON payment_records(payment_scope, ticket_id)
                    WHERE ticket_id IS NOT NULL;

                CREATE UNIQUE INDEX IF NOT EXISTS uq_payment_records_scope_booking
                    ON payment_records(payment_scope, booking_id)
                    WHERE booking_id IS NOT NULL;

                ALTER TABLE payment_records ADD COLUMN IF NOT EXISTS checkout_id_hash text NULL;
                ALTER TABLE payment_records ADD COLUMN IF NOT EXISTS checkout_reference_hash text NULL;
                ALTER TABLE payment_records ADD COLUMN IF NOT EXISTS payment_reference_hash text NULL;";

            await using var cmd = new NpgsqlCommand(sql, connection);
            await cmd.ExecuteNonQueryAsync();
        }

        private static string? HashValue(string? value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return null;
            }

            var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(value.Trim()));
            return Convert.ToHexString(bytes);
        }

        public static async Task UpsertPendingAsync(
            NpgsqlConnection connection,
            string paymentScope,
            Guid? userId,
            Guid? organizerId,
            Guid? eventId,
            Guid? ticketId,
            Guid? bookingId,
            decimal amount,
            string description,
            string checkoutId,
            string checkoutReference,
            string featureUnlockState,
            object? metadata = null)
        {
            await EnsureSchemaAsync(connection);

            var metadataJson = JsonSerializer.Serialize(metadata ?? new { });
            var checkoutIdHash = HashValue(checkoutId);
            var checkoutReferenceHash = HashValue(checkoutReference);

            if (ticketId.HasValue)
            {
                const string ticketSql = @"
                    INSERT INTO payment_records (
                        id, payment_scope, provider, status, receipt_status, feature_unlock_state,
                        user_id, organizer_id, event_id, ticket_id, booking_id,
                        amount, currency, checkout_id, checkout_reference, checkout_id_hash, checkout_reference_hash, description, metadata, updated_at
                    )
                    VALUES (
                        @id, @paymentScope, 'PayMongo', 'Pending', 'Pending', @featureUnlockState,
                        @userId, @organizerId, @eventId, @ticketId, @bookingId,
                        @amount, 'PHP', NULL, NULL, @checkoutIdHash, @checkoutReferenceHash, @description, @metadata::jsonb, NOW()
                    )
                    ON CONFLICT (payment_scope, ticket_id) WHERE ticket_id IS NOT NULL
                    DO UPDATE SET
                        status = 'Pending',
                        receipt_status = 'Pending',
                        feature_unlock_state = EXCLUDED.feature_unlock_state,
                        organizer_id = EXCLUDED.organizer_id,
                        event_id = EXCLUDED.event_id,
                        amount = EXCLUDED.amount,
                        checkout_id = NULL,
                        checkout_reference = NULL,
                        checkout_id_hash = EXCLUDED.checkout_id_hash,
                        checkout_reference_hash = EXCLUDED.checkout_reference_hash,
                        description = EXCLUDED.description,
                        metadata = EXCLUDED.metadata,
                        updated_at = NOW();";

                await using var cmd = new NpgsqlCommand(ticketSql, connection);
                cmd.Parameters.Add("@id", NpgsqlDbType.Uuid).Value = Guid.NewGuid();
                cmd.Parameters.Add("@paymentScope", NpgsqlDbType.Text).Value = paymentScope;
                cmd.Parameters.Add("@featureUnlockState", NpgsqlDbType.Text).Value = featureUnlockState;
                cmd.Parameters.Add("@userId", NpgsqlDbType.Uuid).Value = (object?)userId ?? DBNull.Value;
                cmd.Parameters.Add("@organizerId", NpgsqlDbType.Uuid).Value = (object?)organizerId ?? DBNull.Value;
                cmd.Parameters.Add("@eventId", NpgsqlDbType.Uuid).Value = (object?)eventId ?? DBNull.Value;
                cmd.Parameters.Add("@ticketId", NpgsqlDbType.Uuid).Value = ticketId.Value;
                cmd.Parameters.Add("@bookingId", NpgsqlDbType.Uuid).Value = (object?)bookingId ?? DBNull.Value;
                cmd.Parameters.Add("@amount", NpgsqlDbType.Numeric).Value = amount;
                cmd.Parameters.Add("@checkoutIdHash", NpgsqlDbType.Text).Value = (object?)checkoutIdHash ?? DBNull.Value;
                cmd.Parameters.Add("@checkoutReferenceHash", NpgsqlDbType.Text).Value = (object?)checkoutReferenceHash ?? DBNull.Value;
                cmd.Parameters.Add("@description", NpgsqlDbType.Text).Value = description;
                cmd.Parameters.Add("@metadata", NpgsqlDbType.Jsonb).Value = metadataJson;
                await cmd.ExecuteNonQueryAsync();
                return;
            }

            if (bookingId.HasValue)
            {
                const string bookingSql = @"
                    INSERT INTO payment_records (
                        id, payment_scope, provider, status, receipt_status, feature_unlock_state,
                        user_id, organizer_id, event_id, ticket_id, booking_id,
                        amount, currency, checkout_id, checkout_reference, checkout_id_hash, checkout_reference_hash, description, metadata, updated_at
                    )
                    VALUES (
                        @id, @paymentScope, 'PayMongo', 'Pending', 'Pending', @featureUnlockState,
                        @userId, @organizerId, @eventId, @ticketId, @bookingId,
                        @amount, 'PHP', NULL, NULL, @checkoutIdHash, @checkoutReferenceHash, @description, @metadata::jsonb, NOW()
                    )
                    ON CONFLICT (payment_scope, booking_id) WHERE booking_id IS NOT NULL
                    DO UPDATE SET
                        status = 'Pending',
                        receipt_status = 'Pending',
                        feature_unlock_state = EXCLUDED.feature_unlock_state,
                        organizer_id = EXCLUDED.organizer_id,
                        event_id = EXCLUDED.event_id,
                        amount = EXCLUDED.amount,
                        checkout_id = NULL,
                        checkout_reference = NULL,
                        checkout_id_hash = EXCLUDED.checkout_id_hash,
                        checkout_reference_hash = EXCLUDED.checkout_reference_hash,
                        description = EXCLUDED.description,
                        metadata = EXCLUDED.metadata,
                        updated_at = NOW();";

                await using var cmd = new NpgsqlCommand(bookingSql, connection);
                cmd.Parameters.Add("@id", NpgsqlDbType.Uuid).Value = Guid.NewGuid();
                cmd.Parameters.Add("@paymentScope", NpgsqlDbType.Text).Value = paymentScope;
                cmd.Parameters.Add("@featureUnlockState", NpgsqlDbType.Text).Value = featureUnlockState;
                cmd.Parameters.Add("@userId", NpgsqlDbType.Uuid).Value = (object?)userId ?? DBNull.Value;
                cmd.Parameters.Add("@organizerId", NpgsqlDbType.Uuid).Value = (object?)organizerId ?? DBNull.Value;
                cmd.Parameters.Add("@eventId", NpgsqlDbType.Uuid).Value = (object?)eventId ?? DBNull.Value;
                cmd.Parameters.Add("@ticketId", NpgsqlDbType.Uuid).Value = (object?)ticketId ?? DBNull.Value;
                cmd.Parameters.Add("@bookingId", NpgsqlDbType.Uuid).Value = bookingId.Value;
                cmd.Parameters.Add("@amount", NpgsqlDbType.Numeric).Value = amount;
                cmd.Parameters.Add("@checkoutIdHash", NpgsqlDbType.Text).Value = (object?)checkoutIdHash ?? DBNull.Value;
                cmd.Parameters.Add("@checkoutReferenceHash", NpgsqlDbType.Text).Value = (object?)checkoutReferenceHash ?? DBNull.Value;
                cmd.Parameters.Add("@description", NpgsqlDbType.Text).Value = description;
                cmd.Parameters.Add("@metadata", NpgsqlDbType.Jsonb).Value = metadataJson;
                await cmd.ExecuteNonQueryAsync();
            }
        }

        public static async Task MarkPaidAsync(
            NpgsqlConnection connection,
            string paymentScope,
            Guid? ticketId,
            Guid? bookingId,
            string paymentMethod,
            string paymentReference,
            string checkoutReference,
            string featureUnlockState,
            DateTime? paidAt = null)
        {
            await EnsureSchemaAsync(connection);

            const string sql = @"
                UPDATE payment_records
                SET status = 'Paid',
                    receipt_status = 'Available',
                    feature_unlock_state = @featureUnlockState,
                    payment_method = @paymentMethod,
                    payment_reference = NULL,
                    checkout_reference = NULL,
                    payment_reference_hash = @paymentReferenceHash,
                    checkout_reference_hash = @checkoutReferenceHash,
                    paid_at = COALESCE(paid_at, @paidAt, NOW()),
                    updated_at = NOW()
                WHERE payment_scope = @paymentScope AND status <> 'Refunded'
                  AND (
                    (@ticketId IS NOT NULL AND ticket_id = @ticketId)
                    OR (@bookingId IS NOT NULL AND booking_id = @bookingId)
                  );";

            await using var cmd = new NpgsqlCommand(sql, connection);
            var paymentReferenceHash = HashValue(paymentReference);
            var checkoutReferenceHash = HashValue(checkoutReference);
            cmd.Parameters.Add("@paymentScope", NpgsqlDbType.Text).Value = paymentScope;
            cmd.Parameters.Add("@ticketId", NpgsqlDbType.Uuid).Value = (object?)ticketId ?? DBNull.Value;
            cmd.Parameters.Add("@bookingId", NpgsqlDbType.Uuid).Value = (object?)bookingId ?? DBNull.Value;
            cmd.Parameters.Add("@paymentMethod", NpgsqlDbType.Text).Value = (object?)paymentMethod ?? DBNull.Value;
            cmd.Parameters.Add("@paymentReferenceHash", NpgsqlDbType.Text).Value = (object?)paymentReferenceHash ?? DBNull.Value;
            cmd.Parameters.Add("@checkoutReferenceHash", NpgsqlDbType.Text).Value = (object?)checkoutReferenceHash ?? DBNull.Value;
            cmd.Parameters.Add("@featureUnlockState", NpgsqlDbType.Text).Value = featureUnlockState;
            cmd.Parameters.Add("@paidAt", NpgsqlDbType.TimestampTz).Value = (object?)paidAt ?? DBNull.Value;
            await cmd.ExecuteNonQueryAsync();
        }

        public static async Task MarkRefundedAsync(
            NpgsqlConnection connection,
            string paymentScope,
            Guid? ticketId,
            Guid? bookingId,
            string featureUnlockState)
        {
            await EnsureSchemaAsync(connection);

            const string sql = @"
                UPDATE payment_records
                SET status = 'Refunded',
                    receipt_status = 'Refunded',
                    feature_unlock_state = @featureUnlockState,
                    updated_at = NOW()
                WHERE payment_scope = @paymentScope
                  AND (
                    (@ticketId IS NOT NULL AND ticket_id = @ticketId)
                    OR (@bookingId IS NOT NULL AND booking_id = @bookingId)
                  );";

            await using var cmd = new NpgsqlCommand(sql, connection);
            cmd.Parameters.Add("@paymentScope", NpgsqlDbType.Text).Value = paymentScope;
            cmd.Parameters.Add("@ticketId", NpgsqlDbType.Uuid).Value = (object?)ticketId ?? DBNull.Value;
            cmd.Parameters.Add("@bookingId", NpgsqlDbType.Uuid).Value = (object?)bookingId ?? DBNull.Value;
            cmd.Parameters.Add("@featureUnlockState", NpgsqlDbType.Text).Value = featureUnlockState;
            await cmd.ExecuteNonQueryAsync();
        }
    }
}
