using Npgsql;
using NpgsqlTypes;
using ImajinationAPI.Controllers;

namespace ImajinationAPI.Services
{
    /// <summary>
    /// Background service that releases escrowed talent fees whose confirmation/refund
    /// window has elapsed. Runs every 30 minutes and is a no-op when
    /// Escrow:ReleaseWindowHours is zero (immediate release).
    /// </summary>
    public class EscrowReleaseWindowService : BackgroundService
    {
        private static readonly TimeSpan StartupDelay = TimeSpan.FromSeconds(120);
        private static readonly TimeSpan StandardInterval = TimeSpan.FromMinutes(30);
        private readonly ILogger<EscrowReleaseWindowService> _logger;
        private readonly string _connectionString;

        public EscrowReleaseWindowService(
            IConfiguration configuration,
            ILogger<EscrowReleaseWindowService> logger)
        {
            _logger = logger;
            _connectionString = ConfigurationFallbacks.GetRequiredSupabaseConnectionString(configuration);
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            _logger.LogInformation("[EscrowRelease] Service started.");

            await Task.Delay(StartupDelay, stoppingToken);

            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    await ReleaseReadyEscrowAsync(stoppingToken);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "[EscrowRelease] Error during release sweep.");
                }

                await Task.Delay(StandardInterval, stoppingToken);
            }
        }

        private async Task ReleaseReadyEscrowAsync(CancellationToken ct)
        {
            var window = EscrowService.ReleaseWindow;
            if (window <= TimeSpan.Zero) return;

            await using var connection = new NpgsqlConnection(_connectionString);
            await connection.OpenAsync(ct);
            await EscrowService.EnsureSchemaAsync(connection);

            const string sql = @"
                SELECT e.booking_id, e.beneficiary_user_id, e.net_amount, e.currency
                FROM escrow_transactions e
                INNER JOIN bookings b ON b.id = e.booking_id
                WHERE e.status = 'ReleaseReady'
                  AND e.release_ready_at IS NOT NULL
                  AND e.release_ready_at <= NOW() - @window
                  AND COALESCE(b.dispute_hold, FALSE) = FALSE
                LIMIT 100;";
            var releases = new List<(Guid bookingId, Guid beneficiaryId, decimal amount, string currency)>();
            await using (var cmd = new NpgsqlCommand(sql, connection))
            {
                cmd.Parameters.Add("@window", NpgsqlDbType.Interval).Value = window;
                await using var reader = await cmd.ExecuteReaderAsync(ct);
                while (await reader.ReadAsync(ct))
                {
                    releases.Add((
                        reader.GetGuid(0),
                        reader.GetGuid(1),
                        reader.GetDecimal(2),
                        reader.GetString(3)));
                }
            }

            if (releases.Count == 0) return;

            _logger.LogInformation("[EscrowRelease] Releasing {Count} escrowed bookings past their confirmation window.", releases.Count);

            foreach (var release in releases)
            {
                ct.ThrowIfCancellationRequested();
                try
                {
                    // ReleaseAsync re-validates completion, fees and dispute state, then
                    // credits the talent wallet exactly once (idempotency-key guarded).
                    if (await EscrowService.ReleaseAsync(connection, release.bookingId, "booking_completion"))
                    {
                        await NotificationSupport.EnsureNotificationsTableExistsAsync(connection);
                        await NotificationSupport.InsertNotificationAsync(connection, release.beneficiaryId,
                            "escrow_released", "Payment Released",
                            $"Your talent fee of {release.currency} {release.amount:0.00} for a completed booking has been released to your wallet.",
                            release.bookingId, "booking");
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "[EscrowRelease] Failed to release booking {BookingId}.", release.bookingId);
                }
            }
        }
    }
}