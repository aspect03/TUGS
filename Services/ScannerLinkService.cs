using System.Security.Cryptography;
using System.Text;
using ImajinationAPI.Controllers;
using Npgsql;

namespace ImajinationAPI.Services
{
    public sealed class ScannerLinkService
    {
        private readonly string _connectionString;

        public ScannerLinkService(IConfiguration configuration)
        {
            _connectionString = ConfigurationFallbacks.GetRequiredSupabaseConnectionString(configuration);
        }

        public static string CreateToken() => Convert.ToHexString(RandomNumberGenerator.GetBytes(32));

        public static string HashToken(string token) => Convert.ToHexString(
            SHA256.HashData(Encoding.UTF8.GetBytes(token.Trim())));

        public static async Task EnsureSchemaAsync(NpgsqlConnection connection)
        {
            const string sql = @"
                CREATE TABLE IF NOT EXISTS scanner_links (
                    id uuid PRIMARY KEY,
                    event_id uuid NOT NULL,
                    created_by uuid NOT NULL,
                    label varchar(120) NOT NULL,
                    token_hash varchar(128) NOT NULL UNIQUE,
                    expires_at timestamptz NOT NULL,
                    revoked_at timestamptz NULL,
                    created_at timestamptz NOT NULL DEFAULT NOW(),
                    last_used_at timestamptz NULL
                );
                CREATE INDEX IF NOT EXISTS idx_scanner_links_event ON scanner_links(event_id);
                CREATE TABLE IF NOT EXISTS scanner_sessions (
                    id uuid PRIMARY KEY,
                    scanner_link_id uuid NOT NULL,
                    session_hash varchar(128) NOT NULL UNIQUE,
                    expires_at timestamptz NOT NULL,
                    created_at timestamptz NOT NULL DEFAULT NOW(),
                    last_used_at timestamptz NULL
                );
                CREATE INDEX IF NOT EXISTS idx_scanner_sessions_link ON scanner_sessions(scanner_link_id);";
            await using var cmd = new NpgsqlCommand(sql, connection);
            await cmd.ExecuteNonQueryAsync();
        }

        public async Task<(Guid EventId, string EventTitle)?> ResolveSessionAsync(string? rawSession)
        {
            if (string.IsNullOrWhiteSpace(rawSession)) return null;
            await using var connection = new NpgsqlConnection(_connectionString);
            await connection.OpenAsync();
            await EnsureSchemaAsync(connection);
            const string sql = @"
                SELECT sl.event_id, COALESCE(e.title, 'Selected Event')
                FROM scanner_sessions ss
                JOIN scanner_links sl ON sl.id = ss.scanner_link_id
                JOIN events e ON e.id = sl.event_id
                WHERE ss.session_hash = @hash
                  AND ss.expires_at > NOW()
                  AND sl.expires_at > NOW()
                  AND sl.revoked_at IS NULL
                LIMIT 1;";
            await using var cmd = new NpgsqlCommand(sql, connection);
            cmd.Parameters.AddWithValue("@hash", HashToken(rawSession));
            await using var reader = await cmd.ExecuteReaderAsync();
            if (!await reader.ReadAsync()) return null;
            return (reader.GetGuid(0), reader.IsDBNull(1) ? "Selected Event" : reader.GetString(1));
        }
    }
}
