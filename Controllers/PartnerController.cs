using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Npgsql;
using System.Security.Claims;

namespace ImajinationAPI.Controllers
{
    [ApiController]
    [Route("api/[controller]")]
    public class PartnerController : ControllerBase
    {
        private readonly string _connectionString;
        private readonly ImajinationAPI.Services.EmailService _emailService;
        private readonly IConfiguration _configuration;

        public PartnerController(IConfiguration configuration, ImajinationAPI.Services.EmailService emailService)
        {
            _connectionString = ConfigurationFallbacks.GetRequiredSupabaseConnectionString(configuration);
            _emailService = emailService;
            _configuration = configuration;
        }

        private async Task EnsureSchemaAsync(NpgsqlConnection connection)
        {
            const string sql = @"
                CREATE TABLE IF NOT EXISTS partner_inquiries (
                    id uuid PRIMARY KEY DEFAULT gen_random_uuid(),
                    organizer_name varchar(200) NOT NULL,
                    email varchar(200) NOT NULL,
                    phone varchar(50) NULL,
                    event_name varchar(300) NOT NULL,
                    event_type varchar(100) NULL,
                    expected_date text NULL,
                    expected_venue text NULL,
                    expected_capacity integer NULL,
                    description text NULL,
                    referral_source varchar(200) NULL,
                    status varchar(30) NOT NULL DEFAULT 'Pending',
                    admin_notes text NULL,
                    created_at timestamptz NOT NULL DEFAULT NOW(),
                    updated_at timestamptz NOT NULL DEFAULT NOW()
                );
                CREATE INDEX IF NOT EXISTS idx_partner_inquiries_status ON partner_inquiries(status);
                CREATE INDEX IF NOT EXISTS idx_partner_inquiries_created ON partner_inquiries(created_at DESC);";
            await using var cmd = new NpgsqlCommand(sql, connection);
            await cmd.ExecuteNonQueryAsync();
        }

        // POST /api/partner/apply — public, no auth required
        [HttpPost("apply")]
        public async Task<IActionResult> SubmitApplication([FromBody] PartnerApplicationRequest req)
        {
            if (string.IsNullOrWhiteSpace(req.OrganizerName) ||
                string.IsNullOrWhiteSpace(req.Email) ||
                string.IsNullOrWhiteSpace(req.EventName))
            {
                return BadRequest(new { message = "Name, email, and event name are required." });
            }

            static string? Trim(string? s, int max) { if (string.IsNullOrWhiteSpace(s)) return null; var t = s.Trim(); return t.Length <= max ? t : t[..max]; }
            var sanitizedName  = Trim(req.OrganizerName, 200) ?? "";
            var sanitizedEmail = Trim(req.Email, 200) ?? "";
            var sanitizedEvent = Trim(req.EventName, 300) ?? "";
            var sanitizedDesc  = Trim(req.Description, 2000);
            var sanitizedVenue = Trim(req.ExpectedVenue, 300);
            var sanitizedPhone = Trim(req.Phone, 50);
            var sanitizedType  = Trim(req.EventType, 100);
            var sanitizedRef   = Trim(req.ReferralSource, 200);
            var sanitizedDate  = Trim(req.ExpectedDate, 100);

            if (string.IsNullOrWhiteSpace(sanitizedName) || string.IsNullOrWhiteSpace(sanitizedEvent))
                return BadRequest(new { message = "Name and event name are required." });

            await using var connection = new NpgsqlConnection(_connectionString);
            await connection.OpenAsync();
            await EnsureSchemaAsync(connection);

            const string insertSql = @"
                INSERT INTO partner_inquiries
                    (id, organizer_name, email, phone, event_name, event_type,
                     expected_date, expected_venue, expected_capacity, description, referral_source)
                VALUES
                    (@id, @name, @email, @phone, @eventName, @eventType,
                     @date, @venue, @capacity, @desc, @ref)";
            await using var cmd = new NpgsqlCommand(insertSql, connection);
            cmd.Parameters.AddWithValue("@id", Guid.NewGuid());
            cmd.Parameters.AddWithValue("@name", sanitizedName);
            cmd.Parameters.AddWithValue("@email", sanitizedEmail);
            cmd.Parameters.AddWithValue("@phone", string.IsNullOrWhiteSpace(sanitizedPhone) ? DBNull.Value : sanitizedPhone);
            cmd.Parameters.AddWithValue("@eventName", sanitizedEvent);
            cmd.Parameters.AddWithValue("@eventType", string.IsNullOrWhiteSpace(sanitizedType) ? DBNull.Value : sanitizedType);
            cmd.Parameters.AddWithValue("@date", string.IsNullOrWhiteSpace(sanitizedDate) ? DBNull.Value : sanitizedDate);
            cmd.Parameters.AddWithValue("@venue", string.IsNullOrWhiteSpace(sanitizedVenue) ? DBNull.Value : sanitizedVenue);
            cmd.Parameters.AddWithValue("@capacity", req.ExpectedCapacity.HasValue ? req.ExpectedCapacity.Value : DBNull.Value);
            cmd.Parameters.AddWithValue("@desc", string.IsNullOrWhiteSpace(sanitizedDesc) ? DBNull.Value : sanitizedDesc);
            cmd.Parameters.AddWithValue("@ref", string.IsNullOrWhiteSpace(sanitizedRef) ? DBNull.Value : sanitizedRef);
            await cmd.ExecuteNonQueryAsync();

            // Notify the admin email
            try
            {
                var adminEmail = _configuration["EmailSettings:AdminEmail"] ?? _configuration["EmailSettings:FromEmail"] ?? "";
                if (!string.IsNullOrWhiteSpace(adminEmail))
                {
                    await _emailService.SendRawAsync(adminEmail,
                        $"New Partner Inquiry: {sanitizedEvent}",
                        $"<h2>New Partner Inquiry</h2><p><strong>Organizer:</strong> {sanitizedName}</p><p><strong>Email:</strong> {sanitizedEmail}</p><p><strong>Event:</strong> {sanitizedEvent}</p><p><strong>Type:</strong> {sanitizedType}</p><p><strong>Date:</strong> {sanitizedDate}</p><p><strong>Venue:</strong> {sanitizedVenue}</p><p><strong>Capacity:</strong> {req.ExpectedCapacity}</p><p><strong>Description:</strong> {sanitizedDesc}</p><p>Review in the Admin dashboard → Partner Inquiries.</p>");
                }
            }
            catch { /* email is best-effort */ }

            return Ok(new { message = "Your event proposal has been submitted! We'll reach out to you within 2–3 business days." });
        }

        // GET /api/partner/inquiries — admin only
        [HttpGet("inquiries")]
        [Authorize]
        public async Task<IActionResult> GetInquiries([FromQuery] string? status = null)
        {
            var actorRole = User.FindFirstValue(ClaimTypes.Role) ?? "";
            if (!actorRole.Equals("Admin", StringComparison.OrdinalIgnoreCase))
                return Forbid();

            await using var connection = new NpgsqlConnection(_connectionString);
            await connection.OpenAsync();
            await EnsureSchemaAsync(connection);

            // Avoid passing DBNull for @status — Npgsql can't infer type for null string params
            var sql = string.IsNullOrWhiteSpace(status)
                ? @"SELECT id, organizer_name, email, phone, event_name, event_type,
                           expected_date, expected_venue, expected_capacity, description,
                           referral_source, status, admin_notes, created_at
                    FROM partner_inquiries ORDER BY created_at DESC"
                : @"SELECT id, organizer_name, email, phone, event_name, event_type,
                           expected_date, expected_venue, expected_capacity, description,
                           referral_source, status, admin_notes, created_at
                    FROM partner_inquiries WHERE status = @status ORDER BY created_at DESC";
            await using var cmd = new NpgsqlCommand(sql, connection);
            if (!string.IsNullOrWhiteSpace(status))
                cmd.Parameters.AddWithValue("@status", status);

            var list = new List<object>();
            await using var reader = await cmd.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                list.Add(new
                {
                    id              = reader.GetGuid(0),
                    organizerName   = reader.GetString(1),
                    email           = reader.GetString(2),
                    phone           = reader.IsDBNull(3) ? null : reader.GetString(3),
                    eventName       = reader.GetString(4),
                    eventType       = reader.IsDBNull(5) ? null : reader.GetString(5),
                    expectedDate    = reader.IsDBNull(6) ? null : reader.GetString(6),
                    expectedVenue   = reader.IsDBNull(7) ? null : reader.GetString(7),
                    expectedCapacity = reader.IsDBNull(8) ? (int?)null : reader.GetInt32(8),
                    description     = reader.IsDBNull(9) ? null : reader.GetString(9),
                    referralSource  = reader.IsDBNull(10) ? null : reader.GetString(10),
                    status          = reader.GetString(11),
                    adminNotes      = reader.IsDBNull(12) ? null : reader.GetString(12),
                    createdAt       = reader.GetDateTime(13)
                });
            }
            return Ok(list);
        }

        // PATCH /api/partner/inquiries/{id} — admin only
        [HttpPatch("inquiries/{id}")]
        [Authorize]
        public async Task<IActionResult> UpdateInquiry(Guid id, [FromBody] UpdateInquiryRequest req)
        {
            var actorRole = User.FindFirstValue(ClaimTypes.Role) ?? "";
            if (!actorRole.Equals("Admin", StringComparison.OrdinalIgnoreCase))
                return Forbid();

            var allowedStatuses = new[] { "Pending", "In Review", "Approved", "Rejected" };
            if (!string.IsNullOrWhiteSpace(req.Status) && !allowedStatuses.Contains(req.Status))
                return BadRequest(new { message = "Invalid status." });

            await using var connection = new NpgsqlConnection(_connectionString);
            await connection.OpenAsync();
            await EnsureSchemaAsync(connection);

            // Fetch current record so we can email the organizer
            string organizerName = "", organizerEmail = "", eventName = "";
            const string fetchSql = @"
                SELECT organizer_name, email, event_name, status
                FROM partner_inquiries WHERE id = @id";
            await using (var fetchCmd = new NpgsqlCommand(fetchSql, connection))
            {
                fetchCmd.Parameters.AddWithValue("@id", id);
                await using var rdr = await fetchCmd.ExecuteReaderAsync();
                if (await rdr.ReadAsync())
                {
                    organizerName  = rdr.GetString(0);
                    organizerEmail = rdr.GetString(1);
                    eventName      = rdr.GetString(2);
                }
            }

            const string updateSql = @"
                UPDATE partner_inquiries
                SET status      = COALESCE(@status, status),
                    admin_notes = COALESCE(@notes, admin_notes),
                    updated_at  = NOW()
                WHERE id = @id";
            await using var cmd = new NpgsqlCommand(updateSql, connection);
            cmd.Parameters.AddWithValue("@id", id);
            cmd.Parameters.AddWithValue("@status", string.IsNullOrWhiteSpace(req.Status) ? (object)DBNull.Value : req.Status);
            cmd.Parameters.AddWithValue("@notes", string.IsNullOrWhiteSpace(req.AdminNotes) ? (object)DBNull.Value : req.AdminNotes);
            await cmd.ExecuteNonQueryAsync();

            // Send beautifully designed notification email to the organizer
            var newStatus = req.Status;
            if (!string.IsNullOrWhiteSpace(newStatus) && !string.IsNullOrWhiteSpace(organizerEmail))
            {
                try
                {
                    await _emailService.SendPartnerStatusEmailAsync(
                        organizerEmail, organizerName, eventName, newStatus, req.AdminNotes);
                }
                catch { /* email is best-effort — don't fail the status update */ }
            }

            var message = newStatus switch
            {
                "Approved"  => $"Proposal approved. Notification email sent to {organizerEmail}.",
                "Rejected"  => $"Proposal rejected. Notification email sent to {organizerEmail}.",
                "In Review" => $"Marked as In Review. Email sent to {organizerEmail}.",
                _           => "Inquiry updated."
            };

            return Ok(new { message });
        }

        public record PartnerApplicationRequest(
            string OrganizerName,
            string Email,
            string? Phone,
            string EventName,
            string? EventType,
            string? ExpectedDate,
            string? ExpectedVenue,
            int? ExpectedCapacity,
            string? Description,
            string? ReferralSource
        );

        public record UpdateInquiryRequest(string? Status, string? AdminNotes);
    }
}
