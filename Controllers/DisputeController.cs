using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Npgsql;
using NpgsqlTypes;
using System.Security.Claims;
using ImajinationAPI.Services;

namespace ImajinationAPI.Controllers
{
    public class CreateDisputeRequest
    {
        public Guid bookingId { get; set; }
        public Guid reporterId { get; set; }
        public string? disputeType { get; set; }   // NoShow | PaymentIssue | QualityIssue | Misconduct | Other
        public string? description { get; set; }
        public string? evidenceUrls { get; set; }  // comma-separated URLs or base64 data-urls
    }

    public class ResolveDisputeRequest
    {
        public string? resolution { get; set; }   // Refunded | Dismissed | PartialRefund | EscalatedToAdmin
        public string? adminNotes { get; set; }
    }

    [Route("api/[controller]")]
    [ApiController]
    public class DisputeController : ControllerBase
    {
        private readonly string _connectionString;
        private readonly EmailService _emailService;

        public DisputeController(IConfiguration configuration, EmailService emailService)
        {
            _connectionString = ConfigurationFallbacks.GetRequiredSupabaseConnectionString(configuration);
            _emailService = emailService;
        }

        private static async Task EnsureSchemaAsync(NpgsqlConnection connection)
        {
            const string sql = @"
                CREATE TABLE IF NOT EXISTS booking_disputes (
                    id uuid PRIMARY KEY DEFAULT gen_random_uuid(),
                    booking_id uuid NOT NULL,
                    reporter_id uuid NOT NULL,
                    dispute_type varchar(50) NOT NULL DEFAULT 'Other',
                    description text NOT NULL,
                    evidence_urls text NULL,
                    status varchar(30) NOT NULL DEFAULT 'Open',
                    resolution varchar(40) NULL,
                    admin_notes text NULL,
                    created_at timestamptz NOT NULL DEFAULT NOW(),
                    updated_at timestamptz NOT NULL DEFAULT NOW(),
                    resolved_at timestamptz NULL
                );
                CREATE INDEX IF NOT EXISTS idx_disputes_booking ON booking_disputes(booking_id);
                CREATE INDEX IF NOT EXISTS idx_disputes_status ON booking_disputes(status);";
            await using var cmd = new NpgsqlCommand(sql, connection);
            await cmd.ExecuteNonQueryAsync();
        }

        private Guid? GetActorId() =>
            Guid.TryParse(User.FindFirst(ClaimTypes.NameIdentifier)?.Value, out var id) ? id : null;

        private string GetActorRole() =>
            User.FindFirst(ClaimTypes.Role)?.Value ?? "";

        // POST /api/dispute
        [Authorize]
        [HttpPost]
        public async Task<IActionResult> CreateDispute([FromBody] CreateDisputeRequest req)
        {
            try
            {
                if (req.bookingId == Guid.Empty || req.reporterId == Guid.Empty)
                    return BadRequest(new { message = "Booking ID and reporter ID are required." });

                if (string.IsNullOrWhiteSpace(req.description) || req.description.Trim().Length < 20)
                    return BadRequest(new { message = "Please describe the issue in at least 20 characters." });

                var actorId = GetActorId();
                if (!actorId.HasValue || actorId.Value != req.reporterId)
                    return Forbid();

                var allowedTypes = new[] { "NoShow", "PaymentIssue", "QualityIssue", "Misconduct", "Other" };
                var disputeType = allowedTypes.Contains(req.disputeType ?? "") ? req.disputeType! : "Other";

                await using var connection = new NpgsqlConnection(_connectionString);
                await connection.OpenAsync();
                await EnsureSchemaAsync(connection);

                // Verify reporter is part of this booking
                const string bookingSql = @"
                    SELECT customer_id, target_user_id, COALESCE(event_title,'Booking'),
                           COALESCE(status,''), COALESCE(customer_id::text,''), COALESCE(target_user_id::text,'')
                    FROM bookings WHERE id = @id LIMIT 1;";
                await using var bCmd = new NpgsqlCommand(bookingSql, connection);
                bCmd.Parameters.Add("@id", NpgsqlDbType.Uuid).Value = req.bookingId;
                await using var bRdr = await bCmd.ExecuteReaderAsync();
                if (!await bRdr.ReadAsync())
                    return NotFound(new { message = "Booking not found." });

                var customerId = bRdr.GetGuid(0);
                var targetId = bRdr.GetGuid(1);
                var eventTitle = bRdr.GetString(2);
                var bookingStatus = bRdr.GetString(3);
                await bRdr.CloseAsync();

                if (req.reporterId != customerId && req.reporterId != targetId)
                    return Forbid();

                // Check for existing open dispute
                const string checkSql = "SELECT id FROM booking_disputes WHERE booking_id=@id AND status='Open' LIMIT 1;";
                await using var checkCmd = new NpgsqlCommand(checkSql, connection);
                checkCmd.Parameters.Add("@id", NpgsqlDbType.Uuid).Value = req.bookingId;
                var existing = await checkCmd.ExecuteScalarAsync();
                if (existing != null)
                    return Conflict(new { message = "There is already an open dispute for this booking. Our admin team will review it." });

                var sanitizedDesc = SecuritySupport.SanitizePlainText(req.description, 2000, true) ?? "";
                var evidenceUrls = string.IsNullOrWhiteSpace(req.evidenceUrls) ? null :
                    SecuritySupport.SanitizePlainText(req.evidenceUrls, 10000, false);

                const string insertSql = @"
                    INSERT INTO booking_disputes (booking_id, reporter_id, dispute_type, description, evidence_urls, status)
                    VALUES (@bookingId, @reporterId, @type, @desc, @evidence, 'Open');";
                await using var insertCmd = new NpgsqlCommand(insertSql, connection);
                insertCmd.Parameters.Add("@bookingId", NpgsqlDbType.Uuid).Value = req.bookingId;
                insertCmd.Parameters.Add("@reporterId", NpgsqlDbType.Uuid).Value = req.reporterId;
                insertCmd.Parameters.Add("@type", NpgsqlDbType.Text).Value = disputeType;
                insertCmd.Parameters.Add("@desc", NpgsqlDbType.Text).Value = sanitizedDesc;
                insertCmd.Parameters.Add("@evidence", NpgsqlDbType.Text).Value = (object?)evidenceUrls ?? DBNull.Value;
                await insertCmd.ExecuteNonQueryAsync();

                // Freeze booking — add dispute hold status
                const string holdSql = @"
                    ALTER TABLE bookings ADD COLUMN IF NOT EXISTS dispute_hold boolean NOT NULL DEFAULT FALSE;
                    UPDATE bookings SET dispute_hold = TRUE WHERE id = @id;";
                try
                {
                    await using var holdCmd = new NpgsqlCommand(holdSql, connection);
                    holdCmd.Parameters.Add("@id", NpgsqlDbType.Uuid).Value = req.bookingId;
                    await holdCmd.ExecuteNonQueryAsync();
                }
                catch { /* column may already exist */ }

                // Notify both parties and admin
                await NotificationSupport.EnsureNotificationsTableExistsAsync(connection);
                var notifyList = new HashSet<Guid> { customerId, targetId };
                foreach (var uid in notifyList)
                {
                    await NotificationSupport.InsertNotificationAsync(connection, uid,
                        "dispute_opened", "Dispute Filed",
                        $"A dispute has been opened for the booking '{eventTitle}'. Our admin team will review and respond within 48 hours.",
                        req.bookingId, "booking");
                }

                return Ok(new { message = "Dispute submitted. Our team will review your case within 48 hours. The booking has been placed on hold." });
            }
            catch (Exception ex)
            {
                return StatusCode(500, new { message = "Failed to submit dispute: " + ex.Message });
            }
        }

        // GET /api/dispute/booking/{bookingId}
        [Authorize]
        [HttpGet("booking/{bookingId}")]
        public async Task<IActionResult> GetDisputeForBooking(Guid bookingId)
        {
            try
            {
                await using var connection = new NpgsqlConnection(_connectionString);
                await connection.OpenAsync();
                await EnsureSchemaAsync(connection);

                const string sql = @"
                    SELECT d.id, d.dispute_type, d.description, d.status, d.resolution,
                           d.admin_notes, d.created_at, d.resolved_at,
                           COALESCE(u.firstname,''), COALESCE(u.lastname,'')
                    FROM booking_disputes d
                    LEFT JOIN users u ON u.id = d.reporter_id
                    WHERE d.booking_id = @id
                    ORDER BY d.created_at DESC
                    LIMIT 1;";

                await using var cmd = new NpgsqlCommand(sql, connection);
                cmd.Parameters.Add("@id", NpgsqlDbType.Uuid).Value = bookingId;
                await using var rdr = await cmd.ExecuteReaderAsync();
                if (!await rdr.ReadAsync())
                    return Ok(new { hasDispute = false });

                return Ok(new
                {
                    hasDispute = true,
                    disputeId = rdr.GetGuid(0),
                    disputeType = rdr.GetString(1),
                    description = rdr.GetString(2),
                    status = rdr.GetString(3),
                    resolution = rdr.IsDBNull(4) ? null : rdr.GetString(4),
                    adminNotes = rdr.IsDBNull(5) ? null : rdr.GetString(5),
                    createdAt = rdr.GetDateTime(6),
                    resolvedAt = rdr.IsDBNull(7) ? null : (DateTime?)rdr.GetDateTime(7),
                    reporterName = $"{rdr.GetString(8)} {rdr.GetString(9)}".Trim()
                });
            }
            catch (Exception ex)
            {
                return StatusCode(500, new { message = "Failed to load dispute: " + ex.Message });
            }
        }

        // GET /api/dispute/admin/all — admin view of all disputes
        [Authorize]
        [HttpGet("admin/all")]
        public async Task<IActionResult> GetAllDisputes([FromQuery] string? status, [FromQuery] int page = 1)
        {
            if (!GetActorRole().Equals("Admin", StringComparison.OrdinalIgnoreCase)) return Forbid();
            try
            {
                await using var connection = new NpgsqlConnection(_connectionString);
                await connection.OpenAsync();
                await EnsureSchemaAsync(connection);

                var statusFilter = string.IsNullOrWhiteSpace(status) ? "" : $"AND d.status = @status";
                var sql = $@"
                    SELECT d.id, d.booking_id, d.dispute_type, d.status, d.resolution,
                           d.created_at, d.resolved_at,
                           COALESCE(b.event_title,'Booking'),
                           COALESCE(ru.firstname,''), COALESCE(ru.lastname,''), COALESCE(ru.email,'')
                    FROM booking_disputes d
                    INNER JOIN bookings b ON b.id = d.booking_id
                    LEFT JOIN users ru ON ru.id = d.reporter_id
                    WHERE 1=1 {statusFilter}
                    ORDER BY d.created_at DESC
                    LIMIT 20 OFFSET @offset;";

                await using var cmd = new NpgsqlCommand(sql, connection);
                if (!string.IsNullOrWhiteSpace(status))
                    cmd.Parameters.Add("@status", NpgsqlDbType.Text).Value = status;
                cmd.Parameters.Add("@offset", NpgsqlDbType.Integer).Value = (page - 1) * 20;

                var results = new List<object>();
                await using var rdr = await cmd.ExecuteReaderAsync();
                while (await rdr.ReadAsync())
                {
                    results.Add(new
                    {
                        id = rdr.GetGuid(0),
                        bookingId = rdr.GetGuid(1),
                        disputeType = rdr.GetString(2),
                        status = rdr.GetString(3),
                        resolution = rdr.IsDBNull(4) ? null : rdr.GetString(4),
                        createdAt = rdr.GetDateTime(5),
                        resolvedAt = rdr.IsDBNull(6) ? null : (DateTime?)rdr.GetDateTime(6),
                        eventTitle = rdr.GetString(7),
                        reporterName = $"{rdr.GetString(8)} {rdr.GetString(9)}".Trim(),
                        reporterEmail = rdr.GetString(10)
                    });
                }

                return Ok(results);
            }
            catch (Exception ex)
            {
                return StatusCode(500, new { message = "Failed to load disputes: " + ex.Message });
            }
        }

        // PUT /api/dispute/{disputeId}/resolve — admin resolves
        [Authorize]
        [HttpPut("{disputeId}/resolve")]
        public async Task<IActionResult> ResolveDispute(Guid disputeId, [FromBody] ResolveDisputeRequest req)
        {
            if (!GetActorRole().Equals("Admin", StringComparison.OrdinalIgnoreCase)) return Forbid();
            try
            {
                var allowedResolutions = new[] { "Refunded", "Dismissed", "PartialRefund", "EscalatedToAdmin", "WarningIssued", "NoAction" };
                var resolution = allowedResolutions.Contains(req.resolution ?? "") ? req.resolution! : "NoAction";

                await using var connection = new NpgsqlConnection(_connectionString);
                await connection.OpenAsync();
                await EnsureSchemaAsync(connection);

                const string sql = @"
                    UPDATE booking_disputes
                    SET status = 'Resolved', resolution = @resolution,
                        admin_notes = @notes, resolved_at = NOW(), updated_at = NOW()
                    WHERE id = @id
                    RETURNING booking_id;";
                await using var cmd = new NpgsqlCommand(sql, connection);
                cmd.Parameters.Add("@id", NpgsqlDbType.Uuid).Value = disputeId;
                cmd.Parameters.Add("@resolution", NpgsqlDbType.Text).Value = resolution;
                cmd.Parameters.Add("@notes", NpgsqlDbType.Text).Value = (object?)SecuritySupport.SanitizePlainText(req.adminNotes, 1000, true) ?? DBNull.Value;
                var bookingId = await cmd.ExecuteScalarAsync() as Guid?;

                if (!bookingId.HasValue)
                    return NotFound(new { message = "Dispute not found." });

                // Release dispute hold
                const string holdSql = "UPDATE bookings SET dispute_hold = FALSE WHERE id = @id;";
                await using var holdCmd = new NpgsqlCommand(holdSql, connection);
                holdCmd.Parameters.Add("@id", NpgsqlDbType.Uuid).Value = bookingId.Value;
                await holdCmd.ExecuteNonQueryAsync();

                // Notify both booking parties
                await NotificationSupport.EnsureNotificationsTableExistsAsync(connection);
                const string partiesSql = "SELECT customer_id, target_user_id FROM bookings WHERE id=@id LIMIT 1;";
                await using var pCmd = new NpgsqlCommand(partiesSql, connection);
                pCmd.Parameters.Add("@id", NpgsqlDbType.Uuid).Value = bookingId.Value;
                await using var pRdr = await pCmd.ExecuteReaderAsync();
                if (await pRdr.ReadAsync())
                {
                    var cid = pRdr.GetGuid(0);
                    var tid = pRdr.GetGuid(1);
                    await pRdr.CloseAsync();
                    foreach (var uid in new[] { cid, tid })
                    {
                        await NotificationSupport.InsertNotificationAsync(connection, uid,
                            "dispute_resolved", "Dispute Resolved",
                            $"Your dispute has been reviewed and resolved: {resolution}. {(string.IsNullOrWhiteSpace(req.adminNotes) ? "" : "Admin note: " + req.adminNotes)}",
                            bookingId.Value, "booking");
                    }
                }

                return Ok(new { message = "Dispute resolved." });
            }
            catch (Exception ex)
            {
                return StatusCode(500, new { message = "Failed to resolve dispute: " + ex.Message });
            }
        }
    }
}
