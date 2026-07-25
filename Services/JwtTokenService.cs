using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Hosting;
using Microsoft.IdentityModel.Tokens;

namespace ImajinationAPI.Services
{
    public sealed class JwtTokenService
    {
        private const int MinimumJwtSecretLength = 32;
        private readonly IConfiguration _configuration;
        private readonly byte[] _secretKeyBytes;
        private readonly string _issuer;
        private readonly string _audience;
        private readonly int _expiryMinutes;

        public JwtTokenService(IConfiguration configuration)
        {
            _configuration = configuration;
            _issuer = configuration["Auth:JwtIssuer"] ?? "Tugs!";
            _audience = configuration["Auth:JwtAudience"] ?? "IMAJINATION-CLIENT";
            _expiryMinutes = int.TryParse(configuration["Auth:JwtExpiryMinutes"], out var configuredExpiry)
                ? Math.Clamp(configuredExpiry, 5, 1440)
                : 120;

            var environmentName = configuration["ASPNETCORE_ENVIRONMENT"] ?? Environments.Production;
            var isDevelopment = string.Equals(environmentName, Environments.Development, StringComparison.OrdinalIgnoreCase);
            var configuredSecret = Environment.GetEnvironmentVariable("Auth__JwtSecret") ?? configuration["Auth:JwtSecret"];
            if (!string.IsNullOrWhiteSpace(configuredSecret))
            {
                if (configuredSecret.Length < MinimumJwtSecretLength)
                {
                    throw new InvalidOperationException(
                        $"Auth__JwtSecret must be at least {MinimumJwtSecretLength} characters long.");
                }

                _secretKeyBytes = Encoding.UTF8.GetBytes(configuredSecret);
                return;
            }

            if (!isDevelopment)
            {
                throw new InvalidOperationException(
                    "Auth__JwtSecret is required outside Development. Set a strong JWT secret before starting the server.");
            }

            var fallbackMaterial = configuration.GetConnectionString("DefaultConnection")
                ?? configuration["Supabase:ConnectionString"]
                ?? "imajination-development-fallback-secret";
            _secretKeyBytes = SHA256.HashData(Encoding.UTF8.GetBytes(fallbackMaterial));
        }

        public SymmetricSecurityKey GetSigningKey() => new(_secretKeyBytes);

        public string GenerateAccessToken(Guid userId, string role, string firstName, string username)
        {
            var claims = new List<Claim>
            {
                new(ClaimTypes.NameIdentifier, userId.ToString()),
                new(ClaimTypes.Role, role ?? string.Empty),
                new(ClaimTypes.Name, firstName ?? string.Empty),
                new("username", username ?? string.Empty)
            };

            var credentials = new SigningCredentials(GetSigningKey(), SecurityAlgorithms.HmacSha256);
            var now = DateTime.UtcNow;
            var token = new JwtSecurityToken(
                issuer: _issuer,
                audience: _audience,
                claims: claims,
                notBefore: now,
                expires: now.AddMinutes(_expiryMinutes),
                signingCredentials: credentials);

            return new JwtSecurityTokenHandler().WriteToken(token);
        }

        public TokenValidationParameters CreateValidationParameters()
        {
            return new TokenValidationParameters
            {
                ValidateIssuerSigningKey = true,
                IssuerSigningKey = GetSigningKey(),
                ValidateIssuer = true,
                ValidIssuer = _issuer,
                ValidateAudience = true,
                ValidAudience = _audience,
                ValidateLifetime = true,
                ClockSkew = TimeSpan.FromMinutes(2)
            };
        }
    }
}
