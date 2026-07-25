using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Npgsql;
using NpgsqlTypes;
using ImajinationAPI.Services;

namespace ImajinationAPI.Controllers
{
    public class JoinWaitlistRequest
    {
        public Guid customerId { get; set; }
        public Guid eventId { get; set; }
        public int quantity { get; set; } = 1;
    }

    [Route("api/[controller]")]
    [ApiController]
    public class WaitlistController : ControllerBase
    {
        private readonly string _connectionString;
        private readonly EmailService _emailService;

        public WaitlistController(IConfiguration configuration, EmailService emailService)
        {
            _connectionString = ConfigurationFallbacks.GetRequiredSupabaseConnectionString(configuration);
            _emailService = emailService;
        }

        private static async Task EnsureSchemaAsync(NpgsqlConnection connection)
        {
            const string sql = @"
                CREATE TABLE IF NOT EXISTS event_waitlist (
                    id uuid PRIMARY KEY DEFAULT gen_random_uuid(),
                    event_id uuid NOT NULL,
                    customer_id uuid NOT NULL,
                    quantity int NOT NULL DEFAULT 1,
                    notified_at timestamptz NULL,
                    notified_expires_at timestamptz NULL,
                    status varchar(30) NOT NULL DEFAULT 'Waiting',
                    joined_at timestamptz NOT NULL DEFAULT NOW(),
                    UNIQUE (event_id, customer_id)
                );
                CREATE INDEX IF NOT EXISTS idx_waitlist_event ON event_waitlist(event_id, status);";
            await using var cmd = new NpgsqlCommand(sql, connection);
            await cmd.ExecuteNonQueryAsync();
        }

        // GET /api/waitlist/event/{eventId} — position + count
        [HttpGet("event/{eventId}")]
        public async Task<IActionResult> GetWaitlistInfo(Guid eventId, [FromQuery] Guid? customerId)
        {
            try
            {
                await using var connection = new NpgsqlConnection(_connectionString);
                await connection.OpenAsync();
                await EnsureSchemaAsync(connection);

                const string sql = @"
                    SELECT COUNT(*) FILTER (WHERE status = 'Waiting'),
                           (SELECT position FROM (
                               SELECT customer_id, ROW_NUMBER() OVER (ORDER BY joined_at) AS position
                               FROM event_waitlist
                               WHERE event_id = @eventId AND status = 'Waiting'
                           ) r WHERE r.customer_id = @customerId)
                    FROM event_waitlist
                    WHERE event_id = @eventId;";

                await using var cmd = new NpgsqlCommand(sql, connection);
                cmd.Parameters.Add("@eventId", NpgsqlDbType.Uuid).Value = eventId;
                cmd.Parameters.Add("@customerId", NpgsqlDbType.Uuid).Value = (object?)customerId ?? DBNull.Value;
                await using var reader = await cmd.ExecuteReaderAsync();

                long total = 0;
                long? position = null;
                if (await reader.ReadAsync())
                {
                    total = reader.IsDBNull(0) ? 0 : reader.GetInt64(0);
                    position = reader.IsDBNull(1) ? null : (long?)reader.GetInt64(1);
                }

                return Ok(new { total, position, isOnWaitlist = position.HasValue });
            }
            catch (Exception ex)
            {
                return StatusCode(500, new { message = "Failed to load waitlist info: " + ex.Message });
            }
        }

        // POST /api/waitlist/join
        [Authorize]
        [HttpPost("join")]
        public async Task<IActionResult> JoinWaitlist([FromBody] JoinWaitlistRequest req)
        {
            try
            {
                if (req.customerId == Guid.Empty || req.eventId == Guid.Empty)
                    return BadRequest(new { message = "Missing customer or event ID." });

                if (req.quantity < 1 || req.quantity > 10)
                    return BadRequest(new { message = "Quantity must be between 1 and 10." });

                await using var connection = new NpgsqlConnection(_connectionString);
                await connection.OpenAsync();
                await EnsureSchemaAsync(connection);

                // Check if event is actually sold out
                const string eventSql = @"
                    SELECT title, COALESCE(total_slots, 0), COALESCE(tickets_sold, 0)
                    FROM events WHERE id = @id AND LOWER(COALESCE(status, '')) NOT IN ('finished','cancelled') LIMIT 1;";
                await using var evCmd = new NpgsqlCommand(eventSql, connection);
                evCmd.Parameters.Add("@id", NpgsqlDbType.Uuid).Value = req.eventId;
                await using var evRdr = await evCmd.ExecuteReaderAsync();
                if (!await evRdr.ReadAsync())
                    return NotFound(new { message = "Event not found or is no longer active." });

                var eventTitle = evRdr.GetString(0);
                var slots = evRdr.GetInt32(1);
                var sold = evRdr.GetInt32(2);
                await evRdr.CloseAsync();

                if (slots > sold)
                    return BadRequest(new { message = "This event still has available tickets. Head to the event page to purchase." });

                const string insertSql = @"
                    INSERT INTO event_waitlist (event_id, customer_id, quantity, status, joined_at)
                    VALUES (@eventId, @customerId, @qty, 'Waiting', NOW())
                    ON CONFLICT (event_id, customer_id)
                    DO UPDATE SET quantity = @qty, status = 'Waiting', notified_at = NULL, notified_expires_at = NULL;";
                await using var insertCmd = new NpgsqlCommand(insertSql, connection);
                insertCmd.Parameters.Add("@eventId", NpgsqlDbType.Uuid).Value = req.eventId;
                insertCmd.Parameters.Add("@customerId", NpgsqlDbType.Uuid).Value = req.customerId;
                insertCmd.Parameters.Add("@qty", NpgsqlDbType.Integer).Value = req.quantity;
                await insertCmd.ExecuteNonQueryAsync();

                // Get position
                const string posSql = @"
                    SELECT ROW_NUMBER() OVER (ORDER BY joined_at)
                    FROM event_waitlist
                    WHERE event_id = @eventId AND status = 'Waiting'
                    ORDER BY CASE WHEN customer_id = @customerId THEN 0 ELSE 1 END
                    LIMIT 1;";
                await using var posCmd = new NpgsqlCommand(posSql, connection);
                posCmd.Parameters.Add("@eventId", NpgsqlDbType.Uuid).Value = req.eventId;
                posCmd.Parameters.Add("@customerId", NpgsqlDbType.Uuid).Value = req.customerId;
                var pos = (await posCmd.ExecuteScalarAsync()) as long? ?? 1;

                await NotificationSupport.EnsureNotificationsTableExistsAsync(connection);
                await NotificationSupport.InsertNotificationAsync(connection, req.customerId,
                    "waitlist_joined", "Added to waitlist",
                    $"You're #{pos} on the waitlist for {eventTitle}. We'll notify you if a spot opens.", req.eventId, "event");

                return Ok(new { message = $"You're #{pos} on the waitlist for {eventTitle}.", position = pos });
            }
            catch (Exception ex)
            {
                return StatusCode(500, new { message = "Failed to join waitlist: " + ex.Message });
            }
        }

        // DELETE /api/waitlist/leave/{eventId}/{customerId}
        [Authorize]
        [HttpDelete("leave/{eventId}/{customerId}")]
        public async Task<IActionResult> LeaveWaitlist(Guid eventId, Guid customerId)
        {
            try
            {
                await using var connection = new NpgsqlConnection(_connectionString);
                await connection.OpenAsync();
                await EnsureSchemaAsync(connection);

                const string sql = @"
                    DELETE FROM event_waitlist
                    WHERE event_id = @eventId AND customer_id = @customerId;";
                await using var cmd = new NpgsqlCommand(sql, connection);
                cmd.Parameters.Add("@eventId", NpgsqlDbType.Uuid).Value = eventId;
                cmd.Parameters.Add("@customerId", NpgsqlDbType.Uuid).Value = customerId;
                await cmd.ExecuteNonQueryAsync();

                return Ok(new { message = "Removed from waitlist." });
            }
            catch (Exception ex)
            {
                return StatusCode(500, new { message = "Failed to leave waitlist: " + ex.Message });
            }
        }

        // POST /api/waitlist/notify/{eventId} — called internally when a ticket is cancelled/refunded
        // Notifies the next person in line
        [HttpPost("notify/{eventId}")]
        public async Task<IActionResult> NotifyNextInLine(Guid eventId)
        {
            try
            {
                await using var connection = new NpgsqlConnection(_connectionString);
                await connection.OpenAsync();
                await EnsureSchemaAsync(connection);

                const string eventTitleSql = "SELECT COALESCE(title,'Event') FROM events WHERE id=@id LIMIT 1;";
                await using var titleCmd = new NpgsqlCommand(eventTitleSql, connection);
                titleCmd.Parameters.Add("@id", NpgsqlDbType.Uuid).Value = eventId;
                var eventTitle = (await titleCmd.ExecuteScalarAsync() as string) ?? "Event";

                const string nextSql = @"
                    SELECT w.customer_id, COALESCE(u.email,''), COALESCE(u.firstname,'')
                    FROM event_waitlist w
                    LEFT JOIN users u ON u.id = w.customer_id
                    WHERE w.event_id = @eventId AND w.status = 'Waiting'
                      AND (w.notified_expires_at IS NULL OR w.notified_expires_at < NOW())
                    ORDER BY w.joined_at
                    LIMIT 1;";
                await using var nextCmd = new NpgsqlCommand(nextSql, connection);
                nextCmd.Parameters.Add("@eventId", NpgsqlDbType.Uuid).Value = eventId;
                await using var rdr = await nextCmd.ExecuteReaderAsync();
                if (!await rdr.ReadAsync())
                    return Ok(new { message = "No one on the waitlist to notify." });

                var custId = rdr.GetGuid(0);
                var email = rdr.GetString(1);
                var firstName = rdr.GetString(2);
                await rdr.CloseAsync();

                // Mark as notified — they have 30 minutes to act
                const string markSql = @"
                    UPDATE event_waitlist
                    SET status = 'Notified', notified_at = NOW(), notified_expires_at = NOW() + INTERVAL '30 minutes'
                    WHERE event_id = @eventId AND customer_id = @customerId;";
                await using var markCmd = new NpgsqlCommand(markSql, connection);
                markCmd.Parameters.Add("@eventId", NpgsqlDbType.Uuid).Value = eventId;
                markCmd.Parameters.Add("@customerId", NpgsqlDbType.Uuid).Value = custId;
                await markCmd.ExecuteNonQueryAsync();

                await NotificationSupport.EnsureNotificationsTableExistsAsync(connection);
                await NotificationSupport.InsertNotificationAsync(connection, custId,
                    "waitlist_spot_available", "A spot opened up!",
                    $"A ticket for {eventTitle} just became available! You have 30 minutes to purchase before the next person is notified.",
                    eventId, "event");

                // Send email
                if (!string.IsNullOrWhiteSpace(email))
                {
                    _ = _emailService.SendWaitlistSpotEmailAsync(email, firstName, eventTitle,
                        $"https://imajination.onrender.com/pages/details/EventDetailPage.html?id={eventId}");
                }

                return Ok(new { message = $"Notified {firstName} ({email})." });
            }
            catch (Exception ex)
            {
                return StatusCode(500, new { message = "Failed to notify waitlist: " + ex.Message });
            }
        }
    }
}
