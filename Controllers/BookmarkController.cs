using Microsoft.AspNetCore.Mvc;
using Npgsql;

namespace ImajinationAPI.Controllers
{
    public class ToggleBookmarkRequest
    {
        public Guid userId { get; set; }
        public Guid targetId { get; set; }
        public string? targetType { get; set; }
        public string? title { get; set; }
        public string? url { get; set; }
    }

    [Route("api/[controller]")]
    [ApiController]
    public class BookmarkController : ControllerBase
    {
        private readonly string _connectionString;

        public BookmarkController(IConfiguration configuration)
        {
            _connectionString = ConfigurationFallbacks.GetRequiredSupabaseConnectionString(configuration);
        }

        private static string NormalizeTargetType(string? raw)
        {
            var value = (raw ?? "").Trim().ToLowerInvariant();
            return value is "event" or "organizer" or "recruitment" or "community" ? value : "event";
        }

        private static async Task EnsureBookmarksSchemaAsync(NpgsqlConnection connection)
        {
            const string sql = @"
                CREATE TABLE IF NOT EXISTS saved_items (
                    id uuid PRIMARY KEY,
                    user_id uuid NOT NULL,
                    target_id uuid NOT NULL,
                    target_type varchar(40) NOT NULL,
                    title text NOT NULL DEFAULT '',
                    url text NOT NULL DEFAULT '',
                    created_at timestamptz NOT NULL DEFAULT NOW(),
                    UNIQUE (user_id, target_id, target_type)
                );
                CREATE INDEX IF NOT EXISTS idx_saved_items_user_type ON saved_items(user_id, target_type);";
            await using var cmd = new NpgsqlCommand(sql, connection);
            await cmd.ExecuteNonQueryAsync();
        }

        [HttpGet("user/{userId}")]
        public async Task<IActionResult> GetUserBookmarks(Guid userId, [FromQuery] string? targetType = null)
        {
            try
            {
                await using var connection = new NpgsqlConnection(_connectionString);
                await connection.OpenAsync();
                await EnsureBookmarksSchemaAsync(connection);

                var sql = "SELECT id, target_id, target_type, title, url, created_at FROM saved_items WHERE user_id = @userId";
                if (!string.IsNullOrWhiteSpace(targetType)) sql += " AND target_type = @targetType";
                sql += " ORDER BY created_at DESC;";

                var items = new List<object>();
                await using var cmd = new NpgsqlCommand(sql, connection);
                cmd.Parameters.AddWithValue("@userId", userId);
                if (!string.IsNullOrWhiteSpace(targetType)) cmd.Parameters.AddWithValue("@targetType", NormalizeTargetType(targetType));
                await using var reader = await cmd.ExecuteReaderAsync();
                while (await reader.ReadAsync())
                {
                    items.Add(new
                    {
                        id = reader.GetGuid(0),
                        targetId = reader.GetGuid(1),
                        targetType = reader.GetString(2),
                        title = reader.IsDBNull(3) ? "" : reader.GetString(3),
                        url = reader.IsDBNull(4) ? "" : reader.GetString(4),
                        createdAt = reader.IsDBNull(5) ? DateTime.UtcNow : reader.GetDateTime(5)
                    });
                }
                return Ok(items);
            }
            catch (Exception ex)
            {
                return StatusCode(500, new { message = "Failed to load saved items: " + ex.Message });
            }
        }

        [HttpPost("toggle")]
        public async Task<IActionResult> ToggleBookmark([FromBody] ToggleBookmarkRequest req)
        {
            if (req.userId == Guid.Empty || req.targetId == Guid.Empty)
            {
                return BadRequest(new { message = "Missing saved item details." });
            }

            var targetType = NormalizeTargetType(req.targetType);
            await using var connection = new NpgsqlConnection(_connectionString);
            await connection.OpenAsync();
            await EnsureBookmarksSchemaAsync(connection);

            await using (var deleteCmd = new NpgsqlCommand("DELETE FROM saved_items WHERE user_id = @userId AND target_id = @targetId AND target_type = @targetType;", connection))
            {
                deleteCmd.Parameters.AddWithValue("@userId", req.userId);
                deleteCmd.Parameters.AddWithValue("@targetId", req.targetId);
                deleteCmd.Parameters.AddWithValue("@targetType", targetType);
                if (await deleteCmd.ExecuteNonQueryAsync() > 0)
                {
                    return Ok(new { saved = false, message = "Removed from saved items." });
                }
            }

            const string insertSql = @"
                INSERT INTO saved_items (id, user_id, target_id, target_type, title, url, created_at)
                VALUES (@id, @userId, @targetId, @targetType, @title, @url, NOW())
                ON CONFLICT (user_id, target_id, target_type) DO NOTHING;";
            await using var insertCmd = new NpgsqlCommand(insertSql, connection);
            insertCmd.Parameters.AddWithValue("@id", Guid.NewGuid());
            insertCmd.Parameters.AddWithValue("@userId", req.userId);
            insertCmd.Parameters.AddWithValue("@targetId", req.targetId);
            insertCmd.Parameters.AddWithValue("@targetType", targetType);
            insertCmd.Parameters.AddWithValue("@title", req.title ?? "");
            insertCmd.Parameters.AddWithValue("@url", req.url ?? "");
            await insertCmd.ExecuteNonQueryAsync();

            return Ok(new { saved = true, message = "Saved." });
        }
    }
}
