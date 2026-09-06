using System.Text.Json;
using Npgsql;
using NpgsqlTypes;

namespace ImajinationAPI.Services;

public static class BookingPaymentReconciliationService
{
    // Called only after validating the PayMongo webhook signature and environment.
    public static async Task<bool> ReconcileAsync(NpgsqlConnection connection, JsonElement checkout)
    {
        var checkoutId = checkout.GetProperty("id").GetString();
        var attributes = checkout.GetProperty("attributes");
        if (string.IsNullOrWhiteSpace(checkoutId)) return false;
        await EscrowService.EnsureSchemaAsync(connection);
        await PaymentLedgerService.EnsureSchemaAsync(connection);
        Guid topUpId = Guid.Empty, userId = Guid.Empty;
        decimal topUpAmount = 0;
        await using (var cmd = new NpgsqlCommand("SELECT id, user_id, amount FROM wallet_topups WHERE provider_checkout_id = @id", connection))
        {
            cmd.Parameters.AddWithValue("@id", checkoutId);
            await using var reader = await cmd.ExecuteReaderAsync();
            if (await reader.ReadAsync()) { topUpId = reader.GetGuid(0); userId = reader.GetGuid(1); topUpAmount = reader.GetDecimal(2); }
        }
        if (topUpId != Guid.Empty)
        {
            if (!PayMongoPaymentVerification.TryGetPaidPayment(attributes, topUpAmount, out var topUpPayment))
                throw new InvalidOperationException("Webhook top-up amount or payment status did not match.");
            return await EscrowService.ConfirmTopUpAsync(connection, topUpId, userId, topUpPayment.GetProperty("id").GetString());
        }

        Guid bookingId;
        string scope;
        decimal expectedAmount;
        await using (var cmd = new NpgsqlCommand(@"
            SELECT booking_id, payment_scope, amount FROM payment_records
            WHERE checkout_id_hash = @hash AND booking_id IS NOT NULL
              AND payment_scope IN ('booking_service_fee', 'booking_talent_fee', 'booking_talent_platform_fee');", connection))
        {
            cmd.Parameters.AddWithValue("@hash", Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(checkoutId))));
            await using var reader = await cmd.ExecuteReaderAsync();
            if (!await reader.ReadAsync())
            {
                if (attributes.TryGetProperty("metadata", out var metadata) && metadata.ValueKind == JsonValueKind.Object &&
                    (metadata.TryGetProperty("booking_id", out _) || metadata.TryGetProperty("wallet_topup_id", out _)))
                    throw new InvalidOperationException("Checkout linkage is not ready; retry webhook delivery.");
                return false; // Ticket or an unrelated checkout.
            }
            bookingId = reader.GetGuid(0); scope = reader.GetString(1); expectedAmount = reader.GetDecimal(2);
        }
        if (!PayMongoPaymentVerification.TryGetPaidPayment(attributes, expectedAmount, out var payment))
            throw new InvalidOperationException("Webhook booking amount or payment status did not match.");
        var paymentId = payment.GetProperty("id").GetString();
        var details = payment.GetProperty("attributes");
        var method = details.TryGetProperty("source", out var source) && source.TryGetProperty("type", out var type) ? type.GetString() : "";
        var reference = details.TryGetProperty("reference_number", out var referenceValue) ? referenceValue.GetString() : paymentId;
        var checkoutReference = attributes.TryGetProperty("reference_number", out var checkoutReferenceValue) ? checkoutReferenceValue.GetString() : checkoutId;

        await using (var transaction = await connection.BeginTransactionAsync())
        {
            // All settlement paths take the booking lock before changing money states.
            await using (var lockCmd = new NpgsqlCommand("SELECT id FROM bookings WHERE id = @id FOR UPDATE", connection, transaction))
            {
                lockCmd.Parameters.AddWithValue("@id", bookingId);
                if (await lockCmd.ExecuteScalarAsync() is null) return false;
            }
            var sql = scope switch
            {
                "booking_service_fee" => @"UPDATE bookings SET service_fee_status = 'Paid', service_fee_paid_at = COALESCE(service_fee_paid_at, NOW()),
                    paid_at = COALESCE(paid_at, NOW()), paymongo_payment_id = @paymentId, paymongo_payment_reference = @reference,
                    payment_method = @method, service_fee_payment_method = @method,
                    status = CASE WHEN status LIKE 'Awaiting%' THEN 'Pending ' || target_role || ' Approval' ELSE status END,
                    payment_status = CASE WHEN status LIKE 'Cancelled%' THEN 'Refund Pending' WHEN budget > 0 THEN 'ServiceFeePaid' ELSE 'Paid' END
                    WHERE id = @id AND service_fee_status NOT IN ('Paid', 'Refunded');",
                "booking_talent_fee" => @"UPDATE bookings SET talent_fee_status = 'HeldInEscrow', talent_fee_paid_at = COALESCE(talent_fee_paid_at, NOW()),
                    talent_fee_payment_id = @paymentId, talent_fee_payment_reference = @reference, talent_fee_payment_method = @method,
                    payment_status = CASE WHEN status LIKE 'Cancelled%' THEN 'Refund Pending' ELSE 'TalentFeeHeldInEscrow' END
                    WHERE id = @id AND talent_fee_status NOT IN ('HeldInEscrow', 'ReadyForRelease', 'Released', 'Refunded', 'Refund Pending', 'Refund Failed');",
                _ => @"UPDATE bookings SET talent_platform_fee_status = 'Paid', talent_platform_fee_paid_at = COALESCE(talent_platform_fee_paid_at, NOW()),
                    talent_platform_fee_payment_id = @paymentId, talent_platform_fee_payment_reference = @reference, talent_platform_fee_payment_method = @method
                    WHERE id = @id AND talent_platform_fee_status NOT IN ('Paid', 'Refunded');"
            };
            await using (var cmd = new NpgsqlCommand(sql, connection, transaction))
            {
                cmd.Parameters.AddWithValue("@id", bookingId);
                cmd.Parameters.Add("@paymentId", NpgsqlDbType.Text).Value = (object?)paymentId ?? DBNull.Value;
                cmd.Parameters.Add("@reference", NpgsqlDbType.Text).Value = (object?)reference ?? DBNull.Value;
                cmd.Parameters.Add("@method", NpgsqlDbType.Text).Value = (object?)method ?? DBNull.Value;
                await cmd.ExecuteNonQueryAsync();
            }
            if (scope == "booking_talent_fee") await EscrowService.MarkHeldAsync(connection, bookingId, paymentId);
            await PaymentLedgerService.MarkPaidAsync(connection, scope, null, bookingId, method ?? "", reference ?? "", checkoutReference ?? "",
                scope == "booking_service_fee" ? "MessagesUnlocked" : scope == "booking_talent_fee" ? "TalentFeeHeldInEscrow" : "TalentPlatformFeeSettled", null);
            await transaction.CommitAsync();
        }
        if (scope != "booking_service_fee") await EscrowService.ReleaseAsync(connection, bookingId);
        return true;
    }
}
