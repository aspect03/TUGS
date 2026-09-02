using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Npgsql;
using System.Security.Claims;

namespace ImajinationAPI.Controllers
{
    public class SavePushSubscriptionRequest
    {
        public Guid userId { get; set; }
        public string? endpoint { get; set; }
        public string? p256dh { get; set; }
        public string? auth { get; set; }
    }

    public class RemovePushSubscriptionRequest
    {
        public Guid userId { get; set; }
        public string? endpoint { get; set; }
    }

    [Route("api/push")]
    [ApiController]
    [Authorize]
    public class PushSubscriptionController : ControllerBase
    {
        private readonly string _connectionString;
        private readonly IConfiguration _configuration;

        public PushSubscriptionController(IConfiguration configuration)
        {
            _configuration = configuration;
            _connectionString = ConfigurationFallbacks.GetRequiredSupabaseConnectionString(configuration);
        }

        private static Guid? GetActorUserId(ClaimsPrincipal user) =>
            Guid.TryParse(user.FindFirstValue(ClaimTypes.NameIdentifier), out var userId) ? userId : null;

        private static string GetActorRole(ClaimsPrincipal user) => user.FindFirstValue(ClaimTypes.Role) ?? "User";

        private static bool CanManage(Guid actorUserId, string actorRole, Guid requestedUserId) =>
            actorUserId == requestedUserId || actorRole.Equals("Admin", StringComparison.OrdinalIgnoreCase);

        [AllowAnonymous]
        [HttpGet("public-key")]
        public IActionResult GetPublicKey()
        {
            var publicKey = _configuration["PushNotifications:VapidPublicKey"]
                ?? _configuration["PushNotifications__VapidPublicKey"]
                ?? string.Empty;
            return Ok(new { publicKey });
        }

        private static async Task EnsurePushSchemaAsync(NpgsqlConnection connection)
        {
            const string sql = @"
                CREATE TABLE IF NOT EXISTS push_subscriptions (
                    id uuid PRIMARY KEY,
                    user_id uuid NOT NULL,
                    endpoint text NOT NULL,
                    p256dh text NOT NULL,
                    auth text NOT NULL,
                    user_agent text NULL,
                    created_at timestamptz NOT NULL DEFAULT NOW(),
                    last_seen_at timestamptz NOT NULL DEFAULT NOW(),
                    revoked_at timestamptz NULL,
                    UNIQUE (user_id, endpoint)
                );
                CREATE INDEX IF NOT EXISTS idx_push_subscriptions_user_id ON push_subscriptions(user_id);";
            await using var cmd = new NpgsqlCommand(sql, connection);
            await cmd.ExecuteNonQueryAsync();
        }

        [HttpPost("subscribe")]
        public async Task<IActionResult> Subscribe([FromBody] SavePushSubscriptionRequest req)
        {
            var actorUserId = GetActorUserId(User);
            if (!actorUserId.HasValue) return Unauthorized(new { message = "Sign in again before enabling push notifications." });
            if (!CanManage(actorUserId.Value, GetActorRole(User), req.userId)) return Forbid();
            if (string.IsNullOrWhiteSpace(req.endpoint) || string.IsNullOrWhiteSpace(req.p256dh) || string.IsNullOrWhiteSpace(req.auth))
            {
                return BadRequest(new { message = "Push subscription is incomplete." });
            }

            await using var connection = new NpgsqlConnection(_connectionString);
            await connection.OpenAsync();
            await EnsurePushSchemaAsync(connection);

            const string sql = @"
                INSERT INTO push_subscriptions (id, user_id, endpoint, p256dh, auth, user_agent, created_at, last_seen_at, revoked_at)
                VALUES (@id, @userId, @endpoint, @p256dh, @auth, @userAgent, NOW(), NOW(), NULL)
                ON CONFLICT (user_id, endpoint)
                DO UPDATE SET p256dh = EXCLUDED.p256dh, auth = EXCLUDED.auth, user_agent = EXCLUDED.user_agent, last_seen_at = NOW(), revoked_at = NULL;";
            await using var cmd = new NpgsqlCommand(sql, connection);
            cmd.Parameters.AddWithValue("@id", Guid.NewGuid());
            cmd.Parameters.AddWithValue("@userId", req.userId);
            cmd.Parameters.AddWithValue("@endpoint", req.endpoint.Trim());
            cmd.Parameters.AddWithValue("@p256dh", req.p256dh.Trim());
            cmd.Parameters.AddWithValue("@auth", req.auth.Trim());
            cmd.Parameters.AddWithValue("@userAgent", Request.Headers.UserAgent.ToString());
            await cmd.ExecuteNonQueryAsync();
            return Ok(new { subscribed = true, message = "Push notifications enabled." });
        }

        [HttpDelete("unsubscribe")]
        public async Task<IActionResult> Unsubscribe([FromBody] RemovePushSubscriptionRequest req)
        {
            var actorUserId = GetActorUserId(User);
            if (!actorUserId.HasValue) return Unauthorized(new { message = "Sign in again before changing push notifications." });
            if (!CanManage(actorUserId.Value, GetActorRole(User), req.userId)) return Forbid();

            await using var connection = new NpgsqlConnection(_connectionString);
            await connection.OpenAsync();
            await EnsurePushSchemaAsync(connection);
            var sql = "UPDATE push_subscriptions SET revoked_at = NOW(), last_seen_at = NOW() WHERE user_id = @userId";
            if (!string.IsNullOrWhiteSpace(req.endpoint)) sql += " AND endpoint = @endpoint";
            await using var cmd = new NpgsqlCommand(sql, connection);
            cmd.Parameters.AddWithValue("@userId", req.userId);
            if (!string.IsNullOrWhiteSpace(req.endpoint)) cmd.Parameters.AddWithValue("@endpoint", req.endpoint.Trim());
            var rows = await cmd.ExecuteNonQueryAsync();
            return Ok(new { subscribed = false, updated = rows });
        }

        [HttpGet("user/{userId}/status")]
        public async Task<IActionResult> GetStatus(Guid userId)
        {
            var actorUserId = GetActorUserId(User);
            if (!actorUserId.HasValue) return Unauthorized(new { message = "Sign in again before viewing push status." });
            if (!CanManage(actorUserId.Value, GetActorRole(User), userId)) return Forbid();

            await using var connection = new NpgsqlConnection(_connectionString);
            await connection.OpenAsync();
            await EnsurePushSchemaAsync(connection);
            await using var cmd = new NpgsqlCommand("SELECT COUNT(*) FROM push_subscriptions WHERE user_id = @userId AND revoked_at IS NULL;", connection);
            cmd.Parameters.AddWithValue("@userId", userId);
            var count = Convert.ToInt32(await cmd.ExecuteScalarAsync() ?? 0);
            return Ok(new { subscribed = count > 0, subscriptionCount = count });
        }
    }
}
