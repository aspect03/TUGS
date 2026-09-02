using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Npgsql;
using NpgsqlTypes;

namespace ImajinationAPI.Controllers
{
    public class CreateRecruitmentPostRequest
    {
        public Guid userId { get; set; }
        public string? role { get; set; }
        public string? title { get; set; }
        public string? description { get; set; }
        public string? roleNeeded { get; set; }
        public string? genres { get; set; }
        public string? location { get; set; }
        public DateTime? eventDate { get; set; }
        public decimal? budgetMin { get; set; }
        public decimal? budgetMax { get; set; }
    }

    public class RecruitmentApplicationRequest
    {
        public Guid applicantId { get; set; }
        public string? applicantRole { get; set; }
        public string? message { get; set; }
        public decimal? proposedRate { get; set; }
    }

    public class UpdateRecruitmentApplicationStatusRequest
    {
        public Guid ownerId { get; set; }
        public string? status { get; set; }
        public string? note { get; set; }
    }

    [Route("api/[controller]")]
    [ApiController]
    public class RecruitmentController : ControllerBase
    {
        private readonly string _connectionString;

        public RecruitmentController(IConfiguration configuration)
        {
            _connectionString = ConfigurationFallbacks.GetRequiredSupabaseConnectionString(configuration);
        }

        private Guid? GetActorUserId()
        {
            var raw = User.FindFirstValue(ClaimTypes.NameIdentifier)
                ?? User.FindFirstValue("sub")
                ?? User.FindFirstValue("userId");
            return Guid.TryParse(raw, out var id) ? id : null;
        }

        private static string Clean(string? value, int max = 500)
        {
            var text = (value ?? "").Trim();
            if (text.Length > max) text = text[..max];
            return text;
        }

        private static string NormalizeRoleNeeded(string? value)
        {
            var role = Clean(value, 40);
            return role.Equals("Sessionist", StringComparison.OrdinalIgnoreCase) ? "Sessionist" : "Artist";
        }

        private static string NormalizeApplicantRole(string? value)
        {
            var role = Clean(value, 40);
            return role.Equals("Sessionist", StringComparison.OrdinalIgnoreCase) ? "Sessionist" : "Artist";
        }

        private static string NormalizeStatus(string? value)
        {
            var status = Clean(value, 40);
            return status.Equals("Accepted", StringComparison.OrdinalIgnoreCase) ? "Accepted"
                : status.Equals("Rejected", StringComparison.OrdinalIgnoreCase) ? "Rejected"
                : "Pending";
        }

        private static async Task EnsureRecruitmentSchemaAsync(NpgsqlConnection connection)
        {
            const string sql = @"
                CREATE TABLE IF NOT EXISTS recruitment_posts (
                    id uuid PRIMARY KEY,
                    owner_id uuid NOT NULL,
                    owner_role varchar(40) NOT NULL DEFAULT '',
                    title text NOT NULL,
                    description text NOT NULL,
                    role_needed varchar(40) NOT NULL DEFAULT 'Artist',
                    genres text NOT NULL DEFAULT '',
                    location text NOT NULL DEFAULT '',
                    event_date timestamptz NULL,
                    budget_min numeric(12,2) NULL,
                    budget_max numeric(12,2) NULL,
                    status varchar(30) NOT NULL DEFAULT 'Open',
                    created_at timestamptz NOT NULL DEFAULT NOW(),
                    updated_at timestamptz NOT NULL DEFAULT NOW()
                );

                CREATE TABLE IF NOT EXISTS recruitment_applications (
                    id uuid PRIMARY KEY,
                    post_id uuid NOT NULL REFERENCES recruitment_posts(id) ON DELETE CASCADE,
                    applicant_id uuid NOT NULL,
                    applicant_role varchar(40) NOT NULL DEFAULT 'Artist',
                    message text NOT NULL DEFAULT '',
                    proposed_rate numeric(12,2) NULL,
                    status varchar(30) NOT NULL DEFAULT 'Pending',
                    owner_note text NOT NULL DEFAULT '',
                    created_at timestamptz NOT NULL DEFAULT NOW(),
                    updated_at timestamptz NOT NULL DEFAULT NOW(),
                    UNIQUE(post_id, applicant_id)
                );

                CREATE INDEX IF NOT EXISTS idx_recruitment_posts_status_created ON recruitment_posts(status, created_at DESC);
                CREATE INDEX IF NOT EXISTS idx_recruitment_posts_owner ON recruitment_posts(owner_id, created_at DESC);
                CREATE INDEX IF NOT EXISTS idx_recruitment_applications_post ON recruitment_applications(post_id, created_at DESC);
                CREATE INDEX IF NOT EXISTS idx_recruitment_applications_applicant ON recruitment_applications(applicant_id, created_at DESC);";

            await using var cmd = new NpgsqlCommand(sql, connection);
            await cmd.ExecuteNonQueryAsync();
        }

        [HttpGet("posts")]
        public async Task<IActionResult> GetPosts([FromQuery] Guid? userId = null, [FromQuery] string? query = null, [FromQuery] string? roleNeeded = null)
        {
            try
            {
                await using var connection = new NpgsqlConnection(_connectionString);
                await connection.OpenAsync();
                await EnsureRecruitmentSchemaAsync(connection);

                var posts = new List<object>();
                const string sql = @"
                    SELECT rp.id, rp.owner_id, rp.owner_role, rp.title, rp.description, rp.role_needed, rp.genres, rp.location,
                           rp.event_date, rp.budget_min, rp.budget_max, rp.status, rp.created_at,
                           COALESCE(u.firstname, ''), COALESCE(u.lastname, ''), COALESCE(u.stagename, ''), COALESCE(u.productionname, ''),
                           COALESCE(u.profile_picture, ''), COALESCE(u.is_verified, FALSE),
                           COALESCE((SELECT COUNT(*) FROM recruitment_applications ra WHERE ra.post_id = rp.id), 0),
                           EXISTS(SELECT 1 FROM recruitment_applications ra WHERE ra.post_id = rp.id AND ra.applicant_id = @userId),
                           COALESCE((SELECT ra.status FROM recruitment_applications ra WHERE ra.post_id = rp.id AND ra.applicant_id = @userId LIMIT 1), '')
                    FROM recruitment_posts rp
                    JOIN users u ON u.id = rp.owner_id
                    WHERE COALESCE(rp.status, 'Open') = 'Open'
                      AND (@roleNeeded = '' OR LOWER(rp.role_needed) = LOWER(@roleNeeded))
                      AND (
                        @query = ''
                        OR LOWER(rp.title) LIKE LOWER(@likeQuery)
                        OR LOWER(rp.description) LIKE LOWER(@likeQuery)
                        OR LOWER(rp.genres) LIKE LOWER(@likeQuery)
                        OR LOWER(rp.location) LIKE LOWER(@likeQuery)
                      )
                    ORDER BY rp.created_at DESC
                    LIMIT 80;";

                await using var cmd = new NpgsqlCommand(sql, connection);
                cmd.Parameters.Add("@userId", NpgsqlDbType.Uuid).Value = (object?)userId ?? DBNull.Value;
                cmd.Parameters.Add("@query", NpgsqlDbType.Text).Value = Clean(query, 120);
                cmd.Parameters.Add("@likeQuery", NpgsqlDbType.Text).Value = $"%{Clean(query, 120)}%";
                cmd.Parameters.Add("@roleNeeded", NpgsqlDbType.Text).Value = Clean(roleNeeded, 40);

                await using var reader = await cmd.ExecuteReaderAsync();
                while (await reader.ReadAsync())
                {
                    var ownerRole = reader.GetString(2);
                    posts.Add(new
                    {
                        id = reader.GetGuid(0),
                        ownerId = reader.GetGuid(1),
                        ownerRole,
                        title = reader.GetString(3),
                        description = reader.GetString(4),
                        roleNeeded = reader.GetString(5),
                        genres = reader.GetString(6),
                        location = reader.GetString(7),
                        eventDate = reader.IsDBNull(8) ? (DateTime?)null : reader.GetDateTime(8),
                        budgetMin = reader.IsDBNull(9) ? (decimal?)null : reader.GetDecimal(9),
                        budgetMax = reader.IsDBNull(10) ? (decimal?)null : reader.GetDecimal(10),
                        status = reader.GetString(11),
                        createdAt = reader.GetDateTime(12),
                        ownerName = CommunitySupport.BuildDisplayName(reader.GetString(13), reader.GetString(14), reader.GetString(15), reader.GetString(16), ownerRole),
                        ownerProfilePicture = reader.GetString(17),
                        ownerVerified = reader.GetBoolean(18),
                        applicationCount = Convert.ToInt32(reader.GetInt64(19)),
                        hasApplied = reader.GetBoolean(20),
                        myApplicationStatus = reader.GetString(21)
                    });
                }

                return Ok(posts);
            }
            catch (Exception ex)
            {
                return StatusCode(500, new { message = "Failed to load recruitment posts: " + ex.Message });
            }
        }

        [Authorize]
        [HttpPost("posts")]
        public async Task<IActionResult> CreatePost([FromBody] CreateRecruitmentPostRequest req)
        {
            var actorId = GetActorUserId();
            if (actorId.HasValue && actorId.Value != req.userId) return Forbid();

            var title = Clean(req.title, 160);
            var description = Clean(req.description, 2000);
            if (req.userId == Guid.Empty || title.Length < 5 || description.Length < 15)
            {
                return BadRequest(new { message = "Add a title and a clear recruitment description." });
            }

            try
            {
                await using var connection = new NpgsqlConnection(_connectionString);
                await connection.OpenAsync();
                await EnsureRecruitmentSchemaAsync(connection);
                await NotificationSupport.EnsureNotificationsTableExistsAsync(connection);

                var postId = Guid.NewGuid();
                const string sql = @"
                    INSERT INTO recruitment_posts
                        (id, owner_id, owner_role, title, description, role_needed, genres, location, event_date, budget_min, budget_max, status, created_at, updated_at)
                    VALUES
                        (@id, @ownerId, @ownerRole, @title, @description, @roleNeeded, @genres, @location, @eventDate, @budgetMin, @budgetMax, 'Open', NOW(), NOW());";
                await using var cmd = new NpgsqlCommand(sql, connection);
                cmd.Parameters.Add("@id", NpgsqlDbType.Uuid).Value = postId;
                cmd.Parameters.Add("@ownerId", NpgsqlDbType.Uuid).Value = req.userId;
                cmd.Parameters.Add("@ownerRole", NpgsqlDbType.Text).Value = Clean(req.role, 40);
                cmd.Parameters.Add("@title", NpgsqlDbType.Text).Value = title;
                cmd.Parameters.Add("@description", NpgsqlDbType.Text).Value = description;
                cmd.Parameters.Add("@roleNeeded", NpgsqlDbType.Text).Value = NormalizeRoleNeeded(req.roleNeeded);
                cmd.Parameters.Add("@genres", NpgsqlDbType.Text).Value = Clean(req.genres, 300);
                cmd.Parameters.Add("@location", NpgsqlDbType.Text).Value = Clean(req.location, 300);
                cmd.Parameters.Add("@eventDate", NpgsqlDbType.TimestampTz).Value = (object?)req.eventDate ?? DBNull.Value;
                cmd.Parameters.Add("@budgetMin", NpgsqlDbType.Numeric).Value = (object?)req.budgetMin ?? DBNull.Value;
                cmd.Parameters.Add("@budgetMax", NpgsqlDbType.Numeric).Value = (object?)req.budgetMax ?? DBNull.Value;
                await cmd.ExecuteNonQueryAsync();

                return Ok(new { message = "Recruitment post published.", postId });
            }
            catch (Exception ex)
            {
                return StatusCode(500, new { message = "Failed to create recruitment post: " + ex.Message });
            }
        }

        [Authorize]
        [HttpPost("posts/{postId}/apply")]
        public async Task<IActionResult> Apply(Guid postId, [FromBody] RecruitmentApplicationRequest req)
        {
            var actorId = GetActorUserId();
            if (actorId.HasValue && actorId.Value != req.applicantId) return Forbid();
            if (req.applicantId == Guid.Empty) return BadRequest(new { message = "Missing applicant." });

            var message = Clean(req.message, 1500);
            if (message.Length < 10) return BadRequest(new { message = "Add a short application message." });

            try
            {
                await using var connection = new NpgsqlConnection(_connectionString);
                await connection.OpenAsync();
                await EnsureRecruitmentSchemaAsync(connection);
                await NotificationSupport.EnsureNotificationsTableExistsAsync(connection);

                Guid ownerId;
                string title;
                await using (var postCmd = new NpgsqlCommand("SELECT owner_id, title FROM recruitment_posts WHERE id = @postId AND status = 'Open';", connection))
                {
                    postCmd.Parameters.Add("@postId", NpgsqlDbType.Uuid).Value = postId;
                    await using var reader = await postCmd.ExecuteReaderAsync();
                    if (!await reader.ReadAsync()) return NotFound(new { message = "Recruitment post is no longer open." });
                    ownerId = reader.GetGuid(0);
                    title = reader.GetString(1);
                }

                if (ownerId == req.applicantId) return BadRequest(new { message = "You cannot apply to your own recruitment post." });

                const string sql = @"
                    INSERT INTO recruitment_applications
                        (id, post_id, applicant_id, applicant_role, message, proposed_rate, status, owner_note, created_at, updated_at)
                    VALUES
                        (@id, @postId, @applicantId, @applicantRole, @message, @proposedRate, 'Pending', '', NOW(), NOW())
                    ON CONFLICT (post_id, applicant_id) DO UPDATE
                    SET message = EXCLUDED.message,
                        proposed_rate = EXCLUDED.proposed_rate,
                        status = 'Pending',
                        updated_at = NOW();";
                var applicationId = Guid.NewGuid();
                await using var cmd = new NpgsqlCommand(sql, connection);
                cmd.Parameters.Add("@id", NpgsqlDbType.Uuid).Value = applicationId;
                cmd.Parameters.Add("@postId", NpgsqlDbType.Uuid).Value = postId;
                cmd.Parameters.Add("@applicantId", NpgsqlDbType.Uuid).Value = req.applicantId;
                cmd.Parameters.Add("@applicantRole", NpgsqlDbType.Text).Value = NormalizeApplicantRole(req.applicantRole);
                cmd.Parameters.Add("@message", NpgsqlDbType.Text).Value = message;
                cmd.Parameters.Add("@proposedRate", NpgsqlDbType.Numeric).Value = (object?)req.proposedRate ?? DBNull.Value;
                await cmd.ExecuteNonQueryAsync();

                await NotificationSupport.InsertNotificationAsync(
                    connection,
                    ownerId,
                    "recruitment_application",
                    "New recruitment application",
                    $"Someone applied to '{title}'. Review the applicant from the Recruitment page.",
                    postId,
                    "recruitment");

                return Ok(new { message = "Application sent." });
            }
            catch (Exception ex)
            {
                return StatusCode(500, new { message = "Failed to send application: " + ex.Message });
            }
        }

        [Authorize]
        [HttpGet("mine/{userId}")]
        public async Task<IActionResult> GetMine(Guid userId)
        {
            var actorId = GetActorUserId();
            if (actorId.HasValue && actorId.Value != userId) return Forbid();

            try
            {
                await using var connection = new NpgsqlConnection(_connectionString);
                await connection.OpenAsync();
                await EnsureRecruitmentSchemaAsync(connection);

                var posts = new List<object>();
                const string postSql = @"
                    SELECT rp.id, rp.title, rp.role_needed, rp.status, rp.created_at,
                           COALESCE((SELECT COUNT(*) FROM recruitment_applications ra WHERE ra.post_id = rp.id), 0)
                    FROM recruitment_posts rp
                    WHERE rp.owner_id = @userId
                    ORDER BY rp.created_at DESC;";
                await using (var postCmd = new NpgsqlCommand(postSql, connection))
                {
                    postCmd.Parameters.Add("@userId", NpgsqlDbType.Uuid).Value = userId;
                    await using var reader = await postCmd.ExecuteReaderAsync();
                    while (await reader.ReadAsync())
                    {
                        posts.Add(new
                        {
                            id = reader.GetGuid(0),
                            title = reader.GetString(1),
                            roleNeeded = reader.GetString(2),
                            status = reader.GetString(3),
                            createdAt = reader.GetDateTime(4),
                            applicationCount = Convert.ToInt32(reader.GetInt64(5))
                        });
                    }
                }

                var applications = new List<object>();
                const string appSql = @"
                    SELECT ra.id, ra.post_id, rp.title, ra.applicant_id, ra.applicant_role, ra.message,
                           ra.proposed_rate, ra.status, ra.owner_note, ra.created_at,
                           COALESCE(u.firstname, ''), COALESCE(u.lastname, ''), COALESCE(u.stagename, ''), COALESCE(u.productionname, ''),
                           COALESCE(u.profile_picture, '')
                    FROM recruitment_applications ra
                    JOIN recruitment_posts rp ON rp.id = ra.post_id
                    JOIN users u ON u.id = ra.applicant_id
                    WHERE rp.owner_id = @userId OR ra.applicant_id = @userId
                    ORDER BY ra.created_at DESC;";
                await using (var appCmd = new NpgsqlCommand(appSql, connection))
                {
                    appCmd.Parameters.Add("@userId", NpgsqlDbType.Uuid).Value = userId;
                    await using var reader = await appCmd.ExecuteReaderAsync();
                    while (await reader.ReadAsync())
                    {
                        var applicantRole = reader.GetString(4);
                        applications.Add(new
                        {
                            id = reader.GetGuid(0),
                            postId = reader.GetGuid(1),
                            postTitle = reader.GetString(2),
                            applicantId = reader.GetGuid(3),
                            applicantRole,
                            message = reader.GetString(5),
                            proposedRate = reader.IsDBNull(6) ? (decimal?)null : reader.GetDecimal(6),
                            status = reader.GetString(7),
                            ownerNote = reader.GetString(8),
                            createdAt = reader.GetDateTime(9),
                            applicantName = CommunitySupport.BuildDisplayName(reader.GetString(10), reader.GetString(11), reader.GetString(12), reader.GetString(13), applicantRole),
                            applicantProfilePicture = reader.GetString(14),
                            isOwnerView = true
                        });
                    }
                }

                return Ok(new { posts, applications });
            }
            catch (Exception ex)
            {
                return StatusCode(500, new { message = "Failed to load recruitment activity: " + ex.Message });
            }
        }

        [Authorize]
        [HttpPatch("applications/{applicationId}/status")]
        public async Task<IActionResult> UpdateApplicationStatus(Guid applicationId, [FromBody] UpdateRecruitmentApplicationStatusRequest req)
        {
            var actorId = GetActorUserId();
            if (actorId.HasValue && actorId.Value != req.ownerId) return Forbid();

            var status = NormalizeStatus(req.status);
            if (status == "Pending") return BadRequest(new { message = "Choose Accepted or Rejected." });

            try
            {
                await using var connection = new NpgsqlConnection(_connectionString);
                await connection.OpenAsync();
                await EnsureRecruitmentSchemaAsync(connection);
                await NotificationSupport.EnsureNotificationsTableExistsAsync(connection);

                Guid applicantId;
                Guid postId;
                string title;
                const string updateSql = @"
                    UPDATE recruitment_applications ra
                    SET status = @status,
                        owner_note = @note,
                        updated_at = NOW()
                    FROM recruitment_posts rp
                    WHERE ra.post_id = rp.id
                      AND ra.id = @applicationId
                      AND rp.owner_id = @ownerId
                    RETURNING ra.applicant_id, ra.post_id, rp.title;";
                await using var cmd = new NpgsqlCommand(updateSql, connection);
                cmd.Parameters.Add("@applicationId", NpgsqlDbType.Uuid).Value = applicationId;
                cmd.Parameters.Add("@ownerId", NpgsqlDbType.Uuid).Value = req.ownerId;
                cmd.Parameters.Add("@status", NpgsqlDbType.Text).Value = status;
                cmd.Parameters.Add("@note", NpgsqlDbType.Text).Value = Clean(req.note, 1000);
                await using var reader = await cmd.ExecuteReaderAsync();
                if (!await reader.ReadAsync()) return NotFound(new { message = "Application not found for this recruitment post." });
                applicantId = reader.GetGuid(0);
                postId = reader.GetGuid(1);
                title = reader.GetString(2);
                await reader.CloseAsync();

                await NotificationSupport.InsertNotificationAsync(
                    connection,
                    applicantId,
                    status == "Accepted" ? "recruitment_accepted" : "recruitment_rejected",
                    status == "Accepted" ? "Application accepted" : "Application update",
                    status == "Accepted"
                        ? $"Your application for '{title}' was accepted. Start negotiation from booking messages."
                        : $"Your application for '{title}' was reviewed.",
                    postId,
                    "recruitment");

                return Ok(new { message = $"Application {status.ToLowerInvariant()}.", status });
            }
            catch (Exception ex)
            {
                return StatusCode(500, new { message = "Failed to update application: " + ex.Message });
            }
        }
    }
}
