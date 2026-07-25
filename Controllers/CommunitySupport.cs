using Npgsql;
using NpgsqlTypes;

namespace ImajinationAPI.Controllers
{
    public record ProfileCompletionResult(int Percent, bool IsVerified, string Label);
    public record TalentVerificationSnapshot(
        string Status,
        string Level,
        string Method,
        string Notes,
        DateTime? SubmittedAt,
        DateTime? ReviewedAt,
        bool HasApprovedRequest);

    public class SubmitTalentVerificationRequestDto
    {
        public string? verificationPath { get; set; }
        public string? evidenceSummary { get; set; }
        public string? portfolioLinks { get; set; }
        public string? supportingLinks { get; set; }
        public string? referenceName { get; set; }
        public string? referenceContact { get; set; }
        public string? idType { get; set; }
        public string? idNumberLast4 { get; set; }
        public string? idImageFront { get; set; }
        public string? idImageBack { get; set; }
        public string? selfieImage { get; set; }
        public bool consentConfirmed { get; set; }
        public bool faceVerificationConsent { get; set; }
    }

    public static class CommunitySupport
    {
        private const int DefaultListDataUrlLimit = 4_000_000;

        public static async Task EnsureCommunitySchemaAsync(NpgsqlConnection connection)
        {
            const string sql = @"
                ALTER TABLE users ADD COLUMN IF NOT EXISTS is_verified boolean NOT NULL DEFAULT FALSE;
                ALTER TABLE users ADD COLUMN IF NOT EXISTS profile_completed_at timestamptz NULL;
                ALTER TABLE users ADD COLUMN IF NOT EXISTS verification_status varchar(30) NOT NULL DEFAULT 'Not Submitted';
                ALTER TABLE users ADD COLUMN IF NOT EXISTS verification_level varchar(40) NULL;
                ALTER TABLE users ADD COLUMN IF NOT EXISTS verification_method varchar(60) NULL;
                ALTER TABLE users ADD COLUMN IF NOT EXISTS verification_notes text NULL;
                ALTER TABLE users ADD COLUMN IF NOT EXISTS verification_last_submitted_at timestamptz NULL;
                ALTER TABLE users ADD COLUMN IF NOT EXISTS verification_reviewed_at timestamptz NULL;

                CREATE TABLE IF NOT EXISTS community_posts (
                    id uuid PRIMARY KEY,
                    user_id uuid NOT NULL,
                    role varchar(30) NOT NULL,
                    content text NOT NULL,
                    image_url text NULL,
                    created_at timestamptz NOT NULL DEFAULT NOW()
                );

                CREATE INDEX IF NOT EXISTS idx_community_posts_user_id ON community_posts(user_id);
                CREATE INDEX IF NOT EXISTS idx_community_posts_created_at ON community_posts(created_at DESC);

                CREATE TABLE IF NOT EXISTS talent_verification_requests (
                    id uuid PRIMARY KEY,
                    user_id uuid NOT NULL,
                    role varchar(30) NOT NULL,
                    verification_path varchar(60) NOT NULL,
                    evidence_summary text NOT NULL,
                    portfolio_links text NULL,
                    supporting_links text NULL,
                    reference_name text NULL,
                    reference_contact text NULL,
                    status varchar(30) NOT NULL DEFAULT 'Pending',
                    admin_notes text NULL,
                    created_at timestamptz NOT NULL DEFAULT NOW(),
                    reviewed_at timestamptz NULL
                );

                ALTER TABLE talent_verification_requests ADD COLUMN IF NOT EXISTS id_type varchar(60) NULL;
                ALTER TABLE talent_verification_requests ADD COLUMN IF NOT EXISTS id_number_last4 varchar(8) NULL;
                ALTER TABLE talent_verification_requests ADD COLUMN IF NOT EXISTS id_image_front text NULL;
                ALTER TABLE talent_verification_requests ADD COLUMN IF NOT EXISTS id_image_back text NULL;
                ALTER TABLE talent_verification_requests ADD COLUMN IF NOT EXISTS selfie_image text NULL;
                ALTER TABLE talent_verification_requests ADD COLUMN IF NOT EXISTS consent_confirmed boolean NOT NULL DEFAULT FALSE;
                ALTER TABLE talent_verification_requests ADD COLUMN IF NOT EXISTS face_verification_consent boolean NOT NULL DEFAULT FALSE;
                ALTER TABLE talent_verification_requests ADD COLUMN IF NOT EXISTS id_review_status varchar(30) NOT NULL DEFAULT 'Pending';
                ALTER TABLE talent_verification_requests ADD COLUMN IF NOT EXISTS facial_review_status varchar(30) NOT NULL DEFAULT 'Pending';
                ALTER TABLE talent_verification_requests ADD COLUMN IF NOT EXISTS automated_status varchar(40) NULL;
                ALTER TABLE talent_verification_requests ADD COLUMN IF NOT EXISTS automated_recommendation varchar(60) NULL;
                ALTER TABLE talent_verification_requests ADD COLUMN IF NOT EXISTS automated_score integer NULL;
                ALTER TABLE talent_verification_requests ADD COLUMN IF NOT EXISTS automated_notes text NULL;
                ALTER TABLE talent_verification_requests ADD COLUMN IF NOT EXISTS automated_reviewed_at timestamptz NULL;";

            using var cmd = new NpgsqlCommand(sql, connection);
            await cmd.ExecuteNonQueryAsync();
        }

        public static string NormalizeListImage(string? imageUrl, string fallback = "", int maxDataUrlLength = DefaultListDataUrlLimit)
        {
            if (string.IsNullOrWhiteSpace(imageUrl))
            {
                return fallback;
            }

            var trimmed = imageUrl.Trim();
            if (trimmed.StartsWith("data:image/", StringComparison.OrdinalIgnoreCase) &&
                trimmed.Length > maxDataUrlLength)
            {
                return fallback;
            }

            return trimmed;
        }

        public static string BuildDisplayName(string? firstName, string? lastName, string? stageName, string? productionName, string role)
        {
            if (role.Equals("Organizer", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(productionName))
            {
                return productionName.Trim();
            }

            if ((role.Equals("Artist", StringComparison.OrdinalIgnoreCase) || role.Equals("Sessionist", StringComparison.OrdinalIgnoreCase))
                && !string.IsNullOrWhiteSpace(stageName))
            {
                return stageName.Trim();
            }

            var fullName = $"{firstName ?? ""} {lastName ?? ""}".Trim();
            return string.IsNullOrWhiteSpace(fullName) ? role : fullName;
        }

        public static ProfileCompletionResult CalculateProfileCompletion(
            string role,
            string? firstName,
            string? lastName,
            string? bio,
            string? profilePicture,
            string? genres,
            string? spotifyLink,
            string? productionName,
            string? contactNumber,
            string? address,
            string? stageName)
        {
            var checks = new List<bool>();
            var hasIdentity = !string.IsNullOrWhiteSpace(BuildDisplayName(firstName, lastName, stageName, productionName, role));
            var hasBio = !string.IsNullOrWhiteSpace(bio) && !bio.Contains("hasn't written", StringComparison.OrdinalIgnoreCase);
            var hasPicture = !string.IsNullOrWhiteSpace(profilePicture);

            checks.Add(hasIdentity);
            checks.Add(hasBio);
            checks.Add(hasPicture);

            if (role.Equals("Artist", StringComparison.OrdinalIgnoreCase) || role.Equals("Sessionist", StringComparison.OrdinalIgnoreCase))
            {
                checks.Add(!string.IsNullOrWhiteSpace(genres) && !genres.Equals("Music", StringComparison.OrdinalIgnoreCase) && !genres.Equals("Sessionist", StringComparison.OrdinalIgnoreCase));
                checks.Add(!string.IsNullOrWhiteSpace(spotifyLink));
            }
            else if (role.Equals("Organizer", StringComparison.OrdinalIgnoreCase))
            {
                checks.Add(!string.IsNullOrWhiteSpace(productionName));
                checks.Add(!string.IsNullOrWhiteSpace(contactNumber));
                checks.Add(!string.IsNullOrWhiteSpace(address));
            }

            var completed = checks.Count(item => item);
            var percent = checks.Count == 0 ? 0 : (int)Math.Round(completed * 100d / checks.Count);
            var isVerified = completed == checks.Count && checks.Count > 0;
            var label = isVerified ? "Verified Profile" : percent >= 70 ? "Almost Verified" : "Profile Incomplete";
            return new ProfileCompletionResult(percent, isVerified, label);
        }

        public static async Task SyncProfileVerificationAsync(
            NpgsqlConnection connection,
            Guid userId,
            string role,
            string? firstName,
            string? lastName,
            string? bio,
            string? profilePicture,
            string? genres,
            string? spotifyLink,
            string? productionName,
            string? contactNumber,
            string? address,
            string? stageName)
        {
            await EnsureCommunitySchemaAsync(connection);
            var summary = CalculateProfileCompletion(role, firstName, lastName, bio, profilePicture, genres, spotifyLink, productionName, contactNumber, address, stageName);
            var verification = await GetTalentVerificationSnapshotAsync(connection, userId, role);

            var isTalentRole = role.Equals("Artist", StringComparison.OrdinalIgnoreCase) || role.Equals("Sessionist", StringComparison.OrdinalIgnoreCase);
            var isOrganizerRole = role.Equals("Organizer", StringComparison.OrdinalIgnoreCase);
            bool isVerified;
            if (isTalentRole)
            {
                // For Artists and Sessionists: verified badge is granted solely by admin ID verification approval.
                // Profile completion percentage does not affect this.
                isVerified = verification.HasApprovedRequest;
            }
            else if (isOrganizerRole)
            {
                isVerified = verification.HasApprovedRequest;
            }
            else
            {
                isVerified = summary.IsVerified;
            }

            const string sql = @"
                UPDATE users
                SET is_verified = @isVerified,
                    profile_completed_at = CASE WHEN @isVerified THEN COALESCE(profile_completed_at, NOW()) ELSE NULL END
                WHERE id = @id;";

            using var cmd = new NpgsqlCommand(sql, connection);
            cmd.Parameters.Add("@id", NpgsqlDbType.Uuid).Value = userId;
            cmd.Parameters.Add("@isVerified", NpgsqlDbType.Boolean).Value = isVerified;
            await cmd.ExecuteNonQueryAsync();
        }

        public static async Task SyncProfileVerificationFromDatabaseAsync(NpgsqlConnection connection, Guid userId, string role)
        {
            await EnsureCommunitySchemaAsync(connection);

            const string sql = @"
                SELECT
                    COALESCE(firstname, ''),
                    COALESCE(lastname, ''),
                    COALESCE(bio, ''),
                    COALESCE(profile_picture, ''),
                    COALESCE(genres, ''),
                    COALESCE(spotify_link, ''),
                    COALESCE(productionname, ''),
                    COALESCE(contactnumber, ''),
                    COALESCE(address, ''),
                    COALESCE(stagename, '')
                FROM users
                WHERE id = @id
                LIMIT 1;";

            await using var cmd = new NpgsqlCommand(sql, connection);
            cmd.Parameters.Add("@id", NpgsqlDbType.Uuid).Value = userId;
            await using var reader = await cmd.ExecuteReaderAsync();
            if (!await reader.ReadAsync())
            {
                return;
            }

            var firstName = reader.IsDBNull(0) ? "" : reader.GetString(0);
            var lastName = reader.IsDBNull(1) ? "" : reader.GetString(1);
            var bio = reader.IsDBNull(2) ? "" : reader.GetString(2);
            var profilePicture = reader.IsDBNull(3) ? "" : reader.GetString(3);
            var genres = reader.IsDBNull(4) ? "" : reader.GetString(4);
            var spotifyLink = reader.IsDBNull(5) ? "" : reader.GetString(5);
            var productionName = reader.IsDBNull(6) ? "" : reader.GetString(6);
            var contactNumber = reader.IsDBNull(7) ? "" : reader.GetString(7);
            var address = reader.IsDBNull(8) ? "" : reader.GetString(8);
            var stageName = reader.IsDBNull(9) ? "" : reader.GetString(9);
            await reader.CloseAsync();

            await SyncProfileVerificationAsync(
                connection,
                userId,
                role,
                firstName,
                lastName,
                bio,
                profilePicture,
                genres,
                spotifyLink,
                productionName,
                contactNumber,
                address,
                stageName);
        }

        public static async Task<TalentVerificationSnapshot> GetTalentVerificationSnapshotAsync(NpgsqlConnection connection, Guid userId, string role)
        {
            await EnsureCommunitySchemaAsync(connection);

            if (!role.Equals("Artist", StringComparison.OrdinalIgnoreCase) &&
                !role.Equals("Sessionist", StringComparison.OrdinalIgnoreCase) &&
                !role.Equals("Organizer", StringComparison.OrdinalIgnoreCase))
            {
                return new TalentVerificationSnapshot("", "", "", "", null, null, false);
            }

            const string sql = @"
                SELECT COALESCE(verification_status, 'Not Submitted'),
                       COALESCE(verification_level, ''),
                       COALESCE(verification_method, ''),
                       COALESCE(verification_notes, ''),
                       verification_last_submitted_at,
                       verification_reviewed_at
                FROM users
                WHERE id = @id;";

            await using var cmd = new NpgsqlCommand(sql, connection);
            cmd.Parameters.Add("@id", NpgsqlDbType.Uuid).Value = userId;
            await using var reader = await cmd.ExecuteReaderAsync();
            if (!await reader.ReadAsync())
            {
                return new TalentVerificationSnapshot("Not Submitted", "", "", "", null, null, false);
            }

            var status = reader.IsDBNull(0) ? "Not Submitted" : reader.GetString(0);
            var level = reader.IsDBNull(1) ? "" : reader.GetString(1);
            var method = reader.IsDBNull(2) ? "" : reader.GetString(2);
            var notes = reader.IsDBNull(3) ? "" : reader.GetString(3);
            var submittedAt = reader.IsDBNull(4) ? (DateTime?)null : reader.GetDateTime(4);
            var reviewedAt = reader.IsDBNull(5) ? (DateTime?)null : reader.GetDateTime(5);
            await reader.CloseAsync();

            var hasApprovedRequest = status.Equals("Approved", StringComparison.OrdinalIgnoreCase);
            return new TalentVerificationSnapshot(status, level, method, notes, submittedAt, reviewedAt, hasApprovedRequest);
        }

        public static async Task<bool> IsIdentityApprovedAsync(NpgsqlConnection connection, Guid userId, string role)
        {
            var snapshot = await GetTalentVerificationSnapshotAsync(connection, userId, role);
            return snapshot.HasApprovedRequest;
        }

        private static async Task<bool> HasVerifiedGigAsync(NpgsqlConnection connection, Guid userId)
        {
            const string sql = @"
                SELECT EXISTS (
                    SELECT 1
                    FROM verified_gigs
                    WHERE user_id = @userId
                );";

            await using var cmd = new NpgsqlCommand(sql, connection);
            cmd.Parameters.Add("@userId", NpgsqlDbType.Uuid).Value = userId;
            var result = await cmd.ExecuteScalarAsync();
            return result is bool value && value;
        }
    }
}
