using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Authorization;
using Npgsql;
using NpgsqlTypes;
using ImajinationAPI.Services;

namespace ImajinationAPI.Controllers
{
    public class CreateCommunityPostDto
    {
        public Guid userId { get; set; }
        public string? role { get; set; }
        public string? content { get; set; }
        public string? imageUrl { get; set; }
        public string? category { get; set; }      // GigAnnouncement | OpenCall | StudioAvailable | CollabWanted | General
        public Guid? eventId { get; set; }         // linked event
        public string? pollQuestion { get; set; }
        public List<string>? pollOptions { get; set; }
    }

    public class ToggleCommunityLikeDto
    {
        public Guid userId { get; set; }
    }

    public class AddCommentDto
    {
        public Guid userId { get; set; }
        public string? content { get; set; }
        public Guid? parentId { get; set; }
    }

    public class PollVoteDto
    {
        public Guid userId { get; set; }
    }

    public class FollowDto
    {
        public Guid followerId { get; set; }
        public Guid targetUserId { get; set; }
    }

    [Route("api/[controller]")]
    [ApiController]
    public class CommunityController : ControllerBase
    {
        private readonly string _connectionString;
        private readonly UploadScanningService _uploadScanningService;

        // Run schema DDL only once per app lifetime, not on every request
        private static volatile bool _extendedSchemaReady = false;
        private static readonly SemaphoreSlim _schemaSem = new(1, 1);

        public CommunityController(IConfiguration configuration, UploadScanningService uploadScanningService)
        {
            _connectionString = ConfigurationFallbacks.GetRequiredSupabaseConnectionString(configuration);
            _uploadScanningService = uploadScanningService;
        }

        [HttpGet("feed")]
        public async Task<IActionResult> GetFeed([FromQuery] string? role = null, [FromQuery] int limit = 24, [FromQuery] Guid? userId = null)
        {
            try
            {
                await using var connection = new NpgsqlConnection(_connectionString);
                await connection.OpenAsync();
                await CommunitySupport.EnsureCommunitySchemaAsync(connection);
                await EnsureCommunityPostLikesTableAsync(connection);

                var normalizedRole = (role ?? string.Empty).Trim();
                var hasRoleFilter =
                    normalizedRole.Equals("Artist", StringComparison.OrdinalIgnoreCase) ||
                    normalizedRole.Equals("Sessionist", StringComparison.OrdinalIgnoreCase);

                var safeLimit = Math.Clamp(limit, 1, 60);
                var posts = new List<object>();

                await EnsureCommunityExtendedSchemaAsync(connection);

                var sql = @"
                    SELECT cp.id,
                           cp.user_id,
                           cp.role,
                           cp.content,
                           cp.image_url,
                           cp.created_at,
                           COALESCE(u.firstname, ''),
                           COALESCE(u.lastname, ''),
                           COALESCE(u.stagename, ''),
                           COALESCE(u.productionname, ''),
                           COALESCE(u.profile_picture, ''),
                           COALESCE(u.is_verified, FALSE),
                           COALESCE((SELECT COUNT(*) FROM community_post_likes cpl WHERE cpl.post_id = cp.id), 0),
                           EXISTS (SELECT 1 FROM community_post_likes cpl WHERE cpl.post_id = cp.id AND cpl.user_id = @userId),
                           COALESCE(cp.category, 'General'),
                           cp.event_id,
                           COALESCE((SELECT COUNT(*) FROM community_post_comments cpc WHERE cpc.post_id = cp.id), 0),
                           EXISTS (SELECT 1 FROM community_follows cf WHERE cf.followed_id = cp.user_id AND cf.follower_id = @userId)
                    FROM community_posts cp
                    JOIN users u ON u.id = cp.user_id
                    WHERE (@hasRoleFilter = FALSE OR LOWER(cp.role) = LOWER(@role))
                    ORDER BY cp.created_at DESC
                    LIMIT @limit;";

                using var cmd = new NpgsqlCommand(sql, connection);
                cmd.Parameters.Add("@hasRoleFilter", NpgsqlDbType.Boolean).Value = hasRoleFilter;
                cmd.Parameters.Add("@role", NpgsqlDbType.Text).Value = normalizedRole;
                cmd.Parameters.Add("@limit", NpgsqlDbType.Integer).Value = safeLimit;
                cmd.Parameters.Add("@userId", NpgsqlDbType.Uuid).Value = (object?)userId ?? DBNull.Value;

                using var reader = await cmd.ExecuteReaderAsync();
                while (await reader.ReadAsync())
                {
                    var postRole = reader.IsDBNull(2) ? "" : reader.GetString(2);
                    var postEventId = reader.IsDBNull(15) ? (Guid?)null : reader.GetGuid(15);
                    posts.Add(new
                    {
                        id = reader.GetGuid(0),
                        userId = reader.GetGuid(1),
                        role = postRole,
                        content = reader.IsDBNull(3) ? "" : reader.GetString(3),
                        imageUrl = CommunitySupport.NormalizeListImage(
                            reader.IsDBNull(4) ? "" : reader.GetString(4),
                            string.Empty,
                            2_500_000),
                        createdAt = reader.IsDBNull(5) ? DateTime.UtcNow : reader.GetDateTime(5),
                        authorName = CommunitySupport.BuildDisplayName(
                            reader.IsDBNull(6) ? "" : reader.GetString(6),
                            reader.IsDBNull(7) ? "" : reader.GetString(7),
                            reader.IsDBNull(8) ? "" : reader.GetString(8),
                            reader.IsDBNull(9) ? "" : reader.GetString(9),
                            postRole),
                        authorProfilePicture = CommunitySupport.NormalizeListImage(
                            reader.IsDBNull(10) ? "" : reader.GetString(10),
                            string.Empty),
                        authorVerified = !reader.IsDBNull(11) && reader.GetBoolean(11),
                        likesCount = reader.IsDBNull(12) ? 0 : Convert.ToInt32(reader.GetInt64(12)),
                        isLiked = !reader.IsDBNull(13) && reader.GetBoolean(13),
                        category = reader.IsDBNull(14) ? "General" : reader.GetString(14),
                        eventId = postEventId,
                        commentsCount = reader.IsDBNull(16) ? 0 : Convert.ToInt32(reader.GetInt64(16)),
                        isFollowing = !reader.IsDBNull(17) && reader.GetBoolean(17)
                    });
                }

                return Ok(posts);
            }
            catch (Exception ex)
            {
                return StatusCode(500, new { message = ex.Message });
            }
        }

        [HttpGet("posts/{role}/{userId}")]
        public async Task<IActionResult> GetPosts(string role, Guid userId)
        {
            try
            {
                await using var connection = new NpgsqlConnection(_connectionString);
                await connection.OpenAsync();
                await CommunitySupport.EnsureCommunitySchemaAsync(connection);

                const string sql = @"
                    SELECT cp.id,
                           cp.content,
                           cp.image_url,
                           cp.created_at,
                           COALESCE(u.firstname, ''),
                           COALESCE(u.lastname, ''),
                           COALESCE(u.stagename, ''),
                           COALESCE(u.productionname, ''),
                           COALESCE(u.profile_picture, ''),
                           COALESCE(u.is_verified, FALSE)
                    FROM community_posts cp
                    JOIN users u ON u.id = cp.user_id
                    WHERE cp.user_id = @userId
                      AND LOWER(cp.role) = LOWER(@role)
                    ORDER BY cp.created_at DESC
                    LIMIT 20;";

                var posts = new List<object>();
                using var cmd = new NpgsqlCommand(sql, connection);
                cmd.Parameters.Add("@userId", NpgsqlDbType.Uuid).Value = userId;
                cmd.Parameters.Add("@role", NpgsqlDbType.Text).Value = role;

                using var reader = await cmd.ExecuteReaderAsync();
                while (await reader.ReadAsync())
                {
                    posts.Add(new
                    {
                        id = reader.GetGuid(0),
                        content = reader.IsDBNull(1) ? "" : reader.GetString(1),
                        imageUrl = CommunitySupport.NormalizeListImage(
                            reader.IsDBNull(2) ? "" : reader.GetString(2),
                            string.Empty,
                            2_500_000),
                        createdAt = reader.IsDBNull(3) ? DateTime.UtcNow : reader.GetDateTime(3),
                        authorName = CommunitySupport.BuildDisplayName(
                            reader.IsDBNull(4) ? "" : reader.GetString(4),
                            reader.IsDBNull(5) ? "" : reader.GetString(5),
                            reader.IsDBNull(6) ? "" : reader.GetString(6),
                            reader.IsDBNull(7) ? "" : reader.GetString(7),
                            role),
                        authorProfilePicture = CommunitySupport.NormalizeListImage(
                            reader.IsDBNull(8) ? "" : reader.GetString(8),
                            string.Empty),
                        authorVerified = !reader.IsDBNull(9) && reader.GetBoolean(9)
                    });
                }

                return Ok(posts);
            }
            catch (Exception ex)
            {
                return StatusCode(500, new { message = ex.Message });
            }
        }

        [Authorize(Roles = "Artist,Sessionist")]
        [HttpPost("posts")]
        [RequestSizeLimit(20_000_000)]
        public async Task<IActionResult> CreatePost([FromBody] CreateCommunityPostDto req)
        {
            try
            {
                var normalizedRole = (req.role ?? "").Trim();
                if (req.userId == Guid.Empty || string.IsNullOrWhiteSpace(normalizedRole))
                {
                    return BadRequest(new { message = "Missing post author." });
                }

                if (!normalizedRole.Equals("Artist", StringComparison.OrdinalIgnoreCase)
                    && !normalizedRole.Equals("Sessionist", StringComparison.OrdinalIgnoreCase))
                {
                    return BadRequest(new { message = "Only artists can create public posts." });
                }

                await using var connection = new NpgsqlConnection(_connectionString);
                await connection.OpenAsync();
                await CommunitySupport.EnsureCommunitySchemaAsync(connection);
                await NotificationSupport.EnsureNotificationsTableExistsAsync(connection);
                await SecuritySupport.EnsureSecuritySchemaAsync(connection);

                if (!await SecuritySupport.UserMatchesRoleAsync(connection, req.userId, normalizedRole))
                {
                    return BadRequest(new { message = "Post author does not match the selected role." });
                }

                var sanitizedContent = SecuritySupport.SanitizePlainText(req.content, 2000, true);
                var normalizedImage = SecuritySupport.ValidateAndNormalizeImageDataUrl(req.imageUrl, 2_500_000, out var imageError);
                if (imageError is not null)
                {
                    return BadRequest(new { message = imageError });
                }
                var imageScan = await _uploadScanningService.ScanDataUrlAsync(normalizedImage, "community image");
                if (!imageScan.IsClean)
                {
                    return BadRequest(new { message = imageScan.Message });
                }

                if (string.IsNullOrWhiteSpace(sanitizedContent) && string.IsNullOrWhiteSpace(normalizedImage))
                {
                    return BadRequest(new { message = "Add text or an image before posting." });
                }

                var postId = Guid.NewGuid();

                await EnsureCommunityExtendedSchemaAsync(connection);

                var validCategories = new[] { "GigAnnouncement", "OpenCall", "StudioAvailable", "CollabWanted", "General" };
                var category = validCategories.Contains(req.category ?? "") ? req.category! : "General";

                const string sql = @"
                    INSERT INTO community_posts (id, user_id, role, content, image_url, category, event_id, created_at)
                    VALUES (@id, @userId, @role, @content, @imageUrl, @category, @eventId, NOW());";

                using var cmd = new NpgsqlCommand(sql, connection);
                cmd.Parameters.Add("@id", NpgsqlDbType.Uuid).Value = postId;
                cmd.Parameters.Add("@userId", NpgsqlDbType.Uuid).Value = req.userId;
                cmd.Parameters.Add("@role", NpgsqlDbType.Text).Value = normalizedRole;
                cmd.Parameters.Add("@content", NpgsqlDbType.Text).Value = string.IsNullOrWhiteSpace(sanitizedContent) ? DBNull.Value : sanitizedContent;
                cmd.Parameters.Add("@imageUrl", NpgsqlDbType.Text).Value = (object?)normalizedImage ?? DBNull.Value;
                cmd.Parameters.Add("@category", NpgsqlDbType.Text).Value = category;
                cmd.Parameters.Add("@eventId", NpgsqlDbType.Uuid).Value = (req.eventId.HasValue && req.eventId.Value != Guid.Empty) ? req.eventId.Value : DBNull.Value;
                await cmd.ExecuteNonQueryAsync();

                // Create poll if provided
                if (!string.IsNullOrWhiteSpace(req.pollQuestion) && req.pollOptions is { Count: >= 2 and <= 4 })
                {
                    var sanitizedQuestion = SecuritySupport.SanitizePlainText(req.pollQuestion, 200, false) ?? "";
                    var sanitizedOptions = req.pollOptions
                        .Select(o => SecuritySupport.SanitizePlainText(o, 100, false) ?? "")
                        .Where(o => !string.IsNullOrWhiteSpace(o))
                        .ToList();
                    if (sanitizedOptions.Count >= 2)
                    {
                        var optionsJson = System.Text.Json.JsonSerializer.Serialize(sanitizedOptions);
                        const string pollSql = @"
                            INSERT INTO community_post_polls (id, post_id, question, options, created_at)
                            VALUES (@id, @postId, @question, @options::jsonb, NOW());";
                        await using var pollCmd = new NpgsqlCommand(pollSql, connection);
                        pollCmd.Parameters.Add("@id", NpgsqlDbType.Uuid).Value = Guid.NewGuid();
                        pollCmd.Parameters.Add("@postId", NpgsqlDbType.Uuid).Value = postId;
                        pollCmd.Parameters.Add("@question", NpgsqlDbType.Text).Value = sanitizedQuestion;
                        pollCmd.Parameters.Add("@options", NpgsqlDbType.Text).Value = optionsJson;
                        await pollCmd.ExecuteNonQueryAsync();
                    }
                }
                await SecuritySupport.LogSecurityEventAsync(
                    connection,
                    req.userId,
                    normalizedRole,
                    "community_post_created",
                    "community_post",
                    postId,
                    HttpContext,
                    $"Community post created by {normalizedRole}.");

                var authorName = await GetAuthorDisplayNameAsync(connection, req.userId, normalizedRole);
                var followerIds = await GetFollowerIdsAsync(connection, req.userId);
                foreach (var followerId in followerIds)
                {
                    await NotificationSupport.InsertNotificationIfNotExistsAsync(
                        connection,
                        followerId,
                        "community_post",
                        "New public post",
                        $"{authorName} shared a new post on their profile.",
                        postId,
                        "community_post",
                        12);
                }

                return Ok(new { message = "Post published successfully." });
            }
            catch (Exception ex)
            {
                return StatusCode(500, new { message = ex.Message });
            }
        }

        [Authorize]
        [HttpPost("posts/{postId}/like")]
        public async Task<IActionResult> TogglePostLike(Guid postId, [FromBody] ToggleCommunityLikeDto req)
        {
            try
            {
                if (postId == Guid.Empty || req.userId == Guid.Empty)
                {
                    return BadRequest(new { message = "Missing like details." });
                }

                await using var connection = new NpgsqlConnection(_connectionString);
                await connection.OpenAsync();
                await CommunitySupport.EnsureCommunitySchemaAsync(connection);
                await EnsureCommunityPostLikesTableAsync(connection);

                const string checkSql = @"
                    SELECT EXISTS (
                        SELECT 1
                        FROM community_post_likes
                        WHERE post_id = @postId
                          AND user_id = @userId
                    );";

                bool isLiked;
                await using (var checkCmd = new NpgsqlCommand(checkSql, connection))
                {
                    checkCmd.Parameters.Add("@postId", NpgsqlDbType.Uuid).Value = postId;
                    checkCmd.Parameters.Add("@userId", NpgsqlDbType.Uuid).Value = req.userId;
                    var result = await checkCmd.ExecuteScalarAsync();
                    isLiked = result is bool liked && liked;
                }

                if (isLiked)
                {
                    const string deleteSql = @"
                        DELETE FROM community_post_likes
                        WHERE post_id = @postId
                          AND user_id = @userId;";
                    await using var deleteCmd = new NpgsqlCommand(deleteSql, connection);
                    deleteCmd.Parameters.Add("@postId", NpgsqlDbType.Uuid).Value = postId;
                    deleteCmd.Parameters.Add("@userId", NpgsqlDbType.Uuid).Value = req.userId;
                    await deleteCmd.ExecuteNonQueryAsync();
                }
                else
                {
                    const string insertSql = @"
                        INSERT INTO community_post_likes (id, post_id, user_id, created_at)
                        VALUES (@id, @postId, @userId, NOW())
                        ON CONFLICT (post_id, user_id) DO NOTHING;";
                    await using var insertCmd = new NpgsqlCommand(insertSql, connection);
                    insertCmd.Parameters.Add("@id", NpgsqlDbType.Uuid).Value = Guid.NewGuid();
                    insertCmd.Parameters.Add("@postId", NpgsqlDbType.Uuid).Value = postId;
                    insertCmd.Parameters.Add("@userId", NpgsqlDbType.Uuid).Value = req.userId;
                    await insertCmd.ExecuteNonQueryAsync();
                }

                const string countSql = @"
                    SELECT COUNT(*)
                    FROM community_post_likes
                    WHERE post_id = @postId;";
                await using var countCmd = new NpgsqlCommand(countSql, connection);
                countCmd.Parameters.Add("@postId", NpgsqlDbType.Uuid).Value = postId;
                var countResult = await countCmd.ExecuteScalarAsync();
                var likesCount = countResult is null || countResult == DBNull.Value ? 0 : Convert.ToInt32(countResult);

                return Ok(new
                {
                    isLiked = !isLiked,
                    likesCount
                });
            }
            catch (Exception ex)
            {
                return StatusCode(500, new { message = ex.Message });
            }
        }

        private static async Task<string> GetAuthorDisplayNameAsync(NpgsqlConnection connection, Guid userId, string role)
        {
            const string sql = @"
                SELECT COALESCE(firstname, ''),
                       COALESCE(lastname, ''),
                       COALESCE(stagename, ''),
                       COALESCE(productionname, '')
                FROM users
                WHERE id = @id;";

            await using var cmd = new NpgsqlCommand(sql, connection);
            cmd.Parameters.Add("@id", NpgsqlDbType.Uuid).Value = userId;
            await using var reader = await cmd.ExecuteReaderAsync();
            if (await reader.ReadAsync())
            {
                return CommunitySupport.BuildDisplayName(
                    reader.IsDBNull(0) ? "" : reader.GetString(0),
                    reader.IsDBNull(1) ? "" : reader.GetString(1),
                    reader.IsDBNull(2) ? "" : reader.GetString(2),
                    reader.IsDBNull(3) ? "" : reader.GetString(3),
                    role);
            }

            return role;
        }

        private static async Task<List<Guid>> GetFollowerIdsAsync(NpgsqlConnection connection, Guid userId)
        {
            const string ensureSql = @"
                CREATE TABLE IF NOT EXISTS customer_favorites (
                    id uuid PRIMARY KEY,
                    customer_id uuid NOT NULL,
                    target_user_id uuid NOT NULL,
                    target_role varchar(50) NOT NULL,
                    created_at timestamptz NOT NULL DEFAULT NOW(),
                    CONSTRAINT uq_customer_favorite UNIQUE (customer_id, target_user_id, target_role)
                );";

            await using (var ensureCmd = new NpgsqlCommand(ensureSql, connection))
            {
                await ensureCmd.ExecuteNonQueryAsync();
            }

            const string sql = @"
                SELECT DISTINCT customer_id
                FROM customer_favorites
                WHERE target_user_id = @userId;";

            var ids = new List<Guid>();
            await using var cmd = new NpgsqlCommand(sql, connection);
            cmd.Parameters.Add("@userId", NpgsqlDbType.Uuid).Value = userId;
            await using var reader = await cmd.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                if (!reader.IsDBNull(0))
                {
                    ids.Add(reader.GetGuid(0));
                }
            }

            return ids;
        }

        // ── GET COMMENTS ──────────────────────────────────────────────────────────
        [HttpGet("posts/{postId}/comments")]
        public async Task<IActionResult> GetComments(Guid postId, [FromQuery] Guid? userId = null)
        {
            try
            {
                await using var connection = new NpgsqlConnection(_connectionString);
                await connection.OpenAsync();
                await EnsureCommunityExtendedSchemaAsync(connection);

                const string sql = @"
                    SELECT c.id, c.user_id, c.content, c.created_at, c.parent_id,
                           COALESCE(u.firstname,''), COALESCE(u.lastname,''),
                           COALESCE(u.stagename,''), COALESCE(u.productionname,''),
                           COALESCE(u.profile_picture,''), COALESCE(u.is_verified,FALSE),
                           COALESCE(u.role,'')
                    FROM community_post_comments c
                    JOIN users u ON u.id = c.user_id
                    WHERE c.post_id = @postId
                    ORDER BY c.created_at ASC;";

                var comments = new List<object>();
                await using var cmd = new NpgsqlCommand(sql, connection);
                cmd.Parameters.Add("@postId", NpgsqlDbType.Uuid).Value = postId;
                await using var rdr = await cmd.ExecuteReaderAsync();
                while (await rdr.ReadAsync())
                {
                    var role = rdr.IsDBNull(11) ? "" : rdr.GetString(11);
                    comments.Add(new
                    {
                        id = rdr.GetGuid(0),
                        userId = rdr.GetGuid(1),
                        content = rdr.IsDBNull(2) ? "" : rdr.GetString(2),
                        createdAt = rdr.IsDBNull(3) ? DateTime.UtcNow : rdr.GetDateTime(3),
                        parentId = rdr.IsDBNull(4) ? (Guid?)null : rdr.GetGuid(4),
                        authorName = CommunitySupport.BuildDisplayName(
                            rdr.IsDBNull(5) ? "" : rdr.GetString(5),
                            rdr.IsDBNull(6) ? "" : rdr.GetString(6),
                            rdr.IsDBNull(7) ? "" : rdr.GetString(7),
                            rdr.IsDBNull(8) ? "" : rdr.GetString(8), role),
                        authorProfilePicture = CommunitySupport.NormalizeListImage(rdr.IsDBNull(9) ? "" : rdr.GetString(9), string.Empty),
                        authorVerified = !rdr.IsDBNull(10) && rdr.GetBoolean(10),
                        isOwn = userId.HasValue && rdr.GetGuid(1) == userId.Value
                    });
                }
                return Ok(comments);
            }
            catch (Exception ex) { return StatusCode(500, new { message = ex.Message }); }
        }

        // ── ADD COMMENT ───────────────────────────────────────────────────────────
        [Authorize]
        [HttpPost("posts/{postId}/comments")]
        public async Task<IActionResult> AddComment(Guid postId, [FromBody] AddCommentDto req)
        {
            try
            {
                if (req.userId == Guid.Empty || string.IsNullOrWhiteSpace(req.content))
                    return BadRequest(new { message = "Comment text is required." });

                var sanitized = SecuritySupport.SanitizePlainText(req.content, 1000, true);
                if (string.IsNullOrWhiteSpace(sanitized))
                    return BadRequest(new { message = "Comment cannot be empty." });

                await using var connection = new NpgsqlConnection(_connectionString);
                await connection.OpenAsync();
                await EnsureCommunityExtendedSchemaAsync(connection);
                await NotificationSupport.EnsureNotificationsTableExistsAsync(connection);

                var commentId = Guid.NewGuid();
                const string insertSql = @"
                    INSERT INTO community_post_comments (id, post_id, user_id, content, parent_id, created_at)
                    VALUES (@id, @postId, @userId, @content, @parentId, NOW());";
                await using var cmd = new NpgsqlCommand(insertSql, connection);
                cmd.Parameters.Add("@id", NpgsqlDbType.Uuid).Value = commentId;
                cmd.Parameters.Add("@postId", NpgsqlDbType.Uuid).Value = postId;
                cmd.Parameters.Add("@userId", NpgsqlDbType.Uuid).Value = req.userId;
                cmd.Parameters.Add("@content", NpgsqlDbType.Text).Value = sanitized;
                cmd.Parameters.Add("@parentId", NpgsqlDbType.Uuid).Value = req.parentId.HasValue ? req.parentId.Value : DBNull.Value;
                await cmd.ExecuteNonQueryAsync();

                // Notify post owner
                const string ownerSql = "SELECT user_id FROM community_posts WHERE id=@id LIMIT 1;";
                await using var ownerCmd = new NpgsqlCommand(ownerSql, connection);
                ownerCmd.Parameters.Add("@id", NpgsqlDbType.Uuid).Value = postId;
                var ownerResult = await ownerCmd.ExecuteScalarAsync();
                if (ownerResult is Guid ownerId && ownerId != req.userId)
                {
                    var commenterName = await GetAuthorDisplayNameAsync(connection, req.userId, "");
                    await NotificationSupport.InsertNotificationIfNotExistsAsync(connection, ownerId,
                        "community_comment", "New comment",
                        $"{commenterName} commented on your post.", postId, "community_post", 6);
                }

                return Ok(new { message = "Comment added.", commentId });
            }
            catch (Exception ex) { return StatusCode(500, new { message = ex.Message }); }
        }

        // ── DELETE COMMENT ────────────────────────────────────────────────────────
        [Authorize]
        [HttpDelete("posts/{postId}/comments/{commentId}")]
        public async Task<IActionResult> DeleteComment(Guid postId, Guid commentId)
        {
            try
            {
                var actorId = User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value;
                var actorRole = User.FindFirst(System.Security.Claims.ClaimTypes.Role)?.Value ?? "";
                await using var connection = new NpgsqlConnection(_connectionString);
                await connection.OpenAsync();
                await EnsureCommunityExtendedSchemaAsync(connection);

                const string sql = @"
                    DELETE FROM community_post_comments
                    WHERE id = @commentId AND post_id = @postId
                    AND (user_id = @actorId OR @isAdmin = TRUE);";
                await using var cmd = new NpgsqlCommand(sql, connection);
                cmd.Parameters.Add("@commentId", NpgsqlDbType.Uuid).Value = commentId;
                cmd.Parameters.Add("@postId", NpgsqlDbType.Uuid).Value = postId;
                cmd.Parameters.Add("@actorId", NpgsqlDbType.Uuid).Value = Guid.TryParse(actorId, out var aid) ? aid : Guid.Empty;
                cmd.Parameters.Add("@isAdmin", NpgsqlDbType.Boolean).Value = actorRole.Equals("Admin", StringComparison.OrdinalIgnoreCase);
                var rows = await cmd.ExecuteNonQueryAsync();
                if (rows == 0) return NotFound(new { message = "Comment not found or not authorized." });
                return Ok(new { message = "Comment deleted." });
            }
            catch (Exception ex) { return StatusCode(500, new { message = ex.Message }); }
        }

        // ── TRENDING ──────────────────────────────────────────────────────────────
        [HttpGet("trending")]
        public async Task<IActionResult> GetTrending()
        {
            try
            {
                await using var connection = new NpgsqlConnection(_connectionString);
                await connection.OpenAsync();
                await EnsureCommunityExtendedSchemaAsync(connection);

                // Hot posts (most likes in last 7 days)
                const string hotSql = @"
                    SELECT cp.id, COALESCE(cp.content,''), cp.created_at,
                           COALESCE(u.stagename, CONCAT(u.firstname,' ',u.lastname), 'Unknown'),
                           COALESCE(cp.role,''), COALESCE(cp.category,'General'),
                           COUNT(cpl.id) AS likes
                    FROM community_posts cp
                    LEFT JOIN community_post_likes cpl ON cpl.post_id = cp.id AND cpl.created_at >= NOW() - INTERVAL '7 days'
                    LEFT JOIN users u ON u.id = cp.user_id
                    GROUP BY cp.id, cp.content, cp.created_at, u.stagename, u.firstname, u.lastname, cp.role, cp.category
                    ORDER BY likes DESC, cp.created_at DESC
                    LIMIT 5;";

                var hotPosts = new List<object>();
                await using (var hotCmd = new NpgsqlCommand(hotSql, connection))
                {
                    await using var rdr = await hotCmd.ExecuteReaderAsync();
                    while (await rdr.ReadAsync())
                    {
                        var content = rdr.IsDBNull(1) ? "" : rdr.GetString(1);
                        hotPosts.Add(new
                        {
                            id = rdr.GetGuid(0),
                            preview = content.Length > 80 ? content[..80] + "…" : content,
                            createdAt = rdr.IsDBNull(2) ? DateTime.UtcNow : rdr.GetDateTime(2),
                            authorName = rdr.IsDBNull(3) ? "Unknown" : rdr.GetString(3).Trim(),
                            role = rdr.IsDBNull(4) ? "" : rdr.GetString(4),
                            category = rdr.IsDBNull(5) ? "General" : rdr.GetString(5),
                            likes = Convert.ToInt32(rdr.GetInt64(6))
                        });
                    }
                }

                // Most active this week
                const string activeSql = @"
                    SELECT u.id, COALESCE(u.stagename, CONCAT(u.firstname,' ',u.lastname),'Unknown') AS name,
                           COALESCE(u.role,''), COALESCE(u.profile_picture,''), COALESCE(u.is_verified,FALSE),
                           COUNT(cp.id) AS posts
                    FROM community_posts cp
                    JOIN users u ON u.id = cp.user_id
                    WHERE cp.created_at >= NOW() - INTERVAL '7 days'
                    GROUP BY u.id, u.stagename, u.firstname, u.lastname, u.role, u.profile_picture, u.is_verified
                    ORDER BY posts DESC
                    LIMIT 5;";

                var activeUsers = new List<object>();
                await using (var actCmd = new NpgsqlCommand(activeSql, connection))
                {
                    await using var rdr = await actCmd.ExecuteReaderAsync();
                    while (await rdr.ReadAsync())
                    {
                        activeUsers.Add(new
                        {
                            id = rdr.GetGuid(0),
                            name = rdr.IsDBNull(1) ? "Unknown" : rdr.GetString(1).Trim(),
                            role = rdr.IsDBNull(2) ? "" : rdr.GetString(2),
                            profilePicture = CommunitySupport.NormalizeListImage(rdr.IsDBNull(3) ? "" : rdr.GetString(3), string.Empty),
                            isVerified = !rdr.IsDBNull(4) && rdr.GetBoolean(4),
                            postCount = Convert.ToInt32(rdr.GetInt64(5))
                        });
                    }
                }

                // Category breakdown
                const string catSql = @"
                    SELECT COALESCE(category,'General'), COUNT(*) FROM community_posts
                    WHERE created_at >= NOW() - INTERVAL '30 days'
                    GROUP BY category ORDER BY COUNT(*) DESC;";
                var categories = new List<object>();
                await using (var catCmd = new NpgsqlCommand(catSql, connection))
                {
                    await using var rdr = await catCmd.ExecuteReaderAsync();
                    while (await rdr.ReadAsync())
                        categories.Add(new { category = rdr.GetString(0), count = Convert.ToInt32(rdr.GetInt64(1)) });
                }

                return Ok(new { hotPosts, activeUsers, categories });
            }
            catch (Exception ex) { return StatusCode(500, new { message = ex.Message }); }
        }

        // ── GET POLL DATA ──────────────────────────────────────────────────────────
        [HttpGet("posts/{postId}/poll")]
        public async Task<IActionResult> GetPoll(Guid postId, [FromQuery] Guid? userId = null)
        {
            try
            {
                await using var connection = new NpgsqlConnection(_connectionString);
                await connection.OpenAsync();
                await EnsureCommunityExtendedSchemaAsync(connection);

                const string sql = @"
                    SELECT p.id, p.question, p.options::text,
                           COALESCE((SELECT option_index FROM community_poll_votes WHERE poll_id=p.id AND user_id=@userId LIMIT 1), -1)
                    FROM community_post_polls p WHERE p.post_id=@postId LIMIT 1;";
                await using var cmd = new NpgsqlCommand(sql, connection);
                cmd.Parameters.Add("@postId", NpgsqlDbType.Uuid).Value = postId;
                cmd.Parameters.Add("@userId", NpgsqlDbType.Uuid).Value = (object?)userId ?? DBNull.Value;
                await using var rdr = await cmd.ExecuteReaderAsync();
                if (!await rdr.ReadAsync()) return Ok(new { hasPoll = false });

                var pollId = rdr.GetGuid(0);
                var question = rdr.GetString(1);
                var optionsJson = rdr.IsDBNull(2) ? "[]" : rdr.GetString(2);
                var myVote = rdr.IsDBNull(3) ? -1 : rdr.GetInt32(3);
                await rdr.CloseAsync();

                var options = System.Text.Json.JsonSerializer.Deserialize<List<string>>(optionsJson) ?? new List<string>();

                // Vote counts per option
                const string voteSql = @"
                    SELECT option_index, COUNT(*) FROM community_poll_votes WHERE poll_id=@id GROUP BY option_index;";
                await using var voteCmd = new NpgsqlCommand(voteSql, connection);
                voteCmd.Parameters.Add("@id", NpgsqlDbType.Uuid).Value = pollId;
                var voteCounts = new int[options.Count];
                await using var vRdr = await voteCmd.ExecuteReaderAsync();
                while (await vRdr.ReadAsync())
                {
                    var idx = vRdr.GetInt32(0);
                    if (idx >= 0 && idx < voteCounts.Length)
                        voteCounts[idx] = Convert.ToInt32(vRdr.GetInt64(1));
                }

                return Ok(new { hasPoll = true, pollId, question, options, voteCounts, myVote, totalVotes = voteCounts.Sum() });
            }
            catch (Exception ex) { return StatusCode(500, new { message = ex.Message }); }
        }

        // ── VOTE ON POLL ──────────────────────────────────────────────────────────
        [Authorize]
        [HttpPost("posts/{postId}/poll/vote/{optionIndex}")]
        public async Task<IActionResult> VoteOnPoll(Guid postId, int optionIndex, [FromBody] PollVoteDto req)
        {
            try
            {
                if (req.userId == Guid.Empty) return Unauthorized();
                await using var connection = new NpgsqlConnection(_connectionString);
                await connection.OpenAsync();
                await EnsureCommunityExtendedSchemaAsync(connection);

                const string pollIdSql = "SELECT id FROM community_post_polls WHERE post_id=@postId LIMIT 1;";
                await using var pidCmd = new NpgsqlCommand(pollIdSql, connection);
                pidCmd.Parameters.Add("@postId", NpgsqlDbType.Uuid).Value = postId;
                var pollId = await pidCmd.ExecuteScalarAsync() as Guid?;
                if (!pollId.HasValue) return NotFound(new { message = "Poll not found." });

                const string upsertSql = @"
                    INSERT INTO community_poll_votes (id, poll_id, user_id, option_index, created_at)
                    VALUES (@id, @pollId, @userId, @optIdx, NOW())
                    ON CONFLICT (poll_id, user_id) DO UPDATE SET option_index = @optIdx;";
                await using var vCmd = new NpgsqlCommand(upsertSql, connection);
                vCmd.Parameters.Add("@id", NpgsqlDbType.Uuid).Value = Guid.NewGuid();
                vCmd.Parameters.Add("@pollId", NpgsqlDbType.Uuid).Value = pollId.Value;
                vCmd.Parameters.Add("@userId", NpgsqlDbType.Uuid).Value = req.userId;
                vCmd.Parameters.Add("@optIdx", NpgsqlDbType.Integer).Value = optionIndex;
                await vCmd.ExecuteNonQueryAsync();

                return await GetPoll(postId, req.userId);
            }
            catch (Exception ex) { return StatusCode(500, new { message = ex.Message }); }
        }

        // ── FOLLOW / UNFOLLOW ─────────────────────────────────────────────────────
        [Authorize]
        [HttpPost("follow")]
        public async Task<IActionResult> ToggleFollow([FromBody] FollowDto req)
        {
            try
            {
                if (req.followerId == Guid.Empty || req.targetUserId == Guid.Empty || req.followerId == req.targetUserId)
                    return BadRequest(new { message = "Invalid follow request." });

                await using var connection = new NpgsqlConnection(_connectionString);
                await connection.OpenAsync();
                await EnsureCommunityExtendedSchemaAsync(connection);
                await NotificationSupport.EnsureNotificationsTableExistsAsync(connection);

                const string checkSql = "SELECT id FROM community_follows WHERE follower_id=@f AND followed_id=@t LIMIT 1;";
                await using var checkCmd = new NpgsqlCommand(checkSql, connection);
                checkCmd.Parameters.Add("@f", NpgsqlDbType.Uuid).Value = req.followerId;
                checkCmd.Parameters.Add("@t", NpgsqlDbType.Uuid).Value = req.targetUserId;
                var existing = await checkCmd.ExecuteScalarAsync();

                if (existing != null)
                {
                    const string del = "DELETE FROM community_follows WHERE follower_id=@f AND followed_id=@t;";
                    await using var delCmd = new NpgsqlCommand(del, connection);
                    delCmd.Parameters.Add("@f", NpgsqlDbType.Uuid).Value = req.followerId;
                    delCmd.Parameters.Add("@t", NpgsqlDbType.Uuid).Value = req.targetUserId;
                    await delCmd.ExecuteNonQueryAsync();
                    return Ok(new { isFollowing = false });
                }

                const string ins = @"
                    INSERT INTO community_follows (id, follower_id, followed_id, created_at)
                    VALUES (@id, @f, @t, NOW()) ON CONFLICT DO NOTHING;";
                await using var insCmd = new NpgsqlCommand(ins, connection);
                insCmd.Parameters.Add("@id", NpgsqlDbType.Uuid).Value = Guid.NewGuid();
                insCmd.Parameters.Add("@f", NpgsqlDbType.Uuid).Value = req.followerId;
                insCmd.Parameters.Add("@t", NpgsqlDbType.Uuid).Value = req.targetUserId;
                await insCmd.ExecuteNonQueryAsync();

                var followerName = await GetAuthorDisplayNameAsync(connection, req.followerId, "");
                await NotificationSupport.InsertNotificationIfNotExistsAsync(connection, req.targetUserId,
                    "community_follow", "New follower",
                    $"{followerName} started following your posts.", req.followerId, "user", 24);

                return Ok(new { isFollowing = true });
            }
            catch (Exception ex) { return StatusCode(500, new { message = ex.Message }); }
        }

        // ── FOLLOWER COUNT ────────────────────────────────────────────────────────
        [HttpGet("follows/{userId}")]
        public async Task<IActionResult> GetFollowStats(Guid userId, [FromQuery] Guid? viewerId = null)
        {
            try
            {
                await using var connection = new NpgsqlConnection(_connectionString);
                await connection.OpenAsync();
                await EnsureCommunityExtendedSchemaAsync(connection);

                const string sql = @"
                    SELECT (SELECT COUNT(*) FROM community_follows WHERE followed_id=@id) AS followers,
                           (SELECT COUNT(*) FROM community_follows WHERE follower_id=@id) AS following,
                           EXISTS (SELECT 1 FROM community_follows WHERE follower_id=@viewerId AND followed_id=@id);";
                await using var cmd = new NpgsqlCommand(sql, connection);
                cmd.Parameters.Add("@id", NpgsqlDbType.Uuid).Value = userId;
                cmd.Parameters.Add("@viewerId", NpgsqlDbType.Uuid).Value = (object?)viewerId ?? DBNull.Value;
                await using var rdr = await cmd.ExecuteReaderAsync();
                if (!await rdr.ReadAsync()) return Ok(new { followers = 0, following = 0, isFollowing = false });
                return Ok(new
                {
                    followers = Convert.ToInt32(rdr.GetInt64(0)),
                    following = Convert.ToInt32(rdr.GetInt64(1)),
                    isFollowing = !rdr.IsDBNull(2) && rdr.GetBoolean(2)
                });
            }
            catch (Exception ex) { return StatusCode(500, new { message = ex.Message }); }
        }

        private static async Task EnsureCommunityPostLikesTableAsync(NpgsqlConnection connection)
        {
            const string sql = @"
                CREATE TABLE IF NOT EXISTS community_post_likes (
                    id uuid PRIMARY KEY,
                    post_id uuid NOT NULL,
                    user_id uuid NOT NULL,
                    created_at timestamptz NOT NULL DEFAULT NOW(),
                    CONSTRAINT uq_community_post_like UNIQUE (post_id, user_id)
                );";

            await using var cmd = new NpgsqlCommand(sql, connection);
            await cmd.ExecuteNonQueryAsync();
        }

        private async Task EnsureCommunityExtendedSchemaAsync(NpgsqlConnection connection)
        {
            if (_extendedSchemaReady) return;      // skip DDL on every request after first run
            await _schemaSem.WaitAsync();
            try
            {
                if (_extendedSchemaReady) return;  // double-check inside lock
                await EnsureCommunityPostLikesTableAsync(connection);
            const string sql = @"
                ALTER TABLE community_posts ADD COLUMN IF NOT EXISTS category varchar(40) NOT NULL DEFAULT 'General';
                ALTER TABLE community_posts ADD COLUMN IF NOT EXISTS event_id uuid NULL;

                CREATE TABLE IF NOT EXISTS community_post_comments (
                    id uuid PRIMARY KEY,
                    post_id uuid NOT NULL,
                    user_id uuid NOT NULL,
                    content text NOT NULL,
                    parent_id uuid NULL,
                    created_at timestamptz NOT NULL DEFAULT NOW(),
                    updated_at timestamptz NOT NULL DEFAULT NOW()
                );
                CREATE INDEX IF NOT EXISTS idx_community_comments_post ON community_post_comments(post_id);

                CREATE TABLE IF NOT EXISTS community_post_polls (
                    id uuid PRIMARY KEY,
                    post_id uuid NOT NULL UNIQUE,
                    question text NOT NULL,
                    options jsonb NOT NULL DEFAULT '[]',
                    created_at timestamptz NOT NULL DEFAULT NOW()
                );

                CREATE TABLE IF NOT EXISTS community_poll_votes (
                    id uuid PRIMARY KEY,
                    poll_id uuid NOT NULL,
                    user_id uuid NOT NULL,
                    option_index int NOT NULL,
                    created_at timestamptz NOT NULL DEFAULT NOW(),
                    UNIQUE (poll_id, user_id)
                );

                CREATE TABLE IF NOT EXISTS community_follows (
                    id uuid PRIMARY KEY,
                    follower_id uuid NOT NULL,
                    followed_id uuid NOT NULL,
                    created_at timestamptz NOT NULL DEFAULT NOW(),
                    UNIQUE (follower_id, followed_id)
                );
                CREATE INDEX IF NOT EXISTS idx_community_follows_followed ON community_follows(followed_id);";
                await using var cmd = new NpgsqlCommand(sql, connection);
                await cmd.ExecuteNonQueryAsync();
                _extendedSchemaReady = true;
            }
            finally
            {
                _schemaSem.Release();
            }
        }
    }
}
