using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Npgsql;
using NpgsqlTypes;
using System.Security.Claims;
using System.Security.Cryptography;
using ImajinationAPI.Services;

namespace ImajinationAPI.Controllers
{
    public class InitiateTransferRequest
    {
        public Guid ticketId { get; set; }
        public string? recipientEmail { get; set; }
    }

    [Route("api/ticket-transfer")]
    [ApiController]
    public class TicketTransferController : ControllerBase
    {
        private readonly string _connectionString;
        private readonly EmailService _emailService;

        public TicketTransferController(IConfiguration configuration, EmailService emailService)
        {
            _connectionString = ConfigurationFallbacks.GetRequiredSupabaseConnectionString(configuration);
            _emailService = emailService;
        }

        private static async Task EnsureSchemaAsync(NpgsqlConnection connection)
        {
            const string sql = @"
                CREATE TABLE IF NOT EXISTS ticket_transfers (
                    id uuid PRIMARY KEY DEFAULT gen_random_uuid(),
                    ticket_id uuid NOT NULL,
                    from_customer_id uuid NOT NULL,
                    to_email text NOT NULL,
                    to_customer_id uuid NULL,
                    token text NOT NULL UNIQUE,
                    status varchar(20) NOT NULL DEFAULT 'Pending',
                    created_at timestamptz NOT NULL DEFAULT NOW(),
                    expires_at timestamptz NOT NULL DEFAULT NOW() + INTERVAL '24 hours',
                    completed_at timestamptz NULL
                );
                CREATE INDEX IF NOT EXISTS idx_ticket_transfers_token ON ticket_transfers(token);";
            await using var cmd = new NpgsqlCommand(sql, connection);
            await cmd.ExecuteNonQueryAsync();
        }

        private Guid? GetActorId() =>
            Guid.TryParse(User.FindFirst(ClaimTypes.NameIdentifier)?.Value, out var id) ? id : null;

        // POST /api/ticket-transfer/initiate
        [Authorize]
        [HttpPost("initiate")]
        public async Task<IActionResult> InitiateTransfer([FromBody] InitiateTransferRequest req)
        {
            try
            {
                if (req.ticketId == Guid.Empty)
                    return BadRequest(new { message = "Ticket ID is required." });

                if (string.IsNullOrWhiteSpace(req.recipientEmail) || !req.recipientEmail.Contains('@'))
                    return BadRequest(new { message = "A valid recipient email is required." });

                var actorId = GetActorId();
                if (!actorId.HasValue) return Unauthorized();

                await using var connection = new NpgsqlConnection(_connectionString);
                await connection.OpenAsync();
                await EnsureSchemaAsync(connection);

                // Verify ticket ownership + paid status
                const string ticketSql = @"
                    SELECT t.customer_id, t.payment_status, t.is_used, e.title,
                           COALESCE(u.firstname,''), COALESCE(u.lastname,'')
                    FROM tickets t
                    INNER JOIN events e ON e.id = t.event_id
                    LEFT JOIN users u ON u.id = t.customer_id
                    WHERE t.id = @id LIMIT 1;";
                await using var tCmd = new NpgsqlCommand(ticketSql, connection);
                tCmd.Parameters.Add("@id", NpgsqlDbType.Uuid).Value = req.ticketId;
                await using var rdr = await tCmd.ExecuteReaderAsync();
                if (!await rdr.ReadAsync())
                    return NotFound(new { message = "Ticket not found." });

                var ownerId = rdr.GetGuid(0);
                var paymentStatus = rdr.GetString(1);
                var isUsed = !rdr.IsDBNull(2) && rdr.GetBoolean(2);
                var eventTitle = rdr.GetString(3);
                var ownerFirst = rdr.GetString(4);
                var ownerLast = rdr.GetString(5);
                await rdr.CloseAsync();

                if (ownerId != actorId.Value)
                    return Forbid();

                if (!paymentStatus.Equals("Paid", StringComparison.OrdinalIgnoreCase))
                    return BadRequest(new { message = "Only paid tickets can be transferred." });

                if (isUsed)
                    return BadRequest(new { message = "Already-scanned tickets cannot be transferred." });

                // Don't allow transfer to self
                const string selfCheck = "SELECT email FROM users WHERE id = @id LIMIT 1;";
                await using var selfCmd = new NpgsqlCommand(selfCheck, connection);
                selfCmd.Parameters.Add("@id", NpgsqlDbType.Uuid).Value = actorId.Value;
                var myEmail = (await selfCmd.ExecuteScalarAsync() as string) ?? "";
                if (myEmail.Equals(req.recipientEmail.Trim(), StringComparison.OrdinalIgnoreCase))
                    return BadRequest(new { message = "You cannot transfer a ticket to yourself." });

                // Cancel any existing pending transfers for this ticket
                const string cancelSql = @"
                    UPDATE ticket_transfers SET status = 'Cancelled'
                    WHERE ticket_id = @ticketId AND status = 'Pending';";
                await using var cancelCmd = new NpgsqlCommand(cancelSql, connection);
                cancelCmd.Parameters.Add("@ticketId", NpgsqlDbType.Uuid).Value = req.ticketId;
                await cancelCmd.ExecuteNonQueryAsync();

                // Generate secure token
                var token = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))
                    .Replace("+", "-").Replace("/", "_").Replace("=", "");

                const string insertSql = @"
                    INSERT INTO ticket_transfers (ticket_id, from_customer_id, to_email, token, status, expires_at)
                    VALUES (@ticketId, @fromId, @toEmail, @token, 'Pending', NOW() + INTERVAL '24 hours');";
                await using var insertCmd = new NpgsqlCommand(insertSql, connection);
                insertCmd.Parameters.Add("@ticketId", NpgsqlDbType.Uuid).Value = req.ticketId;
                insertCmd.Parameters.Add("@fromId", NpgsqlDbType.Uuid).Value = actorId.Value;
                insertCmd.Parameters.Add("@toEmail", NpgsqlDbType.Text).Value = req.recipientEmail.Trim().ToLower();
                insertCmd.Parameters.Add("@token", NpgsqlDbType.Text).Value = token;
                await insertCmd.ExecuteNonQueryAsync();

                // Mark ticket as locked (transfer pending)
                const string lockSql = "UPDATE tickets SET transfer_pending = TRUE WHERE id = @id;";
                try
                {
                    await using var lockCmd = new NpgsqlCommand("ALTER TABLE tickets ADD COLUMN IF NOT EXISTS transfer_pending boolean NOT NULL DEFAULT FALSE;", connection);
                    await lockCmd.ExecuteNonQueryAsync();
                    await using var lockCmd2 = new NpgsqlCommand(lockSql, connection);
                    lockCmd2.Parameters.Add("@id", NpgsqlDbType.Uuid).Value = req.ticketId;
                    await lockCmd2.ExecuteNonQueryAsync();
                }
                catch { /* column may already exist */ }

                var senderName = $"{ownerFirst} {ownerLast}".Trim();
                if (string.IsNullOrWhiteSpace(senderName)) senderName = "Someone";

                var acceptUrl = $"https://imajination.onrender.com/pages/bookings/accept-transfer.html?token={token}";

                // Look up recipient name from users table
                const string recipSql = "SELECT COALESCE(firstname,'') FROM users WHERE LOWER(TRIM(email)) = LOWER(TRIM(@email)) LIMIT 1;";
                await using var recipCmd = new NpgsqlCommand(recipSql, connection);
                recipCmd.Parameters.Add("@email", NpgsqlDbType.Text).Value = req.recipientEmail.Trim().ToLower();
                var recipFirst = (await recipCmd.ExecuteScalarAsync() as string) ?? "there";

                _ = _emailService.SendTicketTransferEmailAsync(req.recipientEmail.Trim(), recipFirst, senderName, eventTitle, acceptUrl);

                return Ok(new { message = $"Transfer link sent to {req.recipientEmail}. It expires in 24 hours." });
            }
            catch (Exception ex)
            {
                return StatusCode(500, new { message = "Failed to initiate transfer: " + ex.Message });
            }
        }

        // GET /api/ticket-transfer/preview/{token} — preview before accepting
        [HttpGet("preview/{token}")]
        public async Task<IActionResult> PreviewTransfer(string token)
        {
            try
            {
                await using var connection = new NpgsqlConnection(_connectionString);
                await connection.OpenAsync();
                await EnsureSchemaAsync(connection);

                const string sql = @"
                    SELECT tt.id, tt.ticket_id, tt.to_email, tt.status, tt.expires_at,
                           e.title, e.event_time, e.location, e.city,
                           COALESCE(t.tier_name,'General Admission'), t.quantity, t.total_price,
                           COALESCE(fu.firstname,''), COALESCE(fu.lastname,'')
                    FROM ticket_transfers tt
                    INNER JOIN tickets t ON t.id = tt.ticket_id
                    INNER JOIN events e ON e.id = t.event_id
                    LEFT JOIN users fu ON fu.id = tt.from_customer_id
                    WHERE tt.token = @token LIMIT 1;";

                await using var cmd = new NpgsqlCommand(sql, connection);
                cmd.Parameters.Add("@token", NpgsqlDbType.Text).Value = token;
                await using var rdr = await cmd.ExecuteReaderAsync();
                if (!await rdr.ReadAsync())
                    return NotFound(new { message = "Transfer link not found or expired." });

                var status = rdr.GetString(3);
                var expires = rdr.GetDateTime(4);

                if (status != "Pending")
                    return BadRequest(new { message = status == "Completed" ? "This transfer has already been accepted." : "This transfer link is no longer valid." });
                if (expires < DateTime.UtcNow)
                    return BadRequest(new { message = "This transfer link has expired." });

                return Ok(new
                {
                    transferId = rdr.GetGuid(0),
                    ticketId = rdr.GetGuid(1),
                    toEmail = rdr.GetString(2),
                    status,
                    expiresAt = expires,
                    eventTitle = rdr.GetString(5),
                    eventDate = rdr.IsDBNull(6) ? null : (DateTime?)rdr.GetDateTime(6),
                    location = rdr.GetString(7),
                    city = rdr.GetString(8),
                    tierName = rdr.GetString(9),
                    quantity = rdr.GetInt32(10),
                    totalPrice = rdr.GetDecimal(11),
                    fromName = $"{rdr.GetString(12)} {rdr.GetString(13)}".Trim()
                });
            }
            catch (Exception ex)
            {
                return StatusCode(500, new { message = "Failed to load transfer: " + ex.Message });
            }
        }

        // POST /api/ticket-transfer/accept/{token}
        [Authorize]
        [HttpPost("accept/{token}")]
        public async Task<IActionResult> AcceptTransfer(string token)
        {
            try
            {
                var actorId = GetActorId();
                if (!actorId.HasValue) return Unauthorized();

                await using var connection = new NpgsqlConnection(_connectionString);
                await connection.OpenAsync();
                await EnsureSchemaAsync(connection);
                await using (var ensureTicketColumnCmd = new NpgsqlCommand("ALTER TABLE tickets ADD COLUMN IF NOT EXISTS transfer_pending boolean NOT NULL DEFAULT FALSE;", connection))
                {
                    await ensureTicketColumnCmd.ExecuteNonQueryAsync();
                }
                await using var transaction = await connection.BeginTransactionAsync();

                // Fetch transfer
                const string fetchSql = @"
                    SELECT tt.id, tt.ticket_id, tt.to_email, tt.from_customer_id, tt.status, tt.expires_at
                    FROM ticket_transfers tt
                    WHERE tt.token = @token
                    FOR UPDATE;";
                await using var fetchCmd = new NpgsqlCommand(fetchSql, connection, transaction);
                fetchCmd.Parameters.Add("@token", NpgsqlDbType.Text).Value = token;
                await using var rdr = await fetchCmd.ExecuteReaderAsync();
                if (!await rdr.ReadAsync()) return NotFound(new { message = "Transfer not found." });

                var transferId = rdr.GetGuid(0);
                var ticketId = rdr.GetGuid(1);
                var toEmail = rdr.GetString(2);
                var fromId = rdr.GetGuid(3);
                var status = rdr.GetString(4);
                var expires = rdr.GetDateTime(5);
                await rdr.CloseAsync();

                if (status != "Pending") return BadRequest(new { message = "This transfer has already been processed." });
                if (expires < DateTime.UtcNow) return BadRequest(new { message = "This transfer link has expired." });

                // Verify the accepting user's email matches
                const string emailSql = "SELECT LOWER(TRIM(email)) FROM users WHERE id = @id LIMIT 1;";
                await using var emailCmd = new NpgsqlCommand(emailSql, connection, transaction);
                emailCmd.Parameters.Add("@id", NpgsqlDbType.Uuid).Value = actorId.Value;
                var actorEmail = (await emailCmd.ExecuteScalarAsync() as string) ?? "";
                if (!actorEmail.Equals(toEmail.Trim().ToLower(), StringComparison.OrdinalIgnoreCase))
                    return BadRequest(new { message = "This transfer was intended for a different email address." });

                const string ticketStateSql = @"
                    SELECT customer_id,
                           COALESCE(payment_status, ''),
                           COALESCE(refund_status, ''),
                           COALESCE(is_used, FALSE)
                    FROM tickets
                    WHERE id = @ticketId
                    FOR UPDATE;";
                await using var ticketStateCmd = new NpgsqlCommand(ticketStateSql, connection, transaction);
                ticketStateCmd.Parameters.Add("@ticketId", NpgsqlDbType.Uuid).Value = ticketId;
                await using var ticketReader = await ticketStateCmd.ExecuteReaderAsync();
                if (!await ticketReader.ReadAsync())
                {
                    await transaction.RollbackAsync();
                    return NotFound(new { message = "Ticket not found." });
                }

                var currentOwnerId = ticketReader.IsDBNull(0) ? Guid.Empty : ticketReader.GetGuid(0);
                var paymentStatus = ticketReader.IsDBNull(1) ? string.Empty : ticketReader.GetString(1);
                var refundStatus = ticketReader.IsDBNull(2) ? string.Empty : ticketReader.GetString(2);
                var isUsed = !ticketReader.IsDBNull(3) && ticketReader.GetBoolean(3);
                await ticketReader.CloseAsync();

                if (currentOwnerId != fromId)
                {
                    await transaction.RollbackAsync();
                    return Conflict(new { message = "This ticket is no longer owned by the original sender." });
                }

                if (!paymentStatus.Equals("Paid", StringComparison.OrdinalIgnoreCase))
                {
                    await transaction.RollbackAsync();
                    return BadRequest(new { message = "Only paid tickets can be transferred." });
                }

                if (isUsed)
                {
                    await transaction.RollbackAsync();
                    return BadRequest(new { message = "Already-scanned tickets cannot be transferred." });
                }

                if (refundStatus.Equals("Refunded", StringComparison.OrdinalIgnoreCase))
                {
                    await transaction.RollbackAsync();
                    return BadRequest(new { message = "Refunded tickets cannot be transferred." });
                }

                // Reassign ticket ownership
                const string updateTicketSql = @"
                    UPDATE tickets
                    SET customer_id = @newOwner, transfer_pending = FALSE
                    WHERE id = @ticketId
                      AND customer_id = @fromCustomerId;";
                await using var updateCmd = new NpgsqlCommand(updateTicketSql, connection, transaction);
                updateCmd.Parameters.Add("@newOwner", NpgsqlDbType.Uuid).Value = actorId.Value;
                updateCmd.Parameters.Add("@ticketId", NpgsqlDbType.Uuid).Value = ticketId;
                updateCmd.Parameters.Add("@fromCustomerId", NpgsqlDbType.Uuid).Value = fromId;
                var updatedRows = await updateCmd.ExecuteNonQueryAsync();
                if (updatedRows == 0)
                {
                    await transaction.RollbackAsync();
                    return Conflict(new { message = "This ticket could not be transferred because its ownership changed." });
                }

                // Mark transfer complete
                const string completeSql = @"
                    UPDATE ticket_transfers
                    SET status = 'Completed', to_customer_id = @toId, completed_at = NOW()
                    WHERE id = @id;";
                await using var completeCmd = new NpgsqlCommand(completeSql, connection, transaction);
                completeCmd.Parameters.Add("@toId", NpgsqlDbType.Uuid).Value = actorId.Value;
                completeCmd.Parameters.Add("@id", NpgsqlDbType.Uuid).Value = transferId;
                await completeCmd.ExecuteNonQueryAsync();

                // Notify original owner
                await NotificationSupport.EnsureNotificationsTableExistsAsync(connection);
                await NotificationSupport.InsertNotificationAsync(connection, fromId,
                    "ticket_transfer_complete", "Ticket transferred",
                    "Your ticket transfer was accepted. The ticket has moved to the new holder.", ticketId, "ticket");

                await transaction.CommitAsync();

                return Ok(new { message = "Ticket transfer accepted! The ticket is now in your account.", ticketId });
            }
            catch (Exception ex)
            {
                return StatusCode(500, new { message = "Failed to accept transfer: " + ex.Message });
            }
        }
    }
}
