using System.Security.Claims;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using ImajinationAPI.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Npgsql;
using NpgsqlTypes;

namespace ImajinationAPI.Controllers;

public class WalletTopUpRequest
{
    public decimal amount { get; set; }
    public string? successUrl { get; set; }
    public string? cancelUrl { get; set; }
}

[Route("api/wallet")]
[ApiController]
[Authorize]
public class WalletController : ControllerBase
{
    private readonly string _connectionString;
    private readonly string _paymongoSecretKey;

    public WalletController(IConfiguration configuration)
    {
        _connectionString = ConfigurationFallbacks.GetRequiredSupabaseConnectionString(configuration);
        _paymongoSecretKey = configuration["PayMongo:SecretKey"] ?? string.Empty;
    }

    [HttpGet("me")]
    public async Task<IActionResult> GetMyWallet()
    {
        if (!Guid.TryParse(User.FindFirst(ClaimTypes.NameIdentifier)?.Value, out var userId)) return Unauthorized();
        await using var connection = new NpgsqlConnection(_connectionString);
        await connection.OpenAsync();
        var wallet = await EscrowService.GetWalletSummaryAsync(connection, userId);
        return Ok(new { availableBalance = wallet.AvailableBalance, heldBalance = wallet.HeldBalance, pendingBalance = wallet.PendingBalance, currency = wallet.Currency });
    }

    [HttpGet("me/transactions")]
    public async Task<IActionResult> GetMyTransactions([FromQuery] int limit = 25, [FromQuery] int offset = 0)
    {
        if (!Guid.TryParse(User.FindFirst(ClaimTypes.NameIdentifier)?.Value, out var userId)) return Unauthorized();
        await using var connection = new NpgsqlConnection(_connectionString);
        await connection.OpenAsync();
        var items = await EscrowService.GetLedgerAsync(connection, userId, limit, offset);
        return Ok(items.Select(item => new { item.Id, item.BookingId, item.EntryType, item.Direction, item.Amount, item.Currency, item.ReferenceType, item.ReferenceId, item.CreatedAt }));
    }

    [HttpPost("topups")]
    public async Task<IActionResult> CreateTopUp([FromBody] WalletTopUpRequest req)
    {
        if (!Guid.TryParse(User.FindFirst(ClaimTypes.NameIdentifier)?.Value, out var userId)) return Unauthorized();
        if (req.amount > 1000000m || decimal.Round(req.amount, 2) != req.amount)
            return BadRequest(new { message = "Use an amount up to PHP 1,000,000 with at most two decimal places." });
        if (req.amount < 15m) return BadRequest(new { message = "Wallet top-ups must be at least PHP 15.00." });
        if (string.IsNullOrWhiteSpace(req.successUrl) || string.IsNullOrWhiteSpace(req.cancelUrl))
            return BadRequest(new { message = "Success and cancel URLs are required." });
        if (string.IsNullOrWhiteSpace(_paymongoSecretKey))
            return StatusCode(500, new { message = "PayMongo is not configured yet." });

        await using var connection = new NpgsqlConnection(_connectionString);
        await connection.OpenAsync();
        var topUpId = await EscrowService.CreateTopUpAsync(connection, userId, req.amount);
        var successUrl = AppendQuery(req.successUrl, $"walletTopUpId={topUpId}");
        var payload = new
        {
            data = new
            {
                attributes = new
                {
                    send_email_receipt = false,
                    show_description = true,
                    show_line_items = true,
                    payment_method_types = new[] { "gcash", "card", "paymaya" },
                    line_items = new[] { new { currency = "PHP", amount = (int)(req.amount * 100), name = "Imajination Wallet Top-up", quantity = 1 } },
                    success_url = successUrl,
                    cancel_url = req.cancelUrl,
                    metadata = new { wallet_topup_id = topUpId.ToString(), user_id = userId.ToString() }
                }
            }
        };
        using var client = new HttpClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes(_paymongoSecretKey)));
        client.DefaultRequestHeaders.Add("Idempotency-Key", $"wallet-topup-{topUpId:N}");
        var response = await client.PostAsync("https://api.paymongo.com/v1/checkout_sessions", new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json"));
        var responseBody = await response.Content.ReadAsStringAsync();
        if (!response.IsSuccessStatusCode) return StatusCode((int)response.StatusCode, new { message = "PayMongo could not create the wallet top-up checkout." });

        using var doc = JsonDocument.Parse(responseBody);
        var data = doc.RootElement.GetProperty("data");
        var checkoutId = data.GetProperty("id").GetString() ?? string.Empty;
        var checkoutUrl = data.GetProperty("attributes").GetProperty("checkout_url").GetString();
        await EscrowService.LinkTopUpCheckoutAsync(connection, topUpId, checkoutId);
        return Ok(new { topUpId, checkoutUrl, amount = req.amount });
    }

    [HttpPost("topups/{topUpId}/confirm")]
    public async Task<IActionResult> ConfirmTopUp(Guid topUpId)
    {
        if (!Guid.TryParse(User.FindFirst(ClaimTypes.NameIdentifier)?.Value, out var userId)) return Unauthorized();
        if (string.IsNullOrWhiteSpace(_paymongoSecretKey)) return StatusCode(500, new { message = "PayMongo is not configured yet." });
        await using var connection = new NpgsqlConnection(_connectionString);
        await connection.OpenAsync();
        await EscrowService.EnsureSchemaAsync(connection);
        const string sql = "SELECT provider_checkout_id, amount FROM wallet_topups WHERE id=@id AND user_id=@userId LIMIT 1;";
        await using var cmd = new NpgsqlCommand(sql, connection);
        cmd.Parameters.Add("@id", NpgsqlDbType.Uuid).Value = topUpId;
        cmd.Parameters.Add("@userId", NpgsqlDbType.Uuid).Value = userId;
        string? checkoutId;
        decimal expectedAmount;
        await using (var reader = await cmd.ExecuteReaderAsync())
        {
            if (!await reader.ReadAsync()) return NotFound(new { message = "Wallet top-up not found." });
            checkoutId = reader.IsDBNull(0) ? null : reader.GetString(0);
            expectedAmount = reader.GetDecimal(1);
        }
        if (string.IsNullOrWhiteSpace(checkoutId)) return NotFound(new { message = "Wallet top-up not found." });
        using var client = new HttpClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes(_paymongoSecretKey)));
        var response = await client.GetAsync($"https://api.paymongo.com/v1/checkout_sessions/{checkoutId}");
        if (!response.IsSuccessStatusCode) return StatusCode((int)response.StatusCode, new { message = "PayMongo could not verify this top-up." });
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var attributes = doc.RootElement.GetProperty("data").GetProperty("attributes");
        if (!attributes.TryGetProperty("payments", out var payments) || payments.ValueKind != JsonValueKind.Array || payments.GetArrayLength() == 0)
            return Ok(new { message = "Top-up payment is still pending.", paymentStatus = "PendingPayment" });
        if (!PayMongoPaymentVerification.TryGetPaidPayment(attributes, expectedAmount, out var paidPayment))
            return Conflict(new { message = "No paid payment matches the expected top-up amount and currency." });
        var paymentId = paidPayment.GetProperty("id").GetString();
        var confirmed = await EscrowService.ConfirmTopUpAsync(connection, topUpId, userId, paymentId);
        if (!confirmed) return Conflict(new { message = "This top-up cannot be confirmed in its current state." });
        var wallet = await EscrowService.GetWalletSummaryAsync(connection, userId);
        return Ok(new { message = "Wallet top-up confirmed.", availableBalance = wallet.AvailableBalance, currency = wallet.Currency });
    }

    [HttpPost("bookings/{bookingId}/reserve")]
    public async Task<IActionResult> ReserveBookingEscrow(Guid bookingId)
    {
        if (!Guid.TryParse(User.FindFirst(ClaimTypes.NameIdentifier)?.Value, out var userId)) return Unauthorized();
        await using var connection = new NpgsqlConnection(_connectionString);
        await connection.OpenAsync();
        await EscrowService.EnsureSchemaAsync(connection);
        const string sql = @"
            SELECT customer_id, target_user_id, COALESCE(budget, 0), COALESCE(status, ''), COALESCE(talent_fee_status, 'Unpaid')
            FROM bookings WHERE id = @id LIMIT 1;";
        Guid customerId;
        Guid targetUserId;
        decimal amount;
        string bookingStatus;
        string talentFeeStatus;
        await using (var cmd = new NpgsqlCommand(sql, connection))
        {
            cmd.Parameters.Add("@id", NpgsqlDbType.Uuid).Value = bookingId;
            await using var reader = await cmd.ExecuteReaderAsync();
            if (!await reader.ReadAsync()) return NotFound(new { message = "Booking not found." });
            customerId = reader.GetGuid(0);
            targetUserId = reader.GetGuid(1);
            amount = reader.GetDecimal(2);
            bookingStatus = reader.GetString(3);
            talentFeeStatus = reader.GetString(4);
        }
        if (userId != customerId) return Forbid();
        if (!bookingStatus.Equals("Confirmed", StringComparison.OrdinalIgnoreCase) || amount <= 0)
            return BadRequest(new { message = "Only confirmed bookings with an agreed talent fee can be funded from a wallet." });
        if (talentFeeStatus is "ReadyForRelease" or "Released")
            return Conflict(new { message = "The talent fee is already secured for this booking." });

        try
        {
            await EscrowService.ReserveFromWalletAsync(connection, bookingId, customerId, targetUserId, amount);
            var wallet = await EscrowService.GetWalletSummaryAsync(connection, userId);
            return Ok(new { message = "Talent fee reserved in escrow from your wallet.", availableBalance = wallet.AvailableBalance, heldBalance = wallet.HeldBalance });
        }
        catch (InvalidOperationException ex)
        {
            return Conflict(new { message = ex.Message });
        }
    }

    private static string AppendQuery(string url, string query)
    {
        var separator = url.Contains('?', StringComparison.Ordinal) ? "&" : "?";
        return url + separator + query;
    }
}
