using System.Security.Claims;
using ImajinationAPI.Models;
using ImajinationAPI.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Npgsql;

namespace ImajinationAPI.Controllers
{
    [Route("api/scanner-link")]
    [ApiController]
    public class ScannerLinkController : ControllerBase
    {
        private readonly string _connectionString;

        public ScannerLinkController(IConfiguration configuration)
        {
            _connectionString = ConfigurationFallbacks.GetRequiredSupabaseConnectionString(configuration);
        }

        [Authorize]
        [HttpPost("event/{eventId:guid}")]
        public async Task<IActionResult> Create(Guid eventId, [FromBody] CreateScannerLinkRequest request)
        {
            if (!Guid.TryParse(User.FindFirstValue(ClaimTypes.NameIdentifier), out var actorId)) return Unauthorized();
            var role = User.FindFirstValue(ClaimTypes.Role) ?? string.Empty;
            if (!string.Equals(role, "Admin", StringComparison.OrdinalIgnoreCase)) return Forbid();

            var label = SecuritySupport.SanitizePlainText(request.label, 120, false) ?? "Scanner";
            var expiresAt = request.expiresAt?.ToUniversalTime() ?? DateTime.UtcNow.AddHours(12);
            if (expiresAt <= DateTime.UtcNow || expiresAt > DateTime.UtcNow.AddDays(7))
                return BadRequest(new { message = "Scanner link expiry must be between now and 7 days." });

            await using var connection = new NpgsqlConnection(_connectionString);
            await connection.OpenAsync();
            await ScannerLinkService.EnsureSchemaAsync(connection);
            await using (var eventCmd = new NpgsqlCommand("SELECT 1 FROM events WHERE id = @id;", connection))
            {
                eventCmd.Parameters.AddWithValue("@id", eventId);
                if (await eventCmd.ExecuteScalarAsync() is null) return NotFound(new { message = "Event not found." });
            }

            var token = ScannerLinkService.CreateToken();
            var linkId = Guid.NewGuid();
            const string insert = @"
                INSERT INTO scanner_links (id, event_id, created_by, label, token_hash, expires_at)
                VALUES (@id, @eventId, @createdBy, @label, @hash, @expiresAt);";
            await using var cmd = new NpgsqlCommand(insert, connection);
            cmd.Parameters.AddWithValue("@id", linkId);
            cmd.Parameters.AddWithValue("@eventId", eventId);
            cmd.Parameters.AddWithValue("@createdBy", actorId);
            cmd.Parameters.AddWithValue("@label", label);
            cmd.Parameters.AddWithValue("@hash", ScannerLinkService.HashToken(token));
            cmd.Parameters.AddWithValue("@expiresAt", expiresAt);
            await cmd.ExecuteNonQueryAsync();
            return Ok(new { linkId, token, expiresAt, scannerUrl = $"/pages/tools/dashboardscanner.html?scannerLink={token}" });
        }

        [Authorize]
        [HttpPost("{linkId:guid}/revoke")]
        public async Task<IActionResult> Revoke(Guid linkId)
        {
            var role = User.FindFirstValue(ClaimTypes.Role) ?? string.Empty;
            if (!string.Equals(role, "Admin", StringComparison.OrdinalIgnoreCase)) return Forbid();
            await using var connection = new NpgsqlConnection(_connectionString);
            await connection.OpenAsync();
            await ScannerLinkService.EnsureSchemaAsync(connection);
            await using var cmd = new NpgsqlCommand("UPDATE scanner_links SET revoked_at = NOW() WHERE id = @id AND revoked_at IS NULL;", connection);
            cmd.Parameters.AddWithValue("@id", linkId);
            if (await cmd.ExecuteNonQueryAsync() == 0) return NotFound(new { message = "Scanner link was not found or is already revoked." });
            return Ok(new { message = "Scanner link revoked." });
        }

        [HttpPost("resolve")]
        public async Task<IActionResult> Resolve([FromBody] ResolveScannerLinkRequest request)
        {
            if (string.IsNullOrWhiteSpace(request.token)) return BadRequest(new { message = "Scanner link is required." });
            await using var connection = new NpgsqlConnection(_connectionString);
            await connection.OpenAsync();
            await ScannerLinkService.EnsureSchemaAsync(connection);
            const string lookup = @"
                SELECT sl.id, sl.event_id, COALESCE(e.title, 'Selected Event')
                FROM scanner_links sl JOIN events e ON e.id = sl.event_id
                WHERE sl.token_hash = @hash AND sl.expires_at > NOW() AND sl.revoked_at IS NULL LIMIT 1;";
            Guid linkId;
            Guid eventId;
            string eventTitle;
            await using (var cmd = new NpgsqlCommand(lookup, connection))
            {
                cmd.Parameters.AddWithValue("@hash", ScannerLinkService.HashToken(request.token));
                await using var reader = await cmd.ExecuteReaderAsync();
                if (!await reader.ReadAsync()) return StatusCode(403, new { message = "This scanner link is invalid, expired, or revoked." });
                linkId = reader.GetGuid(0);
                eventId = reader.GetGuid(1);
                eventTitle = reader.IsDBNull(2) ? "Selected Event" : reader.GetString(2);
            }
            var sessionToken = ScannerLinkService.CreateToken();
            await using (var cmd = new NpgsqlCommand(@"
                INSERT INTO scanner_sessions (id, scanner_link_id, session_hash, expires_at)
                VALUES (@id, @linkId, @hash, NOW() + INTERVAL '12 hours');
                UPDATE scanner_links SET last_used_at = NOW() WHERE id = @linkId;", connection))
            {
                cmd.Parameters.AddWithValue("@id", Guid.NewGuid());
                cmd.Parameters.AddWithValue("@linkId", linkId);
                cmd.Parameters.AddWithValue("@hash", ScannerLinkService.HashToken(sessionToken));
                await cmd.ExecuteNonQueryAsync();
            }
            return Ok(new { eventId, eventTitle, sessionToken });
        }
    }
}
