using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Authorization;
using Npgsql;
using BCrypt.Net;
using ImajinationAPI.Models;
using Microsoft.Extensions.Caching.Memory;
using System.Net.Mail;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using System.Security.Claims;
using System.Security.Cryptography;
using ImajinationAPI.Services;

namespace ImajinationAPI.Controllers
{
    [Route("api/[controller]")]
    [ApiController]
    public class AuthController : ControllerBase
    {
        private readonly string _connectionString;
        private readonly IMemoryCache _cache;
        private readonly IConfiguration _config;
        private readonly JwtTokenService _jwtTokenService;
        private readonly TotpService _totpService;
        private readonly ImajinationAPI.Services.EmailService _emailService;
        private const string AccessTokenCookieName = "IMAJINATION-ACCESS";
        private const string SessionTokenCookieName = "IMAJINATION-SESSION";
        private const string RefreshTokenCookieName = "IMAJINATION-REFRESH";

        public AuthController(IConfiguration configuration, IMemoryCache cache, JwtTokenService jwtTokenService, TotpService totpService, ImajinationAPI.Services.EmailService emailService)
        {
            _config = configuration;
            _connectionString = ConfigurationFallbacks.GetRequiredSupabaseConnectionString(configuration);
            _cache = cache;
            _jwtTokenService = jwtTokenService;
            _totpService = totpService;
            _emailService = emailService;
        }

        private CookieOptions BuildAuthCookieOptions()
        {
            var secure = HttpContext.Request.IsHttps || !HttpContext.Request.Host.Host.Contains("localhost", StringComparison.OrdinalIgnoreCase);
            return new CookieOptions
            {
                HttpOnly = true,
                Secure = secure,
                SameSite = SameSiteMode.Lax,
                IsEssential = true,
                Path = "/"
            };
        }

        private void SetAuthCookies(Guid userId, string role, string firstName, string username, string sessionToken, string? refreshToken = null, DateTime? refreshExpiresAt = null)
        {
            var cookieOptions = BuildAuthCookieOptions();
            var accessToken = _jwtTokenService.GenerateAccessToken(userId, role, firstName, username);
            Response.Cookies.Append(AccessTokenCookieName, accessToken, cookieOptions);
            Response.Cookies.Append(SessionTokenCookieName, sessionToken, cookieOptions);
            if (!string.IsNullOrWhiteSpace(refreshToken))
            {
                var refreshOptions = BuildAuthCookieOptions();
                refreshOptions.Expires = refreshExpiresAt ?? DateTimeOffset.UtcNow.AddDays(14);
                Response.Cookies.Append(RefreshTokenCookieName, refreshToken, refreshOptions);
            }
        }

        private void ClearAuthCookies()
        {
            var cookieOptions = BuildAuthCookieOptions();
            Response.Cookies.Delete(AccessTokenCookieName, cookieOptions);
            Response.Cookies.Delete(SessionTokenCookieName, cookieOptions);
            Response.Cookies.Delete(RefreshTokenCookieName, cookieOptions);
        }

        // ==========================================
        // 1. SEND OTP EMAIL
        // ==========================================
        [HttpPost("send-otp")]
        public async Task<IActionResult> SendOtp([FromBody] SendOtpDto req)
        {
            var normalizedEmail = NormalizeEmail(req.email);
            var attemptId = Guid.NewGuid();
            try
            {
                if (string.IsNullOrWhiteSpace(normalizedEmail))
                {
                    return BadRequest(new { message = "Email is required." });
                }

                if (!IsValidEmailAddress(normalizedEmail))
                {
                    return BadRequest(new { message = "Enter a valid email address that can receive verification codes." });
                }

                await using var securityConnection = new NpgsqlConnection(_connectionString);
                await securityConnection.OpenAsync();
                await SecuritySupport.EnsureSecuritySchemaAsync(securityConnection);
                await using (var existingAccountCmd = new NpgsqlCommand(
                    "SELECT 1 FROM users WHERE LOWER(TRIM(email)) = @email LIMIT 1;", securityConnection))
                {
                    existingAccountCmd.Parameters.AddWithValue("@email", normalizedEmail);
                    if (await existingAccountCmd.ExecuteScalarAsync() is not null)
                    {
                        return Conflict(new { message = "An account already uses this email. Please sign in or reset your password." });
                    }
                }
                var otpAllowance = await SecuritySupport.CheckOtpSendAllowanceAsync(
                    securityConnection,
                    normalizedEmail,
                    cooldownSeconds: 30);
                if (!otpAllowance.Allowed)
                {
                    await SecuritySupport.LogSecurityEventAsync(
                        securityConnection,
                        null,
                        null,
                        "otp_request_throttled",
                        "otp",
                        null,
                        HttpContext,
                        $"OTP request blocked for {normalizedEmail}. Retry after {otpAllowance.RetryAfterSeconds}s.");

                    return StatusCode(StatusCodes.Status429TooManyRequests, new
                    {
                        message = otpAllowance.RemainingInWindow <= 0
                            ? "Too many OTP requests. Please wait before requesting another code."
                            : "Please wait a bit before requesting another OTP.",
                        retryAfterSeconds = otpAllowance.RetryAfterSeconds
                    });
                }

                string otpCode = RandomNumberGenerator.GetInt32(100000, 1_000_000).ToString();

                var senderEmail = ConfigurationFallbacks.GetRequiredSetting(
                    _config,
                    "EmailSettings:SenderEmail",
                    "EmailSettings__SenderEmail",
                    "Brevo sender email");
                var senderName = ConfigurationFallbacks.GetRequiredSetting(
                    _config,
                    "EmailSettings:SenderName",
                    "EmailSettings__SenderName",
                    "Brevo sender name");
                var smtpServer = ConfigurationFallbacks.GetRequiredSetting(
                    _config,
                    "EmailSettings:SmtpServer",
                    "EmailSettings__SmtpServer",
                    "Brevo SMTP server");
                var smtpPortRaw = ConfigurationFallbacks.GetRequiredSetting(
                    _config,
                    "EmailSettings:Port",
                    "EmailSettings__Port",
                    "Brevo SMTP port");
                var smtpUsername = ConfigurationFallbacks.GetRequiredSetting(
                    _config,
                    "EmailSettings:Username",
                    "EmailSettings__Username",
                    "Brevo SMTP username");
                var smtpPassword = ConfigurationFallbacks.GetRequiredSetting(
                    _config,
                    "EmailSettings:Password",
                    "EmailSettings__Password",
                    "Brevo SMTP password");

                if (!int.TryParse(smtpPortRaw, out var smtpPort))
                {
                    throw new InvalidOperationException("Brevo SMTP port is invalid. Set EmailSettings__Port to a valid integer.");
                }

                using var mail = new MailMessage();
                mail.From = new MailAddress(senderEmail, senderName);
                mail.To.Add(normalizedEmail);
                mail.Subject = "Your Imajination Verification Code";

                string emailDesign = $@"
                <!DOCTYPE html>
                <html>
                <head>
                    <meta charset='utf-8'>
                    <meta name='viewport' content='width=device-width, initial-scale=1.0'>
                    <link href='https://fonts.googleapis.com/css2?family=Montserrat:wght@400;600;800&display=swap' rel='stylesheet'>
                </head>
                <body style='margin: 0; padding: 0; background-color: #0a0a0a; font-family: ""Montserrat"", Arial, sans-serif; color: #ffffff;'>
                    <table width='100%' cellpadding='0' cellspacing='0' style='background-color: #0a0a0a; padding: 40px 20px;'>
                        <tr>
                            <td align='center'>
                                <table width='100%' max-width='600' cellpadding='0' cellspacing='0' style='max-width: 600px; background-color: #171717; border: 1px solid #262626; border-radius: 24px; overflow: hidden; box-shadow: 0 25px 50px -12px rgba(0, 0, 0, 0.5);'>
                                    <tr>
                                        <td align='center' style='padding: 30px; border-bottom: 1px solid #262626; background-color: #1f1f1f;'>
                                            <h1 style='margin: 0; font-size: 20px; font-weight: 800; letter-spacing: 2px; color: #ffffff; text-transform: uppercase;'>
                                                <span style='color: #dc2626;'>&bull;</span> IMAJINATION
                                            </h1>
                                        </td>
                                    </tr>
                                    <tr>
                                        <td align='center' style='padding: 40px 30px;'>
                                            <h2 style='margin: 0 0 15px 0; font-size: 24px; font-weight: 600; color: #ffffff;'>Verify your email</h2>
                                            <p style='margin: 0 0 30px 0; font-size: 14px; line-height: 24px; color: #a3a3a3;'>
                                                You are almost ready to start exploring indie gigs and booking tickets. Enter the code below to securely verify your account.
                                            </p>
                                            <div style='background-color: #2a0a0a; border: 1px solid #450a0a; border-radius: 12px; padding: 20px; margin-bottom: 30px;'>
                                                <div style='font-family: monospace; font-size: 36px; font-weight: 800; letter-spacing: 12px; color: #ef4444; margin-left: 12px;'>
                                                    {otpCode}
                                                </div>
                                            </div>
                                            <p style='margin: 0; font-size: 12px; font-weight: 600; color: #ef4444; text-transform: uppercase; letter-spacing: 1px;'>
                                                Expires in 5 minutes
                                            </p>
                                        </td>
                                    </tr>
                                    <tr>
                                        <td align='center' style='padding: 30px; background-color: #121212; border-top: 1px solid #262626;'>
                                            <p style='margin: 0; font-size: 12px; color: #525252;'>
                                                If you did not attempt to register for an Imajination account, you can safely ignore this email.
                                            </p>
                                            <p style='margin: 10px 0 0 0; font-size: 12px; color: #525252;'>
                                                &copy; {DateTime.Now.Year} Imajination. All rights reserved.
                                            </p>
                                        </td>
                                    </tr>
                                </table>
                            </td>
                        </tr>
                    </table>
                </body>
                </html>";

                mail.Body = emailDesign;
                mail.IsBodyHtml = true;

                using (var smtp = new SmtpClient(smtpServer, smtpPort))
                {
                    smtp.Credentials = new NetworkCredential(smtpUsername, smtpPassword);
                    smtp.EnableSsl = true;
                    smtp.Timeout = 12_000;
                    await smtp.SendMailAsync(mail).WaitAsync(TimeSpan.FromSeconds(12));
                }

                // Do not create a usable OTP until the SMTP provider has accepted the message.
                _cache.Set(normalizedEmail, otpCode, TimeSpan.FromMinutes(5));
                await SecuritySupport.MarkOtpSentAsync(securityConnection, normalizedEmail);
                await TryLogOtpEmailAttemptAsync(attemptId, normalizedEmail, senderEmail, "AcceptedBySmtp", "OTP email accepted by SMTP provider.");
                await SecuritySupport.LogSecurityEventAsync(
                    securityConnection,
                    null,
                    null,
                    "otp_sent",
                    "otp",
                    null,
                    HttpContext,
                    $"OTP sent to {normalizedEmail}.");

                return Ok(new { message = "Verification code accepted for delivery.", attemptId });
            }
            catch (SmtpException ex)
            {
                await TryLogOtpEmailAttemptAsync(
                    attemptId,
                    normalizedEmail,
                    ConfigurationFallbacks.GetSetting(_config, "EmailSettings:SenderEmail", "EmailSettings__SenderEmail") ?? string.Empty,
                    "SmtpRejected",
                    ex.Message);
                var providerHint = ex.Message.IndexOf("sender", StringComparison.OrdinalIgnoreCase) >= 0
                    ? "We could not send a verification email right now. Please try again later."
                    : "The email could not be delivered. Check the address and try again.";
                return StatusCode(500, new
                {
                    message = providerHint,
                    attemptId
                });
            }
            catch (Exception ex)
            {
                await TryLogOtpEmailAttemptAsync(
                    attemptId,
                    normalizedEmail,
                    ConfigurationFallbacks.GetSetting(_config, "EmailSettings:SenderEmail", "EmailSettings__SenderEmail") ?? string.Empty,
                    "ServerError",
                    ex.Message);
                return StatusCode(500, new
                {
                    message = ConfigurationFallbacks.BuildSafeErrorMessage(
                        _config,
                        "Failed to send email right now. Please try again in a moment.",
                        ex),
                    attemptId
                });
            }
        }

        // ==========================================
        // 2. REGISTER ACCOUNT
        // ==========================================
        [HttpPost("register")]
        public async Task<IActionResult> Register([FromBody] RegisterDto req)
        {
            var validationError = RegistrationValidation.Validate(req);
            if (validationError is not null) return BadRequest(new { message = validationError });
            var normalizedEmail = NormalizeEmail(req.email);
            if (string.IsNullOrWhiteSpace(normalizedEmail))
            {
                return BadRequest(new { message = "Email is required." });
            }

            if (!IsStrongPassword(req.password))
            {
                return BadRequest(new
                {
                    message = "Password must be at least 8 characters and include uppercase, lowercase, number, and special character."
                });
            }

            if (!req.termsAccepted || string.IsNullOrWhiteSpace(req.termsVersion))
            {
                return BadRequest(new { message = "You must read and accept the current Terms and Conditions." });
            }

            if (!_cache.TryGetValue(normalizedEmail, out string savedOtp))
            {
                return BadRequest(new { message = "OTP expired or not requested. Please request a new code." });
            }

            if (savedOtp != req.otp)
            {
                return BadRequest(new { message = "Invalid OTP code." });
            }

            // Birthdate is collected later in profile verification. Never invent an age.
            int? computedAge = null;
            if (req.birthday.HasValue)
            {
                if (req.birthday.Value == default || req.birthday.Value.Date > DateTime.UtcNow.Date)
                    return BadRequest(new { message = "Enter a valid birthday." });
                var today = DateTime.UtcNow.Date;
                var birthdayDate = req.birthday.Value.Date;
                computedAge = today.Year - birthdayDate.Year;
                if (birthdayDate > today.AddYears(-computedAge.Value)) computedAge--;
                if (computedAge < 13)
                    return BadRequest(new { message = "You must be at least 13 years old to register." });
            }

            try
            {
                string passwordHash = BCrypt.Net.BCrypt.HashPassword(req.password);
                
                await using var connection = new NpgsqlConnection(_connectionString);
                await connection.OpenAsync();
                await EnsureTalentRegistrationColumnsAsync(connection);
                await EnsureUserModerationColumnsAsync(connection);
                await EnsureMfaColumnsAsync(connection);
                await SecuritySupport.EnsureSecuritySchemaAsync(connection);

                var normalizedRole = NormalizeRegistrationRole(req.role);
                if (normalizedRole is null)
                {
                    return BadRequest(new
                    {
                        message = "Invalid role. Allowed roles are Customer and Artist."
                    });
                }

                var accountStatus = normalizedRole == "Customer" && computedAge < 18
                    ? "MinorPendingReview"
                    : "Active";

                string sql = @"
                    INSERT INTO users
                    (role, firstname, middlename, lastname, suffix, username, email, contactnumber, birthday, age, passwordhash, stagename, productionname, talent_category, member_names, is_banned, account_status)
                    VALUES
                    (@r, @fn, @mn, @ln, @sx, @un, @em, @cn, @bd, @ag, @ph, @sn, @pn, @tc, @members, FALSE, @accountStatus)";

                using var cmd = new NpgsqlCommand(sql, connection);
                cmd.Parameters.AddWithValue("@r", normalizedRole ?? (object)DBNull.Value);
                cmd.Parameters.AddWithValue("@fn", SecuritySupport.SanitizePlainText(req.firstName, 120, false) ?? (object)DBNull.Value);
                cmd.Parameters.AddWithValue("@mn", (object?)SecuritySupport.SanitizePlainText(req.middleName, 120, false) ?? DBNull.Value);
                cmd.Parameters.AddWithValue("@ln", SecuritySupport.SanitizePlainText(req.lastName, 120, false) ?? (object)DBNull.Value);
                cmd.Parameters.AddWithValue("@sx", (object?)SecuritySupport.SanitizePlainText(req.suffix, 40, false) ?? DBNull.Value);
                cmd.Parameters.AddWithValue("@un", SecuritySupport.SanitizePlainText(req.username, 60, false) ?? (object)DBNull.Value);
                cmd.Parameters.AddWithValue("@em", normalizedEmail);
                cmd.Parameters.AddWithValue("@cn", SecuritySupport.SanitizePlainText(req.contactNumber, 60, false) ?? (object)DBNull.Value);
                cmd.Parameters.Add("@bd", NpgsqlTypes.NpgsqlDbType.Date).Value = (object?)req.birthday?.Date ?? DBNull.Value;
                cmd.Parameters.Add("@ag", NpgsqlTypes.NpgsqlDbType.Integer).Value = (object?)computedAge ?? DBNull.Value;
                cmd.Parameters.AddWithValue("@ph", passwordHash);
                cmd.Parameters.AddWithValue("@sn", (object?)SecuritySupport.SanitizePlainText(req.stageName, 120, false) ?? DBNull.Value);
                cmd.Parameters.AddWithValue("@pn", (object?)SecuritySupport.SanitizePlainText(req.productionName, 160, false) ?? DBNull.Value);
                cmd.Parameters.AddWithValue("@tc", (object?)SecuritySupport.SanitizePlainText(req.talentCategory, 60, false) ?? DBNull.Value);
                cmd.Parameters.AddWithValue("@members", (object?)SecuritySupport.SanitizePlainText(req.memberNames, 600, true) ?? DBNull.Value);
                cmd.Parameters.AddWithValue("@accountStatus", accountStatus);

                await cmd.ExecuteNonQueryAsync();
                await EnsureTermsAcceptanceTableAsync(connection);
                await using (var consentCmd = new NpgsqlCommand(@"
                    INSERT INTO terms_acceptances (id, user_email, role, terms_version, accepted_at)
                    VALUES (@id, @email, @role, @version, NOW());", connection))
                {
                    consentCmd.Parameters.AddWithValue("@id", Guid.NewGuid());
                    consentCmd.Parameters.AddWithValue("@email", normalizedEmail);
                    consentCmd.Parameters.AddWithValue("@role", normalizedRole);
                    consentCmd.Parameters.AddWithValue("@version", SecuritySupport.SanitizePlainText(req.termsVersion, 80, false) ?? "current");
                    await consentCmd.ExecuteNonQueryAsync();
                }
                await SecuritySupport.LogSecurityEventAsync(
                    connection,
                    null,
                    normalizedRole,
                    "account_registered",
                    "user",
                    null,
                    HttpContext,
                    $"{normalizedRole} registration submitted for {normalizedEmail} with status {accountStatus}.");

                _cache.Remove(normalizedEmail);

                // Fire-and-forget welcome email
                var firstName = SecuritySupport.SanitizePlainText(req.firstName, 120, false) ?? "";
                _ = _emailService.SendWelcomeEmailAsync(normalizedEmail, firstName, normalizedRole ?? "Customer");

                return Ok(new
                {
                    message = "Registration successful.",
                    accountStatus
                });
            }
            catch (PostgresException ex) when (ex.SqlState == "23505")
            {
                return Conflict(new { message = "Email or Username already exists." });
            }
            catch (NpgsqlException ex) when (ex.InnerException is SocketException socketEx
                && socketEx.SocketErrorCode == SocketError.HostNotFound)
            {
                return StatusCode(StatusCodes.Status503ServiceUnavailable, new
                {
                    message = ConfigurationFallbacks.BuildSafeErrorMessage(
                        _config,
                        "Google sign-in reached the server, but the configured database host could not be resolved. Check ConnectionStrings__SupabaseConnection and verify the Supabase host name.",
                        ex)
                });
            }
            catch (Exception ex)
            {
                return StatusCode(500, new
                {
                    message = ConfigurationFallbacks.BuildSafeErrorMessage(
                        _config,
                        "Registration could not be completed right now.",
                        ex)
                });
            }
        }

        // ==========================================
        // 3. LOGIN ACCOUNT (CRITICAL FIX ADDED HERE)
        // ==========================================
        [HttpPost("login")]
        public async Task<IActionResult> Login([FromBody] LoginDto req)
        {
            try 
            {
                var normalizedEmail = NormalizeEmail(req.email);
                if (string.IsNullOrWhiteSpace(normalizedEmail))
                {
                    return BadRequest(new { message = "Email is required." });
                }

                await using var connection = new NpgsqlConnection(_connectionString);
                await connection.OpenAsync();
                await EnsureUserModerationColumnsAsync(connection);
                await EnsureMfaColumnsAsync(connection);
                await SecuritySupport.EnsureSecuritySchemaAsync(connection);

                var lockoutState = await SecuritySupport.GetLockoutStateAsync(connection, normalizedEmail);
                if (lockoutState.IsLocked)
                {
                    await SecuritySupport.LogSecurityEventAsync(
                        connection,
                        null,
                        null,
                        "login_blocked_lockout",
                        "user",
                        null,
                        HttpContext,
                        $"Locked-out login attempt for {normalizedEmail}.");

                    return StatusCode(StatusCodes.Status429TooManyRequests, new
                    {
                        message = "Too many failed login attempts. Please wait before trying again.",
                        retryAfterSeconds = lockoutState.RetryAfterSeconds
                    });
                }

                // Match email case-insensitively so normal login and Google login land on the same account.
                using var cmd = new NpgsqlCommand("SELECT id, passwordhash, role, firstname, username, profile_picture, COALESCE(is_banned, FALSE), COALESCE(account_status, 'Active'), COALESCE(mfa_enabled, FALSE) FROM users WHERE LOWER(TRIM(email)) = @email", connection);
                cmd.Parameters.AddWithValue("@email", normalizedEmail);

                Guid authenticatedUserId = Guid.Empty;
                string authenticatedRole = string.Empty;
                string authenticatedFirstName = string.Empty;
                string authenticatedUsername = string.Empty;
                string authenticatedProfilePicture = string.Empty;
                var authenticatedMfaEnabled = false;
                string? loginBlockMessage = null;
                int? loginBlockStatusCode = null;
                var authenticated = false;

                using (var reader = await cmd.ExecuteReaderAsync())
                {
                    if (await reader.ReadAsync())
                    {
                        Guid userId = reader.GetGuid(0);
                        string storedHash = reader.GetString(1);
                        bool isBanned = !reader.IsDBNull(6) && reader.GetBoolean(6);
                        string accountStatus = reader.IsDBNull(7) ? "Active" : reader.GetString(7);

                        if (isBanned || string.Equals(accountStatus, "Banned", StringComparison.OrdinalIgnoreCase))
                        {
                            loginBlockStatusCode = StatusCodes.Status403Forbidden;
                            loginBlockMessage = "This account has been blocked by admin.";
                        }
                        else if (string.Equals(accountStatus, "PendingApproval", StringComparison.OrdinalIgnoreCase))
                        {
                            loginBlockStatusCode = StatusCodes.Status403Forbidden;
                            loginBlockMessage = "This account is still waiting for admin approval.";
                        }
                        else if (string.Equals(accountStatus, "Denied", StringComparison.OrdinalIgnoreCase))
                        {
                            loginBlockStatusCode = StatusCodes.Status403Forbidden;
                            loginBlockMessage = "This account was denied by admin. Please contact support before trying again.";
                        }
                        else if (BCrypt.Net.BCrypt.Verify(req.password, storedHash))
                        {
                            authenticated = true;
                            authenticatedUserId = userId;
                            authenticatedRole = reader.GetString(2);
                            authenticatedFirstName = reader.GetString(3);
                            authenticatedUsername = reader.GetString(4);
                            authenticatedProfilePicture = reader.IsDBNull(5) ? "" : reader.GetString(5);
                            authenticatedMfaEnabled = !reader.IsDBNull(8) && reader.GetBoolean(8);
                        }
                    }
                }

                if (loginBlockStatusCode.HasValue)
                {
                    return StatusCode(loginBlockStatusCode.Value, new { message = loginBlockMessage });
                }

                if (authenticated)
                {
                    await SecuritySupport.ClearFailedLoginAsync(connection, normalizedEmail);
                    if (authenticatedMfaEnabled)
                    {
                        var mfaTicket = CreatePendingMfaTicket(authenticatedUserId, authenticatedRole, authenticatedFirstName, authenticatedUsername, authenticatedProfilePicture, normalizedEmail);
                        await SecuritySupport.LogSecurityEventAsync(
                            connection,
                            authenticatedUserId,
                            authenticatedRole,
                            "mfa_challenge_issued",
                            "user",
                            authenticatedUserId,
                            HttpContext,
                            $"Password accepted and MFA challenge issued for {normalizedEmail}.");

                        return Ok(new
                        {
                            mfaRequired = true,
                            mfaTicket,
                            id = authenticatedUserId,
                            role = authenticatedRole,
                            firstName = authenticatedFirstName,
                            username = authenticatedUsername,
                            profilePicture = authenticatedProfilePicture
                        });
                    }

                    var trackedSession = await SecuritySupport.CreateTrackedSessionAsync(
                        connection,
                        authenticatedUserId,
                        authenticatedRole,
                        HttpContext);
                    await SecuritySupport.LogSecurityEventAsync(
                        connection,
                        authenticatedUserId,
                        authenticatedRole,
                        "login_success",
                        "user",
                        authenticatedUserId,
                        HttpContext,
                        $"Successful login for {normalizedEmail}.");
                    SetAuthCookies(
                        authenticatedUserId,
                        authenticatedRole,
                        authenticatedFirstName,
                        authenticatedUsername,
                        trackedSession.SessionToken,
                        trackedSession.RefreshToken,
                        trackedSession.RefreshExpiresAt);

                    return Ok(new
                    {
                        id = authenticatedUserId,
                        role = authenticatedRole,
                        firstName = authenticatedFirstName,
                        username = authenticatedUsername,
                        profilePicture = authenticatedProfilePicture,
                        sessionToken = trackedSession.SessionToken,
                        refreshToken = trackedSession.RefreshToken,
                        refreshExpiresAt = trackedSession.RefreshExpiresAt,
                        signedOutOtherDevices = trackedSession.RevokedCount > 0
                    });
                }

                var failedAttempts = await SecuritySupport.RecordFailedLoginAsync(connection, normalizedEmail);
                var updatedLockoutState = await SecuritySupport.GetLockoutStateAsync(connection, normalizedEmail);
                await SecuritySupport.LogSecurityEventAsync(
                    connection,
                    null,
                    null,
                    updatedLockoutState.IsLocked ? "login_lockout_triggered" : "login_failed",
                    "user",
                    null,
                    HttpContext,
                    $"Failed login for {normalizedEmail}. Failed attempts: {failedAttempts}.");

                if (updatedLockoutState.IsLocked)
                {
                    return StatusCode(StatusCodes.Status429TooManyRequests, new
                    {
                        message = "Too many failed login attempts. Please wait before trying again.",
                        retryAfterSeconds = updatedLockoutState.RetryAfterSeconds
                    });
                }

                return Unauthorized(new { message = "Invalid email or password." });
            }
            catch (Exception)
            {
                return StatusCode(500, new { message = "Unable to sign in right now. Please try again in a moment." });
            }
        }

        [HttpGet("google-client-id")]
        public IActionResult GetGoogleClientId()
        {
            try
            {
                var config = GetGoogleClientConfiguration();
                if (string.IsNullOrWhiteSpace(config.ClientId))
                {
                    return NotFound(new { message = config.Message });
                }

                return Ok(new
                {
                    clientId = config.ClientId,
                    currentOrigin = $"{Request.Scheme}://{Request.Host}",
                    setupHint = BuildGoogleOriginSetupHint()
                });
            }
            catch (Exception ex)
            {
                return StatusCode(500, new
                {
                    message = ConfigurationFallbacks.BuildSafeErrorMessage(
                        _config,
                        "Failed to load Google sign-in settings.",
                        ex)
                });
            }
        }

        [HttpPost("google-login")]
        public async Task<IActionResult> GoogleLogin([FromBody] GoogleLoginDto req)
        {
            if (string.IsNullOrWhiteSpace(req?.credential))
            {
                return BadRequest(new { message = "Google credential is required." });
            }

            try
            {
                var config = GetGoogleClientConfiguration();
                var clientId = config.ClientId;
                if (string.IsNullOrWhiteSpace(clientId))
                {
                    return StatusCode(500, new { message = config.Message });
                }

                using var httpClient = new HttpClient();
                var verifyUrl = $"https://oauth2.googleapis.com/tokeninfo?id_token={Uri.EscapeDataString(req.credential)}";
                using var verifyResponse = await httpClient.GetAsync(verifyUrl);

                if (!verifyResponse.IsSuccessStatusCode)
                {
                    return Unauthorized(new { message = "Google Sign-In verification failed." });
                }

                var payloadJson = await verifyResponse.Content.ReadAsStringAsync();
                using var payloadDoc = JsonDocument.Parse(payloadJson);
                var payload = payloadDoc.RootElement;

                var audience = payload.TryGetProperty("aud", out var audElement) ? audElement.GetString() : string.Empty;
                if (!string.Equals(audience, clientId, StringComparison.OrdinalIgnoreCase))
                {
                    return Unauthorized(new { message = "Google Sign-In client mismatch." });
                }

                var email = payload.TryGetProperty("email", out var emailElement) ? emailElement.GetString() : string.Empty;
                var emailVerified = payload.TryGetProperty("email_verified", out var verifiedElement) ? verifiedElement.GetString() : string.Empty;
                var givenName = payload.TryGetProperty("given_name", out var givenNameElement) ? givenNameElement.GetString() : string.Empty;
                var familyName = payload.TryGetProperty("family_name", out var familyNameElement) ? familyNameElement.GetString() : string.Empty;
                var picture = payload.TryGetProperty("picture", out var pictureElement) ? pictureElement.GetString() : string.Empty;
                var fullName = payload.TryGetProperty("name", out var nameElement) ? nameElement.GetString() : string.Empty;

                var normalizedEmail = NormalizeEmail(email);
                if (string.IsNullOrWhiteSpace(normalizedEmail) || !string.Equals(emailVerified, "true", StringComparison.OrdinalIgnoreCase))
                {
                    return Unauthorized(new { message = "Your Google account email is not verified." });
                }

                await using var connection = new NpgsqlConnection(_connectionString);
                await connection.OpenAsync();
                await EnsureUserModerationColumnsAsync(connection);
                await EnsureMfaColumnsAsync(connection);
                await SecuritySupport.EnsureSecuritySchemaAsync(connection);

                var existingUser = await FindUserByEmailAsync(connection, normalizedEmail);
                if (existingUser is null)
                {
                    var firstName = !string.IsNullOrWhiteSpace(givenName)
                        ? givenName
                        : (fullName?.Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? "Google");
                    var lastName = !string.IsNullOrWhiteSpace(familyName)
                        ? familyName
                        : (fullName?.Split(' ', StringSplitOptions.RemoveEmptyEntries).Skip(1).FirstOrDefault() ?? string.Empty);
                    var suggestedUsername = await GenerateUniqueUsernameAsync(connection, fullName, normalizedEmail);

                    return Conflict(new
                    {
                        message = "No existing Imajination account was found for this Google email. Continue account setup to finish creating your profile.",
                        signUpRequired = true,
                        email = normalizedEmail,
                        firstName,
                        lastName,
                        fullName = fullName ?? string.Empty,
                        suggestedUsername,
                        profilePicture = picture ?? string.Empty
                    });
                }

                if (existingUser.IsBanned || string.Equals(existingUser.AccountStatus, "Banned", StringComparison.OrdinalIgnoreCase))
                {
                    return StatusCode(403, new { message = "This account has been blocked by admin." });
                }

                if (string.Equals(existingUser.AccountStatus, "PendingApproval", StringComparison.OrdinalIgnoreCase))
                {
                    return StatusCode(403, new { message = "This account is still waiting for admin approval." });
                }

                if (string.Equals(existingUser.AccountStatus, "Denied", StringComparison.OrdinalIgnoreCase))
                {
                    return StatusCode(403, new { message = "This account was denied by admin. Please contact support before trying again." });
                }

                if (string.IsNullOrWhiteSpace(existingUser.ProfilePicture) && !string.IsNullOrWhiteSpace(picture))
                {
                    const string updatePictureSql = "UPDATE users SET profile_picture = @profilePicture WHERE id = @id";
                    await using var updateCmd = new NpgsqlCommand(updatePictureSql, connection);
                    updateCmd.Parameters.AddWithValue("@profilePicture", picture);
                    updateCmd.Parameters.AddWithValue("@id", existingUser.Id);
                    await updateCmd.ExecuteNonQueryAsync();
                    existingUser = existingUser with { ProfilePicture = picture };
                }

                if (existingUser.MfaEnabled)
                {
                    var mfaTicket = CreatePendingMfaTicket(existingUser.Id, existingUser.Role, existingUser.FirstName, existingUser.Username, existingUser.ProfilePicture, normalizedEmail);
                    await SecuritySupport.LogSecurityEventAsync(
                        connection,
                        existingUser.Id,
                        existingUser.Role,
                        "mfa_challenge_issued",
                        "user",
                        existingUser.Id,
                        HttpContext,
                        $"Google sign-in accepted and MFA challenge issued for {normalizedEmail}.");

                    return Ok(new
                    {
                        mfaRequired = true,
                        mfaTicket,
                        id = existingUser.Id,
                        role = existingUser.Role,
                        firstName = existingUser.FirstName,
                        username = existingUser.Username,
                        profilePicture = existingUser.ProfilePicture ?? string.Empty
                    });
                }

                var trackedSession = await SecuritySupport.CreateTrackedSessionAsync(connection, existingUser.Id, existingUser.Role, HttpContext);

                await SecuritySupport.LogSecurityEventAsync(
                    connection,
                    existingUser.Id,
                    existingUser.Role,
                    "google_login_success",
                    "user",
                    existingUser.Id,
                    HttpContext,
                    $"Successful Google Sign-In for {normalizedEmail}.");
                SetAuthCookies(
                    existingUser.Id,
                    existingUser.Role,
                    existingUser.FirstName,
                    existingUser.Username,
                    trackedSession.SessionToken,
                    trackedSession.RefreshToken,
                    trackedSession.RefreshExpiresAt);

                return Ok(new
                {
                    id = existingUser.Id,
                    role = existingUser.Role,
                    firstName = existingUser.FirstName,
                    username = existingUser.Username,
                    profilePicture = existingUser.ProfilePicture ?? string.Empty,
                    sessionToken = trackedSession.SessionToken,
                    refreshToken = trackedSession.RefreshToken,
                    refreshExpiresAt = trackedSession.RefreshExpiresAt,
                    signedOutOtherDevices = trackedSession.RevokedCount > 0
                });
            }
            catch (Exception ex)
            {
                return StatusCode(500, new
                {
                    message = ConfigurationFallbacks.BuildSafeErrorMessage(
                        _config,
                        "Google sign-in could not be completed right now.",
                        ex)
                });
            }
        }

        [Authorize]
        [HttpGet("mfa/status")]
        public async Task<IActionResult> GetMfaStatus()
        {
            var userIdClaim = User.FindFirstValue(ClaimTypes.NameIdentifier);
            if (!Guid.TryParse(userIdClaim, out var userId))
            {
                return Unauthorized(new { message = "A valid user session is required." });
            }

            try
            {
                await using var connection = new NpgsqlConnection(_connectionString);
                await connection.OpenAsync();
                await EnsureMfaColumnsAsync(connection);

                const string sql = @"
                    SELECT COALESCE(mfa_enabled, FALSE), mfa_enrolled_at
                    FROM users
                    WHERE id = @id
                    LIMIT 1;";
                await using var cmd = new NpgsqlCommand(sql, connection);
                cmd.Parameters.AddWithValue("@id", userId);
                await using var reader = await cmd.ExecuteReaderAsync();
                if (!await reader.ReadAsync())
                {
                    return NotFound(new { message = "User not found." });
                }

                return Ok(new
                {
                    enabled = !reader.IsDBNull(0) && reader.GetBoolean(0),
                    enrolledAt = reader.IsDBNull(1) ? (DateTime?)null : reader.GetDateTime(1)
                });
            }
            catch (Exception ex)
            {
                return StatusCode(500, new
                {
                    message = ConfigurationFallbacks.BuildSafeErrorMessage(
                        _config,
                        "Failed to load MFA status.",
                        ex)
                });
            }
        }

        [Authorize]
        [HttpPost("mfa/enroll")]
        public async Task<IActionResult> EnrollMfa()
        {
            var userIdClaim = User.FindFirstValue(ClaimTypes.NameIdentifier);
            if (!Guid.TryParse(userIdClaim, out var userId))
            {
                return Unauthorized(new { message = "A valid user session is required." });
            }

            try
            {
                await using var connection = new NpgsqlConnection(_connectionString);
                await connection.OpenAsync();
                await EnsureMfaColumnsAsync(connection);
                await SecuritySupport.EnsureSecuritySchemaAsync(connection);

                const string lookupSql = "SELECT COALESCE(email, ''), COALESCE(mfa_enabled, FALSE) FROM users WHERE id = @id LIMIT 1;";
                string email;
                bool mfaEnabled;
                await using (var lookupCmd = new NpgsqlCommand(lookupSql, connection))
                {
                    lookupCmd.Parameters.AddWithValue("@id", userId);
                    await using var reader = await lookupCmd.ExecuteReaderAsync();
                    if (!await reader.ReadAsync())
                    {
                        return NotFound(new { message = "User not found." });
                    }

                    email = reader.IsDBNull(0) ? string.Empty : reader.GetString(0);
                    mfaEnabled = !reader.IsDBNull(1) && reader.GetBoolean(1);
                }

                if (mfaEnabled)
                {
                    return Conflict(new { message = "MFA is already enabled for this account." });
                }

                var secret = _totpService.GenerateSecret();
                var accountLabel = string.IsNullOrWhiteSpace(email) ? userId.ToString() : email;
                var issuer = _config["Auth:MfaIssuer"] ?? "Tugs!";
                var setupToken = Guid.NewGuid().ToString("N");
                _cache.Set($"mfa-setup:{setupToken}", new PendingMfaSetup(userId, secret), TimeSpan.FromMinutes(10));

                await SecuritySupport.LogSecurityEventAsync(connection, userId, User.FindFirstValue(ClaimTypes.Role), "mfa_enrollment_started", "user", userId, HttpContext, "User started MFA enrollment.");
                return Ok(new
                {
                    setupToken,
                    secret,
                    manualEntryKey = _totpService.BuildManualEntryKey(secret),
                    otpAuthUri = _totpService.BuildOtpAuthUri(issuer, accountLabel, secret)
                });
            }
            catch (Exception ex)
            {
                return StatusCode(500, new
                {
                    message = ConfigurationFallbacks.BuildSafeErrorMessage(
                        _config,
                        "Failed to start MFA enrollment.",
                        ex)
                });
            }
        }

        [Authorize]
        [HttpPost("mfa/verify-setup")]
        public async Task<IActionResult> VerifyMfaSetup([FromBody] VerifyMfaSetupDto req)
        {
            var userIdClaim = User.FindFirstValue(ClaimTypes.NameIdentifier);
            if (!Guid.TryParse(userIdClaim, out var userId))
            {
                return Unauthorized(new { message = "A valid user session is required." });
            }

            if (string.IsNullOrWhiteSpace(req.setupToken) || string.IsNullOrWhiteSpace(req.code))
            {
                return BadRequest(new { message = "Setup token and authenticator code are required." });
            }

            if (!_cache.TryGetValue($"mfa-setup:{req.setupToken}", out PendingMfaSetup? pendingSetup) || pendingSetup is null || pendingSetup.UserId != userId)
            {
                return BadRequest(new { message = "MFA setup session expired. Start enrollment again." });
            }

            if (!_totpService.ValidateCode(pendingSetup.Secret, req.code))
            {
                return BadRequest(new { message = "Authenticator code is invalid." });
            }

            try
            {
                await using var connection = new NpgsqlConnection(_connectionString);
                await connection.OpenAsync();
                await EnsureMfaColumnsAsync(connection);
                await SecuritySupport.EnsureSecuritySchemaAsync(connection);

                const string sql = @"
                    UPDATE users
                    SET mfa_enabled = TRUE,
                        mfa_secret = @secret,
                        mfa_enrolled_at = NOW()
                    WHERE id = @id;";
                await using var cmd = new NpgsqlCommand(sql, connection);
                cmd.Parameters.AddWithValue("@secret", pendingSetup.Secret);
                cmd.Parameters.AddWithValue("@id", userId);
                await cmd.ExecuteNonQueryAsync();

                _cache.Remove($"mfa-setup:{req.setupToken}");
                await SecuritySupport.LogSecurityEventAsync(connection, userId, User.FindFirstValue(ClaimTypes.Role), "mfa_enabled", "user", userId, HttpContext, "User enabled MFA.");
                return Ok(new { message = "MFA enabled successfully." });
            }
            catch (Exception ex)
            {
                return StatusCode(500, new
                {
                    message = ConfigurationFallbacks.BuildSafeErrorMessage(
                        _config,
                        "Failed to enable MFA.",
                        ex)
                });
            }
        }

        [Authorize]
        [HttpPost("mfa/disable")]
        public async Task<IActionResult> DisableMfa([FromBody] DisableMfaDto req)
        {
            var userIdClaim = User.FindFirstValue(ClaimTypes.NameIdentifier);
            if (!Guid.TryParse(userIdClaim, out var userId))
            {
                return Unauthorized(new { message = "A valid user session is required." });
            }

            if (string.IsNullOrWhiteSpace(req.code))
            {
                return BadRequest(new { message = "Authenticator code is required to disable MFA." });
            }

            try
            {
                await using var connection = new NpgsqlConnection(_connectionString);
                await connection.OpenAsync();
                await EnsureMfaColumnsAsync(connection);
                await SecuritySupport.EnsureSecuritySchemaAsync(connection);

                const string lookupSql = "SELECT COALESCE(mfa_secret, ''), COALESCE(mfa_enabled, FALSE) FROM users WHERE id = @id LIMIT 1;";
                string secret;
                bool enabled;
                await using (var lookupCmd = new NpgsqlCommand(lookupSql, connection))
                {
                    lookupCmd.Parameters.AddWithValue("@id", userId);
                    await using var reader = await lookupCmd.ExecuteReaderAsync();
                    if (!await reader.ReadAsync())
                    {
                        return NotFound(new { message = "User not found." });
                    }

                    secret = reader.IsDBNull(0) ? string.Empty : reader.GetString(0);
                    enabled = !reader.IsDBNull(1) && reader.GetBoolean(1);
                }

                if (!enabled || string.IsNullOrWhiteSpace(secret))
                {
                    return BadRequest(new { message = "MFA is not enabled for this account." });
                }

                if (!_totpService.ValidateCode(secret, req.code))
                {
                    return BadRequest(new { message = "Authenticator code is invalid." });
                }

                const string sql = "UPDATE users SET mfa_enabled = FALSE, mfa_secret = NULL, mfa_enrolled_at = NULL WHERE id = @id;";
                await using var cmd = new NpgsqlCommand(sql, connection);
                cmd.Parameters.AddWithValue("@id", userId);
                await cmd.ExecuteNonQueryAsync();

                await SecuritySupport.LogSecurityEventAsync(connection, userId, User.FindFirstValue(ClaimTypes.Role), "mfa_disabled", "user", userId, HttpContext, "User disabled MFA.");
                return Ok(new { message = "MFA disabled successfully." });
            }
            catch (Exception ex)
            {
                return StatusCode(500, new
                {
                    message = ConfigurationFallbacks.BuildSafeErrorMessage(
                        _config,
                        "Failed to disable MFA.",
                        ex)
                });
            }
        }

        [AllowAnonymous]
        [HttpPost("mfa/complete-login")]
        public async Task<IActionResult> CompleteMfaLogin([FromBody] CompleteMfaLoginDto req)
        {
            if (string.IsNullOrWhiteSpace(req.mfaTicket) || string.IsNullOrWhiteSpace(req.code))
            {
                return BadRequest(new { message = "MFA ticket and authenticator code are required." });
            }

            if (!_cache.TryGetValue($"mfa-login:{req.mfaTicket}", out PendingMfaLogin? pendingLogin) || pendingLogin is null)
            {
                return BadRequest(new { message = "MFA login session expired. Sign in again." });
            }

            try
            {
                await using var connection = new NpgsqlConnection(_connectionString);
                await connection.OpenAsync();
                await EnsureMfaColumnsAsync(connection);
                await SecuritySupport.EnsureSecuritySchemaAsync(connection);

                const string lookupSql = "SELECT COALESCE(mfa_secret, ''), COALESCE(mfa_enabled, FALSE) FROM users WHERE id = @id LIMIT 1;";
                string secret;
                bool enabled;
                await using (var lookupCmd = new NpgsqlCommand(lookupSql, connection))
                {
                    lookupCmd.Parameters.AddWithValue("@id", pendingLogin.UserId);
                    await using var reader = await lookupCmd.ExecuteReaderAsync();
                    if (!await reader.ReadAsync())
                    {
                        return NotFound(new { message = "User not found." });
                    }

                    secret = reader.IsDBNull(0) ? string.Empty : reader.GetString(0);
                    enabled = !reader.IsDBNull(1) && reader.GetBoolean(1);
                }

                if (!enabled || string.IsNullOrWhiteSpace(secret))
                {
                    return BadRequest(new { message = "MFA is not enabled for this account." });
                }

                if (!_totpService.ValidateCode(secret, req.code))
                {
                    await SecuritySupport.LogSecurityEventAsync(connection, pendingLogin.UserId, pendingLogin.Role, "mfa_failed", "user", pendingLogin.UserId, HttpContext, "Invalid MFA code submitted during login.");
                    return Unauthorized(new { message = "Authenticator code is invalid." });
                }

                var trackedSession = await SecuritySupport.CreateTrackedSessionAsync(connection, pendingLogin.UserId, pendingLogin.Role, HttpContext);
                await SecuritySupport.LogSecurityEventAsync(connection, pendingLogin.UserId, pendingLogin.Role, "mfa_success", "user", pendingLogin.UserId, HttpContext, "MFA challenge completed successfully.");
                _cache.Remove($"mfa-login:{req.mfaTicket}");
                SetAuthCookies(
                    pendingLogin.UserId,
                    pendingLogin.Role,
                    pendingLogin.FirstName,
                    pendingLogin.Username,
                    trackedSession.SessionToken,
                    trackedSession.RefreshToken,
                    trackedSession.RefreshExpiresAt);

                return Ok(new
                {
                    id = pendingLogin.UserId,
                    role = pendingLogin.Role,
                    firstName = pendingLogin.FirstName,
                    username = pendingLogin.Username,
                    profilePicture = pendingLogin.ProfilePicture,
                    sessionToken = trackedSession.SessionToken,
                    refreshToken = trackedSession.RefreshToken,
                    refreshExpiresAt = trackedSession.RefreshExpiresAt,
                    signedOutOtherDevices = trackedSession.RevokedCount > 0
                });
            }
            catch (Exception ex)
            {
                return StatusCode(500, new
                {
                    message = ConfigurationFallbacks.BuildSafeErrorMessage(
                        _config,
                        "Failed to complete MFA login.",
                        ex)
                });
            }
        }

        [HttpGet("session-status")]
        public async Task<IActionResult> GetSessionStatus([FromQuery] Guid userId)
        {
            var sessionToken = Request.Headers["X-Session-Token"].ToString();
            if (string.IsNullOrWhiteSpace(sessionToken))
            {
                sessionToken = Request.Cookies[SessionTokenCookieName] ?? string.Empty;
            }

            if (userId == Guid.Empty && Guid.TryParse(User.FindFirstValue(ClaimTypes.NameIdentifier), out var authenticatedUserId))
            {
                userId = authenticatedUserId;
            }

            if (userId == Guid.Empty || string.IsNullOrWhiteSpace(sessionToken))
            {
                return Unauthorized(new { message = "Session validation requires a valid user and session token." });
            }

            try
            {
                await using var connection = new NpgsqlConnection(_connectionString);
                await connection.OpenAsync();
                await SecuritySupport.EnsureSecuritySchemaAsync(connection);

                var sessionState = await SecuritySupport.ValidateTrackedSessionAsync(connection, userId, sessionToken);
                if (!sessionState.IsValid)
                {
                    return Unauthorized(new
                    {
                        message = sessionState.Message ?? "This session is no longer active.",
                        replacedElsewhere = sessionState.ReplacedElsewhere
                    });
                }

                await SecuritySupport.TouchTrackedSessionAsync(connection, userId, sessionToken);
                return Ok(new { active = true });
            }
            catch
            {
                return StatusCode(500, new { message = "Unable to verify session right now." });
            }
        }

        [AllowAnonymous]
        [HttpPost("refresh")]
        public async Task<IActionResult> RefreshSession([FromBody] RefreshSessionDto? req)
        {
            var userId = req?.userId ?? Guid.Empty;
            if (userId == Guid.Empty && Guid.TryParse(User.FindFirstValue(ClaimTypes.NameIdentifier), out var claimUserId))
            {
                userId = claimUserId;
            }

            var refreshToken = req?.refreshToken;
            if (string.IsNullOrWhiteSpace(refreshToken))
            {
                refreshToken = Request.Cookies[RefreshTokenCookieName] ?? string.Empty;
            }

            if (userId == Guid.Empty || string.IsNullOrWhiteSpace(refreshToken))
            {
                return BadRequest(new { message = "Refresh token details are required." });
            }

            try
            {
                await using var connection = new NpgsqlConnection(_connectionString);
                await connection.OpenAsync();
                await SecuritySupport.EnsureSecuritySchemaAsync(connection);

                const string lookupSql = @"
                    SELECT s.id,
                           u.id,
                           COALESCE(u.role, ''),
                           COALESCE(u.firstname, ''),
                           COALESCE(u.username, ''),
                           COALESCE(u.profile_picture, ''),
                           COALESCE(u.is_banned, FALSE),
                           COALESCE(u.account_status, 'Active')
                    FROM user_active_sessions s
                    INNER JOIN users u ON u.id = s.user_id
                    WHERE s.user_id = @userId
                      AND s.refresh_token_hash = @refreshTokenHash
                      AND s.revoked_at IS NULL
                      AND COALESCE(s.refresh_expires_at, NOW() - interval '1 minute') > NOW()
                    LIMIT 1;";

                Guid sessionId;
                string role;
                string firstName;
                string username;
                string profilePicture;
                bool isBanned;
                string accountStatus;
                await using (var lookupCmd = new NpgsqlCommand(lookupSql, connection))
                {
                    lookupCmd.Parameters.AddWithValue("@userId", userId);
                    lookupCmd.Parameters.AddWithValue("@refreshTokenHash", SecuritySupport.HashToken(refreshToken));
                    await using var reader = await lookupCmd.ExecuteReaderAsync();
                    if (!await reader.ReadAsync())
                    {
                        ClearAuthCookies();
                        return Unauthorized(new { message = "Refresh token is invalid or expired." });
                    }

                    sessionId = reader.GetGuid(0);
                    role = reader.GetString(2);
                    firstName = reader.GetString(3);
                    username = reader.GetString(4);
                    profilePicture = reader.GetString(5);
                    isBanned = reader.GetBoolean(6);
                    accountStatus = reader.GetString(7);
                }

                if (isBanned || accountStatus.Equals("Banned", StringComparison.OrdinalIgnoreCase))
                {
                    ClearAuthCookies();
                    return StatusCode(403, new { message = "This account has been blocked by admin." });
                }

                var nextSessionToken = SecuritySupport.GenerateSecureToken();
                var nextRefreshToken = SecuritySupport.GenerateSecureToken();
                var nextRefreshExpiresAt = DateTime.UtcNow.AddDays(14);

                const string rotateSql = @"
                    UPDATE user_active_sessions
                    SET session_token = @sessionToken,
                        refresh_token_hash = @refreshTokenHash,
                        refresh_expires_at = @refreshExpiresAt,
                        last_seen_at = NOW(),
                        ip_address = @ipAddress,
                        user_agent = @userAgent
                    WHERE id = @sessionId
                      AND revoked_at IS NULL;";

                await using (var rotateCmd = new NpgsqlCommand(rotateSql, connection))
                {
                    rotateCmd.Parameters.AddWithValue("@sessionId", sessionId);
                    rotateCmd.Parameters.AddWithValue("@sessionToken", nextSessionToken);
                    rotateCmd.Parameters.AddWithValue("@refreshTokenHash", SecuritySupport.HashToken(nextRefreshToken));
                    rotateCmd.Parameters.AddWithValue("@refreshExpiresAt", nextRefreshExpiresAt);
                    rotateCmd.Parameters.AddWithValue("@ipAddress", (object?)SecuritySupport.GetClientIpAddress(HttpContext) ?? DBNull.Value);
                    rotateCmd.Parameters.AddWithValue("@userAgent", (object?)SecuritySupport.GetUserAgent(HttpContext) ?? DBNull.Value);
                    await rotateCmd.ExecuteNonQueryAsync();
                }

                await SecuritySupport.LogSecurityEventAsync(
                    connection,
                    userId,
                    role,
                    "session_refreshed",
                    "user",
                    userId,
                    HttpContext,
                    "Access and refresh tokens were rotated.");

                SetAuthCookies(userId, role, firstName, username, nextSessionToken, nextRefreshToken, nextRefreshExpiresAt);
                return Ok(new
                {
                    id = userId,
                    role,
                    firstName,
                    username,
                    profilePicture,
                    sessionToken = nextSessionToken,
                    refreshToken = nextRefreshToken,
                    refreshExpiresAt = nextRefreshExpiresAt
                });
            }
            catch
            {
                return StatusCode(500, new { message = "Unable to refresh session right now." });
            }
        }

        [HttpPost("logout")]
        public async Task<IActionResult> Logout([FromBody] SessionLogoutDto req)
        {
            if (req.userId == Guid.Empty && Guid.TryParse(User.FindFirstValue(ClaimTypes.NameIdentifier), out var authenticatedUserId))
            {
                req.userId = authenticatedUserId;
            }

            if (string.IsNullOrWhiteSpace(req.sessionToken))
            {
                req.sessionToken = Request.Headers["X-Session-Token"].ToString();
            }

            if (string.IsNullOrWhiteSpace(req.sessionToken))
            {
                req.sessionToken = Request.Cookies[SessionTokenCookieName] ?? string.Empty;
            }

            if (req.userId == Guid.Empty || string.IsNullOrWhiteSpace(req.sessionToken))
            {
                return BadRequest(new { message = "Session details are required to log out." });
            }

            try
            {
                await using var connection = new NpgsqlConnection(_connectionString);
                await connection.OpenAsync();
                await SecuritySupport.EnsureSecuritySchemaAsync(connection);
                await SecuritySupport.RevokeTrackedSessionAsync(connection, req.userId, req.sessionToken, "Signed out by the user");
                await SecuritySupport.LogSecurityEventAsync(
                    connection,
                    req.userId,
                    Request.Headers["X-Actor-Role"].ToString(),
                    "logout",
                    "user",
                    req.userId,
                    HttpContext,
                    "User signed out of the current device.");
                ClearAuthCookies();
                return Ok(new { message = "Signed out successfully." });
            }
            catch
            {
                return StatusCode(500, new { message = "Unable to sign out right now." });
            }
        }

        // ==========================================
        // 4. RESET PASSWORD
        // ==========================================
        [HttpPost("reset-password")]
        public async Task<IActionResult> ResetPassword([FromBody] ResetPasswordDto req)
        {
            var normalizedEmail = NormalizeEmail(req.email);
            if (string.IsNullOrWhiteSpace(normalizedEmail))
            {
                return BadRequest(new { message = "Email is required." });
            }

            if (!_cache.TryGetValue(normalizedEmail, out string savedOtp))
            {
                return BadRequest(new { message = "OTP expired or not requested." });
            }

            if (savedOtp != req.otp)
            {
                return BadRequest(new { message = "Invalid OTP code." });
            }

            if (!IsStrongPassword(req.newPassword))
            {
                return BadRequest(new { message = "Password must be at least 8 characters and include uppercase, lowercase, number, and special character." });
            }

            try
            {
                string newPasswordHash = BCrypt.Net.BCrypt.HashPassword(req.newPassword);

                await using var connection = new NpgsqlConnection(_connectionString);
                await connection.OpenAsync();
                await SecuritySupport.EnsureSecuritySchemaAsync(connection);

                string sql = "UPDATE users SET passwordhash = @ph WHERE LOWER(TRIM(email)) = @em";
                using var cmd = new NpgsqlCommand(sql, connection);
                cmd.Parameters.AddWithValue("@ph", newPasswordHash);
                cmd.Parameters.AddWithValue("@em", normalizedEmail);

                int rowsAffected = await cmd.ExecuteNonQueryAsync();

                if (rowsAffected == 0)
                {
                    return BadRequest(new { message = "User not found." });
                }

                _cache.Remove(normalizedEmail);
                await SecuritySupport.LogSecurityEventAsync(
                    connection,
                    null,
                    null,
                    "password_reset",
                    "user",
                    null,
                    HttpContext,
                    $"Password reset completed for {normalizedEmail}.");

                return Ok(new { message = "Password updated successfully." });
            }
            catch (Exception ex)
            {
                return StatusCode(500, new
                {
                    message = ConfigurationFallbacks.BuildSafeErrorMessage(
                        _config,
                        "Password reset could not be completed right now.",
                        ex)
                });
            }
        }

        private GoogleClientConfiguration GetGoogleClientConfiguration()
        {
            var configuredClientId = ConfigurationFallbacks.GetSetting(_config, "GoogleAuth:ClientId", "GoogleAuth__ClientId");
            if (!string.IsNullOrWhiteSpace(configuredClientId))
            {
                return new GoogleClientConfiguration(configuredClientId, string.Empty);
            }

            var credentialsPath = Path.Combine(Directory.GetCurrentDirectory(), "credentials.json");
            if (!System.IO.File.Exists(credentialsPath))
            {
                return new GoogleClientConfiguration(null, "Google Sign-In is not configured. Set GoogleAuth__ClientId or add a local credentials.json.");
            }

            var json = System.IO.File.ReadAllText(credentialsPath);
            using var doc = JsonDocument.Parse(json);

            if (doc.RootElement.TryGetProperty("web", out var web) &&
                web.TryGetProperty("client_id", out var webClientId))
            {
                return new GoogleClientConfiguration(webClientId.GetString(), string.Empty);
            }

            if (doc.RootElement.TryGetProperty("installed", out _))
            {
                return new GoogleClientConfiguration(
                    null,
                    "Google Sign-In needs a Web application OAuth client. Your credentials.json is using an Installed/Desktop client, which causes the 'no registered origin' error."
                );
            }

            return new GoogleClientConfiguration(null, "Google Sign-In credentials are invalid. Expected the 'web' client format from Google Cloud.");
        }

        private string BuildGoogleOriginSetupHint()
        {
            var scheme = Request?.Scheme;
            var host = Request?.Host;
            if (string.IsNullOrWhiteSpace(scheme) || !host.HasValue)
            {
                return "Use a Google Web application client and add your app URL to Authorized JavaScript origins.";
            }

            var currentOrigin = $"{scheme}://{host.Value}";
            return $"If Google says the origin is not allowed, add {currentOrigin} to Authorized JavaScript origins for this Web client in Google Cloud Console.";
        }

        private async Task<UserLoginResult?> FindUserByEmailAsync(NpgsqlConnection connection, string email)
        {
            const string query = "SELECT id, role, firstname, username, profile_picture, COALESCE(is_banned, FALSE), COALESCE(account_status, 'Active'), COALESCE(mfa_enabled, FALSE) FROM users WHERE LOWER(TRIM(email)) = @email LIMIT 1";
            await using var cmd = new NpgsqlCommand(query, connection);
            cmd.Parameters.AddWithValue("@email", email);

            await using var reader = await cmd.ExecuteReaderAsync();
            if (!await reader.ReadAsync())
            {
                return null;
            }

            return new UserLoginResult(
                reader.GetGuid(0),
                reader.IsDBNull(1) ? "Customer" : reader.GetString(1),
                reader.IsDBNull(2) ? "Google" : reader.GetString(2),
                reader.IsDBNull(3) ? email.Split('@')[0] : reader.GetString(3),
                reader.IsDBNull(4) ? string.Empty : reader.GetString(4),
                !reader.IsDBNull(5) && reader.GetBoolean(5),
                reader.IsDBNull(6) ? "Active" : reader.GetString(6),
                !reader.IsDBNull(7) && reader.GetBoolean(7)
            );
        }

        private static async Task EnsureUserModerationColumnsAsync(NpgsqlConnection connection)
        {
            const string sql = @"
                ALTER TABLE users
                ADD COLUMN IF NOT EXISTS is_banned BOOLEAN NOT NULL DEFAULT FALSE;

                ALTER TABLE users
                ADD COLUMN IF NOT EXISTS account_status VARCHAR(40) NOT NULL DEFAULT 'Active';

                ALTER TABLE users
                ADD COLUMN IF NOT EXISTS age_verification_status VARCHAR(40) NOT NULL DEFAULT 'Unverified';";

            await using var cmd = new NpgsqlCommand(sql, connection);
            await cmd.ExecuteNonQueryAsync();
        }

        private static async Task EnsureTermsAcceptanceTableAsync(NpgsqlConnection connection)
        {
            const string sql = @"
                CREATE TABLE IF NOT EXISTS terms_acceptances (
                    id uuid PRIMARY KEY,
                    user_email text NOT NULL,
                    role varchar(40) NOT NULL,
                    terms_version varchar(80) NOT NULL,
                    accepted_at timestamptz NOT NULL DEFAULT NOW()
                );
                CREATE INDEX IF NOT EXISTS idx_terms_acceptances_email ON terms_acceptances(user_email);";
            await using var cmd = new NpgsqlCommand(sql, connection);
            await cmd.ExecuteNonQueryAsync();
        }

        private static async Task EnsureMfaColumnsAsync(NpgsqlConnection connection)
        {
            const string sql = @"
                ALTER TABLE users
                ADD COLUMN IF NOT EXISTS mfa_enabled BOOLEAN NOT NULL DEFAULT FALSE;

                ALTER TABLE users
                ADD COLUMN IF NOT EXISTS mfa_secret TEXT NULL;

                ALTER TABLE users
                ADD COLUMN IF NOT EXISTS mfa_enrolled_at timestamptz NULL;";

            await using var cmd = new NpgsqlCommand(sql, connection);
            await cmd.ExecuteNonQueryAsync();
        }

        private static async Task EnsureTalentRegistrationColumnsAsync(NpgsqlConnection connection)
        {
            const string sql = @"
                ALTER TABLE users
                ADD COLUMN IF NOT EXISTS talent_category VARCHAR(60);

                ALTER TABLE users
                ADD COLUMN IF NOT EXISTS member_names TEXT;";

            await using var cmd = new NpgsqlCommand(sql, connection);
            await cmd.ExecuteNonQueryAsync();
        }

        private async Task TryLogOtpEmailAttemptAsync(Guid attemptId, string recipientEmail, string senderEmail, string deliveryState, string providerMessage)
        {
            try
            {
                await using var connection = new NpgsqlConnection(_connectionString);
                await connection.OpenAsync();
                await EnsureOtpAuditTableAsync(connection);

                const string sql = @"
                    INSERT INTO otp_email_audit (id, recipient_email, sender_email, delivery_state, provider_message, created_at)
                    VALUES (@id, @recipientEmail, @senderEmail, @deliveryState, @providerMessage, NOW());";

                await using var cmd = new NpgsqlCommand(sql, connection);
                cmd.Parameters.AddWithValue("@id", attemptId);
                cmd.Parameters.AddWithValue("@recipientEmail", recipientEmail ?? string.Empty);
                cmd.Parameters.AddWithValue("@senderEmail", senderEmail ?? string.Empty);
                cmd.Parameters.AddWithValue("@deliveryState", deliveryState ?? string.Empty);
                cmd.Parameters.AddWithValue("@providerMessage", providerMessage ?? string.Empty);
                await cmd.ExecuteNonQueryAsync();
            }
            catch
            {
                // Sending OTP should not fail because audit logging is unavailable.
            }
        }

        private static async Task EnsureOtpAuditTableAsync(NpgsqlConnection connection)
        {
            const string sql = @"
                CREATE TABLE IF NOT EXISTS otp_email_audit (
                    id uuid PRIMARY KEY,
                    recipient_email text NOT NULL,
                    sender_email text NOT NULL,
                    delivery_state varchar(40) NOT NULL,
                    provider_message text NULL,
                    created_at timestamptz NOT NULL DEFAULT NOW()
                );";

            await using var cmd = new NpgsqlCommand(sql, connection);
            await cmd.ExecuteNonQueryAsync();
        }

        private async Task<string> GenerateUniqueUsernameAsync(NpgsqlConnection connection, string? fullName, string email)
        {
            var baseName = string.IsNullOrWhiteSpace(fullName) ? email.Split('@')[0] : fullName;
            var normalized = new string(baseName
                .ToLowerInvariant()
                .Where(char.IsLetterOrDigit)
                .ToArray());

            if (string.IsNullOrWhiteSpace(normalized))
            {
                normalized = "user";
            }

            if (normalized.Length > 20)
            {
                normalized = normalized[..20];
            }

            var candidate = normalized;
            var suffix = 1;

            while (await UsernameExistsAsync(connection, candidate))
            {
                candidate = $"{normalized}{suffix}";
                if (candidate.Length > 24)
                {
                    candidate = candidate[..24];
                }
                suffix++;
            }

            return candidate;
        }

        private async Task<bool> UsernameExistsAsync(NpgsqlConnection connection, string username)
        {
            const string query = "SELECT 1 FROM users WHERE username = @username LIMIT 1";
            await using var cmd = new NpgsqlCommand(query, connection);
            cmd.Parameters.AddWithValue("@username", username);
            var result = await cmd.ExecuteScalarAsync();
            return result is not null;
        }

        private static string NormalizeEmail(string? email)
        {
            return (email ?? string.Empty).Trim().ToLowerInvariant();
        }

        private static bool IsValidEmailAddress(string email) => RegistrationValidation.IsValidEmail(email);

        private static string? NormalizeRegistrationRole(string? role)
        {
            var normalized = SecuritySupport.SanitizePlainText(role, 40, false);
            if (string.IsNullOrWhiteSpace(normalized))
            {
                return null;
            }

            return normalized.Trim().ToLowerInvariant() switch
            {
                "customer"   => "Customer",
                "artist"     => "Artist",
                "sessionist" => "Sessionist",
                "organizer"  => "Organizer",
                _ => null
            };
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

        private string CreatePendingMfaTicket(Guid userId, string role, string firstName, string username, string profilePicture, string email)
        {
            var ticket = Guid.NewGuid().ToString("N");
            _cache.Set($"mfa-login:{ticket}", new PendingMfaLogin(userId, role, firstName, username, profilePicture, email), TimeSpan.FromMinutes(5));
            return ticket;
        }

        private sealed record UserLoginResult(Guid Id, string Role, string FirstName, string Username, string ProfilePicture, bool IsBanned, string AccountStatus, bool MfaEnabled);
        private sealed record PendingMfaLogin(Guid UserId, string Role, string FirstName, string Username, string ProfilePicture, string Email);
        private sealed record PendingMfaSetup(Guid UserId, string Secret);
        private sealed record GoogleClientConfiguration(string? ClientId, string Message);
        public sealed class SessionLogoutDto
        {
            public Guid userId { get; set; }
            public string sessionToken { get; set; } = string.Empty;
        }

        public sealed class VerifyMfaSetupDto
        {
            public string setupToken { get; set; } = string.Empty;
            public string code { get; set; } = string.Empty;
        }

        public sealed class CompleteMfaLoginDto
        {
            public string mfaTicket { get; set; } = string.Empty;
            public string code { get; set; } = string.Empty;
        }

        public sealed class DisableMfaDto
        {
            public string code { get; set; } = string.Empty;
        }
    }
}
