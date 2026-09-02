using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Authorization;
using Npgsql;
using NpgsqlTypes;
using ImajinationAPI.Models;
using System.Text.Json;
using System.Threading;
using System.Security.Claims;
using ImajinationAPI.Services;

namespace ImajinationAPI.Controllers
{
    [Route("api/[controller]")]
    [ApiController]
    public class EventController : ControllerBase
    {
        private readonly string _connectionString;
        private readonly UploadScanningService _uploadScanningService;
        private static readonly SemaphoreSlim EventLineupSchemaLock = new(1, 1);
        private static volatile bool _eventLineupColumnsEnsured;
        private const int EventListPosterDataUrlLimit = 5_000_000;

        public EventController(IConfiguration configuration, UploadScanningService uploadScanningService)
        {
            _connectionString = ConfigurationFallbacks.GetRequiredSupabaseConnectionString(configuration);
            _uploadScanningService = uploadScanningService;
        }

        private Guid? GetActorUserId() =>
            Guid.TryParse(User.FindFirstValue(ClaimTypes.NameIdentifier), out var parsedUserId) ? parsedUserId : null;

        private bool IsAdmin() =>
            string.Equals(User.FindFirstValue(ClaimTypes.Role), "Admin", StringComparison.OrdinalIgnoreCase);

        private bool CanManageOrganizer(Guid organizerId)
        {
            var actorUserId = GetActorUserId();
            return IsAdmin() || (actorUserId.HasValue && actorUserId.Value == organizerId);
        }

        public class UpdateEventScheduleDaysRequest
        {
            public Guid organizerId { get; set; }
            public List<EventScheduleDayDto>? days { get; set; }
        }

        public class ArchiveEventRequest
        {
            public Guid? organizerId { get; set; }
        }

        private static readonly JsonSerializerOptions LineupJsonOptions = new()
        {
            PropertyNameCaseInsensitive = true
        };

        private static List<string> ValidatePublishReadiness(
            CreateEventDto req,
            string? title,
            string? city,
            string? location,
            string? posterUrl,
            DateTime eventTimeUtc)
        {
            var errors = new List<string>();

            if (string.IsNullOrWhiteSpace(title))
            {
                errors.Add("Event title is required before publishing.");
            }

            if (eventTimeUtc <= DateTime.UtcNow)
            {
                errors.Add("Event date and time must be in the future before publishing.");
            }

            if (string.IsNullOrWhiteSpace(city))
            {
                errors.Add("Event city is required before publishing.");
            }

            if (string.IsNullOrWhiteSpace(location))
            {
                errors.Add("Event venue or full location is required before publishing.");
            }

            if (string.IsNullOrWhiteSpace(posterUrl))
            {
                errors.Add("Event poster is required before publishing.");
            }

            if (req.price < 0 || (req.tierPrice.HasValue && req.tierPrice.Value < 0))
            {
                errors.Add("Ticket prices cannot be negative.");
            }

            var tierCapacity = req.tiers?
                .Where(tier => !string.IsNullOrWhiteSpace(tier.name))
                .Sum(tier => Math.Max(0, tier.totalSlots)) ?? 0;
            var listedCapacity = Math.Max(0, req.slots);
            var legacyTierCapacity = Math.Max(0, req.tierSlots ?? 0);
            var hasTicketCapacity = tierCapacity > 0 || listedCapacity > 0 || legacyTierCapacity > 0;

            if (!hasTicketCapacity)
            {
                errors.Add("At least one ticket slot or ticket tier is required before publishing.");
            }

            if (req.maxTicketsPerCustomer is < 1)
            {
                errors.Add("Max tickets per customer must be at least 1.");
            }

            if (req.minimumAge is not (0 or 13 or 16 or 18))
            {
                errors.Add("Choose an event age restriction: All ages, 13+, 16+, or 18+.");
            }

            if (req.minimumAge > 0 && string.IsNullOrWhiteSpace(req.ageAdvisory))
            {
                errors.Add("A public age advisory is required for restricted events.");
            }

            if (req.saleStartsAt.HasValue && req.saleEndsAt.HasValue &&
                PlatformFeatureSupport.NormalizeToUtc(req.saleStartsAt) >= PlatformFeatureSupport.NormalizeToUtc(req.saleEndsAt))
            {
                errors.Add("Sale end date must be after the sale start date.");
            }

            return errors;
        }

        private async Task EnsureVerifiedGigsTableExists(NpgsqlConnection connection)
        {
            const string sql = @"
                CREATE TABLE IF NOT EXISTS verified_gigs (
                    id uuid PRIMARY KEY,
                    user_id uuid NOT NULL,
                    event_id uuid NOT NULL,
                    role_at_event varchar(30) NOT NULL,
                    verification_status varchar(30) NOT NULL DEFAULT 'Verified',
                    notes text NULL,
                    verified_at timestamptz NOT NULL DEFAULT NOW(),
                    UNIQUE (user_id, event_id, role_at_event)
                );

                CREATE TABLE IF NOT EXISTS event_artists (
                    id uuid PRIMARY KEY,
                    event_id uuid NOT NULL,
                    artist_user_id uuid NOT NULL,
                    booking_id uuid NULL,
                    status varchar(40) NOT NULL DEFAULT 'Confirmed',
                    created_at timestamptz NOT NULL DEFAULT NOW(),
                    UNIQUE (event_id, artist_user_id)
                );

                CREATE TABLE IF NOT EXISTS event_sessionists (
                    id uuid PRIMARY KEY,
                    event_id uuid NOT NULL,
                    sessionist_user_id uuid NOT NULL,
                    booking_id uuid NULL,
                    status varchar(40) NOT NULL DEFAULT 'Confirmed',
                    created_at timestamptz NOT NULL DEFAULT NOW(),
                    UNIQUE (event_id, sessionist_user_id)
                );";

            using var cmd = new NpgsqlCommand(sql, connection);
            await cmd.ExecuteNonQueryAsync();
        }

        private async Task CreateVerifiedGigRecords(NpgsqlConnection connection, Guid eventId)
        {
            await EnsureVerifiedGigsTableExists(connection);
            await EnsureEventLineupColumnsOnce(connection);

            const string eventSql = @"
                SELECT COALESCE(artist_lineup, '[]'), COALESCE(sessionist_lineup, '[]')
                FROM events
                WHERE id = @id;";

            string artistLineupJson = "[]";
            string sessionistLineupJson = "[]";
            using (var eventCmd = new NpgsqlCommand(eventSql, connection))
            {
                eventCmd.Parameters.AddWithValue("@id", eventId);
                using var reader = await eventCmd.ExecuteReaderAsync();
                if (await reader.ReadAsync())
                {
                    artistLineupJson = reader.IsDBNull(0) ? "[]" : reader.GetString(0);
                    sessionistLineupJson = reader.IsDBNull(1) ? "[]" : reader.GetString(1);
                }
            }

            var artistIds = new HashSet<Guid>();
            var sessionistIds = new HashSet<Guid>();

            const string eventArtistsSql = "SELECT artist_user_id FROM event_artists WHERE event_id = @eventId;";
            using (var artistCmd = new NpgsqlCommand(eventArtistsSql, connection))
            {
                artistCmd.Parameters.AddWithValue("@eventId", eventId);
                using var reader = await artistCmd.ExecuteReaderAsync();
                while (await reader.ReadAsync())
                {
                    if (!reader.IsDBNull(0)) artistIds.Add(reader.GetGuid(0));
                }
            }

            const string eventSessionistsSql = "SELECT sessionist_user_id FROM event_sessionists WHERE event_id = @eventId;";
            using (var sessionistCmd = new NpgsqlCommand(eventSessionistsSql, connection))
            {
                sessionistCmd.Parameters.AddWithValue("@eventId", eventId);
                using var reader = await sessionistCmd.ExecuteReaderAsync();
                while (await reader.ReadAsync())
                {
                    if (!reader.IsDBNull(0)) sessionistIds.Add(reader.GetGuid(0));
                }
            }

            foreach (var item in DeserializeLineup(artistLineupJson))
            {
                if (item.id != Guid.Empty) artistIds.Add(item.id);
            }

            foreach (var item in DeserializeLineup(sessionistLineupJson))
            {
                if (item.id != Guid.Empty) sessionistIds.Add(item.id);
            }

            const string insertSql = @"
                INSERT INTO verified_gigs (id, user_id, event_id, role_at_event, verification_status, notes, verified_at)
                VALUES (@id, @userId, @eventId, @role, 'Verified', @notes, NOW())
                ON CONFLICT (user_id, event_id, role_at_event)
                DO NOTHING;";

            foreach (var userId in artistIds)
            {
                using var cmd = new NpgsqlCommand(insertSql, connection);
                cmd.Parameters.AddWithValue("@id", Guid.NewGuid());
                cmd.Parameters.AddWithValue("@userId", userId);
                cmd.Parameters.AddWithValue("@eventId", eventId);
                cmd.Parameters.AddWithValue("@role", "Artist");
                cmd.Parameters.AddWithValue("@notes", "Verified from finished event lineup.");
                await cmd.ExecuteNonQueryAsync();
            }

            foreach (var userId in sessionistIds)
            {
                using var cmd = new NpgsqlCommand(insertSql, connection);
                cmd.Parameters.AddWithValue("@id", Guid.NewGuid());
                cmd.Parameters.AddWithValue("@userId", userId);
                cmd.Parameters.AddWithValue("@eventId", eventId);
                cmd.Parameters.AddWithValue("@role", "Sessionist");
                cmd.Parameters.AddWithValue("@notes", "Verified from finished event lineup.");
                await cmd.ExecuteNonQueryAsync();
            }
        }

        private static volatile bool _tierSchemaReady = false;
        private static readonly SemaphoreSlim _tierSchemaLock = new(1, 1);

        private async Task EnsureEventTiersSchema(NpgsqlConnection connection)
        {
            if (_tierSchemaReady) return;
            await _tierSchemaLock.WaitAsync();
            try
            {
                if (_tierSchemaReady) return;
                const string sql = @"
                    CREATE TABLE IF NOT EXISTS event_tiers (
                        id uuid PRIMARY KEY DEFAULT gen_random_uuid(),
                        event_id uuid NOT NULL,
                        name varchar(120) NOT NULL,
                        description text NULL,
                        color varchar(20) NULL,
                        price decimal(12,2) NOT NULL DEFAULT 0,
                        total_slots integer NOT NULL DEFAULT 0,
                        slots_sold integer NOT NULL DEFAULT 0,
                        sort_order integer NOT NULL DEFAULT 0,
                        created_at timestamptz NOT NULL DEFAULT NOW(),
                        UNIQUE (event_id, name)
                    );
                    CREATE INDEX IF NOT EXISTS idx_event_tiers_event ON event_tiers(event_id);";
                await using var cmd = new NpgsqlCommand(sql, connection);
                await cmd.ExecuteNonQueryAsync();
                _tierSchemaReady = true;
            }
            finally { _tierSchemaLock.Release(); }
        }

        private async Task<List<ImajinationAPI.Models.TicketTierDto>> FetchTiersAsync(NpgsqlConnection connection, Guid eventId)
        {
            // slots_sold is calculated live from confirmed tickets so it's always accurate
            // even if the increment column fell out of sync from previous code versions.
            const string sql = @"
                SELECT et.id, et.name, et.description, et.color, et.price, et.total_slots,
                       COALESCE((
                           SELECT SUM(t.quantity)
                           FROM tickets t
                           WHERE t.event_id = et.event_id
                             AND LOWER(COALESCE(t.tier_name, '')) = LOWER(et.name)
                             AND t.payment_method NOT IN ('AwaitingPayment')
                       ), 0) AS slots_sold,
                       et.sort_order
                FROM event_tiers et
                WHERE et.event_id = @eventId
                ORDER BY et.sort_order ASC, et.created_at ASC;";
            var tiers = new List<ImajinationAPI.Models.TicketTierDto>();
            await using var cmd = new NpgsqlCommand(sql, connection);
            cmd.Parameters.AddWithValue("@eventId", eventId);
            await using var rdr = await cmd.ExecuteReaderAsync();
            while (await rdr.ReadAsync())
            {
                tiers.Add(new ImajinationAPI.Models.TicketTierDto
                {
                    id = rdr.GetGuid(0),
                    name = rdr.IsDBNull(1) ? "" : rdr.GetString(1),
                    description = rdr.IsDBNull(2) ? null : rdr.GetString(2),
                    color = rdr.IsDBNull(3) ? null : rdr.GetString(3),
                    price = rdr.IsDBNull(4) ? 0 : rdr.GetDecimal(4),
                    totalSlots = rdr.IsDBNull(5) ? 0 : rdr.GetInt32(5),
                    slotsSold = rdr.IsDBNull(6) ? 0 : (int)(long)rdr.GetValue(6),
                    sortOrder = rdr.IsDBNull(7) ? 0 : rdr.GetInt32(7)
                });
            }
            return tiers;
        }

        private async Task SyncTiersAsync(NpgsqlConnection connection, Guid eventId, List<ImajinationAPI.Models.TicketTierDto> tiers)
        {
            await EnsureEventTiersSchema(connection);
            if (tiers == null || tiers.Count == 0) return;

            // Step 1: Delete all existing tiers that have no sales.
            // We'll re-insert everything, so any tier with 0 sales can be safely removed.
            // Tiers that have sales are preserved via the UPSERT conflict resolution below.
            const string delSql = @"
                DELETE FROM event_tiers
                WHERE event_id = @eventId
                  AND slots_sold = 0;";
            await using (var delCmd = new NpgsqlCommand(delSql, connection))
            {
                delCmd.Parameters.AddWithValue("@eventId", eventId);
                await delCmd.ExecuteNonQueryAsync();
            }

            // Step 2: Insert all tiers. ON CONFLICT preserves sold counts for tiers
            // that had sales (they won't be deleted above) and re-adds zero-sales tiers.
            int order = 0;
            foreach (var tier in tiers)
            {
                var tierName = SecuritySupport.SanitizePlainText(tier.name, 120, false) ?? "Tier";
                if (string.IsNullOrWhiteSpace(tierName)) continue;
                var tierDesc = SecuritySupport.SanitizePlainText(tier.description, 300, false);

                const string upsertSql = @"
                    INSERT INTO event_tiers (id, event_id, name, description, color, price, total_slots, sort_order)
                    VALUES (@id, @eventId, @name, @desc, @color, @price, @slots, @order)
                    ON CONFLICT (event_id, name) DO UPDATE
                        SET description = EXCLUDED.description,
                            color       = EXCLUDED.color,
                            price       = EXCLUDED.price,
                            total_slots = EXCLUDED.total_slots,
                            sort_order  = EXCLUDED.sort_order;";
                await using var upsertCmd = new NpgsqlCommand(upsertSql, connection);
                upsertCmd.Parameters.AddWithValue("@id", Guid.NewGuid());
                upsertCmd.Parameters.AddWithValue("@eventId", eventId);
                upsertCmd.Parameters.AddWithValue("@name", tierName);
                upsertCmd.Parameters.AddWithValue("@desc", (object?)
                    (string.IsNullOrWhiteSpace(tierDesc) ? null : tierDesc) ?? DBNull.Value);
                upsertCmd.Parameters.AddWithValue("@color", (object?)
                    (string.IsNullOrWhiteSpace(tier.color) ? null : tier.color) ?? DBNull.Value);
                upsertCmd.Parameters.AddWithValue("@price", Math.Max(0m, tier.price));
                upsertCmd.Parameters.AddWithValue("@slots", Math.Max(0, tier.totalSlots));
                upsertCmd.Parameters.AddWithValue("@order", order++);
                await upsertCmd.ExecuteNonQueryAsync();
            }
        }

        private static async Task EnsureEventScheduleSchemaAsync(NpgsqlConnection connection)
        {
            const string sql = @"
                CREATE TABLE IF NOT EXISTS event_schedule_days (
                    id uuid PRIMARY KEY,
                    event_id uuid NOT NULL,
                    day_date date NOT NULL,
                    start_time time NULL,
                    end_time time NULL,
                    label text NULL,
                    sort_order integer NOT NULL DEFAULT 0,
                    created_at timestamptz NOT NULL DEFAULT NOW(),
                    UNIQUE (event_id, day_date, sort_order)
                );
                CREATE INDEX IF NOT EXISTS idx_event_schedule_days_event ON event_schedule_days(event_id);";
            await using var cmd = new NpgsqlCommand(sql, connection);
            await cmd.ExecuteNonQueryAsync();
        }

        private static List<EventScheduleDayDto> NormalizeScheduleDays(IEnumerable<EventScheduleDayDto>? days, DateTime fallbackEventTime)
        {
            var normalized = (days ?? Enumerable.Empty<EventScheduleDayDto>())
                .Where(day => day.dayDate != default)
                .Select((day, index) => new EventScheduleDayDto
                {
                    id = day.id,
                    dayDate = day.dayDate.Date,
                    startTime = day.startTime,
                    endTime = day.endTime,
                    label = SecuritySupport.SanitizePlainText(day.label, 120, false),
                    sortOrder = day.sortOrder > 0 ? day.sortOrder : index + 1
                })
                .OrderBy(day => day.dayDate)
                .ThenBy(day => day.sortOrder)
                .Take(14)
                .ToList();

            if (normalized.Count == 0)
            {
                normalized.Add(new EventScheduleDayDto
                {
                    dayDate = fallbackEventTime.Date,
                    startTime = fallbackEventTime.TimeOfDay,
                    label = "Main event",
                    sortOrder = 1
                });
            }

            return normalized;
        }

        private static async Task<List<EventScheduleDayDto>> FetchScheduleDaysAsync(NpgsqlConnection connection, Guid eventId)
        {
            await EnsureEventScheduleSchemaAsync(connection);
            var days = new List<EventScheduleDayDto>();
            const string sql = @"
                SELECT id, day_date, start_time, end_time, COALESCE(label, ''), sort_order
                FROM event_schedule_days
                WHERE event_id = @eventId
                ORDER BY day_date ASC, sort_order ASC;";
            await using var cmd = new NpgsqlCommand(sql, connection);
            cmd.Parameters.AddWithValue("@eventId", eventId);
            await using var reader = await cmd.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                days.Add(new EventScheduleDayDto
                {
                    id = reader.GetGuid(0),
                    dayDate = reader.GetDateTime(1),
                    startTime = reader.IsDBNull(2) ? null : reader.GetFieldValue<TimeSpan>(2),
                    endTime = reader.IsDBNull(3) ? null : reader.GetFieldValue<TimeSpan>(3),
                    label = reader.IsDBNull(4) ? null : reader.GetString(4),
                    sortOrder = reader.IsDBNull(5) ? 0 : reader.GetInt32(5)
                });
            }
            return days;
        }

        private static async Task SyncScheduleDaysAsync(NpgsqlConnection connection, Guid eventId, List<EventScheduleDayDto> days)
        {
            await EnsureEventScheduleSchemaAsync(connection);
            await using (var deleteCmd = new NpgsqlCommand("DELETE FROM event_schedule_days WHERE event_id = @eventId;", connection))
            {
                deleteCmd.Parameters.AddWithValue("@eventId", eventId);
                await deleteCmd.ExecuteNonQueryAsync();
            }

            const string insertSql = @"
                INSERT INTO event_schedule_days (id, event_id, day_date, start_time, end_time, label, sort_order, created_at)
                VALUES (@id, @eventId, @dayDate, @startTime, @endTime, @label, @sortOrder, NOW());";
            foreach (var day in days)
            {
                await using var cmd = new NpgsqlCommand(insertSql, connection);
                cmd.Parameters.AddWithValue("@id", day.id.GetValueOrDefault(Guid.NewGuid()));
                cmd.Parameters.AddWithValue("@eventId", eventId);
                cmd.Parameters.AddWithValue("@dayDate", day.dayDate.Date);
                cmd.Parameters.AddWithValue("@startTime", (object?)day.startTime ?? DBNull.Value);
                cmd.Parameters.AddWithValue("@endTime", (object?)day.endTime ?? DBNull.Value);
                cmd.Parameters.AddWithValue("@label", string.IsNullOrWhiteSpace(day.label) ? DBNull.Value : day.label);
                cmd.Parameters.AddWithValue("@sortOrder", day.sortOrder);
                await cmd.ExecuteNonQueryAsync();
            }
        }

        private async Task EnsureEventLineupColumns(NpgsqlConnection connection)
        {
            const string sql = @"
                ALTER TABLE events ADD COLUMN IF NOT EXISTS artist_lineup text;
                ALTER TABLE events ADD COLUMN IF NOT EXISTS sessionist_lineup text;
                ALTER TABLE events ADD COLUMN IF NOT EXISTS created_at timestamptz NOT NULL DEFAULT NOW();
                ALTER TABLE events ADD COLUMN IF NOT EXISTS sale_quantity_limit integer NULL;
                ALTER TABLE events ADD COLUMN IF NOT EXISTS sale_quantity_used integer NOT NULL DEFAULT 0;
                ALTER TABLE events ADD COLUMN IF NOT EXISTS minimum_age integer NOT NULL DEFAULT 0;
                ALTER TABLE events ADD COLUMN IF NOT EXISTS age_advisory text NULL;";

            using var cmd = new NpgsqlCommand(sql, connection);
            await cmd.ExecuteNonQueryAsync();
        }

        private async Task EnsureTicketAttendanceColumnsExist(NpgsqlConnection connection)
        {
            const string sql = @"
                ALTER TABLE tickets ADD COLUMN IF NOT EXISTS used_quantity integer NOT NULL DEFAULT 0;
                ALTER TABLE tickets ADD COLUMN IF NOT EXISTS used_ticket_units text NOT NULL DEFAULT '';
                UPDATE tickets
                SET used_quantity = CASE
                        WHEN COALESCE(is_used, FALSE) THEN COALESCE(quantity, 0)
                        ELSE COALESCE(used_quantity, 0)
                    END
                WHERE COALESCE(is_used, FALSE)
                  AND COALESCE(used_quantity, 0) = 0;";

            using var cmd = new NpgsqlCommand(sql, connection);
            await cmd.ExecuteNonQueryAsync();
        }

        private async Task EnsureEventLineupColumnsOnce(NpgsqlConnection connection)
        {
            if (_eventLineupColumnsEnsured) return;

            await EventLineupSchemaLock.WaitAsync();
            try
            {
                if (_eventLineupColumnsEnsured) return;

                await EnsureEventLineupColumns(connection);
                _eventLineupColumnsEnsured = true;
            }
            finally
            {
                EventLineupSchemaLock.Release();
            }
        }

        private static List<TalentLineupItemDto> NormalizeLineup(IEnumerable<TalentLineupItemDto>? lineup, string role)
        {
            if (lineup == null) return new List<TalentLineupItemDto>();

            return lineup
                .Where(item => item != null && !string.IsNullOrWhiteSpace(item.displayName))
                .GroupBy(item => item.id != Guid.Empty
                    ? $"id:{item.id}"
                    : $"external:{role}:{item.displayName.Trim().ToLowerInvariant()}")
                .Select(group =>
                {
                    var item = group.First();
                    return new TalentLineupItemDto
                    {
                        id = item.id,
                        displayName = item.displayName.Trim(),
                        role = role,
                        profilePicture = string.IsNullOrWhiteSpace(item.profilePicture) ? null : item.profilePicture
                    };
                })
                .ToList();
        }

        private static string SerializeLineup(IEnumerable<TalentLineupItemDto> lineup) =>
            JsonSerializer.Serialize(lineup, LineupJsonOptions);

        private static List<TalentLineupItemDto> DeserializeLineup(string? raw)
        {
            if (string.IsNullOrWhiteSpace(raw)) return new List<TalentLineupItemDto>();

            try
            {
                return JsonSerializer.Deserialize<List<TalentLineupItemDto>>(raw, LineupJsonOptions) ?? new List<TalentLineupItemDto>();
            }
            catch
            {
                return new List<TalentLineupItemDto>();
            }
        }

        private static string BuildLineupDisplay(CreateEventDto req)
        {
            var linkedNames = NormalizeLineup(req.artistLineup, "Artist")
                .Concat(NormalizeLineup(req.sessionistLineup, "Sessionist"))
                .Select(item => item.displayName)
                .ToList();

            if (linkedNames.Count > 0)
            {
                return string.Join(", ", linkedNames);
            }

            return req.artists ?? "";
        }

        private static List<TalentLineupItemDto> MergeLineupMembers(IEnumerable<TalentLineupItemDto> artists, IEnumerable<TalentLineupItemDto> sessionists)
        {
            return artists.Concat(sessionists)
                .Where(item => item != null && item.id != Guid.Empty)
                .GroupBy(item => item.id)
                .Select(group => group.First())
                .ToList();
        }

        private static async Task NotifyLineupAddedAsync(NpgsqlConnection connection, Guid eventId, string eventTitle, IEnumerable<TalentLineupItemDto> lineup)
        {
            await NotificationSupport.EnsureNotificationsTableExistsAsync(connection);
            foreach (var item in lineup.Where(item => item.id != Guid.Empty))
            {
                await NotificationSupport.InsertNotificationIfNotExistsAsync(
                    connection,
                    item.id,
                    "lineup_added",
                    "Added to event lineup",
                    $"You were added to the lineup for '{eventTitle}'.",
                    eventId,
                    "event",
                    24);
            }
        }

        private static async Task NotifyLineupRemovedAsync(NpgsqlConnection connection, Guid userId, Guid eventId, string eventTitle)
        {
            await NotificationSupport.EnsureNotificationsTableExistsAsync(connection);
            await NotificationSupport.InsertNotificationAsync(
                connection,
                userId,
                "lineup_removed",
                "Removed from event lineup",
                $"You were removed from the lineup for '{eventTitle}'.",
                eventId,
                "event");
        }

        private static async Task<HashSet<Guid>> GetConnectedEventUserIdsAsync(
            NpgsqlConnection connection,
            Guid eventId,
            IEnumerable<TalentLineupItemDto> artistLineup,
            IEnumerable<TalentLineupItemDto> sessionistLineup)
        {
            var userIds = new HashSet<Guid>(
                artistLineup
                    .Concat(sessionistLineup)
                    .Where(item => item.id != Guid.Empty)
                    .Select(item => item.id));

            const string ticketSql = @"
                SELECT DISTINCT customer_id
                FROM tickets
                WHERE event_id = @eventId
                  AND customer_id IS NOT NULL;";

            await using (var ticketCmd = new NpgsqlCommand(ticketSql, connection))
            {
                ticketCmd.Parameters.AddWithValue("@eventId", eventId);
                await using var ticketReader = await ticketCmd.ExecuteReaderAsync();
                while (await ticketReader.ReadAsync())
                {
                    if (!ticketReader.IsDBNull(0))
                    {
                        userIds.Add(ticketReader.GetGuid(0));
                    }
                }
            }

            const string bookingSql = @"
                SELECT DISTINCT target_user_id
                FROM bookings
                WHERE event_id = @eventId
                  AND target_user_id IS NOT NULL;";

            await using (var bookingCmd = new NpgsqlCommand(bookingSql, connection))
            {
                bookingCmd.Parameters.AddWithValue("@eventId", eventId);
                await using var bookingReader = await bookingCmd.ExecuteReaderAsync();
                while (await bookingReader.ReadAsync())
                {
                    if (!bookingReader.IsDBNull(0))
                    {
                        userIds.Add(bookingReader.GetGuid(0));
                    }
                }
            }

            return userIds;
        }

        private static async Task NotifyEventAudienceUpdatedAsync(
            NpgsqlConnection connection,
            IEnumerable<Guid> userIds,
            Guid organizerId,
            Guid eventId,
            string eventTitle,
            string message,
            string notificationType = "event_updated")
        {
            await NotificationSupport.EnsureNotificationsTableExistsAsync(connection);

            foreach (var userId in userIds.Where(id => id != Guid.Empty && id != organizerId).Distinct())
            {
                await NotificationSupport.InsertNotificationIfNotExistsAsync(
                    connection,
                    userId,
                    notificationType,
                    "Event update",
                    message,
                    eventId,
                    "event",
                    6);
            }
        }

        private async Task AutoFinishExpiredEvents(NpgsqlConnection connection, Guid? organizerId = null)
        {
            // Once the calendar day after the event starts, automatically treat it as finished.
            var sql = @"
                UPDATE events
                SET status = 'Finished'
                WHERE COALESCE(status, 'Upcoming') <> 'Finished'
                  AND event_time < @today";

            if (organizerId.HasValue)
            {
                sql += " AND organizer_id = @organizerId";
            }

            using var cmd = new NpgsqlCommand(sql, connection);
            cmd.Parameters.AddWithValue("@today", DateTime.Today);
            if (organizerId.HasValue)
            {
                cmd.Parameters.AddWithValue("@organizerId", organizerId.Value);
            }
            await cmd.ExecuteNonQueryAsync();
        }

        [Authorize]
        [HttpDelete("{eventId}/lineup/{role}/{userId}")]
        public async Task<IActionResult> RemoveLineupMember(Guid eventId, string role, Guid userId)
        {
            try
            {
                await using var connection = new NpgsqlConnection(_connectionString);
                await connection.OpenAsync();
                await EnsureVerifiedGigsTableExists(connection);
                await EnsureEventLineupColumnsOnce(connection);

                const string readSql = @"
                    SELECT COALESCE(artist_lineup, '[]'), COALESCE(sessionist_lineup, '[]'), COALESCE(title, 'This event')
                    FROM events
                    WHERE id = @id;";

                string artistLineupRaw = "[]";
                string sessionistLineupRaw = "[]";
                string eventTitle = "This event";
                using (var readCmd = new NpgsqlCommand(readSql, connection))
                {
                    readCmd.Parameters.AddWithValue("@id", eventId);
                    using var reader = await readCmd.ExecuteReaderAsync();
                    if (!await reader.ReadAsync())
                    {
                        return NotFound(new { message = "Event not found." });
                    }

                    artistLineupRaw = reader.IsDBNull(0) ? "[]" : reader.GetString(0);
                    sessionistLineupRaw = reader.IsDBNull(1) ? "[]" : reader.GetString(1);
                    eventTitle = reader.IsDBNull(2) ? "This event" : reader.GetString(2);
                }

                await using (var ownerCmd = new NpgsqlCommand("SELECT organizer_id FROM events WHERE id = @id LIMIT 1;", connection))
                {
                    ownerCmd.Parameters.AddWithValue("@id", eventId);
                    var organizerId = await ownerCmd.ExecuteScalarAsync();
                    if (organizerId is null || organizerId == DBNull.Value)
                    {
                        return NotFound(new { message = "Event not found." });
                    }

                    if (!CanManageOrganizer((Guid)organizerId))
                    {
                        return Forbid();
                    }
                }

                var normalizedRole = (role ?? string.Empty).Trim().ToLowerInvariant();
                var artistLineup = DeserializeLineup(artistLineupRaw);
                var sessionistLineup = DeserializeLineup(sessionistLineupRaw);
                int removedCount = 0;

                if (normalizedRole == "artist")
                {
                    removedCount = artistLineup.RemoveAll(item => item.id == userId);

                    const string deleteSql = "DELETE FROM event_artists WHERE event_id = @eventId AND artist_user_id = @userId;";
                    using var deleteCmd = new NpgsqlCommand(deleteSql, connection);
                    deleteCmd.Parameters.AddWithValue("@eventId", eventId);
                    deleteCmd.Parameters.AddWithValue("@userId", userId);
                    await deleteCmd.ExecuteNonQueryAsync();
                }
                else if (normalizedRole == "sessionist")
                {
                    removedCount = sessionistLineup.RemoveAll(item => item.id == userId);

                    const string deleteSql = "DELETE FROM event_sessionists WHERE event_id = @eventId AND sessionist_user_id = @userId;";
                    using var deleteCmd = new NpgsqlCommand(deleteSql, connection);
                    deleteCmd.Parameters.AddWithValue("@eventId", eventId);
                    deleteCmd.Parameters.AddWithValue("@userId", userId);
                    await deleteCmd.ExecuteNonQueryAsync();
                }
                else
                {
                    return BadRequest(new { message = "Invalid lineup role." });
                }

                const string updateSql = @"
                    UPDATE events
                    SET artist_lineup = @artistLineup,
                        sessionist_lineup = @sessionistLineup,
                        artists = @artists
                    WHERE id = @id;";

                var mergedDisplay = artistLineup
                    .Concat(sessionistLineup)
                    .Select(item => item.displayName)
                    .Where(name => !string.IsNullOrWhiteSpace(name))
                    .ToList();

                using (var updateCmd = new NpgsqlCommand(updateSql, connection))
                {
                    updateCmd.Parameters.AddWithValue("@artistLineup", SerializeLineup(artistLineup));
                    updateCmd.Parameters.AddWithValue("@sessionistLineup", SerializeLineup(sessionistLineup));
                    updateCmd.Parameters.AddWithValue("@artists", string.Join(", ", mergedDisplay));
                    updateCmd.Parameters.AddWithValue("@id", eventId);
                    await updateCmd.ExecuteNonQueryAsync();
                }

                if (removedCount > 0)
                {
                    await NotifyLineupRemovedAsync(connection, userId, eventId, eventTitle);
                }

                return Ok(new
                {
                    message = removedCount > 0 ? "Lineup member removed." : "Lineup was already up to date.",
                    artistLineup,
                    sessionistLineup
                });
            }
            catch (Exception ex)
            {
                return StatusCode(500, new { message = "Failed to remove lineup member: " + ex.Message });
            }
        }

        // 1. CREATE EVENT
        [Authorize]
        [HttpPost("create")]
        [RequestSizeLimit(100_000_000)]
        public async Task<IActionResult> CreateEvent([FromBody] CreateEventDto req)
        {
            try
            {
                req.minimumAge ??= 0;
                var eventTime = req.time.Kind == DateTimeKind.Unspecified
                    ? DateTime.SpecifyKind(req.time, DateTimeKind.Local).ToUniversalTime()
                    : req.time.ToUniversalTime();

                if (eventTime <= DateTime.UtcNow)
                {
                    return BadRequest(new { message = "Event date and time must be in the future. Past dates are not allowed." });
                }

                await using var connection = new NpgsqlConnection(_connectionString);
                await connection.OpenAsync();
                await EnsureEventLineupColumnsOnce(connection);
                await PlatformFeatureSupport.EnsureSharedBusinessSchemaAsync(connection);
                await CommunitySupport.EnsureCommunitySchemaAsync(connection);
                await NotificationSupport.EnsureNotificationsTableExistsAsync(connection);
                await SecuritySupport.EnsureSecuritySchemaAsync(connection);
                await EnsureEventScheduleSchemaAsync(connection);
                await EnsureEventScheduleSchemaAsync(connection);
                await EnsureEventScheduleSchemaAsync(connection);

                var actorUserId = GetActorUserId();
                if (!actorUserId.HasValue)
                {
                    return Unauthorized(new { message = "Sign in again before creating an event." });
                }

                var actorRole = User.FindFirstValue(ClaimTypes.Role) ?? string.Empty;
                Guid? requestedOrganizerId = req.organizerId.HasValue && req.organizerId.Value != Guid.Empty
                    ? req.organizerId.Value
                    : null;
                Guid? effectiveOrganizerId = IsAdmin()
                    ? requestedOrganizerId
                    : actorUserId.Value;

                if (!IsAdmin() && requestedOrganizerId.HasValue && requestedOrganizerId.Value != actorUserId.Value)
                {
                    return Forbid();
                }

                var sanitizedTitle = SecuritySupport.SanitizePlainText(req.title, 180, false);
                var sanitizedArtists = SecuritySupport.SanitizePlainText(BuildLineupDisplay(req), 600, true);
                var sanitizedDescription = SecuritySupport.SanitizePlainText(req.description, 4000, true);
                var sanitizedCity = SecuritySupport.SanitizePlainText(req.city, 120, false);
                var sanitizedLocation = SecuritySupport.SanitizePlainText(req.location, 200, true);
                var sanitizedGenres = SecuritySupport.SanitizePlainText(req.genres, 300, false);
                var sanitizedEventType = SecuritySupport.SanitizePlainText(req.eventType, 80, false);
                var sanitizedTierName = SecuritySupport.SanitizePlainText(req.tierName, 120, false);
                var sanitizedBundles = SecuritySupport.SanitizePlainText(req.bundles, 1000, true);
                var sanitizedDiscounts = SecuritySupport.SanitizePlainText(req.discounts, 1000, true);
                var sanitizedSponsors = SecuritySupport.SanitizePlainText(req.sponsors, 1000, true);
                var sanitizedSaleName = SecuritySupport.SanitizePlainText(req.saleName, 120, false);
                var sanitizedSaleType = SecuritySupport.SanitizePlainText(req.saleType, 40, false);
                var sanitizedAgeAdvisory = SecuritySupport.SanitizePlainText(req.ageAdvisory, 500, true);
                var sanitizedStatus = SecuritySupport.SanitizePlainText(req.status, 40, false);
                var normalizedStatus = string.Equals(sanitizedStatus, "Draft", StringComparison.OrdinalIgnoreCase) ? "Draft" : "Upcoming";
                var normalizedPoster = SecuritySupport.ValidateAndNormalizeImageDataUrl(req.posterUrl, 3_500_000, out var posterError);
                if (posterError is not null)
                {
                    return BadRequest(new { message = posterError });
                }
                var posterScan = await _uploadScanningService.ScanDataUrlAsync(normalizedPoster, "event poster");
                if (!posterScan.IsClean)
                {
                    return BadRequest(new { message = posterScan.Message });
                }

                if (!string.Equals(normalizedStatus, "Draft", StringComparison.OrdinalIgnoreCase))
                {
                    var publishErrors = ValidatePublishReadiness(
                        req,
                        sanitizedTitle,
                        sanitizedCity,
                        sanitizedLocation,
                        normalizedPoster,
                        eventTime);

                    if (publishErrors.Count > 0)
                    {
                        return BadRequest(new
                        {
                            message = "Complete the required event details before publishing.",
                            details = publishErrors
                        });
                    }
                }

                var normalizedArtistLineup = NormalizeLineup(req.artistLineup, "Artist");
                var normalizedSessionistLineup = NormalizeLineup(req.sessionistLineup, "Sessionist");
                var lineupDisplay = sanitizedArtists;
                var mergedLineup = MergeLineupMembers(normalizedArtistLineup, normalizedSessionistLineup);
                var eventId = Guid.NewGuid();
                var maxTicketsPerCustomer = Math.Clamp(req.maxTicketsPerCustomer ?? 5, 3, 10);
                var normalizedScheduleDays = NormalizeScheduleDays(req.scheduleDays, eventTime);

                string sql = @"
                    INSERT INTO events
                    (id, organizer_id, title, artists, description, event_time, city, location, poster_url, base_price, total_slots, max_tickets_per_customer, event_type, genres, tier_name, tier_price, tier_slots, bundles, discounts, sponsors, sale_name, sale_type, sale_value, sale_starts_at, sale_ends_at, sale_quantity_limit, minimum_age, age_advisory, status, artist_lineup, sessionist_lineup)
                    VALUES
                    (@id, @orgId, @title, @artists, @desc, @time, @city, @loc, @poster, @price, @slots, @maxTicketsPerCustomer, @eType, @genres, @tName, @tPrice, @tSlots, @bund, @disc, @spons, @saleName, @saleType, @saleValue, @saleStartsAt, @saleEndsAt, @saleQtyLimit, @minimumAge, @ageAdvisory, @status, @artistLineup, @sessionistLineup)";

                using var cmd = new NpgsqlCommand(sql, connection);
                cmd.Parameters.AddWithValue("@id", eventId);
                cmd.Parameters.Add("@orgId", NpgsqlDbType.Uuid).Value = (object?)effectiveOrganizerId ?? DBNull.Value;
                cmd.Parameters.AddWithValue("@title", sanitizedTitle);
                cmd.Parameters.AddWithValue("@artists", lineupDisplay);
                cmd.Parameters.AddWithValue("@desc", sanitizedDescription);
                cmd.Parameters.AddWithValue("@time", req.time);
                cmd.Parameters.AddWithValue("@city", string.IsNullOrWhiteSpace(sanitizedCity) ? DBNull.Value : sanitizedCity);
                cmd.Parameters.AddWithValue("@loc", sanitizedLocation);
                cmd.Parameters.AddWithValue("@poster", (object?)normalizedPoster ?? DBNull.Value);
                cmd.Parameters.AddWithValue("@price", req.price);
                cmd.Parameters.AddWithValue("@slots", req.slots);
                cmd.Parameters.AddWithValue("@maxTicketsPerCustomer", maxTicketsPerCustomer);
                
                cmd.Parameters.AddWithValue("@eType", string.IsNullOrWhiteSpace(sanitizedEventType) ? "Live Gig" : sanitizedEventType);
                cmd.Parameters.AddWithValue("@genres", sanitizedGenres);

                cmd.Parameters.AddWithValue("@tName", string.IsNullOrWhiteSpace(sanitizedTierName) ? DBNull.Value : sanitizedTierName);
                cmd.Parameters.AddWithValue("@tPrice", (object?)req.tierPrice ?? DBNull.Value);
                cmd.Parameters.AddWithValue("@tSlots", (object?)req.tierSlots ?? DBNull.Value);
                cmd.Parameters.AddWithValue("@bund", string.IsNullOrWhiteSpace(sanitizedBundles) ? DBNull.Value : sanitizedBundles);
                cmd.Parameters.AddWithValue("@disc", string.IsNullOrWhiteSpace(sanitizedDiscounts) ? DBNull.Value : sanitizedDiscounts);
                cmd.Parameters.AddWithValue("@spons", string.IsNullOrWhiteSpace(sanitizedSponsors) ? DBNull.Value : sanitizedSponsors);
                cmd.Parameters.AddWithValue("@saleName", string.IsNullOrWhiteSpace(sanitizedSaleName) ? DBNull.Value : sanitizedSaleName);
                cmd.Parameters.AddWithValue("@saleType", string.IsNullOrWhiteSpace(sanitizedSaleType) ? DBNull.Value : sanitizedSaleType);
                cmd.Parameters.AddWithValue("@saleValue", (object?)req.saleValue ?? DBNull.Value);
                cmd.Parameters.AddWithValue("@saleStartsAt", (object?)PlatformFeatureSupport.NormalizeToUtc(req.saleStartsAt) ?? DBNull.Value);
                cmd.Parameters.AddWithValue("@saleEndsAt", (object?)PlatformFeatureSupport.NormalizeToUtc(req.saleEndsAt) ?? DBNull.Value);
                cmd.Parameters.AddWithValue("@saleQtyLimit", (object?)req.saleQuantityLimit ?? DBNull.Value);
                cmd.Parameters.AddWithValue("@minimumAge", req.minimumAge ?? 0);
                cmd.Parameters.AddWithValue("@ageAdvisory", (object?)sanitizedAgeAdvisory ?? DBNull.Value);
                cmd.Parameters.AddWithValue("@status", normalizedStatus);
                cmd.Parameters.AddWithValue("@artistLineup", SerializeLineup(normalizedArtistLineup));
                cmd.Parameters.AddWithValue("@sessionistLineup", SerializeLineup(normalizedSessionistLineup));

                await cmd.ExecuteNonQueryAsync();
                await SecuritySupport.LogSecurityEventAsync(
                    connection,
                    actorUserId,
                    actorRole,
                    "event_created",
                    "event",
                    eventId,
                    HttpContext,
                    $"{actorRole} created event '{sanitizedTitle}'.");
                await NotifyLineupAddedAsync(connection, eventId, req.title, mergedLineup);
                await SyncScheduleDaysAsync(connection, eventId, normalizedScheduleDays);

                // Sync ticket tiers (wrapped so tier errors never kill the event save)
                if (req.tiers is { Count: > 0 })
                {
                    try { await SyncTiersAsync(connection, eventId, req.tiers); }
                    catch (Exception tierEx)
                    {
                        // Log but don't fail — event is already saved
                        Console.Error.WriteLine($"[TierSync] Failed for event {eventId}: {tierEx.Message}");
                    }
                }

                return Ok(new { message = normalizedStatus == "Draft" ? "Draft saved successfully!" : "Event successfully created!", eventId });
            }
            catch (Exception ex)
            {
                return StatusCode(500, new { message = "Failed to create event: " + ex.Message });
            }
        }

        // 2. GET ALL EVENTS FOR ORGANIZER
        [Authorize]
        [HttpGet("organizer/{orgId}")]
        public async Task<IActionResult> GetOrganizerEvents(Guid orgId)
        {
            try
            {
                if (!CanManageOrganizer(orgId))
                {
                    return Forbid();
                }

                var events = new List<EventDto>();
                await using var connection = new NpgsqlConnection(_connectionString);
                await connection.OpenAsync();
                await EnsureEventLineupColumnsOnce(connection);
                await EnsureTicketAttendanceColumnsExist(connection);
                await PlatformFeatureSupport.EnsureSharedBusinessSchemaAsync(connection);
                await AutoFinishExpiredEvents(connection, orgId);

                const string sql = @"
                    SELECT
                        e.id,
                        e.title,
                        e.event_time,
                        e.city,
                        e.location,
                        e.base_price,
                        e.total_slots,
                        e.tickets_sold,
                        e.status,
                        e.event_type,
                        e.genres,
                        e.sale_name,
                        e.sale_type,
                        e.sale_value,
                        e.sale_starts_at,
                        e.sale_ends_at,
                        e.artist_lineup,
                        e.sessionist_lineup,
                        COALESCE((
                            SELECT SUM(COALESCE(t.used_quantity, CASE WHEN COALESCE(t.is_used, FALSE) THEN COALESCE(t.quantity, 0) ELSE 0 END))
                            FROM tickets t
                            WHERE t.event_id = e.id
                        ), 0) AS attended_tickets,
                        e.poster_url,
                        COALESCE(e.sale_quantity_limit, 0),
                        COALESCE(e.sale_quantity_used, 0),
                        COALESCE(e.minimum_age, 0),
                        e.age_advisory
                    FROM events e
                    WHERE e.organizer_id = @orgId
                    ORDER BY e.created_at DESC, e.event_time ASC";
                using var cmd = new NpgsqlCommand(sql, connection);
                cmd.Parameters.AddWithValue("@orgId", orgId);

                using var reader = await cmd.ExecuteReaderAsync();
                while (await reader.ReadAsync())
                {
                    events.Add(new EventDto
                    {
                        id = reader.GetGuid(0),
                        title = reader.GetString(1),
                        time = reader.GetDateTime(2),
                        city = reader.IsDBNull(3) ? "" : reader.GetString(3),
                        location = reader.GetString(4),
                        price = reader.GetDecimal(5),
                        slots = reader.GetInt32(6),
                        ticketsSold = reader.IsDBNull(7) ? 0 : reader.GetInt32(7),
                        attendedTickets = reader.IsDBNull(18) ? 0 : Convert.ToInt32(reader.GetInt64(18)),
                        status = reader.IsDBNull(8) ? "Upcoming" : reader.GetString(8),
                        eventType = reader.IsDBNull(9) ? "Live Gig" : reader.GetString(9),
                        genres = reader.IsDBNull(10) ? "" : reader.GetString(10),
                        saleName = reader.IsDBNull(11) ? "" : reader.GetString(11),
                        saleType = reader.IsDBNull(12) ? "" : reader.GetString(12),
                        saleValue = reader.IsDBNull(13) ? null : (decimal?)reader.GetDecimal(13),
                        saleStartsAt = reader.IsDBNull(14) ? null : (DateTime?)reader.GetDateTime(14),
                        saleEndsAt = reader.IsDBNull(15) ? null : (DateTime?)reader.GetDateTime(15),
                        artistLineup = DeserializeLineup(reader.IsDBNull(16) ? null : reader.GetString(16)),
                        sessionistLineup = DeserializeLineup(reader.IsDBNull(17) ? null : reader.GetString(17)),
                        posterUrl = reader.IsDBNull(19) ? null : reader.GetString(19),
                        saleQuantityLimit = reader.IsDBNull(20) ? null : (int?)reader.GetInt32(20),
                        saleQuantityUsed = reader.IsDBNull(21) ? null : (int?)reader.GetInt32(21),
                        minimumAge = reader.IsDBNull(22) ? 0 : reader.GetInt32(22),
                        ageAdvisory = reader.IsDBNull(23) ? null : reader.GetString(23)
                    });
                }
                return Ok(events);
            }
            catch (Exception ex)
            {
                return StatusCode(500, new { message = "Error fetching events: " + ex.Message });
            }
        }

        [Authorize(Roles = "Admin")]
        [HttpGet("admin/all")]
        public async Task<IActionResult> GetAdminEvents()
        {
            try
            {
                var events = new List<EventDto>();
                await using var connection = new NpgsqlConnection(_connectionString);
                await connection.OpenAsync();
                await EnsureEventLineupColumnsOnce(connection);
                await EnsureTicketAttendanceColumnsExist(connection);
                await PlatformFeatureSupport.EnsureSharedBusinessSchemaAsync(connection);
                await AutoFinishExpiredEvents(connection);

                const string sql = @"
                    SELECT
                        e.id,
                        e.title,
                        e.event_time,
                        e.city,
                        e.location,
                        e.base_price,
                        e.total_slots,
                        e.tickets_sold,
                        e.status,
                        e.event_type,
                        e.genres,
                        e.sale_name,
                        e.sale_type,
                        e.sale_value,
                        e.sale_starts_at,
                        e.sale_ends_at,
                        e.artist_lineup,
                        e.sessionist_lineup,
                        COALESCE((
                            SELECT SUM(COALESCE(t.used_quantity, CASE WHEN COALESCE(t.is_used, FALSE) THEN COALESCE(t.quantity, 0) ELSE 0 END))
                            FROM tickets t
                            WHERE t.event_id = e.id
                        ), 0) AS attended_tickets,
                        e.poster_url,
                        COALESCE(e.sale_quantity_limit, 0),
                        COALESCE(e.sale_quantity_used, 0)
                    FROM events e
                    ORDER BY e.created_at DESC, e.event_time ASC";

                using var cmd = new NpgsqlCommand(sql, connection);
                using var reader = await cmd.ExecuteReaderAsync();
                while (await reader.ReadAsync())
                {
                    events.Add(new EventDto
                    {
                        id = reader.GetGuid(0),
                        title = reader.GetString(1),
                        time = reader.GetDateTime(2),
                        city = reader.IsDBNull(3) ? "" : reader.GetString(3),
                        location = reader.GetString(4),
                        price = reader.GetDecimal(5),
                        slots = reader.GetInt32(6),
                        ticketsSold = reader.IsDBNull(7) ? 0 : reader.GetInt32(7),
                        attendedTickets = reader.IsDBNull(18) ? 0 : Convert.ToInt32(reader.GetInt64(18)),
                        status = reader.IsDBNull(8) ? "Upcoming" : reader.GetString(8),
                        eventType = reader.IsDBNull(9) ? "Live Gig" : reader.GetString(9),
                        genres = reader.IsDBNull(10) ? "" : reader.GetString(10),
                        saleName = reader.IsDBNull(11) ? "" : reader.GetString(11),
                        saleType = reader.IsDBNull(12) ? "" : reader.GetString(12),
                        saleValue = reader.IsDBNull(13) ? null : (decimal?)reader.GetDecimal(13),
                        saleStartsAt = reader.IsDBNull(14) ? null : (DateTime?)reader.GetDateTime(14),
                        saleEndsAt = reader.IsDBNull(15) ? null : (DateTime?)reader.GetDateTime(15),
                        artistLineup = DeserializeLineup(reader.IsDBNull(16) ? null : reader.GetString(16)),
                        sessionistLineup = DeserializeLineup(reader.IsDBNull(17) ? null : reader.GetString(17)),
                        posterUrl = reader.IsDBNull(19) ? null : reader.GetString(19),
                        saleQuantityLimit = reader.IsDBNull(20) ? null : (int?)reader.GetInt32(20),
                        saleQuantityUsed = reader.IsDBNull(21) ? null : (int?)reader.GetInt32(21)
                    });
                }

                return Ok(events);
            }
            catch (Exception ex)
            {
                return StatusCode(500, new { message = "Error fetching admin events: " + ex.Message });
            }
        }

        [Authorize]
        [HttpGet("organizer/{orgId}/dashboard")]
        public async Task<IActionResult> GetOrganizerDashboard(Guid orgId)
        {
            try
            {
                if (!CanManageOrganizer(orgId))
                {
                    return Forbid();
                }

                await using var connection = new NpgsqlConnection(_connectionString);
                await connection.OpenAsync();
                await EnsureEventLineupColumnsOnce(connection);
                await EnsureTicketAttendanceColumnsExist(connection);
                await AutoFinishExpiredEvents(connection, orgId);

                var now = DateTime.Now;
                var next30Days = now.AddDays(30);
                var startOfYear = new DateTime(now.Year, 1, 1);
                var startOfNextYear = startOfYear.AddYears(1);

                int upcomingEvents;
                int ticketsSold;
                int finishedEvents;
                decimal revenueYtd;
                var recentSales = new List<object>();

                const string statsSql = @"
                    SELECT
                        COUNT(*) FILTER (
                            WHERE status = 'Upcoming'
                              AND event_time >= @now
                              AND event_time < @next30Days
                        ) AS upcoming_events,
                        COALESCE(SUM(COALESCE(tickets_sold, 0)), 0) AS tickets_sold,
                        COUNT(*) FILTER (WHERE status = 'Finished') AS finished_events
                    FROM events
                    WHERE organizer_id = @orgId;";

                using (var statsCmd = new NpgsqlCommand(statsSql, connection))
                {
                    statsCmd.Parameters.AddWithValue("@orgId", orgId);
                    statsCmd.Parameters.AddWithValue("@now", now);
                    statsCmd.Parameters.AddWithValue("@next30Days", next30Days);

                    using var reader = await statsCmd.ExecuteReaderAsync();
                    await reader.ReadAsync();
                    upcomingEvents = reader.IsDBNull(0) ? 0 : reader.GetInt32(0);
                    ticketsSold = reader.IsDBNull(1) ? 0 : Convert.ToInt32(reader.GetInt64(1));
                    finishedEvents = reader.IsDBNull(2) ? 0 : reader.GetInt32(2);
                }

                const string revenueSql = @"
                    SELECT COALESCE(SUM(t.total_price), 0)
                    FROM tickets t
                    INNER JOIN events e ON e.id = t.event_id
                    WHERE e.organizer_id = @orgId
                      AND t.purchase_date >= @startOfYear
                      AND t.purchase_date < @startOfNextYear;";

                using (var revenueCmd = new NpgsqlCommand(revenueSql, connection))
                {
                    revenueCmd.Parameters.AddWithValue("@orgId", orgId);
                    revenueCmd.Parameters.AddWithValue("@startOfYear", startOfYear);
                    revenueCmd.Parameters.AddWithValue("@startOfNextYear", startOfNextYear);

                    var result = await revenueCmd.ExecuteScalarAsync();
                    revenueYtd = result == null || result == DBNull.Value ? 0 : Convert.ToDecimal(result);
                }

                const string recentSalesSql = @"
                    SELECT e.title, t.purchase_date, t.payment_method, t.total_price
                    FROM tickets t
                    INNER JOIN events e ON e.id = t.event_id
                    WHERE e.organizer_id = @orgId
                    ORDER BY t.purchase_date DESC
                    LIMIT 5;";

                using (var recentSalesCmd = new NpgsqlCommand(recentSalesSql, connection))
                {
                    recentSalesCmd.Parameters.AddWithValue("@orgId", orgId);

                    using var reader = await recentSalesCmd.ExecuteReaderAsync();
                    while (await reader.ReadAsync())
                    {
                        recentSales.Add(new
                        {
                            eventTitle = reader.IsDBNull(0) ? "Untitled Event" : reader.GetString(0),
                            purchaseDate = reader.IsDBNull(1) ? now : reader.GetDateTime(1),
                            paymentMethod = reader.IsDBNull(2) ? "Unknown" : reader.GetString(2),
                            totalPrice = reader.IsDBNull(3) ? 0 : reader.GetDecimal(3)
                        });
                    }
                }

                return Ok(new
                {
                    upcomingEvents,
                    ticketsSold,
                    revenueYtd,
                    finishedEvents,
                    recentSales
                });
            }
            catch (Exception ex)
            {
                return StatusCode(500, new { message = "Error fetching organizer dashboard: " + ex.Message });
            }
        }

        [Authorize]
        [HttpGet("organizer/{orgId}/attendees")]
        public async Task<IActionResult> GetOrganizerAttendees(Guid orgId)
        {
            try
            {
                if (!CanManageOrganizer(orgId))
                {
                    return Forbid();
                }

                var attendees = new List<object>();
                await using var connection = new NpgsqlConnection(_connectionString);
                await connection.OpenAsync();
                await EnsureEventLineupColumnsOnce(connection);
                await EnsureTicketAttendanceColumnsExist(connection);
                await AutoFinishExpiredEvents(connection, orgId);

                const string sql = @"
                    SELECT
                        t.id,
                        e.title,
                        COALESCE(u.firstname, ''),
                        COALESCE(u.lastname, ''),
                        COALESCE(u.email, ''),
                        COALESCE(t.tier_name, 'General Admission'),
                        COALESCE(t.quantity, 0),
                        COALESCE(t.total_price, 0),
                        t.purchase_date,
                        COALESCE(t.payment_method, 'Unknown'),
                        COALESCE(t.is_used, FALSE),
                        COALESCE(t.used_quantity, CASE WHEN COALESCE(t.is_used, FALSE) THEN COALESCE(t.quantity, 0) ELSE 0 END),
                        COALESCE(t.used_ticket_units, '')
                    FROM tickets t
                    INNER JOIN events e ON e.id = t.event_id
                    LEFT JOIN users u ON u.id = t.customer_id
                    WHERE e.organizer_id = @orgId
                    ORDER BY t.purchase_date DESC;";

                using var cmd = new NpgsqlCommand(sql, connection);
                cmd.Parameters.AddWithValue("@orgId", orgId);

                using var reader = await cmd.ExecuteReaderAsync();
                while (await reader.ReadAsync())
                {
                    var firstName = reader.IsDBNull(2) ? "" : reader.GetString(2);
                    var lastName = reader.IsDBNull(3) ? "" : reader.GetString(3);

                    var quantity = reader.IsDBNull(6) ? 0 : reader.GetInt32(6);
                    var usedQuantity = reader.IsDBNull(11) ? (!reader.IsDBNull(10) && reader.GetBoolean(10) ? quantity : 0) : reader.GetInt32(11);
                    var usedTicketUnits = TicketController.ParseUsedTicketUnits(reader.IsDBNull(12) ? "" : reader.GetString(12));

                    attendees.Add(new
                    {
                        ticketId = reader.GetGuid(0),
                        eventTitle = reader.IsDBNull(1) ? "Untitled Event" : reader.GetString(1),
                        attendeeName = $"{firstName} {lastName}".Trim(),
                        email = reader.IsDBNull(4) ? "" : reader.GetString(4),
                        tierName = reader.IsDBNull(5) ? "General Admission" : reader.GetString(5),
                        quantity = quantity,
                        totalPrice = reader.IsDBNull(7) ? 0 : reader.GetDecimal(7),
                        purchaseDate = reader.IsDBNull(8) ? DateTime.Now : reader.GetDateTime(8),
                        paymentMethod = reader.IsDBNull(9) ? "Unknown" : reader.GetString(9),
                        isUsed = quantity > 0 && usedQuantity >= quantity,
                        usedQuantity = usedQuantity,
                        usedTicketUnits = usedTicketUnits
                    });
                }

                return Ok(attendees);
            }
            catch (Exception ex)
            {
                return StatusCode(500, new { message = "Error fetching attendees: " + ex.Message });
            }
        }

        [Authorize]
        [HttpGet("organizer/{orgId}/payouts")]
        public async Task<IActionResult> GetOrganizerPayouts(Guid orgId)
        {
            try
            {
                if (!CanManageOrganizer(orgId))
                {
                    return Forbid();
                }

                await using var connection = new NpgsqlConnection(_connectionString);
                await connection.OpenAsync();
                await EnsureEventLineupColumnsOnce(connection);
                await AutoFinishExpiredEvents(connection, orgId);

                decimal totalRevenue = 0;
                int totalTickets = 0;
                int paidTransactions = 0;
                var eventBreakdown = new List<object>();

                const string totalsSql = @"
                    SELECT
                        COALESCE(SUM(t.total_price), 0) AS total_revenue,
                        COALESCE(SUM(t.quantity), 0) AS total_tickets,
                        COUNT(*) AS transactions
                    FROM tickets t
                    INNER JOIN events e ON e.id = t.event_id
                    WHERE e.organizer_id = @orgId;";

                using (var totalsCmd = new NpgsqlCommand(totalsSql, connection))
                {
                    totalsCmd.Parameters.AddWithValue("@orgId", orgId);

                    using var reader = await totalsCmd.ExecuteReaderAsync();
                    if (await reader.ReadAsync())
                    {
                        totalRevenue = reader.IsDBNull(0) ? 0 : reader.GetDecimal(0);
                        totalTickets = reader.IsDBNull(1) ? 0 : Convert.ToInt32(reader.GetInt64(1));
                        paidTransactions = reader.IsDBNull(2) ? 0 : Convert.ToInt32(reader.GetInt64(2));
                    }
                }

                const string eventsSql = @"
                    SELECT
                        e.id,
                        e.title,
                        COALESCE(e.status, 'Upcoming'),
                        COALESCE(SUM(t.total_price), 0) AS gross_revenue,
                        COALESCE(SUM(t.quantity), 0) AS tickets_sold,
                        COUNT(t.id) AS transactions
                    FROM events e
                    LEFT JOIN tickets t ON t.event_id = e.id
                    WHERE e.organizer_id = @orgId
                    GROUP BY e.id, e.title, e.status, e.event_time
                    ORDER BY gross_revenue DESC, e.event_time ASC;";

                using (var eventsCmd = new NpgsqlCommand(eventsSql, connection))
                {
                    eventsCmd.Parameters.AddWithValue("@orgId", orgId);

                    using var reader = await eventsCmd.ExecuteReaderAsync();
                    while (await reader.ReadAsync())
                    {
                        eventBreakdown.Add(new
                        {
                            eventId = reader.GetGuid(0),
                            eventTitle = reader.IsDBNull(1) ? "Untitled Event" : reader.GetString(1),
                            status = reader.IsDBNull(2) ? "Upcoming" : reader.GetString(2),
                            grossRevenue = reader.IsDBNull(3) ? 0 : reader.GetDecimal(3),
                            ticketsSold = reader.IsDBNull(4) ? 0 : Convert.ToInt32(reader.GetInt64(4)),
                            transactions = reader.IsDBNull(5) ? 0 : Convert.ToInt32(reader.GetInt64(5))
                        });
                    }
                }

                return Ok(new
                {
                    totalRevenue,
                    totalTickets,
                    paidTransactions,
                    eventBreakdown
                });
            }
            catch (Exception ex)
            {
                return StatusCode(500, new { message = "Error fetching payout data: " + ex.Message });
            }
        }

        [Authorize]
        [HttpGet("organizer/{orgId}/analytics/{eventId}")]
        public async Task<IActionResult> GetOrganizerEventAnalytics(Guid orgId, Guid eventId)
        {
            try
            {
                var isAdmin = IsAdmin();
                if (!isAdmin && !CanManageOrganizer(orgId))
                {
                    return Forbid();
                }

                await using var connection = new NpgsqlConnection(_connectionString);
                await connection.OpenAsync();
                await EnsureEventLineupColumnsOnce(connection);
                await EnsureTicketAttendanceColumnsExist(connection);
                await AutoFinishExpiredEvents(connection, isAdmin ? null : orgId);

                object? summary = null;
                var salesTimeline = new List<object>();
                var tierBreakdown = new List<object>();
                var recentTransactions = new List<object>();

                const string summarySql = @"
                    SELECT
                        e.id,
                        COALESCE(e.title, 'Untitled Event'),
                        COALESCE(e.status, 'Upcoming'),
                        e.event_time,
                        COALESCE(e.city, ''),
                        COALESCE(e.location, ''),
                        COALESCE(e.event_type, ''),
                        COALESCE(e.base_price, 0),
                        COALESCE(e.total_slots, 0),
                        COALESCE(e.tickets_sold, 0),
                        COALESCE(e.artist_lineup, '[]'),
                        COALESCE(e.sessionist_lineup, '[]'),
                        COALESCE(SUM(t.total_price), 0) AS gross_revenue,
                        COALESCE(SUM(t.quantity), 0) AS purchased_tickets,
                        COALESCE(SUM(COALESCE(t.used_quantity, CASE WHEN COALESCE(t.is_used, FALSE) THEN t.quantity ELSE 0 END)), 0) AS used_tickets,
                        COUNT(t.id) AS transactions
                    FROM events e
                    LEFT JOIN tickets t ON t.event_id = e.id
                    WHERE e.id = @eventId
                      AND (@isAdmin = TRUE OR e.organizer_id = @orgId)
                    GROUP BY
                        e.id,
                        e.title,
                        e.status,
                        e.event_time,
                        e.city,
                        e.location,
                        e.event_type,
                        e.base_price,
                        e.total_slots,
                        e.tickets_sold,
                        e.artist_lineup,
                        e.sessionist_lineup;";

                using (var summaryCmd = new NpgsqlCommand(summarySql, connection))
                {
                    summaryCmd.Parameters.AddWithValue("@eventId", eventId);
                    summaryCmd.Parameters.AddWithValue("@isAdmin", isAdmin);
                    summaryCmd.Parameters.AddWithValue("@orgId", orgId);

                    using var reader = await summaryCmd.ExecuteReaderAsync();
                    if (!await reader.ReadAsync())
                    {
                        return NotFound(new { message = isAdmin ? "Event not found." : "Event not found or does not belong to this organizer." });
                    }

                    var artistLineup = DeserializeLineup(reader.IsDBNull(10) ? "[]" : reader.GetString(10));
                    var sessionistLineup = DeserializeLineup(reader.IsDBNull(11) ? "[]" : reader.GetString(11));
                    var totalSlots = reader.IsDBNull(8) ? 0 : reader.GetInt32(8);
                    var soldTickets = reader.IsDBNull(13) ? 0 : Convert.ToInt32(reader.GetInt64(13));
                    var usedTickets = reader.IsDBNull(14) ? 0 : Convert.ToInt32(reader.GetInt64(14));
                    var grossRevenue = reader.IsDBNull(12) ? 0 : reader.GetDecimal(12);
                    var transactions = reader.IsDBNull(15) ? 0 : Convert.ToInt32(reader.GetInt64(15));
                    var remainingTickets = Math.Max(0, totalSlots - soldTickets);
                    var attendanceRate = soldTickets <= 0 ? 0 : Math.Round((decimal)usedTickets / soldTickets * 100m, 1);
                    var sellThroughRate = totalSlots <= 0 ? 0 : Math.Round((decimal)soldTickets / totalSlots * 100m, 1);
                    var averageOrderValue = transactions <= 0 ? 0 : Math.Round(grossRevenue / transactions, 2);

                    summary = new
                    {
                        eventId = reader.GetGuid(0),
                        title = reader.IsDBNull(1) ? "Untitled Event" : reader.GetString(1),
                        status = reader.IsDBNull(2) ? "Upcoming" : reader.GetString(2),
                        eventTime = reader.IsDBNull(3) ? DateTime.Now : reader.GetDateTime(3),
                        city = reader.IsDBNull(4) ? "" : reader.GetString(4),
                        location = reader.IsDBNull(5) ? "" : reader.GetString(5),
                        eventType = reader.IsDBNull(6) ? "" : reader.GetString(6),
                        ticketPrice = reader.IsDBNull(7) ? 0 : reader.GetDecimal(7),
                        totalSlots,
                        ticketsSold = soldTickets,
                        remainingTickets,
                        usedTickets,
                        grossRevenue,
                        transactions,
                        attendanceRate,
                        sellThroughRate,
                        averageOrderValue,
                        artistCount = artistLineup.Count,
                        sessionistCount = sessionistLineup.Count
                    };
                }

                const string timelineSql = @"
                    SELECT
                        DATE_TRUNC('day', purchase_date) AS sale_day,
                        COALESCE(SUM(quantity), 0) AS tickets_sold,
                        COALESCE(SUM(total_price), 0) AS revenue
                    FROM tickets
                    WHERE event_id = @eventId
                    GROUP BY sale_day
                    ORDER BY sale_day ASC;";

                using (var timelineCmd = new NpgsqlCommand(timelineSql, connection))
                {
                    timelineCmd.Parameters.AddWithValue("@eventId", eventId);

                    using var reader = await timelineCmd.ExecuteReaderAsync();
                    while (await reader.ReadAsync())
                    {
                        salesTimeline.Add(new
                        {
                            day = reader.IsDBNull(0) ? DateTime.Now.Date : reader.GetDateTime(0),
                            ticketsSold = reader.IsDBNull(1) ? 0 : Convert.ToInt32(reader.GetInt64(1)),
                            revenue = reader.IsDBNull(2) ? 0 : reader.GetDecimal(2)
                        });
                    }
                }

                const string tiersSql = @"
                    SELECT
                        COALESCE(tier_name, 'General Admission') AS tier_name,
                        COALESCE(SUM(quantity), 0) AS tickets_sold,
                        COALESCE(SUM(total_price), 0) AS revenue,
                        COALESCE(SUM(COALESCE(used_quantity, CASE WHEN COALESCE(is_used, FALSE) THEN quantity ELSE 0 END)), 0) AS used_tickets
                    FROM tickets
                    WHERE event_id = @eventId
                    GROUP BY COALESCE(tier_name, 'General Admission')
                    ORDER BY revenue DESC, tickets_sold DESC, tier_name ASC;";

                using (var tiersCmd = new NpgsqlCommand(tiersSql, connection))
                {
                    tiersCmd.Parameters.AddWithValue("@eventId", eventId);

                    using var reader = await tiersCmd.ExecuteReaderAsync();
                    while (await reader.ReadAsync())
                    {
                        tierBreakdown.Add(new
                        {
                            tierName = reader.IsDBNull(0) ? "General Admission" : reader.GetString(0),
                            ticketsSold = reader.IsDBNull(1) ? 0 : Convert.ToInt32(reader.GetInt64(1)),
                            revenue = reader.IsDBNull(2) ? 0 : reader.GetDecimal(2),
                            usedTickets = reader.IsDBNull(3) ? 0 : Convert.ToInt32(reader.GetInt64(3))
                        });
                    }
                }

                const string recentTransactionsSql = @"
                    SELECT
                        COALESCE(u.firstname, ''),
                        COALESCE(u.lastname, ''),
                        COALESCE(u.email, ''),
                        COALESCE(t.tier_name, 'General Admission'),
                        COALESCE(t.quantity, 0),
                        COALESCE(t.total_price, 0),
                        t.purchase_date,
                        COALESCE(t.payment_method, 'Unknown'),
                        COALESCE(t.is_used, FALSE),
                        COALESCE(t.used_quantity, CASE WHEN COALESCE(t.is_used, FALSE) THEN COALESCE(t.quantity, 0) ELSE 0 END)
                    FROM tickets t
                    LEFT JOIN users u ON u.id = t.customer_id
                    WHERE t.event_id = @eventId
                    ORDER BY t.purchase_date DESC
                    LIMIT 8;";

                using (var transactionsCmd = new NpgsqlCommand(recentTransactionsSql, connection))
                {
                    transactionsCmd.Parameters.AddWithValue("@eventId", eventId);

                    using var reader = await transactionsCmd.ExecuteReaderAsync();
                    while (await reader.ReadAsync())
                    {
                        var firstName = reader.IsDBNull(0) ? "" : reader.GetString(0);
                        var lastName = reader.IsDBNull(1) ? "" : reader.GetString(1);

                        var quantity = reader.IsDBNull(4) ? 0 : reader.GetInt32(4);
                        var usedQuantity = reader.IsDBNull(9) ? (!reader.IsDBNull(8) && reader.GetBoolean(8) ? quantity : 0) : reader.GetInt32(9);

                        recentTransactions.Add(new
                        {
                            attendeeName = $"{firstName} {lastName}".Trim(),
                            email = reader.IsDBNull(2) ? "" : reader.GetString(2),
                            tierName = reader.IsDBNull(3) ? "General Admission" : reader.GetString(3),
                            quantity = quantity,
                            totalPrice = reader.IsDBNull(5) ? 0 : reader.GetDecimal(5),
                            purchaseDate = reader.IsDBNull(6) ? DateTime.Now : reader.GetDateTime(6),
                            paymentMethod = reader.IsDBNull(7) ? "Unknown" : reader.GetString(7),
                            isUsed = quantity > 0 && usedQuantity >= quantity,
                            usedQuantity = usedQuantity
                        });
                    }
                }

                return Ok(new
                {
                    summary,
                    salesTimeline,
                    tierBreakdown,
                    recentTransactions
                });
            }
            catch (Exception ex)
            {
                return StatusCode(500, new { message = "Error fetching event analytics: " + ex.Message });
            }
        }

        [Authorize(Roles = "Admin")]
        [HttpGet("admin/analytics/{eventId}")]
        public Task<IActionResult> GetAdminEventAnalytics(Guid eventId)
        {
            return GetOrganizerEventAnalytics(Guid.Empty, eventId);
        }

        // 3. DELETE EVENT
        [Authorize]
        [HttpDelete("{eventId}")]
        public async Task<IActionResult> DeleteEvent(Guid eventId)
        {
            try
            {
                await using var connection = new NpgsqlConnection(_connectionString);
                await connection.OpenAsync();

                var actorUserId = GetActorUserId();
                if (!actorUserId.HasValue)
                {
                    return Unauthorized(new { message = "Sign in again before deleting an event." });
                }

                string sql = @"
                    DELETE FROM events
                    WHERE id = @id
                      AND (@isAdmin = TRUE OR organizer_id = @organizerId)";
                using var cmd = new NpgsqlCommand(sql, connection);
                cmd.Parameters.AddWithValue("@id", eventId);
                cmd.Parameters.AddWithValue("@isAdmin", IsAdmin());
                cmd.Parameters.AddWithValue("@organizerId", actorUserId.Value);

                int rows = await cmd.ExecuteNonQueryAsync();
                if (rows == 0) return NotFound(new { message = "Event not found." });

                return Ok(new { message = "Event deleted." });
            }
            catch (Exception ex)
            {
                return StatusCode(500, new { message = "Error deleting event: " + ex.Message });
            }
        }

        // 4. FINISH EVENT
        [Authorize]
        [HttpPost("{eventId}/finish")]
        public async Task<IActionResult> FinishEvent(Guid eventId)
        {
            try
            {
                await using var connection = new NpgsqlConnection(_connectionString);
                await connection.OpenAsync();
                await EnsureVerifiedGigsTableExists(connection);

                var actorUserId = GetActorUserId();
                if (!actorUserId.HasValue)
                {
                    return Unauthorized(new { message = "Sign in again before finishing an event." });
                }

                string updateSql = @"
                    UPDATE events
                    SET status = 'Finished'
                    WHERE id = @id
                      AND (@isAdmin = TRUE OR organizer_id = @organizerId)
                    RETURNING title, base_price, tickets_sold, total_slots";
                using var cmd = new NpgsqlCommand(updateSql, connection);
                cmd.Parameters.AddWithValue("@id", eventId);
                cmd.Parameters.AddWithValue("@isAdmin", IsAdmin());
                cmd.Parameters.AddWithValue("@organizerId", actorUserId.Value);

                using var reader = await cmd.ExecuteReaderAsync();
                if (await reader.ReadAsync())
                {
                    string title = reader.GetString(0);
                    decimal price = reader.GetDecimal(1);
                    int sold = reader.IsDBNull(2) ? 0 : reader.GetInt32(2);
                    int capacity = reader.GetInt32(3);
                    decimal totalRevenue = price * sold;
                    await reader.CloseAsync();
                    await CreateVerifiedGigRecords(connection, eventId);

                    return Ok(new { 
                        message = "Event finished.",
                        report = new { eventName = title, ticketPrice = price, ticketsSold = sold, totalCapacity = capacity, grossRevenue = totalRevenue }
                    });
                }
                return NotFound(new { message = "Event not found." });
            }
            catch (Exception ex)
            {
                return StatusCode(500, new { message = "Error finishing event: " + ex.Message });
            }
        }

        [Authorize]
        [HttpPost("{eventId}/archive")]
        public async Task<IActionResult> ArchiveEvent(Guid eventId, [FromBody] ArchiveEventRequest? req)
        {
            try
            {
                await using var connection = new NpgsqlConnection(_connectionString);
                await connection.OpenAsync();

                var actorRole = User.FindFirstValue(ClaimTypes.Role) ?? string.Empty;
                var actorUserId = Guid.TryParse(User.FindFirstValue(ClaimTypes.NameIdentifier), out var parsedActor)
                    ? parsedActor
                    : Guid.Empty;
                var requestedOrganizerId = req?.organizerId ?? actorUserId;

                const string sql = @"
                    UPDATE events
                    SET status = 'Archived'
                    WHERE id = @id
                      AND COALESCE(status, 'Upcoming') = 'Finished'
                      AND (
                        @isAdmin = TRUE
                        OR organizer_id = @organizerId
                      )
                    RETURNING title;";

                await using var cmd = new NpgsqlCommand(sql, connection);
                cmd.Parameters.AddWithValue("@id", eventId);
                cmd.Parameters.AddWithValue("@isAdmin", actorRole.Equals("Admin", StringComparison.OrdinalIgnoreCase));
                cmd.Parameters.AddWithValue("@organizerId", requestedOrganizerId);

                var title = await cmd.ExecuteScalarAsync() as string;
                if (string.IsNullOrWhiteSpace(title))
                {
                    return NotFound(new { message = "Only finished events owned by you can be archived." });
                }

                await SecuritySupport.EnsureSecuritySchemaAsync(connection);
                await SecuritySupport.LogSecurityEventAsync(
                    connection,
                    actorUserId == Guid.Empty ? null : actorUserId,
                    actorRole,
                    "event_archived",
                    "event",
                    eventId,
                    HttpContext,
                    $"Archived event '{title}'.");

                return Ok(new { message = "Event archived.", status = "Archived" });
            }
            catch (Exception ex)
            {
                return StatusCode(500, new { message = "Error archiving event: " + ex.Message });
            }
        }

        // 5. GET ALL PUBLIC EVENTS (For Events.html)
        [HttpGet("all")]
        public async Task<IActionResult> GetAllEvents()
        {
            try
            {
                var events = new List<object>();
                await using var connection = new NpgsqlConnection(_connectionString);
                await connection.OpenAsync();
                await CommunitySupport.EnsureCommunitySchemaAsync(connection);
                string sql = @"
                    SELECT e.id,
                           e.title,
                           e.event_time,
                           e.city,
                           e.location,
                           e.base_price,
                           e.poster_url,
                           e.event_type,
                           e.genres,
                           e.organizer_id,
                           COALESCE(NULLIF(u.productionname, ''), NULLIF(TRIM(COALESCE(u.firstname, '') || ' ' || COALESCE(u.lastname, '')), ''), 'Unknown Organizer'),
                           COALESCE(u.profile_picture, ''),
                           COALESCE(u.is_verified, FALSE),
                           e.sale_name,
                           e.sale_type,
                           e.sale_value,
                           e.sale_starts_at,
                           e.sale_ends_at,
                           COALESCE(e.artist_lineup, '[]'),
                           COALESCE(e.sale_quantity_limit, 0),
                           COALESCE(e.sale_quantity_used,  0)
                    FROM events e
                    LEFT JOIN users u ON u.id = e.organizer_id
                    WHERE COALESCE(e.status, 'Upcoming') = 'Upcoming'
                      AND e.event_time >= CURRENT_DATE
                    ORDER BY e.created_at DESC, e.event_time ASC
                    LIMIT 12";
                using var cmd = new NpgsqlCommand(sql, connection);
                cmd.CommandTimeout = 5;

                using var reader = await cmd.ExecuteReaderAsync();
                while (await reader.ReadAsync())
                {
                    events.Add(new
                    {
                        id = reader.GetGuid(0),
                        title = reader.GetString(1),
                        time = reader.GetDateTime(2),
                        city = reader.IsDBNull(3) ? "" : reader.GetString(3),
                        location = reader.GetString(4),
                        price = reader.GetDecimal(5),
                        posterUrl = CommunitySupport.NormalizeListImage(
                            reader.IsDBNull(6) ? "" : reader.GetString(6),
                            "https://images.unsplash.com/photo-1514525253161-7a46d19cd819?auto=format&fit=crop&q=80&w=1200",
                            EventListPosterDataUrlLimit),
                        eventType = reader.IsDBNull(7) ? "Live Gig" : reader.GetString(7),
                        genres = reader.IsDBNull(8) ? "" : reader.GetString(8),
                        organizerId = reader.IsDBNull(9) ? Guid.Empty : reader.GetGuid(9),
                        organizerName = reader.IsDBNull(10) ? "Unknown Organizer" : reader.GetString(10),
                        organizerProfilePicture = reader.IsDBNull(11) ? "" : reader.GetString(11),
                        organizerVerified = !reader.IsDBNull(12) && reader.GetBoolean(12),
                        saleName = reader.IsDBNull(13) ? null : reader.GetString(13),
                        saleType = reader.IsDBNull(14) ? null : reader.GetString(14),
                        saleValue = reader.IsDBNull(15) ? null : (decimal?)reader.GetDecimal(15),
                        saleStartsAt = reader.IsDBNull(16) ? null : (DateTime?)reader.GetDateTime(16),
                        saleEndsAt = reader.IsDBNull(17) ? null : (DateTime?)reader.GetDateTime(17),
                        artistLineup = DeserializeLineup(reader.IsDBNull(18) ? null : reader.GetString(18)),
                        saleQuantityLimit = reader.GetInt32(19) > 0 ? reader.GetInt32(19) : (int?)null,
                        saleQuantityUsed  = reader.GetInt32(20),
                        saleExhausted     = reader.GetInt32(19) > 0 && reader.GetInt32(20) >= reader.GetInt32(19)
                    });
                }
                return Ok(events);
            }
            catch (Exception ex)
            {
                return StatusCode(500, new { message = "Error fetching events: " + ex.Message });
            }
        }

        // 5.b GET 6 EVENTS FOR LANDING PAGE CAROUSEL (NEW FIX!)
        [HttpGet("landing")]
        public async Task<IActionResult> GetLandingEvents()
        {
            try
            {
                var events = new List<object>();
                await using var connection = new NpgsqlConnection(_connectionString);
                await connection.OpenAsync();
                await EnsureEventLineupColumnsOnce(connection);
                await PlatformFeatureSupport.EnsureSharedBusinessSchemaAsync(connection);
                await CommunitySupport.EnsureCommunitySchemaAsync(connection);
                string sql = @"
                    SELECT e.id,
                           e.title,
                           e.event_time,
                           e.city,
                           e.location,
                           e.base_price,
                           e.poster_url,
                           e.event_type,
                           e.genres,
                           e.organizer_id,
                           COALESCE(NULLIF(u.productionname, ''), NULLIF(TRIM(COALESCE(u.firstname, '') || ' ' || COALESCE(u.lastname, '')), ''), 'Unknown Organizer'),
                           COALESCE(u.profile_picture, ''),
                           COALESCE(u.is_verified, FALSE),
                           e.sale_name, e.sale_type, e.sale_value, e.sale_starts_at, e.sale_ends_at,
                           COALESCE(e.sale_quantity_limit, 0),
                           COALESCE(e.sale_quantity_used,  0)
                    FROM events e
                    LEFT JOIN users u ON u.id = e.organizer_id
                    WHERE COALESCE(e.status, 'Upcoming') = 'Upcoming'
                      AND e.event_time >= CURRENT_DATE
                    ORDER BY e.created_at DESC, e.event_time ASC
                    LIMIT 6";
                using var cmd = new NpgsqlCommand(sql, connection);
                cmd.CommandTimeout = 5;

                using var reader = await cmd.ExecuteReaderAsync();
                while (await reader.ReadAsync())
                {
                    events.Add(new
                    {
                        id = reader.GetGuid(0),
                        title = reader.GetString(1),
                        time = reader.GetDateTime(2),
                        city = reader.IsDBNull(3) ? "" : reader.GetString(3),
                        location = reader.GetString(4),
                        price = reader.GetDecimal(5),
                        posterUrl = reader.IsDBNull(6) ? "https://images.unsplash.com/photo-1514525253161-7a46d19cd819?auto=format&fit=crop&q=80&w=1200" : reader.GetString(6),
                        eventType = reader.IsDBNull(7) ? "Live Gig" : reader.GetString(7),
                        genres = reader.IsDBNull(8) ? "" : reader.GetString(8),
                        organizerId = reader.IsDBNull(9) ? Guid.Empty : reader.GetGuid(9),
                        organizerName = reader.IsDBNull(10) ? "Unknown Organizer" : reader.GetString(10),
                        organizerProfilePicture = reader.IsDBNull(11) ? "" : reader.GetString(11),
                        organizerVerified = !reader.IsDBNull(12) && reader.GetBoolean(12),
                        saleName     = reader.IsDBNull(13) ? null : reader.GetString(13),
                        saleType     = reader.IsDBNull(14) ? null : reader.GetString(14),
                        saleValue    = reader.IsDBNull(15) ? null : (decimal?)reader.GetDecimal(15),
                        saleStartsAt = reader.IsDBNull(16) ? null : (DateTime?)reader.GetDateTime(16),
                        saleEndsAt   = reader.IsDBNull(17) ? null : (DateTime?)reader.GetDateTime(17),
                        saleQuantityLimit = reader.GetInt32(18) > 0 ? reader.GetInt32(18) : (int?)null,
                        saleQuantityUsed  = reader.GetInt32(19),
                        saleExhausted     = reader.GetInt32(18) > 0 && reader.GetInt32(19) >= reader.GetInt32(18)
                    });
                }
                return Ok(events);
            }
            catch (Exception ex)
            {
                return StatusCode(500, new { message = "Error fetching events: " + ex.Message });
            }
        }

        // 6. GET SINGLE EVENT DETAILS
        [HttpGet("{id}")]
        public async Task<IActionResult> GetEventById(Guid id)
        {
            try
            {
                await using var connection = new NpgsqlConnection(_connectionString);
                await connection.OpenAsync();
                await EnsureEventLineupColumnsOnce(connection);
                await EnsureEventTiersSchema(connection);
                await PlatformFeatureSupport.EnsureSharedBusinessSchemaAsync(connection);
                await CommunitySupport.EnsureCommunitySchemaAsync(connection);

                string sql = @"
                    SELECT e.id,
                           e.title,
                           e.artists,
                           e.description,
                           e.event_time,
                           e.city,
                           e.location,
                           e.poster_url,
                           e.base_price,
                           e.total_slots,
                           e.tickets_sold,
                           COALESCE(e.max_tickets_per_customer, 5),
                           e.status,
                           e.event_type,
                           e.genres,
                           e.tier_name,
                           e.tier_price,
                           e.tier_slots,
                           e.bundles,
                           e.sale_name,
                           e.sale_type,
                           e.sale_value,
                           e.sale_starts_at,
                           e.sale_ends_at,
                           COALESCE(e.sale_quantity_limit, 0),
                           COALESCE(e.sale_quantity_used, 0),
                           e.artist_lineup,
                           e.sessionist_lineup,
                           e.organizer_id,
                           COALESCE(NULLIF(u.productionname, ''), NULLIF(TRIM(COALESCE(u.firstname, '') || ' ' || COALESCE(u.lastname, '')), ''), 'Unknown Organizer'),
                           COALESCE(u.profile_picture, ''),
                           COALESCE(u.bio, ''),
                           COALESCE(u.is_verified, FALSE),
                           COALESCE(u.email, ''),
                           COALESCE(e.minimum_age, 0),
                           e.age_advisory
                    FROM events e
                    LEFT JOIN users u ON u.id = e.organizer_id
                    WHERE e.id = @id";
                using var cmd = new NpgsqlCommand(sql, connection);
                cmd.Parameters.AddWithValue("@id", id);
                cmd.CommandTimeout = 5;

                using var reader = await cmd.ExecuteReaderAsync(System.Data.CommandBehavior.SingleRow);
                if (!await reader.ReadAsync())
                {
                    return NotFound(new { message = "Event not found." });
                }

                // Read all columns into local variables so we can close the reader and fetch tiers
                var evId        = reader.GetGuid(0);
                var evTitle     = reader.IsDBNull(1)  ? "Untitled Event" : reader.GetString(1);
                var evArtists   = reader.IsDBNull(2)  ? "TBA"            : reader.GetString(2);
                var evDesc      = reader.IsDBNull(3)  ? "No description available." : reader.GetString(3);
                var evTime      = reader.GetDateTime(4);
                var evCity      = reader.IsDBNull(5)  ? ""               : reader.GetString(5);
                var evLocation  = reader.IsDBNull(6)  ? "TBA"            : reader.GetString(6);
                var evPoster    = reader.IsDBNull(7)  ? "https://images.unsplash.com/photo-1492684223066-81342ee5ff30?auto=format&fit=crop&q=80&w=1600" : reader.GetString(7);
                var evPrice     = reader.IsDBNull(8)  ? 0m               : reader.GetDecimal(8);
                var evSlots     = reader.IsDBNull(9)  ? 0                : reader.GetInt32(9);
                var evSold      = reader.IsDBNull(10) ? 0                : reader.GetInt32(10);
                var evMaxTix    = reader.IsDBNull(11) ? 5                : reader.GetInt32(11);
                var evStatus    = reader.IsDBNull(12) ? "Upcoming"       : reader.GetString(12);
                var evType      = reader.IsDBNull(13) ? "Live Gig"       : reader.GetString(13);
                var evGenres    = reader.IsDBNull(14) ? ""               : reader.GetString(14);
                var evTierName  = reader.IsDBNull(15) ? null             : reader.GetString(15);
                var evTierPrice = reader.IsDBNull(16) ? null             : (decimal?)reader.GetDecimal(16);
                var evTierSlots = reader.IsDBNull(17) ? null             : (int?)reader.GetInt32(17);
                var evBundles   = reader.IsDBNull(18) ? null             : reader.GetString(18);
                var evSaleName  = reader.IsDBNull(19) ? null             : reader.GetString(19);
                var evSaleType  = reader.IsDBNull(20) ? null             : reader.GetString(20);
                var evSaleVal   = reader.IsDBNull(21) ? null             : (decimal?)reader.GetDecimal(21);
                var evSaleStart    = reader.IsDBNull(22) ? null             : (DateTime?)reader.GetDateTime(22);
                var evSaleEnd      = reader.IsDBNull(23) ? null             : (DateTime?)reader.GetDateTime(23);
                var evSaleQtyLimit = reader.GetInt32(24);  // COALESCE → always int
                var evSaleQtyUsed  = reader.GetInt32(25);  // COALESCE → always int
                var evAL           = DeserializeLineup(reader.IsDBNull(26)  ? null : reader.GetString(26));
                var evSL           = DeserializeLineup(reader.IsDBNull(27)  ? null : reader.GetString(27));
                var evOrgId        = reader.IsDBNull(28) ? Guid.Empty       : reader.GetGuid(28);
                var evOrgName      = reader.IsDBNull(29) ? "Unknown Organizer" : reader.GetString(29);
                var evOrgPic       = reader.IsDBNull(30) ? ""               : reader.GetString(30);
                var evOrgBio       = reader.IsDBNull(31) ? ""               : reader.GetString(31);
                var evOrgVer       = !reader.IsDBNull(32) && reader.GetBoolean(32);
                var evOrgEmail     = reader.IsDBNull(33) ? ""               : reader.GetString(33);
                var evMinimumAge   = reader.IsDBNull(34) ? 0                : reader.GetInt32(34);
                var evAgeAdvisory  = reader.IsDBNull(35) ? null             : reader.GetString(35);
                await reader.CloseAsync();

                // Fetch tiers with live slot counts (separate query, reader already closed)
                var tiers = await FetchTiersAsync(connection, evId);
                var scheduleDays = await FetchScheduleDaysAsync(connection, evId);

                // Calculate live ticketsSold from confirmed tickets so it's always accurate
                int liveTicketsSold = evSold;
                try
                {
                    await using var soldCmd = new NpgsqlCommand(
                        @"SELECT COALESCE(SUM(quantity), 0) FROM tickets
                          WHERE event_id = @id AND payment_method NOT IN ('AwaitingPayment')", connection);
                    soldCmd.Parameters.AddWithValue("@id", evId);
                    var soldResult = await soldCmd.ExecuteScalarAsync();
                    liveTicketsSold = Convert.ToInt32(soldResult ?? 0);
                }
                catch { /* fallback to stored value */ }

                return Ok(new
                {
                    id = evId, title = evTitle, artists = evArtists, description = evDesc,
                    time = evTime, city = evCity, location = evLocation, posterUrl = evPoster,
                    price = evPrice, totalSlots = evSlots, ticketsSold = liveTicketsSold,
                    maxTicketsPerCustomer = evMaxTix, status = evStatus,
                    eventType = evType, genres = evGenres,
                    tierName = evTierName, tierPrice = evTierPrice, tierSlots = evTierSlots,
                    bundles = evBundles, saleName = evSaleName, saleType = evSaleType,
                    saleValue = evSaleVal, saleStartsAt = evSaleStart, saleEndsAt = evSaleEnd,
                    saleQuantityLimit = evSaleQtyLimit > 0 ? evSaleQtyLimit : (int?)null,
                    saleQuantityUsed  = evSaleQtyUsed,
                    // saleExhausted = true when limit is set and fully used — frontend uses this to kill the promo instantly
                    saleExhausted = evSaleQtyLimit > 0 && evSaleQtyUsed >= evSaleQtyLimit,
                    artistLineup = evAL, sessionistLineup = evSL,
                    organizerId = evOrgId, organizerName = evOrgName,
                    organizerProfilePicture = evOrgPic, organizerBio = evOrgBio,
                    organizerVerified = evOrgVer, organizerEmail = evOrgEmail,
                    minimumAge = evMinimumAge, ageAdvisory = evAgeAdvisory,
                    tiers,
                    scheduleDays
                });
            }
            catch (Exception ex)
            {
                return StatusCode(500, new { message = "Error fetching event details: " + ex.Message });
            }
        }

        // GET /api/event/{id}/tiers — real-time tier availability
        [HttpGet("{id}/tiers")]
        public async Task<IActionResult> GetEventTiers(Guid id)
        {
            try
            {
                await using var connection = new NpgsqlConnection(_connectionString);
                await connection.OpenAsync();
                await EnsureEventTiersSchema(connection);
                var tiers = await FetchTiersAsync(connection, id);
                return Ok(tiers);
            }
            catch (Exception ex)
            {
                return StatusCode(500, new { message = "Error fetching tiers: " + ex.Message });
            }
        }

        [HttpGet("{id}/schedule-days")]
        public async Task<IActionResult> GetEventScheduleDays(Guid id)
        {
            try
            {
                await using var connection = new NpgsqlConnection(_connectionString);
                await connection.OpenAsync();
                return Ok(await FetchScheduleDaysAsync(connection, id));
            }
            catch (Exception ex)
            {
                return StatusCode(500, new { message = "Failed to fetch event schedule: " + ex.Message });
            }
        }

        [Authorize]
        [HttpPut("{id}/schedule-days")]
        public async Task<IActionResult> UpdateEventScheduleDays(Guid id, [FromBody] UpdateEventScheduleDaysRequest req)
        {
            try
            {
                if (req.organizerId == Guid.Empty || !CanManageOrganizer(req.organizerId))
                {
                    return Forbid();
                }

                await using var connection = new NpgsqlConnection(_connectionString);
                await connection.OpenAsync();
                await EnsureEventScheduleSchemaAsync(connection);

                await using var checkCmd = new NpgsqlCommand("SELECT event_time FROM events WHERE id = @id AND organizer_id = @organizerId LIMIT 1;", connection);
                checkCmd.Parameters.AddWithValue("@id", id);
                checkCmd.Parameters.AddWithValue("@organizerId", req.organizerId);
                var result = await checkCmd.ExecuteScalarAsync();
                if (result is null || result == DBNull.Value)
                {
                    return NotFound(new { message = "Event not found or you don't have permission to edit it." });
                }

                var days = NormalizeScheduleDays(req.days, Convert.ToDateTime(result));
                await SyncScheduleDaysAsync(connection, id, days);
                return Ok(new { message = "Event schedule updated.", scheduleDays = days });
            }
            catch (Exception ex)
            {
                return StatusCode(500, new { message = "Failed to update event schedule: " + ex.Message });
            }
        }

        // 7. UPDATE EVENT
        [Authorize]
        [HttpPut("{id}")]
        [RequestSizeLimit(100_000_000)]
        public async Task<IActionResult> UpdateEvent(Guid id, [FromBody] CreateEventDto req)
        {
            try
            {
                req.minimumAge ??= 0;
                var updatedEventTime = req.time.Kind == DateTimeKind.Unspecified
                    ? DateTime.SpecifyKind(req.time, DateTimeKind.Local).ToUniversalTime()
                    : req.time.ToUniversalTime();

                if (updatedEventTime <= DateTime.UtcNow)
                {
                    return BadRequest(new { message = "Event date and time must be in the future. Past dates are not allowed." });
                }

                await using var connection = new NpgsqlConnection(_connectionString);
                await connection.OpenAsync();
                await EnsureEventLineupColumnsOnce(connection);
                await PlatformFeatureSupport.EnsureSharedBusinessSchemaAsync(connection);
                await CommunitySupport.EnsureCommunitySchemaAsync(connection);
                await NotificationSupport.EnsureNotificationsTableExistsAsync(connection);
                await SecuritySupport.EnsureSecuritySchemaAsync(connection);

                var actorUserId = GetActorUserId();
                if (!actorUserId.HasValue)
                {
                    return Unauthorized(new { message = "Sign in again before updating an event." });
                }

                var actorRole = User.FindFirstValue(ClaimTypes.Role) ?? string.Empty;
                var requestedOrganizerId = req.organizerId.HasValue && req.organizerId.Value != Guid.Empty
                    ? req.organizerId.Value
                    : (Guid?)null;
                var isAdmin = IsAdmin();
                if (!isAdmin && (!requestedOrganizerId.HasValue || !CanManageOrganizer(requestedOrganizerId.Value)))
                {
                    return Forbid();
                }

                var sanitizedTitle = SecuritySupport.SanitizePlainText(req.title, 180, false);
                var sanitizedArtists = SecuritySupport.SanitizePlainText(BuildLineupDisplay(req), 600, true);
                var sanitizedDescription = SecuritySupport.SanitizePlainText(req.description, 4000, true);
                var sanitizedCity = SecuritySupport.SanitizePlainText(req.city, 120, false);
                var sanitizedLocation = SecuritySupport.SanitizePlainText(req.location, 200, true);
                var sanitizedGenres = SecuritySupport.SanitizePlainText(req.genres, 300, false);
                var sanitizedEventType = SecuritySupport.SanitizePlainText(req.eventType, 80, false);
                var sanitizedTierName = SecuritySupport.SanitizePlainText(req.tierName, 120, false);
                var sanitizedBundles = SecuritySupport.SanitizePlainText(req.bundles, 1000, true);
                var sanitizedDiscounts = SecuritySupport.SanitizePlainText(req.discounts, 1000, true);
                var sanitizedSponsors = SecuritySupport.SanitizePlainText(req.sponsors, 1000, true);
                var sanitizedSaleName = SecuritySupport.SanitizePlainText(req.saleName, 120, false);
                var sanitizedSaleType = SecuritySupport.SanitizePlainText(req.saleType, 40, false);
                var sanitizedAgeAdvisory = SecuritySupport.SanitizePlainText(req.ageAdvisory, 500, true);
                var sanitizedStatus = SecuritySupport.SanitizePlainText(req.status, 40, false);
                var normalizedStatus = string.Equals(sanitizedStatus, "Draft", StringComparison.OrdinalIgnoreCase) ? "Draft" : "Upcoming";
                var normalizedPoster = SecuritySupport.ValidateAndNormalizeImageDataUrl(req.posterUrl, 3_500_000, out var posterError);
                if (posterError is not null)
                {
                    return BadRequest(new { message = posterError });
                }
                var posterScan = await _uploadScanningService.ScanDataUrlAsync(normalizedPoster, "event poster");
                if (!posterScan.IsClean)
                {
                    return BadRequest(new { message = posterScan.Message });
                }

                var previousArtistLineup = new List<TalentLineupItemDto>();
                var previousSessionistLineup = new List<TalentLineupItemDto>();
                const string existingSql = @"
                    SELECT COALESCE(artist_lineup, '[]'),
                           COALESCE(sessionist_lineup, '[]'),
                           COALESCE(title, 'This event'),
                           event_time,
                           COALESCE(city, ''),
                           COALESCE(location, ''),
                           organizer_id,
                           COALESCE(poster_url, '')
                    FROM events
                    WHERE id = @id
                      AND (@isAdmin = TRUE OR organizer_id = @orgId);";
                string previousTitle = "This event";
                DateTime previousEventTime = DateTime.UtcNow;
                string previousCity = "";
                string previousLocation = "";
                string previousPosterUrl = "";
                Guid organizerId = requestedOrganizerId ?? Guid.Empty;
                using (var existingCmd = new NpgsqlCommand(existingSql, connection))
                {
                    existingCmd.Parameters.AddWithValue("@id", id);
                    existingCmd.Parameters.AddWithValue("@isAdmin", isAdmin);
                    existingCmd.Parameters.Add("@orgId", NpgsqlDbType.Uuid).Value = (object?)requestedOrganizerId ?? DBNull.Value;
                    using var reader = await existingCmd.ExecuteReaderAsync();
                    if (!await reader.ReadAsync())
                    {
                        return NotFound(new { message = "Event not found or you don't have permission to edit it." });
                    }

                    previousArtistLineup = DeserializeLineup(reader.IsDBNull(0) ? null : reader.GetString(0));
                    previousSessionistLineup = DeserializeLineup(reader.IsDBNull(1) ? null : reader.GetString(1));
                    previousTitle = reader.IsDBNull(2) ? "This event" : reader.GetString(2);
                    previousEventTime = reader.IsDBNull(3) ? DateTime.UtcNow : reader.GetDateTime(3);
                    previousCity = reader.IsDBNull(4) ? "" : reader.GetString(4);
                    previousLocation = reader.IsDBNull(5) ? "" : reader.GetString(5);
                    organizerId = reader.IsDBNull(6) ? Guid.Empty : reader.GetGuid(6);
                    previousPosterUrl = reader.IsDBNull(7) ? "" : reader.GetString(7);
                }

                if (!string.Equals(normalizedStatus, "Draft", StringComparison.OrdinalIgnoreCase))
                {
                    var publishErrors = ValidatePublishReadiness(
                        req,
                        sanitizedTitle,
                        sanitizedCity,
                        sanitizedLocation,
                        normalizedPoster ?? previousPosterUrl,
                        updatedEventTime);

                    if (publishErrors.Count > 0)
                    {
                        return BadRequest(new
                        {
                            message = "Complete the required event details before publishing.",
                            details = publishErrors
                        });
                    }
                }

                var normalizedArtistLineup = NormalizeLineup(req.artistLineup, "Artist");
                var normalizedSessionistLineup = NormalizeLineup(req.sessionistLineup, "Sessionist");
                var lineupDisplay = sanitizedArtists;
                var maxTicketsPerCustomer = Math.Clamp(req.maxTicketsPerCustomer ?? 5, 3, 10);
                var normalizedScheduleDays = NormalizeScheduleDays(req.scheduleDays, updatedEventTime);
                var normalizedEventTime = new DateTime(
                    previousEventTime.Year,
                    previousEventTime.Month,
                    previousEventTime.Day,
                    req.time.Hour,
                    req.time.Minute,
                    req.time.Second,
                    req.time.Kind == DateTimeKind.Unspecified ? DateTimeKind.Utc : req.time.Kind);

                var audienceUserIds = await GetConnectedEventUserIdsAsync(connection, id, previousArtistLineup, previousSessionistLineup);
                audienceUserIds.UnionWith(await GetConnectedEventUserIdsAsync(connection, id, normalizedArtistLineup, normalizedSessionistLineup));

                string sql = @"
                    UPDATE events SET 
                        title = @title, 
                        artists = @artists, 
                        description = @desc, 
                        event_time = @time, 
                        city = @city,
                        location = @loc, 
                        poster_url = COALESCE(@poster, poster_url), 
                        base_price = @price, 
                        total_slots = @slots, 
                        max_tickets_per_customer = @maxTicketsPerCustomer,
                        event_type = @eType, 
                        genres = @genres,
                        tier_name = @tName,
                        tier_price = @tPrice,
                        tier_slots = @tSlots,
                        bundles = @bund,
                        discounts = COALESCE(@disc, discounts),
                        sponsors = COALESCE(@spons, sponsors),
                        sale_name = COALESCE(@saleName, sale_name),
                        sale_type = COALESCE(@saleType, sale_type),
                        sale_value = COALESCE(@saleValue, sale_value),
                        sale_starts_at = COALESCE(@saleStartsAt, sale_starts_at),
                        sale_ends_at = COALESCE(@saleEndsAt, sale_ends_at),
                        sale_quantity_limit = @saleQtyLimit,
                        minimum_age = @minimumAge,
                        age_advisory = @ageAdvisory,
                        sale_quantity_used = 0,
                        status = @status,
                        artist_lineup = @artistLineup,
                        sessionist_lineup = @sessionistLineup
                    WHERE id = @id
                      AND (@isAdmin = TRUE OR organizer_id = @orgId)";

                using var cmd = new NpgsqlCommand(sql, connection);
                cmd.Parameters.AddWithValue("@id", id);
                cmd.Parameters.AddWithValue("@isAdmin", isAdmin);
                cmd.Parameters.Add("@orgId", NpgsqlDbType.Uuid).Value = (object?)requestedOrganizerId ?? DBNull.Value;
                
                cmd.Parameters.AddWithValue("@title", sanitizedTitle);
                cmd.Parameters.AddWithValue("@artists", lineupDisplay);
                cmd.Parameters.AddWithValue("@desc", sanitizedDescription);
                cmd.Parameters.AddWithValue("@time", normalizedEventTime);
                cmd.Parameters.AddWithValue("@city", string.IsNullOrWhiteSpace(sanitizedCity) ? DBNull.Value : sanitizedCity);
                cmd.Parameters.AddWithValue("@loc", sanitizedLocation);
                cmd.Parameters.AddWithValue("@poster", (object?)normalizedPoster ?? DBNull.Value);
                cmd.Parameters.AddWithValue("@price", req.price);
                cmd.Parameters.AddWithValue("@slots", req.slots);
                cmd.Parameters.AddWithValue("@maxTicketsPerCustomer", maxTicketsPerCustomer);
                cmd.Parameters.AddWithValue("@eType", string.IsNullOrWhiteSpace(sanitizedEventType) ? "Live Gig" : sanitizedEventType);
                cmd.Parameters.AddWithValue("@genres", sanitizedGenres);
                
                cmd.Parameters.AddWithValue("@tName", string.IsNullOrWhiteSpace(sanitizedTierName) ? DBNull.Value : sanitizedTierName);
                cmd.Parameters.AddWithValue("@tPrice", (object?)req.tierPrice ?? DBNull.Value);
                cmd.Parameters.AddWithValue("@tSlots", (object?)req.tierSlots ?? DBNull.Value);
                cmd.Parameters.AddWithValue("@bund", string.IsNullOrWhiteSpace(sanitizedBundles) ? DBNull.Value : sanitizedBundles);
                
                cmd.Parameters.AddWithValue("@disc", string.IsNullOrWhiteSpace(sanitizedDiscounts) ? DBNull.Value : sanitizedDiscounts);
                cmd.Parameters.AddWithValue("@spons", string.IsNullOrWhiteSpace(sanitizedSponsors) ? DBNull.Value : sanitizedSponsors);
                cmd.Parameters.AddWithValue("@saleName", string.IsNullOrWhiteSpace(sanitizedSaleName) ? DBNull.Value : sanitizedSaleName);
                cmd.Parameters.AddWithValue("@saleType", string.IsNullOrWhiteSpace(sanitizedSaleType) ? DBNull.Value : sanitizedSaleType);
                cmd.Parameters.AddWithValue("@saleValue", (object?)req.saleValue ?? DBNull.Value);
                cmd.Parameters.AddWithValue("@saleStartsAt", (object?)PlatformFeatureSupport.NormalizeToUtc(req.saleStartsAt) ?? DBNull.Value);
                cmd.Parameters.AddWithValue("@saleEndsAt", (object?)PlatformFeatureSupport.NormalizeToUtc(req.saleEndsAt) ?? DBNull.Value);
                cmd.Parameters.AddWithValue("@saleQtyLimit", (object?)req.saleQuantityLimit ?? DBNull.Value);
                cmd.Parameters.AddWithValue("@minimumAge", req.minimumAge ?? 0);
                cmd.Parameters.AddWithValue("@ageAdvisory", (object?)sanitizedAgeAdvisory ?? DBNull.Value);
                cmd.Parameters.AddWithValue("@status", normalizedStatus);
                cmd.Parameters.AddWithValue("@artistLineup", SerializeLineup(normalizedArtistLineup));
                cmd.Parameters.AddWithValue("@sessionistLineup", SerializeLineup(normalizedSessionistLineup));

                int rows = await cmd.ExecuteNonQueryAsync();
                if (rows == 0) return NotFound(new { message = "Event not found or you don't have permission to edit it." });
                await SyncScheduleDaysAsync(connection, id, normalizedScheduleDays);
                await SecuritySupport.LogSecurityEventAsync(
                    connection,
                    actorUserId,
                    actorRole,
                    "event_updated",
                    "event",
                    id,
                    HttpContext,
                    $"{actorRole} updated event '{sanitizedTitle}'.");

                var previousIds = MergeLineupMembers(previousArtistLineup, previousSessionistLineup).Select(item => item.id).ToHashSet();
                var currentMembers = MergeLineupMembers(normalizedArtistLineup, normalizedSessionistLineup);
                var currentIds = currentMembers.Select(item => item.id).ToHashSet();

                var addedMembers = currentMembers.Where(item => !previousIds.Contains(item.id)).ToList();
                var removedIds = previousIds.Where(idValue => !currentIds.Contains(idValue)).ToList();

                if (addedMembers.Count > 0)
                {
                    await NotifyLineupAddedAsync(connection, id, req.title, addedMembers);
                }

                foreach (var removedId in removedIds)
                {
                    await NotifyLineupRemovedAsync(connection, removedId, id, string.IsNullOrWhiteSpace(req.title) ? previousTitle : req.title);
                }

                var effectiveTitle = string.IsNullOrWhiteSpace(sanitizedTitle) ? previousTitle : sanitizedTitle;
                var changeNotes = new List<string>();
                if (!string.Equals(previousTitle, effectiveTitle, StringComparison.OrdinalIgnoreCase))
                {
                    changeNotes.Add("title");
                }
                if (previousEventTime.TimeOfDay != normalizedEventTime.TimeOfDay)
                {
                    changeNotes.Add("time");
                }
                if (!string.Equals(previousCity, sanitizedCity ?? string.Empty, StringComparison.OrdinalIgnoreCase))
                {
                    changeNotes.Add("city");
                }
                if (!string.Equals(previousLocation, sanitizedLocation ?? string.Empty, StringComparison.OrdinalIgnoreCase))
                {
                    changeNotes.Add("venue");
                }
                if (addedMembers.Count > 0 || removedIds.Count > 0)
                {
                    changeNotes.Add("lineup");
                }
                if (!string.Equals(normalizedStatus, "Draft", StringComparison.OrdinalIgnoreCase))
                {
                    if (!string.Equals(req.description ?? string.Empty, string.Empty, StringComparison.Ordinal) && !changeNotes.Contains("details"))
                    {
                        // Reserve generic copy for non-location event edits that still matter to users.
                    }
                }

                if (changeNotes.Count > 0)
                {
                    var message = $"'{effectiveTitle}' has been updated by the organizer.";
                    var locationChanged = changeNotes.Contains("city") || changeNotes.Contains("venue");
                    if (locationChanged)
                    {
                        var locationBits = new List<string>();
                        if (!string.IsNullOrWhiteSpace(sanitizedLocation)) locationBits.Add(sanitizedLocation);
                        if (!string.IsNullOrWhiteSpace(sanitizedCity)) locationBits.Add(sanitizedCity);
                        message = $"'{effectiveTitle}' has a venue update. New location: {string.Join(", ", locationBits)}.";
                    }
                    else if (changeNotes.Count == 1 && changeNotes[0] == "time")
                    {
                        message = $"'{effectiveTitle}' has a new start time. Check the updated event details before you go.";
                    }
                    else if (changeNotes.Count == 1 && changeNotes[0] == "lineup")
                    {
                        message = $"'{effectiveTitle}' has an updated performer lineup. Open the event to review the latest details.";
                    }
                    else
                    {
                        message = $"'{effectiveTitle}' has updated event details ({string.Join(", ", changeNotes)}). Open the event for the latest information.";
                    }

                    await NotifyEventAudienceUpdatedAsync(
                        connection,
                        audienceUserIds,
                        organizerId,
                        id,
                        effectiveTitle,
                        message,
                        locationChanged ? "event_location_updated" : "event_updated");
                }

                // Sync ticket tiers on update
                if (req.tiers is { Count: > 0 })
                    await SyncTiersAsync(connection, id, req.tiers);

                return Ok(new { message = normalizedStatus == "Draft" ? "Draft updated successfully!" : "Event successfully updated!" });
            }
            catch (Exception ex)
            {
                return StatusCode(500, new { message = "Failed to update event: " + ex.Message });
            }
        }
    }
}
