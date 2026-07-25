using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Authorization;
using Npgsql;
using NpgsqlTypes;
using ImajinationAPI.Services;
using System.Threading;
using System.Security.Claims;
using Microsoft.Extensions.Caching.Memory;

namespace ImajinationAPI.Controllers
{
    [Route("api/[controller]")]
    [ApiController]
    public class SessionistController : ControllerBase
    {
        private readonly string _connectionString;
        private readonly UploadScanningService _uploadScanningService;
        private readonly AutomatedVerificationAssessmentService _automatedVerificationAssessmentService;
        private readonly IMemoryCache _cache;
        private static readonly SemaphoreSlim SessionistSchemaLock = new(1, 1);
        private static volatile bool _sessionistSchemaEnsured;

        public SessionistController(
            IConfiguration configuration,
            UploadScanningService uploadScanningService,
            AutomatedVerificationAssessmentService automatedVerificationAssessmentService,
            IMemoryCache cache)
        {
            _connectionString = ConfigurationFallbacks.GetRequiredSupabaseConnectionString(configuration);
            _uploadScanningService = uploadScanningService;
            _automatedVerificationAssessmentService = automatedVerificationAssessmentService;
            _cache = cache;
        }

        private Guid? GetActorUserId() =>
            Guid.TryParse(User.FindFirstValue(ClaimTypes.NameIdentifier), out var parsedUserId) ? parsedUserId : null;

        private bool IsAdmin() =>
            string.Equals(User.FindFirstValue(ClaimTypes.Role), "Admin", StringComparison.OrdinalIgnoreCase);

        private bool CanAccessOwnSessionistRecord(Guid targetUserId)
        {
            var actorUserId = GetActorUserId();
            return IsAdmin() || (actorUserId.HasValue && actorUserId.Value == targetUserId);
        }

        private async Task EnsureEventLineupColumns(NpgsqlConnection connection)
        {
            const string sql = @"
                ALTER TABLE events ADD COLUMN IF NOT EXISTS artist_lineup text;
                ALTER TABLE events ADD COLUMN IF NOT EXISTS sessionist_lineup text;";

            using var cmd = new NpgsqlCommand(sql, connection);
            await cmd.ExecuteNonQueryAsync();
        }

        private async Task EnsureAvailabilityColumn(NpgsqlConnection connection)
        {
            const string sql = @"ALTER TABLE users ADD COLUMN IF NOT EXISTS is_available boolean NOT NULL DEFAULT TRUE;";
            using var cmd = new NpgsqlCommand(sql, connection);
            await cmd.ExecuteNonQueryAsync();
        }

        private async Task EnsureTalentRegistrationColumns(NpgsqlConnection connection)
        {
            const string sql = @"
                ALTER TABLE users ADD COLUMN IF NOT EXISTS talent_category VARCHAR(60);
                ALTER TABLE users ADD COLUMN IF NOT EXISTS member_names TEXT;";

            using var cmd = new NpgsqlCommand(sql, connection);
            await cmd.ExecuteNonQueryAsync();
        }

        private async Task EnsureProfileStorageColumns(NpgsqlConnection connection)
        {
            const string sql = @"
                ALTER TABLE users ADD COLUMN IF NOT EXISTS bio TEXT;
                ALTER TABLE users ADD COLUMN IF NOT EXISTS genres TEXT;
                ALTER TABLE users ADD COLUMN IF NOT EXISTS spotify_link TEXT;
                ALTER TABLE users ADD COLUMN IF NOT EXISTS profile_picture TEXT;
                ALTER TABLE users ALTER COLUMN bio TYPE TEXT;
                ALTER TABLE users ALTER COLUMN genres TYPE TEXT;
                ALTER TABLE users ALTER COLUMN spotify_link TYPE TEXT;
                ALTER TABLE users ALTER COLUMN profile_picture TYPE TEXT;";

            using var cmd = new NpgsqlCommand(sql, connection);
            await cmd.ExecuteNonQueryAsync();
        }

        private async Task EnsureTalentInteractionTables(NpgsqlConnection connection)
        {
            const string sql = @"
                CREATE TABLE IF NOT EXISTS customer_favorites (
                    id uuid PRIMARY KEY,
                    customer_id uuid NOT NULL,
                    target_user_id uuid NOT NULL,
                    target_role varchar(50) NOT NULL,
                    created_at timestamptz NOT NULL DEFAULT NOW(),
                    CONSTRAINT uq_customer_favorite UNIQUE (customer_id, target_user_id, target_role)
                );

                CREATE TABLE IF NOT EXISTS talent_reviews (
                    id uuid PRIMARY KEY,
                    customer_id uuid NOT NULL,
                    target_user_id uuid NOT NULL,
                    target_role varchar(50) NOT NULL,
                    rating int NOT NULL,
                    feedback text NULL,
                    created_at timestamptz NOT NULL DEFAULT NOW(),
                    updated_at timestamptz NOT NULL DEFAULT NOW(),
                    CONSTRAINT uq_customer_review UNIQUE (customer_id, target_user_id, target_role)
                );";

            using var cmd = new NpgsqlCommand(sql, connection);
            await cmd.ExecuteNonQueryAsync();
        }

        private async Task EnsureSessionistSchemaOnce(NpgsqlConnection connection)
        {
            if (_sessionistSchemaEnsured) return;

            await SessionistSchemaLock.WaitAsync();
            try
            {
                if (_sessionistSchemaEnsured) return;

                await EnsureAvailabilityColumn(connection);
                await EnsureTalentRegistrationColumns(connection);
                await EnsureProfileStorageColumns(connection);
                await EnsureVerifiedGigsTableExists(connection);
                await EnsureTalentInteractionTables(connection);
                await CommunitySupport.EnsureCommunitySchemaAsync(connection);
                await PlatformFeatureSupport.EnsureSharedBusinessSchemaAsync(connection);
                _sessionistSchemaEnsured = true;
            }
            finally
            {
                SessionistSchemaLock.Release();
            }
        }

        [HttpGet("all")]
        public async Task<IActionResult> GetAllSessionists()
        {
            try
            {
                var sessionists = new List<object>();
                await using var connection = new NpgsqlConnection(_connectionString);
                await connection.OpenAsync();
                await EnsureSessionistSchemaOnce(connection);

                string sql = @"
                    SELECT
                        u.id,
                        u.stagename,
                        u.firstname,
                        u.lastname,
                        u.profile_picture,
                        u.genres,
                        COALESCE(u.is_available, TRUE),
                        COALESCE(u.is_verified, FALSE),
                        u.bio,
                        u.spotify_link,
                        u.talent_category,
                        u.member_names,
                        COALESCE(u.base_price, 0),
                        COALESCE(review_stats.average_rating, 0),
                        COALESCE(review_stats.review_count, 0),
                        COALESCE(u.verification_status, 'Not Submitted')
                    FROM users u
                    LEFT JOIN (
                        SELECT
                            target_user_id,
                            ROUND(AVG(rating)::numeric, 1) AS average_rating,
                            COUNT(*) AS review_count
                        FROM talent_reviews
                        WHERE LOWER(COALESCE(target_role, '')) = 'sessionist'
                        GROUP BY target_user_id
                    ) review_stats ON review_stats.target_user_id = u.id
                    WHERE u.role = 'Sessionist'";
                using var cmd = new NpgsqlCommand(sql, connection);
                cmd.CommandTimeout = 5;

                using var reader = await cmd.ExecuteReaderAsync();
                while (await reader.ReadAsync())
                {
                    string stage = reader.IsDBNull(1) ? "" : reader.GetString(1);
                    string first = reader.IsDBNull(2) ? "" : reader.GetString(2);
                    string last = reader.IsDBNull(3) ? "" : reader.GetString(3);
                    
                    var verificationStatus = reader.IsDBNull(15) ? "Not Submitted" : reader.GetString(15);
                    var isApproved = verificationStatus.Equals("Approved", StringComparison.OrdinalIgnoreCase);
                    sessionists.Add(new
                    {
                        id = reader.GetGuid(0),
                        displayName = !string.IsNullOrEmpty(stage) ? stage : $"{first} {last}".Trim(),
                        profilePicture = CommunitySupport.NormalizeListImage(
                            reader.IsDBNull(4) ? "" : reader.GetString(4),
                            "https://images.unsplash.com/photo-1493225457124-a3eb161ffa5f?auto=format&fit=crop&q=80&w=300"),
                        genres = reader.IsDBNull(5) ? "Sessionist" : reader.GetString(5),
                        isAvailable = reader.IsDBNull(6) || reader.GetBoolean(6),
                        isVerified = isApproved,
                        verificationStatus,
                        talentCategory = reader.IsDBNull(10) ? "" : reader.GetString(10),
                        memberNames = reader.IsDBNull(11) ? "" : reader.GetString(11),
                        basePrice = reader.IsDBNull(12) ? 0 : reader.GetDecimal(12),
                        averageRating = reader.IsDBNull(13) ? 0 : reader.GetDecimal(13),
                        reviewCount = reader.IsDBNull(14) ? 0 : Convert.ToInt32(reader.GetInt64(14)),
                        profileCompletionPercent = CommunitySupport.CalculateProfileCompletion(
                            "Sessionist",
                            first,
                            last,
                            reader.IsDBNull(8) ? "" : reader.GetString(8),
                            reader.IsDBNull(4) ? "" : reader.GetString(4),
                            reader.IsDBNull(5) ? "" : reader.GetString(5),
                            reader.IsDBNull(9) ? "" : reader.GetString(9),
                            "",
                            "",
                            "",
                            stage
                        ).Percent
                    });
                }
                return Ok(sessionists);
            }
            catch (Exception)
            {
                return StatusCode(500, new { message = "Unable to update sessionist profile right now." });
            }
        }

        [HttpGet("{id}")]
        public async Task<IActionResult> GetSessionistById(Guid id)
        {
            try
            {
                var canViewPrivateFields = CanAccessOwnSessionistRecord(id);
                await using var connection = new NpgsqlConnection(_connectionString);
                await connection.OpenAsync();
                await EnsureSessionistSchemaOnce(connection);

                string sql = "SELECT stagename, firstname, lastname, COALESCE(email, ''), profile_picture, bio, genres, spotify_link, COALESCE(is_available, TRUE), COALESCE(is_verified, FALSE), talent_category, member_names, COALESCE(base_price, 0) FROM users WHERE id = @id";
                using var cmd = new NpgsqlCommand(sql, connection);
                cmd.Parameters.AddWithValue("@id", id);
                cmd.CommandTimeout = 5;

                using var reader = await cmd.ExecuteReaderAsync();
                if (await reader.ReadAsync())
                {
                    string stage = reader.IsDBNull(0) ? "" : reader.GetString(0);
                    string first = reader.IsDBNull(1) ? "" : reader.GetString(1);
                    string last = reader.IsDBNull(2) ? "" : reader.GetString(2);
                    string displayName = !string.IsNullOrEmpty(stage) ? stage : $"{first} {last}".Trim();
                    string email = reader.IsDBNull(3) ? "" : reader.GetString(3);
                    string profilePicture = reader.IsDBNull(4) ? "" : reader.GetString(4);
                    string bio = reader.IsDBNull(5) ? "This sessionist hasn't written a bio yet." : reader.GetString(5);
                    string genres = reader.IsDBNull(6) ? "Sessionist" : reader.GetString(6);
                    string spotifyLink = reader.IsDBNull(7) ? "" : reader.GetString(7);
                    bool isAvailable = reader.IsDBNull(8) || reader.GetBoolean(8);
                    bool isVerified = !reader.IsDBNull(9) && reader.GetBoolean(9);
                    string talentCategory = reader.IsDBNull(10) ? "" : reader.GetString(10);
                    string memberNames = reader.IsDBNull(11) ? "" : reader.GetString(11);
                    decimal basePrice = reader.IsDBNull(12) ? 0 : reader.GetDecimal(12);
                    var profileSummary = CommunitySupport.CalculateProfileCompletion("Sessionist", first, last, bio, profilePicture, genres, spotifyLink, "", "", "", stage);
                    await reader.CloseAsync();
                    await CommunitySupport.SyncProfileVerificationAsync(connection, id, "Sessionist", first, last, bio, profilePicture, genres, spotifyLink, "", "", "", stage);
                    var verification = await CommunitySupport.GetTalentVerificationSnapshotAsync(connection, id, "Sessionist");
                    DateTime? verificationAssetSubmittedAt = null;
                    bool hasVerificationIdFront = false;
                    bool hasVerificationIdBack = false;
                    bool hasVerificationSelfie = false;
                    string verificationIdType = string.Empty;
                    string verificationIdLast4 = string.Empty;
                    string verificationEvidenceSummary = string.Empty;
                    string verificationSupportingLinks = string.Empty;
                    string verificationReferenceName = string.Empty;
                    string verificationReferenceContact = string.Empty;

                    const string verificationAssetsSql = @"
                        SELECT created_at,
                               COALESCE(id_type, ''),
                               COALESCE(id_number_last4, ''),
                               COALESCE(evidence_summary, ''),
                               COALESCE(supporting_links, ''),
                               COALESCE(reference_name, ''),
                               COALESCE(reference_contact, ''),
                               COALESCE(id_image_front, ''),
                               COALESCE(id_image_back, ''),
                               COALESCE(selfie_image, '')
                        FROM talent_verification_requests
                        WHERE user_id = @id
                          AND role = 'Sessionist'
                        ORDER BY created_at DESC
                        LIMIT 1;";

                    using (var verificationAssetsCmd = new NpgsqlCommand(verificationAssetsSql, connection))
                    {
                        verificationAssetsCmd.Parameters.AddWithValue("@id", id);
                        verificationAssetsCmd.CommandTimeout = 5;

                        using var verificationAssetsReader = await verificationAssetsCmd.ExecuteReaderAsync(System.Data.CommandBehavior.SingleRow);
                        if (await verificationAssetsReader.ReadAsync())
                        {
                            verificationAssetSubmittedAt = verificationAssetsReader.IsDBNull(0) ? null : (DateTime?)verificationAssetsReader.GetDateTime(0);
                            verificationIdType = verificationAssetsReader.IsDBNull(1) ? string.Empty : verificationAssetsReader.GetString(1);
                            verificationIdLast4 = verificationAssetsReader.IsDBNull(2) ? string.Empty : verificationAssetsReader.GetString(2);
                            verificationEvidenceSummary = verificationAssetsReader.IsDBNull(3) ? string.Empty : verificationAssetsReader.GetString(3);
                            verificationSupportingLinks = verificationAssetsReader.IsDBNull(4) ? string.Empty : verificationAssetsReader.GetString(4);
                            verificationReferenceName = verificationAssetsReader.IsDBNull(5) ? string.Empty : verificationAssetsReader.GetString(5);
                            verificationReferenceContact = verificationAssetsReader.IsDBNull(6) ? string.Empty : verificationAssetsReader.GetString(6);
                            hasVerificationIdFront = !verificationAssetsReader.IsDBNull(7) && !string.IsNullOrWhiteSpace(verificationAssetsReader.GetString(7));
                            hasVerificationIdBack = !verificationAssetsReader.IsDBNull(8) && !string.IsNullOrWhiteSpace(verificationAssetsReader.GetString(8));
                            hasVerificationSelfie = !verificationAssetsReader.IsDBNull(9) && !string.IsNullOrWhiteSpace(verificationAssetsReader.GetString(9));
                        }
                    }

                    var relatedEvents = new List<object>();
                    const string relatedEventsSql = @"
                        SELECT id, title, event_time, city, location, COALESCE(status, 'Upcoming'), poster_url
                        FROM events
                        WHERE sessionist_lineup LIKE @needle
                        ORDER BY event_time DESC
                        LIMIT 8";

                    using var eventsCmd = new NpgsqlCommand(relatedEventsSql, connection);
                    eventsCmd.Parameters.AddWithValue("@needle", $"%{id}%");
                    eventsCmd.CommandTimeout = 5;

                    using var eventReader = await eventsCmd.ExecuteReaderAsync();
                    while (await eventReader.ReadAsync())
                    {
                        relatedEvents.Add(new
                        {
                            id = eventReader.GetGuid(0),
                            title = eventReader.IsDBNull(1) ? "Untitled Event" : eventReader.GetString(1),
                            time = eventReader.IsDBNull(2) ? DateTime.MinValue : eventReader.GetDateTime(2),
                            city = eventReader.IsDBNull(3) ? "" : eventReader.GetString(3),
                            location = eventReader.IsDBNull(4) ? "" : eventReader.GetString(4),
                            status = eventReader.IsDBNull(5) ? "Upcoming" : eventReader.GetString(5),
                            posterUrl = eventReader.IsDBNull(6) ? "" : eventReader.GetString(6)
                        });
                    }
                    await eventReader.CloseAsync();

                    var workedWithArtists = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    const string workedWithArtistsSql = @"
                        SELECT COALESCE(artists, '')
                        FROM events
                        WHERE sessionist_lineup LIKE @needle
                          AND (
                              COALESCE(status, 'Upcoming') = 'Finished'
                              OR event_time < NOW()
                          )
                        ORDER BY event_time DESC
                        LIMIT 20;";

                    using (var workedWithCmd = new NpgsqlCommand(workedWithArtistsSql, connection))
                    {
                        workedWithCmd.Parameters.AddWithValue("@needle", $"%{id}%");
                        workedWithCmd.CommandTimeout = 5;

                        using var workedWithReader = await workedWithCmd.ExecuteReaderAsync();
                        while (await workedWithReader.ReadAsync())
                        {
                            var artistText = workedWithReader.IsDBNull(0) ? string.Empty : workedWithReader.GetString(0);
                            foreach (var artistName in artistText.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                            {
                                if (!string.IsNullOrWhiteSpace(artistName))
                                {
                                    workedWithArtists.Add(artistName);
                                }
                            }
                        }
                    }

                    return Ok(new
                    {
                        displayName,
                        firstName = first,
                        lastName = last,
                        stageName = stage,
                        email = canViewPrivateFields ? email : string.Empty,
                        profilePicture,
                        bio,
                        genres,
                        spotifyLink,
                        talentCategory,
                        memberNames,
                        basePrice,
                        isAvailable,
                        isVerified = verification.HasApprovedRequest,
                        verificationStatus = verification.Status,
                        verificationLevel = verification.Level,
                        verificationMethod = canViewPrivateFields ? verification.Method : string.Empty,
                        verificationNotes = canViewPrivateFields ? verification.Notes : string.Empty,
                        verificationSubmittedAt = canViewPrivateFields ? verification.SubmittedAt : null,
                        verificationReviewedAt = canViewPrivateFields ? verification.ReviewedAt : null,
                        verificationUploads = canViewPrivateFields
                            ? new
                            {
                                idFrontSubmitted = hasVerificationIdFront,
                                idBackSubmitted = hasVerificationIdBack,
                                selfieSubmitted = hasVerificationSelfie,
                                submittedAt = verificationAssetSubmittedAt
                            }
                            : null,
                        verificationRequest = canViewPrivateFields
                            ? new
                            {
                                idType = verificationIdType,
                                idLast4 = verificationIdLast4,
                                evidenceSummary = verificationEvidenceSummary,
                                supportingLinks = verificationSupportingLinks,
                                referenceName = verificationReferenceName,
                                referenceContact = verificationReferenceContact
                            }
                            : null,
                        profileCompletionPercent = profileSummary.Percent,
                        profileCompletionLabel = profileSummary.Label,
                        relatedEvents,
                        workedWithArtists = workedWithArtists.Take(12).ToArray()
                    });
                }
                return NotFound(new { message = "Sessionist not found." });
            }
            catch (Exception)
            {
                return StatusCode(500, new { message = "Failed to load sessionist profile." });
            }
        }

        [HttpGet("spotlight")]
        public async Task<IActionResult> GetSpotlightSessionists()
        {
            try
            {
                var sessionists = new List<object>();
                await using var connection = new NpgsqlConnection(_connectionString);
                await connection.OpenAsync();
                await EnsureSessionistSchemaOnce(connection);

                string sql = "SELECT id, stagename, firstname, lastname, profile_picture, genres, COALESCE(is_available, TRUE), COALESCE(is_verified, FALSE) FROM users WHERE role = 'Sessionist' LIMIT 4";
                using var cmd = new NpgsqlCommand(sql, connection);
                cmd.CommandTimeout = 5;

                using var reader = await cmd.ExecuteReaderAsync();
                while (await reader.ReadAsync())
                {
                    string stage = reader.IsDBNull(1) ? "" : reader.GetString(1);
                    string first = reader.IsDBNull(2) ? "" : reader.GetString(2);
                    string last = reader.IsDBNull(3) ? "" : reader.GetString(3);

                    sessionists.Add(new
                    {
                        id = reader.GetGuid(0),
                        displayName = !string.IsNullOrEmpty(stage) ? stage : $"{first} {last}".Trim(),
                        profilePicture = reader.IsDBNull(4) ? "" : reader.GetString(4),
                        genres = reader.IsDBNull(5) ? "Sessionist" : reader.GetString(5),
                        isAvailable = reader.IsDBNull(6) || reader.GetBoolean(6),
                        isVerified = !reader.IsDBNull(7) && reader.GetBoolean(7)
                    });
                }
                return Ok(sessionists);
            }
            catch (Exception)
            {
                return StatusCode(500, new { message = "Failed to load spotlight sessionists right now." });
            }
        }

        [Authorize(Roles = "Artist,Sessionist")]
        [HttpPut("{id}/profile")]
        public async Task<IActionResult> UpdateProfile(Guid id, [FromBody] UpdateArtistProfileDto req) 
        {
            try
            {
                if (!CanAccessOwnSessionistRecord(id))
                {
                    return Forbid();
                }

                await using var connection = new NpgsqlConnection(_connectionString);
                await connection.OpenAsync();
                await EnsureSessionistSchemaOnce(connection);
                await SecuritySupport.EnsureSecuritySchemaAsync(connection);

                var sanitizedFirstName = SecuritySupport.SanitizePlainText(req.firstName, 80, false);
                var sanitizedLastName = SecuritySupport.SanitizePlainText(req.lastName, 80, false);
                var sanitizedStageName = SecuritySupport.SanitizePlainText(req.stageName, 120, false);
                var sanitizedBio = SecuritySupport.SanitizePlainText(req.bio, 2500, true);
                var sanitizedGenres = SecuritySupport.SanitizePlainText(req.genres, 400, false);
                var sanitizedSpotifyLink = SecuritySupport.SanitizeUrl(req.spotifyLink);
                var normalizedPicture = SecuritySupport.ValidateAndNormalizeImageDataUrl(req.profilePicture, 2_500_000, out var imageError);
                if (imageError is not null)
                {
                    return BadRequest(new { message = imageError });
                }
                var pictureScan = await _uploadScanningService.ScanDataUrlAsync(normalizedPicture, "sessionist profile image");
                if (!pictureScan.IsClean)
                {
                    return BadRequest(new { message = pictureScan.Message });
                }

                if (string.IsNullOrWhiteSpace(sanitizedFirstName) || string.IsNullOrWhiteSpace(sanitizedLastName))
                {
                    return BadRequest(new { message = "First name and last name are required." });
                }

                string sql = @"
                    UPDATE users SET 
                        firstname = @firstName,
                        lastname = @lastName,
                        stagename = @stageName,
                        bio = @bio, 
                        genres = @genres, 
                        spotify_link = @spotify, 
                        profile_picture = COALESCE(@pic, profile_picture),
                        is_available = COALESCE(@isAvailable, is_available),
                        base_price = COALESCE(@basePrice, base_price) 
                    WHERE id = @id AND role = 'Sessionist'";

                using var cmd = new NpgsqlCommand(sql, connection);
                cmd.CommandTimeout = 5;
                cmd.Parameters.AddWithValue("@id", id);
                cmd.Parameters.AddWithValue("@firstName", sanitizedFirstName);
                cmd.Parameters.AddWithValue("@lastName", sanitizedLastName);
                cmd.Parameters.AddWithValue("@stageName", string.IsNullOrWhiteSpace(sanitizedStageName) ? DBNull.Value : sanitizedStageName);
                cmd.Parameters.AddWithValue("@bio", string.IsNullOrWhiteSpace(sanitizedBio) ? DBNull.Value : sanitizedBio);
                cmd.Parameters.AddWithValue("@genres", string.IsNullOrWhiteSpace(sanitizedGenres) ? DBNull.Value : sanitizedGenres);
                cmd.Parameters.AddWithValue("@spotify", string.IsNullOrWhiteSpace(sanitizedSpotifyLink) ? DBNull.Value : sanitizedSpotifyLink);
                cmd.Parameters.AddWithValue("@pic", (object?)normalizedPicture ?? DBNull.Value);
                cmd.Parameters.AddWithValue("@isAvailable", (object?)req.isAvailable ?? DBNull.Value);
                var sanitizedBasePrice = req.basePrice.HasValue
                    ? Math.Round(req.basePrice.Value, 2, MidpointRounding.AwayFromZero)
                    : (decimal?)null;
                cmd.Parameters.Add("@basePrice", NpgsqlTypes.NpgsqlDbType.Numeric).Value = (object?)sanitizedBasePrice ?? DBNull.Value;

                int rows = await cmd.ExecuteNonQueryAsync();
                if (rows == 0) return BadRequest(new { message = "Update failed. Make sure you are a Sessionist." });

                const string profileSql = @"
                    SELECT COALESCE(stagename, ''),
                           COALESCE(firstname, ''),
                           COALESCE(lastname, ''),
                           COALESCE(profile_picture, ''),
                           COALESCE(bio, ''),
                           COALESCE(genres, ''),
                           COALESCE(spotify_link, ''),
                           COALESCE(is_available, TRUE),
                           COALESCE(base_price, 0)
                    FROM users
                    WHERE id = @id AND role = 'Sessionist';";

                string stage = "";
                string first = "";
                string last = "";
                string profilePicture = "";
                string bio = "";
                string genres = "";
                string spotifyLink = "";
                bool isAvailable = true;
                decimal basePrice = 0;

                using (var profileCmd = new NpgsqlCommand(profileSql, connection))
                {
                    profileCmd.Parameters.AddWithValue("@id", id);
                    profileCmd.CommandTimeout = 5;
                    using var reader = await profileCmd.ExecuteReaderAsync();
                    if (await reader.ReadAsync())
                    {
                        stage = reader.IsDBNull(0) ? "" : reader.GetString(0);
                        first = reader.IsDBNull(1) ? "" : reader.GetString(1);
                        last = reader.IsDBNull(2) ? "" : reader.GetString(2);
                        profilePicture = reader.IsDBNull(3) ? "" : reader.GetString(3);
                        bio = reader.IsDBNull(4) ? "" : reader.GetString(4);
                        genres = reader.IsDBNull(5) ? "" : reader.GetString(5);
                        spotifyLink = reader.IsDBNull(6) ? "" : reader.GetString(6);
                        isAvailable = reader.IsDBNull(7) || reader.GetBoolean(7);
                        basePrice = reader.IsDBNull(8) ? 0 : reader.GetDecimal(8);
                    }
                }

                var profileSummary = CommunitySupport.CalculateProfileCompletion("Sessionist", first, last, bio, profilePicture, genres, spotifyLink, "", "", "", stage);
                await CommunitySupport.SyncProfileVerificationAsync(connection, id, "Sessionist", first, last, bio, profilePicture, genres, spotifyLink, "", "", "", stage);
                var verification = await CommunitySupport.GetTalentVerificationSnapshotAsync(connection, id, "Sessionist");
                await SecuritySupport.LogSecurityEventAsync(
                    connection,
                    id,
                    "Sessionist",
                    "profile_updated",
                    "user",
                    id,
                    HttpContext,
                    "Sessionist profile updated.");

                return Ok(new
                {
                    message = "Profile updated successfully!",
                    displayName = string.IsNullOrWhiteSpace(stage) ? $"{first} {last}".Trim() : stage,
                    firstName = first,
                    lastName = last,
                    stageName = stage,
                    isAvailable,
                    profilePicture,
                    basePrice,
                    isVerified = profileSummary.IsVerified,
                    verificationStatus = verification.Status,
                    verificationLevel = verification.Level,
                    verificationMethod = verification.Method,
                    profileCompletionPercent = profileSummary.Percent,
                    profileCompletionLabel = profileSummary.Label
                });
            }
            catch (Exception)
            {
                return StatusCode(500, new { message = "Failed to load sessionists right now." });
            }
        }

        [Authorize(Roles = "Artist,Sessionist")]
        [HttpPost("{id}/change-password")]
        public async Task<IActionResult> ChangePassword(Guid id, [FromBody] ChangeTalentPasswordDto req)
        {
            try
            {
                if (!CanAccessOwnSessionistRecord(id))
                {
                    return Forbid();
                }

                if (string.IsNullOrWhiteSpace(req.currentPassword) || string.IsNullOrWhiteSpace(req.newPassword) || string.IsNullOrWhiteSpace(req.otp))
                {
                    return BadRequest(new { message = "Current password, new password, and OTP are required." });
                }

                if (!IsStrongPassword(req.newPassword))
                {
                    return BadRequest(new { message = "New password must be at least 8 characters and include uppercase, lowercase, number, and special character." });
                }

                await using var connection = new NpgsqlConnection(_connectionString);
                await connection.OpenAsync();
                await SecuritySupport.EnsureSecuritySchemaAsync(connection);

                const string sql = @"
                    SELECT COALESCE(email, ''),
                           COALESCE(passwordhash, '')
                    FROM users
                    WHERE id = @id AND role = 'Sessionist';";

                string email = "";
                string passwordHash = "";
                await using (var cmd = new NpgsqlCommand(sql, connection))
                {
                    cmd.Parameters.Add("@id", NpgsqlDbType.Uuid).Value = id;
                    await using var reader = await cmd.ExecuteReaderAsync();
                    if (!await reader.ReadAsync())
                    {
                        return NotFound(new { message = "Sessionist not found." });
                    }

                    email = reader.IsDBNull(0) ? "" : reader.GetString(0);
                    passwordHash = reader.IsDBNull(1) ? "" : reader.GetString(1);
                }

                var normalizedEmail = NormalizeEmail(email);
                if (string.IsNullOrWhiteSpace(normalizedEmail))
                {
                    return BadRequest(new { message = "Sessionist email is required before changing password." });
                }

                if (!_cache.TryGetValue(normalizedEmail, out string? savedOtp) || string.IsNullOrWhiteSpace(savedOtp))
                {
                    return BadRequest(new { message = "OTP expired or not requested. Request a new code first." });
                }

                if (!string.Equals(savedOtp, req.otp.Trim(), StringComparison.Ordinal))
                {
                    return BadRequest(new { message = "Invalid OTP code." });
                }

                if (!BCrypt.Net.BCrypt.Verify(req.currentPassword, passwordHash))
                {
                    return BadRequest(new { message = "Current password is incorrect." });
                }

                var nextPasswordHash = BCrypt.Net.BCrypt.HashPassword(req.newPassword);
                const string updateSql = "UPDATE users SET passwordhash = @passwordHash WHERE id = @id AND role = 'Sessionist';";
                await using (var updateCmd = new NpgsqlCommand(updateSql, connection))
                {
                    updateCmd.Parameters.Add("@passwordHash", NpgsqlDbType.Text).Value = nextPasswordHash;
                    updateCmd.Parameters.Add("@id", NpgsqlDbType.Uuid).Value = id;
                    await updateCmd.ExecuteNonQueryAsync();
                }

                _cache.Remove(normalizedEmail);
                await SecuritySupport.LogSecurityEventAsync(
                    connection,
                    id,
                    "Sessionist",
                    "password_changed",
                    "user",
                    id,
                    HttpContext,
                    "Sessionist changed password with OTP verification.");

                return Ok(new { message = "Password changed successfully." });
            }
            catch (Exception)
            {
                return StatusCode(500, new { message = "Failed to change password right now." });
            }
        }

        [Authorize(Roles = "Artist,Sessionist")]
        [HttpPost("{id}/verification-request")]
        public async Task<IActionResult> SubmitVerificationRequest(Guid id, [FromBody] SubmitTalentVerificationRequestDto req)
        {
            try
            {
                if (!CanAccessOwnSessionistRecord(id))
                {
                    return Forbid();
                }

                await using var connection = new NpgsqlConnection(_connectionString);
                await connection.OpenAsync();
                await EnsureSessionistSchemaOnce(connection);

                if (!req.consentConfirmed || !req.faceVerificationConsent)
                {
                    return BadRequest(new { message = "You must consent to ID and facial verification before submitting." });
                }

                var verificationPath = SecuritySupport.SanitizePlainText(req.verificationPath, 60, true) ?? "PhilippineIdAndFace";
                var evidenceSummary = SecuritySupport.SanitizePlainText(req.evidenceSummary, 1200, true);
                var idType = SecuritySupport.SanitizePlainText(req.idType, 60, false);
                var idNumberLast4 = SecuritySupport.SanitizePlainText(req.idNumberLast4, 8, false);
                if (string.IsNullOrWhiteSpace(evidenceSummary))
                {
                    return BadRequest(new { message = "Add a clear summary of your proof, references, or sample work." });
                }
                if (string.IsNullOrWhiteSpace(idType))
                {
                    return BadRequest(new { message = "Choose the Philippine ID type you are submitting." });
                }
                if (string.IsNullOrWhiteSpace(idNumberLast4) || idNumberLast4.Length < 4)
                {
                    return BadRequest(new { message = "Enter the last 4 characters of the submitted ID number." });
                }

                var normalizedIdFront = SecuritySupport.ValidateAndNormalizeImageDataUrl(req.idImageFront, 3_500_000, out var idFrontError);
                if (idFrontError is not null)
                {
                    return BadRequest(new { message = idFrontError });
                }
                if (string.IsNullOrWhiteSpace(normalizedIdFront))
                {
                    return BadRequest(new { message = "Upload the front image of your Philippine ID." });
                }

                var normalizedIdBack = SecuritySupport.ValidateAndNormalizeImageDataUrl(req.idImageBack, 3_500_000, out var idBackError);
                if (idBackError is not null)
                {
                    return BadRequest(new { message = idBackError });
                }

                var normalizedSelfie = SecuritySupport.ValidateAndNormalizeImageDataUrl(req.selfieImage, 3_500_000, out var selfieError);
                if (selfieError is not null)
                {
                    return BadRequest(new { message = selfieError });
                }
                if (string.IsNullOrWhiteSpace(normalizedSelfie))
                {
                    return BadRequest(new { message = "Upload a clear selfie for facial verification." });
                }

                var idFrontScan = await _uploadScanningService.ScanDataUrlAsync(normalizedIdFront, "sessionist ID front");
                if (!idFrontScan.IsClean)
                {
                    return BadRequest(new { message = idFrontScan.Message });
                }
                var idBackScan = await _uploadScanningService.ScanDataUrlAsync(normalizedIdBack, "sessionist ID back");
                if (!idBackScan.IsClean)
                {
                    return BadRequest(new { message = idBackScan.Message });
                }
                var selfieScan = await _uploadScanningService.ScanDataUrlAsync(normalizedSelfie, "sessionist verification selfie");
                if (!selfieScan.IsClean)
                {
                    return BadRequest(new { message = selfieScan.Message });
                }

                var automatedAssessment = _automatedVerificationAssessmentService.Assess(
                    idType,
                    idNumberLast4,
                    evidenceSummary,
                    normalizedIdFront,
                    normalizedIdBack,
                    normalizedSelfie);

                const string pendingSql = @"
                    SELECT COUNT(*)
                    FROM talent_verification_requests
                    WHERE user_id = @userId
                      AND status = 'Pending';";
                await using (var pendingCmd = new NpgsqlCommand(pendingSql, connection))
                {
                    pendingCmd.Parameters.Add("@userId", NpgsqlDbType.Uuid).Value = id;
                    var pendingCount = Convert.ToInt32(await pendingCmd.ExecuteScalarAsync() ?? 0);
                    if (pendingCount > 0)
                    {
                        return Conflict(new { message = "A verification request is already under review." });
                    }
                }

                const string insertSql = @"
                    INSERT INTO talent_verification_requests (
                        id, user_id, role, verification_path, evidence_summary, portfolio_links, supporting_links, reference_name, reference_contact,
                        id_type, id_number_last4, id_image_front, id_image_back, selfie_image, consent_confirmed, face_verification_consent,
                        id_review_status, facial_review_status, automated_status, automated_recommendation, automated_score, automated_notes, automated_reviewed_at, status, created_at
                    )
                    VALUES (
                        @id, @userId, 'Sessionist', @verificationPath, @evidenceSummary, @portfolioLinks, @supportingLinks, @referenceName, @referenceContact,
                        @idType, @idNumberLast4, @idImageFront, @idImageBack, @selfieImage, @consentConfirmed, @faceVerificationConsent,
                        'Pending', 'Pending', @automatedStatus, @automatedRecommendation, @automatedScore, @automatedNotes, NOW(), 'Pending', NOW()
                    );";
                await using (var insertCmd = new NpgsqlCommand(insertSql, connection))
                {
                    insertCmd.Parameters.Add("@id", NpgsqlDbType.Uuid).Value = Guid.NewGuid();
                    insertCmd.Parameters.Add("@userId", NpgsqlDbType.Uuid).Value = id;
                    insertCmd.Parameters.Add("@verificationPath", NpgsqlDbType.Text).Value = verificationPath;
                    insertCmd.Parameters.Add("@evidenceSummary", NpgsqlDbType.Text).Value = evidenceSummary;
                    insertCmd.Parameters.Add("@portfolioLinks", NpgsqlDbType.Text).Value = (object?)SecuritySupport.SanitizePlainText(req.portfolioLinks, 1500, true) ?? DBNull.Value;
                    insertCmd.Parameters.Add("@supportingLinks", NpgsqlDbType.Text).Value = (object?)SecuritySupport.SanitizePlainText(req.supportingLinks, 1500, true) ?? DBNull.Value;
                    insertCmd.Parameters.Add("@referenceName", NpgsqlDbType.Text).Value = (object?)SecuritySupport.SanitizePlainText(req.referenceName, 160, true) ?? DBNull.Value;
                    insertCmd.Parameters.Add("@referenceContact", NpgsqlDbType.Text).Value = (object?)SecuritySupport.SanitizePlainText(req.referenceContact, 160, true) ?? DBNull.Value;
                    insertCmd.Parameters.Add("@idType", NpgsqlDbType.Text).Value = idType;
                    insertCmd.Parameters.Add("@idNumberLast4", NpgsqlDbType.Text).Value = idNumberLast4;
                    insertCmd.Parameters.Add("@idImageFront", NpgsqlDbType.Text).Value = SecuritySupport.ProtectSensitiveData(normalizedIdFront, _connectionString);
                    insertCmd.Parameters.Add("@idImageBack", NpgsqlDbType.Text).Value = string.IsNullOrWhiteSpace(normalizedIdBack)
                        ? DBNull.Value
                        : (object)SecuritySupport.ProtectSensitiveData(normalizedIdBack, _connectionString);
                    insertCmd.Parameters.Add("@selfieImage", NpgsqlDbType.Text).Value = SecuritySupport.ProtectSensitiveData(normalizedSelfie, _connectionString);
                    insertCmd.Parameters.Add("@consentConfirmed", NpgsqlDbType.Boolean).Value = req.consentConfirmed;
                    insertCmd.Parameters.Add("@faceVerificationConsent", NpgsqlDbType.Boolean).Value = req.faceVerificationConsent;
                    insertCmd.Parameters.Add("@automatedStatus", NpgsqlDbType.Text).Value = automatedAssessment.Status;
                    insertCmd.Parameters.Add("@automatedRecommendation", NpgsqlDbType.Text).Value = automatedAssessment.Recommendation;
                    insertCmd.Parameters.Add("@automatedScore", NpgsqlDbType.Integer).Value = automatedAssessment.Score;
                    insertCmd.Parameters.Add("@automatedNotes", NpgsqlDbType.Text).Value = automatedAssessment.Notes;
                    await insertCmd.ExecuteNonQueryAsync();
                }

                const string updateSql = @"
                    UPDATE users
                    SET verification_status = 'Pending',
                        verification_level = 'Identity Review',
                        verification_method = @verificationMethod,
                        verification_notes = 'Submitted Philippine ID and selfie evidence. Waiting for admin review.',
                        verification_last_submitted_at = NOW()
                    WHERE id = @userId;";
                await using var updateCmd = new NpgsqlCommand(updateSql, connection);
                updateCmd.Parameters.Add("@userId", NpgsqlDbType.Uuid).Value = id;
                updateCmd.Parameters.Add("@verificationMethod", NpgsqlDbType.Text).Value = "Philippine ID + Facial Review";
                await updateCmd.ExecuteNonQueryAsync();

                return Ok(new
                {
                    message = "Verification request submitted. Automated screening is complete and the request is now waiting for admin review.",
                    status = "Pending",
                    automatedStatus = automatedAssessment.Status,
                    automatedRecommendation = automatedAssessment.Recommendation,
                    automatedScore = automatedAssessment.Score
                });
            }
            catch (Exception)
            {
                return StatusCode(500, new { message = "Failed to submit verification request." });
            }
        }

        [Authorize(Roles = "Artist,Sessionist")]
        [HttpPatch("{id}/availability")]
        public async Task<IActionResult> UpdateAvailability(Guid id, [FromBody] UpdateAvailabilityDto req)
        {
            try
            {
                if (!CanAccessOwnSessionistRecord(id))
                {
                    return Forbid();
                }

                await using var connection = new NpgsqlConnection(_connectionString);
                await connection.OpenAsync();
                await EnsureSessionistSchemaOnce(connection);

                const string sql = "UPDATE users SET is_available = @isAvailable WHERE id = @id AND role = 'Sessionist'";
                using var cmd = new NpgsqlCommand(sql, connection);
                cmd.Parameters.AddWithValue("@id", id);
                cmd.Parameters.AddWithValue("@isAvailable", req.isAvailable);
                cmd.CommandTimeout = 5;

                var rows = await cmd.ExecuteNonQueryAsync();
                if (rows == 0)
                {
                    return NotFound(new { message = "Sessionist not found." });
                }

                return Ok(new { message = "Availability updated successfully.", isAvailable = req.isAvailable });
            }
            catch (Exception)
            {
                return StatusCode(500, new { message = "Unable to update sessionist availability right now." });
            }
        }

        private static async Task EnsureVerifiedGigsTableExists(NpgsqlConnection connection)
        {
            const string sql = @"
                CREATE TABLE IF NOT EXISTS verified_gigs (
                    id uuid PRIMARY KEY,
                    user_id uuid NOT NULL,
                    event_id uuid NOT NULL,
                    role_at_event varchar(30) NOT NULL,
                    verification_status varchar(30) NOT NULL DEFAULT 'Verified',
                    notes text NULL,
                    verified_at timestamptz NOT NULL DEFAULT NOW(),
                    UNIQUE (user_id, event_id, role_at_event)
                );";

            using var cmd = new NpgsqlCommand(sql, connection);
            await cmd.ExecuteNonQueryAsync();
        }

        private static string NormalizeEmail(string? email)
        {
            return (email ?? string.Empty).Trim().ToLowerInvariant();
        }

        private static bool IsStrongPassword(string? password)
        {
            if (string.IsNullOrWhiteSpace(password) || password.Length < 8)
            {
                return false;
            }

            return password.Any(char.IsUpper)
                && password.Any(char.IsLower)
                && password.Any(char.IsDigit)
                && password.Any(ch => !char.IsLetterOrDigit(ch));
        }
    }
}
