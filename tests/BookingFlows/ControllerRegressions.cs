using System.Reflection;
using System.Security.Claims;
using System.Text.Json;
using ImajinationAPI.Controllers;
using ImajinationAPI.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;

internal static class ControllerRegressions
{
    public static async Task RunAsync(string connectionString)
    {
        Environment.SetEnvironmentVariable("ConnectionStrings__SupabaseConnection", connectionString);
        await using var db = new NpgsqlConnection(connectionString);
        await db.OpenAsync();
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        { ["ConnectionStrings:SupabaseConnection"] = connectionString }).Build();
        var admin = Guid.NewGuid();
        var controller = new DisputeController(config, new EmailService(config, NullLogger<EmailService>.Instance));
        controller.ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext
        { User = new ClaimsPrincipal(new ClaimsIdentity(new[] { new Claim(ClaimTypes.NameIdentifier, admin.ToString()), new Claim(ClaimTypes.Role, "Admin") }, "test")) } };
        await (Task)typeof(DisputeController).GetMethod("EnsureSchemaAsync", BindingFlags.NonPublic | BindingFlags.Static)!.Invoke(null, new object[] { db })!;
        var booking = Guid.NewGuid(); var dispute = Guid.NewGuid(); var talent = Guid.NewGuid();
        await Sql("INSERT INTO bookings(id,customer_id,target_user_id,budget,status,dispute_hold) VALUES(@id,@id,@talent,100,'Confirmed',TRUE)", booking, talent);
        await EscrowService.CreatePendingAsync(db, booking, admin, talent, 100m);
        await EscrowService.MarkHeldAsync(db, booking, "regression-payment");
        await EscrowService.MarkDisputedAsync(db, booking);
        await using (var cmd = new NpgsqlCommand("INSERT INTO booking_disputes(id,booking_id,reporter_id,description) VALUES(@id,@booking,@id,'Regression dispute')", db))
        { cmd.Parameters.AddWithValue("@id", dispute); cmd.Parameters.AddWithValue("@booking", booking); await cmd.ExecuteNonQueryAsync(); }
        var result = await controller.ResolveDispute(dispute, new ResolveDisputeRequest { resolution = "PartialRefund", customerRefundAmount = 100m });
        if (result is not ConflictObjectResult) throw new Exception("Invalid partial settlement must return Conflict");
        var preserved = await Scalar("SELECT status = 'Open' AND (SELECT dispute_hold FROM bookings WHERE id=booking_id) FROM booking_disputes WHERE id=@id", dispute);

        // Simulate a crash after the provider refund was persisted, before local settlement.
        await PaymentRefundService.EnsureSchemaAsync(db);
        var recovery = Guid.NewGuid();
        await Sql("INSERT INTO bookings(id,customer_id,target_user_id,budget,status) VALUES(@id,@id,@talent,100,'Confirmed')", recovery, talent);
        await EscrowService.CreatePendingAsync(db, recovery, admin, talent, 100m);
        await EscrowService.MarkHeldAsync(db, recovery, "recovery-payment");
        await EscrowService.SettlePartialAsync(db, recovery, admin, null, 25m, "retry regression");
        var refundId = "re_" + recovery.ToString("N");
        var request = await PaymentRefundService.CreateRefundRequestAsync(db, "booking", "booking_talent_fee", recovery, null, admin, null, "Admin", "test", null, 25m, "recovery-payment", new { partialRefund = true });
        await PaymentRefundService.UpdateRefundRequestAsync(db, request, "Refunded", refundId, "succeeded", null, null);
        using var resource = JsonDocument.Parse(JsonSerializer.Serialize(new { id = refundId }));
        var method = typeof(PayMongoWebhookController).GetMethod("ReconcileRefundEventAsync", BindingFlags.NonPublic | BindingFlags.Static)!;
        foreach (var eventType in new[] { "refund.paid", "refund.paid", "refund.pending", "refund.failed" })
            await (Task)method.Invoke(null, new object[] { db, eventType, resource.RootElement, CancellationToken.None })!;
        var recovered = await Scalar("SELECT status='Released' FROM escrow_transactions WHERE booking_id=@id", recovery);
        var terminalPreserved = await Scalar("SELECT status='Refunded' FROM refund_requests WHERE id=@id", request);
        var creditOnce = await Scalar("SELECT COUNT(*)=1 AND SUM(amount)=75 FROM wallet_ledger_entries WHERE booking_id=@id AND entry_type='EscrowRelease'", recovery);
        if (!preserved || !recovered || !terminalPreserved || !creditOnce)
            throw new Exception($"Controller regressions: dispute preserved={preserved}; refund recovered={recovered}; terminal preserved={terminalPreserved}; credited once={creditOnce}");
        Console.WriteLine("PASS invalid partial settlement preserves open dispute and hold");
        Console.WriteLine("PASS terminal refund replay recovers settlement once and ignores stale events");
        async Task Sql(string sql, Guid id, Guid talentId) { await using var cmd = new NpgsqlCommand(sql, db); cmd.Parameters.AddWithValue("@id", id); cmd.Parameters.AddWithValue("@talent", talentId); await cmd.ExecuteNonQueryAsync(); }
        async Task<bool> Scalar(string sql, Guid id) { await using var cmd = new NpgsqlCommand(sql, db); cmd.Parameters.AddWithValue("@id", id); return (bool)(await cmd.ExecuteScalarAsync())!; }
    }
}
