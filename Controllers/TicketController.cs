using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Authorization;
using Npgsql;
using System.Net.Http.Headers;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using ImajinationAPI.Services;

namespace ImajinationAPI.Controllers
{
    public class CheckoutRequest
    {
        public string eventId { get; set; } = string.Empty;
        public string customerId { get; set; } = string.Empty;
        public string tierName { get; set; } = string.Empty;
        public int quantity { get; set; }
        public decimal totalPrice { get; set; }
        public string successUrl { get; set; } = string.Empty;
        public string cancelUrl { get; set; } = string.Empty;
    }

    public class ScanTicketRequest
    {
        public string? ticketValue { get; set; }
    }

    public class CreateTicketRefundRequest
    {
        public string? reasonCode { get; set; }
        public string? notes { get; set; }
    }

    internal sealed record ParsedTicketScan(Guid TicketId, int? UnitNumber);

    internal sealed record TicketPricingPhase(
        string Name,
        decimal Multiplier,
        int PercentageMarkup,
        string BadgeTone,
        string Description);

    [Route("api/[controller]")]
    [ApiController]
    public class TicketController : ControllerBase
    {
        private readonly string _connectionString;
        private readonly string _paymongoSecretKey;
        private readonly IConfiguration _configuration;
        private readonly ImajinationAPI.Services.EmailService _emailService;
        private readonly ImajinationAPI.Services.TicketPdfService _ticketPdfService;
        private readonly ImajinationAPI.Hubs.EventScanBroadcaster _scanBroadcaster;
        private const decimal PayMongoMinimumAmount = 20m;
        private const decimal TicketServiceFeeRate = 0.05m;
        private const decimal PlatformFeePerTicket = 10m;  // ₱10 platform revenue per ticket sold

        public TicketController(IConfiguration configuration, ImajinationAPI.Services.EmailService emailService, ImajinationAPI.Services.TicketPdfService ticketPdfService, ImajinationAPI.Hubs.EventScanBroadcaster scanBroadcaster)
        {
            _configuration = configuration;
            _connectionString = ConfigurationFallbacks.GetRequiredSupabaseConnectionString(configuration);
            _paymongoSecretKey = configuration["PayMongo:SecretKey"] ?? string.Empty;
            _emailService = emailService;
            _ticketPdfService = ticketPdfService;
            _scanBroadcaster = scanBroadcaster;
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

        [HttpGet("event-checkout/{id}")]
        public async Task<IActionResult> GetEventForCheckout(Guid id)
        {
            try
            {
                await using var connection = new NpgsqlConnection(_connectionString);
                await connection.OpenAsync();
                await PlatformFeatureSupport.EnsureSharedBusinessSchemaAsync(connection);
                // Ensure event_tiers table exists
                try {
                    await using var ensureCmd = new NpgsqlCommand(
                        @"CREATE TABLE IF NOT EXISTS event_tiers (
                            id uuid PRIMARY KEY DEFAULT gen_random_uuid(),
                            event_id uuid NOT NULL, name varchar(120) NOT NULL,
                            description text NULL, color varchar(20) NULL,
                            price decimal(12,2) NOT NULL DEFAULT 0,
                            total_slots integer NOT NULL DEFAULT 0,
                            slots_sold integer NOT NULL DEFAULT 0,
                            sort_order integer NOT NULL DEFAULT 0,
                            created_at timestamptz NOT NULL DEFAULT NOW(),
                            UNIQUE (event_id, name));", connection);
                    await ensureCmd.ExecuteNonQueryAsync();
                } catch { /* ignore if already exists */ }

                string sql = @"
                    SELECT title,
                           poster_url,
                           organizer_id,
                           base_price,
                           tier_name,
                           tier_price,
                           total_slots,
                           tickets_sold,
                           bundles,
                           event_time,
                           sale_name,
                           sale_type,
                           sale_value,
                           sale_starts_at,
                           sale_ends_at,
                           COALESCE(max_tickets_per_customer, 5),
                           sale_quantity_limit,
                           COALESCE(sale_quantity_used, 0)
                    FROM events
                    WHERE id = @id";
                using var cmd = new NpgsqlCommand(sql, connection);
                cmd.Parameters.AddWithValue("@id", id);

                using var reader = await cmd.ExecuteReaderAsync();
                if (await reader.ReadAsync())
                {
                    var organizerId = reader.IsDBNull(2) ? Guid.Empty : reader.GetGuid(2);
                    var basePrice = reader.IsDBNull(3) ? 0 : reader.GetDecimal(3);
                    var tierPrice = reader.IsDBNull(5) ? null : (decimal?)reader.GetDecimal(5);
                    var eventTime = reader.IsDBNull(9) ? DateTime.UtcNow : reader.GetDateTime(9);
                    var saleName = reader.IsDBNull(10) ? null : reader.GetString(10);
                    var saleType = reader.IsDBNull(11) ? null : reader.GetString(11);
                    var saleValue = reader.IsDBNull(12) ? null : (decimal?)reader.GetDecimal(12);
                    var saleStartsAt = reader.IsDBNull(13) ? null : (DateTime?)reader.GetDateTime(13);
                    var saleEndsAt = reader.IsDBNull(14) ? null : (DateTime?)reader.GetDateTime(14);
                    var maxTicketsPerCustomer = reader.IsDBNull(15) ? 5 : Math.Clamp(reader.GetInt32(15), 3, 10);
                    var saleQuantityLimitCheckout = reader.IsDBNull(16) ? null : (int?)reader.GetInt32(16);
                    var saleQuantityUsedCheckout = reader.IsDBNull(17) ? 0 : reader.GetInt32(17);
                    var saleActive = IsSaleActive(saleStartsAt, saleEndsAt);
                    // If promo limit is set and reached, treat sale as inactive for display
                    if (saleActive && saleQuantityLimitCheckout.HasValue && saleQuantityUsedCheckout >= saleQuantityLimitCheckout.Value)
                    {
                        saleActive = false;
                    }
                    var pricingPhase = GetTicketPricingPhase(eventTime);
                    var adjustedBasePrice = ApplyPhaseMarkup(ApplySale(basePrice, saleType, saleValue, saleActive), pricingPhase);
                    decimal? adjustedTierPrice = tierPrice.HasValue
                        ? ApplyPhaseMarkup(ApplySale(tierPrice.Value, saleType, saleValue, saleActive), pricingPhase)
                        : null;
                    var bundleTiers = BuildBundleTierPayload(
                        reader.IsDBNull(8) ? null : reader.GetString(8),
                        saleType, saleValue, saleActive, pricingPhase);

                    // Capture all remaining columns before closing reader
                    var evTitle     = reader.GetString(0);
                    var evPosterUrl = reader.IsDBNull(1) ? "https://images.unsplash.com/photo-1492684223066-81342ee5ff30" : reader.GetString(1);
                    var evTierName  = reader.IsDBNull(4) ? null : reader.GetString(4);
                    var totalSlotsVal = reader.IsDBNull(6) ? 0 : reader.GetInt32(6);
                    var ticketsSoldVal = reader.IsDBNull(7) ? 0 : reader.GetInt32(7);
                    var evBundles   = reader.IsDBNull(8) ? null : reader.GetString(8);
                    await reader.CloseAsync();

                    // Fetch tiers with LIVE slot counts from confirmed tickets
                    var tiersData = new List<object>();
                    try {
                        const string tiersSql = @"
                            SELECT et.name, et.description, et.color, et.price, et.total_slots,
                                   COALESCE((
                                       SELECT SUM(t.quantity) FROM tickets t
                                       WHERE t.event_id = et.event_id
                                         AND LOWER(COALESCE(t.tier_name,'')) = LOWER(et.name)
                                         AND t.payment_method NOT IN ('AwaitingPayment')
                                   ), 0) AS slots_sold,
                                   et.sort_order
                            FROM event_tiers et WHERE et.event_id = @eid ORDER BY et.sort_order ASC;";
                        await using var tiersCmd = new NpgsqlCommand(tiersSql, connection);
                        tiersCmd.Parameters.AddWithValue("@eid", id);
                        await using var tiersRdr = await tiersCmd.ExecuteReaderAsync();
                        while (await tiersRdr.ReadAsync())
                        {
                            var tprice = tiersRdr.IsDBNull(3) ? 0m : tiersRdr.GetDecimal(3);
                            var tSlots = tiersRdr.IsDBNull(4) ? 0 : tiersRdr.GetInt32(4);
                            var tSold  = Convert.ToInt32(tiersRdr.IsDBNull(5) ? 0L : tiersRdr.GetValue(5));
                            var dispTierPrice = ApplyPhaseMarkup(ApplySale(tprice, saleType, saleValue, saleActive), pricingPhase);
                            tiersData.Add(new {
                                name = tiersRdr.IsDBNull(0) ? "Tier" : tiersRdr.GetString(0),
                                description = tiersRdr.IsDBNull(1) ? null : tiersRdr.GetString(1),
                                color = tiersRdr.IsDBNull(2) ? "#e53e3e" : tiersRdr.GetString(2),
                                price = tprice,
                                displayPrice = dispTierPrice,
                                totalSlots = tSlots,
                                slotsSold = tSold
                            });
                        }
                    } catch { /* fallback: empty tiers */ }

                    return Ok(new
                    {
                        title = evTitle,
                        posterUrl = evPosterUrl,
                        organizerId,
                        basePrice,
                        displayBasePrice = adjustedBasePrice,
                        tierName = evTierName,
                        tierPrice,
                        displayTierPrice = adjustedTierPrice,
                        slots = totalSlotsVal,
                        totalSlots = totalSlotsVal,  // alias: ev.totalSlots works in checkout fallback
                        ticketsSold = ticketsSoldVal,
                        bundles = evBundles,
                        time = eventTime,
                        saleName,
                        saleType,
                        saleValue,
                        saleStartsAt,
                        saleEndsAt,
                        saleActive,
                        pricingPhase = pricingPhase.Name,
                        pricingPhasePercentage = pricingPhase.PercentageMarkup,
                        pricingPhaseTone = pricingPhase.BadgeTone,
                        pricingPhaseDescription = pricingPhase.Description,
                        pricingMultiplier = pricingPhase.Multiplier,
                        maxTicketsPerCustomer,
                        ticketServiceFeeRate = TicketServiceFeeRate,
                        ticketServiceFeeLabel = $"Ticket Service Fee ({TicketServiceFeeRate * 100:0}%)",
                        platformFeePerTicket = PlatformFeePerTicket,
                        bundleTiers,
                        saleQuantityLimit = saleQuantityLimitCheckout,
                        saleQuantityRemaining = saleQuantityLimitCheckout.HasValue
                            ? Math.Max(0, saleQuantityLimitCheckout.Value - saleQuantityUsedCheckout)
                            : (int?)null,
                        tiers = tiersData  // new: actual tier list from event_tiers table
                    });
                }
                return NotFound(new { message = "Event not found." });
            }
            catch (Exception ex) { return StatusCode(500, new { message = "DB Fetch Error: " + ex.Message }); }
        }

        [HttpPost("checkout")]
        [Authorize]
        public async Task<IActionResult> Checkout([FromBody] CheckoutRequest req)
        {
            try
            {
                var actorUserIdClaim = User.FindFirstValue(ClaimTypes.NameIdentifier);
                if (!Guid.TryParse(actorUserIdClaim, out var parsedCustomerId) || parsedCustomerId == Guid.Empty)
                {
                    return Unauthorized(new { message = "Sign in again before starting checkout." });
                }

                var actorRole = User.FindFirstValue(ClaimTypes.Role) ?? string.Empty;

                if (!Guid.TryParse(req.eventId, out var parsedEventId) || parsedEventId == Guid.Empty)
                {
                    return BadRequest(new { message = "A valid event is required before checkout can start." });
                }

                if (!string.IsNullOrWhiteSpace(req.customerId) &&
                    Guid.TryParse(req.customerId, out var requestedCustomerId) &&
                    requestedCustomerId != parsedCustomerId)
                {
                    return Forbid();
                }

                if (req.totalPrice < 20)
                {
                    return BadRequest(new { message = "PayMongo requires a minimum amount of ₱20.00." });
                }

                await using var connection = new NpgsqlConnection(_connectionString);
                await connection.OpenAsync();
                await PlatformFeatureSupport.EnsureSharedBusinessSchemaAsync(connection);
                await EnsureTicketPaymentColumnsExist(connection);
                await PaymentLedgerService.EnsureSchemaAsync(connection);
                await NotificationSupport.EnsureNotificationsTableExistsAsync(connection);

                string eventTitle = "Imajination Ticket";
                Guid organizerId = Guid.Empty;
                decimal basePrice = 0m;
                string? primaryTierName = null;
                decimal? primaryTierPrice = null;
                string? bundles = null;
                DateTime eventTime = DateTime.UtcNow;
                string? saleType = null;
                decimal? saleValue = null;
                DateTime? saleStartsAt = null;
                DateTime? saleEndsAt = null;
                int maxTicketsPerCustomer = 5;
                int? saleQuantityLimit = null;
                int saleQuantityUsed = 0;
                string getTitleSql = @"
                    SELECT title, organizer_id, base_price, tier_name, tier_price, bundles, event_time, sale_type, sale_value, sale_starts_at, sale_ends_at, COALESCE(max_tickets_per_customer, 5),
                           sale_quantity_limit, COALESCE(sale_quantity_used, 0)
                    FROM events
                    WHERE id = @id";
                using (var titleCmd = new NpgsqlCommand(getTitleSql, connection))
                {
                    titleCmd.Parameters.AddWithValue("@id", parsedEventId);
                    using var reader = await titleCmd.ExecuteReaderAsync();
                    if (await reader.ReadAsync())
                    {
                        if (!reader.IsDBNull(0)) eventTitle = reader.GetString(0);
                        if (!reader.IsDBNull(1)) organizerId = reader.GetGuid(1);
                        basePrice = reader.IsDBNull(2) ? 0m : reader.GetDecimal(2);
                        primaryTierName = reader.IsDBNull(3) ? null : reader.GetString(3);
                        primaryTierPrice = reader.IsDBNull(4) ? null : reader.GetDecimal(4);
                        bundles = reader.IsDBNull(5) ? null : reader.GetString(5);
                        eventTime = reader.IsDBNull(6) ? DateTime.UtcNow : reader.GetDateTime(6);
                        saleType = reader.IsDBNull(7) ? null : reader.GetString(7);
                        saleValue = reader.IsDBNull(8) ? null : reader.GetDecimal(8);
                        saleStartsAt = reader.IsDBNull(9) ? null : reader.GetDateTime(9);
                        saleEndsAt = reader.IsDBNull(10) ? null : reader.GetDateTime(10);
                        maxTicketsPerCustomer = reader.IsDBNull(11) ? 5 : Math.Clamp(reader.GetInt32(11), 3, 10);
                        saleQuantityLimit = reader.IsDBNull(12) ? null : (int?)reader.GetInt32(12);
                        saleQuantityUsed = reader.IsDBNull(13) ? 0 : reader.GetInt32(13);
                    }
                    else
                    {
                        return NotFound(new { message = "Event not found." });
                    }
                }

                if (organizerId != Guid.Empty && organizerId == parsedCustomerId)
                {
                    return BadRequest(new { message = "Event organizers cannot buy tickets for their own event." });
                }

                if (req.quantity < 1 || req.quantity > maxTicketsPerCustomer)
                {
                    return BadRequest(new { message = $"This event allows up to {maxTicketsPerCustomer} tickets per customer." });
                }

                // Clean up stale AwaitingPayment tickets for this customer+event before checking limit
                const string cleanupSql = @"
                    DELETE FROM tickets
                    WHERE event_id = @eventId
                      AND customer_id = @customerId
                      AND payment_method = 'AwaitingPayment'
                      AND created_at < NOW() - INTERVAL '2 hours';";
                await using (var cleanupCmd = new NpgsqlCommand(cleanupSql, connection))
                {
                    cleanupCmd.Parameters.AddWithValue("@eventId", parsedEventId);
                    cleanupCmd.Parameters.AddWithValue("@customerId", parsedCustomerId);
                    await cleanupCmd.ExecuteNonQueryAsync();
                }

                // Only count tickets that have been paid (not AwaitingPayment)
                const string personalLimitSql = @"
                    SELECT COALESCE(SUM(quantity), 0)
                    FROM tickets
                    WHERE event_id = @eventId
                      AND customer_id = @customerId
                      AND payment_method NOT IN ('AwaitingPayment');";
                await using (var personalLimitCmd = new NpgsqlCommand(personalLimitSql, connection))
                {
                    personalLimitCmd.Parameters.AddWithValue("@eventId", parsedEventId);
                    personalLimitCmd.Parameters.AddWithValue("@customerId", parsedCustomerId);
                    var existingCount = Convert.ToInt32(await personalLimitCmd.ExecuteScalarAsync() ?? 0);
                    if (existingCount + req.quantity > maxTicketsPerCustomer)
                    {
                        return BadRequest(new
                        {
                            message = $"You already have {existingCount} paid ticket(s) for this event. The organizer limit is {maxTicketsPerCustomer} per customer."
                        });
                    }
                }

                var normalizedTierName = string.IsNullOrWhiteSpace(req.tierName) ? "General Admission" : req.tierName.Trim();
                var saleActive = IsSaleActive(saleStartsAt, saleEndsAt);

                // Enforce promo ticket quantity limit
                if (saleActive && saleQuantityLimit.HasValue)
                {
                    var promoRemaining = saleQuantityLimit.Value - saleQuantityUsed;
                    if (promoRemaining <= 0)
                    {
                        // Limit fully exhausted — revert to full price
                        saleActive = false;
                    }
                    else if (req.quantity > promoRemaining)
                    {
                        // Can't split a purchase between promo and non-promo — block and tell customer
                        return BadRequest(new
                        {
                            message = $"Only {promoRemaining} promo ticket{(promoRemaining == 1 ? "" : "s")} remaining. Please reduce your quantity to {promoRemaining} or fewer to get the promo price."
                        });
                    }
                }

                // Track whether this ticket was purchased at the promo/sale price
                var promoApplied = saleActive && saleQuantityLimit.HasValue;

                var pricingPhase = GetTicketPricingPhase(eventTime);
                var unitPrice = ResolveTierPrice(normalizedTierName, basePrice, primaryTierName, primaryTierPrice, bundles, saleType, saleValue, saleActive, pricingPhase);
                var subtotal = decimal.Round(unitPrice * req.quantity, 2);
                var serviceFee = decimal.Round(subtotal * TicketServiceFeeRate, 2);
                var platformFee = PlatformFeePerTicket * req.quantity;  // ₱10 per ticket — platform revenue
                var finalTotal = subtotal + serviceFee + platformFee;

                string insertSql = @"
                    INSERT INTO tickets (
                        id, event_id, customer_id, tier_name, quantity, total_price, payment_method,
                        ticket_unit_price, ticket_subtotal, ticket_service_fee, ticket_service_fee_rate,
                        pricing_phase_name, pricing_phase_percentage, promo_applied, platform_fee
                    )
                    VALUES (
                        @id, @eId, @cId, @tier, @qty, @total, 'AwaitingPayment',
                        @unitPrice, @subtotal, @serviceFee, @serviceFeeRate,
                        @phaseName, @phasePercentage, @promoApplied, @platformFee
                    ) RETURNING id";

                using var insertCmd = new NpgsqlCommand(insertSql, connection);
                insertCmd.Parameters.AddWithValue("@id", Guid.NewGuid());
                insertCmd.Parameters.AddWithValue("@eId", parsedEventId);
                insertCmd.Parameters.AddWithValue("@cId", parsedCustomerId);
                insertCmd.Parameters.AddWithValue("@tier", normalizedTierName);
                insertCmd.Parameters.AddWithValue("@qty", req.quantity);
                insertCmd.Parameters.AddWithValue("@total", finalTotal);
                insertCmd.Parameters.AddWithValue("@unitPrice", unitPrice);
                insertCmd.Parameters.AddWithValue("@subtotal", subtotal);
                insertCmd.Parameters.AddWithValue("@serviceFee", serviceFee);
                insertCmd.Parameters.AddWithValue("@serviceFeeRate", TicketServiceFeeRate);
                insertCmd.Parameters.AddWithValue("@phaseName", pricingPhase.Name);
                insertCmd.Parameters.AddWithValue("@phasePercentage", pricingPhase.PercentageMarkup);
                insertCmd.Parameters.AddWithValue("@promoApplied", promoApplied);
                insertCmd.Parameters.AddWithValue("@platformFee", platformFee);
                var newTicketId = await insertCmd.ExecuteScalarAsync();
                if (newTicketId is not Guid newTicketGuid || newTicketGuid == Guid.Empty)
                {
                    throw new InvalidOperationException("Ticket record could not be created before checkout.");
                }

                // tickets_sold is incremented only on confirmed payment to avoid counting cancelled checkouts
                var successUrl = AppendQuery(req.successUrl, $"ticketPaid=1&ticketId={newTicketGuid}");
                var cancelUrl = AppendQuery(req.cancelUrl, $"ticketPending=1&ticketId={newTicketGuid}");

                if (string.IsNullOrWhiteSpace(_paymongoSecretKey))
                {
                    return StatusCode(500, new { message = "PayMongo is not configured yet. Add PayMongo:SecretKey before starting checkout." });
                }

                if (finalTotal < PayMongoMinimumAmount)
                {
                    return BadRequest(new { message = $"PayMongo requires a minimum amount of ₱{PayMongoMinimumAmount:0.00}." });
                }

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
                                new { currency = "PHP", amount = (int)(unitPrice * 100), name = $"{normalizedTierName} Ticket - {eventTitle}", quantity = req.quantity },
                                new { currency = "PHP", amount = (int)(serviceFee * 100), name = $"Ticket Service Fee ({TicketServiceFeeRate * 100:0}%)", quantity = 1 }
                            },
                            success_url = successUrl,
                            cancel_url = cancelUrl
                        }
                    }
                };

                using var client = new HttpClient();
                var plainTextBytes = Encoding.UTF8.GetBytes(_paymongoSecretKey);
                string base64Auth = Convert.ToBase64String(plainTextBytes);
                client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic", base64Auth);
                client.DefaultRequestHeaders.TryAddWithoutValidation("Idempotency-Key", $"ticket-{newTicketGuid:N}");
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

                using JsonDocument doc = JsonDocument.Parse(responseString);
                var data = doc.RootElement.GetProperty("data");
                string checkoutUrl = data.GetProperty("attributes").GetProperty("checkout_url").GetString();
                string checkoutId = data.GetProperty("id").GetString();
                string checkoutReference = data.GetProperty("attributes").TryGetProperty("reference_number", out var referenceProp)
                    ? referenceProp.GetString()
                    : string.Empty;

                string ticketUpdateSql = @"
                    UPDATE tickets
                    SET paymongo_checkout_id = @checkoutId,
                        paymongo_checkout_reference = @checkoutReference,
                        payment_method = 'AwaitingPayment'
                    WHERE id = @ticketId";

                using var ticketUpdateCmd = new NpgsqlCommand(ticketUpdateSql, connection);
                ticketUpdateCmd.Parameters.AddWithValue("@checkoutId", (object?)checkoutId ?? DBNull.Value);
                ticketUpdateCmd.Parameters.AddWithValue("@checkoutReference", (object?)checkoutReference ?? DBNull.Value);
                ticketUpdateCmd.Parameters.AddWithValue("@ticketId", newTicketGuid);
                await ticketUpdateCmd.ExecuteNonQueryAsync();

                await PaymentLedgerService.UpsertPendingAsync(
                    connection,
                    paymentScope: "ticket_purchase",
                    userId: parsedCustomerId,
                    organizerId: organizerId == Guid.Empty ? null : organizerId,
                    eventId: parsedEventId,
                    ticketId: newTicketGuid,
                    bookingId: null,
                    amount: finalTotal,
                    description: $"Ticket purchase for {eventTitle}",
                    checkoutId: checkoutId ?? string.Empty,
                    checkoutReference: checkoutReference ?? string.Empty,
                    featureUnlockState: "TicketPending",
                    metadata: new
                    {
                        tierName = normalizedTierName,
                        quantity = req.quantity,
                        subtotal,
                        serviceFee,
                        pricingPhase = pricingPhase.Name,
                        pricingPhasePercentage = pricingPhase.PercentageMarkup
                    });

                return Ok(new { message = "Checkout session created!", checkoutUrl = checkoutUrl });
            }
            catch (Exception ex)
            {
                return StatusCode(500, new
                {
                    message = ConfigurationFallbacks.BuildSafeErrorMessage(
                        _configuration,
                        "Checkout could not be started right now.",
                        ex)
                });
            }
        }

        private static bool IsSaleActive(DateTime? startsAt, DateTime? endsAt)
        {
            var now = DateTime.UtcNow;
            var hasStarted = !startsAt.HasValue || startsAt.Value <= now;
            var hasNotEnded = !endsAt.HasValue || endsAt.Value >= now;
            return hasStarted && hasNotEnded;
        }

        private static TicketPricingPhase GetTicketPricingPhase(DateTime eventTime)
        {
            var normalizedEventTime = eventTime.Kind == DateTimeKind.Utc ? eventTime : eventTime.ToUniversalTime();
            var hoursUntilEvent = (normalizedEventTime - DateTime.UtcNow).TotalHours;

            if (hoursUntilEvent <= 0)
            {
                return new TicketPricingPhase(
                    "Walk-In",
                    1.20m,
                    20,
                    "red",
                    "Walk-in pricing adds 20% once the event has started.");
            }

            if (hoursUntilEvent <= 2)
            {
                return new TicketPricingPhase(
                    "Early Bird",
                    1.10m,
                    10,
                    "amber",
                    "Early-bird pricing adds 10% close to show time.");
            }

            return new TicketPricingPhase(
                "Pre-Sale",
                1.00m,
                0,
                "blue",
                "Pre-sale pricing uses the standard ticket rate.");
        }

        private static decimal ApplyPhaseMarkup(decimal basePrice, TicketPricingPhase pricingPhase)
            => decimal.Round(basePrice * pricingPhase.Multiplier, 2);

        private static decimal ApplySale(decimal originalPrice, string? saleType, decimal? saleValue, bool saleActive)
        {
            if (!saleActive || !saleValue.HasValue || saleValue.Value <= 0) return originalPrice;

            var normalizedType = (saleType ?? string.Empty).Trim().ToLowerInvariant();
            decimal discounted = normalizedType switch
            {
                "percent" => originalPrice - (originalPrice * (saleValue.Value / 100m)),
                "amount" => originalPrice - saleValue.Value,
                _ => originalPrice
            };

            return discounted < 0 ? 0 : decimal.Round(discounted, 2);
        }

        private static decimal ResolveTierPrice(
            string requestedTier,
            decimal basePrice,
            string? specialTierName,
            decimal? specialTierPrice,
            string? bundles,
            string? saleType,
            decimal? saleValue,
            bool saleActive,
            TicketPricingPhase pricingPhase)
        {
            decimal resolvedPrice = basePrice;

            if (!string.IsNullOrWhiteSpace(requestedTier))
            {
                if (!string.IsNullOrWhiteSpace(specialTierName) &&
                    requestedTier.Equals(specialTierName.Trim(), StringComparison.OrdinalIgnoreCase) &&
                    specialTierPrice.HasValue)
                {
                    resolvedPrice = specialTierPrice.Value;
                }
                else if (!string.IsNullOrWhiteSpace(bundles))
                {
                    foreach (var bundle in ParseBundleTierRecords(bundles))
                    {
                        if (requestedTier.Equals(bundle.Name, StringComparison.OrdinalIgnoreCase))
                        {
                            resolvedPrice = bundle.Price;
                            break;
                        }
                    }
                }
            }

            var saleAdjustedPrice = ApplySale(resolvedPrice, saleType, saleValue, saleActive);
            return ApplyPhaseMarkup(saleAdjustedPrice, pricingPhase);
        }

        private static List<object> BuildBundleTierPayload(
            string? bundles,
            string? saleType,
            decimal? saleValue,
            bool saleActive,
            TicketPricingPhase pricingPhase)
        {
            if (string.IsNullOrWhiteSpace(bundles))
            {
                return new List<object>();
            }

            return ParseBundleTierRecords(bundles)
                .Select(bundle => new
                {
                    tierName = bundle.Name,
                    basePrice = bundle.Price,
                    displayPrice = ApplyPhaseMarkup(ApplySale(bundle.Price, saleType, saleValue, saleActive), pricingPhase),
                    slots = bundle.Slots
                })
                .Cast<object>()
                .ToList();
        }

        private static IEnumerable<(string Name, decimal Price, int Slots)> ParseBundleTierRecords(string bundles)
        {
            var parts = bundles.Split('|', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            foreach (var part in parts)
            {
                var match = System.Text.RegularExpressions.Regex.Match(
                    part,
                    @"\[Tier\]\s*(.*?):\s*P([0-9.]+)\s*\((\d+)\s*slots\)",
                    System.Text.RegularExpressions.RegexOptions.IgnoreCase);

                if (!match.Success) continue;
                if (!decimal.TryParse(match.Groups[2].Value, out var price)) continue;

                yield return (
                    match.Groups[1].Value.Trim(),
                    price,
                    int.TryParse(match.Groups[3].Value, out var slots) ? slots : 0
                );
            }
        }

        [HttpGet("customer/{customerId}")]
        public async Task<IActionResult> GetCustomerTickets(Guid customerId)
        {
            try
            {
                var tickets = new List<object>();
                await using var connection = new NpgsqlConnection(_connectionString);
                await connection.OpenAsync();
                await EnsureTicketPaymentColumnsExist(connection);
                await PaymentLedgerService.EnsureSchemaAsync(connection);
                await PaymentRefundService.EnsureSchemaAsync(connection);
                await SyncPendingTicketRefundStatusesAsync(connection, customerId);

                const string staleCleanupSql = @"
                    DELETE FROM tickets
                    WHERE customer_id = @cId
                      AND payment_method = 'AwaitingPayment'
                      AND created_at < NOW() - INTERVAL '2 hours';";
                await using (var cleanupCmd = new NpgsqlCommand(staleCleanupSql, connection))
                {
                    cleanupCmd.Parameters.AddWithValue("@cId", customerId);
                    await cleanupCmd.ExecuteNonQueryAsync();
                }

                string sql = @"
                    SELECT t.id, t.event_id, t.tier_name, t.quantity, t.total_price, t.payment_method, t.purchase_date, t.is_used,
                           e.title, e.event_time, e.location, e.status, COALESCE(t.paymongo_payment_reference, ''), COALESCE(t.paymongo_checkout_reference, ''),
                           COALESCE(t.ticket_unit_price, 0), COALESCE(t.ticket_subtotal, t.total_price), COALESCE(t.ticket_service_fee, 0),
                           COALESCE(t.ticket_service_fee_rate, 0), COALESCE(t.pricing_phase_name, 'Pre-Sale'), COALESCE(t.pricing_phase_percentage, 0),
                           COALESCE(t.used_quantity, CASE WHEN COALESCE(t.is_used, FALSE) THEN COALESCE(t.quantity, 1) ELSE 0 END),
                           COALESCE(t.used_ticket_units, ''),
                           COALESCE(t.refund_status, ''),
                           COALESCE(t.refund_amount, 0),
                           COALESCE(t.platform_fee, 0)
                    FROM tickets t
                    JOIN events e ON t.event_id = e.id
                    WHERE t.customer_id = @cId
                    ORDER BY t.purchase_date DESC";

                using var cmd = new NpgsqlCommand(sql, connection);
                cmd.Parameters.AddWithValue("@cId", customerId);

                using var reader = await cmd.ExecuteReaderAsync();
                while (await reader.ReadAsync())
                {
                    var quantity = reader.GetInt32(3);
                    var usedQuantity = reader.IsDBNull(20) ? (reader.IsDBNull(7) ? 0 : (reader.GetBoolean(7) ? quantity : 0)) : reader.GetInt32(20);
                    usedQuantity = Math.Clamp(usedQuantity, 0, Math.Max(quantity, 0));
                    tickets.Add(new
                    {
                        ticketId = reader.GetGuid(0),
                        eventId = reader.IsDBNull(1) ? Guid.Empty : reader.GetGuid(1),
                        tierName = reader.GetString(2),
                        quantity = quantity,
                        totalPrice = reader.GetDecimal(4),
                        paymentMethod = reader.GetString(5),
                        purchaseDate = reader.GetDateTime(6),
                        isUsed = quantity > 0 && usedQuantity >= quantity,
                        usedQuantity = usedQuantity,
                        remainingQuantity = Math.Max(quantity - usedQuantity, 0),
                        hasScannedUnits = usedQuantity > 0,
                        usedTicketUnits = ParseUsedTicketUnits(reader.IsDBNull(21) ? "" : reader.GetString(21)),
                        eventTitle = reader.GetString(8),
                        eventTime = reader.GetDateTime(9),
                        location = reader.GetString(10),
                        eventStatus = reader.IsDBNull(11) ? "Upcoming" : reader.GetString(11),
                        paymentReference = reader.IsDBNull(12) ? "" : reader.GetString(12),
                        checkoutReference = reader.IsDBNull(13) ? "" : reader.GetString(13),
                        unitPrice = reader.IsDBNull(14) ? 0 : reader.GetDecimal(14),
                        subtotal = reader.IsDBNull(15) ? reader.GetDecimal(4) : reader.GetDecimal(15),
                        serviceFee = reader.IsDBNull(16) ? 0 : reader.GetDecimal(16),
                        serviceFeeRate = reader.IsDBNull(17) ? 0 : reader.GetDecimal(17),
                        pricingPhase = reader.IsDBNull(18) ? "Pre-Sale" : reader.GetString(18),
                        pricingPhasePercentage = reader.IsDBNull(19) ? 0 : reader.GetInt32(19),
                        refundStatus = reader.IsDBNull(22) ? "" : reader.GetString(22),
                        refundAmount = reader.IsDBNull(23) ? 0 : reader.GetDecimal(23),
                        platformFee  = reader.IsDBNull(24) ? 0 : reader.GetDecimal(24)
                    });
                }
                return Ok(tickets);
            }
            catch (Exception ex) { return StatusCode(500, new { message = ex.Message }); }
        }

        private async Task SyncPendingTicketRefundStatusesAsync(NpgsqlConnection connection, Guid customerId)
        {
            if (customerId == Guid.Empty || string.IsNullOrWhiteSpace(_paymongoSecretKey))
            {
                return;
            }

            const string sql = @"
                SELECT
                    rr.id,
                    rr.ticket_id,
                    COALESCE(rr.provider_refund_id, ''),
                    COALESCE(rr.status, 'Requested'),
                    COALESCE(rr.amount, 0)
                FROM refund_requests rr
                INNER JOIN tickets t ON t.id = rr.ticket_id
                WHERE rr.refund_scope = 'ticket'
                  AND t.customer_id = @customerId
                  AND COALESCE(t.refund_status, '') = 'Refund Pending'
                  AND COALESCE(rr.status, 'Requested') IN ('Requested', 'ManualReview', 'Refund Pending');";

            var pendingItems = new List<(Guid RefundRequestId, Guid TicketId, string ProviderRefundId, string Status, decimal Amount)>();
            await using (var cmd = new NpgsqlCommand(sql, connection))
            {
                cmd.Parameters.AddWithValue("@customerId", customerId);
                await using var reader = await cmd.ExecuteReaderAsync();
                while (await reader.ReadAsync())
                {
                    pendingItems.Add((
                        reader.GetGuid(0),
                        reader.GetGuid(1),
                        reader.IsDBNull(2) ? string.Empty : reader.GetString(2),
                        reader.IsDBNull(3) ? "Requested" : reader.GetString(3),
                        reader.IsDBNull(4) ? 0m : reader.GetDecimal(4)
                    ));
                }
            }

            foreach (var item in pendingItems)
            {
                if (string.IsNullOrWhiteSpace(item.ProviderRefundId))
                {
                    continue;
                }

                var refundResult = await PaymentRefundService.GetPayMongoRefundStatusAsync(_paymongoSecretKey, item.ProviderRefundId);
                var localRefundStatus = refundResult.Status == "Refunded"
                    ? "Refunded"
                    : refundResult.Status == "Refund Pending"
                        ? "Refund Pending"
                        : refundResult.Status == "ManualReview"
                            ? "Refund Pending"
                            : "Refund Failed";

                await using (var updateTicketCmd = new NpgsqlCommand(@"
                    UPDATE tickets
                    SET refund_status = @status,
                        refund_processed_at = CASE WHEN @status = 'Refunded' THEN NOW() ELSE refund_processed_at END
                    WHERE id = @ticketId;", connection))
                {
                    updateTicketCmd.Parameters.AddWithValue("@status", localRefundStatus);
                    updateTicketCmd.Parameters.AddWithValue("@ticketId", item.TicketId);
                    await updateTicketCmd.ExecuteNonQueryAsync();
                }

                await PaymentRefundService.UpdateRefundRequestAsync(
                    connection,
                    item.RefundRequestId,
                    status: localRefundStatus == "Refunded" ? "Refunded" : localRefundStatus == "Refund Pending" ? "ManualReview" : "Failed",
                    providerRefundId: refundResult.RefundId,
                    providerStatus: refundResult.ProviderStatus,
                    errorCode: refundResult.ErrorCode,
                    errorMessage: refundResult.ErrorMessage);

                if (localRefundStatus == "Refunded")
                {
                    await PaymentLedgerService.MarkRefundedAsync(connection, "ticket_purchase", item.TicketId, null, "Refunded");
                }
            }
        }

        // Cancel/delete a ticket that is still AwaitingPayment — only the ticket owner can do this
        [HttpDelete("{ticketId}/cancel-pending")]
        [Authorize]
        public async Task<IActionResult> CancelPendingTicket(Guid ticketId)
        {
            try
            {
                var actorId = User.FindFirstValue(ClaimTypes.NameIdentifier);
                if (!Guid.TryParse(actorId, out var parsedActorId)) return Unauthorized();

                await using var connection = new NpgsqlConnection(_connectionString);
                await connection.OpenAsync();

                const string sql = @"
                    DELETE FROM tickets
                    WHERE id = @id
                      AND customer_id = @actor
                      AND payment_method = 'AwaitingPayment'
                    RETURNING id;";
                await using var cmd = new NpgsqlCommand(sql, connection);
                cmd.Parameters.AddWithValue("@id", ticketId);
                cmd.Parameters.AddWithValue("@actor", parsedActorId);
                var deleted = await cmd.ExecuteScalarAsync();
                if (deleted == null) return NotFound(new { message = "Ticket not found or already confirmed." });
                return Ok(new { message = "Pending ticket removed." });
            }
            catch (Exception ex)
            {
                return StatusCode(500, new { message = "Failed to cancel ticket: " + ex.Message });
            }
        }

        [HttpPost("{ticketId}/payment/confirm")]
        [Authorize]
        public async Task<IActionResult> ConfirmTicketPayment(Guid ticketId)
        {
            try
            {
                var actorId = User.FindFirstValue(ClaimTypes.NameIdentifier);
                if (!Guid.TryParse(actorId, out var parsedActorId) || parsedActorId == Guid.Empty)
                {
                    return Unauthorized(new { message = "Sign in again before confirming this payment." });
                }

                await using var connection = new NpgsqlConnection(_connectionString);
                await connection.OpenAsync();
                await EnsureTicketPaymentColumnsExist(connection);

                const string sql = @"
                    SELECT COALESCE(paymongo_checkout_id, ''),
                           COALESCE(payment_method, ''),
                           COALESCE(paymongo_checkout_reference, ''),
                           customer_id
                    FROM tickets
                    WHERE id = @id";

                string checkoutId;
                string paymentMethod;
                string checkoutReference;
                Guid customerId;

                using (var cmd = new NpgsqlCommand(sql, connection))
                {
                    cmd.Parameters.AddWithValue("@id", ticketId);
                    using var reader = await cmd.ExecuteReaderAsync();
                    if (!await reader.ReadAsync())
                    {
                        return NotFound(new { message = "Ticket not found." });
                    }

                    checkoutId = reader.IsDBNull(0) ? "" : reader.GetString(0);
                    paymentMethod = reader.IsDBNull(1) ? "" : reader.GetString(1);
                    checkoutReference = reader.IsDBNull(2) ? "" : reader.GetString(2);
                    customerId = reader.IsDBNull(3) ? Guid.Empty : reader.GetGuid(3);
                }

                if (customerId == Guid.Empty || customerId != parsedActorId)
                {
                    return Forbid();
                }

                if (paymentMethod != null && paymentMethod != "" && paymentMethod != "AwaitingPayment" && paymentMethod != "PayMongo")
                {
                    return Ok(new { message = "Ticket payment already confirmed.", paymentMethod });
                }

                if (string.IsNullOrWhiteSpace(checkoutId))
                {
                    return BadRequest(new { message = "No checkout session is attached to this ticket yet." });
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
                if (string.IsNullOrWhiteSpace(checkoutReference) && attributes.TryGetProperty("reference_number", out var checkoutReferenceProp))
                {
                    checkoutReference = checkoutReferenceProp.GetString() ?? "";
                }

                // Check session-level status — PayMongo sets this to "paid" as soon as payment succeeds,
                // even if the payments[] array hasn't populated yet (async on their side).
                var sessionStatus = attributes.TryGetProperty("status", out var statusProp)
                    ? (statusProp.GetString() ?? "").ToLowerInvariant()
                    : "";

                var payments = attributes.TryGetProperty("payments", out var paymentArray) && paymentArray.ValueKind == JsonValueKind.Array
                    ? paymentArray
                    : default;

                bool hasPayments = payments.ValueKind == JsonValueKind.Array && payments.GetArrayLength() > 0;

                // Not paid if session status is not "paid" and payments array is empty
                if (sessionStatus != "paid" && !hasPayments)
                {
                    return Ok(new { message = "Payment is still pending.", paymentMethod = "AwaitingPayment" });
                }

                string confirmedMethod = "PayMongo";
                string paymentReference = "";
                string paymentId = "";

                if (hasPayments)
                {
                    var firstPayment = payments[0];
                    if (firstPayment.TryGetProperty("id", out var paymentIdProp))
                        paymentId = paymentIdProp.GetString() ?? "";

                    if (firstPayment.TryGetProperty("attributes", out var paymentAttributes))
                    {
                        if (paymentAttributes.TryGetProperty("reference_number", out var paymentReferenceProp))
                            paymentReference = paymentReferenceProp.GetString() ?? "";

                        if (paymentAttributes.TryGetProperty("source", out var sourceProp) &&
                            sourceProp.ValueKind == JsonValueKind.Object &&
                            sourceProp.TryGetProperty("type", out var sourceTypeProp))
                        {
                            confirmedMethod = sourceTypeProp.GetString() ?? "PayMongo";
                        }
                    }
                }
                // Fallback when payments[] is empty but session status is "paid"
                if (string.IsNullOrWhiteSpace(paymentReference))
                    paymentReference = checkoutReference;

                await using var transaction = await connection.BeginTransactionAsync();

                const string updateSql = @"
                    UPDATE tickets
                    SET payment_method = @paymentMethod,
                        paymongo_payment_id = @paymentId,
                        paymongo_payment_reference = @paymentReference,
                        paymongo_checkout_reference = @checkoutReference
                    WHERE id = @ticketId
                      AND customer_id = @customerId
                      AND COALESCE(payment_method, '') IN ('', 'AwaitingPayment', 'PayMongo')
                    RETURNING id;";

                using var updateCmd = new NpgsqlCommand(updateSql, connection, transaction);
                updateCmd.Parameters.AddWithValue("@paymentMethod", confirmedMethod);
                updateCmd.Parameters.AddWithValue("@paymentId", (object?)paymentId ?? DBNull.Value);
                updateCmd.Parameters.AddWithValue("@paymentReference", (object?)paymentReference ?? DBNull.Value);
                updateCmd.Parameters.AddWithValue("@checkoutReference", (object?)checkoutReference ?? DBNull.Value);
                updateCmd.Parameters.AddWithValue("@ticketId", ticketId);
                updateCmd.Parameters.AddWithValue("@customerId", parsedActorId);
                var updatedTicketId = await updateCmd.ExecuteScalarAsync();
                if (updatedTicketId is null)
                {
                    await transaction.RollbackAsync();
                    return Ok(new { message = "Ticket payment already confirmed.", paymentMethod });
                }

                // Increment tickets_sold on the event; only increment sale_quantity_used for promo tickets
                const string incrementSoldSql = @"
                    UPDATE events e
                    SET tickets_sold = COALESCE(tickets_sold, 0) + t.quantity,
                        sale_quantity_used = CASE
                            WHEN COALESCE(t.promo_applied, FALSE) = TRUE
                            THEN COALESCE(e.sale_quantity_used, 0) + t.quantity
                            ELSE COALESCE(e.sale_quantity_used, 0)
                        END
                    FROM tickets t
                    WHERE t.id = @ticketId AND e.id = t.event_id;";
                using var incrementCmd = new NpgsqlCommand(incrementSoldSql, connection, transaction);
                incrementCmd.Parameters.AddWithValue("@ticketId", ticketId);
                await incrementCmd.ExecuteNonQueryAsync();

                // Also increment slots_sold on the specific event_tier (if one exists matching this ticket's tier)
                const string incrementTierSql = @"
                    UPDATE event_tiers et
                    SET slots_sold = COALESCE(slots_sold, 0) + t.quantity
                    FROM tickets t
                    WHERE t.id = @ticketId
                      AND et.event_id = t.event_id
                      AND LOWER(et.name) = LOWER(COALESCE(t.tier_name, ''));";
                using var incrementTierCmd = new NpgsqlCommand(incrementTierSql, connection, transaction);
                incrementTierCmd.Parameters.AddWithValue("@ticketId", ticketId);
                await incrementTierCmd.ExecuteNonQueryAsync();

                await PaymentLedgerService.MarkPaidAsync(
                    connection,
                    paymentScope: "ticket_purchase",
                    ticketId: ticketId,
                    bookingId: null,
                    paymentMethod: confirmedMethod,
                    paymentReference: paymentReference,
                    checkoutReference: checkoutReference,
                    featureUnlockState: "TicketIssued");

                var details = await GetTicketNotificationContextAsync(connection, ticketId);
                if (details.customerId != Guid.Empty)
                {
                    await NotificationSupport.InsertNotificationIfNotExistsAsync(
                        connection,
                        details.customerId,
                        "ticket_purchase",
                        "Ticket payment confirmed",
                        $"Your ticket for '{details.eventTitle}' is confirmed.",
                        ticketId,
                        "ticket",
                        24);
                }

                if (details.organizerId != Guid.Empty)
                {
                    await NotificationSupport.InsertNotificationIfNotExistsAsync(
                        connection,
                        details.organizerId,
                        "ticket_sale",
                        "New ticket purchase",
                        $"{details.customerName} purchased {details.quantity} ticket(s) for '{details.eventTitle}'.",
                        ticketId,
                        "ticket",
                        24);
                }

                await transaction.CommitAsync();

                // Broadcast ticket sale to organizer's real-time dashboard
                _ = Task.Run(async () =>
                {
                    try
                    {
                        await using var bcConn = new NpgsqlConnection(_connectionString);
                        await bcConn.OpenAsync();
                        const string bcSql = "SELECT t.event_id, t.quantity, t.total_price FROM tickets t WHERE t.id=@id LIMIT 1;";
                        await using var bcCmd = new NpgsqlCommand(bcSql, bcConn);
                        bcCmd.Parameters.AddWithValue("@id", ticketId);
                        await using var bcRdr = await bcCmd.ExecuteReaderAsync();
                        if (await bcRdr.ReadAsync())
                        {
                            var evId = bcRdr.IsDBNull(0) ? Guid.Empty : bcRdr.GetGuid(0);
                            if (evId != Guid.Empty)
                                await _scanBroadcaster.BroadcastTicketSaleAsync(evId.ToString(), new
                                {
                                    ticketId,
                                    quantity = bcRdr.IsDBNull(1) ? 1 : bcRdr.GetInt32(1),
                                    revenue = bcRdr.IsDBNull(2) ? 0m : bcRdr.GetDecimal(2),
                                    soldAt = DateTime.UtcNow
                                });
                        }
                    }
                    catch { }
                });

                // Fire-and-forget receipt email
                _ = Task.Run(async () =>
                {
                    try
                    {
                        await using var emailConn = new NpgsqlConnection(_connectionString);
                        await emailConn.OpenAsync();
                        const string receiptSql = @"
                            SELECT t.customer_id, e.title, t.quantity, t.total_price,
                                   e.event_time, e.location, e.city
                            FROM tickets t
                            INNER JOIN events e ON e.id = t.event_id
                            WHERE t.id = @id LIMIT 1;";
                        await using var rcmd = new NpgsqlCommand(receiptSql, emailConn);
                        rcmd.Parameters.AddWithValue("@id", ticketId);
                        await using var rdr = await rcmd.ExecuteReaderAsync();
                        if (await rdr.ReadAsync())
                        {
                            var custId = rdr.IsDBNull(0) ? Guid.Empty : rdr.GetGuid(0);
                            var evTitle = rdr.IsDBNull(1) ? "Your Event" : rdr.GetString(1);
                            var qty = rdr.IsDBNull(2) ? 1 : rdr.GetInt32(2);
                            var price = rdr.IsDBNull(3) ? 0m : rdr.GetDecimal(3);
                            var evTime = rdr.IsDBNull(4) ? (DateTime?)null : rdr.GetDateTime(4);
                            var loc = rdr.IsDBNull(5) ? null : rdr.GetString(5);
                            var city = rdr.IsDBNull(6) ? null : rdr.GetString(6);
                            await rdr.CloseAsync();
                            if (custId != Guid.Empty)
                                await _emailService.SendTicketReceiptEmailAsync(
                                    emailConn, custId, evTitle, qty, price,
                                    evTime, loc, city, ticketId.ToString());
                        }
                    }
                    catch { /* email errors never break main flow */ }
                });

                return Ok(new { message = "Ticket payment confirmed.", paymentMethod = confirmedMethod });
            }
            catch (Exception ex)
            {
                return StatusCode(500, new
                {
                    message = ConfigurationFallbacks.BuildSafeErrorMessage(
                        _configuration,
                        "Failed to confirm ticket payment.",
                        ex)
                });
            }
        }

        [Authorize]
        [HttpPost("{ticketId}/refunds/request")]
        public async Task<IActionResult> RequestTicketRefund(Guid ticketId, [FromBody] CreateTicketRefundRequest? req)
        {
            try
            {
                var actorUserId = Guid.TryParse(User.FindFirstValue(ClaimTypes.NameIdentifier), out var parsedUserId)
                    ? parsedUserId
                    : Guid.Empty;
                if (actorUserId == Guid.Empty)
                {
                    return Unauthorized(new { message = "Sign in again before requesting a ticket refund." });
                }

                await using var connection = new NpgsqlConnection(_connectionString);
                await connection.OpenAsync();
                await EnsureTicketPaymentColumnsExist(connection);
                await PaymentLedgerService.EnsureSchemaAsync(connection);
                await PaymentRefundService.EnsureSchemaAsync(connection);

                const string sql = @"
                    SELECT t.customer_id,
                           COALESCE(t.event_id, '00000000-0000-0000-0000-000000000000'::uuid),
                           COALESCE(t.total_price, 0),
                           COALESCE(t.payment_method, ''),
                           COALESCE(t.paymongo_payment_id, ''),
                           COALESCE(t.paymongo_checkout_id, ''),
                           COALESCE(t.quantity, 1),
                           COALESCE(t.used_quantity, CASE WHEN COALESCE(t.is_used, FALSE) THEN COALESCE(t.quantity, 1) ELSE 0 END),
                           COALESCE(t.refund_status, ''),
                           COALESCE(e.title, 'Event Ticket'),
                           e.event_time,
                           COALESCE(e.status, 'Upcoming')
                    FROM tickets t
                    LEFT JOIN events e ON e.id = t.event_id
                    WHERE t.id = @id;";

                Guid customerId;
                Guid eventId;
                decimal totalPrice;
                string paymentMethod;
                string paymentId;
                string checkoutId;
                int quantity;
                int usedQuantity;
                string refundStatus;
                string eventTitle;
                DateTime? eventTime;
                string eventStatus;

                await using (var cmd = new NpgsqlCommand(sql, connection))
                {
                    cmd.Parameters.AddWithValue("@id", ticketId);
                    await using var reader = await cmd.ExecuteReaderAsync();
                    if (!await reader.ReadAsync())
                    {
                        return NotFound(new { message = "Ticket not found." });
                    }

                    customerId = reader.IsDBNull(0) ? Guid.Empty : reader.GetGuid(0);
                    eventId = reader.IsDBNull(1) ? Guid.Empty : reader.GetGuid(1);
                    totalPrice = reader.IsDBNull(2) ? 0 : reader.GetDecimal(2);
                    paymentMethod = reader.IsDBNull(3) ? "" : reader.GetString(3);
                    paymentId = reader.IsDBNull(4) ? "" : reader.GetString(4);
                    checkoutId = reader.IsDBNull(5) ? "" : reader.GetString(5);
                    quantity = reader.IsDBNull(6) ? 1 : reader.GetInt32(6);
                    usedQuantity = reader.IsDBNull(7) ? 0 : reader.GetInt32(7);
                    refundStatus = reader.IsDBNull(8) ? "" : reader.GetString(8);
                    eventTitle = reader.IsDBNull(9) ? "Event Ticket" : reader.GetString(9);
                    eventTime = reader.IsDBNull(10) ? (DateTime?)null : reader.GetDateTime(10);
                    eventStatus = reader.IsDBNull(11) ? "Upcoming" : reader.GetString(11);
                }

                if (customerId != actorUserId)
                {
                    return Forbid();
                }

                if (string.Equals(refundStatus, "Refunded", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(refundStatus, "Refund Pending", StringComparison.OrdinalIgnoreCase))
                {
                    return Conflict(new { message = "This ticket already has an active refund state." });
                }

                if (string.IsNullOrWhiteSpace(paymentMethod) || string.Equals(paymentMethod, "AwaitingPayment", StringComparison.OrdinalIgnoreCase))
                {
                    return BadRequest(new { message = "Only paid tickets can be refunded." });
                }

                if (usedQuantity > 0 || quantity <= 0)
                {
                    return BadRequest(new { message = "Used tickets can no longer be refunded." });
                }

                if (string.Equals(eventStatus, "Finished", StringComparison.OrdinalIgnoreCase) ||
                    (eventTime.HasValue && eventTime.Value.ToUniversalTime() <= DateTime.UtcNow))
                {
                    return BadRequest(new { message = "Ticket refunds are only available before the event starts." });
                }

                if (string.IsNullOrWhiteSpace(paymentId))
                {
                    paymentId = await PaymentRefundService.ResolveCheckoutPaymentIdAsync(_paymongoSecretKey, checkoutId) ?? string.Empty;
                }

                await using (var markPendingCmd = new NpgsqlCommand(
                    "UPDATE tickets SET refund_status = 'Refund Pending', refund_amount = @amount, refund_reason = @reason WHERE id = @id;",
                    connection))
                {
                    markPendingCmd.Parameters.AddWithValue("@id", ticketId);
                    markPendingCmd.Parameters.AddWithValue("@amount", totalPrice);
                    markPendingCmd.Parameters.AddWithValue("@reason", (object?)(req?.notes ?? req?.reasonCode ?? "Ticket refund requested") ?? DBNull.Value);
                    await markPendingCmd.ExecuteNonQueryAsync();
                }

                var refundRequestId = await PaymentRefundService.CreateRefundRequestAsync(
                    connection,
                    refundScope: "ticket",
                    paymentScope: "ticket_purchase",
                    bookingId: null,
                    ticketId: ticketId,
                    requesterUserId: actorUserId,
                    beneficiaryUserId: customerId,
                    requesterRole: User.FindFirstValue(ClaimTypes.Role),
                    reasonCode: string.IsNullOrWhiteSpace(req?.reasonCode) ? "Other" : req!.reasonCode!,
                    reasonDetails: req?.notes,
                    amount: totalPrice,
                    providerPaymentId: paymentId,
                    metadata: new { eventId, eventTitle });

                var refundResult = await PaymentRefundService.CreatePayMongoRefundAsync(
                    _paymongoSecretKey,
                    paymentId,
                    totalPrice,
                    req?.reasonCode ?? "Other",
                    req?.notes);

                var localRefundStatus = refundResult.Status == "Refunded"
                    ? "Refunded"
                    : refundResult.Status == "Refund Pending"
                        ? "Refund Pending"
                        : "Refund Failed";

                await using (var updateCmd = new NpgsqlCommand(
                    "UPDATE tickets SET refund_status = @status, refund_amount = @amount, refund_reason = @reason, refund_processed_at = CASE WHEN @status = 'Refunded' THEN NOW() ELSE refund_processed_at END WHERE id = @id;",
                    connection))
                {
                    updateCmd.Parameters.AddWithValue("@id", ticketId);
                    updateCmd.Parameters.AddWithValue("@status", localRefundStatus);
                    updateCmd.Parameters.AddWithValue("@amount", totalPrice);
                    updateCmd.Parameters.AddWithValue("@reason", (object?)(req?.notes ?? req?.reasonCode ?? "Ticket refund requested") ?? DBNull.Value);
                    await updateCmd.ExecuteNonQueryAsync();
                }

                await PaymentRefundService.UpdateRefundRequestAsync(
                    connection,
                    refundRequestId,
                    status: localRefundStatus == "Refunded" ? "Refunded" : localRefundStatus == "Refund Pending" ? "ManualReview" : "Failed",
                    providerRefundId: refundResult.RefundId,
                    providerStatus: refundResult.ProviderStatus,
                    errorCode: refundResult.ErrorCode,
                    errorMessage: refundResult.ErrorMessage);

                if (localRefundStatus == "Refunded")
                {
                    await PaymentLedgerService.MarkRefundedAsync(connection, "ticket_purchase", ticketId, null, "Refunded");
                }

                return Ok(new
                {
                    message = localRefundStatus == "Refunded"
                        ? "Ticket refund processed."
                        : localRefundStatus == "Refund Pending"
                            ? "Ticket refund was submitted and needs PayMongo/manual review."
                            : "Ticket refund request failed. Please verify the payment details.",
                    refundStatus = localRefundStatus
                });
            }
            catch (Exception ex)
            {
                return StatusCode(500, new
                {
                    message = ConfigurationFallbacks.BuildSafeErrorMessage(
                        _configuration,
                        "Failed to process ticket refund.",
                        ex)
                });
            }
        }

        [HttpPost("scan")]
        public async Task<IActionResult> ScanTicket([FromBody] ScanTicketRequest? req, [FromQuery] Guid? eventId = null)
        {
            try
            {
                if (eventId == null || eventId == Guid.Empty)
                {
                    return BadRequest(new { message = "EVENT REQUIRED", details = "Open the scanner from a specific organizer event before validating tickets." });
                }

                var parsedScan = ParseTicketScan(req?.ticketValue);
                if (parsedScan == null || parsedScan.TicketId == Guid.Empty)
                {
                    return BadRequest(new
                    {
                        message = "INVALID QR",
                        details = "The scanned QR code does not contain a valid ticket ID."
                    });
                }

                await using var connection = new NpgsqlConnection(_connectionString);
                await connection.OpenAsync();
                await EnsureTicketPaymentColumnsExist(connection);

                string checkSql = @"
                    SELECT t.is_used,
                           t.tier_name,
                           t.quantity,
                           e.title,
                           e.event_time,
                           e.status,
                           e.id,
                           COALESCE(t.used_quantity, CASE WHEN COALESCE(t.is_used, FALSE) THEN COALESCE(t.quantity, 1) ELSE 0 END),
                           COALESCE(t.used_ticket_units, ''),
                           COALESCE(t.refund_status, '')
                    FROM tickets t
                    JOIN events e ON t.event_id = e.id 
                    WHERE t.id = @id";
                    
                using var checkCmd = new NpgsqlCommand(checkSql, connection);
                checkCmd.Parameters.AddWithValue("@id", parsedScan.TicketId);

                using var reader = await checkCmd.ExecuteReaderAsync();
                if (!await reader.ReadAsync())
                {
                    return NotFound(new { message = "INVALID TICKET: This ticket does not exist in the database." });
                }

                bool isUsed = reader.IsDBNull(0) ? false : reader.GetBoolean(0);
                string tier = reader.GetString(1);
                int qty = reader.GetInt32(2);
                string evTitle = reader.GetString(3);
                DateTime eventTime = reader.GetDateTime(4);
                string eventStatus = reader.IsDBNull(5) ? "Upcoming" : reader.GetString(5);
                Guid ticketEventId = reader.IsDBNull(6) ? Guid.Empty : reader.GetGuid(6);
                int usedQuantity = reader.IsDBNull(7) ? (isUsed ? qty : 0) : reader.GetInt32(7);
                var usedUnits = ParseUsedTicketUnits(reader.IsDBNull(8) ? "" : reader.GetString(8));
                var refundStatus = reader.IsDBNull(9) ? "" : reader.GetString(9);
                
                await reader.CloseAsync();

                if (string.Equals(refundStatus, "Refunded", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(refundStatus, "Refund Pending", StringComparison.OrdinalIgnoreCase))
                {
                    return BadRequest(new { message = "REFUND LOCK", details = "This ticket is under refund handling and can no longer be scanned." });
                }

                if (ticketEventId != eventId.Value)
                {
                    return BadRequest(new
                    {
                        message = "WRONG EVENT",
                        details = $"This ticket is for \"{evTitle}\" — it cannot be used at this event."
                    });
                }

                if (eventStatus == "Finished" || DateTime.Now > eventTime.AddHours(24))
                {
                    return BadRequest(new { message = "EXPIRED TICKET", details = $"The event '{evTitle}' has already ended." });
                }

                if (qty > 1 && !parsedScan.UnitNumber.HasValue)
                {
                    return BadRequest(new
                    {
                        message = "INDIVIDUAL QR REQUIRED",
                        details = $"This purchase contains {qty} tickets. Use the specific QR for each ticket holder."
                    });
                }

                var unitNumber = parsedScan.UnitNumber.GetValueOrDefault(1);
                if (unitNumber < 1 || unitNumber > Math.Max(qty, 1))
                {
                    return BadRequest(new
                    {
                        message = "INVALID QR",
                        details = $"The scanned QR points to ticket {unitNumber}, but this order only has {qty} ticket(s)."
                    });
                }

                if (usedUnits.Contains(unitNumber))
                {
                    return BadRequest(new
                    {
                        message = "ALREADY SCANNED!",
                        details = $"Ticket {unitNumber} of {qty} for {evTitle} was already used."
                    });
                }

                if (isUsed || usedQuantity >= qty)
                {
                    return BadRequest(new { message = "ALREADY SCANNED!", details = $"{qty}x {tier} for {evTitle} was already used." });
                }

                usedUnits.Add(unitNumber);
                var nextUsedQuantity = usedUnits.Count;
                var fullyUsed = nextUsedQuantity >= qty;

                string updateSql = @"
                    UPDATE tickets
                    SET used_quantity = @usedQuantity,
                        used_ticket_units = @usedUnits,
                        is_used = @isUsed
                    WHERE id = @id";
                using var updateCmd = new NpgsqlCommand(updateSql, connection);
                updateCmd.Parameters.AddWithValue("@id", parsedScan.TicketId);
                updateCmd.Parameters.AddWithValue("@usedQuantity", nextUsedQuantity);
                updateCmd.Parameters.AddWithValue("@usedUnits", SerializeUsedTicketUnits(usedUnits));
                updateCmd.Parameters.AddWithValue("@isUsed", fullyUsed);
                await updateCmd.ExecuteNonQueryAsync();

                var scanResult = new
                {
                    message = "SUCCESS! Ticket is Valid.",
                    details = qty > 1
                        ? $"Ticket {unitNumber} of {qty} · {tier} · {evTitle} — {Math.Max(qty - nextUsedQuantity, 0)} remaining"
                        : $"{tier} · {evTitle}",
                    eventTitle = evTitle,
                    tierName = tier,
                    unitNumber,
                    totalQuantity = qty,
                    usedQuantity = nextUsedQuantity,
                    remainingQuantity = Math.Max(qty - nextUsedQuantity, 0),
                    isFullyUsed = fullyUsed,
                    ticketId = parsedScan.TicketId
                };

                // Broadcast to organizer's real-time dashboard
                _ = _scanBroadcaster.BroadcastScanAsync(eventId.Value.ToString(), new
                {
                    ticketId = parsedScan.TicketId,
                    tier,
                    unit = unitNumber,
                    totalQty = qty,
                    usedQty = nextUsedQuantity,
                    scannedAt = DateTime.UtcNow
                });

                return Ok(scanResult);
            }
            catch (Exception ex) { return StatusCode(500, new { message = "Database Error: " + ex.Message }); }
        }

        private static ParsedTicketScan? ParseTicketScan(string? ticketValue)
        {
            if (string.IsNullOrWhiteSpace(ticketValue)) return null;

            var trimmed = ticketValue.Trim();
            if (Guid.TryParse(trimmed, out var directGuid))
            {
                return new ParsedTicketScan(directGuid, null);
            }

            if (Uri.TryCreate(trimmed, UriKind.Absolute, out var uri))
            {
                var query = Microsoft.AspNetCore.WebUtilities.QueryHelpers.ParseQuery(uri.Query);
                if (query.TryGetValue("ticketId", out var ticketIdValues) && Guid.TryParse(ticketIdValues.FirstOrDefault(), out var queryGuid))
                {
                    int? unitNumber = null;
                    if (query.TryGetValue("unit", out var unitValues) && int.TryParse(unitValues.FirstOrDefault(), out var parsedUnit))
                    {
                        unitNumber = parsedUnit;
                    }

                    return new ParsedTicketScan(queryGuid, unitNumber);
                }
            }

            var pipeMatch = System.Text.RegularExpressions.Regex.Match(trimmed, @"(?<ticketId>[0-9a-fA-F]{8}\-(?:[0-9a-fA-F]{4}\-){3}[0-9a-fA-F]{12})\|(?<unit>\d+)$");
            if (pipeMatch.Success && Guid.TryParse(pipeMatch.Groups["ticketId"].Value, out var pipeGuid))
            {
                return new ParsedTicketScan(
                    pipeGuid,
                    int.TryParse(pipeMatch.Groups["unit"].Value, out var pipeUnit) ? pipeUnit : null);
            }

            var match = System.Text.RegularExpressions.Regex.Match(trimmed, @"[0-9a-fA-F]{8}\-(?:[0-9a-fA-F]{4}\-){3}[0-9a-fA-F]{12}");
            if (match.Success && Guid.TryParse(match.Value, out var embeddedGuid))
            {
                return new ParsedTicketScan(embeddedGuid, null);
            }

            return null;
        }

        internal static List<int> ParseUsedTicketUnits(string? rawValue)
        {
            if (string.IsNullOrWhiteSpace(rawValue))
            {
                return new List<int>();
            }

            return rawValue
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(value => int.TryParse(value, out var parsed) ? parsed : 0)
                .Where(value => value > 0)
                .Distinct()
                .OrderBy(value => value)
                .ToList();
        }

        private static string SerializeUsedTicketUnits(IEnumerable<int> units)
        {
            return string.Join(",", units
                .Where(value => value > 0)
                .Distinct()
                .OrderBy(value => value));
        }

        private static async Task EnsureTicketPaymentColumnsExist(NpgsqlConnection connection)
        {
            const string alterSql = @"
                CREATE TABLE IF NOT EXISTS tickets (
                    id uuid PRIMARY KEY,
                    event_id uuid NOT NULL,
                    customer_id uuid NOT NULL,
                    tier_name text NOT NULL DEFAULT 'General Admission',
                    quantity integer NOT NULL DEFAULT 1,
                    total_price numeric(12,2) NOT NULL DEFAULT 0,
                    payment_method text NOT NULL DEFAULT 'AwaitingPayment',
                    purchase_date timestamptz NOT NULL DEFAULT NOW(),
                    is_used boolean NOT NULL DEFAULT FALSE,
                    created_at timestamptz NOT NULL DEFAULT NOW()
                );
                CREATE INDEX IF NOT EXISTS idx_tickets_event_id ON tickets(event_id);
                CREATE INDEX IF NOT EXISTS idx_tickets_customer_id ON tickets(customer_id);
                ALTER TABLE tickets ADD COLUMN IF NOT EXISTS paymongo_checkout_id text NULL;
                ALTER TABLE tickets ADD COLUMN IF NOT EXISTS paymongo_checkout_reference text NULL;
                ALTER TABLE tickets ADD COLUMN IF NOT EXISTS paymongo_payment_id text NULL;
                ALTER TABLE tickets ADD COLUMN IF NOT EXISTS paymongo_payment_reference text NULL;
                ALTER TABLE tickets ADD COLUMN IF NOT EXISTS refund_status varchar(30) NULL;
                ALTER TABLE tickets ADD COLUMN IF NOT EXISTS refund_amount numeric(12,2) NOT NULL DEFAULT 0;
                ALTER TABLE tickets ADD COLUMN IF NOT EXISTS refund_reason text NULL;
                ALTER TABLE tickets ADD COLUMN IF NOT EXISTS refund_processed_at timestamptz NULL;
                ALTER TABLE tickets ADD COLUMN IF NOT EXISTS ticket_unit_price numeric(12,2) NOT NULL DEFAULT 0;
                ALTER TABLE tickets ADD COLUMN IF NOT EXISTS ticket_subtotal numeric(12,2) NOT NULL DEFAULT 0;
                ALTER TABLE tickets ADD COLUMN IF NOT EXISTS ticket_service_fee numeric(12,2) NOT NULL DEFAULT 0;
                ALTER TABLE tickets ADD COLUMN IF NOT EXISTS ticket_service_fee_rate numeric(8,4) NOT NULL DEFAULT 0;
                ALTER TABLE tickets ADD COLUMN IF NOT EXISTS pricing_phase_name varchar(50) NOT NULL DEFAULT 'Pre-Sale';
                ALTER TABLE tickets ADD COLUMN IF NOT EXISTS pricing_phase_percentage integer NOT NULL DEFAULT 0;
                ALTER TABLE tickets ADD COLUMN IF NOT EXISTS created_at timestamptz NOT NULL DEFAULT NOW();
                ALTER TABLE tickets ADD COLUMN IF NOT EXISTS used_quantity integer NOT NULL DEFAULT 0;
                ALTER TABLE tickets ADD COLUMN IF NOT EXISTS used_ticket_units text NOT NULL DEFAULT '';
                ALTER TABLE tickets ADD COLUMN IF NOT EXISTS promo_applied boolean NOT NULL DEFAULT FALSE;
                ALTER TABLE tickets ADD COLUMN IF NOT EXISTS platform_fee numeric(12,2) NOT NULL DEFAULT 0;";

            using (var cmd = new NpgsqlCommand(alterSql, connection))
            {
                await cmd.ExecuteNonQueryAsync();
            }

            const string ticketDmlSql = @"
                UPDATE tickets
                SET used_quantity = COALESCE(quantity, 1)
                WHERE COALESCE(is_used, FALSE) = TRUE
                  AND COALESCE(used_quantity, 0) = 0;";

            using var dmlCmd = new NpgsqlCommand(ticketDmlSql, connection);
            await dmlCmd.ExecuteNonQueryAsync();
        }

        private static async Task<(Guid customerId, Guid organizerId, string eventTitle, int quantity, string customerName)> GetTicketNotificationContextAsync(NpgsqlConnection connection, Guid ticketId)
        {
            const string sql = @"
                SELECT
                    t.customer_id,
                    COALESCE(e.organizer_id, '00000000-0000-0000-0000-000000000000'::uuid),
                    COALESCE(e.title, 'Your event'),
                    COALESCE(t.quantity, 1),
                    COALESCE(u.firstname, ''),
                    COALESCE(u.lastname, '')
                FROM tickets t
                INNER JOIN events e ON e.id = t.event_id
                LEFT JOIN users u ON u.id = t.customer_id
                WHERE t.id = @id
                LIMIT 1;";

            await using var cmd = new NpgsqlCommand(sql, connection);
            cmd.Parameters.AddWithValue("@id", ticketId);
            await using var reader = await cmd.ExecuteReaderAsync();
            if (await reader.ReadAsync())
            {
                var first = reader.IsDBNull(4) ? "" : reader.GetString(4);
                var last = reader.IsDBNull(5) ? "" : reader.GetString(5);
                return (
                    reader.IsDBNull(0) ? Guid.Empty : reader.GetGuid(0),
                    reader.IsDBNull(1) ? Guid.Empty : reader.GetGuid(1),
                    reader.IsDBNull(2) ? "Your event" : reader.GetString(2),
                    reader.IsDBNull(3) ? 1 : reader.GetInt32(3),
                    string.IsNullOrWhiteSpace($"{first} {last}".Trim()) ? "A customer" : $"{first} {last}".Trim()
                );
            }

            return (Guid.Empty, Guid.Empty, "Your event", 1, "A customer");
        }

        [HttpGet("{ticketId}/download")]
        public async Task<IActionResult> DownloadTicketPdf(Guid ticketId)
        {
            try
            {
                await using var connection = new NpgsqlConnection(_connectionString);
                await connection.OpenAsync();

                const string sql = @"
                    SELECT
                        t.id, t.customer_id,
                        COALESCE(u.firstname, ''), COALESCE(u.lastname, ''),
                        COALESCE(e.title, 'Event'),
                        e.event_time,
                        COALESCE(e.location, ''), COALESCE(e.city, ''),
                        COALESCE(t.tier_name, 'General Admission'),
                        COALESCE(t.quantity, 1),
                        COALESCE(t.total_price, 0),
                        COALESCE(t.is_used, FALSE),
                        EXISTS (
                            SELECT 1 FROM payment_records pr
                            WHERE pr.ticket_id = t.id
                              AND LOWER(pr.status) = 'paid'
                        ) AS is_paid,
                        COALESCE(t.used_ticket_units, '')
                    FROM tickets t
                    INNER JOIN events e ON e.id = t.event_id
                    LEFT JOIN users u ON u.id = t.customer_id
                    WHERE t.id = @id
                    LIMIT 1;";

                await using var cmd = new NpgsqlCommand(sql, connection);
                cmd.Parameters.AddWithValue("@id", ticketId);
                await using var reader = await cmd.ExecuteReaderAsync();

                if (!await reader.ReadAsync())
                    return NotFound(new { message = "Ticket not found." });

                var customerId = reader.IsDBNull(1) ? Guid.Empty : reader.GetGuid(1);

                // Auth check — manual so browser navigation gets a clean redirect instead of 401 challenge
                var actorId = User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value;
                var actorRole = User.FindFirst(System.Security.Claims.ClaimTypes.Role)?.Value ?? "";
                if (string.IsNullOrWhiteSpace(actorId))
                {
                    // Unauthenticated browser navigation → redirect to login cleanly
                    return Redirect($"/pages/auth/login.html?returnTo=/api/ticket/{ticketId}/download");
                }
                if (!actorRole.Equals("Admin", StringComparison.OrdinalIgnoreCase)
                    && (!Guid.TryParse(actorId, out var aid) || aid != customerId))
                {
                    return StatusCode(403, new { message = "You are not authorised to download this ticket." });
                }

                var isPaid = !reader.IsDBNull(12) && reader.GetBoolean(12);
                if (!isPaid)
                {
                    return BadRequest(new { message = "Ticket payment has not been confirmed yet." });
                }

                var first = reader.IsDBNull(2) ? "" : reader.GetString(2);
                var last = reader.IsDBNull(3) ? "" : reader.GetString(3);
                var customerName = $"{first} {last}".Trim();
                if (string.IsNullOrWhiteSpace(customerName)) customerName = "Ticket Holder";

                var eventDate = reader.IsDBNull(5) ? null : (DateTime?)reader.GetDateTime(5);
                var dateStr = eventDate.HasValue
                    ? eventDate.Value.ToLocalTime().ToString("ddd, MMM d yyyy · h:mm tt")
                    : "Date TBA";

                var usedUnitsStr = reader.IsDBNull(13) ? "" : reader.GetString(13);
                var usedUnitsSet = ParseUsedTicketUnits(usedUnitsStr).ToHashSet();

                var data = new ImajinationAPI.Services.TicketPdfService.TicketPdfData(
                    TicketId: ticketId,
                    CustomerName: customerName,
                    EventTitle: reader.IsDBNull(4) ? "Event" : reader.GetString(4),
                    EventDate: dateStr,
                    Venue: reader.IsDBNull(6) ? "" : reader.GetString(6),
                    City: reader.IsDBNull(7) ? "" : reader.GetString(7),
                    TierName: reader.IsDBNull(8) ? "General Admission" : reader.GetString(8),
                    Quantity: reader.IsDBNull(9) ? 1 : reader.GetInt32(9),
                    TotalPrice: reader.IsDBNull(10) ? 0m : reader.GetDecimal(10),
                    OrderRef: ticketId.ToString()[..8],
                    IsUsed: !reader.IsDBNull(11) && reader.GetBoolean(11),
                    UsedUnits: usedUnitsSet
                );

                var pdfBytes = _ticketPdfService.GenerateTicketPdf(data);
                var fileName = $"ticket-{ticketId.ToString()[..8].ToUpper()}.pdf";
                return File(pdfBytes, "application/pdf", fileName);
            }
            catch (Exception ex)
            {
                return StatusCode(500, new { message = "Failed to generate ticket PDF: " + ex.Message });
            }
        }

        private static string AppendQuery(string? url, string query)
        {
            if (string.IsNullOrWhiteSpace(url))
            {
                return "";
            }

            var separator = url.Contains('?') ? "&" : "?";
            return $"{url}{separator}{query}";
        }
    }
}
