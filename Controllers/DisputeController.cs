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
        public decimal? customerRefundAmount { get; set; }   // required for PartialRefund
    }

    [Route("api/[controller]")]
    [ApiController]
    public class DisputeController : ControllerBase
    {
        private readonly string _connectionString;
        private readonly EmailService _emailService;
        private readonly string _paymongoSecretKey;

        public DisputeController(IConfiguration configuration, EmailService emailService)
        {
            _connectionString = ConfigurationFallbacks.GetRequiredSupabaseConnectionString(configuration);
            _emailService = emailService;
            _paymongoSecretKey = configuration["PayMongo:SecretKey"] ?? string.Empty;
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
                await EscrowService.EnsureSchemaAsync(connection);

                await using var transaction = await connection.BeginTransactionAsync();
                // Serialize dispute creation against completion and release.
                // Verify reporter is part of this booking
                const string bookingSql = @"
                    SELECT customer_id, target_user_id, COALESCE(event_title,'Booking'),
                           COALESCE(status,''), COALESCE(customer_id::text,''), COALESCE(target_user_id::text,'')
                    FROM bookings WHERE id = @id LIMIT 1 FOR UPDATE;";
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
                const string checkSql = "SELECT id FROM booking_disputes WHERE booking_id=@id AND status IN ('Open', 'Escalated') LIMIT 1;";
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

                const string holdSql = "UPDATE bookings SET dispute_hold = TRUE WHERE id = @id;";
                await using (var holdCmd = new NpgsqlCommand(holdSql, connection, transaction))
                {
                    holdCmd.Parameters.Add("@id", NpgsqlDbType.Uuid).Value = req.bookingId;
                    await holdCmd.ExecuteNonQueryAsync();
                }
                await EscrowService.MarkDisputedAsync(connection, req.bookingId);
                await transaction.CommitAsync();

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
                await EscrowService.EnsureSchemaAsync(connection);
                const string accessSql = "SELECT EXISTS (SELECT 1 FROM bookings WHERE id = @id AND (customer_id = @actorId OR target_user_id = @actorId));";
                await using var accessCmd = new NpgsqlCommand(accessSql, connection);
                accessCmd.Parameters.Add("@id", NpgsqlDbType.Uuid).Value = bookingId;
                accessCmd.Parameters.Add("@actorId", NpgsqlDbType.Uuid).Value = GetActorId() ?? Guid.Empty;
                if (!GetActorRole().Equals("Admin", StringComparison.OrdinalIgnoreCase) && !(bool)(await accessCmd.ExecuteScalarAsync())!)
                    return Forbid();


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
                           COALESCE(ru.firstname,''), COALESCE(ru.lastname,''), COALESCE(ru.email,''),
                           d.description, d.evidence_urls, d.admin_notes
                    FROM booking_disputes d
                    INNER JOIN bookings b ON b.id = d.booking_id
                    LEFT JOIN users ru ON ru.id = d.reporter_id
                    WHERE 1=1 {statusFilter}
                    ORDER BY d.created_at DESC
                    LIMIT 20 OFFSET @offset;";

                await using var cmd = new NpgsqlCommand(sql, connection);
                if (!string.IsNullOrWhiteSpace(status))
                    cmd.Parameters.Add("@status", NpgsqlDbType.Text).Value = status;
                cmd.Parameters.Add("@offset", NpgsqlDbType.Integer).Value = (Math.Clamp(page, 1, 100000) - 1) * 20;

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
                        reporterEmail = rdr.GetString(10),
                        description = rdr.GetString(11),
                        evidence = rdr.IsDBNull(12) ? null : rdr.GetString(12),
                        adminNotes = rdr.IsDBNull(13) ? null : rdr.GetString(13)
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
                if (!allowedResolutions.Contains(req.resolution ?? ""))
                    return BadRequest(new { message = "Choose a valid dispute resolution." });
                var resolution = req.resolution!;

                await using var connection = new NpgsqlConnection(_connectionString);
                await connection.OpenAsync();
                await EnsureSchemaAsync(connection);
                await EscrowService.EnsureSchemaAsync(connection);
                await using var transaction = await connection.BeginTransactionAsync();
                const string lockSql = @"
                    SELECT b.id FROM bookings b JOIN booking_disputes d ON d.booking_id = b.id
                    WHERE d.id = @id AND d.status IN ('Open', 'Escalated') FOR UPDATE OF b, d;";
                await using var lockCmd = new NpgsqlCommand(lockSql, connection, transaction);
                lockCmd.Parameters.Add("@id", NpgsqlDbType.Uuid).Value = disputeId;
                if (await lockCmd.ExecuteScalarAsync() is not Guid lockedBookingId)
                    return Conflict(new { message = "This dispute was already resolved or does not exist." });
                if (resolution == "Refunded")
                {
                    const string refundSql = "SELECT EXISTS (SELECT 1 FROM escrow_transactions WHERE booking_id = @id AND status = 'Refunded');";
                    await using var refundCmd = new NpgsqlCommand(refundSql, connection, transaction);
                    refundCmd.Parameters.Add("@id", NpgsqlDbType.Uuid).Value = lockedBookingId;
                    if (!(bool)(await refundCmd.ExecuteScalarAsync())!)
                        return Conflict(new { message = "Complete and verify the refund before resolving this dispute as refunded." });
                }
                else if (resolution == "PartialRefund")
                {
                    if (!req.customerRefundAmount.HasValue || req.customerRefundAmount.Value <= 0)
                        return BadRequest(new { message = "A refund amount is required for a partial settlement." });
                }


                const string sql = @"
                    UPDATE booking_disputes
                    SET status = CASE WHEN @resolution = 'EscalatedToAdmin' THEN 'Escalated' ELSE 'Resolved' END, resolution = @resolution,
                        admin_notes = @notes, resolved_at = CASE WHEN @resolution = 'EscalatedToAdmin' THEN NULL ELSE NOW() END, updated_at = NOW()
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
                const string holdSql = "UPDATE bookings SET dispute_hold = @hold WHERE id = @id;";
                await using var holdCmd = new NpgsqlCommand(holdSql, connection);
                holdCmd.Parameters.Add("@id", NpgsqlDbType.Uuid).Value = bookingId.Value;
                holdCmd.Parameters.Add("@hold", NpgsqlDbType.Boolean).Value = resolution == "EscalatedToAdmin";
                await holdCmd.ExecuteNonQueryAsync();

                if (resolution is "Dismissed" or "NoAction" or "WarningIssued")
                {
                    await EscrowService.ClearDisputeAsync(connection, bookingId.Value);
                    await transaction.CommitAsync();

                    const string releaseCheckSql = @"
                        SELECT COALESCE(status, ''), COALESCE(talent_platform_fee_status, 'Unpaid')
                        FROM bookings WHERE id = @id;";
                    await using var releaseCheckCmd = new NpgsqlCommand(releaseCheckSql, connection);
                    releaseCheckCmd.Parameters.Add("@id", NpgsqlDbType.Uuid).Value = bookingId.Value;
                    await using var releaseReader = await releaseCheckCmd.ExecuteReaderAsync();
                    if (await releaseReader.ReadAsync())
                    {
                        var isCompleted = string.Equals(releaseReader.GetString(0), "Completed", StringComparison.OrdinalIgnoreCase);
                        var platformPaid = string.Equals(releaseReader.GetString(1), "Paid", StringComparison.OrdinalIgnoreCase);
                        await releaseReader.CloseAsync();
                        if (isCompleted && platformPaid)
                        {
                            await EscrowService.MarkReleaseReadyAsync(connection, bookingId.Value);
                            await EscrowService.ReleaseAsync(connection, bookingId.Value, "dispute_dismissed");
                        }
                    }
                }

                PartialSettlementOutcome? settlement = null;
                if (resolution == "PartialRefund")
                {
                    try
                    {
                        settlement = await EscrowService.SettlePartialAsync(
                            connection, bookingId.Value, GetActorId() ?? Guid.Empty, disputeId,
                            req.customerRefundAmount!.Value,
                            SecuritySupport.SanitizePlainText(req.adminNotes, 1000, true), transaction);
                        if (!settlement.Settled)
                            return Conflict(new { message = "The escrow has already been settled." });
                    }
                    catch (InvalidOperationException ex)
                    {
                        return Conflict(new { message = ex.Message });
                    }
                }

                if (resolution is not ("Dismissed" or "NoAction" or "WarningIssued"))
                    await transaction.CommitAsync();

                if (resolution == "PartialRefund")
                {
                    var adminId = GetActorId() ?? Guid.Empty;
                    var adminNotesClean = SecuritySupport.SanitizePlainText(req.adminNotes, 1000, true);
                    try
                    {
                        if (!settlement!.Provider.Equals("Wallet", StringComparison.OrdinalIgnoreCase) &&
                            !string.IsNullOrWhiteSpace(settlement.ProviderPaymentId))
                        {
                            var refundRequestId = await PaymentRefundService.CreateRefundRequestAsync(
                                connection, "booking", "booking_talent_fee", bookingId.Value, null,
                                adminId, null, "Admin", "booking_dispute_partial", adminNotesClean,
                                settlement.CustomerRefundAmount, settlement.ProviderPaymentId,
                                new { componentType = "talent", partialRefund = true });
                            var refundResult = await PaymentRefundService.CreatePayMongoRefundAsync(
                                _paymongoSecretKey, settlement.ProviderPaymentId, settlement.CustomerRefundAmount,
                                "booking_dispute_partial", adminNotesClean);
                            await PaymentRefundService.UpdateRefundRequestAsync(connection, refundRequestId,
                                status: string.Equals(refundResult.Status, "Refunded", StringComparison.OrdinalIgnoreCase) ? "Refunded" : string.Equals(refundResult.Status, "Refund Pending", StringComparison.OrdinalIgnoreCase) ? "ManualReview" : "Failed",
                                providerRefundId: refundResult.RefundId, providerStatus: refundResult.ProviderStatus,
                                errorCode: refundResult.ErrorCode, errorMessage: refundResult.ErrorMessage);
                            if (string.Equals(refundResult.Status, "Refunded", StringComparison.OrdinalIgnoreCase))
                                await EscrowService.ApplyRefundOutcomeAsync(connection, bookingId.Value);
                        }
                    }
                    catch (InvalidOperationException ex)
                    {
                        return Conflict(new { message = ex.Message });
                    }
                }

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
                            resolution == "EscalatedToAdmin" ? "dispute_escalated" : "dispute_resolved",
                            resolution == "EscalatedToAdmin" ? "Dispute Escalated" : "Dispute Resolved",
                            $"Your dispute has been reviewed: {resolution}. {(string.IsNullOrWhiteSpace(req.adminNotes) ? "" : "Admin note: " + req.adminNotes)}",
                            bookingId.Value, "booking");
                    }
                }

                return Ok(new { message = resolution == "EscalatedToAdmin" ? "Dispute escalated. Funds remain on hold." : "Dispute resolved." });
            }
            catch (Exception ex)
            {
                return StatusCode(500, new { message = "Failed to resolve dispute: " + ex.Message });
            }
        }
    }
}
