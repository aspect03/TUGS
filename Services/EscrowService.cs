using Npgsql;
using NpgsqlTypes;

namespace ImajinationAPI.Services;

public sealed record WalletSummary(decimal AvailableBalance, decimal HeldBalance, decimal PendingBalance, string Currency);

public sealed record WalletLedgerItem(
    Guid Id,
    Guid? BookingId,
    string EntryType,
    string Direction,
    decimal Amount,
    string Currency,
    string ReferenceType,
    string ReferenceId,
    DateTime CreatedAt);

public static class EscrowService
{
    public static async Task EnsureSchemaAsync(NpgsqlConnection connection)
    {
        const string sql = @"
            CREATE TABLE IF NOT EXISTS escrow_transactions (
                id uuid PRIMARY KEY,
                booking_id uuid NOT NULL UNIQUE,
                payer_user_id uuid NOT NULL,
                beneficiary_user_id uuid NOT NULL,
                payment_record_id uuid NULL,
                provider varchar(40) NOT NULL DEFAULT 'PayMongo',
                provider_checkout_id text NULL,
                provider_payment_id text NULL,
                currency varchar(10) NOT NULL DEFAULT 'PHP',
                gross_amount numeric(12,2) NOT NULL CHECK (gross_amount >= 0),
                platform_fee numeric(12,2) NOT NULL DEFAULT 0 CHECK (platform_fee >= 0),
                net_amount numeric(12,2) NOT NULL CHECK (net_amount >= 0),
                status varchar(40) NOT NULL DEFAULT 'PendingPayment',
                funded_at timestamptz NULL,
                release_ready_at timestamptz NULL,
                released_at timestamptz NULL,
                refunded_at timestamptz NULL,
                created_at timestamptz NOT NULL DEFAULT NOW(),
                updated_at timestamptz NOT NULL DEFAULT NOW()
            );
            CREATE INDEX IF NOT EXISTS idx_escrow_beneficiary_status
                ON escrow_transactions(beneficiary_user_id, status);

            CREATE TABLE IF NOT EXISTS wallet_accounts (
                id uuid PRIMARY KEY,
                user_id uuid NOT NULL,
                currency varchar(10) NOT NULL DEFAULT 'PHP',
                status varchar(20) NOT NULL DEFAULT 'Active',
                created_at timestamptz NOT NULL DEFAULT NOW(),
                updated_at timestamptz NOT NULL DEFAULT NOW(),
                UNIQUE (user_id, currency)
            );

            CREATE TABLE IF NOT EXISTS wallet_ledger_entries (
                id uuid PRIMARY KEY,
                wallet_account_id uuid NOT NULL REFERENCES wallet_accounts(id),
                escrow_transaction_id uuid NULL REFERENCES escrow_transactions(id),
                booking_id uuid NULL,
                entry_type varchar(40) NOT NULL,
                direction varchar(10) NOT NULL CHECK (direction IN ('Credit', 'Debit')),
                amount numeric(12,2) NOT NULL CHECK (amount > 0),
                currency varchar(10) NOT NULL DEFAULT 'PHP',
                reference_type varchar(50) NOT NULL,
                reference_id varchar(255) NOT NULL,
                idempotency_key varchar(150) NOT NULL UNIQUE,
                metadata jsonb NOT NULL DEFAULT '{}'::jsonb,
                created_at timestamptz NOT NULL DEFAULT NOW()
            );
            CREATE INDEX IF NOT EXISTS idx_wallet_ledger_account_created
                ON wallet_ledger_entries(wallet_account_id, created_at DESC);
            CREATE INDEX IF NOT EXISTS idx_wallet_ledger_escrow
                ON wallet_ledger_entries(escrow_transaction_id);

            CREATE TABLE IF NOT EXISTS wallet_topups (
                id uuid PRIMARY KEY,
                user_id uuid NOT NULL,
                amount numeric(12,2) NOT NULL CHECK (amount > 0),
                currency varchar(10) NOT NULL DEFAULT 'PHP',
                status varchar(30) NOT NULL DEFAULT 'PendingPayment',
                provider varchar(40) NOT NULL DEFAULT 'PayMongo',
                provider_checkout_id text NULL,
                provider_payment_id text NULL,
                created_at timestamptz NOT NULL DEFAULT NOW(),
                confirmed_at timestamptz NULL,
                updated_at timestamptz NOT NULL DEFAULT NOW()
            );
            CREATE INDEX IF NOT EXISTS idx_wallet_topups_user_status
                ON wallet_topups(user_id, status);

            CREATE TABLE IF NOT EXISTS escrow_resolutions (
                id uuid PRIMARY KEY,
                escrow_transaction_id uuid NOT NULL REFERENCES escrow_transactions(id),
                dispute_id uuid NULL,
                resolved_by uuid NOT NULL,
                resolution_type varchar(40) NOT NULL,
                talent_amount numeric(12,2) NOT NULL DEFAULT 0 CHECK (talent_amount >= 0),
                customer_refund_amount numeric(12,2) NOT NULL DEFAULT 0 CHECK (customer_refund_amount >= 0),
                notes text NULL,
                created_at timestamptz NOT NULL DEFAULT NOW()
            );
            CREATE INDEX IF NOT EXISTS idx_escrow_resolutions_escrow
                ON escrow_resolutions(escrow_transaction_id);

            ALTER TABLE bookings ADD COLUMN IF NOT EXISTS escrow_transaction_id uuid NULL;
            ALTER TABLE bookings ADD COLUMN IF NOT EXISTS escrow_status varchar(40) NULL;
            CREATE UNIQUE INDEX IF NOT EXISTS uq_bookings_escrow_transaction
                ON bookings(escrow_transaction_id) WHERE escrow_transaction_id IS NOT NULL;";

        await using var cmd = new NpgsqlCommand(sql, connection);
        await cmd.ExecuteNonQueryAsync();
    }

    public static async Task<Guid> CreatePendingAsync(
        NpgsqlConnection connection,
        Guid bookingId,
        Guid payerUserId,
        Guid beneficiaryUserId,
        decimal amount)
    {
        await EnsureSchemaAsync(connection);
        const string sql = @"
            INSERT INTO escrow_transactions (
                id, booking_id, payer_user_id, beneficiary_user_id, gross_amount, net_amount, status
            ) VALUES (
                @id, @bookingId, @payerUserId, @beneficiaryUserId, @amount, @amount, 'PendingPayment'
            )
            ON CONFLICT (booking_id) DO UPDATE
            SET payer_user_id = EXCLUDED.payer_user_id,
                beneficiary_user_id = EXCLUDED.beneficiary_user_id,
                gross_amount = EXCLUDED.gross_amount,
                net_amount = EXCLUDED.net_amount,
                updated_at = NOW()
            WHERE escrow_transactions.status = 'PendingPayment'
            RETURNING id;";

        await using var cmd = new NpgsqlCommand(sql, connection);
        cmd.Parameters.Add("@id", NpgsqlDbType.Uuid).Value = Guid.NewGuid();
        cmd.Parameters.Add("@bookingId", NpgsqlDbType.Uuid).Value = bookingId;
        cmd.Parameters.Add("@payerUserId", NpgsqlDbType.Uuid).Value = payerUserId;
        cmd.Parameters.Add("@beneficiaryUserId", NpgsqlDbType.Uuid).Value = beneficiaryUserId;
        cmd.Parameters.Add("@amount", NpgsqlDbType.Numeric).Value = amount;
        var id = await cmd.ExecuteScalarAsync();
        if (id is Guid escrowId)
        {
            await SetBookingEscrowStateAsync(connection, bookingId, escrowId, "PendingPayment");
            return escrowId;
        }

        throw new InvalidOperationException("The talent fee is already funded or settled for this booking.");
    }

    public static async Task<Guid> ReserveFromWalletAsync(
        NpgsqlConnection connection,
        Guid bookingId,
        Guid payerUserId,
        Guid beneficiaryUserId,
        decimal amount)
    {
        if (amount <= 0) throw new InvalidOperationException("The escrow amount must be greater than zero.");
        await EnsureSchemaAsync(connection);
        await using var transaction = await connection.BeginTransactionAsync();
        try
        {
            var walletId = await EnsureWalletAccountAsync(connection, transaction, payerUserId, "PHP");
            const string balanceSql = @"
                SELECT COALESCE(SUM(CASE WHEN direction = 'Credit' THEN amount ELSE -amount END), 0)
                FROM wallet_ledger_entries WHERE wallet_account_id = @walletId;";
            decimal balance;
            await using (var balanceCmd = new NpgsqlCommand(balanceSql, connection, transaction))
            {
                balanceCmd.Parameters.Add("@walletId", NpgsqlDbType.Uuid).Value = walletId;
                balance = Convert.ToDecimal(await balanceCmd.ExecuteScalarAsync() ?? 0m);
            }
            if (balance < amount) throw new InvalidOperationException("Your wallet does not have enough available balance for this booking.");

            const string escrowSql = @"
                INSERT INTO escrow_transactions (
                    id, booking_id, payer_user_id, beneficiary_user_id, provider, gross_amount, net_amount, status, funded_at
                ) VALUES (
                    @id, @bookingId, @payerUserId, @beneficiaryUserId, 'Wallet', @amount, @amount, 'Held', NOW()
                )
                ON CONFLICT (booking_id) DO NOTHING
                RETURNING id;";
            Guid escrowId;
            await using (var escrowCmd = new NpgsqlCommand(escrowSql, connection, transaction))
            {
                escrowCmd.Parameters.Add("@id", NpgsqlDbType.Uuid).Value = Guid.NewGuid();
                escrowCmd.Parameters.Add("@bookingId", NpgsqlDbType.Uuid).Value = bookingId;
                escrowCmd.Parameters.Add("@payerUserId", NpgsqlDbType.Uuid).Value = payerUserId;
                escrowCmd.Parameters.Add("@beneficiaryUserId", NpgsqlDbType.Uuid).Value = beneficiaryUserId;
                escrowCmd.Parameters.Add("@amount", NpgsqlDbType.Numeric).Value = amount;
                var value = await escrowCmd.ExecuteScalarAsync();
                if (value is not Guid createdEscrowId)
                    throw new InvalidOperationException("This booking already has a payment or escrow record.");
                escrowId = createdEscrowId;
            }

            await InsertLedgerEntryAsync(connection, transaction, walletId, escrowId, bookingId,
                "EscrowReserve", "Debit", amount, "booking_escrow", bookingId.ToString(), $"escrow:{escrowId}:reserve");
            await transaction.CommitAsync();
            await SetBookingEscrowStateAsync(connection, bookingId, escrowId, "Held");
            return escrowId;
        }
        catch
        {
            await transaction.RollbackAsync();
            throw;
        }
    }

    public static async Task<Guid> CreateTopUpAsync(NpgsqlConnection connection, Guid userId, decimal amount)
    {
        if (amount <= 0) throw new InvalidOperationException("Top-up amount must be greater than zero.");
        await EnsureSchemaAsync(connection);
        const string sql = @"
            INSERT INTO wallet_topups (id, user_id, amount)
            VALUES (@id, @userId, @amount)
            RETURNING id;";
        await using var cmd = new NpgsqlCommand(sql, connection);
        cmd.Parameters.Add("@id", NpgsqlDbType.Uuid).Value = Guid.NewGuid();
        cmd.Parameters.Add("@userId", NpgsqlDbType.Uuid).Value = userId;
        cmd.Parameters.Add("@amount", NpgsqlDbType.Numeric).Value = amount;
        return (Guid)(await cmd.ExecuteScalarAsync())!;
    }

    public static async Task LinkTopUpCheckoutAsync(NpgsqlConnection connection, Guid topUpId, string checkoutId)
    {
        const string sql = @"
            UPDATE wallet_topups SET provider_checkout_id = @checkoutId, updated_at = NOW()
            WHERE id = @id AND status = 'PendingPayment';";
        await using var cmd = new NpgsqlCommand(sql, connection);
        cmd.Parameters.Add("@id", NpgsqlDbType.Uuid).Value = topUpId;
        cmd.Parameters.Add("@checkoutId", NpgsqlDbType.Text).Value = checkoutId;
        await cmd.ExecuteNonQueryAsync();
    }

    public static async Task<bool> ConfirmTopUpAsync(NpgsqlConnection connection, Guid topUpId, Guid userId, string? paymentId)
    {
        await EnsureSchemaAsync(connection);
        await using var transaction = await connection.BeginTransactionAsync();
        try
        {
            const string topUpSql = @"
                SELECT amount, currency, status FROM wallet_topups
                WHERE id = @id AND user_id = @userId FOR UPDATE;";
            decimal amount;
            string currency;
            string status;
            await using (var topUpCmd = new NpgsqlCommand(topUpSql, connection, transaction))
            {
                topUpCmd.Parameters.Add("@id", NpgsqlDbType.Uuid).Value = topUpId;
                topUpCmd.Parameters.Add("@userId", NpgsqlDbType.Uuid).Value = userId;
                await using var reader = await topUpCmd.ExecuteReaderAsync();
                if (!await reader.ReadAsync()) return false;
                amount = reader.GetDecimal(0);
                currency = reader.GetString(1);
                status = reader.GetString(2);
            }
            if (status == "Paid") { await transaction.CommitAsync(); return true; }
            if (status != "PendingPayment") return false;

            var walletId = await EnsureWalletAccountAsync(connection, transaction, userId, currency);
            await InsertLedgerEntryAsync(connection, transaction, walletId, null, null,
                "WalletTopUp", "Credit", amount, "wallet_topup", topUpId.ToString(), $"wallet-topup:{topUpId}");
            const string updateSql = @"
                UPDATE wallet_topups SET status = 'Paid', provider_payment_id = @paymentId,
                    confirmed_at = NOW(), updated_at = NOW() WHERE id = @id;";
            await using (var updateCmd = new NpgsqlCommand(updateSql, connection, transaction))
            {
                updateCmd.Parameters.Add("@id", NpgsqlDbType.Uuid).Value = topUpId;
                updateCmd.Parameters.Add("@paymentId", NpgsqlDbType.Text).Value = (object?)paymentId ?? DBNull.Value;
                await updateCmd.ExecuteNonQueryAsync();
            }
            await transaction.CommitAsync();
            return true;
        }
        catch
        {
            await transaction.RollbackAsync();
            throw;
        }
    }

    public static async Task LinkCheckoutAsync(NpgsqlConnection connection, Guid escrowId, string? checkoutId)
    {
        const string sql = @"
            UPDATE escrow_transactions
            SET provider_checkout_id = @checkoutId, updated_at = NOW()
            WHERE id = @id AND status = 'PendingPayment';";
        await using var cmd = new NpgsqlCommand(sql, connection);
        cmd.Parameters.Add("@id", NpgsqlDbType.Uuid).Value = escrowId;
        cmd.Parameters.Add("@checkoutId", NpgsqlDbType.Text).Value = (object?)checkoutId ?? DBNull.Value;
        await cmd.ExecuteNonQueryAsync();
    }

    public static async Task MarkHeldAsync(NpgsqlConnection connection, Guid bookingId, string? providerPaymentId)
    {
        await EnsureSchemaAsync(connection);
        const string sql = @"
            UPDATE escrow_transactions
            SET status = CASE WHEN status = 'Disputed' THEN 'Disputed' ELSE 'Held' END,
                provider_payment_id = COALESCE(@paymentId, provider_payment_id),
                funded_at = COALESCE(funded_at, NOW()),
                updated_at = NOW()
            WHERE booking_id = @bookingId
              AND status IN ('PendingPayment', 'Held', 'Disputed')
            RETURNING id, status;";
        await using var cmd = new NpgsqlCommand(sql, connection);
        cmd.Parameters.Add("@bookingId", NpgsqlDbType.Uuid).Value = bookingId;
        cmd.Parameters.Add("@paymentId", NpgsqlDbType.Text).Value = (object?)providerPaymentId ?? DBNull.Value;
        await using var reader = await cmd.ExecuteReaderAsync();
        if (await reader.ReadAsync())
        {
            var escrowId = reader.GetGuid(0);
            var status = reader.GetString(1);
            await reader.CloseAsync();
            await SetBookingEscrowStateAsync(connection, bookingId, escrowId, status);
        }
    }

    public static async Task MarkReleaseReadyAsync(NpgsqlConnection connection, Guid bookingId)
    {
        const string sql = @"
            UPDATE escrow_transactions
            SET status = 'ReleaseReady', release_ready_at = COALESCE(release_ready_at, NOW()), updated_at = NOW()
            WHERE booking_id = @bookingId AND status = 'Held'
            RETURNING id;";
        await using var cmd = new NpgsqlCommand(sql, connection);
        cmd.Parameters.Add("@bookingId", NpgsqlDbType.Uuid).Value = bookingId;
        var value = await cmd.ExecuteScalarAsync();
        if (value is Guid escrowId)
        {
            await SetBookingEscrowStateAsync(connection, bookingId, escrowId, "ReleaseReady");
        }
    }

    public static async Task<bool> ReleaseAsync(NpgsqlConnection connection, Guid bookingId, string referenceType = "booking_completion")
    {
        await EnsureSchemaAsync(connection);
        await using var transaction = await connection.BeginTransactionAsync();
        try
        {
            const string lockSql = @"
                SELECT e.id, e.beneficiary_user_id, e.net_amount, e.currency, e.status
                FROM escrow_transactions e
                INNER JOIN bookings b ON b.id = e.booking_id
                WHERE e.booking_id = @bookingId AND COALESCE(b.dispute_hold, FALSE) = FALSE
                FOR UPDATE;";
            Guid escrowId;
            Guid beneficiaryId;
            decimal netAmount;
            string currency;
            string status;
            await using (var lockCmd = new NpgsqlCommand(lockSql, connection, transaction))
            {
                lockCmd.Parameters.Add("@bookingId", NpgsqlDbType.Uuid).Value = bookingId;
                await using var reader = await lockCmd.ExecuteReaderAsync();
                if (!await reader.ReadAsync()) return false;
                escrowId = reader.GetGuid(0);
                beneficiaryId = reader.GetGuid(1);
                netAmount = reader.GetDecimal(2);
                currency = reader.GetString(3);
                status = reader.GetString(4);
            }

            if (status == "Released")
            {
                await transaction.CommitAsync();
                return true;
            }
            if (status is not ("Held" or "ReleaseReady")) return false;

            const string accountSql = @"
                INSERT INTO wallet_accounts (id, user_id, currency, status)
                VALUES (@id, @userId, @currency, 'Active')
                ON CONFLICT (user_id, currency) DO UPDATE SET updated_at = NOW()
                RETURNING id;";
            Guid walletId;
            await using (var accountCmd = new NpgsqlCommand(accountSql, connection, transaction))
            {
                accountCmd.Parameters.Add("@id", NpgsqlDbType.Uuid).Value = Guid.NewGuid();
                accountCmd.Parameters.Add("@userId", NpgsqlDbType.Uuid).Value = beneficiaryId;
                accountCmd.Parameters.Add("@currency", NpgsqlDbType.Text).Value = currency;
                walletId = (Guid)(await accountCmd.ExecuteScalarAsync())!;
            }

            const string ledgerSql = @"
                INSERT INTO wallet_ledger_entries (
                    id, wallet_account_id, escrow_transaction_id, booking_id, entry_type, direction,
                    amount, currency, reference_type, reference_id, idempotency_key
                ) VALUES (
                    @id, @walletId, @escrowId, @bookingId, 'EscrowRelease', 'Credit',
                    @amount, @currency, @referenceType, @referenceId, @idempotencyKey
                ) ON CONFLICT (idempotency_key) DO NOTHING;";
            await using (var ledgerCmd = new NpgsqlCommand(ledgerSql, connection, transaction))
            {
                ledgerCmd.Parameters.Add("@id", NpgsqlDbType.Uuid).Value = Guid.NewGuid();
                ledgerCmd.Parameters.Add("@walletId", NpgsqlDbType.Uuid).Value = walletId;
                ledgerCmd.Parameters.Add("@escrowId", NpgsqlDbType.Uuid).Value = escrowId;
                ledgerCmd.Parameters.Add("@bookingId", NpgsqlDbType.Uuid).Value = bookingId;
                ledgerCmd.Parameters.Add("@amount", NpgsqlDbType.Numeric).Value = netAmount;
                ledgerCmd.Parameters.Add("@currency", NpgsqlDbType.Text).Value = currency;
                ledgerCmd.Parameters.Add("@referenceType", NpgsqlDbType.Text).Value = referenceType;
                ledgerCmd.Parameters.Add("@referenceId", NpgsqlDbType.Text).Value = bookingId.ToString();
                ledgerCmd.Parameters.Add("@idempotencyKey", NpgsqlDbType.Text).Value = $"escrow:{escrowId}:release";
                await ledgerCmd.ExecuteNonQueryAsync();
            }

            const string releaseSql = @"
                UPDATE escrow_transactions
                SET status = 'Released', released_at = COALESCE(released_at, NOW()), updated_at = NOW()
                WHERE id = @id;";
            await using (var releaseCmd = new NpgsqlCommand(releaseSql, connection, transaction))
            {
                releaseCmd.Parameters.Add("@id", NpgsqlDbType.Uuid).Value = escrowId;
                await releaseCmd.ExecuteNonQueryAsync();
            }
            await transaction.CommitAsync();
            await SetBookingEscrowStateAsync(connection, bookingId, escrowId, "Released");
            return true;
        }
        catch
        {
            await transaction.RollbackAsync();
            throw;
        }
    }

    public static async Task MarkDisputedAsync(NpgsqlConnection connection, Guid bookingId)
    {
        const string sql = @"
            UPDATE escrow_transactions SET status = 'Disputed', updated_at = NOW()
            WHERE booking_id = @bookingId AND status IN ('PendingPayment', 'Held', 'ReleaseReady')
            RETURNING id;";
        await using var cmd = new NpgsqlCommand(sql, connection);
        cmd.Parameters.Add("@bookingId", NpgsqlDbType.Uuid).Value = bookingId;
        var value = await cmd.ExecuteScalarAsync();
        if (value is Guid escrowId) await SetBookingEscrowStateAsync(connection, bookingId, escrowId, "Disputed");
    }

    public static async Task ClearDisputeAsync(NpgsqlConnection connection, Guid bookingId)
    {
        const string sql = @"
            UPDATE escrow_transactions SET status = 'Held', updated_at = NOW()
            WHERE booking_id = @bookingId AND status = 'Disputed'
            RETURNING id;";
        await using var cmd = new NpgsqlCommand(sql, connection);
        cmd.Parameters.Add("@bookingId", NpgsqlDbType.Uuid).Value = bookingId;
        var value = await cmd.ExecuteScalarAsync();
        if (value is Guid escrowId) await SetBookingEscrowStateAsync(connection, bookingId, escrowId, "Held");
    }

    public static async Task MarkRefundPendingAsync(NpgsqlConnection connection, Guid bookingId)
    {
        const string sql = @"
            UPDATE escrow_transactions SET status = 'RefundPending', updated_at = NOW()
            WHERE booking_id = @bookingId AND status IN ('Held', 'ReleaseReady', 'Disputed')
            RETURNING id;";
        await using var cmd = new NpgsqlCommand(sql, connection);
        cmd.Parameters.Add("@bookingId", NpgsqlDbType.Uuid).Value = bookingId;
        var value = await cmd.ExecuteScalarAsync();
        if (value is Guid escrowId) await SetBookingEscrowStateAsync(connection, bookingId, escrowId, "RefundPending");
    }

    public static async Task<bool> IsWalletFundedAsync(NpgsqlConnection connection, Guid bookingId)
    {
        const string sql = "SELECT provider FROM escrow_transactions WHERE booking_id = @bookingId LIMIT 1;";
        await using var cmd = new NpgsqlCommand(sql, connection);
        cmd.Parameters.Add("@bookingId", NpgsqlDbType.Uuid).Value = bookingId;
        var provider = await cmd.ExecuteScalarAsync() as string;
        return string.Equals(provider, "Wallet", StringComparison.OrdinalIgnoreCase);
    }

    public static async Task MarkRefundedAsync(NpgsqlConnection connection, Guid bookingId)
    {
        await EnsureSchemaAsync(connection);
        await using var transaction = await connection.BeginTransactionAsync();
        try
        {
            const string lockSql = @"
                SELECT id, payer_user_id, gross_amount, currency, provider, status
                FROM escrow_transactions WHERE booking_id = @bookingId FOR UPDATE;";
            Guid escrowId;
            Guid payerUserId;
            decimal amount;
            string currency;
            string provider;
            string status;
            await using (var lockCmd = new NpgsqlCommand(lockSql, connection, transaction))
            {
                lockCmd.Parameters.Add("@bookingId", NpgsqlDbType.Uuid).Value = bookingId;
                await using var reader = await lockCmd.ExecuteReaderAsync();
                if (!await reader.ReadAsync()) return;
                escrowId = reader.GetGuid(0);
                payerUserId = reader.GetGuid(1);
                amount = reader.GetDecimal(2);
                currency = reader.GetString(3);
                provider = reader.GetString(4);
                status = reader.GetString(5);
            }
            if (status == "Refunded") { await transaction.CommitAsync(); return; }
            if (status is not ("RefundPending" or "Held" or "ReleaseReady" or "Disputed")) return;

            if (provider.Equals("Wallet", StringComparison.OrdinalIgnoreCase))
            {
                var walletId = await EnsureWalletAccountAsync(connection, transaction, payerUserId, currency);
                await InsertLedgerEntryAsync(connection, transaction, walletId, escrowId, bookingId,
                    "EscrowRefund", "Credit", amount, "booking_escrow_refund", bookingId.ToString(), $"escrow:{escrowId}:refund");
            }

            const string updateSql = @"
                UPDATE escrow_transactions SET status = 'Refunded', refunded_at = COALESCE(refunded_at, NOW()), updated_at = NOW()
                WHERE id = @id;";
            await using (var updateCmd = new NpgsqlCommand(updateSql, connection, transaction))
            {
                updateCmd.Parameters.Add("@id", NpgsqlDbType.Uuid).Value = escrowId;
                await updateCmd.ExecuteNonQueryAsync();
            }
            await transaction.CommitAsync();
            await SetBookingEscrowStateAsync(connection, bookingId, escrowId, "Refunded");
        }
        catch
        {
            await transaction.RollbackAsync();
            throw;
        }
    }

    public static async Task<WalletSummary> GetWalletSummaryAsync(NpgsqlConnection connection, Guid userId)
    {
        await EnsureSchemaAsync(connection);
        const string sql = @"
            SELECT
                COALESCE(SUM(CASE WHEN l.direction = 'Credit' THEN l.amount ELSE -l.amount END), 0) AS available,
                COALESCE((SELECT SUM(e.net_amount) FROM escrow_transactions e WHERE (e.beneficiary_user_id = @userId OR e.payer_user_id = @userId) AND e.status IN ('Held', 'ReleaseReady', 'Disputed')), 0) AS held,
                COALESCE((SELECT SUM(e.net_amount) FROM escrow_transactions e WHERE (e.beneficiary_user_id = @userId OR e.payer_user_id = @userId) AND e.status = 'PendingPayment'), 0) AS pending
            FROM wallet_accounts a
            LEFT JOIN wallet_ledger_entries l ON l.wallet_account_id = a.id
            WHERE a.user_id = @userId AND a.currency = 'PHP';";
        await using var cmd = new NpgsqlCommand(sql, connection);
        cmd.Parameters.Add("@userId", NpgsqlDbType.Uuid).Value = userId;
        await using var reader = await cmd.ExecuteReaderAsync();
        if (!await reader.ReadAsync()) return new WalletSummary(0, 0, 0, "PHP");
        return new WalletSummary(reader.GetDecimal(0), reader.GetDecimal(1), reader.GetDecimal(2), "PHP");
    }

    public static async Task<IReadOnlyList<WalletLedgerItem>> GetLedgerAsync(NpgsqlConnection connection, Guid userId, int limit, int offset)
    {
        await EnsureSchemaAsync(connection);
        const string sql = @"
            SELECT l.id, l.booking_id, l.entry_type, l.direction, l.amount, l.currency,
                   l.reference_type, l.reference_id, l.created_at
            FROM wallet_ledger_entries l
            INNER JOIN wallet_accounts a ON a.id = l.wallet_account_id
            WHERE a.user_id = @userId
            ORDER BY l.created_at DESC
            LIMIT @limit OFFSET @offset;";
        await using var cmd = new NpgsqlCommand(sql, connection);
        cmd.Parameters.Add("@userId", NpgsqlDbType.Uuid).Value = userId;
        cmd.Parameters.Add("@limit", NpgsqlDbType.Integer).Value = Math.Clamp(limit, 1, 100);
        cmd.Parameters.Add("@offset", NpgsqlDbType.Integer).Value = Math.Max(offset, 0);
        var items = new List<WalletLedgerItem>();
        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            items.Add(new WalletLedgerItem(reader.GetGuid(0), reader.IsDBNull(1) ? null : reader.GetGuid(1), reader.GetString(2), reader.GetString(3), reader.GetDecimal(4), reader.GetString(5), reader.GetString(6), reader.GetString(7), reader.GetDateTime(8)));
        }
        return items;
    }

    private static async Task SetBookingEscrowStateAsync(NpgsqlConnection connection, Guid bookingId, Guid escrowId, string status)
    {
        const string sql = @"
            UPDATE bookings
            SET escrow_transaction_id = @escrowId, escrow_status = @status, updated_at = NOW()
            WHERE id = @bookingId;";
        await using var cmd = new NpgsqlCommand(sql, connection);
        cmd.Parameters.Add("@escrowId", NpgsqlDbType.Uuid).Value = escrowId;
        cmd.Parameters.Add("@status", NpgsqlDbType.Text).Value = status;
        cmd.Parameters.Add("@bookingId", NpgsqlDbType.Uuid).Value = bookingId;
        await cmd.ExecuteNonQueryAsync();
    }

    private static async Task<Guid> EnsureWalletAccountAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, Guid userId, string currency)
    {
        const string sql = @"
            INSERT INTO wallet_accounts (id, user_id, currency, status)
            VALUES (@id, @userId, @currency, 'Active')
            ON CONFLICT (user_id, currency) DO UPDATE SET updated_at = NOW()
            RETURNING id;";
        await using var cmd = new NpgsqlCommand(sql, connection, transaction);
        cmd.Parameters.Add("@id", NpgsqlDbType.Uuid).Value = Guid.NewGuid();
        cmd.Parameters.Add("@userId", NpgsqlDbType.Uuid).Value = userId;
        cmd.Parameters.Add("@currency", NpgsqlDbType.Text).Value = currency;
        return (Guid)(await cmd.ExecuteScalarAsync())!;
    }

    private static async Task InsertLedgerEntryAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid walletAccountId,
        Guid? escrowTransactionId,
        Guid? bookingId,
        string entryType,
        string direction,
        decimal amount,
        string referenceType,
        string referenceId,
        string idempotencyKey)
    {
        const string sql = @"
            INSERT INTO wallet_ledger_entries (
                id, wallet_account_id, escrow_transaction_id, booking_id, entry_type, direction,
                amount, currency, reference_type, reference_id, idempotency_key
            ) VALUES (
                @id, @walletAccountId, @escrowTransactionId, @bookingId, @entryType, @direction,
                @amount, 'PHP', @referenceType, @referenceId, @idempotencyKey
            ) ON CONFLICT (idempotency_key) DO NOTHING;";
        await using var cmd = new NpgsqlCommand(sql, connection, transaction);
        cmd.Parameters.Add("@id", NpgsqlDbType.Uuid).Value = Guid.NewGuid();
        cmd.Parameters.Add("@walletAccountId", NpgsqlDbType.Uuid).Value = walletAccountId;
        cmd.Parameters.Add("@escrowTransactionId", NpgsqlDbType.Uuid).Value = (object?)escrowTransactionId ?? DBNull.Value;
        cmd.Parameters.Add("@bookingId", NpgsqlDbType.Uuid).Value = (object?)bookingId ?? DBNull.Value;
        cmd.Parameters.Add("@entryType", NpgsqlDbType.Text).Value = entryType;
        cmd.Parameters.Add("@direction", NpgsqlDbType.Text).Value = direction;
        cmd.Parameters.Add("@amount", NpgsqlDbType.Numeric).Value = amount;
        cmd.Parameters.Add("@referenceType", NpgsqlDbType.Text).Value = referenceType;
        cmd.Parameters.Add("@referenceId", NpgsqlDbType.Text).Value = referenceId;
        cmd.Parameters.Add("@idempotencyKey", NpgsqlDbType.Text).Value = idempotencyKey;
        await cmd.ExecuteNonQueryAsync();
    }
}
