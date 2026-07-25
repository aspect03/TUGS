using Npgsql;
using NpgsqlTypes;

namespace ImajinationAPI.Controllers
{
    internal static class PlatformFeatureSupport
    {
        public static async Task EnsureSharedBusinessSchemaAsync(NpgsqlConnection connection)
        {
            const string sql = @"
                ALTER TABLE users ADD COLUMN IF NOT EXISTS base_price numeric NOT NULL DEFAULT 0;
                ALTER TABLE users ADD COLUMN IF NOT EXISTS verification_status varchar(30) NOT NULL DEFAULT 'Not Submitted';
                ALTER TABLE users ADD COLUMN IF NOT EXISTS verification_level varchar(40) NULL;
                ALTER TABLE users ADD COLUMN IF NOT EXISTS verification_method varchar(60) NULL;
                ALTER TABLE users ADD COLUMN IF NOT EXISTS verification_notes text NULL;
                ALTER TABLE users ADD COLUMN IF NOT EXISTS verification_last_submitted_at timestamptz NULL;
                ALTER TABLE users ADD COLUMN IF NOT EXISTS verification_reviewed_at timestamptz NULL;

                ALTER TABLE events ADD COLUMN IF NOT EXISTS sale_name text NULL;
                ALTER TABLE events ADD COLUMN IF NOT EXISTS sale_type varchar(20) NULL;
                ALTER TABLE events ADD COLUMN IF NOT EXISTS sale_value numeric NULL;
                ALTER TABLE events ADD COLUMN IF NOT EXISTS sale_starts_at timestamptz NULL;
                ALTER TABLE events ADD COLUMN IF NOT EXISTS sale_ends_at timestamptz NULL;
                ALTER TABLE events ADD COLUMN IF NOT EXISTS max_tickets_per_customer integer NOT NULL DEFAULT 5;

                CREATE TABLE IF NOT EXISTS user_calendar_blocks (
                    id uuid PRIMARY KEY,
                    user_id uuid NOT NULL,
                    role varchar(30) NOT NULL,
                    blocked_date date NOT NULL,
                    reason text NULL,
                    created_at timestamptz NOT NULL DEFAULT NOW(),
                    UNIQUE (user_id, role, blocked_date)
                );

                ALTER TABLE user_calendar_blocks
                    ADD COLUMN IF NOT EXISTS blocked_date date;

                ALTER TABLE user_calendar_blocks
                    ADD COLUMN IF NOT EXISTS block_date date;

                CREATE INDEX IF NOT EXISTS idx_user_calendar_blocks_user_date
                    ON user_calendar_blocks(user_id, blocked_date);

                CREATE TABLE IF NOT EXISTS event_reviews (
                    id uuid PRIMARY KEY,
                    event_id uuid NOT NULL,
                    customer_id uuid NOT NULL,
                    rating integer NOT NULL,
                    feedback text NULL,
                    created_at timestamptz NOT NULL DEFAULT NOW(),
                    updated_at timestamptz NOT NULL DEFAULT NOW(),
                    UNIQUE (event_id, customer_id)
                );

                CREATE INDEX IF NOT EXISTS idx_event_reviews_event
                    ON event_reviews(event_id);

                CREATE TABLE IF NOT EXISTS entity_reports (
                    id uuid PRIMARY KEY,
                    reporter_user_id uuid NOT NULL,
                    target_entity_id uuid NOT NULL,
                    target_entity_type varchar(30) NOT NULL,
                    reason text NOT NULL,
                    details text NULL,
                    status varchar(30) NOT NULL DEFAULT 'Pending',
                    created_at timestamptz NOT NULL DEFAULT NOW()
                );

                ALTER TABLE entity_reports
                    ADD COLUMN IF NOT EXISTS target_entity_id uuid;

                ALTER TABLE entity_reports
                    ADD COLUMN IF NOT EXISTS target_entity_type varchar(30);

                ALTER TABLE entity_reports
                    ADD COLUMN IF NOT EXISTS target_id uuid;

                ALTER TABLE entity_reports
                    ADD COLUMN IF NOT EXISTS target_type varchar(30);

                ALTER TABLE entity_reports
                    ADD COLUMN IF NOT EXISTS admin_note text NULL;

                ALTER TABLE entity_reports
                    ADD COLUMN IF NOT EXISTS reviewed_at timestamptz NULL;

                CREATE INDEX IF NOT EXISTS idx_entity_reports_target
                    ON entity_reports(target_entity_type, target_entity_id);

                CREATE INDEX IF NOT EXISTS idx_entity_reports_status
                    ON entity_reports(status);

                CREATE TABLE IF NOT EXISTS booking_contracts (
                    id uuid PRIMARY KEY,
                    booking_id uuid NOT NULL UNIQUE,
                    contract_status varchar(30) NOT NULL DEFAULT 'Draft',
                    title text NOT NULL,
                    terms text NOT NULL,
                    agreed_fee numeric NULL,
                    event_date timestamptz NULL,
                    location text NULL,
                    proposed_by_user_id uuid NULL,
                    proposed_by_role varchar(30) NULL,
                    accepted_by_user_id uuid NULL,
                    accepted_by_role varchar(30) NULL,
                    accepted_at timestamptz NULL,
                    revision_number integer NOT NULL DEFAULT 1,
                    last_action varchar(30) NOT NULL DEFAULT 'DraftSaved',
                    created_at timestamptz NOT NULL DEFAULT NOW(),
                    updated_at timestamptz NOT NULL DEFAULT NOW()
                );

                ALTER TABLE booking_contracts
                    ADD COLUMN IF NOT EXISTS booking_id uuid NULL;

                ALTER TABLE booking_contracts
                    ADD COLUMN IF NOT EXISTS contract_status varchar(30) NULL;

                ALTER TABLE booking_contracts
                    ADD COLUMN IF NOT EXISTS title text NULL;

                ALTER TABLE booking_contracts
                    ADD COLUMN IF NOT EXISTS terms text NULL;

                ALTER TABLE booking_contracts
                    ADD COLUMN IF NOT EXISTS agreed_fee numeric NULL;

                ALTER TABLE booking_contracts
                    ADD COLUMN IF NOT EXISTS event_date timestamptz NULL;

                ALTER TABLE booking_contracts
                    ADD COLUMN IF NOT EXISTS location text NULL;

                ALTER TABLE booking_contracts
                    ADD COLUMN IF NOT EXISTS proposed_by_user_id uuid NULL;

                ALTER TABLE booking_contracts
                    ADD COLUMN IF NOT EXISTS proposed_by_role varchar(30) NULL;

                ALTER TABLE booking_contracts
                    ADD COLUMN IF NOT EXISTS accepted_by_user_id uuid NULL;

                ALTER TABLE booking_contracts
                    ADD COLUMN IF NOT EXISTS accepted_by_role varchar(30) NULL;

                ALTER TABLE booking_contracts
                    ADD COLUMN IF NOT EXISTS accepted_at timestamptz NULL;

                ALTER TABLE booking_contracts
                    ADD COLUMN IF NOT EXISTS revision_number integer NOT NULL DEFAULT 1;

                ALTER TABLE booking_contracts
                    ADD COLUMN IF NOT EXISTS last_action varchar(30) NOT NULL DEFAULT 'DraftSaved';

                ALTER TABLE booking_contracts
                    ADD COLUMN IF NOT EXISTS created_at timestamptz NULL;

                ALTER TABLE booking_contracts
                    ADD COLUMN IF NOT EXISTS updated_at timestamptz NULL;

                CREATE TABLE IF NOT EXISTS booking_contract_history (
                    id uuid PRIMARY KEY,
                    booking_id uuid NOT NULL,
                    revision_number integer NOT NULL,
                    action varchar(30) NOT NULL,
                    actor_user_id uuid NULL,
                    actor_role varchar(30) NULL,
                    title text NOT NULL,
                    terms text NOT NULL,
                    proposed_fee numeric NULL,
                    contract_status varchar(30) NOT NULL,
                    created_at timestamptz NOT NULL DEFAULT NOW()
                );

                ALTER TABLE booking_contract_history
                    ADD COLUMN IF NOT EXISTS booking_id uuid NULL;

                ALTER TABLE booking_contract_history
                    ADD COLUMN IF NOT EXISTS revision_number integer NULL;

                ALTER TABLE booking_contract_history
                    ADD COLUMN IF NOT EXISTS action varchar(30) NULL;

                ALTER TABLE booking_contract_history
                    ADD COLUMN IF NOT EXISTS actor_user_id uuid NULL;

                ALTER TABLE booking_contract_history
                    ADD COLUMN IF NOT EXISTS actor_role varchar(30) NULL;

                ALTER TABLE booking_contract_history
                    ADD COLUMN IF NOT EXISTS title text NULL;

                ALTER TABLE booking_contract_history
                    ADD COLUMN IF NOT EXISTS terms text NULL;

                ALTER TABLE booking_contract_history
                    ADD COLUMN IF NOT EXISTS proposed_fee numeric NULL;

                ALTER TABLE booking_contract_history
                    ADD COLUMN IF NOT EXISTS contract_status varchar(30) NULL;

                ALTER TABLE booking_contract_history
                    ADD COLUMN IF NOT EXISTS created_at timestamptz NULL;

                CREATE INDEX IF NOT EXISTS idx_booking_contract_history_booking
                    ON booking_contract_history(booking_id, revision_number DESC);

                CREATE TABLE IF NOT EXISTS talent_verification_requests (
                    id uuid PRIMARY KEY,
                    user_id uuid NOT NULL,
                    role varchar(30) NOT NULL,
                    verification_path varchar(60) NOT NULL,
                    evidence_summary text NOT NULL,
                    portfolio_links text NULL,
                    supporting_links text NULL,
                    reference_name text NULL,
                    reference_contact text NULL,
                    status varchar(30) NOT NULL DEFAULT 'Pending',
                    admin_notes text NULL,
                    created_at timestamptz NOT NULL DEFAULT NOW(),
                    reviewed_at timestamptz NULL
                );";

            const string dmlSql = @"
                UPDATE user_calendar_blocks
                SET blocked_date = COALESCE(blocked_date, block_date),
                    block_date = COALESCE(block_date, blocked_date)
                WHERE blocked_date IS NULL OR block_date IS NULL;

                UPDATE entity_reports
                SET target_entity_id = COALESCE(target_entity_id, target_id),
                    target_id = COALESCE(target_id, target_entity_id),
                    target_entity_type = COALESCE(target_entity_type, target_type),
                    target_type = COALESCE(target_type, target_entity_type)
                WHERE target_entity_id IS NULL
                   OR target_id IS NULL
                   OR target_entity_type IS NULL
                   OR target_type IS NULL;

                UPDATE booking_contracts
                SET contract_status = COALESCE(NULLIF(contract_status, ''), 'Draft'),
                    title = COALESCE(title, 'Performance Contract'),
                    terms = COALESCE(terms, ''),
                    revision_number = COALESCE(revision_number, 1),
                    last_action = COALESCE(NULLIF(last_action, ''), 'DraftSaved'),
                    created_at = COALESCE(created_at, NOW()),
                    updated_at = COALESCE(updated_at, NOW());

                UPDATE booking_contract_history
                SET revision_number = COALESCE(revision_number, 1),
                    action = COALESCE(NULLIF(action, ''), 'DraftSaved'),
                    title = COALESCE(title, 'Performance Contract'),
                    terms = COALESCE(terms, ''),
                    contract_status = COALESCE(NULLIF(contract_status, ''), 'Draft'),
                    created_at = COALESCE(created_at, NOW());";

            await using (var ddlCmd = new NpgsqlCommand(sql, connection))
            {
                await ddlCmd.ExecuteNonQueryAsync();
            }

            await using var dmlCmd = new NpgsqlCommand(dmlSql, connection);
            await dmlCmd.ExecuteNonQueryAsync();
        }

        public static DateTime? NormalizeToUtc(DateTime? value)
        {
            if (!value.HasValue) return null;

            return value.Value.Kind switch
            {
                DateTimeKind.Utc => value.Value,
                DateTimeKind.Local => value.Value.ToUniversalTime(),
                _ => DateTime.SpecifyKind(value.Value, DateTimeKind.Local).ToUniversalTime()
            };
        }

        public static DateOnly NormalizeDateOnly(DateTime value)
        {
            var utc = NormalizeToUtc(value)!;
            return DateOnly.FromDateTime(utc.Value);
        }

        public static async Task<bool> UserHasCalendarBlockAsync(NpgsqlConnection connection, Guid userId, string role, DateTime? eventDate)
        {
            if (userId == Guid.Empty || !eventDate.HasValue) return false;

            await EnsureSharedBusinessSchemaAsync(connection);
            var blockDate = NormalizeDateOnly(eventDate.Value);

            const string sql = @"
                SELECT EXISTS (
                    SELECT 1
                    FROM user_calendar_blocks
                    WHERE user_id = @userId
                      AND LOWER(role) = LOWER(@role)
                      AND blocked_date = @blockDate
                );";

            await using var cmd = new NpgsqlCommand(sql, connection);
            cmd.Parameters.Add("@userId", NpgsqlDbType.Uuid).Value = userId;
            cmd.Parameters.Add("@role", NpgsqlDbType.Text).Value = role;
            cmd.Parameters.Add("@blockDate", NpgsqlDbType.Date).Value = blockDate;
            var result = await cmd.ExecuteScalarAsync();
            return result is bool value && value;
        }

        public static async Task<bool> UserHasBookingConflictAsync(NpgsqlConnection connection, Guid userId, DateTime? eventDate, Guid? ignoreBookingId = null)
        {
            if (userId == Guid.Empty || !eventDate.HasValue) return false;

            var blockDate = NormalizeDateOnly(eventDate.Value);
            const string sql = @"
                SELECT EXISTS (
                    SELECT 1
                    FROM bookings
                    WHERE target_user_id = @userId
                      AND event_date IS NOT NULL
                      AND status NOT ILIKE 'Cancelled%'
                      AND (@ignoreBookingId IS NULL OR id <> @ignoreBookingId)
                      AND (event_date AT TIME ZONE 'UTC')::date = @blockDate
                );";

            await using var cmd = new NpgsqlCommand(sql, connection);
            cmd.Parameters.Add("@userId", NpgsqlDbType.Uuid).Value = userId;
            cmd.Parameters.Add("@ignoreBookingId", NpgsqlDbType.Uuid).Value = (object?)ignoreBookingId ?? DBNull.Value;
            cmd.Parameters.Add("@blockDate", NpgsqlDbType.Date).Value = blockDate;
            var result = await cmd.ExecuteScalarAsync();
            return result is bool value && value;
        }

        public static async Task<bool> UserHasBookingConflictAsync(
            NpgsqlConnection connection,
            Guid userId,
            DateTime? eventStart,
            DateTime? eventEnd,
            Guid? ignoreBookingId = null)
        {
            if (userId == Guid.Empty || !eventStart.HasValue) return false;

            await EnsureSharedBusinessSchemaAsync(connection);
            var normalizedStart = NormalizeToUtc(eventStart);
            var normalizedEnd = NormalizeToUtc(eventEnd) ?? normalizedStart?.AddHours(4);
            if (!normalizedStart.HasValue || !normalizedEnd.HasValue)
            {
                return false;
            }

            if (normalizedEnd <= normalizedStart)
            {
                normalizedEnd = normalizedStart.Value.AddHours(1);
            }

            const string sql = @"
                SELECT EXISTS (
                    SELECT 1
                    FROM bookings
                    WHERE target_user_id = @userId
                      AND event_date IS NOT NULL
                      AND status NOT ILIKE 'Cancelled%'
                      AND LOWER(COALESCE(status, 'Pending')) <> 'completed'
                      AND (@ignoreBookingId IS NULL OR id <> @ignoreBookingId)
                      AND COALESCE(event_end_time, event_date + INTERVAL '4 hours') > @eventStart
                      AND event_date < @eventEnd
                );";

            await using var cmd = new NpgsqlCommand(sql, connection);
            cmd.Parameters.Add("@userId", NpgsqlDbType.Uuid).Value = userId;
            cmd.Parameters.Add("@ignoreBookingId", NpgsqlDbType.Uuid).Value = (object?)ignoreBookingId ?? DBNull.Value;
            cmd.Parameters.Add("@eventStart", NpgsqlDbType.TimestampTz).Value = normalizedStart.Value;
            cmd.Parameters.Add("@eventEnd", NpgsqlDbType.TimestampTz).Value = normalizedEnd.Value;
            var result = await cmd.ExecuteScalarAsync();
            return result is bool value && value;
        }

        public static async Task EnsureBookingContractAsync(
            NpgsqlConnection connection,
            Guid bookingId,
            string eventTitle,
            decimal? budget,
            DateTime? eventDate,
            string? location,
            string? targetRole)
        {
            await EnsureSharedBusinessSchemaAsync(connection);

            const string sql = @"
                INSERT INTO booking_contracts (
                    id, booking_id, contract_status, title, terms, agreed_fee, event_date, location, revision_number, last_action, created_at, updated_at
                )
                VALUES (
                    @id, @bookingId, 'Draft', @title, @terms, @agreedFee, @eventDate, @location, 1, 'DraftSaved', NOW(), NOW()
                )
                ON CONFLICT (booking_id)
                DO UPDATE SET
                    event_date = EXCLUDED.event_date,
                    location = EXCLUDED.location,
                    updated_at = NOW();";

            var contractTitle = string.IsNullOrWhiteSpace(eventTitle) ? "Performance Contract" : $"Performance Contract - {eventTitle}";
            var contractTerms =
                $"This draft contract reserves the {targetRole ?? "talent"} for the booking subject to organizer/customer confirmation, fee settlement, and final logistics approval.";

            await using var cmd = new NpgsqlCommand(sql, connection);
            cmd.Parameters.Add("@id", NpgsqlDbType.Uuid).Value = Guid.NewGuid();
            cmd.Parameters.Add("@bookingId", NpgsqlDbType.Uuid).Value = bookingId;
            cmd.Parameters.Add("@title", NpgsqlDbType.Text).Value = contractTitle;
            cmd.Parameters.Add("@terms", NpgsqlDbType.Text).Value = contractTerms;
            cmd.Parameters.Add("@agreedFee", NpgsqlDbType.Numeric).Value = (object?)budget ?? DBNull.Value;
            cmd.Parameters.Add("@eventDate", NpgsqlDbType.TimestampTz).Value = (object?)NormalizeToUtc(eventDate) ?? DBNull.Value;
            cmd.Parameters.Add("@location", NpgsqlDbType.Text).Value = (object?)location ?? DBNull.Value;
            await cmd.ExecuteNonQueryAsync();
        }
    }
}
