using System.Threading.RateLimiting;
using Npgsql;
using Microsoft.AspNetCore.Builder;
using ImajinationAPI.Controllers;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.FileProviders;
using ImajinationAPI.Services;

var dotenvPath = FindDotEnvPath();
var dotenvValues = dotenvPath is null
    ? new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
    : LoadDotEnv(dotenvPath);
var builder = WebApplication.CreateBuilder(args);

var renderPort = Environment.GetEnvironmentVariable("PORT");
if (!string.IsNullOrWhiteSpace(renderPort))
{
    builder.WebHost.UseUrls($"http://0.0.0.0:{renderPort}");
}

if (dotenvValues.Count > 0)
{
    builder.Configuration.AddInMemoryCollection(dotenvValues);
}

var allowedOrigins = builder.Configuration
    .GetSection("AppSecurity:AllowedOrigins")
    .Get<string[]>()?
    .Where(origin => !string.IsNullOrWhiteSpace(origin))
    .Select(origin => origin.Trim().TrimEnd('/'))
    .Distinct(StringComparer.OrdinalIgnoreCase)
    .ToArray()
    ?? Array.Empty<string>();
ValidateSecurityConfiguration(builder.Configuration, builder.Environment, allowedOrigins);
var jwtBootstrapService = new JwtTokenService(builder.Configuration);

var dbConnectionString = ConfigurationFallbacks.GetRequiredSupabaseConnectionString(builder.Configuration);
var npgsqlDataSource = NpgsqlDataSource.Create(dbConnectionString);
builder.Services.AddSingleton(npgsqlDataSource);

// Add Controllers
builder.Services.AddControllers();
builder.Services.AddDataProtection();
builder.Services.Configure<ForwardedHeadersOptions>(options =>
{
    options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
    options.KnownNetworks.Clear();
    options.KnownProxies.Clear();
});
builder.Services.AddSingleton(jwtBootstrapService);
builder.Services.AddSingleton<TotpService>();
builder.Services.AddSingleton<UploadScanningService>();
builder.Services.AddSingleton<AutomatedVerificationAssessmentService>();
builder.Services.AddSingleton<MessageProtectionService>();
builder.Services.AddSingleton<BookingMessageStreamService>();
builder.Services.AddSingleton<EmailService>();
builder.Services.AddSingleton<TicketPdfService>();
builder.Services.AddHostedService<EventReminderService>();
builder.Services.AddSignalR();
builder.Services.AddSingleton<ImajinationAPI.Hubs.EventScanBroadcaster>();
builder.Services.AddAuthentication(options =>
    {
        options.DefaultScheme = "HybridAuth";
        options.DefaultAuthenticateScheme = "HybridAuth";
        options.DefaultChallengeScheme = "HybridAuth";
    })
    .AddPolicyScheme("HybridAuth", "JWT or session token", options =>
    {
        options.ForwardDefaultSelector = context =>
        {
            var authorization = context.Request.Headers.Authorization.ToString();
            if (!string.IsNullOrWhiteSpace(authorization) &&
                authorization.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
            {
                return JwtBearerDefaults.AuthenticationScheme;
            }

            var accessCookie = context.Request.Cookies["IMAJINATION-ACCESS"];
            if (!string.IsNullOrWhiteSpace(accessCookie))
            {
                return JwtBearerDefaults.AuthenticationScheme;
            }

            if (!string.IsNullOrWhiteSpace(context.Request.Headers["X-Session-Token"]))
            {
                return SessionTokenAuthenticationHandler.SchemeName;
            }

            return JwtBearerDefaults.AuthenticationScheme;
        };
    })
    .AddJwtBearer(options =>
    {
        options.TokenValidationParameters = jwtBootstrapService.CreateValidationParameters();
        options.Events = new JwtBearerEvents
        {
            OnMessageReceived = context =>
            {
                if (string.IsNullOrWhiteSpace(context.Token))
                {
                    context.Token = context.Request.Cookies["IMAJINATION-ACCESS"];
                }

                return Task.CompletedTask;
            }
        };
    })
    .AddScheme<AuthenticationSchemeOptions, SessionTokenAuthenticationHandler>(
        SessionTokenAuthenticationHandler.SchemeName,
        _ => { });
builder.Services.AddAuthorization();
builder.Services.AddAntiforgery(options =>
{
    options.HeaderName = "X-CSRF-TOKEN";
    options.Cookie.Name = "IMAJINATION-XSRF";
    options.Cookie.HttpOnly = false;
    options.Cookie.SameSite = SameSiteMode.Strict;
    options.Cookie.SecurePolicy = builder.Environment.IsDevelopment()
        ? CookieSecurePolicy.SameAsRequest
        : CookieSecurePolicy.Always;
});
builder.Services.AddMemoryCache(); // <-- ADD THIS LINE
builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    options.OnRejected = async (context, cancellationToken) =>
    {
        context.HttpContext.Response.ContentType = "application/json";
        await context.HttpContext.Response.WriteAsync(
            "{\"message\":\"Too many requests. Please wait a moment and try again.\"}",
            cancellationToken);
    };

    options.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(httpContext =>
    {
        var path = httpContext.Request.Path.Value ?? string.Empty;
        var remoteKey = httpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown";
        var key = $"{remoteKey}:{path}";
        var isAuthRoute = path.StartsWith("/api/auth", StringComparison.OrdinalIgnoreCase);

        return RateLimitPartition.GetFixedWindowLimiter(
            partitionKey: key,
            factory: _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = isAuthRoute ? 10 : 120,
                Window = TimeSpan.FromMinutes(1),
                QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
                QueueLimit = isAuthRoute ? 0 : 20
            });
    });
});

// Add CORS - This is the "Key" that lets your HTML talk to C#
builder.Services.AddCors(options =>
{
    options.AddPolicy("AllowImajination",
        policy =>
        {
            if (allowedOrigins.Length > 0)
            {
                policy.WithOrigins(allowedOrigins)
                      .AllowAnyHeader()
                      .AllowAnyMethod();
            }
            else
            {
                // Safe local-development fallback
                policy.WithOrigins(
                          "http://localhost:5248",
                          "https://localhost:5248",
                          "http://127.0.0.1:5248",
                          "https://127.0.0.1:5248")
                      .AllowAnyHeader()
                      .AllowAnyMethod();
            }
        });
});

var app = builder.Build();

app.UseForwardedHeaders();

app.UseExceptionHandler(exceptionApp =>
{
    exceptionApp.Run(async context =>
    {
        var exceptionFeature = context.Features.Get<IExceptionHandlerFeature>();
        var logger = context.RequestServices.GetRequiredService<ILoggerFactory>().CreateLogger("GlobalExceptionHandler");
        if (exceptionFeature?.Error is not null)
        {
            logger.LogError(exceptionFeature.Error, "Unhandled exception for {Path}", context.Request.Path);
        }

        context.Response.StatusCode = StatusCodes.Status500InternalServerError;
        context.Response.ContentType = "application/json";
        await context.Response.WriteAsJsonAsync(new
        {
            message = "The server could not complete this request safely. Please try again."
        });
    });
});

var legacyStaticPaths = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
{
    ["/"] = "/pages/home/LandingPage.html",
    ["/LandingPage.html"] = "/pages/home/LandingPage.html",
    ["/login.html"] = "/pages/auth/login.html",
    ["/forgotpassword.html"] = "/pages/auth/forgotpassword.html",
    ["/signuproles.html"] = "/pages/auth/signuproles.html",
    ["/signup customer.html"] = "/pages/auth/signup customer.html",
    ["/signup organizer.html"] = "/pages/auth/signup organizer.html",
    ["/artist.html"] = "/pages/auth/artist.html",
    ["/sessionist.html"] = "/pages/auth/sessionist.html",
    ["/Events.html"] = "/pages/browse/Events.html",
    ["/Artists.html"] = "/pages/browse/Artists.html",
    ["/Sessionists.html"] = "/pages/browse/Sessionists.html",
    ["/Community.html"] = "/pages/browse/Community.html",
    ["/Checkout.html"] = "/pages/bookings/Checkout.html",
    ["/messages.html"] = "/pages/bookings/messages.html",
    ["/CustomerDashboard.html"] = "/pages/dashboards/CustomerDashboard.html",
    ["/OrganizerDashboard.html"] = "/pages/dashboards/OrganizerDashboard.html",
    ["/ArtistDashboard.html"] = "/pages/dashboards/ArtistDashboard.html",
    ["/SessionistDashboard.html"] = "/pages/dashboards/SessionistDashboard.html",
    ["/ProfileAdmin.html"] = "/pages/dashboards/ProfileAdmin.html",
    ["/ArtistDetails.html"] = "/pages/details/ArtistDetails.html",
    ["/artistdetails.html"] = "/pages/details/ArtistDetails.html",
    ["/EventDetailPage.html"] = "/pages/details/EventDetailPage.html",
    ["/SessionistDetailsPage.html"] = "/pages/details/SessionistDetailsPage.html",
    ["/dashboardscanner.html"] = "/pages/tools/dashboardscanner.html",
    ["/style.css"] = "/assets/css/style.css"
};

app.Use(async (context, next) =>
{
    var scriptSources = new[]
    {
        "'self'",
        "'unsafe-inline'",
        "https://cdn.tailwindcss.com",
        "https://unpkg.com",
        "https://accounts.google.com",
        "https://cdn.jsdelivr.net",
        "https://code.iconify.design",
        "'wasm-unsafe-eval'"
    };

    context.Response.Headers["Content-Security-Policy"] =
        "default-src 'self' data: blob: https:; " +
        "img-src 'self' data: blob: https: http:; " +
        "style-src 'self' 'unsafe-inline' https://fonts.googleapis.com https://cdn.tailwindcss.com https://accounts.google.com; " +
        "style-src-elem 'self' 'unsafe-inline' https://fonts.googleapis.com https://cdn.tailwindcss.com https://accounts.google.com; " +
        "font-src 'self' data: https://fonts.gstatic.com; " +
        $"script-src {string.Join(' ', scriptSources)}; " +
        "script-src-attr 'unsafe-inline'; " +
        "frame-src 'self' https://accounts.google.com; " +
        "connect-src 'self' https: wss: ws:; " +
        "object-src 'none'; " +
        "base-uri 'self'; " +
        "form-action 'self'; " +
        "frame-ancestors 'self';";
    context.Response.Headers["X-Content-Type-Options"] = "nosniff";
    context.Response.Headers["X-Frame-Options"] = "SAMEORIGIN";
    context.Response.Headers["Referrer-Policy"] = "strict-origin-when-cross-origin";
    var isLoginPage = context.Request.Path.Equals("/pages/auth/login.html", StringComparison.OrdinalIgnoreCase)
        || context.Request.Path.Equals("/login.html", StringComparison.OrdinalIgnoreCase);
    context.Response.Headers["Cross-Origin-Opener-Policy"] = isLoginPage
        ? "unsafe-none"
        : "same-origin-allow-popups";
    context.Response.Headers["Cross-Origin-Resource-Policy"] = "same-origin";
    context.Response.Headers["X-Permitted-Cross-Domain-Policies"] = "none";
    context.Response.Headers["Permissions-Policy"] =
        "camera=(self), microphone=(), geolocation=(), payment=(), usb=()";

    if (context.Request.Path.StartsWithSegments("/api", StringComparison.OrdinalIgnoreCase))
    {
        context.Response.Headers["Cache-Control"] = "no-store, no-cache, must-revalidate";
        context.Response.Headers["Pragma"] = "no-cache";
        context.Response.Headers["Expires"] = "0";
    }

    await next();
});

var contentRootProvider = new PhysicalFileProvider(app.Environment.ContentRootPath);

app.Use(async (context, next) =>
{
    var requestPath = context.Request.Path.Value;

    if (!string.IsNullOrWhiteSpace(requestPath))
    {
        if (legacyStaticPaths.TryGetValue(requestPath, out var rewrittenPath))
        {
            context.Request.Path = rewrittenPath;
        }
        else if (requestPath.StartsWith("/images/", StringComparison.OrdinalIgnoreCase))
        {
            context.Request.Path = "/assets" + requestPath;
        }
    }

    await next();
});

app.UseStaticFiles(new StaticFileOptions
{
    FileProvider = contentRootProvider
});

// Enable CORS
app.UseCors("AllowImajination");
app.UseRateLimiter();

if (!app.Environment.IsDevelopment())
{
    app.UseHsts();
    app.UseHttpsRedirection();
}

var antiforgery = app.Services.GetRequiredService<IAntiforgery>();
app.Use(async (context, next) =>
{
    var requestPath = context.Request.Path;
    var isPublicAuthPost =
        requestPath.StartsWithSegments("/api/auth/login", StringComparison.OrdinalIgnoreCase) ||
        requestPath.StartsWithSegments("/api/auth/google-login", StringComparison.OrdinalIgnoreCase) ||
        requestPath.StartsWithSegments("/api/auth/mfa/complete-login", StringComparison.OrdinalIgnoreCase) ||
        requestPath.StartsWithSegments("/api/auth/send-otp", StringComparison.OrdinalIgnoreCase) ||
        requestPath.StartsWithSegments("/api/auth/reset-password", StringComparison.OrdinalIgnoreCase) ||
        requestPath.StartsWithSegments("/api/partner/apply", StringComparison.OrdinalIgnoreCase); // public partner form
    var isRealtimeMessagePost =
        HttpMethods.IsPost(context.Request.Method) &&
        requestPath.StartsWithSegments("/api/message/booking", StringComparison.OrdinalIgnoreCase);
    var requestedWith = context.Request.Headers["X-Requested-With"].ToString();
    var secFetchSite = context.Request.Headers["Sec-Fetch-Site"].ToString();
    var isSameOriginAjaxRequest =
        string.Equals(requestedWith, "fetch", StringComparison.OrdinalIgnoreCase) &&
        (string.IsNullOrWhiteSpace(secFetchSite) ||
         string.Equals(secFetchSite, "same-origin", StringComparison.OrdinalIgnoreCase) ||
         string.Equals(secFetchSite, "same-site", StringComparison.OrdinalIgnoreCase) ||
         string.Equals(secFetchSite, "none", StringComparison.OrdinalIgnoreCase));

    if (!context.Request.Path.StartsWithSegments("/api") ||
        HttpMethods.IsGet(context.Request.Method) ||
        HttpMethods.IsHead(context.Request.Method) ||
        HttpMethods.IsOptions(context.Request.Method) ||
        HttpMethods.IsTrace(context.Request.Method) ||
        context.Request.Path.StartsWithSegments("/api/security/csrf-token") ||
        isPublicAuthPost ||
        isRealtimeMessagePost ||
        isSameOriginAjaxRequest)
    {
        await next();
        return;
    }

    try
    {
        await antiforgery.ValidateRequestAsync(context);
        await next();
    }
    catch (AntiforgeryValidationException)
    {
        context.Response.StatusCode = StatusCodes.Status400BadRequest;
        context.Response.ContentType = "application/json";
        await context.Response.WriteAsync("{\"message\":\"Security validation failed. Refresh the page and try again.\"}");
    }
});

app.UseAuthentication();
app.UseAuthorization();

// Map the routes
app.MapControllers();
app.MapHub<ImajinationAPI.Hubs.EventScanHub>("/hubs/event-scan");

app.Run();

static void ValidateSecurityConfiguration(IConfiguration configuration, IWebHostEnvironment environment, string[] allowedOrigins)
{
    var jwtSecret = Environment.GetEnvironmentVariable("Auth__JwtSecret") ?? configuration["Auth:JwtSecret"];
    if (!environment.IsDevelopment() && string.IsNullOrWhiteSpace(jwtSecret))
    {
        throw new InvalidOperationException(
            "Auth__JwtSecret must be configured outside Development so JWT signing does not fall back to insecure defaults.");
    }

    if (!environment.IsDevelopment() && allowedOrigins.Length == 0)
    {
        throw new InvalidOperationException(
            "At least one AppSecurity:AllowedOrigins entry is required outside Development.");
    }
}

static Dictionary<string, string?> LoadDotEnv(string filePath)
{
    var values = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);

    if (!File.Exists(filePath))
    {
        return values;
    }

    foreach (var rawLine in File.ReadAllLines(filePath))
    {
        var line = rawLine.Trim();
        if (string.IsNullOrWhiteSpace(line) || line.StartsWith("#", StringComparison.Ordinal))
        {
            continue;
        }

        var separatorIndex = line.IndexOf('=');
        if (separatorIndex <= 0)
        {
            continue;
        }

        var key = line[..separatorIndex].Trim().Replace("__", ":");
        var value = line[(separatorIndex + 1)..].Trim();

        if (string.IsNullOrWhiteSpace(key))
        {
            continue;
        }

        if ((value.StartsWith('"') && value.EndsWith('"')) || (value.StartsWith('\'') && value.EndsWith('\'')))
        {
            value = value[1..^1];
        }

        values[key] = value;
    }

    return values;
}

static string? FindDotEnvPath()
{
    var startingPoints = new[]
    {
        Directory.GetCurrentDirectory(),
        AppContext.BaseDirectory
    }
    .Where(path => !string.IsNullOrWhiteSpace(path))
    .Distinct(StringComparer.OrdinalIgnoreCase);

    foreach (var start in startingPoints)
    {
        var current = new DirectoryInfo(start);
        while (current is not null)
        {
            var candidate = Path.Combine(current.FullName, ".env");
            if (File.Exists(candidate))
            {
                return candidate;
            }

            current = current.Parent;
        }
    }

    return null;
}
