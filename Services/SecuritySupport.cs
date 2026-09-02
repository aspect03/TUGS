using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Http;
using Npgsql;
using NpgsqlTypes;

namespace ImajinationAPI.Services
{
    public static class SecuritySupport
    {
        private static readonly Regex HtmlTagRegex = new("<.*?>", RegexOptions.Compiled | RegexOptions.Singleline);
        private static readonly Regex ScriptRegex = new(@"<script\b[^<]*(?:(?!<\/script>)<[^<]*)*<\/script>", RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.Singleline);
        private static readonly Regex MultiWhitespaceRegex = new(@"\s{2,}", RegexOptions.Compiled);
        private static readonly HashSet<string> AllowedImageMimeTypes = new(StringComparer.OrdinalIgnoreCase)
        {
            "image/jpeg",
            "image/png",
            "image/webp",
            "image/gif"
        };

        private static volatile bool _schemaEnsured = false;
        private static readonly SemaphoreSlim _schemaSemaphore = new(1, 1);

        public static async Task EnsureSecuritySchemaAsync(NpgsqlConnection connection)
        {
            if (_schemaEnsured) return;

            await _schemaSemaphore.WaitAsync();
            try
            {
                if (_schemaEnsured) return;
            const string sql = @"
                CREATE TABLE IF NOT EXISTS auth_security_state (
                    email text PRIMARY KEY,
                    failed_attempts integer NOT NULL DEFAULT 0,
                    lockout_until timestamptz NULL,
                    last_failed_at timestamptz NULL,
                    otp_last_sent_at timestamptz NULL,
                    otp_window_started_at timestamptz NULL,
                    otp_requests_in_window integer NOT NULL DEFAULT 0,
                    updated_at timestamptz NOT NULL DEFAULT NOW()
                );

                CREATE TABLE IF NOT EXISTS security_audit_logs (
                    id uuid PRIMARY KEY,
                    user_id uuid NULL,
                    actor_role varchar(50) NULL,
                    action_type varchar(80) NOT NULL,
                    target_type varchar(60) NULL,
                    target_id uuid NULL,
                    ip_address text NULL,
                    user_agent text NULL,
                    details text NULL,
                    created_at timestamptz NOT NULL DEFAULT NOW()
                );

                CREATE INDEX IF NOT EXISTS idx_security_audit_logs_created_at
                    ON security_audit_logs(created_at DESC);

                CREATE INDEX IF NOT EXISTS idx_security_audit_logs_action_type
                    ON security_audit_logs(action_type);

                CREATE INDEX IF NOT EXISTS idx_security_audit_logs_user_id
                    ON security_audit_logs(user_id);

                CREATE TABLE IF NOT EXISTS user_active_sessions (
                    id uuid PRIMARY KEY,
                    user_id uuid NOT NULL,
                    session_token text NOT NULL UNIQUE,
                    actor_role varchar(50) NULL,
                    ip_address text NULL,
                    user_agent text NULL,
                    created_at timestamptz NOT NULL DEFAULT NOW(),
                    last_seen_at timestamptz NOT NULL DEFAULT NOW(),
                    revoked_at timestamptz NULL,
                    revoked_reason text NULL
                );
                ALTER TABLE user_active_sessions ADD COLUMN IF NOT EXISTS refresh_token_hash text NULL;
                ALTER TABLE user_active_sessions ADD COLUMN IF NOT EXISTS refresh_expires_at timestamptz NULL;

                CREATE INDEX IF NOT EXISTS idx_user_active_sessions_user_id
                    ON user_active_sessions(user_id);

                CREATE INDEX IF NOT EXISTS idx_user_active_sessions_last_seen
                    ON user_active_sessions(last_seen_at DESC);

                CREATE INDEX IF NOT EXISTS idx_user_active_sessions_refresh_token_hash
                    ON user_active_sessions(refresh_token_hash);
            ";

                await using var cmd = new NpgsqlCommand(sql, connection);
                await cmd.ExecuteNonQueryAsync();
                _schemaEnsured = true;
            }
            finally
            {
                _schemaSemaphore.Release();
            }
        }

        public static string SanitizePlainText(string? value, int maxLength = 4000, bool allowLineBreaks = true)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return string.Empty;
            }

            var normalized = value.Replace("\r\n", "\n").Replace('\r', '\n');
            normalized = ScriptRegex.Replace(normalized, string.Empty);
            normalized = HtmlTagRegex.Replace(normalized, string.Empty);
            normalized = WebUtility.HtmlDecode(normalized);

            var builder = new StringBuilder(normalized.Length);
            foreach (var ch in normalized)
            {
                if (char.IsControl(ch) && ch != '\n' && ch != '\t')
                {
                    continue;
                }

                if (!allowLineBreaks && ch == '\n')
                {
                    builder.Append(' ');
                    continue;
                }

                builder.Append(ch);
            }

            normalized = MultiWhitespaceRegex.Replace(builder.ToString().Trim(), " ");
            if (allowLineBreaks)
            {
                normalized = normalized.Replace(" \n", "\n").Replace("\n ", "\n");
            }

            if (normalized.Length > maxLength)
            {
                normalized = normalized[..maxLength];
            }

            return normalized.Trim();
        }

        public static string SanitizeUrl(string? value, int maxLength = 2048)
        {
            var sanitized = SanitizePlainText(value, maxLength, allowLineBreaks: false);
            if (string.IsNullOrWhiteSpace(sanitized))
            {
                return string.Empty;
            }

            if (Uri.TryCreate(sanitized, UriKind.Absolute, out var uri) &&
                (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps))
            {
                return uri.ToString();
            }

            return string.Empty;
        }

        public static string? ValidateAndNormalizeImageDataUrl(string? dataUrl, int maxBytes, out string? errorMessage)
        {
            errorMessage = null;
            if (string.IsNullOrWhiteSpace(dataUrl))
            {
                return null;
            }

            if (!dataUrl.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
            {
                errorMessage = "Only data URL uploads are supported.";
                return null;
            }

            var commaIndex = dataUrl.IndexOf(',');
            if (commaIndex <= 5)
            {
                errorMessage = "Invalid image payload.";
                return null;
            }

            var meta = dataUrl[..commaIndex];
            var payload = dataUrl[(commaIndex + 1)..];
            if (!meta.Contains(";base64", StringComparison.OrdinalIgnoreCase))
            {
                errorMessage = "Image uploads must be base64 encoded.";
                return null;
            }

            var mime = meta[5..].Split(';', StringSplitOptions.RemoveEmptyEntries)[0].Trim();
            if (!AllowedImageMimeTypes.Contains(mime))
            {
                errorMessage = "Unsupported image format. Use JPG, PNG, WEBP, or GIF.";
                return null;
            }

            try
            {
                var bytes = Convert.FromBase64String(payload);
                if (bytes.Length > maxBytes)
                {
                    errorMessage = $"Image is too large. Keep it under {Math.Round(maxBytes / 1024d / 1024d, 1)} MB.";
                    return null;
                }
            }
            catch
            {
                errorMessage = "Invalid image upload.";
                return null;
            }

            return dataUrl.Trim();
        }

        public static string ProtectSensitiveData(string? plaintext, string secret)
        {
            if (string.IsNullOrWhiteSpace(plaintext))
            {
                return string.Empty;
            }

            if (plaintext.StartsWith("enc:", StringComparison.OrdinalIgnoreCase))
            {
                return plaintext;
            }

            var cleanSecret = string.IsNullOrWhiteSpace(secret) ? "imajination-fallback-secret" : secret.Trim();
            using var aes = Aes.Create();
            aes.Key = SHA256.HashData(Encoding.UTF8.GetBytes(cleanSecret));
            aes.Mode = CipherMode.CBC;
            aes.Padding = PaddingMode.PKCS7;
            aes.GenerateIV();

            var plainBytes = Encoding.UTF8.GetBytes(plaintext);
            using var encryptor = aes.CreateEncryptor(aes.Key, aes.IV);
            var cipherBytes = encryptor.TransformFinalBlock(plainBytes, 0, plainBytes.Length);

            var payload = new byte[aes.IV.Length + cipherBytes.Length];
            Buffer.BlockCopy(aes.IV, 0, payload, 0, aes.IV.Length);
            Buffer.BlockCopy(cipherBytes, 0, payload, aes.IV.Length, cipherBytes.Length);
            return "enc:" + Convert.ToBase64String(payload);
        }

        public static string RevealSensitiveData(string? ciphertext, string secret)
        {
            if (string.IsNullOrWhiteSpace(ciphertext))
            {
                return string.Empty;
            }

            if (!ciphertext.StartsWith("enc:", StringComparison.OrdinalIgnoreCase))
            {
                return ciphertext;
            }

            try
            {
                var cleanSecret = string.IsNullOrWhiteSpace(secret) ? "imajination-fallback-secret" : secret.Trim();
                var payload = Convert.FromBase64String(ciphertext[4..]);
                if (payload.Length <= 16)
                {
                    return string.Empty;
                }

                var iv = payload[..16];
                var cipherBytes = payload[16..];

                using var aes = Aes.Create();
                aes.Key = SHA256.HashData(Encoding.UTF8.GetBytes(cleanSecret));
                aes.IV = iv;
                aes.Mode = CipherMode.CBC;
                aes.Padding = PaddingMode.PKCS7;

                using var decryptor = aes.CreateDecryptor(aes.Key, aes.IV);
                var plainBytes = decryptor.TransformFinalBlock(cipherBytes, 0, cipherBytes.Length);
                return Encoding.UTF8.GetString(plainBytes);
            }
            catch
            {
                return string.Empty;
            }
        }

        public static async Task<(bool IsLocked, int RetryAfterSeconds, int FailedAttempts)> GetLockoutStateAsync(
            NpgsqlConnection connection,
            string email)
        {
            const string sql = @"
                SELECT failed_attempts, lockout_until
                FROM auth_security_state
                WHERE email = @email
                LIMIT 1;";

            await using var cmd = new NpgsqlCommand(sql, connection);
            cmd.Parameters.Add("@email", NpgsqlDbType.Text).Value = email;
            await using var reader = await cmd.ExecuteReaderAsync();
            if (!await reader.ReadAsync())
            {
                return (false, 0, 0);
            }

            var failedAttempts = reader.IsDBNull(0) ? 0 : reader.GetInt32(0);
            var lockoutUntil = reader.IsDBNull(1) ? (DateTime?)null : reader.GetDateTime(1);
            if (lockoutUntil.HasValue && lockoutUntil.Value > DateTime.UtcNow)
            {
                var retryAfter = Math.Max(1, (int)Math.Ceiling((lockoutUntil.Value - DateTime.UtcNow).TotalSeconds));
                return (true, retryAfter, failedAttempts);
            }

            return (false, 0, failedAttempts);
        }

        public static async Task<int> RecordFailedLoginAsync(
            NpgsqlConnection connection,
            string email,
            int lockoutThreshold = 5,
            int lockoutMinutes = 15)
        {
            const string sql = @"
                INSERT INTO auth_security_state (email, failed_attempts, last_failed_at, updated_at)
                VALUES (@email, 1, NOW(), NOW())
                ON CONFLICT (email)
                DO UPDATE
                SET failed_attempts = auth_security_state.failed_attempts + 1,
                    last_failed_at = NOW(),
                    updated_at = NOW()
                RETURNING failed_attempts;";

            int failedAttempts;
            await using (var cmd = new NpgsqlCommand(sql, connection))
            {
                cmd.Parameters.Add("@email", NpgsqlDbType.Text).Value = email;
                failedAttempts = Convert.ToInt32(await cmd.ExecuteScalarAsync() ?? 1);
            }

            if (failedAttempts >= lockoutThreshold)
            {
                const string lockSql = @"
                    UPDATE auth_security_state
                    SET lockout_until = NOW() + (@lockoutMinutes || ' minutes')::interval,
                        updated_at = NOW()
                    WHERE email = @email;";
                await using var lockCmd = new NpgsqlCommand(lockSql, connection);
                lockCmd.Parameters.Add("@lockoutMinutes", NpgsqlDbType.Integer).Value = lockoutMinutes;
                lockCmd.Parameters.Add("@email", NpgsqlDbType.Text).Value = email;
                await lockCmd.ExecuteNonQueryAsync();
            }

            return failedAttempts;
        }

        public static async Task ClearFailedLoginAsync(NpgsqlConnection connection, string email)
        {
            const string sql = @"
                INSERT INTO auth_security_state (email, failed_attempts, lockout_until, updated_at)
                VALUES (@email, 0, NULL, NOW())
                ON CONFLICT (email)
                DO UPDATE
                SET failed_attempts = 0,
                    lockout_until = NULL,
                    updated_at = NOW();";

            await using var cmd = new NpgsqlCommand(sql, connection);
            cmd.Parameters.Add("@email", NpgsqlDbType.Text).Value = email;
            await cmd.ExecuteNonQueryAsync();
        }

        public static async Task<(bool Allowed, int RetryAfterSeconds, int RemainingInWindow)> CheckOtpSendAllowanceAsync(
            NpgsqlConnection connection,
            string email,
            int cooldownSeconds = 60,
            int maxPerWindow = 5,
            int windowMinutes = 15)
        {
            const string sql = @"
                SELECT otp_last_sent_at, otp_window_started_at, otp_requests_in_window
                FROM auth_security_state
                WHERE email = @email
                LIMIT 1;";

            DateTime? lastSentAt = null;
            DateTime? windowStartedAt = null;
            var requestCount = 0;

            await using (var cmd = new NpgsqlCommand(sql, connection))
            {
                cmd.Parameters.Add("@email", NpgsqlDbType.Text).Value = email;
                await using var reader = await cmd.ExecuteReaderAsync();
                if (await reader.ReadAsync())
                {
                    lastSentAt = reader.IsDBNull(0) ? (DateTime?)null : reader.GetDateTime(0);
                    windowStartedAt = reader.IsDBNull(1) ? (DateTime?)null : reader.GetDateTime(1);
                    requestCount = reader.IsDBNull(2) ? 0 : reader.GetInt32(2);
                }
            }

            var now = DateTime.UtcNow;
            if (lastSentAt.HasValue && lastSentAt.Value > now.AddSeconds(-cooldownSeconds))
            {
                var retryAfter = Math.Max(1, cooldownSeconds - (int)Math.Floor((now - lastSentAt.Value).TotalSeconds));
                return (false, retryAfter, Math.Max(0, maxPerWindow - requestCount));
            }

            if (!windowStartedAt.HasValue || windowStartedAt.Value <= now.AddMinutes(-windowMinutes))
            {
                return (true, 0, maxPerWindow);
            }

            if (requestCount >= maxPerWindow)
            {
                var retryAfter = Math.Max(1, (int)Math.Ceiling((windowStartedAt.Value.AddMinutes(windowMinutes) - now).TotalSeconds));
                return (false, retryAfter, 0);
            }

            return (true, 0, Math.Max(0, maxPerWindow - requestCount));
        }

        public static async Task MarkOtpSentAsync(NpgsqlConnection connection, string email)
        {
            const string sql = @"
                INSERT INTO auth_security_state (email, otp_last_sent_at, otp_window_started_at, otp_requests_in_window, updated_at)
                VALUES (@email, NOW(), NOW(), 1, NOW())
                ON CONFLICT (email)
                DO UPDATE
                SET otp_last_sent_at = NOW(),
                    otp_window_started_at = CASE
                        WHEN auth_security_state.otp_window_started_at IS NULL
                             OR auth_security_state.otp_window_started_at <= NOW() - INTERVAL '15 minutes'
                        THEN NOW()
                        ELSE auth_security_state.otp_window_started_at
                    END,
                    otp_requests_in_window = CASE
                        WHEN auth_security_state.otp_window_started_at IS NULL
                             OR auth_security_state.otp_window_started_at <= NOW() - INTERVAL '15 minutes'
                        THEN 1
                        ELSE auth_security_state.otp_requests_in_window + 1
                    END,
                    updated_at = NOW();";

            await using var cmd = new NpgsqlCommand(sql, connection);
            cmd.Parameters.Add("@email", NpgsqlDbType.Text).Value = email;
            await cmd.ExecuteNonQueryAsync();
        }

        public static async Task LogSecurityEventAsync(
            NpgsqlConnection connection,
            Guid? userId,
            string? actorRole,
            string actionType,
            string? targetType,
            Guid? targetId,
            HttpContext? context,
            string? details)
        {
            await EnsureSecuritySchemaAsync(connection);

            const string sql = @"
                INSERT INTO security_audit_logs (
                    id, user_id, actor_role, action_type, target_type, target_id, ip_address, user_agent, details, created_at
                )
                VALUES (
                    @id, @userId, @actorRole, @actionType, @targetType, @targetId, @ipAddress, @userAgent, @details, NOW()
                );";

            await using var cmd = new NpgsqlCommand(sql, connection);
            cmd.Parameters.Add("@id", NpgsqlDbType.Uuid).Value = Guid.NewGuid();
            cmd.Parameters.Add("@userId", NpgsqlDbType.Uuid).Value = (object?)userId ?? DBNull.Value;
            cmd.Parameters.Add("@actorRole", NpgsqlDbType.Text).Value = (object?)actorRole ?? DBNull.Value;
            cmd.Parameters.Add("@actionType", NpgsqlDbType.Text).Value = actionType;
            cmd.Parameters.Add("@targetType", NpgsqlDbType.Text).Value = (object?)targetType ?? DBNull.Value;
            cmd.Parameters.Add("@targetId", NpgsqlDbType.Uuid).Value = (object?)targetId ?? DBNull.Value;
            cmd.Parameters.Add("@ipAddress", NpgsqlDbType.Text).Value = (object?)GetClientIpAddress(context) ?? DBNull.Value;
            cmd.Parameters.Add("@userAgent", NpgsqlDbType.Text).Value = (object?)GetUserAgent(context) ?? DBNull.Value;
            cmd.Parameters.Add("@details", NpgsqlDbType.Text).Value = (object?)SanitizePlainText(details, 2000) ?? DBNull.Value;
            await cmd.ExecuteNonQueryAsync();
        }

        public static string GenerateSecureToken()
        {
            Span<byte> tokenBytes = stackalloc byte[32];
            RandomNumberGenerator.Fill(tokenBytes);
            return Convert.ToBase64String(tokenBytes)
                .Replace("/", "_", StringComparison.Ordinal)
                .Replace("+", "-", StringComparison.Ordinal)
                .TrimEnd('=');
        }

        public static string HashToken(string token)
        {
            var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(token));
            return Convert.ToHexString(bytes).ToLowerInvariant();
        }

        public static async Task<(string SessionToken, string RefreshToken, DateTime RefreshExpiresAt, int RevokedCount)> CreateTrackedSessionAsync(
            NpgsqlConnection connection,
            Guid userId,
            string? actorRole,
            HttpContext? context,
            bool revokeOtherSessions = true)
        {
            await EnsureSecuritySchemaAsync(connection);

            var revokedCount = 0;
            if (revokeOtherSessions)
            {
                const string revokeSql = @"
                    UPDATE user_active_sessions
                    SET revoked_at = NOW(),
                        revoked_reason = 'Signed in on another device'
                    WHERE user_id = @userId
                      AND revoked_at IS NULL;";

                await using var revokeCmd = new NpgsqlCommand(revokeSql, connection);
                revokeCmd.Parameters.Add("@userId", NpgsqlDbType.Uuid).Value = userId;
                revokedCount = await revokeCmd.ExecuteNonQueryAsync();
            }

            var sessionToken = GenerateSecureToken();
            var refreshToken = GenerateSecureToken();
            var refreshExpiresAt = DateTime.UtcNow.AddDays(14);

            const string insertSql = @"
                INSERT INTO user_active_sessions (
                    id, user_id, session_token, refresh_token_hash, refresh_expires_at, actor_role, ip_address, user_agent, created_at, last_seen_at
                )
                VALUES (
                    @id, @userId, @sessionToken, @refreshTokenHash, @refreshExpiresAt, @actorRole, @ipAddress, @userAgent, NOW(), NOW()
                );";

            await using (var insertCmd = new NpgsqlCommand(insertSql, connection))
            {
                insertCmd.Parameters.Add("@id", NpgsqlDbType.Uuid).Value = Guid.NewGuid();
                insertCmd.Parameters.Add("@userId", NpgsqlDbType.Uuid).Value = userId;
                insertCmd.Parameters.Add("@sessionToken", NpgsqlDbType.Text).Value = sessionToken;
                insertCmd.Parameters.Add("@refreshTokenHash", NpgsqlDbType.Text).Value = HashToken(refreshToken);
                insertCmd.Parameters.Add("@refreshExpiresAt", NpgsqlDbType.TimestampTz).Value = refreshExpiresAt;
                insertCmd.Parameters.Add("@actorRole", NpgsqlDbType.Text).Value = (object?)actorRole ?? DBNull.Value;
                insertCmd.Parameters.Add("@ipAddress", NpgsqlDbType.Text).Value = (object?)GetClientIpAddress(context) ?? DBNull.Value;
                insertCmd.Parameters.Add("@userAgent", NpgsqlDbType.Text).Value = (object?)GetUserAgent(context) ?? DBNull.Value;
                await insertCmd.ExecuteNonQueryAsync();
            }

            return (sessionToken, refreshToken, refreshExpiresAt, revokedCount);
        }

        public static async Task<(bool IsValid, bool ReplacedElsewhere, string? Message)> ValidateTrackedSessionAsync(
            NpgsqlConnection connection,
            Guid userId,
            string? sessionToken)
        {
            await EnsureSecuritySchemaAsync(connection);

            if (userId == Guid.Empty || string.IsNullOrWhiteSpace(sessionToken))
            {
                return (false, false, "Missing session details.");
            }

            const string sql = @"
                SELECT revoked_at, revoked_reason
                FROM user_active_sessions
                WHERE user_id = @userId
                  AND session_token = @sessionToken
                LIMIT 1;";

            await using var cmd = new NpgsqlCommand(sql, connection);
            cmd.Parameters.Add("@userId", NpgsqlDbType.Uuid).Value = userId;
            cmd.Parameters.Add("@sessionToken", NpgsqlDbType.Text).Value = sessionToken;

            await using var reader = await cmd.ExecuteReaderAsync();
            if (!await reader.ReadAsync())
            {
                return (false, false, "This session is no longer active.");
            }

            var revokedAt = reader.IsDBNull(0) ? (DateTime?)null : reader.GetDateTime(0);
            var revokedReason = reader.IsDBNull(1) ? string.Empty : reader.GetString(1);
            if (revokedAt.HasValue)
            {
                var replacedElsewhere = revokedReason.Contains("another device", StringComparison.OrdinalIgnoreCase);
                return (false, replacedElsewhere, string.IsNullOrWhiteSpace(revokedReason) ? "This session has ended." : revokedReason);
            }

            return (true, false, null);
        }

        public static async Task TouchTrackedSessionAsync(NpgsqlConnection connection, Guid userId, string? sessionToken)
        {
            await EnsureSecuritySchemaAsync(connection);
            if (userId == Guid.Empty || string.IsNullOrWhiteSpace(sessionToken))
            {
                return;
            }

            const string sql = @"
                UPDATE user_active_sessions
                SET last_seen_at = NOW()
                WHERE user_id = @userId
                  AND session_token = @sessionToken
                  AND revoked_at IS NULL;";

            await using var cmd = new NpgsqlCommand(sql, connection);
            cmd.Parameters.Add("@userId", NpgsqlDbType.Uuid).Value = userId;
            cmd.Parameters.Add("@sessionToken", NpgsqlDbType.Text).Value = sessionToken;
            await cmd.ExecuteNonQueryAsync();
        }

        public static async Task RevokeTrackedSessionAsync(
            NpgsqlConnection connection,
            Guid userId,
            string? sessionToken,
            string reason)
        {
            await EnsureSecuritySchemaAsync(connection);
            if (userId == Guid.Empty || string.IsNullOrWhiteSpace(sessionToken))
            {
                return;
            }

            const string sql = @"
                UPDATE user_active_sessions
                SET revoked_at = NOW(),
                    revoked_reason = @reason
                WHERE user_id = @userId
                  AND session_token = @sessionToken
                  AND revoked_at IS NULL;";

            await using var cmd = new NpgsqlCommand(sql, connection);
            cmd.Parameters.Add("@userId", NpgsqlDbType.Uuid).Value = userId;
            cmd.Parameters.Add("@sessionToken", NpgsqlDbType.Text).Value = sessionToken;
            cmd.Parameters.Add("@reason", NpgsqlDbType.Text).Value = SanitizePlainText(reason, 240, false);
            await cmd.ExecuteNonQueryAsync();
        }

        public static async Task<bool> UserMatchesRoleAsync(NpgsqlConnection connection, Guid userId, string expectedRole)
        {
            const string sql = "SELECT 1 FROM users WHERE id = @id AND LOWER(role) = LOWER(@role) LIMIT 1;";
            await using var cmd = new NpgsqlCommand(sql, connection);
            cmd.Parameters.Add("@id", NpgsqlDbType.Uuid).Value = userId;
            cmd.Parameters.Add("@role", NpgsqlDbType.Text).Value = expectedRole;
            var result = await cmd.ExecuteScalarAsync();
            return result is not null;
        }

        public static string? GetClientIpAddress(HttpContext? context)
            => context?.Connection?.RemoteIpAddress?.ToString();

        public static string? GetUserAgent(HttpContext? context)
            => context?.Request?.Headers?.UserAgent.ToString();
    }
}
