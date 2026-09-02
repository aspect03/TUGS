using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Authorization;
using ImajinationAPI.Models;
using ImajinationAPI.Services;
using Npgsql;
using NpgsqlTypes;
using System.Net.Http.Headers;
using System.Security.Claims;
using System.Text;
using System.Text.Json;

namespace ImajinationAPI.Controllers
{
    public class CreateBookingRequest
    {
        public Guid customerId { get; set; }
        public Guid targetUserId { get; set; }
        public Guid? eventId { get; set; }
        public string? requesterRole { get; set; }
        public string? targetRole { get; set; }
        public string? serviceType { get; set; }
        public string? eventTitle { get; set; }
        public DateTime? eventDate { get; set; }
        public DateTime? eventEndTime { get; set; }
        public string? location { get; set; }
        public decimal? budget { get; set; }
        public string? notes { get; set; }
        public string? message { get; set; }
        public List<BookingSlotRequest>? bookingSlots { get; set; }
    }

    public class BookingSlotRequest
    {
        public DateTime? eventDate { get; set; }
        public DateTime? eventEndTime { get; set; }
    }

    public class UpdateBookingStatusRequest
    {
        public string? status { get; set; }
    }

    public class CloseBookingConversationRequest
    {
        public string? reason { get; set; }
    }

    public class BookingCheckoutRequest
    {
        public string? successUrl { get; set; }
        public string? cancelUrl { get; set; }
        public string? paymentType { get; set; }
    }

    public class UpdateBookingContractRequest
    {
        public string? title { get; set; }
        public string? terms { get; set; }
        public decimal? agreedFee { get; set; }
        public string? contractStatus { get; set; }
    }

    internal sealed class BookingParticipantContext
    {
        public Guid BookingId { get; init; }
        public Guid CustomerId { get; init; }
        public Guid TargetUserId { get; init; }
        public string TargetRole { get; init; } = "Artist";
        public string Status { get; init; } = "Pending";
        public decimal Budget { get; init; }
    }

    public class CreateBookingRefundRequest
    {
        public string? reasonCode { get; set; }
        public string? notes { get; set; }
    }

    [Route("api/[controller]")]
    [ApiController]
    [Authorize]
    public class BookingController : ControllerBase
    {
        private readonly string _connectionString;
        private readonly string _paymongoSecretKey;
        private readonly MessageProtectionService _messageProtection;
        private readonly IConfiguration _configuration;
        private readonly ImajinationAPI.Services.EmailService _emailService;
        private const decimal BookingServiceFee = 15m;
        private const decimal PayMongoMinimumAmount = 15m;
        private const decimal FixedTalentPlatformFee = 15m;

        public BookingController(IConfiguration configuration, MessageProtectionService messageProtection, ImajinationAPI.Services.EmailService emailService)
        {
            _configuration = configuration;
            _connectionString = ConfigurationFallbacks.GetRequiredSupabaseConnectionString(configuration);
            _paymongoSecretKey = configuration["PayMongo:SecretKey"] ?? string.Empty;
            _messageProtection = messageProtection;
            _emailService = emailService;
        }

        private static string NormalizePaymentStatus(string? rawStatus)
        {
            if (string.IsNullOrWhiteSpace(rawStatus))
            {
                return "Unpaid";
            }

            return rawStatus.Trim();
        }

        private static string NormalizeServiceFeeStatus(string? rawServiceFeeStatus, string? rawPaymentStatus)
        {
            var serviceStatus = NormalizePaymentStatus(rawServiceFeeStatus);
            var paymentStatus = NormalizePaymentStatus(rawPaymentStatus);

            if (serviceStatus.Equals("ServiceFeePaid", StringComparison.OrdinalIgnoreCase))
            {
                return "Paid";
            }

            if (serviceStatus.Equals("Paid", StringComparison.OrdinalIgnoreCase) ||
                serviceStatus.Equals("AwaitingPayment", StringComparison.OrdinalIgnoreCase) ||
                serviceStatus.Equals("NotRequired", StringComparison.OrdinalIgnoreCase) ||
                serviceStatus.Equals("Refund Pending", StringComparison.OrdinalIgnoreCase) ||
                serviceStatus.Equals("Unpaid", StringComparison.OrdinalIgnoreCase))
            {
                return serviceStatus;
            }

            if (paymentStatus.Equals("ServiceFeePaid", StringComparison.OrdinalIgnoreCase))
            {
                return "Paid";
            }

            if (paymentStatus.Equals("Paid", StringComparison.OrdinalIgnoreCase) ||
                paymentStatus.Equals("AwaitingPayment", StringComparison.OrdinalIgnoreCase) ||
                paymentStatus.Equals("NotRequired", StringComparison.OrdinalIgnoreCase) ||
                paymentStatus.Equals("Refund Pending", StringComparison.OrdinalIgnoreCase))
            {
                return paymentStatus;
            }

            return "Unpaid";
        }

        private static string NormalizeTalentFeeStatus(string? rawTalentFeeStatus)
        {
            var talentStatus = NormalizePaymentStatus(rawTalentFeeStatus);

            if (talentStatus.Equals("AwaitingTalentFeePayment", StringComparison.OrdinalIgnoreCase))
            {
                return "AwaitingPayment";
            }

             if (talentStatus.Equals("TalentFeePaid", StringComparison.OrdinalIgnoreCase))
            {
                return "HeldInEscrow";
            }

            return talentStatus;
        }

        private const string CompletionPendingCustomerConfirmation = "Completion Pending Customer Confirmation";
        private const string CompletionPendingTalentConfirmation = "Completion Pending Talent Confirmation";

        private static bool IsCompletionPendingStatus(string? status) =>
            string.Equals(status, CompletionPendingCustomerConfirmation, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(status, CompletionPendingTalentConfirmation, StringComparison.OrdinalIgnoreCase);

        private static Guid? GetActorUserId(ClaimsPrincipal user)
        {
            return Guid.TryParse(user.FindFirstValue(ClaimTypes.NameIdentifier), out var parsedUserId)
                ? parsedUserId
                : null;
        }

        private static string GetActorRole(ClaimsPrincipal user)
        {
            var role = user.FindFirstValue(ClaimTypes.Role);
            return string.IsNullOrWhiteSpace(role) ? "User" : role.Trim();
        }

        private static bool CanAccessBooking(Guid actorUserId, string actorRole, BookingParticipantContext context)
        {
            if (actorUserId == Guid.Empty)
            {
                return false;
            }

            if (actorRole.Equals("Admin", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            return actorUserId == context.CustomerId || actorUserId == context.TargetUserId;
        }

        private static bool CanAccessUserScopedBookingRoute(Guid actorUserId, string actorRole, Guid requestedUserId)
        {
            if (actorUserId == Guid.Empty || requestedUserId == Guid.Empty)
            {
                return false;
            }

            if (actorRole.Equals("Admin", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            return actorUserId == requestedUserId;
        }

        private static bool IsTalentRole(string? role) =>
            string.Equals(role, "Artist", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(role, "Sessionist", StringComparison.OrdinalIgnoreCase);

        private static bool CanRespondToProposal(Guid actorUserId, string actorRole, string? proposedByRole, Guid? proposedByUserId)
        {
            if (actorUserId == Guid.Empty)
            {
                return false;
            }

            if (proposedByUserId.HasValue && proposedByUserId.Value == actorUserId)
            {
                return false;
            }

            if (string.IsNullOrWhiteSpace(proposedByRole))
            {
                return true;
            }

            return !string.Equals(actorRole, proposedByRole, StringComparison.OrdinalIgnoreCase);
        }

        private async Task<BookingParticipantContext?> GetBookingParticipantContextAsync(NpgsqlConnection connection, Guid bookingId)
        {
            const string sql = @"
                SELECT id,
                       customer_id,
                       target_user_id,
                       COALESCE(target_role, 'Artist'),
                       COALESCE(status, 'Pending'),
                       COALESCE(budget, 0)
                FROM bookings
                WHERE id = @id;";

            await using var cmd = new NpgsqlCommand(sql, connection);
            cmd.Parameters.Add("@id", NpgsqlDbType.Uuid).Value = bookingId;
            await using var reader = await cmd.ExecuteReaderAsync();
            if (!await reader.ReadAsync())
            {
                return null;
            }

            return new BookingParticipantContext
            {
                BookingId = reader.GetGuid(0),
                CustomerId = reader.IsDBNull(1) ? Guid.Empty : reader.GetGuid(1),
                TargetUserId = reader.IsDBNull(2) ? Guid.Empty : reader.GetGuid(2),
                TargetRole = reader.IsDBNull(3) ? "Artist" : NormalizeTargetRole(reader.GetString(3)),
                Status = reader.IsDBNull(4) ? "Pending" : reader.GetString(4),
                Budget = reader.IsDBNull(5) ? 0 : reader.GetDecimal(5)
            };
        }

        private static async Task<int> CountActiveBookingsForCustomerAsync(NpgsqlConnection connection, Guid customerId, string targetRole)
        {
            const string sql = @"
                SELECT COUNT(*)
                FROM bookings
                WHERE customer_id = @customerId
                  AND LOWER(COALESCE(target_role, '')) = LOWER(@targetRole)
                  AND COALESCE(status, 'Pending') NOT ILIKE 'Cancelled%'
                  AND LOWER(COALESCE(status, 'Pending')) <> 'completed';";

            using var cmd = new NpgsqlCommand(sql, connection);
            cmd.Parameters.Add("@customerId", NpgsqlDbType.Uuid).Value = customerId;
            cmd.Parameters.Add("@targetRole", NpgsqlDbType.Text).Value = targetRole;
            var result = await cmd.ExecuteScalarAsync();
            return result is null || result == DBNull.Value ? 0 : Convert.ToInt32(result);
        }

        private static DateTime? NormalizeBookingEndToUtc(DateTime? eventDate, DateTime? eventEndTime)
        {
            var normalizedStart = PlatformFeatureSupport.NormalizeToUtc(eventDate);
            if (!normalizedStart.HasValue)
            {
                return null;
            }

            var normalizedEnd = PlatformFeatureSupport.NormalizeToUtc(eventEndTime);
            if (!normalizedEnd.HasValue || normalizedEnd <= normalizedStart)
            {
                return normalizedStart.Value.AddHours(4);
            }

            return normalizedEnd;
        }

        private static decimal CalculateTalentPlatformFee(string targetRole, decimal? budget)
        {
            if (!IsTalentRole(targetRole))
            {
                return 0m;
            }

            var normalizedBudget = budget.GetValueOrDefault();
            if (normalizedBudget <= 0)
            {
                return 0m;
            }

            return FixedTalentPlatformFee;
        }

        [HttpPost]
        public async Task<IActionResult> CreateBooking([FromBody] CreateBookingRequest req)
        {
            try
            {
                var actorUserId = GetActorUserId(User);
                var actorRole = GetActorRole(User);
                if (!actorUserId.HasValue)
                {
                    return Unauthorized(new { message = "Sign in again before creating a booking." });
                }

                await using var connection = new NpgsqlConnection(_connectionString);
                await connection.OpenAsync();
                await EnsureBookingsTableExists(connection);
                await PlatformFeatureSupport.EnsureSharedBusinessSchemaAsync(connection);
                await CommunitySupport.EnsureCommunitySchemaAsync(connection);

                if (req.customerId == Guid.Empty || req.targetUserId == Guid.Empty)
                {
                    return BadRequest(new { message = "Missing booking participants." });
                }

                if (req.customerId != actorUserId.Value && !actorRole.Equals("Admin", StringComparison.OrdinalIgnoreCase))
                {
                    return Forbid();
                }

                var normalizedRole = NormalizeTargetRole(req.targetRole);
                if ((string.Equals(normalizedRole, "Artist", StringComparison.OrdinalIgnoreCase) ||
                     string.Equals(normalizedRole, "Sessionist", StringComparison.OrdinalIgnoreCase)) &&
                    !await CommunitySupport.IsIdentityApprovedAsync(connection, req.targetUserId, normalizedRole))
                {
                    return Conflict(new { message = $"{normalizedRole} cannot receive bookings until admin verification is approved." });
                }

                var notes = string.IsNullOrWhiteSpace(req.notes) ? req.message : req.notes;
                var protectedNotes = string.IsNullOrWhiteSpace(notes) ? null : _messageProtection.Protect(notes);
                var bookingFee = BookingServiceFee;
                var talentPlatformFee = CalculateTalentPlatformFee(normalizedRole, req.budget);
                var initialStatus = $"Awaiting {normalizedRole} Fee Payment";
                var paymentStatus = "Unpaid";
                var serviceFeeStatus = "Unpaid";
                var talentPlatformFeeStatus = talentPlatformFee > 0 ? "Unpaid" : "NotRequired";
                await EnsureNotificationsTableExists(connection);

                var bookingWindows = new List<(DateTime Start, DateTime End)>();
                if (req.bookingSlots is { Count: > 0 })
                {
                    foreach (var slot in req.bookingSlots.Where(item => item?.eventDate != null))
                    {
                        var normalizedStart = NormalizeToUtc(slot.eventDate);
                        var normalizedEnd = NormalizeBookingEndToUtc(slot.eventDate, slot.eventEndTime);
                        if (!normalizedStart.HasValue || !normalizedEnd.HasValue)
                        {
                            continue;
                        }

                        bookingWindows.Add((normalizedStart.Value, normalizedEnd.Value));
                    }
                }

                if (bookingWindows.Count == 0)
                {
                    var normalizedStart = NormalizeToUtc(req.eventDate);
                    var normalizedEnd = NormalizeBookingEndToUtc(req.eventDate, req.eventEndTime);
                    if (normalizedStart.HasValue && normalizedEnd.HasValue)
                    {
                        bookingWindows.Add((normalizedStart.Value, normalizedEnd.Value));
                    }
                }

                if (bookingWindows.Count == 0)
                {
                    return BadRequest(new { message = "Add at least one booking date and end time." });
                }

                if (string.Equals(actorRole, "Customer", StringComparison.OrdinalIgnoreCase))
                {
                    var activeBookingsForRole = await CountActiveBookingsForCustomerAsync(connection, req.customerId, normalizedRole);
                    if (activeBookingsForRole + bookingWindows.Count > 20)
                    {
                        return Conflict(new
                        {
                            message = $"You can only keep 20 active {normalizedRole.ToLowerInvariant()} bookings at the same time. Finish or cancel an existing request first."
                        });
                    }
                }

                foreach (var window in bookingWindows)
                {
                    if (await PlatformFeatureSupport.UserHasCalendarBlockAsync(connection, req.targetUserId, normalizedRole, window.Start))
                    {
                        return Conflict(new { message = $"{normalizedRole} blocked {window.Start:MMMM d, yyyy} from bookings." });
                    }

                    if (await PlatformFeatureSupport.UserHasBookingConflictAsync(connection, req.targetUserId, window.Start, window.End))
                    {
                        return Conflict(new { message = $"{normalizedRole} is already booked during one of the selected time slots." });
                    }
                }

                const string sql = @"
                    INSERT INTO bookings
                        (id, customer_id, target_user_id, event_id, target_role, service_type, event_title, event_date, event_end_time, location, budget, booking_fee, notes, message, status, payment_status, service_fee_status, talent_platform_fee, talent_platform_fee_status, booking_group_id, booking_sequence, created_at)
                    VALUES
                        (@id, @customerId, @targetUserId, @eventId, @targetRole, @serviceType, @eventTitle, @eventDate, @eventEndTime, @location, @budget, @bookingFee, @notes, @message, @status, @paymentStatus, @serviceFeeStatus, @talentPlatformFee, @talentPlatformFeeStatus, @bookingGroupId, @bookingSequence, NOW());";

                var bookingGroupId = bookingWindows.Count > 1 ? Guid.NewGuid() : (Guid?)null;
                var bookingIds = new List<Guid>();

                for (var index = 0; index < bookingWindows.Count; index++)
                {
                    var bookingId = Guid.NewGuid();
                    var window = bookingWindows[index];
                    using var cmd = new NpgsqlCommand(sql, connection);
                    cmd.Parameters.Add("@id", NpgsqlDbType.Uuid).Value = bookingId;
                    cmd.Parameters.Add("@customerId", NpgsqlDbType.Uuid).Value = req.customerId;
                    cmd.Parameters.Add("@targetUserId", NpgsqlDbType.Uuid).Value = req.targetUserId;
                    cmd.Parameters.Add("@eventId", NpgsqlDbType.Uuid).Value = (object?)req.eventId ?? DBNull.Value;
                    cmd.Parameters.Add("@targetRole", NpgsqlDbType.Text).Value = normalizedRole;
                    cmd.Parameters.Add("@serviceType", NpgsqlDbType.Text).Value = (object?)req.serviceType ?? DBNull.Value;
                    cmd.Parameters.Add("@eventTitle", NpgsqlDbType.Text).Value = (object?)req.eventTitle ?? DBNull.Value;
                    cmd.Parameters.Add("@eventDate", NpgsqlDbType.TimestampTz).Value = window.Start;
                    cmd.Parameters.Add("@eventEndTime", NpgsqlDbType.TimestampTz).Value = window.End;
                    cmd.Parameters.Add("@location", NpgsqlDbType.Text).Value = (object?)req.location ?? DBNull.Value;
                    cmd.Parameters.Add("@budget", NpgsqlDbType.Numeric).Value = (object?)req.budget ?? DBNull.Value;
                    cmd.Parameters.Add("@bookingFee", NpgsqlDbType.Numeric).Value = bookingFee;
                    cmd.Parameters.Add("@notes", NpgsqlDbType.Text).Value = (object?)protectedNotes ?? DBNull.Value;
                    cmd.Parameters.Add("@message", NpgsqlDbType.Text).Value = (object?)protectedNotes ?? DBNull.Value;
                    cmd.Parameters.Add("@status", NpgsqlDbType.Text).Value = initialStatus;
                    cmd.Parameters.Add("@paymentStatus", NpgsqlDbType.Text).Value = paymentStatus;
                    cmd.Parameters.Add("@serviceFeeStatus", NpgsqlDbType.Text).Value = serviceFeeStatus;
                    cmd.Parameters.Add("@talentPlatformFee", NpgsqlDbType.Numeric).Value = talentPlatformFee;
                    cmd.Parameters.Add("@talentPlatformFeeStatus", NpgsqlDbType.Text).Value = talentPlatformFeeStatus;
                    cmd.Parameters.Add("@bookingGroupId", NpgsqlDbType.Uuid).Value = (object?)bookingGroupId ?? DBNull.Value;
                    cmd.Parameters.Add("@bookingSequence", NpgsqlDbType.Integer).Value = index + 1;
                    await cmd.ExecuteNonQueryAsync();
                    bookingIds.Add(bookingId);

                    await InsertNotification(
                        connection,
                        req.targetUserId,
                        "booking_request",
                        "New booking request",
                        $"{(string.IsNullOrWhiteSpace(req.eventTitle) ? "A new booking request" : req.eventTitle)} is waiting for your review.",
                        bookingId,
                        "booking");
                }

                // Fire-and-forget: email talent about the new request
                var (custFirst, custLast) = await GetUserNameAsync(connection, req.customerId);
                var customerDisplayName = $"{custFirst} {custLast}".Trim();
                if (string.IsNullOrWhiteSpace(customerDisplayName)) customerDisplayName = "A customer";
                _ = _emailService.SendBookingRequestEmailAsync(
                    connection, req.targetUserId, customerDisplayName, normalizedRole,
                    req.eventTitle ?? "Untitled Event",
                    req.bookingSlots?.FirstOrDefault()?.eventDate ?? req.eventDate,
                    req.location, req.budget);

                return Ok(new
                {
                    message = bookingIds.Count > 1
                        ? $"{bookingIds.Count} booking requests were created. Continue with the first service-fee payment to start the first chat thread."
                        : $"Booking created. Pay the {FormatPeso(BookingServiceFee)} service fee to open the chat.",
                    bookingId = bookingIds.FirstOrDefault(),
                    bookingIds,
                    bookingCount = bookingIds.Count,
                    eventId = req.eventId,
                    bookingFee = bookingFee,
                    talentPlatformFee,
                    status = initialStatus
                });
            }
            catch (Exception ex)
            {
                return StatusCode(500, new { message = "Failed to create booking request: " + ex.Message });
            }
        }

        [HttpGet("target/{userId}")]
        public async Task<IActionResult> GetBookingsForTarget(Guid userId)
        {
            try
            {
                var actorUserId = GetActorUserId(User);
                var actorRole = GetActorRole(User);
                if (!actorUserId.HasValue)
                {
                    return Unauthorized(new { message = "Sign in again before viewing bookings." });
                }

                if (!CanAccessUserScopedBookingRoute(actorUserId.Value, actorRole, userId))
                {
                    return Forbid();
                }

                var bookings = new List<object>();
                await using var connection = new NpgsqlConnection(_connectionString);
                await connection.OpenAsync();
                await EnsureBookingsTableExists(connection);
                await EnsureBookingMessagesTableExists(connection);

                const string sql = @"
                    SELECT
                        b.id,
                        b.customer_id,
                        b.target_user_id,
                        b.event_id,
                        COALESCE(u.firstname, '') AS customer_firstname,
                        COALESCE(u.lastname, '') AS customer_lastname,
                        COALESCE(u.email, '') AS customer_email,
                        COALESCE(b.service_type, ''),
                        COALESCE(b.event_title, ''),
                        b.event_date,
                        b.event_end_time,
                        COALESCE(b.location, ''),
                        COALESCE(b.budget, 0),
                        COALESCE(b.booking_fee, 15),
                        COALESCE(b.talent_platform_fee, 0),
                        COALESCE(b.notes, COALESCE(b.message, '')),
                        COALESCE(b.message, ''),
                        COALESCE(b.status, 'Pending'),
                        b.created_at,
                        COALESCE(b.payment_status, 'Unpaid'),
                        COALESCE(b.payment_method, ''),
                        COALESCE(b.service_fee_status, COALESCE(b.payment_status, 'Unpaid')),
                        COALESCE(b.talent_fee_status, 'Unpaid'),
                        COALESCE(b.talent_platform_fee_status, 'Unpaid'),
                        COALESCE(b.service_fee_payment_method, COALESCE(b.payment_method, '')),
                        COALESCE(b.talent_fee_payment_method, ''),
                        COALESCE(b.talent_platform_fee_payment_method, ''),
                        b.customer_completed_at,
                        b.target_completed_at,
                        EXISTS (
                            SELECT 1
                            FROM booking_messages m
                            WHERE m.booking_id = b.id
                        ) AS has_messages
                    FROM bookings b
                    LEFT JOIN users u ON u.id = b.customer_id
                    WHERE b.target_user_id = @userId
                    ORDER BY b.created_at DESC;";

                using var cmd = new NpgsqlCommand(sql, connection);
                cmd.Parameters.Add("@userId", NpgsqlDbType.Uuid).Value = userId;

                using var reader = await cmd.ExecuteReaderAsync();
                while (await reader.ReadAsync())
                {
                    var firstName = reader.IsDBNull(4) ? "" : reader.GetString(4);
                    var lastName = reader.IsDBNull(5) ? "" : reader.GetString(5);
                    var decryptedNotes = _messageProtection.Unprotect(reader.IsDBNull(15) ? "" : reader.GetString(15));
                    var decryptedMessage = _messageProtection.Unprotect(reader.IsDBNull(16) ? "" : reader.GetString(16));

                    bookings.Add(new
                    {
                        id = reader.GetGuid(0),
                        customerId = reader.IsDBNull(1) ? Guid.Empty : reader.GetGuid(1),
                        targetUserId = reader.IsDBNull(2) ? Guid.Empty : reader.GetGuid(2),
                        eventId = reader.IsDBNull(3) ? (Guid?)null : reader.GetGuid(3),
                        customerName = $"{firstName} {lastName}".Trim(),
                        customerEmail = reader.IsDBNull(6) ? "" : reader.GetString(6),
                        serviceType = reader.IsDBNull(7) ? "" : reader.GetString(7),
                        eventTitle = reader.IsDBNull(8) ? "" : reader.GetString(8),
                        eventDate = reader.IsDBNull(9) ? (DateTime?)null : reader.GetDateTime(9),
                        eventEndTime = reader.IsDBNull(10) ? (DateTime?)null : reader.GetDateTime(10),
                        location = reader.IsDBNull(11) ? "" : reader.GetString(11),
                        budget = reader.IsDBNull(12) ? 0 : reader.GetDecimal(12),
                        bookingFee = reader.IsDBNull(13) ? BookingServiceFee : reader.GetDecimal(13),
                        talentPlatformFee = reader.IsDBNull(14) ? 0 : reader.GetDecimal(14),
                        notes = decryptedNotes,
                        message = decryptedMessage,
                        status = reader.IsDBNull(17) ? "Pending" : reader.GetString(17),
                        createdAt = reader.IsDBNull(18) ? DateTime.UtcNow : reader.GetDateTime(18),
                        paymentStatus = NormalizePaymentStatus(reader.IsDBNull(19) ? "Unpaid" : reader.GetString(19)),
                        paymentMethod = reader.IsDBNull(20) ? "" : reader.GetString(20),
                        serviceFeeStatus = NormalizeServiceFeeStatus(reader.IsDBNull(21) ? "Unpaid" : reader.GetString(21), reader.IsDBNull(19) ? "Unpaid" : reader.GetString(19)),
                        talentFeeStatus = NormalizeTalentFeeStatus(reader.IsDBNull(22) ? "Unpaid" : reader.GetString(22)),
                        talentPlatformFeeStatus = NormalizePaymentStatus(reader.IsDBNull(23) ? "Unpaid" : reader.GetString(23)),
                        serviceFeePaymentMethod = reader.IsDBNull(24) ? "" : reader.GetString(24),
                        talentFeePaymentMethod = reader.IsDBNull(25) ? "" : reader.GetString(25),
                        talentPlatformFeePaymentMethod = reader.IsDBNull(26) ? "" : reader.GetString(26),
                        customerCompletedAt = reader.IsDBNull(27) ? (DateTime?)null : reader.GetDateTime(27),
                        targetCompletedAt = reader.IsDBNull(28) ? (DateTime?)null : reader.GetDateTime(28),
                        hasMessages = !reader.IsDBNull(29) && reader.GetBoolean(29)
                    });
                }

                return Ok(bookings);
            }
            catch (Exception ex)
            {
                return StatusCode(500, new { message = "Failed to fetch bookings: " + ex.Message });
            }
        }

        [HttpGet("talent/{userId}/wallet")]
        public async Task<IActionResult> GetTalentWallet(Guid userId)
        {
            try
            {
                var actorUserId = GetActorUserId(User);
                var actorRole = GetActorRole(User);
                if (!actorUserId.HasValue) return Unauthorized(new { message = "Sign in again before viewing wallet." });
                if (!CanAccessUserScopedBookingRoute(actorUserId.Value, actorRole, userId)) return Forbid();

                await using var connection = new NpgsqlConnection(_connectionString);
                await connection.OpenAsync();
                await EnsureBookingsTableExists(connection);

                const string summarySql = @"
                    SELECT
                        COALESCE(SUM(CASE WHEN COALESCE(talent_fee_status, 'Unpaid') = 'Released' THEN COALESCE(budget, 0) ELSE 0 END), 0),
                        COALESCE(SUM(CASE WHEN COALESCE(talent_fee_status, 'Unpaid') IN ('HeldInEscrow', 'ReadyForRelease') THEN COALESCE(budget, 0) ELSE 0 END), 0),
                        COALESCE(SUM(CASE WHEN COALESCE(talent_fee_status, 'Unpaid') IN ('Unpaid', 'AwaitingPayment') AND COALESCE(status, '') = 'Confirmed' THEN COALESCE(budget, 0) ELSE 0 END), 0),
                        COALESCE(SUM(CASE WHEN COALESCE(talent_platform_fee_status, 'Unpaid') = 'Paid' THEN COALESCE(talent_platform_fee, 0) ELSE 0 END), 0),
                        COUNT(*) FILTER (WHERE COALESCE(status, '') = 'Completed')
                    FROM bookings
                    WHERE target_user_id = @userId;";

                decimal released;
                decimal escrow;
                decimal awaiting;
                decimal platformFees;
                long completedCount;
                await using (var summaryCmd = new NpgsqlCommand(summarySql, connection))
                {
                    summaryCmd.Parameters.Add("@userId", NpgsqlDbType.Uuid).Value = userId;
                    await using var reader = await summaryCmd.ExecuteReaderAsync(System.Data.CommandBehavior.SingleRow);
                    await reader.ReadAsync();
                    released = reader.GetDecimal(0);
                    escrow = reader.GetDecimal(1);
                    awaiting = reader.GetDecimal(2);
                    platformFees = reader.GetDecimal(3);
                    completedCount = reader.GetInt64(4);
                }

                var transactions = new List<object>();
                const string txSql = @"
                    SELECT id, COALESCE(event_title, ''), event_date, COALESCE(budget, 0),
                           COALESCE(status, 'Pending'), COALESCE(talent_fee_status, 'Unpaid'),
                           COALESCE(talent_platform_fee, 0), COALESCE(talent_platform_fee_status, 'Unpaid'),
                           talent_fee_paid_at, talent_platform_fee_paid_at, updated_at
                    FROM bookings
                    WHERE target_user_id = @userId
                    ORDER BY COALESCE(updated_at, created_at) DESC
                    LIMIT 10;";
                await using (var txCmd = new NpgsqlCommand(txSql, connection))
                {
                    txCmd.Parameters.Add("@userId", NpgsqlDbType.Uuid).Value = userId;
                    await using var reader = await txCmd.ExecuteReaderAsync();
                    while (await reader.ReadAsync())
                    {
                        transactions.Add(new
                        {
                            id = reader.GetGuid(0),
                            eventTitle = reader.IsDBNull(1) ? "" : reader.GetString(1),
                            eventDate = reader.IsDBNull(2) ? (DateTime?)null : reader.GetDateTime(2),
                            budget = reader.IsDBNull(3) ? 0 : reader.GetDecimal(3),
                            status = reader.IsDBNull(4) ? "Pending" : reader.GetString(4),
                            talentFeeStatus = reader.IsDBNull(5) ? "Unpaid" : reader.GetString(5),
                            talentPlatformFee = reader.IsDBNull(6) ? 0 : reader.GetDecimal(6),
                            talentPlatformFeeStatus = reader.IsDBNull(7) ? "Unpaid" : reader.GetString(7),
                            talentFeePaidAt = reader.IsDBNull(8) ? (DateTime?)null : reader.GetDateTime(8),
                            platformFeePaidAt = reader.IsDBNull(9) ? (DateTime?)null : reader.GetDateTime(9),
                            updatedAt = reader.IsDBNull(10) ? (DateTime?)null : reader.GetDateTime(10)
                        });
                    }
                }

                return Ok(new
                {
                    releasedEarnings = released,
                    heldInEscrow = escrow,
                    awaitingCustomerPayment = awaiting,
                    platformFeesPaid = platformFees,
                    completedBookings = completedCount,
                    transactions
                });
            }
            catch (Exception ex)
            {
                return StatusCode(500, new { message = "Failed to load wallet: " + ex.Message });
            }
        }

        [HttpGet("{bookingId}/tracking")]
        public async Task<IActionResult> GetBookingTracking(Guid bookingId)
        {
            try
            {
                var actorUserId = GetActorUserId(User);
                var actorRole = GetActorRole(User);
                if (!actorUserId.HasValue) return Unauthorized(new { message = "Sign in again before viewing tracking." });

                await using var connection = new NpgsqlConnection(_connectionString);
                await connection.OpenAsync();
                await EnsureBookingsTableExists(connection);
                await EnsureBookingMessagesTableExists(connection);

                var context = await GetBookingParticipantContextAsync(connection, bookingId);
                if (context is null) return NotFound(new { message = "Booking not found." });
                if (!CanAccessBooking(actorUserId.Value, actorRole, context)) return Forbid();

                const string sql = @"
                    SELECT COALESCE(status, 'Pending'), COALESCE(payment_status, 'Unpaid'),
                           COALESCE(service_fee_status, 'Unpaid'), COALESCE(talent_fee_status, 'Unpaid'),
                           COALESCE(talent_platform_fee_status, 'Unpaid'), event_date, event_end_time,
                           customer_completed_at, target_completed_at, updated_at,
                           (SELECT MAX(created_at) FROM booking_messages WHERE booking_id = @bookingId)
                    FROM bookings
                    WHERE id = @bookingId;";
                await using var cmd = new NpgsqlCommand(sql, connection);
                cmd.Parameters.Add("@bookingId", NpgsqlDbType.Uuid).Value = bookingId;
                await using var reader = await cmd.ExecuteReaderAsync(System.Data.CommandBehavior.SingleRow);
                if (!await reader.ReadAsync()) return NotFound(new { message = "Booking not found." });

                var status = reader.GetString(0);
                var paymentStatus = reader.GetString(1);
                var serviceFeeStatus = reader.GetString(2);
                var talentFeeStatus = reader.GetString(3);
                var platformFeeStatus = reader.GetString(4);
                var customerCompletedAt = reader.IsDBNull(7) ? (DateTime?)null : reader.GetDateTime(7);
                var targetCompletedAt = reader.IsDBNull(8) ? (DateTime?)null : reader.GetDateTime(8);

                return Ok(new
                {
                    bookingId,
                    status,
                    paymentStatus,
                    serviceFeeStatus,
                    talentFeeStatus,
                    talentPlatformFeeStatus = platformFeeStatus,
                    eventDate = reader.IsDBNull(5) ? (DateTime?)null : reader.GetDateTime(5),
                    eventEndTime = reader.IsDBNull(6) ? (DateTime?)null : reader.GetDateTime(6),
                    customerCompletedAt,
                    targetCompletedAt,
                    updatedAt = reader.IsDBNull(9) ? (DateTime?)null : reader.GetDateTime(9),
                    lastMessageAt = reader.IsDBNull(10) ? (DateTime?)null : reader.GetDateTime(10),
                    steps = new[]
                    {
                        new { key = "requested", label = "Request sent", done = true },
                        new { key = "service_fee", label = "Service fee paid", done = serviceFeeStatus.Equals("Paid", StringComparison.OrdinalIgnoreCase) || paymentStatus.Equals("Paid", StringComparison.OrdinalIgnoreCase) },
                        new { key = "confirmed", label = "Booking confirmed", done = status.Equals("Confirmed", StringComparison.OrdinalIgnoreCase) || status.Contains("Completion", StringComparison.OrdinalIgnoreCase) || status.Equals("Completed", StringComparison.OrdinalIgnoreCase) },
                        new { key = "escrow", label = "Talent fee secured", done = talentFeeStatus.Equals("HeldInEscrow", StringComparison.OrdinalIgnoreCase) || talentFeeStatus.Equals("ReadyForRelease", StringComparison.OrdinalIgnoreCase) || talentFeeStatus.Equals("Released", StringComparison.OrdinalIgnoreCase) },
                        new { key = "completion", label = "Completion confirmed", done = customerCompletedAt.HasValue && targetCompletedAt.HasValue },
                        new { key = "released", label = "Escrow released", done = talentFeeStatus.Equals("Released", StringComparison.OrdinalIgnoreCase) }
                    }
                });
            }
            catch (Exception ex)
            {
                return StatusCode(500, new { message = "Failed to load booking tracking: " + ex.Message });
            }
        }

        [HttpGet("customer/{userId}")]
        public async Task<IActionResult> GetBookingsForCustomer(Guid userId)
        {
            try
            {
                var actorUserId = GetActorUserId(User);
                var actorRole = GetActorRole(User);
                if (!actorUserId.HasValue)
                {
                    return Unauthorized(new { message = "Sign in again before viewing bookings." });
                }

                if (!CanAccessUserScopedBookingRoute(actorUserId.Value, actorRole, userId))
                {
                    return Forbid();
                }

                var bookings = new List<object>();
                await using var connection = new NpgsqlConnection(_connectionString);
                await connection.OpenAsync();
                await EnsureBookingsTableExists(connection);
                await EnsureBookingMessagesTableExists(connection);

                const string sql = @"
                    SELECT
                        b.id,
                        b.customer_id,
                        b.target_user_id,
                        b.event_id,
                        COALESCE(t.firstname, '') AS target_firstname,
                        COALESCE(t.lastname, '') AS target_lastname,
                        COALESCE(t.stagename, '') AS target_stage_name,
                        COALESCE(b.target_role, ''),
                        COALESCE(b.service_type, ''),
                        COALESCE(b.event_title, ''),
                        b.event_date,
                        b.event_end_time,
                        COALESCE(b.location, ''),
                        COALESCE(b.budget, 0),
                        COALESCE(b.booking_fee, 15),
                        COALESCE(b.talent_platform_fee, 0),
                        COALESCE(b.notes, COALESCE(b.message, '')),
                        COALESCE(b.message, ''),
                        COALESCE(b.status, 'Pending'),
                        b.created_at,
                        COALESCE(b.payment_status, 'Unpaid'),
                        COALESCE(b.payment_method, ''),
                        COALESCE(b.service_fee_status, COALESCE(b.payment_status, 'Unpaid')),
                        COALESCE(b.talent_fee_status, 'Unpaid'),
                        COALESCE(b.talent_platform_fee_status, 'Unpaid'),
                        COALESCE(b.service_fee_payment_method, COALESCE(b.payment_method, '')),
                        COALESCE(b.talent_fee_payment_method, ''),
                        COALESCE(b.talent_platform_fee_payment_method, ''),
                        b.customer_completed_at,
                        b.target_completed_at,
                        EXISTS (
                            SELECT 1
                            FROM booking_messages m
                            WHERE m.booking_id = b.id
                        ) AS has_messages
                    FROM bookings b
                    LEFT JOIN users t ON t.id = b.target_user_id
                    WHERE b.customer_id = @userId
                    ORDER BY b.created_at DESC;";

                using var cmd = new NpgsqlCommand(sql, connection);
                cmd.Parameters.Add("@userId", NpgsqlDbType.Uuid).Value = userId;

                using var reader = await cmd.ExecuteReaderAsync();
                while (await reader.ReadAsync())
                {
                    var targetFirst = reader.IsDBNull(4) ? "" : reader.GetString(4);
                    var targetLast = reader.IsDBNull(5) ? "" : reader.GetString(5);
                    var targetStage = reader.IsDBNull(6) ? "" : reader.GetString(6);
                    var decryptedNotes = _messageProtection.Unprotect(reader.IsDBNull(16) ? "" : reader.GetString(16));
                    var decryptedMessage = _messageProtection.Unprotect(reader.IsDBNull(17) ? "" : reader.GetString(17));

                    bookings.Add(new
                    {
                        id = reader.GetGuid(0),
                        customerId = reader.IsDBNull(1) ? Guid.Empty : reader.GetGuid(1),
                        targetUserId = reader.IsDBNull(2) ? Guid.Empty : reader.GetGuid(2),
                        eventId = reader.IsDBNull(3) ? (Guid?)null : reader.GetGuid(3),
                        targetName = !string.IsNullOrWhiteSpace(targetStage) ? targetStage : $"{targetFirst} {targetLast}".Trim(),
                        targetRole = reader.IsDBNull(7) ? "" : reader.GetString(7),
                        serviceType = reader.IsDBNull(8) ? "" : reader.GetString(8),
                        eventTitle = reader.IsDBNull(9) ? "" : reader.GetString(9),
                        eventDate = reader.IsDBNull(10) ? (DateTime?)null : reader.GetDateTime(10),
                        eventEndTime = reader.IsDBNull(11) ? (DateTime?)null : reader.GetDateTime(11),
                        location = reader.IsDBNull(12) ? "" : reader.GetString(12),
                        budget = reader.IsDBNull(13) ? 0 : reader.GetDecimal(13),
                        bookingFee = reader.IsDBNull(14) ? BookingServiceFee : reader.GetDecimal(14),
                        talentPlatformFee = reader.IsDBNull(15) ? 0 : reader.GetDecimal(15),
                        notes = decryptedNotes,
                        message = decryptedMessage,
                        status = reader.IsDBNull(18) ? "Pending" : reader.GetString(18),
                        createdAt = reader.IsDBNull(19) ? DateTime.UtcNow : reader.GetDateTime(19),
                        paymentStatus = NormalizePaymentStatus(reader.IsDBNull(20) ? "Unpaid" : reader.GetString(20)),
                        paymentMethod = reader.IsDBNull(21) ? "" : reader.GetString(21),
                        serviceFeeStatus = NormalizeServiceFeeStatus(reader.IsDBNull(22) ? "Unpaid" : reader.GetString(22), reader.IsDBNull(20) ? "Unpaid" : reader.GetString(20)),
                        talentFeeStatus = NormalizeTalentFeeStatus(reader.IsDBNull(23) ? "Unpaid" : reader.GetString(23)),
                        talentPlatformFeeStatus = NormalizePaymentStatus(reader.IsDBNull(24) ? "Unpaid" : reader.GetString(24)),
                        serviceFeePaymentMethod = reader.IsDBNull(25) ? "" : reader.GetString(25),
                        talentFeePaymentMethod = reader.IsDBNull(26) ? "" : reader.GetString(26),
                        talentPlatformFeePaymentMethod = reader.IsDBNull(27) ? "" : reader.GetString(27),
                        customerCompletedAt = reader.IsDBNull(28) ? (DateTime?)null : reader.GetDateTime(28),
                        targetCompletedAt = reader.IsDBNull(29) ? (DateTime?)null : reader.GetDateTime(29),
                        hasMessages = !reader.IsDBNull(30) && reader.GetBoolean(30)
                    });
                }

                return Ok(bookings);
            }
            catch (Exception ex)
            {
                return StatusCode(500, new { message = "Failed to fetch customer bookings: " + ex.Message });
            }
        }

        [HttpGet("organizer/{userId}")]
        public async Task<IActionResult> GetBookingsForOrganizer(Guid userId)
        {
            try
            {
                var actorUserId = GetActorUserId(User);
                var actorRole = GetActorRole(User);
                if (!actorUserId.HasValue)
                {
                    return Unauthorized(new { message = "Sign in again before viewing bookings." });
                }

                if (!CanAccessUserScopedBookingRoute(actorUserId.Value, actorRole, userId))
                {
                    return Forbid();
                }

                var bookings = new List<object>();
                await using var connection = new NpgsqlConnection(_connectionString);
                await connection.OpenAsync();
                await EnsureBookingsTableExists(connection);
                await EnsureBookingMessagesTableExists(connection);

                const string sql = @"
                    SELECT
                        b.id,
                        b.event_id,
                        COALESCE(b.event_title, COALESCE(e.title, 'Booking Request')),
                        b.event_date,
                        COALESCE(b.location, COALESCE(e.location, '')),
                        COALESCE(b.service_type, ''),
                        COALESCE(b.status, 'Pending'),
                        COALESCE(b.payment_status, 'Unpaid'),
                        COALESCE(b.service_fee_status, COALESCE(b.payment_status, 'Unpaid')),
                        COALESCE(b.talent_fee_status, 'Unpaid'),
                        COALESCE(b.booking_fee, 0),
                        COALESCE(b.budget, 0),
                        b.created_at,
                        COALESCE(b.target_role, ''),
                        COALESCE(t.stagename, ''),
                        COALESCE(t.firstname, ''),
                        COALESCE(t.lastname, ''),
                        COALESCE(c.firstname, ''),
                        COALESCE(c.lastname, ''),
                        EXISTS (
                            SELECT 1
                            FROM booking_messages m
                            WHERE m.booking_id = b.id
                        ) AS has_messages
                    FROM bookings b
                    LEFT JOIN events e ON e.id = b.event_id
                    LEFT JOIN users t ON t.id = b.target_user_id
                    LEFT JOIN users c ON c.id = b.customer_id
                    WHERE e.organizer_id = @userId
                       OR (b.customer_id = @userId AND COALESCE(b.target_role, '') IN ('Artist', 'Sessionist'))
                    ORDER BY b.created_at DESC;";

                await using var cmd = new NpgsqlCommand(sql, connection);
                cmd.Parameters.Add("@userId", NpgsqlDbType.Uuid).Value = userId;

                await using var reader = await cmd.ExecuteReaderAsync();
                while (await reader.ReadAsync())
                {
                    var targetStageName = reader.IsDBNull(14) ? string.Empty : reader.GetString(14);
                    var targetFullName = $"{(reader.IsDBNull(15) ? string.Empty : reader.GetString(15))} {(reader.IsDBNull(16) ? string.Empty : reader.GetString(16))}".Trim();
                    var requesterName = $"{(reader.IsDBNull(17) ? string.Empty : reader.GetString(17))} {(reader.IsDBNull(18) ? string.Empty : reader.GetString(18))}".Trim();

                    bookings.Add(new
                    {
                        id = reader.GetGuid(0),
                        eventId = reader.IsDBNull(1) ? (Guid?)null : reader.GetGuid(1),
                        eventTitle = reader.IsDBNull(2) ? "Booking Request" : reader.GetString(2),
                        eventDate = reader.IsDBNull(3) ? (DateTime?)null : reader.GetDateTime(3),
                        location = reader.IsDBNull(4) ? string.Empty : reader.GetString(4),
                        serviceType = reader.IsDBNull(5) ? string.Empty : reader.GetString(5),
                        status = reader.IsDBNull(6) ? "Pending" : reader.GetString(6),
                        paymentStatus = NormalizePaymentStatus(reader.IsDBNull(7) ? "Unpaid" : reader.GetString(7)),
                        serviceFeeStatus = NormalizeServiceFeeStatus(reader.IsDBNull(8) ? "Unpaid" : reader.GetString(8), reader.IsDBNull(7) ? "Unpaid" : reader.GetString(7)),
                        talentFeeStatus = NormalizeTalentFeeStatus(reader.IsDBNull(9) ? "Unpaid" : reader.GetString(9)),
                        bookingFee = reader.IsDBNull(10) ? 0m : reader.GetDecimal(10),
                        budget = reader.IsDBNull(11) ? 0m : reader.GetDecimal(11),
                        createdAt = reader.IsDBNull(12) ? DateTime.UtcNow : reader.GetDateTime(12),
                        targetRole = reader.IsDBNull(13) ? string.Empty : reader.GetString(13),
                        targetName = !string.IsNullOrWhiteSpace(targetStageName) ? targetStageName : (string.IsNullOrWhiteSpace(targetFullName) ? "Unknown Talent" : targetFullName),
                        requesterName = string.IsNullOrWhiteSpace(requesterName) ? "Unknown Requester" : requesterName,
                        hasMessages = !reader.IsDBNull(19) && reader.GetBoolean(19)
                    });
                }

                return Ok(bookings);
            }
            catch (Exception ex)
            {
                return StatusCode(500, new { message = "Failed to fetch organizer bookings: " + ex.Message });
            }
        }

        [HttpPatch("{bookingId}/status")]
        public async Task<IActionResult> UpdateBookingStatus(Guid bookingId, [FromBody] UpdateBookingStatusRequest req)
        {
            try
            {
                var actorUserId = GetActorUserId(User);
                var actorRole = GetActorRole(User);
                if (!actorUserId.HasValue)
                {
                    return Unauthorized(new { message = "Sign in again before updating this booking." });
                }

                if (string.IsNullOrWhiteSpace(req.status))
                {
                    return BadRequest(new { message = "Booking status is required." });
                }

                await using var connection = new NpgsqlConnection(_connectionString);
                await connection.OpenAsync();
                await EnsureBookingsTableExists(connection);
                await EnsureNotificationsTableExists(connection);
                await PaymentLedgerService.EnsureSchemaAsync(connection);
                await PaymentRefundService.EnsureSchemaAsync(connection);
                await EscrowService.EnsureSchemaAsync(connection);
                await EnsureEventBookingSupportExists(connection);
                var bookingContext = await GetBookingParticipantContextAsync(connection, bookingId);
                if (bookingContext is null)
                {
                    return NotFound(new { message = "Booking request not found." });
                }

                if (!CanAccessBooking(actorUserId.Value, actorRole, bookingContext))
                {
                    return Forbid();
                }

                string targetRole;
                string paymentStatus;
                string serviceFeeStatus;
                string currentStatus;
                string talentFeeStatus;
                string talentPlatformFeeStatus;
                decimal talentPlatformFee;
                decimal budget;
                DateTime? customerCompletedAt;
                DateTime? targetCompletedAt;
                Guid customerId;
                Guid targetUserId;
                Guid? eventId;
                string eventTitle;
                DateTime? eventDate;
                DateTime? eventEndTime;
                string location;

                const string loadSql = @"
                    SELECT COALESCE(target_role, 'Artist'),
                           COALESCE(payment_status, 'Unpaid'),
                           COALESCE(service_fee_status, COALESCE(payment_status, 'Unpaid')),
                           COALESCE(status, 'Pending'),
                           COALESCE(talent_fee_status, 'Unpaid'),
                           COALESCE(talent_platform_fee_status, 'Unpaid'),
                           COALESCE(talent_platform_fee, 0),
                           COALESCE(budget, 0),
                           customer_completed_at,
                           target_completed_at,
                           customer_id,
                           target_user_id,
                           event_id,
                           COALESCE(event_title, 'Booking Request'),
                           event_date,
                           event_end_time,
                           COALESCE(location, '')
                    FROM bookings
                    WHERE id = @id";
                using (var loadCmd = new NpgsqlCommand(loadSql, connection))
                {
                    loadCmd.Parameters.Add("@id", NpgsqlDbType.Uuid).Value = bookingId;
                    using var reader = await loadCmd.ExecuteReaderAsync();
                    if (!await reader.ReadAsync())
                    {
                        return NotFound(new { message = "Booking request not found." });
                    }

                    targetRole = reader.IsDBNull(0) ? "Artist" : NormalizeTargetRole(reader.GetString(0));
                    paymentStatus = NormalizePaymentStatus(reader.IsDBNull(1) ? "Unpaid" : reader.GetString(1));
                    serviceFeeStatus = NormalizeServiceFeeStatus(reader.IsDBNull(2) ? paymentStatus : reader.GetString(2), paymentStatus);
                    currentStatus = reader.IsDBNull(3) ? "Pending" : reader.GetString(3);
                    talentFeeStatus = NormalizeTalentFeeStatus(reader.IsDBNull(4) ? "Unpaid" : reader.GetString(4));
                    talentPlatformFeeStatus = NormalizePaymentStatus(reader.IsDBNull(5) ? "Unpaid" : reader.GetString(5));
                    talentPlatformFee = reader.IsDBNull(6) ? 0 : reader.GetDecimal(6);
                    budget = reader.IsDBNull(7) ? 0 : reader.GetDecimal(7);
                    customerCompletedAt = reader.IsDBNull(8) ? (DateTime?)null : reader.GetDateTime(8);
                    targetCompletedAt = reader.IsDBNull(9) ? (DateTime?)null : reader.GetDateTime(9);
                    customerId = reader.IsDBNull(10) ? Guid.Empty : reader.GetGuid(10);
                    targetUserId = reader.IsDBNull(11) ? Guid.Empty : reader.GetGuid(11);
                    eventId = reader.IsDBNull(12) ? (Guid?)null : reader.GetGuid(12);
                    eventTitle = reader.IsDBNull(13) ? "Booking Request" : reader.GetString(13);
                    eventDate = reader.IsDBNull(14) ? (DateTime?)null : reader.GetDateTime(14);
                    eventEndTime = reader.IsDBNull(15) ? (DateTime?)null : reader.GetDateTime(15);
                    location = reader.IsDBNull(16) ? "" : reader.GetString(16);
                }

                var normalizedStatus = req.status.Trim();
                if (normalizedStatus.Equals("Accepted", StringComparison.OrdinalIgnoreCase))
                {
                    normalizedStatus = "Confirmed";
                }
                else if (normalizedStatus.Equals("Declined", StringComparison.OrdinalIgnoreCase))
                {
                    normalizedStatus = $"Cancelled by {targetRole}";
                }
                else if (normalizedStatus.Equals("CancelledByCustomer", StringComparison.OrdinalIgnoreCase) ||
                         normalizedStatus.Equals("Cancelled by Customer", StringComparison.OrdinalIgnoreCase))
                {
                    normalizedStatus = "Cancelled by Customer";
                }

                if (currentStatus.Equals("Completed", StringComparison.OrdinalIgnoreCase))
                {
                    return BadRequest(new { message = "This booking has already been completed and settled." });
                }

                if (currentStatus.StartsWith("Cancelled", StringComparison.OrdinalIgnoreCase))
                {
                    return BadRequest(new { message = "This booking has already been cancelled." });
                }

                var bookingWindowEnded = (eventEndTime ?? eventDate)?.ToUniversalTime() <= DateTime.UtcNow;
                if (normalizedStatus.StartsWith("Cancelled by ", StringComparison.OrdinalIgnoreCase) &&
                    (bookingWindowEnded == true || IsCompletionPendingStatus(currentStatus)))
                {
                    return BadRequest(new { message = "This booking can no longer be cancelled after the booked schedule has passed. Use completion confirmation or the no-show refund flow instead." });
                }

                if (normalizedStatus.Equals("Confirmed", StringComparison.OrdinalIgnoreCase) &&
                    serviceFeeStatus != "Paid" &&
                    serviceFeeStatus != "NotRequired")
                {
                    return BadRequest(new { message = "The booking fee must be paid before this request can be confirmed." });
                }

                if (normalizedStatus.Equals("Completed", StringComparison.OrdinalIgnoreCase))
                {
                    if (!currentStatus.Equals("Confirmed", StringComparison.OrdinalIgnoreCase) &&
                        !IsCompletionPendingStatus(currentStatus))
                    {
                        return BadRequest(new { message = "Only confirmed bookings or bookings already awaiting the other side's confirmation can be completed." });
                    }

                    if (!eventDate.HasValue)
                    {
                        return BadRequest(new { message = "This booking needs an event date before it can be completed." });
                    }

                    var completionGate = (eventEndTime ?? eventDate.Value).ToUniversalTime();
                    if (completionGate > DateTime.UtcNow)
                    {
                        return BadRequest(new { message = "This booking can only be completed after the booked time range has passed." });
                    }
                }

                if (normalizedStatus.Equals("Confirmed", StringComparison.OrdinalIgnoreCase))
                {
                    if (await PlatformFeatureSupport.UserHasCalendarBlockAsync(connection, targetUserId, targetRole, eventDate))
                    {
                        return Conflict(new { message = $"{targetRole} blocked this date from bookings." });
                    }

                    if (await PlatformFeatureSupport.UserHasBookingConflictAsync(connection, targetUserId, eventDate, eventEndTime, bookingId))
                    {
                        return Conflict(new { message = $"{targetRole} is already booked in this time slot." });
                    }
                }

                var isCustomerActor = actorUserId.Value == customerId;
                var isTargetActor = actorUserId.Value == targetUserId;
                var isAdminActor = actorRole.Equals("Admin", StringComparison.OrdinalIgnoreCase);
                var completionFinalized = false;
                var completionAwaitingMessage = string.Empty;
                var nextPaymentStatus = paymentStatus;
                if (normalizedStatus.Equals("Confirmed", StringComparison.OrdinalIgnoreCase))
                {
                    nextPaymentStatus = serviceFeeStatus == "NotRequired"
                        ? "NotRequired"
                        : budget > 0 ? "AwaitingTalentFeePayment" : "Paid";
                }
                else if (normalizedStatus.StartsWith("Cancelled by ", StringComparison.OrdinalIgnoreCase) && serviceFeeStatus == "Paid")
                {
                    nextPaymentStatus = "Refund Pending";
                }
                else if (normalizedStatus.Equals("Completed", StringComparison.OrdinalIgnoreCase))
                {
                    if (!isCustomerActor && !isTargetActor && !isAdminActor)
                    {
                        return BadRequest(new { message = "Only the booking participants can confirm completion." });
                    }

                    var customerCompletionRecorded = customerCompletedAt.HasValue || isCustomerActor || isAdminActor;
                    var targetCompletionRecorded = targetCompletedAt.HasValue || isTargetActor || isAdminActor;
                    completionFinalized = customerCompletionRecorded && targetCompletionRecorded;

                    if (completionFinalized)
                    {
                        nextPaymentStatus = budget > 0 &&
                            (talentFeeStatus.Equals("HeldInEscrow", StringComparison.OrdinalIgnoreCase) ||
                             talentFeeStatus.Equals("ReadyForRelease", StringComparison.OrdinalIgnoreCase))
                            ? (talentPlatformFeeStatus.Equals("Paid", StringComparison.OrdinalIgnoreCase) ? "Paid" : "TalentReleasePendingPlatformFee")
                            : budget > 0 && !talentFeeStatus.Equals("Released", StringComparison.OrdinalIgnoreCase)
                                ? paymentStatus
                                : serviceFeeStatus == "NotRequired" ? "NotRequired" : "Paid";
                    }
                    else
                    {
                        normalizedStatus = isCustomerActor
                            ? CompletionPendingTalentConfirmation
                            : CompletionPendingCustomerConfirmation;
                        nextPaymentStatus = paymentStatus;
                        completionAwaitingMessage = isCustomerActor
                            ? $"{targetRole} must also confirm completion before escrow is released."
                            : "The customer must also confirm completion before escrow is released.";
                    }
                }

                const string sql = @"
                    UPDATE bookings
                    SET status = @status,
                        payment_status = @paymentStatus,
                        customer_completed_at = CASE
                            WHEN @resetCompletion = TRUE THEN NULL
                            WHEN @markCustomerCompleted = TRUE THEN COALESCE(customer_completed_at, NOW())
                            ELSE customer_completed_at
                        END,
                        target_completed_at = CASE
                            WHEN @resetCompletion = TRUE THEN NULL
                            WHEN @markTargetCompleted = TRUE THEN COALESCE(target_completed_at, NOW())
                            ELSE target_completed_at
                        END,
                        talent_fee_status = CASE
                            WHEN @completed = TRUE AND COALESCE(dispute_hold, FALSE) = FALSE AND COALESCE(talent_fee_status, 'Unpaid') IN ('HeldInEscrow', 'ReadyForRelease') AND COALESCE(talent_platform_fee_status, 'Unpaid') = 'Paid' THEN 'Released'
                            WHEN @completed = TRUE AND COALESCE(talent_fee_status, 'Unpaid') = 'HeldInEscrow' THEN 'HeldInEscrow'
                            ELSE talent_fee_status
                        END,
                        talent_platform_fee_status = CASE
                            WHEN @confirmed = TRUE AND talent_platform_fee > 0 AND COALESCE(talent_platform_fee_status, 'Unpaid') = 'Unpaid' THEN 'AwaitingPayment'
                            ELSE talent_platform_fee_status
                        END
                    WHERE id = @id";
                using var cmd = new NpgsqlCommand(sql, connection);
                cmd.Parameters.Add("@status", NpgsqlDbType.Text).Value = normalizedStatus;
                cmd.Parameters.Add("@paymentStatus", NpgsqlDbType.Text).Value = nextPaymentStatus;
                cmd.Parameters.Add("@confirmed", NpgsqlDbType.Boolean).Value = normalizedStatus.Equals("Confirmed", StringComparison.OrdinalIgnoreCase);
                cmd.Parameters.Add("@completed", NpgsqlDbType.Boolean).Value = completionFinalized;
                cmd.Parameters.Add("@markCustomerCompleted", NpgsqlDbType.Boolean).Value = normalizedStatus.Equals("Completed", StringComparison.OrdinalIgnoreCase) ? true : isCustomerActor && req.status.Trim().Equals("Completed", StringComparison.OrdinalIgnoreCase);
                cmd.Parameters.Add("@markTargetCompleted", NpgsqlDbType.Boolean).Value = normalizedStatus.Equals("Completed", StringComparison.OrdinalIgnoreCase) ? true : isTargetActor && req.status.Trim().Equals("Completed", StringComparison.OrdinalIgnoreCase);
                cmd.Parameters.Add("@resetCompletion", NpgsqlDbType.Boolean).Value =
                    normalizedStatus.Equals("Confirmed", StringComparison.OrdinalIgnoreCase) ||
                    normalizedStatus.StartsWith("Cancelled by ", StringComparison.OrdinalIgnoreCase);
                cmd.Parameters.Add("@id", NpgsqlDbType.Uuid).Value = bookingId;

                var rows = await cmd.ExecuteNonQueryAsync();
                if (rows == 0)
                {
                    return NotFound(new { message = "Booking request not found." });
                }

                if (completionFinalized && budget > 0)
                {
                    await EscrowService.MarkReleaseReadyAsync(connection, bookingId);
                    if (talentPlatformFeeStatus.Equals("Paid", StringComparison.OrdinalIgnoreCase))
                    {
                        await EscrowService.ReleaseAsync(connection, bookingId);
                    }
                }

                if (normalizedStatus.Equals("Confirmed", StringComparison.OrdinalIgnoreCase) && eventId.HasValue && targetUserId != Guid.Empty)
                {
                    await SyncBookingIntoEventLineup(connection, bookingId, eventId.Value, targetUserId, targetRole);
                }

                if (normalizedStatus.Equals("Confirmed", StringComparison.OrdinalIgnoreCase))
                {
                    await PlatformFeatureSupport.EnsureBookingContractAsync(connection, bookingId, eventTitle, budget, eventDate, location, targetRole);
                }

                if (normalizedStatus.Equals($"Cancelled by {targetRole}", StringComparison.OrdinalIgnoreCase))
                {
                    await ProcessAutomaticBookingRefundsAsync(
                        connection,
                        bookingId,
                        reasonCode: "TalentCancelled",
                        notes: $"{targetRole} cancelled the booking before fulfillment.",
                        requesterUserId: targetUserId,
                        requesterRole: targetRole,
                        refundServiceFee: true,
                        refundTalentFee: true,
                        refundPlatformFee: false);
                }
                else if (normalizedStatus.Equals("Cancelled by Customer", StringComparison.OrdinalIgnoreCase))
                {
                    await ProcessAutomaticBookingRefundsAsync(
                        connection,
                        bookingId,
                        reasonCode: "CustomerCancelled",
                        notes: "Customer cancelled the booking.",
                        requesterUserId: customerId,
                        requesterRole: "Customer",
                        refundServiceFee: false,
                        refundTalentFee: false,
                        refundPlatformFee: true);
                }

                if (customerId != Guid.Empty)
                {
                    var customerMessage = normalizedStatus.Equals("Confirmed", StringComparison.OrdinalIgnoreCase)
                        ? $"{targetRole} confirmed your booking for {eventTitle}."
                        : normalizedStatus.Equals("Completed", StringComparison.OrdinalIgnoreCase)
                            ? $"Your booking for {eventTitle} has been marked as completed."
                        : IsCompletionPendingStatus(normalizedStatus)
                            ? $"Completion was confirmed for {eventTitle}, but the other side still needs to confirm before payout is released."
                        : normalizedStatus.Equals("Cancelled by Customer", StringComparison.OrdinalIgnoreCase)
                            ? $"You cancelled your booking for {eventTitle}."
                        : normalizedStatus.StartsWith("Cancelled by ", StringComparison.OrdinalIgnoreCase)
                            ? $"{targetRole} cancelled your booking for {eventTitle}."
                            : $"Your booking for {eventTitle} is now {normalizedStatus}.";

                    await InsertNotification(connection, customerId, "booking_status", "Booking updated", customerMessage, bookingId, "booking");
                }

                if (targetUserId != Guid.Empty && targetUserId != customerId && normalizedStatus.Equals("Completed", StringComparison.OrdinalIgnoreCase))
                {
                    await InsertNotification(connection, targetUserId, "booking_status", "Booking completed", $"The booking for {eventTitle} has been marked as completed.", bookingId, "booking");
                }
                else if (targetUserId != Guid.Empty && targetUserId != customerId && normalizedStatus.Equals(CompletionPendingTalentConfirmation, StringComparison.OrdinalIgnoreCase))
                {
                    await InsertNotification(connection, targetUserId, "booking_status", "Completion awaiting your confirmation", $"The customer confirmed completion for {eventTitle}. Confirm the booking if the performance was fulfilled, or use the refund flow if it was not.", bookingId, "booking");
                }
                else if (customerId != Guid.Empty && normalizedStatus.Equals(CompletionPendingCustomerConfirmation, StringComparison.OrdinalIgnoreCase))
                {
                    await InsertNotification(connection, customerId, "booking_status", "Completion awaiting your confirmation", $"{targetRole} confirmed completion for {eventTitle}. Confirm the booking if the performance was fulfilled, or request a refund review if there was a no-show.", bookingId, "booking");
                }
                else if (targetUserId != Guid.Empty && targetUserId != customerId && normalizedStatus.Equals("Cancelled by Customer", StringComparison.OrdinalIgnoreCase))
                {
                    await InsertNotification(connection, targetUserId, "booking_status", "Booking cancelled", $"The customer cancelled the booking for {eventTitle}.", bookingId, "booking");
                }

                // Fire-and-forget emails for key status transitions
                _ = Task.Run(async () =>
                {
                    try
                    {
                        await using var emailConn = new NpgsqlConnection(_connectionString);
                        await emailConn.OpenAsync();
                        var (tFirst, tLast) = await GetUserNameAsync(emailConn, targetUserId);
                        var targetDisplayName = $"{tFirst} {tLast}".Trim();
                        if (string.IsNullOrWhiteSpace(targetDisplayName)) targetDisplayName = targetRole;

                        if (normalizedStatus.Equals("Confirmed", StringComparison.OrdinalIgnoreCase))
                        {
                            await _emailService.SendBookingConfirmedEmailAsync(
                                emailConn, customerId, targetDisplayName, targetRole,
                                eventTitle, eventDate, location, budget);
                        }
                        else if (completionFinalized)
                        {
                            await _emailService.SendBookingCompletedEmailAsync(
                                emailConn, customerId, targetUserId, targetDisplayName, eventTitle);
                        }
                        else if (normalizedStatus.Equals($"Cancelled by {targetRole}", StringComparison.OrdinalIgnoreCase))
                        {
                            var (cFirst, _) = await GetUserNameAsync(emailConn, customerId);
                            await _emailService.SendBookingCancelledEmailAsync(
                                emailConn, customerId, targetDisplayName, eventTitle, isRefundIssued: true);
                        }
                        else if (normalizedStatus.Equals("Cancelled by Customer", StringComparison.OrdinalIgnoreCase))
                        {
                            var (cFirst, _) = await GetUserNameAsync(emailConn, customerId);
                            var custName = string.IsNullOrWhiteSpace(cFirst) ? "The customer" : cFirst;
                            await _emailService.SendBookingCancelledEmailAsync(
                                emailConn, targetUserId, custName, eventTitle, isRefundIssued: false);
                        }
                    }
                    catch { /* email errors never break main flow */ }
                });

                var responseMessage = normalizedStatus.Equals("Completed", StringComparison.OrdinalIgnoreCase)
                    ? "Booking completed and settlement finalized."
                    : IsCompletionPendingStatus(normalizedStatus)
                        ? $"Your completion confirmation was recorded. {completionAwaitingMessage}"
                        : "Booking request updated.";

                return Ok(new { message = responseMessage, status = normalizedStatus, paymentStatus = nextPaymentStatus });
            }
            catch (Exception ex)
            {
                return StatusCode(500, new { message = "Failed to update booking request: " + ex.Message });
            }
        }

        [HttpGet("{bookingId}")]
        public async Task<IActionResult> GetBookingById(Guid bookingId)
        {
            try
            {
                await using var connection = new NpgsqlConnection(_connectionString);
                await connection.OpenAsync();
                await EnsureBookingsTableExists(connection);

                const string sql = @"
                    SELECT
                        b.id,
                        b.customer_id,
                        b.target_user_id,
                        b.event_id,
                        COALESCE(c.firstname, '') AS customer_firstname,
                        COALESCE(c.lastname, '') AS customer_lastname,
                        COALESCE(t.firstname, '') AS target_firstname,
                        COALESCE(t.lastname, '') AS target_lastname,
                        COALESCE(t.stagename, '') AS target_stage_name,
                        COALESCE(b.target_role, ''),
                        COALESCE(b.service_type, ''),
                        COALESCE(b.event_title, ''),
                        b.event_date,
                        b.event_end_time,
                        COALESCE(b.location, ''),
                        COALESCE(b.budget, 0),
                        COALESCE(b.booking_fee, 15),
                        COALESCE(b.talent_platform_fee, 0),
                        COALESCE(b.notes, COALESCE(b.message, '')),
                        COALESCE(b.message, ''),
                        COALESCE(b.status, 'Pending'),
                        COALESCE(b.payment_status, 'Unpaid'),
                        b.paid_at,
                        COALESCE(b.payment_method, ''),
                        COALESCE(b.service_fee_status, COALESCE(b.payment_status, 'Unpaid')),
                        COALESCE(b.talent_fee_status, 'Unpaid'),
                        COALESCE(b.talent_platform_fee_status, 'Unpaid'),
                        b.service_fee_paid_at,
                        b.talent_fee_paid_at,
                        b.talent_platform_fee_paid_at,
                        COALESCE(b.service_fee_payment_method, COALESCE(b.payment_method, '')),
                        COALESCE(b.talent_fee_payment_method, ''),
                        COALESCE(b.talent_platform_fee_payment_method, ''),
                        COALESCE(b.talent_fee_payment_reference, ''),
                        COALESCE(b.talent_platform_fee_payment_reference, ''),
                        COALESCE(b.talent_fee_checkout_reference, ''),
                        COALESCE(b.talent_platform_fee_checkout_reference, ''),
                        COALESCE(b.paymongo_payment_reference, ''),
                        COALESCE(b.paymongo_checkout_reference, ''),
                        COALESCE(b.paymongo_payment_id, ''),
                        COALESCE(b.talent_fee_payment_id, ''),
                        COALESCE(b.talent_platform_fee_payment_id, ''),
                        b.customer_completed_at,
                        b.target_completed_at,
                        b.booking_group_id,
                        COALESCE(b.booking_sequence, 1),
                        b.updated_at,
                        b.conversation_closed_at,
                        b.conversation_closed_by_user_id,
                        COALESCE(b.conversation_close_reason, '')
                    FROM bookings b
                    LEFT JOIN users c ON c.id = b.customer_id
                    LEFT JOIN users t ON t.id = b.target_user_id
                    WHERE b.id = @bookingId;";

                using var cmd = new NpgsqlCommand(sql, connection);
                cmd.Parameters.Add("@bookingId", NpgsqlDbType.Uuid).Value = bookingId;

                using var reader = await cmd.ExecuteReaderAsync();
                if (!await reader.ReadAsync())
                {
                    return NotFound(new { message = "Booking request not found." });
                }

                var customerFirst = reader.IsDBNull(4) ? "" : reader.GetString(4);
                var customerLast = reader.IsDBNull(5) ? "" : reader.GetString(5);
                var targetFirst = reader.IsDBNull(6) ? "" : reader.GetString(6);
                var targetLast = reader.IsDBNull(7) ? "" : reader.GetString(7);
                var targetStage = reader.IsDBNull(8) ? "" : reader.GetString(8);

                return Ok(new
                {
                    id = reader.GetGuid(0),
                    customerId = reader.GetGuid(1),
                    targetUserId = reader.GetGuid(2),
                    eventId = reader.IsDBNull(3) ? (Guid?)null : reader.GetGuid(3),
                    customerName = $"{customerFirst} {customerLast}".Trim(),
                    targetName = !string.IsNullOrWhiteSpace(targetStage) ? targetStage : $"{targetFirst} {targetLast}".Trim(),
                    targetRole = reader.IsDBNull(9) ? "" : reader.GetString(9),
                    serviceType = reader.IsDBNull(10) ? "" : reader.GetString(10),
                    eventTitle = reader.IsDBNull(11) ? "" : reader.GetString(11),
                    eventDate = reader.IsDBNull(12) ? (DateTime?)null : reader.GetDateTime(12),
                    eventEndTime = reader.IsDBNull(13) ? (DateTime?)null : reader.GetDateTime(13),
                    location = reader.IsDBNull(14) ? "" : reader.GetString(14),
                    budget = reader.IsDBNull(15) ? 0 : reader.GetDecimal(15),
                    bookingFee = reader.IsDBNull(16) ? BookingServiceFee : reader.GetDecimal(16),
                    talentPlatformFee = reader.IsDBNull(17) ? 0 : reader.GetDecimal(17),
                    notes = _messageProtection.Unprotect(reader.IsDBNull(18) ? "" : reader.GetString(18)),
                    message = _messageProtection.Unprotect(reader.IsDBNull(19) ? "" : reader.GetString(19)),
                    status = reader.IsDBNull(20) ? "Pending" : reader.GetString(20),
                    paymentStatus = NormalizePaymentStatus(reader.IsDBNull(21) ? "Unpaid" : reader.GetString(21)),
                    paidAt = reader.IsDBNull(22) ? (DateTime?)null : reader.GetDateTime(22),
                    paymentMethod = reader.IsDBNull(23) ? "" : reader.GetString(23),
                    serviceFeeStatus = NormalizeServiceFeeStatus(reader.IsDBNull(24) ? "Unpaid" : reader.GetString(24), reader.IsDBNull(21) ? "Unpaid" : reader.GetString(21)),
                    talentFeeStatus = NormalizeTalentFeeStatus(reader.IsDBNull(25) ? "Unpaid" : reader.GetString(25)),
                    talentPlatformFeeStatus = NormalizePaymentStatus(reader.IsDBNull(26) ? "Unpaid" : reader.GetString(26)),
                    serviceFeePaidAt = reader.IsDBNull(27) ? (DateTime?)null : reader.GetDateTime(27),
                    talentFeePaidAt = reader.IsDBNull(28) ? (DateTime?)null : reader.GetDateTime(28),
                    talentPlatformFeePaidAt = reader.IsDBNull(29) ? (DateTime?)null : reader.GetDateTime(29),
                    serviceFeePaymentMethod = reader.IsDBNull(30) ? "" : reader.GetString(30),
                    talentFeePaymentMethod = reader.IsDBNull(31) ? "" : reader.GetString(31),
                    talentPlatformFeePaymentMethod = reader.IsDBNull(32) ? "" : reader.GetString(32),
                    talentFeePaymentReference = reader.IsDBNull(33) ? "" : reader.GetString(33),
                    talentPlatformFeePaymentReference = reader.IsDBNull(34) ? "" : reader.GetString(34),
                    talentFeeCheckoutReference = reader.IsDBNull(35) ? "" : reader.GetString(35),
                    talentPlatformFeeCheckoutReference = reader.IsDBNull(36) ? "" : reader.GetString(36),
                    paymentReference = reader.IsDBNull(37) ? "" : reader.GetString(37),
                    checkoutReference = reader.IsDBNull(38) ? "" : reader.GetString(38),
                    paymentId = reader.IsDBNull(39) ? "" : reader.GetString(39),
                    talentFeePaymentId = reader.IsDBNull(40) ? "" : reader.GetString(40),
                    talentPlatformFeePaymentId = reader.IsDBNull(41) ? "" : reader.GetString(41),
                    customerCompletedAt = reader.IsDBNull(42) ? (DateTime?)null : reader.GetDateTime(42),
                    targetCompletedAt = reader.IsDBNull(43) ? (DateTime?)null : reader.GetDateTime(43),
                    bookingGroupId = reader.IsDBNull(44) ? (Guid?)null : reader.GetGuid(44),
                    bookingSequence = reader.IsDBNull(45) ? 1 : reader.GetInt32(45),
                    updatedAt = reader.IsDBNull(46) ? (DateTime?)null : reader.GetDateTime(46),
                    conversationClosedAt = reader.IsDBNull(47) ? (DateTime?)null : reader.GetDateTime(47),
                    conversationClosedByUserId = reader.IsDBNull(48) ? (Guid?)null : reader.GetGuid(48),
                    conversationCloseReason = reader.IsDBNull(49) ? "" : reader.GetString(49),
                    conversationClosed = !reader.IsDBNull(47)
                });
            }
            catch (Exception ex)
            {
                return StatusCode(500, new { message = "Failed to load booking details: " + ex.Message });
            }
        }

        [HttpPost("{bookingId}/conversation/close")]
        public async Task<IActionResult> CloseBookingConversation(Guid bookingId, [FromBody] CloseBookingConversationRequest? req)
        {
            try
            {
                var actorUserId = GetActorUserId(User);
                var actorRole = GetActorRole(User);
                if (!actorUserId.HasValue)
                {
                    return Unauthorized(new { message = "Sign in again before closing this conversation." });
                }

                await using var connection = new NpgsqlConnection(_connectionString);
                await connection.OpenAsync();
                await EnsureBookingsTableExists(connection);
                await EnsureNotificationsTableExists(connection);

                var context = await GetBookingParticipantContextAsync(connection, bookingId);
                if (context is null) return NotFound(new { message = "Booking not found." });
                if (!CanAccessBooking(actorUserId.Value, actorRole, context)) return Forbid();

                const string updateSql = @"
                    UPDATE bookings
                    SET conversation_closed_at = COALESCE(conversation_closed_at, NOW()),
                        conversation_closed_by_user_id = COALESCE(conversation_closed_by_user_id, @actorUserId),
                        conversation_close_reason = COALESCE(NULLIF(@reason, ''), conversation_close_reason, 'Closed by participant'),
                        updated_at = NOW()
                    WHERE id = @bookingId
                    RETURNING customer_id, target_user_id, COALESCE(event_title, 'Booking');";

                Guid customerId;
                Guid targetUserId;
                string eventTitle;
                await using (var cmd = new NpgsqlCommand(updateSql, connection))
                {
                    cmd.Parameters.Add("@bookingId", NpgsqlDbType.Uuid).Value = bookingId;
                    cmd.Parameters.Add("@actorUserId", NpgsqlDbType.Uuid).Value = actorUserId.Value;
                    cmd.Parameters.Add("@reason", NpgsqlDbType.Text).Value = (req?.reason ?? "").Trim();
                    await using var reader = await cmd.ExecuteReaderAsync();
                    if (!await reader.ReadAsync()) return NotFound(new { message = "Booking not found." });
                    customerId = reader.GetGuid(0);
                    targetUserId = reader.GetGuid(1);
                    eventTitle = reader.GetString(2);
                }

                var notifyUser = actorUserId.Value == customerId ? targetUserId : customerId;
                await InsertNotification(
                    connection,
                    notifyUser,
                    "conversation_closed",
                    "Conversation closed",
                    $"The booking conversation for {eventTitle} was closed.",
                    bookingId,
                    "booking");

                return Ok(new { message = "Conversation closed.", conversationClosed = true, conversationClosedAt = DateTime.UtcNow });
            }
            catch (Exception ex)
            {
                return StatusCode(500, new { message = "Failed to close conversation: " + ex.Message });
            }
        }

        [HttpGet("{bookingId}/contract")]
        public async Task<IActionResult> GetBookingContract(Guid bookingId)
        {
            try
            {
                var actorUserId = GetActorUserId(User);
                var actorRole = GetActorRole(User);
                if (!actorUserId.HasValue)
                {
                    return Unauthorized(new { message = "Sign in again before opening this contract." });
                }

                await using var connection = new NpgsqlConnection(_connectionString);
                await connection.OpenAsync();
                await PlatformFeatureSupport.EnsureSharedBusinessSchemaAsync(connection);
                var bookingContext = await GetBookingParticipantContextAsync(connection, bookingId);
                if (bookingContext is null)
                {
                    return NotFound(new { message = "Booking not found." });
                }

                if (!CanAccessBooking(actorUserId.Value, actorRole, bookingContext))
                {
                    return Forbid();
                }

                const string sql = @"
                    SELECT id,
                           booking_id,
                           contract_status,
                           title,
                           terms,
                           COALESCE(agreed_fee, 0),
                           event_date,
                           COALESCE(location, ''),
                           proposed_by_user_id,
                           COALESCE(proposed_by_role, ''),
                           accepted_by_user_id,
                           COALESCE(accepted_by_role, ''),
                           accepted_at,
                           COALESCE(revision_number, 1),
                           COALESCE(last_action, 'DraftSaved'),
                           created_at,
                           updated_at
                    FROM booking_contracts
                    WHERE booking_id = @bookingId;";

                await using var cmd = new NpgsqlCommand(sql, connection);
                cmd.Parameters.Add("@bookingId", NpgsqlDbType.Uuid).Value = bookingId;
                await using var reader = await cmd.ExecuteReaderAsync();
                if (!await reader.ReadAsync())
                {
                    return NotFound(new { message = "No contract draft yet for this booking." });
                }

                return Ok(new
                {
                    id = reader.GetGuid(0),
                    bookingId = reader.GetGuid(1),
                    contractStatus = reader.IsDBNull(2) ? "Draft" : reader.GetString(2),
                    title = reader.IsDBNull(3) ? "Performance Contract" : reader.GetString(3),
                    terms = reader.IsDBNull(4) ? "" : reader.GetString(4),
                    agreedFee = reader.IsDBNull(5) ? 0 : reader.GetDecimal(5),
                    eventDate = reader.IsDBNull(6) ? (DateTime?)null : reader.GetDateTime(6),
                    location = reader.IsDBNull(7) ? "" : reader.GetString(7),
                    proposedByUserId = reader.IsDBNull(8) ? (Guid?)null : reader.GetGuid(8),
                    proposedByRole = reader.IsDBNull(9) ? "" : reader.GetString(9),
                    acceptedByUserId = reader.IsDBNull(10) ? (Guid?)null : reader.GetGuid(10),
                    acceptedByRole = reader.IsDBNull(11) ? "" : reader.GetString(11),
                    acceptedAt = reader.IsDBNull(12) ? (DateTime?)null : reader.GetDateTime(12),
                    revisionNumber = reader.IsDBNull(13) ? 1 : reader.GetInt32(13),
                    lastAction = reader.IsDBNull(14) ? "DraftSaved" : reader.GetString(14),
                    createdAt = reader.IsDBNull(15) ? DateTime.UtcNow : reader.GetDateTime(15),
                    updatedAt = reader.IsDBNull(16) ? DateTime.UtcNow : reader.GetDateTime(16)
                });
            }
            catch (Exception ex)
            {
                return StatusCode(500, new { message = "Failed to load contract draft: " + ex.Message });
            }
        }

        [HttpPost("{bookingId}/contract")]
        public async Task<IActionResult> CreateBookingContract(Guid bookingId)
        {
            try
            {
                var actorUserId = GetActorUserId(User);
                var actorRole = GetActorRole(User);
                if (!actorUserId.HasValue)
                {
                    return Unauthorized(new { message = "Sign in again before creating this contract." });
                }

                await using var connection = new NpgsqlConnection(_connectionString);
                await connection.OpenAsync();
                await EnsureBookingsTableExists(connection);
                await PlatformFeatureSupport.EnsureSharedBusinessSchemaAsync(connection);
                var bookingContext = await GetBookingParticipantContextAsync(connection, bookingId);
                if (bookingContext is null)
                {
                    return NotFound(new { message = "Booking not found." });
                }

                if (!CanAccessBooking(actorUserId.Value, actorRole, bookingContext))
                {
                    return Forbid();
                }

                const string bookingSql = @"
                    SELECT
                        COALESCE(event_title, 'Booking Request'),
                        COALESCE(budget, 0),
                        event_date,
                        COALESCE(location, ''),
                        COALESCE(target_role, 'Artist')
                    FROM bookings
                    WHERE id = @id;";

                string eventTitle;
                decimal budget;
                DateTime? eventDate;
                string location;
                string targetRole;

                await using (var bookingCmd = new NpgsqlCommand(bookingSql, connection))
                {
                    bookingCmd.Parameters.Add("@id", NpgsqlDbType.Uuid).Value = bookingId;
                    await using var reader = await bookingCmd.ExecuteReaderAsync();
                    if (!await reader.ReadAsync())
                    {
                        return NotFound(new { message = "Booking not found." });
                    }

                    eventTitle = reader.IsDBNull(0) ? "Booking Request" : reader.GetString(0);
                    budget = reader.IsDBNull(1) ? 0 : reader.GetDecimal(1);
                    eventDate = reader.IsDBNull(2) ? (DateTime?)null : reader.GetDateTime(2);
                    location = reader.IsDBNull(3) ? "" : reader.GetString(3);
                    targetRole = reader.IsDBNull(4) ? "Artist" : reader.GetString(4);
                }

                await PlatformFeatureSupport.EnsureBookingContractAsync(connection, bookingId, eventTitle, budget, eventDate, location, targetRole);

                const string bootstrapSql = @"
                    UPDATE booking_contracts
                    SET proposed_by_user_id = COALESCE(proposed_by_user_id, @actorUserId),
                        proposed_by_role = COALESCE(proposed_by_role, @actorRole),
                        last_action = COALESCE(NULLIF(last_action, ''), 'DraftSaved')
                    WHERE booking_id = @bookingId;";
                await using (var bootstrapCmd = new NpgsqlCommand(bootstrapSql, connection))
                {
                    bootstrapCmd.Parameters.Add("@actorUserId", NpgsqlDbType.Uuid).Value = actorUserId.Value;
                    bootstrapCmd.Parameters.Add("@actorRole", NpgsqlDbType.Text).Value = actorRole;
                    bootstrapCmd.Parameters.Add("@bookingId", NpgsqlDbType.Uuid).Value = bookingId;
                    await bootstrapCmd.ExecuteNonQueryAsync();
                }

                return await GetBookingContract(bookingId);
            }
            catch (Exception ex)
            {
                return StatusCode(500, new { message = "Failed to create contract draft: " + ex.Message });
            }
        }

        [HttpPatch("{bookingId}/contract")]
        public async Task<IActionResult> UpdateBookingContract(Guid bookingId, [FromBody] UpdateBookingContractRequest req)
        {
            try
            {
                var actorUserId = GetActorUserId(User);
                var actorRole = GetActorRole(User);
                if (!actorUserId.HasValue)
                {
                    return Unauthorized(new { message = "Sign in again before updating this contract." });
                }

                await using var connection = new NpgsqlConnection(_connectionString);
                await connection.OpenAsync();
                await PlatformFeatureSupport.EnsureSharedBusinessSchemaAsync(connection);
                await EnsureBookingsTableExists(connection);
                var bookingContext = await GetBookingParticipantContextAsync(connection, bookingId);
                if (bookingContext is null)
                {
                    return NotFound(new { message = "Booking not found." });
                }

                if (!CanAccessBooking(actorUserId.Value, actorRole, bookingContext))
                {
                    return Forbid();
                }

                const string contractSql = @"
                    SELECT COALESCE(title, ''),
                           COALESCE(terms, ''),
                           agreed_fee,
                           COALESCE(contract_status, 'Draft'),
                           proposed_by_user_id,
                           COALESCE(proposed_by_role, ''),
                           COALESCE(revision_number, 1)
                    FROM booking_contracts
                    WHERE booking_id = @bookingId;";

                string currentTitle;
                string currentTerms;
                decimal? currentAgreedFee;
                string currentContractStatus;
                Guid? proposedByUserId;
                string proposedByRole;
                int currentRevisionNumber;

                await using (var loadCmd = new NpgsqlCommand(contractSql, connection))
                {
                    loadCmd.Parameters.Add("@bookingId", NpgsqlDbType.Uuid).Value = bookingId;
                    await using var reader = await loadCmd.ExecuteReaderAsync();
                    if (!await reader.ReadAsync())
                    {
                        return NotFound(new { message = "No contract draft yet for this booking." });
                    }

                    currentTitle = reader.IsDBNull(0) ? "" : reader.GetString(0);
                    currentTerms = reader.IsDBNull(1) ? "" : reader.GetString(1);
                    currentAgreedFee = reader.IsDBNull(2) ? (decimal?)null : reader.GetDecimal(2);
                    currentContractStatus = reader.IsDBNull(3) ? "Draft" : reader.GetString(3);
                    proposedByUserId = reader.IsDBNull(4) ? (Guid?)null : reader.GetGuid(4);
                    proposedByRole = reader.IsDBNull(5) ? "" : reader.GetString(5);
                    currentRevisionNumber = reader.IsDBNull(6) ? 1 : reader.GetInt32(6);
                }

                var requestedStatus = string.IsNullOrWhiteSpace(req.contractStatus) ? null : req.contractStatus.Trim();
                var nextTitle = string.IsNullOrWhiteSpace(req.title) ? currentTitle : req.title.Trim();
                var nextTerms = string.IsNullOrWhiteSpace(req.terms) ? currentTerms : req.terms.Trim();
                var nextAgreedFee = req.agreedFee ?? currentAgreedFee;

                if (requestedStatus is not null &&
                    requestedStatus.Equals("Rejected", StringComparison.OrdinalIgnoreCase))
                {
                    requestedStatus = "NeedsRevision";
                }

                if (requestedStatus is not null &&
                    (requestedStatus.Equals("Accepted", StringComparison.OrdinalIgnoreCase) ||
                     requestedStatus.Equals("NeedsRevision", StringComparison.OrdinalIgnoreCase)) &&
                    !CanRespondToProposal(actorUserId.Value, actorRole, proposedByRole, proposedByUserId))
                {
                    return BadRequest(new { message = "You cannot respond to your own contract proposal." });
                }

                if (requestedStatus is not null &&
                    requestedStatus.Equals("PendingAcceptance", StringComparison.OrdinalIgnoreCase))
                {
                    if (string.IsNullOrWhiteSpace(nextTitle) || string.IsNullOrWhiteSpace(nextTerms) || !nextAgreedFee.HasValue || nextAgreedFee.Value <= 0)
                    {
                        return BadRequest(new { message = "A submitted contract proposal needs a title, terms, and proposed budget." });
                    }

                    const string submitSql = @"
                        UPDATE booking_contracts
                        SET title = @title,
                            terms = @terms,
                            agreed_fee = @agreedFee,
                            contract_status = 'PendingAcceptance',
                            proposed_by_user_id = @actorUserId,
                            proposed_by_role = @actorRole,
                            accepted_by_user_id = NULL,
                            accepted_by_role = NULL,
                            accepted_at = NULL,
                            revision_number = COALESCE(revision_number, 1) + 1,
                            last_action = 'ProposalSubmitted',
                            updated_at = NOW()
                        WHERE booking_id = @bookingId;";

                    await using (var submitCmd = new NpgsqlCommand(submitSql, connection))
                    {
                        submitCmd.Parameters.Add("@title", NpgsqlDbType.Text).Value = nextTitle;
                        submitCmd.Parameters.Add("@terms", NpgsqlDbType.Text).Value = nextTerms;
                        submitCmd.Parameters.Add("@agreedFee", NpgsqlDbType.Numeric).Value = nextAgreedFee.Value;
                        submitCmd.Parameters.Add("@actorUserId", NpgsqlDbType.Uuid).Value = actorUserId.Value;
                        submitCmd.Parameters.Add("@actorRole", NpgsqlDbType.Text).Value = actorRole;
                        submitCmd.Parameters.Add("@bookingId", NpgsqlDbType.Uuid).Value = bookingId;
                        await submitCmd.ExecuteNonQueryAsync();
                    }

                    const string historySql = @"
                        INSERT INTO booking_contract_history (
                            id, booking_id, revision_number, action, actor_user_id, actor_role, title, terms, proposed_fee, contract_status, created_at
                        ) VALUES (
                            @id, @bookingId, @revisionNumber, 'ProposalSubmitted', @actorUserId, @actorRole, @title, @terms, @agreedFee, 'PendingAcceptance', NOW()
                        );";
                    await using (var historyCmd = new NpgsqlCommand(historySql, connection))
                    {
                        historyCmd.Parameters.Add("@id", NpgsqlDbType.Uuid).Value = Guid.NewGuid();
                        historyCmd.Parameters.Add("@bookingId", NpgsqlDbType.Uuid).Value = bookingId;
                        historyCmd.Parameters.Add("@revisionNumber", NpgsqlDbType.Integer).Value = currentRevisionNumber + 1;
                        historyCmd.Parameters.Add("@actorUserId", NpgsqlDbType.Uuid).Value = actorUserId.Value;
                        historyCmd.Parameters.Add("@actorRole", NpgsqlDbType.Text).Value = actorRole;
                        historyCmd.Parameters.Add("@title", NpgsqlDbType.Text).Value = nextTitle;
                        historyCmd.Parameters.Add("@terms", NpgsqlDbType.Text).Value = nextTerms;
                        historyCmd.Parameters.Add("@agreedFee", NpgsqlDbType.Numeric).Value = nextAgreedFee.Value;
                        await historyCmd.ExecuteNonQueryAsync();
                    }

                    return Ok(new { message = "Contract proposal submitted for review." });
                }

                if (requestedStatus is not null &&
                    requestedStatus.Equals("Accepted", StringComparison.OrdinalIgnoreCase))
                {
                    if (!currentAgreedFee.HasValue || currentAgreedFee.Value <= 0)
                    {
                        return BadRequest(new { message = "The current contract does not have a valid agreed budget yet." });
                    }

                    var agreedPlatformFee = CalculateTalentPlatformFee(bookingContext.TargetRole, currentAgreedFee);
                    var nextPaymentStatus = currentAgreedFee.Value > 0 ? "AwaitingTalentFeePayment" : "Paid";
                    const string acceptContractSql = @"
                        UPDATE booking_contracts
                        SET contract_status = 'Accepted',
                            accepted_by_user_id = @actorUserId,
                            accepted_by_role = @actorRole,
                            accepted_at = NOW(),
                            last_action = 'Accepted',
                            updated_at = NOW()
                        WHERE booking_id = @bookingId;";

                    await using (var acceptCmd = new NpgsqlCommand(acceptContractSql, connection))
                    {
                        acceptCmd.Parameters.Add("@actorUserId", NpgsqlDbType.Uuid).Value = actorUserId.Value;
                        acceptCmd.Parameters.Add("@actorRole", NpgsqlDbType.Text).Value = actorRole;
                        acceptCmd.Parameters.Add("@bookingId", NpgsqlDbType.Uuid).Value = bookingId;
                        await acceptCmd.ExecuteNonQueryAsync();
                    }

                    const string bookingUpdateSql = @"
                        UPDATE bookings
                        SET budget = @agreedFee,
                            talent_platform_fee = @platformFee,
                            talent_platform_fee_status = CASE
                                WHEN @platformFee > 0 THEN COALESCE(NULLIF(talent_platform_fee_status, 'NotRequired'), 'Unpaid')
                                ELSE 'NotRequired'
                            END,
                            status = 'Confirmed',
                            payment_status = CASE
                                WHEN COALESCE(service_fee_status, COALESCE(payment_status, 'Unpaid')) = 'Paid' AND @agreedFee > 0 THEN 'AwaitingTalentFeePayment'
                                WHEN COALESCE(service_fee_status, COALESCE(payment_status, 'Unpaid')) = 'Paid' THEN 'Paid'
                                ELSE payment_status
                            END
                        WHERE id = @bookingId;";
                    await using (var bookingCmd = new NpgsqlCommand(bookingUpdateSql, connection))
                    {
                        bookingCmd.Parameters.Add("@agreedFee", NpgsqlDbType.Numeric).Value = currentAgreedFee.Value;
                        bookingCmd.Parameters.Add("@platformFee", NpgsqlDbType.Numeric).Value = agreedPlatformFee;
                        bookingCmd.Parameters.Add("@bookingId", NpgsqlDbType.Uuid).Value = bookingId;
                        await bookingCmd.ExecuteNonQueryAsync();
                    }

                    const string acceptedHistorySql = @"
                        INSERT INTO booking_contract_history (
                            id, booking_id, revision_number, action, actor_user_id, actor_role, title, terms, proposed_fee, contract_status, created_at
                        ) VALUES (
                            @id, @bookingId, @revisionNumber, 'Accepted', @actorUserId, @actorRole, @title, @terms, @agreedFee, 'Accepted', NOW()
                        );";
                    await using (var acceptedHistoryCmd = new NpgsqlCommand(acceptedHistorySql, connection))
                    {
                        acceptedHistoryCmd.Parameters.Add("@id", NpgsqlDbType.Uuid).Value = Guid.NewGuid();
                        acceptedHistoryCmd.Parameters.Add("@bookingId", NpgsqlDbType.Uuid).Value = bookingId;
                        acceptedHistoryCmd.Parameters.Add("@revisionNumber", NpgsqlDbType.Integer).Value = currentRevisionNumber;
                        acceptedHistoryCmd.Parameters.Add("@actorUserId", NpgsqlDbType.Uuid).Value = actorUserId.Value;
                        acceptedHistoryCmd.Parameters.Add("@actorRole", NpgsqlDbType.Text).Value = actorRole;
                        acceptedHistoryCmd.Parameters.Add("@title", NpgsqlDbType.Text).Value = currentTitle;
                        acceptedHistoryCmd.Parameters.Add("@terms", NpgsqlDbType.Text).Value = currentTerms;
                        acceptedHistoryCmd.Parameters.Add("@agreedFee", NpgsqlDbType.Numeric).Value = currentAgreedFee.Value;
                        await acceptedHistoryCmd.ExecuteNonQueryAsync();
                    }

                    return Ok(new { message = "Contract accepted. Booking budget is now locked and ready for payment." });
                }

                if (requestedStatus is not null &&
                    requestedStatus.Equals("NeedsRevision", StringComparison.OrdinalIgnoreCase))
                {
                    const string revisionSql = @"
                        UPDATE booking_contracts
                        SET contract_status = 'NeedsRevision',
                            accepted_by_user_id = NULL,
                            accepted_by_role = NULL,
                            accepted_at = NULL,
                            last_action = 'NeedsRevision',
                            updated_at = NOW()
                        WHERE booking_id = @bookingId;";

                    await using (var revisionCmd = new NpgsqlCommand(revisionSql, connection))
                    {
                        revisionCmd.Parameters.Add("@bookingId", NpgsqlDbType.Uuid).Value = bookingId;
                        await revisionCmd.ExecuteNonQueryAsync();
                    }

                    return Ok(new { message = "Contract sent back for revision. Either side can submit a new proposal." });
                }

                const string sql = @"
                    UPDATE booking_contracts
                    SET title = COALESCE(@title, title),
                        terms = COALESCE(@terms, terms),
                        agreed_fee = COALESCE(@agreedFee, agreed_fee),
                        contract_status = COALESCE(@contractStatus, contract_status),
                        proposed_by_user_id = COALESCE(@actorUserId, proposed_by_user_id),
                        proposed_by_role = COALESCE(@actorRole, proposed_by_role),
                        last_action = 'DraftSaved',
                        updated_at = NOW()
                    WHERE booking_id = @bookingId;";

                await using var cmd = new NpgsqlCommand(sql, connection);
                cmd.Parameters.Add("@bookingId", NpgsqlDbType.Uuid).Value = bookingId;
                cmd.Parameters.Add("@title", NpgsqlDbType.Text).Value = (object?)req.title ?? DBNull.Value;
                cmd.Parameters.Add("@terms", NpgsqlDbType.Text).Value = (object?)req.terms ?? DBNull.Value;
                cmd.Parameters.Add("@agreedFee", NpgsqlDbType.Numeric).Value = (object?)req.agreedFee ?? DBNull.Value;
                cmd.Parameters.Add("@contractStatus", NpgsqlDbType.Text).Value = (object?)requestedStatus ?? DBNull.Value;
                cmd.Parameters.Add("@actorUserId", NpgsqlDbType.Uuid).Value = actorUserId.Value;
                cmd.Parameters.Add("@actorRole", NpgsqlDbType.Text).Value = actorRole;
                var updated = await cmd.ExecuteNonQueryAsync();

                if (updated == 0)
                {
                    return NotFound(new { message = "No contract draft yet for this booking." });
                }

                return Ok(new { message = "Contract draft updated." });
            }
            catch (Exception ex)
            {
                return StatusCode(500, new { message = "Failed to update contract draft: " + ex.Message });
            }
        }

        [HttpPost("{bookingId}/checkout")]
        [HttpPost("checkout/{bookingId}")]
        public async Task<IActionResult> CreateBookingCheckout(Guid bookingId, [FromBody] BookingCheckoutRequest req)
        {
            try
            {
                var actorUserId = GetActorUserId(User);
                var actorRole = GetActorRole(User);
                if (!actorUserId.HasValue)
                {
                    return Unauthorized(new { message = "Sign in again before starting checkout." });
                }

                await using var connection = new NpgsqlConnection(_connectionString);
                await connection.OpenAsync();
                await EnsureBookingsTableExists(connection);
                await PaymentLedgerService.EnsureSchemaAsync(connection);
                await EscrowService.EnsureSchemaAsync(connection);
                var bookingContext = await GetBookingParticipantContextAsync(connection, bookingId);
                if (bookingContext is null)
                {
                    return NotFound(new { message = "Booking request not found." });
                }

                if (!CanAccessBooking(actorUserId.Value, actorRole, bookingContext))
                {
                    return Forbid();
                }

                const string bookingSql = @"
                    SELECT COALESCE(event_title, 'Booking Request'),
                           COALESCE(booking_fee, 15),
                           COALESCE(budget, 0),
                           COALESCE(talent_platform_fee, 0),
                           COALESCE(status, 'Pending'),
                           COALESCE(payment_status, 'Unpaid'),
                           COALESCE(service_fee_status, COALESCE(payment_status, 'Unpaid')),
                           COALESCE(talent_fee_status, 'Unpaid'),
                           COALESCE(talent_platform_fee_status, 'Unpaid'),
                           customer_id,
                           target_user_id,
                           event_id
                    FROM bookings
                    WHERE id = @id;";

                string eventTitle;
                decimal bookingFee;
                decimal budget;
                decimal talentPlatformFee;
                string status;
                string paymentStatus;
                string serviceFeeStatus;
                string talentFeeStatus;
                string talentPlatformFeeStatus;
                Guid customerId;
                Guid targetUserId;
                Guid? eventId;

                using (var bookingCmd = new NpgsqlCommand(bookingSql, connection))
                {
                    bookingCmd.Parameters.Add("@id", NpgsqlDbType.Uuid).Value = bookingId;
                    using var reader = await bookingCmd.ExecuteReaderAsync();
                    if (!await reader.ReadAsync())
                    {
                        return NotFound(new { message = "Booking request not found." });
                    }

                    eventTitle = reader.IsDBNull(0) ? "Booking Request" : reader.GetString(0);
                    bookingFee = reader.IsDBNull(1) ? BookingServiceFee : reader.GetDecimal(1);
                    budget = reader.IsDBNull(2) ? 0 : reader.GetDecimal(2);
                    talentPlatformFee = reader.IsDBNull(3) ? 0 : reader.GetDecimal(3);
                    status = reader.IsDBNull(4) ? "Pending" : reader.GetString(4);
                    paymentStatus = NormalizePaymentStatus(reader.IsDBNull(5) ? "Unpaid" : reader.GetString(5));
                    serviceFeeStatus = NormalizeServiceFeeStatus(reader.IsDBNull(6) ? paymentStatus : reader.GetString(6), paymentStatus);
                    talentFeeStatus = NormalizeTalentFeeStatus(reader.IsDBNull(7) ? "Unpaid" : reader.GetString(7));
                    talentPlatformFeeStatus = NormalizePaymentStatus(reader.IsDBNull(8) ? "Unpaid" : reader.GetString(8));
                    customerId = reader.IsDBNull(9) ? Guid.Empty : reader.GetGuid(9);
                    targetUserId = reader.IsDBNull(10) ? Guid.Empty : reader.GetGuid(10);
                    eventId = reader.IsDBNull(11) ? null : reader.GetGuid(11);
                }

                var paymentType = string.Equals(req.paymentType, "talent", StringComparison.OrdinalIgnoreCase)
                    ? "talent"
                    : string.Equals(req.paymentType, "platform", StringComparison.OrdinalIgnoreCase)
                        ? "platform"
                        : "service";
                if (paymentType == "service" && actorUserId.Value != customerId)
                {
                    return Forbid();
                }

                if (paymentType == "talent" && actorUserId.Value != customerId)
                {
                    return Forbid();
                }

                if (paymentType == "platform" && actorUserId.Value != targetUserId)
                {
                    return Forbid();
                }

                var amountToCharge = paymentType == "talent" ? budget : paymentType == "platform" ? talentPlatformFee : bookingFee;

                if (paymentType == "service" && amountToCharge < PayMongoMinimumAmount)
                {
                    amountToCharge = PayMongoMinimumAmount;
                }

                if (status.StartsWith("Cancelled", StringComparison.OrdinalIgnoreCase))
                {
                    return BadRequest(new { message = "This booking is no longer eligible for payment." });
                }

                if (string.IsNullOrWhiteSpace(_paymongoSecretKey))
                {
                    return StatusCode(500, new { message = "PayMongo is not configured yet. Add PayMongo:SecretKey before starting checkout." });
                }

                if (paymentType == "service" && serviceFeeStatus == "Paid")
                {
                    return Conflict(new { message = "The booking service fee has already been paid." });
                }

                if (paymentType == "talent" && !string.Equals(status, "Confirmed", StringComparison.OrdinalIgnoreCase))
                {
                    return BadRequest(new { message = "The promised talent fee can only be paid after the booking is confirmed." });
                }

                if (paymentType == "talent" &&
                    (talentFeeStatus == "Paid" || talentFeeStatus == "HeldInEscrow" || talentFeeStatus == "ReadyForRelease" || talentFeeStatus == "Released"))
                {
                    return Conflict(new { message = "The talent fee is already secured by the platform for this booking." });
                }

                if (paymentType == "platform" && talentPlatformFeeStatus == "Paid")
                {
                    return Conflict(new { message = "The talent platform fee has already been paid." });
                }

                if (paymentType == "platform" && !string.Equals(status, "Confirmed", StringComparison.OrdinalIgnoreCase))
                {
                    return BadRequest(new { message = "The platform fee becomes payable after the booking is confirmed." });
                }

                if (amountToCharge < PayMongoMinimumAmount)
                {
                    return BadRequest(new { message = $"PayMongo requires a minimum amount of {FormatPeso(PayMongoMinimumAmount)}." });
                }

                if (paymentType == "talent" && amountToCharge <= 0)
                {
                    return BadRequest(new { message = "Set an agreed talent fee in the contract before charging the customer." });
                }

                if (string.IsNullOrWhiteSpace(req.successUrl) || string.IsNullOrWhiteSpace(req.cancelUrl))
                {
                    return BadRequest(new { message = "Success and cancel URLs are required." });
                }

                Guid? escrowTransactionId = null;
                if (paymentType == "talent")
                {
                    escrowTransactionId = await EscrowService.CreatePendingAsync(
                        connection,
                        bookingId,
                        customerId,
                        targetUserId,
                        amountToCharge);
                }

                var amountInCentavos = (int)(amountToCharge * 100);
                var paymentLabel = paymentType == "talent" ? "Talent Fee Escrow" : paymentType == "platform" ? "Talent Platform Fee" : "Booking Service Fee";
                var paymongoPayload = new
                {
                    data = new
                    {
                        attributes = new
                        {
                            send_email_receipt = false,
                            show_description = true,
                            show_line_items = true,
                            payment_method_types = new[] { "gcash", "card", "paymaya" },
                            line_items = new[]
                            {
                                new
                                {
                                    currency = "PHP",
                                    amount = amountInCentavos,
                                    name = $"{paymentLabel} - {eventTitle}",
                                    quantity = 1
                                }
                            },
                            success_url = req.successUrl,
                            cancel_url = req.cancelUrl,
                            metadata = new
                            {
                                booking_id = bookingId.ToString(),
                                payment_type = paymentType,
                                escrow_transaction_id = escrowTransactionId?.ToString()
                            }
                        }
                    }
                };

                using var client = new HttpClient();
                var plainTextBytes = Encoding.UTF8.GetBytes(_paymongoSecretKey);
                var base64Auth = Convert.ToBase64String(plainTextBytes);
                client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic", base64Auth);
                client.DefaultRequestHeaders.Add("Idempotency-Key", $"booking-{bookingId}-{paymentType}");

                var content = new StringContent(JsonSerializer.Serialize(paymongoPayload), Encoding.UTF8, "application/json");
                var response = await client.PostAsync("https://api.paymongo.com/v1/checkout_sessions", content);
                var responseString = await response.Content.ReadAsStringAsync();

                if (!response.IsSuccessStatusCode)
                {
                    var payMongoMessage = GetPayMongoHttpErrorMessage(
                        (int)response.StatusCode,
                        responseString,
                        "PayMongo could not create the checkout session. Please verify the amount and payment settings."
                    );
                    return StatusCode((int)response.StatusCode, new { message = payMongoMessage, details = responseString });
                }

                using var doc = JsonDocument.Parse(responseString);
                var data = doc.RootElement.GetProperty("data");
                var checkoutId = data.GetProperty("id").GetString();
                var checkoutUrl = data.GetProperty("attributes").GetProperty("checkout_url").GetString();
                var checkoutReference = data.GetProperty("attributes").TryGetProperty("reference_number", out var referenceProp)
                    ? referenceProp.GetString()
                    : null;

                if (escrowTransactionId.HasValue)
                {
                    await EscrowService.LinkCheckoutAsync(connection, escrowTransactionId.Value, checkoutId);
                }

                var updateSql = paymentType == "talent"
                    ? @"
                        UPDATE bookings
                        SET payment_status = 'AwaitingTalentFeePayment',
                            talent_fee_status = 'AwaitingPayment',
                            talent_fee_checkout_id = @checkoutId,
                            talent_fee_checkout_url = @checkoutUrl,
                            talent_fee_checkout_reference = @checkoutReference
                        WHERE id = @bookingId;"
                    : paymentType == "platform"
                        ? @"
                        UPDATE bookings
                        SET talent_platform_fee_status = 'AwaitingPayment',
                            talent_platform_fee = @amountToCharge,
                            talent_platform_fee_checkout_id = @checkoutId,
                            talent_platform_fee_checkout_url = @checkoutUrl,
                            talent_platform_fee_checkout_reference = @checkoutReference
                        WHERE id = @bookingId;"
                    : @"
                        UPDATE bookings
                        SET payment_status = 'AwaitingPayment',
                            service_fee_status = 'AwaitingPayment',
                            booking_fee = @amountToCharge,
                            paymongo_checkout_id = @checkoutId,
                            paymongo_checkout_url = @checkoutUrl,
                            paymongo_checkout_reference = @checkoutReference
                        WHERE id = @bookingId;";

                using var updateCmd = new NpgsqlCommand(updateSql, connection);
                updateCmd.Parameters.Add("@checkoutId", NpgsqlDbType.Text).Value = (object?)checkoutId ?? DBNull.Value;
                updateCmd.Parameters.Add("@checkoutUrl", NpgsqlDbType.Text).Value = (object?)checkoutUrl ?? DBNull.Value;
                updateCmd.Parameters.Add("@checkoutReference", NpgsqlDbType.Text).Value = (object?)checkoutReference ?? DBNull.Value;
                updateCmd.Parameters.Add("@amountToCharge", NpgsqlDbType.Numeric).Value = amountToCharge;
                updateCmd.Parameters.Add("@bookingId", NpgsqlDbType.Uuid).Value = bookingId;
                await updateCmd.ExecuteNonQueryAsync();

                await PaymentLedgerService.UpsertPendingAsync(
                    connection,
                    paymentScope: paymentType == "talent" ? "booking_talent_fee" : paymentType == "platform" ? "booking_talent_platform_fee" : "booking_service_fee",
                    userId: paymentType == "platform"
                        ? (targetUserId == Guid.Empty ? null : targetUserId)
                        : (customerId == Guid.Empty ? null : customerId),
                    organizerId: targetUserId == Guid.Empty ? null : targetUserId,
                    eventId: eventId,
                    ticketId: null,
                    bookingId: bookingId,
                    amount: amountToCharge,
                    description: $"{paymentLabel} for {eventTitle}",
                    checkoutId: checkoutId ?? string.Empty,
                    checkoutReference: checkoutReference ?? string.Empty,
                    featureUnlockState: paymentType == "talent" ? "AwaitingTalentSettlement" : paymentType == "platform" ? "TalentPlatformFeePending" : "MessagesLocked",
                    metadata: new
                    {
                        paymentType,
                        eventTitle,
                        bookingFee,
                        budget,
                        escrowTransactionId
                    });

                return Ok(new
                {
                    message = "Booking checkout session created.",
                    checkoutUrl,
                    paymentStatus = paymentType == "talent" ? "AwaitingTalentFeePayment" : paymentType == "platform" ? "AwaitingTalentPlatformFeePayment" : "AwaitingPayment",
                    paymentType,
                    amount = amountToCharge
                });
            }
            catch (Exception ex)
            {
                return StatusCode(500, new
                {
                    message = ConfigurationFallbacks.BuildSafeErrorMessage(
                        _configuration,
                        "Failed to create booking checkout.",
                        ex)
                });
            }
        }

        [HttpPost("{bookingId}/payment/confirm")]
        public async Task<IActionResult> ConfirmBookingPayment(Guid bookingId, [FromQuery] string? paymentType)
        {
            try
            {
                var actorUserId = GetActorUserId(User);
                var actorRole = GetActorRole(User);
                if (!actorUserId.HasValue)
                {
                    return Unauthorized(new { message = "Sign in again before confirming this payment." });
                }

                await using var connection = new NpgsqlConnection(_connectionString);
                await connection.OpenAsync();
                await EnsureBookingsTableExists(connection);
                await EnsureNotificationsTableExists(connection);
                await EscrowService.EnsureSchemaAsync(connection);
                var bookingContext = await GetBookingParticipantContextAsync(connection, bookingId);
                if (bookingContext is null)
                {
                    return NotFound(new { message = "Booking request not found." });
                }

                if (!CanAccessBooking(actorUserId.Value, actorRole, bookingContext))
                {
                    return Forbid();
                }

                const string sql = @"
                    SELECT COALESCE(paymongo_checkout_id, ''),
                           COALESCE(payment_status, 'Unpaid'),
                           COALESCE(paymongo_checkout_reference, ''),
                           COALESCE(status, 'Pending'),
                           COALESCE(target_role, 'Artist'),
                           COALESCE(service_fee_status, COALESCE(payment_status, 'Unpaid')),
                           COALESCE(talent_fee_status, 'Unpaid'),
                           COALESCE(talent_platform_fee_status, 'Unpaid'),
                           COALESCE(talent_fee_checkout_id, ''),
                           COALESCE(talent_fee_checkout_reference, ''),
                           COALESCE(talent_platform_fee_checkout_id, ''),
                           COALESCE(talent_platform_fee_checkout_reference, ''),
                           customer_id,
                           target_user_id,
                           COALESCE(event_title, 'Booking Request')
                    FROM bookings
                    WHERE id = @id;";

                string checkoutId;
                string paymentStatus;
                string checkoutReference;
                string status;
                string targetRole;
                string serviceFeeStatus;
                string talentFeeStatus;
                string talentPlatformFeeStatus;
                string talentCheckoutId;
                string talentCheckoutReference;
                string platformCheckoutId;
                string platformCheckoutReference;
                Guid customerId;
                Guid targetUserId;
                string eventTitle;

                using (var cmd = new NpgsqlCommand(sql, connection))
                {
                    cmd.Parameters.Add("@id", NpgsqlDbType.Uuid).Value = bookingId;
                    using var reader = await cmd.ExecuteReaderAsync();
                    if (!await reader.ReadAsync())
                    {
                        return NotFound(new { message = "Booking request not found." });
                    }

                    checkoutId = reader.IsDBNull(0) ? "" : reader.GetString(0);
                    paymentStatus = NormalizePaymentStatus(reader.IsDBNull(1) ? "Unpaid" : reader.GetString(1));
                    checkoutReference = reader.IsDBNull(2) ? "" : reader.GetString(2);
                    status = reader.IsDBNull(3) ? "Pending" : reader.GetString(3);
                    targetRole = reader.IsDBNull(4) ? "Artist" : NormalizeTargetRole(reader.GetString(4));
                    serviceFeeStatus = NormalizeServiceFeeStatus(reader.IsDBNull(5) ? paymentStatus : reader.GetString(5), paymentStatus);
                    talentFeeStatus = NormalizeTalentFeeStatus(reader.IsDBNull(6) ? "Unpaid" : reader.GetString(6));
                    talentPlatformFeeStatus = NormalizePaymentStatus(reader.IsDBNull(7) ? "Unpaid" : reader.GetString(7));
                    talentCheckoutId = reader.IsDBNull(8) ? "" : reader.GetString(8);
                    talentCheckoutReference = reader.IsDBNull(9) ? "" : reader.GetString(9);
                    platformCheckoutId = reader.IsDBNull(10) ? "" : reader.GetString(10);
                    platformCheckoutReference = reader.IsDBNull(11) ? "" : reader.GetString(11);
                    customerId = reader.IsDBNull(12) ? Guid.Empty : reader.GetGuid(12);
                    targetUserId = reader.IsDBNull(13) ? Guid.Empty : reader.GetGuid(13);
                    eventTitle = reader.IsDBNull(14) ? "Booking Request" : reader.GetString(14);
                }

                var normalizedPaymentType = string.Equals(paymentType, "talent", StringComparison.OrdinalIgnoreCase)
                    ? "talent"
                    : string.Equals(paymentType, "platform", StringComparison.OrdinalIgnoreCase)
                        ? "platform"
                        : "service";
                if (normalizedPaymentType == "service" && actorUserId.Value != customerId)
                {
                    return Forbid();
                }

                if (normalizedPaymentType == "talent" && actorUserId.Value != customerId)
                {
                    return Forbid();
                }

                if (normalizedPaymentType == "platform" && actorUserId.Value != targetUserId)
                {
                    return Forbid();
                }

                if (normalizedPaymentType == "talent")
                {
                    checkoutId = talentCheckoutId;
                    checkoutReference = talentCheckoutReference;
                }
                else if (normalizedPaymentType == "platform")
                {
                    checkoutId = platformCheckoutId;
                    checkoutReference = platformCheckoutReference;
                }

                if (normalizedPaymentType == "service" && serviceFeeStatus == "Paid")
                {
                    return Ok(new { message = "Booking payment already confirmed.", paymentStatus = serviceFeeStatus });
                }

                if (normalizedPaymentType == "talent" &&
                    (talentFeeStatus == "Paid" || talentFeeStatus == "HeldInEscrow" || talentFeeStatus == "ReadyForRelease" || talentFeeStatus == "Released"))
                {
                    return Ok(new { message = "Talent fee already confirmed.", paymentStatus = talentFeeStatus });
                }

                if (normalizedPaymentType == "platform" && talentPlatformFeeStatus == "Paid")
                {
                    return Ok(new { message = "Talent platform fee already confirmed.", paymentStatus = talentPlatformFeeStatus });
                }

                if (string.IsNullOrWhiteSpace(checkoutId))
                {
                    return BadRequest(new { message = "No checkout session is attached to this booking yet." });
                }

                using var client = new HttpClient();
                var plainTextBytes = Encoding.UTF8.GetBytes(_paymongoSecretKey);
                var base64Auth = Convert.ToBase64String(plainTextBytes);
                client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic", base64Auth);

                var response = await client.GetAsync($"https://api.paymongo.com/v1/checkout_sessions/{checkoutId}");
                var responseString = await response.Content.ReadAsStringAsync();

                if (!response.IsSuccessStatusCode)
                {
                    return StatusCode((int)response.StatusCode, new { message = "Failed to verify PayMongo checkout.", details = responseString });
                }

                using var doc = JsonDocument.Parse(responseString);
                var attributes = doc.RootElement.GetProperty("data").GetProperty("attributes");
                var payments = attributes.TryGetProperty("payments", out var paymentArray) && paymentArray.ValueKind == JsonValueKind.Array
                    ? paymentArray
                    : default;
                if (string.IsNullOrWhiteSpace(checkoutReference) && attributes.TryGetProperty("reference_number", out var checkoutReferenceProp))
                {
                    checkoutReference = checkoutReferenceProp.GetString() ?? "";
                }

                if (payments.ValueKind != JsonValueKind.Array || payments.GetArrayLength() == 0)
                {
                    return Ok(new { message = "Payment is still pending.", paymentStatus = "AwaitingPayment" });
                }

                string paymentReference = "";
                string paymentMethod = "";
                string paymentId = "";
                DateTime? paidAt = null;

                var firstPayment = payments[0];
                if (firstPayment.TryGetProperty("id", out var paymentIdProp))
                {
                    paymentId = paymentIdProp.GetString() ?? "";
                }

                if (firstPayment.TryGetProperty("attributes", out var paymentAttributes))
                {
                    if (paymentAttributes.TryGetProperty("reference_number", out var referenceProp))
                    {
                        paymentReference = referenceProp.GetString() ?? "";
                    }

                    if (paymentAttributes.TryGetProperty("paid_at", out var paidAtProp) && paidAtProp.ValueKind == JsonValueKind.Number)
                    {
                        paidAt = DateTimeOffset.FromUnixTimeSeconds(paidAtProp.GetInt64()).UtcDateTime;
                    }

                    if (paymentAttributes.TryGetProperty("source", out var sourceProp) &&
                        sourceProp.ValueKind == JsonValueKind.Object &&
                        sourceProp.TryGetProperty("type", out var sourceTypeProp))
                    {
                        paymentMethod = sourceTypeProp.GetString() ?? "";
                    }
                }

                var nextStatus = normalizedPaymentType == "service" && status.StartsWith("Awaiting", StringComparison.OrdinalIgnoreCase)
                    ? $"Pending {targetRole} Approval"
                    : status;
                var canReleaseEscrow = normalizedPaymentType == "platform" &&
                    string.Equals(status, "Completed", StringComparison.OrdinalIgnoreCase) &&
                    (talentFeeStatus == "HeldInEscrow" || talentFeeStatus == "ReadyForRelease");

                var updateSql = normalizedPaymentType == "talent"
                    ? @"
                        UPDATE bookings
                        SET payment_status = 'TalentFeeHeldInEscrow',
                            talent_fee_status = CASE
                                WHEN COALESCE(talent_platform_fee_status, 'Unpaid') = 'Paid' AND COALESCE(status, 'Pending') = 'Completed' THEN 'Released'
                                WHEN COALESCE(talent_platform_fee_status, 'Unpaid') = 'Paid' THEN 'ReadyForRelease'
                                ELSE 'HeldInEscrow'
                            END,
                            talent_fee_paid_at = COALESCE(@paidAt, NOW()),
                            talent_fee_payment_id = @paymentId,
                            talent_fee_payment_reference = @paymentReference,
                            talent_fee_payment_method = @paymentMethod,
                            talent_fee_checkout_reference = @checkoutReference
                        WHERE id = @bookingId;"
                    : normalizedPaymentType == "platform"
                        ? @"
                        UPDATE bookings
                        SET payment_status = CASE
                                WHEN COALESCE(talent_fee_status, 'Unpaid') IN ('HeldInEscrow', 'ReadyForRelease') AND COALESCE(status, 'Pending') = 'Completed' THEN 'Paid'
                                ELSE payment_status
                            END,
                            talent_fee_status = CASE
                                WHEN COALESCE(talent_fee_status, 'Unpaid') IN ('HeldInEscrow', 'ReadyForRelease') AND COALESCE(status, 'Pending') = 'Completed' THEN 'Released'
                                WHEN COALESCE(talent_fee_status, 'Unpaid') = 'HeldInEscrow' THEN 'ReadyForRelease'
                                ELSE talent_fee_status
                            END,
                            talent_platform_fee_status = 'Paid',
                            talent_platform_fee_paid_at = COALESCE(@paidAt, NOW()),
                            talent_platform_fee_payment_id = @paymentId,
                            talent_platform_fee_payment_reference = @paymentReference,
                            talent_platform_fee_payment_method = @paymentMethod,
                            talent_platform_fee_checkout_reference = @checkoutReference
                        WHERE id = @bookingId;"
                    : @"
                        UPDATE bookings
                        SET payment_status = CASE
                                WHEN COALESCE(budget, 0) > 0 THEN 'ServiceFeePaid'
                                ELSE 'Paid'
                            END,
                            service_fee_status = 'Paid',
                            status = @status,
                            paid_at = COALESCE(@paidAt, NOW()),
                            service_fee_paid_at = COALESCE(@paidAt, NOW()),
                            paymongo_payment_id = @paymentId,
                            paymongo_payment_reference = @paymentReference,
                            payment_method = @paymentMethod,
                            service_fee_payment_method = @paymentMethod,
                            paymongo_checkout_reference = @checkoutReference
                        WHERE id = @bookingId;";

                using var updateCmd = new NpgsqlCommand(updateSql, connection);
                updateCmd.Parameters.Add("@status", NpgsqlDbType.Text).Value = nextStatus;
                updateCmd.Parameters.Add("@paidAt", NpgsqlDbType.TimestampTz).Value = (object?)paidAt ?? DBNull.Value;
                updateCmd.Parameters.Add("@paymentId", NpgsqlDbType.Text).Value = (object?)paymentId ?? DBNull.Value;
                updateCmd.Parameters.Add("@paymentReference", NpgsqlDbType.Text).Value = (object?)paymentReference ?? DBNull.Value;
                updateCmd.Parameters.Add("@paymentMethod", NpgsqlDbType.Text).Value = (object?)paymentMethod ?? DBNull.Value;
                updateCmd.Parameters.Add("@checkoutReference", NpgsqlDbType.Text).Value = (object?)checkoutReference ?? DBNull.Value;
                updateCmd.Parameters.Add("@bookingId", NpgsqlDbType.Uuid).Value = bookingId;
                await updateCmd.ExecuteNonQueryAsync();

                if (normalizedPaymentType == "talent")
                {
                    await EscrowService.MarkHeldAsync(connection, bookingId, paymentId);
                }
                else if (normalizedPaymentType == "platform" && canReleaseEscrow)
                {
                    await EscrowService.ReleaseAsync(connection, bookingId);
                }

                await PaymentLedgerService.MarkPaidAsync(
                    connection,
                    paymentScope: normalizedPaymentType == "talent" ? "booking_talent_fee" : normalizedPaymentType == "platform" ? "booking_talent_platform_fee" : "booking_service_fee",
                    ticketId: null,
                    bookingId: bookingId,
                    paymentMethod: paymentMethod,
                    paymentReference: paymentReference,
                    checkoutReference: checkoutReference,
                    featureUnlockState: normalizedPaymentType == "talent" ? "TalentFeeHeldInEscrow" : normalizedPaymentType == "platform" ? (canReleaseEscrow ? "TalentFeeReleased" : "TalentPlatformFeeSettled") : "MessagesUnlocked",
                    paidAt: paidAt);

                if (normalizedPaymentType == "service" && targetUserId != Guid.Empty)
                {
                    await InsertNotification(connection, targetUserId, "booking_payment", "Service fee paid", $"The booking service fee for {eventTitle} has been paid. You can now review the request.", bookingId, "booking");
                }

                if (normalizedPaymentType == "talent" && targetUserId != Guid.Empty)
                {
                    await InsertNotification(connection, targetUserId, "talent_fee_paid", "Talent fee held in escrow", $"The customer's talent fee for {eventTitle} is now secured by the platform and will only be released after completion and platform-fee settlement.", bookingId, "booking");
                }

                if (normalizedPaymentType == "platform" && targetUserId != Guid.Empty)
                {
                    await InsertNotification(connection, targetUserId, "platform_fee_paid", "Platform fee paid", $"Your platform fee for {eventTitle} is confirmed.", bookingId, "booking");
                }

                if (normalizedPaymentType == "talent" && customerId != Guid.Empty)
                {
                    await InsertNotification(connection, customerId, "payment_receipt", "Talent fee held in escrow", $"Your talent fee payment for {eventTitle} is now held by the platform until the booking is completed.", bookingId, "booking");
                }

                return Ok(new
                {
                    message = normalizedPaymentType == "talent" ? "Talent fee secured in escrow." : normalizedPaymentType == "platform" ? (canReleaseEscrow ? "Platform fee confirmed and escrow released." : "Talent platform fee confirmed.") : "Booking payment confirmed.",
                    paymentStatus = normalizedPaymentType == "talent"
                        ? "HeldInEscrow"
                        : normalizedPaymentType == "platform"
                            ? (canReleaseEscrow ? "Released" : "Paid")
                            : (status.StartsWith("Awaiting", StringComparison.OrdinalIgnoreCase) ? "ServiceFeePaid" : "Paid"),
                    paymentMethod,
                    status = nextStatus
                });
            }
            catch (Exception ex)
            {
                return StatusCode(500, new
                {
                    message = ConfigurationFallbacks.BuildSafeErrorMessage(
                        _configuration,
                        "Failed to confirm booking payment.",
                        ex)
                });
            }
        }

        [HttpPost("{bookingId}/refunds/request")]
        public async Task<IActionResult> RequestBookingRefund(Guid bookingId, [FromBody] CreateBookingRefundRequest? req)
        {
            try
            {
                var actorUserId = GetActorUserId(User);
                var actorRole = GetActorRole(User);
                if (!actorUserId.HasValue)
                {
                    return Unauthorized(new { message = "Sign in again before requesting a refund." });
                }

                await using var connection = new NpgsqlConnection(_connectionString);
                await connection.OpenAsync();
                await EnsureBookingsTableExists(connection);
                await EnsureNotificationsTableExists(connection);
                await PaymentLedgerService.EnsureSchemaAsync(connection);
                await PaymentRefundService.EnsureSchemaAsync(connection);
                await EscrowService.EnsureSchemaAsync(connection);

                const string sql = @"
                    SELECT customer_id,
                           target_user_id,
                           event_id,
                           COALESCE(target_role, 'Artist'),
                           COALESCE(status, 'Pending'),
                           event_date,
                           event_end_time,
                           COALESCE(event_title, 'Booking Request'),
                           COALESCE(service_fee_status, COALESCE(payment_status, 'Unpaid')),
                           COALESCE(talent_fee_status, 'Unpaid'),
                           COALESCE(talent_platform_fee_status, 'Unpaid')
                    FROM bookings
                    WHERE id = @id;";

                Guid customerId;
                Guid targetUserId;
                Guid? eventId;
                string targetRole;
                string status;
                DateTime? eventDate;
                DateTime? eventEndTime;
                string eventTitle;
                string serviceFeeStatus;
                string talentFeeStatus;
                string platformFeeStatus;

                await using (var cmd = new NpgsqlCommand(sql, connection))
                {
                    cmd.Parameters.Add("@id", NpgsqlDbType.Uuid).Value = bookingId;
                    await using var reader = await cmd.ExecuteReaderAsync();
                    if (!await reader.ReadAsync())
                    {
                        return NotFound(new { message = "Booking request not found." });
                    }

                    customerId = reader.IsDBNull(0) ? Guid.Empty : reader.GetGuid(0);
                    targetUserId = reader.IsDBNull(1) ? Guid.Empty : reader.GetGuid(1);
                    eventId = reader.IsDBNull(2) ? (Guid?)null : reader.GetGuid(2);
                    targetRole = reader.IsDBNull(3) ? "Artist" : reader.GetString(3);
                    status = reader.IsDBNull(4) ? "Pending" : reader.GetString(4);
                    eventDate = reader.IsDBNull(5) ? (DateTime?)null : reader.GetDateTime(5);
                    eventEndTime = reader.IsDBNull(6) ? (DateTime?)null : reader.GetDateTime(6);
                    eventTitle = reader.IsDBNull(7) ? "Booking Request" : reader.GetString(7);
                    serviceFeeStatus = reader.IsDBNull(8) ? "Unpaid" : reader.GetString(8);
                    talentFeeStatus = reader.IsDBNull(9) ? "Unpaid" : reader.GetString(9);
                    platformFeeStatus = reader.IsDBNull(10) ? "Unpaid" : reader.GetString(10);
                }

                var isCustomerSide = actorUserId.Value == customerId;
                var isTargetSide = actorUserId.Value == targetUserId || actorRole.Equals(targetRole, StringComparison.OrdinalIgnoreCase);
                if (!isCustomerSide && !isTargetSide && !actorRole.Equals("Admin", StringComparison.OrdinalIgnoreCase))
                {
                    return Forbid();
                }

                var reasonCode = string.IsNullOrWhiteSpace(req?.reasonCode) ? "Other" : req!.reasonCode!.Trim();
                var notes = req?.notes?.Trim();
                var bookingEnded = (eventEndTime ?? eventDate)?.ToUniversalTime() <= DateTime.UtcNow;

                if (reasonCode.Equals("TalentNoShow", StringComparison.OrdinalIgnoreCase) && !isCustomerSide)
                {
                    return BadRequest(new { message = "Only the customer or organizer can report a talent no-show." });
                }

                if (reasonCode.Equals("CustomerNoShow", StringComparison.OrdinalIgnoreCase) && !isTargetSide)
                {
                    return BadRequest(new { message = "Only the assigned artist or sessionist can report a customer no-show." });
                }

                if ((reasonCode.Equals("TalentNoShow", StringComparison.OrdinalIgnoreCase) ||
                     reasonCode.Equals("CustomerNoShow", StringComparison.OrdinalIgnoreCase)) &&
                    bookingEnded != true)
                {
                    return BadRequest(new { message = "No-show refunds can only be requested after the booked schedule has passed." });
                }

                var result = await ProcessAutomaticBookingRefundsAsync(
                    connection,
                    bookingId,
                    reasonCode,
                    notes,
                    actorUserId,
                    actorRole,
                    refundServiceFee: isCustomerSide && (reasonCode.Equals("TalentNoShow", StringComparison.OrdinalIgnoreCase) || reasonCode.Equals("TalentCancelled", StringComparison.OrdinalIgnoreCase) || reasonCode.Equals("Other", StringComparison.OrdinalIgnoreCase)),
                    refundTalentFee: isCustomerSide && (reasonCode.Equals("TalentNoShow", StringComparison.OrdinalIgnoreCase) || reasonCode.Equals("TalentCancelled", StringComparison.OrdinalIgnoreCase) || reasonCode.Equals("Other", StringComparison.OrdinalIgnoreCase)),
                    refundPlatformFee: isTargetSide && (reasonCode.Equals("CustomerNoShow", StringComparison.OrdinalIgnoreCase) || reasonCode.Equals("CustomerCancelled", StringComparison.OrdinalIgnoreCase)));

                if (!result.AnyProcessed)
                {
                    return BadRequest(new
                    {
                        message = isCustomerSide
                            ? $"No refundable customer payment was found for {eventTitle}. Current statuses: service fee {serviceFeeStatus}, talent fee {talentFeeStatus}."
                            : $"No refundable platform fee was found for {eventTitle}. Current platform fee status: {platformFeeStatus}."
                    });
                }

                if (customerId != Guid.Empty && isTargetSide)
                {
                    await InsertNotification(connection, customerId, "booking_refund", "Booking refund requested", $"{targetRole} requested a refund review for {eventTitle}.", bookingId, "booking");
                }

                if (targetUserId != Guid.Empty && isCustomerSide)
                {
                    await InsertNotification(connection, targetUserId, "booking_refund", "Booking refund requested", $"A refund review was requested for {eventTitle}.", bookingId, "booking");
                }

                return Ok(new
                {
                    message = result.HasManualReview
                        ? "Refund request recorded. Some items need manual review because PayMongo could not complete them automatically."
                        : "Refund request processed.",
                    refundStatus = result.OverallStatus
                });
            }
            catch (Exception ex)
            {
                return StatusCode(500, new
                {
                    message = ConfigurationFallbacks.BuildSafeErrorMessage(
                        _configuration,
                        "Failed to process booking refund.",
                        ex)
                });
            }
        }

        private sealed record BookingRefundBatchResult(bool AnyProcessed, bool HasManualReview, string OverallStatus);

        private async Task<BookingRefundBatchResult> ProcessAutomaticBookingRefundsAsync(
            NpgsqlConnection connection,
            Guid bookingId,
            string reasonCode,
            string? notes,
            Guid? requesterUserId,
            string? requesterRole,
            bool refundServiceFee,
            bool refundTalentFee,
            bool refundPlatformFee)
        {
            const string sql = @"
                SELECT customer_id,
                       target_user_id,
                       COALESCE(event_title, 'Booking Request'),
                       COALESCE(service_fee_status, COALESCE(payment_status, 'Unpaid')),
                       COALESCE(talent_fee_status, 'Unpaid'),
                       COALESCE(talent_platform_fee_status, 'Unpaid'),
                       COALESCE(booking_fee, 0),
                       COALESCE(budget, 0),
                       COALESCE(talent_platform_fee, 0),
                       COALESCE(paymongo_payment_id, ''),
                       COALESCE(talent_fee_payment_id, ''),
                       COALESCE(talent_platform_fee_payment_id, ''),
                       COALESCE(paymongo_checkout_id, ''),
                       COALESCE(talent_fee_checkout_id, ''),
                       COALESCE(talent_platform_fee_checkout_id, '')
                FROM bookings
                WHERE id = @id;";

            Guid customerId;
            Guid targetUserId;
            string eventTitle;
            string serviceFeeStatus;
            string talentFeeStatus;
            string platformFeeStatus;
            decimal bookingFee;
            decimal budget;
            decimal platformFee;
            string servicePaymentId;
            string talentPaymentId;
            string platformPaymentId;
            string serviceCheckoutId;
            string talentCheckoutId;
            string platformCheckoutId;

            await using (var cmd = new NpgsqlCommand(sql, connection))
            {
                cmd.Parameters.Add("@id", NpgsqlDbType.Uuid).Value = bookingId;
                await using var reader = await cmd.ExecuteReaderAsync();
                if (!await reader.ReadAsync())
                {
                    return new BookingRefundBatchResult(false, false, "Unpaid");
                }

                customerId = reader.IsDBNull(0) ? Guid.Empty : reader.GetGuid(0);
                targetUserId = reader.IsDBNull(1) ? Guid.Empty : reader.GetGuid(1);
                eventTitle = reader.IsDBNull(2) ? "Booking Request" : reader.GetString(2);
                serviceFeeStatus = reader.IsDBNull(3) ? "Unpaid" : reader.GetString(3);
                talentFeeStatus = reader.IsDBNull(4) ? "Unpaid" : reader.GetString(4);
                platformFeeStatus = reader.IsDBNull(5) ? "Unpaid" : reader.GetString(5);
                bookingFee = reader.IsDBNull(6) ? 0 : reader.GetDecimal(6);
                budget = reader.IsDBNull(7) ? 0 : reader.GetDecimal(7);
                platformFee = reader.IsDBNull(8) ? 0 : reader.GetDecimal(8);
                servicePaymentId = reader.IsDBNull(9) ? "" : reader.GetString(9);
                talentPaymentId = reader.IsDBNull(10) ? "" : reader.GetString(10);
                platformPaymentId = reader.IsDBNull(11) ? "" : reader.GetString(11);
                serviceCheckoutId = reader.IsDBNull(12) ? "" : reader.GetString(12);
                talentCheckoutId = reader.IsDBNull(13) ? "" : reader.GetString(13);
                platformCheckoutId = reader.IsDBNull(14) ? "" : reader.GetString(14);
            }

            var anyProcessed = false;
            var hasManualReview = false;

            if (refundServiceFee && serviceFeeStatus.Equals("Paid", StringComparison.OrdinalIgnoreCase) && bookingFee > 0)
            {
                var result = await RefundBookingComponentAsync(
                    connection,
                    bookingId,
                    "booking_service_fee",
                    "service",
                    bookingFee,
                    string.IsNullOrWhiteSpace(servicePaymentId)
                        ? await PaymentRefundService.ResolveCheckoutPaymentIdAsync(_paymongoSecretKey, serviceCheckoutId)
                        : servicePaymentId,
                    requesterUserId,
                    customerId == Guid.Empty ? requesterUserId : customerId,
                    requesterRole,
                    reasonCode,
                    notes);

                anyProcessed |= result is not null;
                hasManualReview |= string.Equals(result, "Refund Pending", StringComparison.OrdinalIgnoreCase) || string.Equals(result, "Refund Failed", StringComparison.OrdinalIgnoreCase);
            }

            if (refundTalentFee &&
                (talentFeeStatus.Equals("Paid", StringComparison.OrdinalIgnoreCase) ||
                 talentFeeStatus.Equals("HeldInEscrow", StringComparison.OrdinalIgnoreCase) ||
                 talentFeeStatus.Equals("ReadyForRelease", StringComparison.OrdinalIgnoreCase)) &&
                budget > 0)
            {
                var result = await RefundBookingComponentAsync(
                    connection,
                    bookingId,
                    "booking_talent_fee",
                    "talent",
                    budget,
                    string.IsNullOrWhiteSpace(talentPaymentId)
                        ? await PaymentRefundService.ResolveCheckoutPaymentIdAsync(_paymongoSecretKey, talentCheckoutId)
                        : talentPaymentId,
                    requesterUserId,
                    customerId == Guid.Empty ? requesterUserId : customerId,
                    requesterRole,
                    reasonCode,
                    notes);

                anyProcessed |= result is not null;
                hasManualReview |= string.Equals(result, "Refund Pending", StringComparison.OrdinalIgnoreCase) || string.Equals(result, "Refund Failed", StringComparison.OrdinalIgnoreCase);
            }

            if (refundPlatformFee && platformFeeStatus.Equals("Paid", StringComparison.OrdinalIgnoreCase) && platformFee > 0)
            {
                var result = await RefundBookingComponentAsync(
                    connection,
                    bookingId,
                    "booking_talent_platform_fee",
                    "platform",
                    platformFee,
                    string.IsNullOrWhiteSpace(platformPaymentId)
                        ? await PaymentRefundService.ResolveCheckoutPaymentIdAsync(_paymongoSecretKey, platformCheckoutId)
                        : platformPaymentId,
                    requesterUserId,
                    targetUserId == Guid.Empty ? requesterUserId : targetUserId,
                    requesterRole,
                    reasonCode,
                    notes);

                anyProcessed |= result is not null;
                hasManualReview |= string.Equals(result, "Refund Pending", StringComparison.OrdinalIgnoreCase) || string.Equals(result, "Refund Failed", StringComparison.OrdinalIgnoreCase);
            }

            var overallStatus = await RecomputeBookingPaymentStatusAsync(connection, bookingId);

            if (anyProcessed)
            {
                if (customerId != Guid.Empty && (refundServiceFee || refundTalentFee))
                {
                    await InsertNotification(connection, customerId, "booking_refund", "Booking refund updated", $"Your refund status for {eventTitle} is now {overallStatus}.", bookingId, "booking");
                }

                if (targetUserId != Guid.Empty && refundPlatformFee)
                {
                    await InsertNotification(connection, targetUserId, "booking_refund", "Platform fee refund updated", $"Your platform-fee refund status for {eventTitle} is now {overallStatus}.", bookingId, "booking");
                }
            }

            return new BookingRefundBatchResult(anyProcessed, hasManualReview, overallStatus);
        }

        private async Task<string?> RefundBookingComponentAsync(
            NpgsqlConnection connection,
            Guid bookingId,
            string paymentScope,
            string componentType,
            decimal amount,
            string? paymentId,
            Guid? requesterUserId,
            Guid? beneficiaryUserId,
            string? requesterRole,
            string reasonCode,
            string? notes)
        {
            if (amount <= 0)
            {
                return null;
            }

            var pendingStatusSql = componentType == "talent"
                ? "UPDATE bookings SET talent_fee_status = 'Refund Pending', payment_status = 'Refund Pending' WHERE id = @id;"
                : componentType == "platform"
                    ? "UPDATE bookings SET talent_platform_fee_status = 'Refund Pending', payment_status = 'Refund Pending' WHERE id = @id;"
                    : "UPDATE bookings SET service_fee_status = 'Refund Pending', payment_status = 'Refund Pending' WHERE id = @id;";

            await using (var pendingCmd = new NpgsqlCommand(pendingStatusSql, connection))
            {
                pendingCmd.Parameters.Add("@id", NpgsqlDbType.Uuid).Value = bookingId;
                await pendingCmd.ExecuteNonQueryAsync();
            }

            if (componentType == "talent")
            {
                await EscrowService.MarkRefundPendingAsync(connection, bookingId);
                if (await EscrowService.IsWalletFundedAsync(connection, bookingId))
                {
                    await using var walletRefundCmd = new NpgsqlCommand(
                        "UPDATE bookings SET talent_fee_status = 'Refunded', payment_status = 'Refunded' WHERE id = @id;",
                        connection);
                    walletRefundCmd.Parameters.Add("@id", NpgsqlDbType.Uuid).Value = bookingId;
                    await walletRefundCmd.ExecuteNonQueryAsync();
                    await EscrowService.MarkRefundedAsync(connection, bookingId);
                    return "Refunded";
                }
            }

            var refundRequestId = await PaymentRefundService.CreateRefundRequestAsync(
                connection,
                refundScope: "booking",
                paymentScope: paymentScope,
                bookingId: bookingId,
                ticketId: null,
                requesterUserId: requesterUserId,
                beneficiaryUserId: beneficiaryUserId,
                requesterRole: requesterRole,
                reasonCode: reasonCode,
                reasonDetails: notes,
                amount: amount,
                providerPaymentId: paymentId,
                metadata: new { componentType });

            var refundResult = await PaymentRefundService.CreatePayMongoRefundAsync(
                _paymongoSecretKey,
                paymentId ?? string.Empty,
                amount,
                reasonCode,
                notes);

            var targetStatus = refundResult.Status == "Refunded"
                ? "Refunded"
                : refundResult.Status == "Refund Pending"
                    ? "Refund Pending"
                    : "Refund Failed";
            var bookingUpdateSql = componentType == "talent"
                ? @"
                    UPDATE bookings
                    SET talent_fee_status = @componentStatus
                    WHERE id = @id;"
                : componentType == "platform"
                    ? @"
                    UPDATE bookings
                    SET talent_platform_fee_status = @componentStatus
                    WHERE id = @id;"
                    : @"
                    UPDATE bookings
                    SET service_fee_status = @componentStatus
                    WHERE id = @id;";

            await using (var updateCmd = new NpgsqlCommand(bookingUpdateSql, connection))
            {
                updateCmd.Parameters.Add("@id", NpgsqlDbType.Uuid).Value = bookingId;
                updateCmd.Parameters.Add("@componentStatus", NpgsqlDbType.Text).Value = targetStatus;
                await updateCmd.ExecuteNonQueryAsync();
            }

            await PaymentRefundService.UpdateRefundRequestAsync(
                connection,
                refundRequestId,
                status: targetStatus == "Refunded" ? "Refunded" : targetStatus == "Refund Pending" ? "ManualReview" : "Failed",
                providerRefundId: refundResult.RefundId,
                providerStatus: refundResult.ProviderStatus,
                errorCode: refundResult.ErrorCode,
                errorMessage: refundResult.ErrorMessage);

            if (targetStatus == "Refunded")
            {
                await PaymentLedgerService.MarkRefundedAsync(connection, paymentScope, null, bookingId, "Refunded");
                if (componentType == "talent")
                {
                    await EscrowService.MarkRefundedAsync(connection, bookingId);
                }
                return "Refunded";
            }

            return targetStatus == "Failed" ? "Refund Failed" : "Refund Pending";
        }

        private static async Task<string> RecomputeBookingPaymentStatusAsync(NpgsqlConnection connection, Guid bookingId)
        {
            const string sql = @"
                SELECT COALESCE(service_fee_status, COALESCE(payment_status, 'Unpaid')),
                       COALESCE(talent_fee_status, 'Unpaid'),
                       COALESCE(talent_platform_fee_status, 'Unpaid')
                FROM bookings
                WHERE id = @id;";

            string serviceFeeStatus;
            string talentFeeStatus;
            string platformFeeStatus;

            await using (var cmd = new NpgsqlCommand(sql, connection))
            {
                cmd.Parameters.Add("@id", NpgsqlDbType.Uuid).Value = bookingId;
                await using var reader = await cmd.ExecuteReaderAsync();
                if (!await reader.ReadAsync())
                {
                    return "Unpaid";
                }

                serviceFeeStatus = reader.IsDBNull(0) ? "Unpaid" : reader.GetString(0);
                talentFeeStatus = reader.IsDBNull(1) ? "Unpaid" : reader.GetString(1);
                platformFeeStatus = reader.IsDBNull(2) ? "Unpaid" : reader.GetString(2);
            }

            string nextStatus;
            if (serviceFeeStatus.Contains("Refund", StringComparison.OrdinalIgnoreCase) ||
                talentFeeStatus.Contains("Refund", StringComparison.OrdinalIgnoreCase) ||
                platformFeeStatus.Contains("Refund", StringComparison.OrdinalIgnoreCase))
            {
                if (serviceFeeStatus.Equals("Refund Pending", StringComparison.OrdinalIgnoreCase) ||
                    talentFeeStatus.Equals("Refund Pending", StringComparison.OrdinalIgnoreCase) ||
                    platformFeeStatus.Equals("Refund Pending", StringComparison.OrdinalIgnoreCase))
                {
                    nextStatus = "Refund Pending";
                }
                else if (serviceFeeStatus.Equals("Refund Failed", StringComparison.OrdinalIgnoreCase) ||
                         talentFeeStatus.Equals("Refund Failed", StringComparison.OrdinalIgnoreCase) ||
                         platformFeeStatus.Equals("Refund Failed", StringComparison.OrdinalIgnoreCase))
                {
                    nextStatus = "Refund Failed";
                }
                else
                {
                    nextStatus = "Refunded";
                }
            }
            else if (serviceFeeStatus.Equals("Paid", StringComparison.OrdinalIgnoreCase) &&
                     (talentFeeStatus.Equals("Paid", StringComparison.OrdinalIgnoreCase) || talentFeeStatus.Equals("Unpaid", StringComparison.OrdinalIgnoreCase) || talentFeeStatus.Equals("NotRequired", StringComparison.OrdinalIgnoreCase)))
            {
                nextStatus = "Paid";
            }
            else
            {
                nextStatus = "Unpaid";
            }

            await using var updateCmd = new NpgsqlCommand("UPDATE bookings SET payment_status = @paymentStatus WHERE id = @id;", connection);
            updateCmd.Parameters.Add("@id", NpgsqlDbType.Uuid).Value = bookingId;
            updateCmd.Parameters.Add("@paymentStatus", NpgsqlDbType.Text).Value = nextStatus;
            await updateCmd.ExecuteNonQueryAsync();
            return nextStatus;
        }

        private static async Task EnsureBookingsTableExists(NpgsqlConnection connection)
        {
            const string sql = @"
                CREATE TABLE IF NOT EXISTS bookings (
                    id uuid PRIMARY KEY,
                    customer_id uuid NOT NULL,
                    target_user_id uuid NOT NULL,
                    event_id uuid NULL,
                    target_role varchar(50),
                    service_type text NULL,
                    event_title text,
                    event_date timestamptz NULL,
                    event_end_time timestamptz NULL,
                    location text,
                    budget numeric NULL,
                    booking_fee numeric NOT NULL DEFAULT 15,
                    talent_platform_fee numeric NOT NULL DEFAULT 0,
                    notes text NULL,
                    message text,
                    status varchar(60) NOT NULL DEFAULT 'Pending',
                    created_at timestamptz NOT NULL DEFAULT NOW(),
                    payment_status varchar(30) NOT NULL DEFAULT 'Unpaid',
                    service_fee_status varchar(30) NOT NULL DEFAULT 'Unpaid',
                    talent_fee_status varchar(30) NOT NULL DEFAULT 'Unpaid',
                    talent_platform_fee_status varchar(30) NOT NULL DEFAULT 'Unpaid',
                    payment_method text NULL,
                    service_fee_payment_method text NULL,
                    talent_fee_payment_method text NULL,
                    talent_platform_fee_payment_method text NULL,
                    paymongo_checkout_id text NULL,
                    paymongo_checkout_url text NULL,
                    paymongo_checkout_reference text NULL,
                    paymongo_payment_id text NULL,
                    paymongo_payment_reference text NULL,
                    talent_fee_checkout_id text NULL,
                    talent_fee_checkout_url text NULL,
                    talent_fee_checkout_reference text NULL,
                    talent_fee_payment_id text NULL,
                    talent_fee_payment_reference text NULL,
                    talent_platform_fee_checkout_id text NULL,
                    talent_platform_fee_checkout_url text NULL,
                    talent_platform_fee_checkout_reference text NULL,
                    talent_platform_fee_payment_id text NULL,
                    talent_platform_fee_payment_reference text NULL,
                    paid_at timestamptz NULL,
                    service_fee_paid_at timestamptz NULL,
                    talent_fee_paid_at timestamptz NULL,
                    talent_platform_fee_paid_at timestamptz NULL,
                    customer_completed_at timestamptz NULL,
                    target_completed_at timestamptz NULL,
                    booking_group_id uuid NULL,
                    booking_sequence integer NOT NULL DEFAULT 1,
                    updated_at timestamptz NOT NULL DEFAULT NOW()
                );";

            using var cmd = new NpgsqlCommand(sql, connection);
            await cmd.ExecuteNonQueryAsync();

            const string alterSql = @"
                ALTER TABLE bookings ADD COLUMN IF NOT EXISTS service_type text NULL;
                ALTER TABLE bookings ADD COLUMN IF NOT EXISTS event_id uuid NULL;
                ALTER TABLE bookings ADD COLUMN IF NOT EXISTS event_end_time timestamptz NULL;
                ALTER TABLE bookings ADD COLUMN IF NOT EXISTS booking_fee numeric NOT NULL DEFAULT 15;
                ALTER TABLE bookings ADD COLUMN IF NOT EXISTS talent_platform_fee numeric NOT NULL DEFAULT 0;
                ALTER TABLE bookings ADD COLUMN IF NOT EXISTS notes text NULL;
                ALTER TABLE bookings ADD COLUMN IF NOT EXISTS payment_status varchar(30) NOT NULL DEFAULT 'Unpaid';
                ALTER TABLE bookings ADD COLUMN IF NOT EXISTS service_fee_status varchar(30) NOT NULL DEFAULT 'Unpaid';
                ALTER TABLE bookings ADD COLUMN IF NOT EXISTS talent_fee_status varchar(30) NOT NULL DEFAULT 'Unpaid';
                ALTER TABLE bookings ADD COLUMN IF NOT EXISTS talent_platform_fee_status varchar(30) NOT NULL DEFAULT 'Unpaid';
                ALTER TABLE bookings ADD COLUMN IF NOT EXISTS payment_method text NULL;
                ALTER TABLE bookings ADD COLUMN IF NOT EXISTS service_fee_payment_method text NULL;
                ALTER TABLE bookings ADD COLUMN IF NOT EXISTS talent_fee_payment_method text NULL;
                ALTER TABLE bookings ADD COLUMN IF NOT EXISTS talent_platform_fee_payment_method text NULL;
                ALTER TABLE bookings ADD COLUMN IF NOT EXISTS paymongo_checkout_id text NULL;
                ALTER TABLE bookings ADD COLUMN IF NOT EXISTS paymongo_checkout_url text NULL;
                ALTER TABLE bookings ADD COLUMN IF NOT EXISTS paymongo_checkout_reference text NULL;
                ALTER TABLE bookings ADD COLUMN IF NOT EXISTS paymongo_payment_id text NULL;
                ALTER TABLE bookings ADD COLUMN IF NOT EXISTS paymongo_payment_reference text NULL;
                ALTER TABLE bookings ADD COLUMN IF NOT EXISTS talent_fee_checkout_id text NULL;
                ALTER TABLE bookings ADD COLUMN IF NOT EXISTS talent_fee_checkout_url text NULL;
                ALTER TABLE bookings ADD COLUMN IF NOT EXISTS talent_fee_checkout_reference text NULL;
                ALTER TABLE bookings ADD COLUMN IF NOT EXISTS talent_fee_payment_id text NULL;
                ALTER TABLE bookings ADD COLUMN IF NOT EXISTS talent_fee_payment_reference text NULL;
                ALTER TABLE bookings ADD COLUMN IF NOT EXISTS talent_platform_fee_checkout_id text NULL;
                ALTER TABLE bookings ADD COLUMN IF NOT EXISTS talent_platform_fee_checkout_url text NULL;
                ALTER TABLE bookings ADD COLUMN IF NOT EXISTS talent_platform_fee_checkout_reference text NULL;
                ALTER TABLE bookings ADD COLUMN IF NOT EXISTS talent_platform_fee_payment_id text NULL;
                ALTER TABLE bookings ADD COLUMN IF NOT EXISTS talent_platform_fee_payment_reference text NULL;
                ALTER TABLE bookings ADD COLUMN IF NOT EXISTS paid_at timestamptz NULL;
                ALTER TABLE bookings ADD COLUMN IF NOT EXISTS service_fee_paid_at timestamptz NULL;
                ALTER TABLE bookings ADD COLUMN IF NOT EXISTS talent_fee_paid_at timestamptz NULL;
                ALTER TABLE bookings ADD COLUMN IF NOT EXISTS talent_platform_fee_paid_at timestamptz NULL;
                ALTER TABLE bookings ADD COLUMN IF NOT EXISTS customer_completed_at timestamptz NULL;
                ALTER TABLE bookings ADD COLUMN IF NOT EXISTS target_completed_at timestamptz NULL;
                ALTER TABLE bookings ADD COLUMN IF NOT EXISTS booking_group_id uuid NULL;
                ALTER TABLE bookings ADD COLUMN IF NOT EXISTS booking_sequence integer NOT NULL DEFAULT 1;
                ALTER TABLE bookings ADD COLUMN IF NOT EXISTS conversation_closed_at timestamptz NULL;
                ALTER TABLE bookings ADD COLUMN IF NOT EXISTS conversation_closed_by_user_id uuid NULL;
                ALTER TABLE bookings ADD COLUMN IF NOT EXISTS conversation_close_reason text NOT NULL DEFAULT '';
                ALTER TABLE bookings ADD COLUMN IF NOT EXISTS dispute_hold boolean NOT NULL DEFAULT FALSE;
                ALTER TABLE bookings ADD COLUMN IF NOT EXISTS escrow_transaction_id uuid NULL;
                ALTER TABLE bookings ADD COLUMN IF NOT EXISTS escrow_status varchar(40) NULL;
                ALTER TABLE bookings ADD COLUMN IF NOT EXISTS updated_at timestamptz NOT NULL DEFAULT NOW();
                UPDATE bookings SET updated_at = COALESCE(updated_at, created_at, NOW()) WHERE updated_at IS NULL;
                ALTER TABLE bookings ALTER COLUMN status TYPE varchar(60);";

            using var alterCmd = new NpgsqlCommand(alterSql, connection);
            await alterCmd.ExecuteNonQueryAsync();
        }

        private static async Task EnsureNotificationsTableExists(NpgsqlConnection connection)
        {
            const string sql = @"
                CREATE TABLE IF NOT EXISTS notifications (
                    id uuid PRIMARY KEY,
                    user_id uuid NOT NULL,
                    type varchar(60) NOT NULL,
                    title varchar(150) NOT NULL,
                    message text NOT NULL,
                    related_id uuid NULL,
                    related_type varchar(50) NULL,
                    is_read boolean NOT NULL DEFAULT FALSE,
                    created_at timestamptz NOT NULL DEFAULT NOW()
                );";

            using var cmd = new NpgsqlCommand(sql, connection);
            await cmd.ExecuteNonQueryAsync();
        }

        private static async Task EnsureEventBookingSupportExists(NpgsqlConnection connection)
        {
            const string sql = @"
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

        private static async Task InsertNotification(
            NpgsqlConnection connection,
            Guid userId,
            string type,
            string title,
            string message,
            Guid? relatedId = null,
            string? relatedType = null)
        {
            const string sql = @"
                INSERT INTO notifications (id, user_id, type, title, message, related_id, related_type, is_read, created_at)
                VALUES (@id, @userId, @type, @title, @message, @relatedId, @relatedType, FALSE, NOW());";

            using var cmd = new NpgsqlCommand(sql, connection);
            cmd.Parameters.Add("@id", NpgsqlDbType.Uuid).Value = Guid.NewGuid();
            cmd.Parameters.Add("@userId", NpgsqlDbType.Uuid).Value = userId;
            cmd.Parameters.Add("@type", NpgsqlDbType.Text).Value = type;
            cmd.Parameters.Add("@title", NpgsqlDbType.Text).Value = title;
            cmd.Parameters.Add("@message", NpgsqlDbType.Text).Value = message;
            cmd.Parameters.Add("@relatedId", NpgsqlDbType.Uuid).Value = (object?)relatedId ?? DBNull.Value;
            cmd.Parameters.Add("@relatedType", NpgsqlDbType.Text).Value = (object?)relatedType ?? DBNull.Value;
            await cmd.ExecuteNonQueryAsync();
        }

        private static async Task SyncBookingIntoEventLineup(
            NpgsqlConnection connection,
            Guid bookingId,
            Guid eventId,
            Guid targetUserId,
            string targetRole)
        {
            await EnsureEventBookingSupportExists(connection);

            const string userSql = @"
                SELECT COALESCE(stagename, ''),
                       COALESCE(firstname, ''),
                       COALESCE(lastname, ''),
                       COALESCE(profile_picture, '')
                FROM users
                WHERE id = @id;";

            string displayName = "";
            string profilePicture = "";
            using (var userCmd = new NpgsqlCommand(userSql, connection))
            {
                userCmd.Parameters.Add("@id", NpgsqlDbType.Uuid).Value = targetUserId;
                using var reader = await userCmd.ExecuteReaderAsync();
                if (await reader.ReadAsync())
                {
                    var stageName = reader.IsDBNull(0) ? "" : reader.GetString(0);
                    var firstName = reader.IsDBNull(1) ? "" : reader.GetString(1);
                    var lastName = reader.IsDBNull(2) ? "" : reader.GetString(2);
                    profilePicture = reader.IsDBNull(3) ? "" : reader.GetString(3);
                    displayName = !string.IsNullOrWhiteSpace(stageName) ? stageName : $"{firstName} {lastName}".Trim();
                }
            }

            if (string.IsNullOrWhiteSpace(displayName))
            {
                displayName = targetRole;
            }

            if (string.Equals(targetRole, "Sessionist", StringComparison.OrdinalIgnoreCase))
            {
                const string linkSql = @"
                    INSERT INTO event_sessionists (id, event_id, sessionist_user_id, booking_id, status, created_at)
                    VALUES (@id, @eventId, @userId, @bookingId, 'Confirmed', NOW())
                    ON CONFLICT (event_id, sessionist_user_id)
                    DO UPDATE SET booking_id = EXCLUDED.booking_id, status = 'Confirmed';";

                using var linkCmd = new NpgsqlCommand(linkSql, connection);
                linkCmd.Parameters.Add("@id", NpgsqlDbType.Uuid).Value = Guid.NewGuid();
                linkCmd.Parameters.Add("@eventId", NpgsqlDbType.Uuid).Value = eventId;
                linkCmd.Parameters.Add("@userId", NpgsqlDbType.Uuid).Value = targetUserId;
                linkCmd.Parameters.Add("@bookingId", NpgsqlDbType.Uuid).Value = bookingId;
                await linkCmd.ExecuteNonQueryAsync();
            }
            else
            {
                const string linkSql = @"
                    INSERT INTO event_artists (id, event_id, artist_user_id, booking_id, status, created_at)
                    VALUES (@id, @eventId, @userId, @bookingId, 'Confirmed', NOW())
                    ON CONFLICT (event_id, artist_user_id)
                    DO UPDATE SET booking_id = EXCLUDED.booking_id, status = 'Confirmed';";

                using var linkCmd = new NpgsqlCommand(linkSql, connection);
                linkCmd.Parameters.Add("@id", NpgsqlDbType.Uuid).Value = Guid.NewGuid();
                linkCmd.Parameters.Add("@eventId", NpgsqlDbType.Uuid).Value = eventId;
                linkCmd.Parameters.Add("@userId", NpgsqlDbType.Uuid).Value = targetUserId;
                linkCmd.Parameters.Add("@bookingId", NpgsqlDbType.Uuid).Value = bookingId;
                await linkCmd.ExecuteNonQueryAsync();
            }

            var isSessionist = string.Equals(targetRole, "Sessionist", StringComparison.OrdinalIgnoreCase);
            var roleName = isSessionist ? "Sessionist" : "Artist";

            string existingJson = "[]";
            string eventTitle = "This event";
            const string sessionistLineupSql = "SELECT COALESCE(sessionist_lineup, '[]'), COALESCE(title, 'This event') FROM events WHERE id = @id;";
            const string artistLineupSql = "SELECT COALESCE(artist_lineup, '[]'), COALESCE(title, 'This event') FROM events WHERE id = @id;";
            using (var lineupCmd = new NpgsqlCommand(isSessionist ? sessionistLineupSql : artistLineupSql, connection))
            {
                lineupCmd.Parameters.Add("@id", NpgsqlDbType.Uuid).Value = eventId;
                using var reader = await lineupCmd.ExecuteReaderAsync();
                if (await reader.ReadAsync())
                {
                    existingJson = reader.IsDBNull(0) ? "[]" : reader.GetString(0);
                    eventTitle = reader.IsDBNull(1) ? "This event" : reader.GetString(1);
                }
            }

            List<TalentLineupItemDto> lineup;
            try
            {
                lineup = JsonSerializer.Deserialize<List<TalentLineupItemDto>>(existingJson, new JsonSerializerOptions
                {
                    PropertyNameCaseInsensitive = true
                }) ?? new List<TalentLineupItemDto>();
            }
            catch
            {
                lineup = new List<TalentLineupItemDto>();
            }

            if (lineup.Any(item => item.id == targetUserId))
            {
                return;
            }

            lineup.Add(new TalentLineupItemDto
            {
                id = targetUserId,
                displayName = displayName,
                role = roleName,
                profilePicture = string.IsNullOrWhiteSpace(profilePicture) ? null : profilePicture
            });

            const string updateSessionistLineupSql = "UPDATE events SET sessionist_lineup = @json WHERE id = @eventId;";
            const string updateArtistLineupSql = "UPDATE events SET artist_lineup = @json WHERE id = @eventId;";
            using var updateCmd = new NpgsqlCommand(isSessionist ? updateSessionistLineupSql : updateArtistLineupSql, connection);
            updateCmd.Parameters.Add("@json", NpgsqlDbType.Text).Value = JsonSerializer.Serialize(lineup);
            updateCmd.Parameters.Add("@eventId", NpgsqlDbType.Uuid).Value = eventId;
            await updateCmd.ExecuteNonQueryAsync();

            await InsertNotification(
                connection,
                targetUserId,
                "lineup_added",
                "Added to event lineup",
                $"You were added to the lineup for '{eventTitle}'.",
                eventId,
                "event");
        }

        private static async Task EnsureBookingMessagesTableExists(NpgsqlConnection connection)
        {
            const string sql = @"
                CREATE TABLE IF NOT EXISTS booking_messages (
                    id uuid PRIMARY KEY,
                    booking_id uuid NOT NULL,
                    sender_id uuid NOT NULL,
                    receiver_id uuid NOT NULL,
                    message_text text NOT NULL,
                    created_at timestamptz NOT NULL DEFAULT NOW()
                );";

            using var cmd = new NpgsqlCommand(sql, connection);
            await cmd.ExecuteNonQueryAsync();
        }

        private static DateTime? NormalizeToUtc(DateTime? value)
        {
            if (!value.HasValue)
            {
                return null;
            }

            return value.Value.Kind switch
            {
                DateTimeKind.Utc => value.Value,
                DateTimeKind.Local => value.Value.ToUniversalTime(),
                _ => DateTime.SpecifyKind(value.Value, DateTimeKind.Local).ToUniversalTime()
            };
        }

        private static string NormalizeTargetRole(string? role)
        {
            var normalized = (role ?? "").Trim().ToLowerInvariant();
            return normalized == "sessionist" ? "Sessionist" : "Artist";
        }

        private static async Task<(string first, string last)> GetUserNameAsync(NpgsqlConnection connection, Guid userId)
        {
            const string sql = "SELECT COALESCE(firstname,''), COALESCE(lastname,'') FROM users WHERE id=@id LIMIT 1;";
            await using var cmd = new NpgsqlCommand(sql, connection);
            cmd.Parameters.AddWithValue("@id", userId);
            await using var reader = await cmd.ExecuteReaderAsync();
            if (await reader.ReadAsync()) return (reader.GetString(0), reader.GetString(1));
            return ("", "");
        }

        private static string FormatPeso(decimal amount)
        {
            return $"PHP {amount:0.00}";
        }

        private static string GetPayMongoErrorMessage(string responseString, string fallbackMessage)
        {
            if (string.IsNullOrWhiteSpace(responseString))
            {
                return fallbackMessage;
            }

            try
            {
                using var doc = JsonDocument.Parse(responseString);
                var root = doc.RootElement;

                if (root.TryGetProperty("errors", out var errors) &&
                    errors.ValueKind == JsonValueKind.Array &&
                    errors.GetArrayLength() > 0)
                {
                    var firstError = errors[0];

                    if (firstError.TryGetProperty("detail", out var detail) &&
                        detail.ValueKind == JsonValueKind.String &&
                        !string.IsNullOrWhiteSpace(detail.GetString()))
                    {
                        return detail.GetString()!;
                    }

                    if (firstError.TryGetProperty("code", out var code) &&
                        code.ValueKind == JsonValueKind.String &&
                        !string.IsNullOrWhiteSpace(code.GetString()))
                    {
                        return $"PayMongo error: {code.GetString()}";
                    }
                }
            }
            catch
            {
            }

            return fallbackMessage;
        }

        private static string GetPayMongoHttpErrorMessage(int statusCode, string responseString, string fallbackMessage)
        {
            if (statusCode == StatusCodes.Status401Unauthorized)
            {
                return "PayMongo rejected the configured secret key. Use the exact test secret key for local checkout.";
            }

            return GetPayMongoErrorMessage(responseString, fallbackMessage);
        }
    }
}
