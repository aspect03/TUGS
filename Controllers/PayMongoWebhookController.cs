using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Npgsql;
using NpgsqlTypes;

namespace ImajinationAPI.Controllers
{
    [ApiController]
    [AllowAnonymous]
    [Route("api/ticket/paymongo")]
    public sealed class PayMongoWebhookController : ControllerBase
    {
        private const int MaxWebhookBodyBytes = 256 * 1024;
        private static readonly TimeSpan MaxWebhookAge = TimeSpan.FromMinutes(5);
        private readonly string _connectionString;
        private readonly string _webhookSecret;
        private readonly bool _isTestMode;
        private readonly ILogger<PayMongoWebhookController> _logger;

        public PayMongoWebhookController(IConfiguration configuration, ILogger<PayMongoWebhookController> logger)
        {
            _connectionString = ConfigurationFallbacks.GetRequiredSupabaseConnectionString(configuration);
            _webhookSecret = configuration["PayMongo:WebhookSecret"] ?? string.Empty;
            _isTestMode = (configuration["PayMongo:SecretKey"] ?? string.Empty).StartsWith("sk_test_", StringComparison.Ordinal);
            _logger = logger;
        }

        [HttpPost("webhook")]
        [Consumes("application/json")]
        public async Task<IActionResult> Receive(CancellationToken cancellationToken)
        {
            if (string.IsNullOrWhiteSpace(_webhookSecret))
            {
                _logger.LogError("PayMongo webhook received before PayMongo:WebhookSecret was configured.");
                return StatusCode(StatusCodes.Status503ServiceUnavailable);
            }

            if (Request.ContentLength is > MaxWebhookBodyBytes)
            {
                return StatusCode(StatusCodes.Status413PayloadTooLarge);
            }

            await using var body = new MemoryStream();
            await Request.Body.CopyToAsync(body, cancellationToken);
            var rawBody = body.ToArray();
            if (rawBody.Length == 0 || rawBody.Length > MaxWebhookBodyBytes ||
                !TryVerifySignature(Request.Headers["Paymongo-Signature"], rawBody))
            {
                return Unauthorized();
            }

            try
            {
                using var document = JsonDocument.Parse(rawBody);
                var root = document.RootElement;
                var eventId = root.GetProperty("data").GetProperty("id").GetString();
                var attributes = root.GetProperty("data").GetProperty("attributes");
                var eventType = attributes.GetProperty("type").GetString();
                var liveMode = attributes.TryGetProperty("livemode", out var liveModeValue) && liveModeValue.ValueKind == JsonValueKind.True;
                var resourceId = attributes.TryGetProperty("data", out var resource) && resource.ValueKind == JsonValueKind.Object && resource.TryGetProperty("id", out var resourceIdValue)
                    ? resourceIdValue.GetString()
                    : null;

                if (string.IsNullOrWhiteSpace(eventId) || string.IsNullOrWhiteSpace(eventType)) return BadRequest();

                await using var connection = new NpgsqlConnection(_connectionString);
                await connection.OpenAsync(cancellationToken);
                await EnsureWebhookSchemaAsync(connection, cancellationToken);

                const string insertSql = @"
                    INSERT INTO paymongo_webhook_events (event_id, event_type, provider_resource_id, livemode, received_at)
                    VALUES (@eventId, @eventType, @resourceId, @livemode, NOW())
                    ON CONFLICT (event_id) DO NOTHING;";
                await using var command = new NpgsqlCommand(insertSql, connection);
                command.Parameters.AddWithValue("@eventId", eventId);
                command.Parameters.Add("@eventType", NpgsqlDbType.Text).Value = eventType;
                command.Parameters.Add("@resourceId", NpgsqlDbType.Text).Value = (object?)resourceId ?? DBNull.Value;
                command.Parameters.Add("@livemode", NpgsqlDbType.Boolean).Value = liveMode;
                await command.ExecuteNonQueryAsync(cancellationToken);

                _logger.LogInformation("Verified PayMongo webhook {EventType} ({EventId}) received.", eventType, eventId);
                return Ok(new { received = true });
            }
            catch (JsonException)
            {
                return BadRequest();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Unable to record a verified PayMongo webhook.");
                return StatusCode(StatusCodes.Status500InternalServerError);
            }
        }

        private bool TryVerifySignature(string? headerValue, byte[] rawBody)
        {
            if (string.IsNullOrWhiteSpace(headerValue)) return false;

            var parts = headerValue.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(part => part.Split('=', 2, StringSplitOptions.None))
                .Where(pair => pair.Length == 2)
                .ToDictionary(pair => pair[0], pair => pair[1], StringComparer.OrdinalIgnoreCase);

            if (!parts.TryGetValue("t", out var timestampText) ||
                !long.TryParse(timestampText, out var unixTimestamp) ||
                Math.Abs(DateTimeOffset.UtcNow.ToUnixTimeSeconds() - unixTimestamp) > MaxWebhookAge.TotalSeconds)
            {
                return false;
            }

            var signatureKey = _isTestMode ? "te" : "li";
            if (!parts.TryGetValue(signatureKey, out var providedSignature) || string.IsNullOrWhiteSpace(providedSignature)) return false;

            var prefix = Encoding.UTF8.GetBytes(timestampText + ".");
            var signedPayload = new byte[prefix.Length + rawBody.Length];
            Buffer.BlockCopy(prefix, 0, signedPayload, 0, prefix.Length);
            Buffer.BlockCopy(rawBody, 0, signedPayload, prefix.Length, rawBody.Length);

            var expectedSignature = HMACSHA256.HashData(Encoding.UTF8.GetBytes(_webhookSecret), signedPayload);
            try
            {
                var providedBytes = Convert.FromHexString(providedSignature);
                return CryptographicOperations.FixedTimeEquals(expectedSignature, providedBytes);
            }
            catch (FormatException)
            {
                return false;
            }
        }

        private static async Task EnsureWebhookSchemaAsync(NpgsqlConnection connection, CancellationToken cancellationToken)
        {
            const string sql = @"
                CREATE TABLE IF NOT EXISTS paymongo_webhook_events (
                    event_id text PRIMARY KEY,
                    event_type text NOT NULL,
                    provider_resource_id text NULL,
                    livemode boolean NOT NULL,
                    received_at timestamptz NOT NULL DEFAULT NOW()
                );";
            await using var command = new NpgsqlCommand(sql, connection);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
    }
}