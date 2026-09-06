using System.Text.Json;
using ImajinationAPI.Services;
using Npgsql;

var connectionString = Environment.GetEnvironmentVariable("BOOKING_TEST_DATABASE") ?? "Host=127.0.0.1;Port=55439;Database=postgres;Username=escrow_test";
var builder = new NpgsqlConnectionStringBuilder(connectionString);
if (builder.Host != "127.0.0.1" || builder.Username != "escrow_test") throw new Exception("Only the isolated local escrow_test database is supported.");
await using var db = new NpgsqlConnection(connectionString);
await db.OpenAsync();
EscrowService.InitializeReleaseWindow(TimeSpan.Zero);
await Sql(@"CREATE TABLE IF NOT EXISTS bookings (
 id uuid PRIMARY KEY, customer_id uuid NOT NULL, target_user_id uuid NOT NULL, budget numeric(12,2), status text,
 talent_fee_status text DEFAULT 'Unpaid', talent_platform_fee_status text DEFAULT 'Paid', payment_status text,
 customer_completed_at timestamptz, target_completed_at timestamptz, talent_fee_paid_at timestamptz, updated_at timestamptz DEFAULT NOW(),
 service_fee_status text DEFAULT 'Unpaid', service_fee_paid_at timestamptz, paid_at timestamptz,
 paymongo_payment_id text, paymongo_payment_reference text, payment_method text, service_fee_payment_method text,
 target_role text DEFAULT 'Artist', talent_fee_payment_id text, talent_fee_payment_reference text, talent_fee_payment_method text,
 talent_platform_fee_paid_at timestamptz, talent_platform_fee_payment_id text, talent_platform_fee_payment_reference text, talent_platform_fee_payment_method text
);");
await EscrowService.EnsureSchemaAsync(db);
var customer = Guid.NewGuid(); var talent = Guid.NewGuid();
var topup = await EscrowService.CreateTopUpAsync(db, customer, 1000m);
await EscrowService.ConfirmTopUpAsync(db, topup, customer, "test-payment");
await EscrowService.ConfirmTopUpAsync(db, topup, customer, "test-payment");
Check((await EscrowService.GetWalletSummaryAsync(db, customer)).AvailableBalance == 1000m, "Duplicate top-up credits once");
var booking = await Booking(600m);
await EscrowService.ReserveFromWalletAsync(db, booking, customer, talent, 600m);
await EscrowService.ReserveFromWalletAsync(db, booking, customer, talent, 600m);
Check((await EscrowService.GetWalletSummaryAsync(db, customer)).AvailableBalance == 400m, "Duplicate reservation debits once");
Check(!await EscrowService.ReleaseAsync(db, booking), "Uncompleted booking cannot release");
await Sql("UPDATE bookings SET status='Completed', customer_completed_at=NOW(), target_completed_at=NOW(), dispute_hold=TRUE WHERE id=@id", booking);
Check(!await EscrowService.ReleaseAsync(db, booking), "Dispute hold blocks release");
await Sql("UPDATE bookings SET dispute_hold=FALSE WHERE id=@id", booking);
Check(await EscrowService.ReleaseAsync(db, booking), "Both confirmations release escrow");
Check(await EscrowService.ReleaseAsync(db, booking), "Repeated release succeeds idempotently");
Check((await EscrowService.GetWalletSummaryAsync(db, talent)).AvailableBalance == 600m, "Release credits talent exactly once");
var first = await Booking(300m); var second = await Booking(300m);
var results = await Task.WhenAll(Reserve(first), Reserve(second));
Check(results.Count(x => x) == 1, "Concurrent reservations cannot overdraw wallet");
Check((await EscrowService.GetWalletSummaryAsync(db, customer)).AvailableBalance == 100m, "Concurrent reservations preserve balance");
var pending = await Booking(500m);
await EscrowService.CreatePendingAsync(db, pending, customer, talent, 500m);
await EscrowService.MarkDisputedAsync(db, pending);
await EscrowService.ClearDisputeAsync(db, pending);
await using (var cmd = new NpgsqlCommand("SELECT status FROM escrow_transactions WHERE booking_id=@id", db))
{ cmd.Parameters.AddWithValue("@id", pending); Check((string?)await cmd.ExecuteScalarAsync() == "PendingPayment", "Dismissing an unfunded dispute does not invent funds"); }
var checkoutId = "cs_test_" + Guid.NewGuid().ToString("N");
await PaymentLedgerService.UpsertPendingAsync(db, "booking_talent_fee", customer, null, null, null, pending, 500m, "test booking", checkoutId, checkoutId, "Locked");
using var checkout = JsonDocument.Parse(JsonSerializer.Serialize(new { id = checkoutId, attributes = new { payments = new[] { new { id = "pay_test", attributes = new { status = "paid", currency = "PHP", amount = 50000 } } } } }));
await BookingPaymentReconciliationService.ReconcileAsync(db, checkout.RootElement);
await BookingPaymentReconciliationService.ReconcileAsync(db, checkout.RootElement);
await using (var cmd = new NpgsqlCommand("SELECT status FROM escrow_transactions WHERE booking_id=@id", db))
{ cmd.Parameters.AddWithValue("@id", pending); Check((string?)await cmd.ExecuteScalarAsync() == "Held", "Repeated webhook funds escrow without releasing early"); }
var partialPm = await Booking(800m);
await EscrowService.CreatePendingAsync(db, partialPm, customer, talent, 800m);
await EscrowService.MarkHeldAsync(db, partialPm, "pay_test_partial");
var pmOutcome = await EscrowService.SettlePartialAsync(db, partialPm, customer, null, 200m, "partial test");
Check(pmOutcome.Settled && pmOutcome.Provider == "PayMongo", "PayMongo escrow accepts partial settlement into RefundPending");
await using (var cmd = new NpgsqlCommand("SELECT status FROM escrow_transactions WHERE booking_id=@id", db))
{ cmd.Parameters.AddWithValue("@id", partialPm); Check((string?)await cmd.ExecuteScalarAsync() == "RefundPending", "Partial settlement does not release talent prematurely"); }
await EscrowService.ApplyRefundOutcomeAsync(db, partialPm);
await using (var cmd = new NpgsqlCommand("SELECT status FROM escrow_transactions WHERE booking_id=@id", db))
{ cmd.Parameters.AddWithValue("@id", partialPm); Check((string?)await cmd.ExecuteScalarAsync() == "Released", "Refund webhook outcome releases the talent remainder"); }
await using (var cmd = new NpgsqlCommand("SELECT talent_fee_status FROM bookings WHERE id=@id", db))
{ cmd.Parameters.AddWithValue("@id", partialPm); Check((string?)await cmd.ExecuteScalarAsync() == "Partially Refunded", "Partial settlement marks booking partially refunded"); }
await using (var cmd = new NpgsqlCommand("SELECT COUNT(*) FROM escrow_resolutions WHERE escrow_transaction_id=(SELECT id FROM escrow_transactions WHERE booking_id=@id) AND resolution_type='Partial'", db))
{ cmd.Parameters.AddWithValue("@id", partialPm); Check(Convert.ToInt32(await cmd.ExecuteScalarAsync()) == 1, "Partial settlement records escrow_resolutions"); }
var topup2 = await EscrowService.CreateTopUpAsync(db, customer, 1000m);
await EscrowService.ConfirmTopUpAsync(db, topup2, customer, "test-payment-2");
var partialW = await Booking(900m);
await EscrowService.ReserveFromWalletAsync(db, partialW, customer, talent, 900m);
var wOutcome = await EscrowService.SettlePartialAsync(db, partialW, customer, null, 300m, "partial test");
Check(wOutcome.Settled && wOutcome.Provider == "Wallet", "Wallet escrow settles partial refund internally");
var wOutcome2 = await EscrowService.SettlePartialAsync(db, partialW, customer, null, 300m, "partial test");
Check(!wOutcome2.Settled, "Repeated partial settlement refuses an already settled escrow");
Check((await EscrowService.GetWalletSummaryAsync(db, customer)).AvailableBalance == 500m, "Wallet partial refund credits customer wallet");
Check((await EscrowService.GetWalletSummaryAsync(db, talent)).AvailableBalance == 1800m, "Wallet partial settlement credits talent remainder");
var fullRefund = await Booking(400m);
await EscrowService.ReserveFromWalletAsync(db, fullRefund, customer, talent, 400m);
await EscrowService.MarkRefundPendingAsync(db, fullRefund);
await EscrowService.ApplyRefundOutcomeAsync(db, fullRefund);
await using (var cmd = new NpgsqlCommand("SELECT status FROM escrow_transactions WHERE booking_id=@id", db))
{ cmd.Parameters.AddWithValue("@id", fullRefund); Check((string?)await cmd.ExecuteScalarAsync() == "Refunded", "Refund webhook outcome falls back to full refund when no partial resolution exists"); }
Check((await EscrowService.GetWalletSummaryAsync(db, customer)).AvailableBalance == 500m, "Full refund without partial resolution credits full amount once");
foreach (var scenario in new[] { ("paid", "PHP", 50000, true), ("failed", "PHP", 50000, false), ("pending", "PHP", 50000, false), ("paid", "USD", 50000, false), ("paid", "PHP", 49999, false) })
{
 using var payload = JsonDocument.Parse(JsonSerializer.Serialize(new { payments = new[] { new { id = "pay_test", attributes = new { status = scenario.Item1, currency = scenario.Item2, amount = scenario.Item3 } } } }));
 Check(PayMongoPaymentVerification.TryGetPaidPayment(payload.RootElement, 500m, out _) == scenario.Item4, "Payment validation: " + scenario);
}
Check(await EscrowService.MarkRefundPendingAsync(db, pending), "Refund claims held escrow");
Check(!await EscrowService.MarkRefundPendingAsync(db, pending), "Duplicate refund cannot claim escrow twice");
await EscrowService.MarkRefundedAsync(db, pending);
await PaymentLedgerService.MarkRefundedAsync(db, "booking_talent_fee", null, pending, "Refunded");
await BookingPaymentReconciliationService.ReconcileAsync(db, checkout.RootElement);
await using (var cmd = new NpgsqlCommand("SELECT talent_fee_status FROM bookings WHERE id=@id", db))
{ cmd.Parameters.AddWithValue("@id", pending); Check((string?)await cmd.ExecuteScalarAsync() == "Refunded", "Late paid webhook cannot undo refund"); }
await using (var cmd = new NpgsqlCommand("SELECT status FROM payment_records WHERE booking_id=@id", db))
{ cmd.Parameters.AddWithValue("@id", pending); Check((string?)await cmd.ExecuteScalarAsync() == "Refunded", "Late webhook preserves refunded ledger"); }
EscrowService.InitializeReleaseWindow(TimeSpan.FromHours(72));
var tup = await EscrowService.CreateTopUpAsync(db, customer, 500m);
await EscrowService.ConfirmTopUpAsync(db, tup, customer, "test-payment-3");
var windowed = await Booking(500m);
await EscrowService.ReserveFromWalletAsync(db, windowed, customer, talent, 500m);
Check(!await EscrowService.ReleaseAsync(db, windowed), "Confirmation window blocks immediate release before completion");
await Sql("UPDATE bookings SET status='Completed', customer_completed_at=NOW(), target_completed_at=NOW() WHERE id=@id", windowed);
Check(!await EscrowService.ReleaseAsync(db, windowed), "Completed booking stays held inside the confirmation window");
await using (var cmd = new NpgsqlCommand("SELECT status FROM escrow_transactions WHERE booking_id=@id", db))
{ cmd.Parameters.AddWithValue("@id", windowed); Check((string?)await cmd.ExecuteScalarAsync() == "ReleaseReady", "Window transition arms ReleaseReady after both confirmations"); }
await Sql("UPDATE escrow_transactions SET release_ready_at = NOW() - INTERVAL '73 hours' WHERE booking_id=@id", windowed);
Check(await EscrowService.ReleaseAsync(db, windowed), "Release proceeds once the confirmation window elapses");
Check((await EscrowService.GetWalletSummaryAsync(db, talent)).AvailableBalance == 2300m, "Talent is paid once after the window elapses");
Check((await EscrowService.GetWalletSummaryAsync(db, customer)).AvailableBalance == 500m, "Window release leaves the reserved escrow out of the customer wallet");
EscrowService.InitializeReleaseWindow(TimeSpan.Zero);
await ControllerRegressions.RunAsync(connectionString);
Console.WriteLine("All booking payment integration checks passed.");
void Check(bool value, string label) { if (!value) throw new Exception(label); Console.WriteLine("PASS " + label); }
async Task Sql(string sql, Guid? id = null) { await using var cmd = new NpgsqlCommand(sql, db); if (id.HasValue) cmd.Parameters.AddWithValue("@id", id.Value); await cmd.ExecuteNonQueryAsync(); }
async Task<Guid> Booking(decimal amount) { var id = Guid.NewGuid(); await using var cmd = new NpgsqlCommand("INSERT INTO bookings(id,customer_id,target_user_id,budget,status) VALUES(@id,@customer,@talent,@amount,'Confirmed')", db); cmd.Parameters.AddWithValue("@id",id);cmd.Parameters.AddWithValue("@customer",customer);cmd.Parameters.AddWithValue("@talent",talent);cmd.Parameters.AddWithValue("@amount",amount); await cmd.ExecuteNonQueryAsync();return id; }
async Task<bool> Reserve(Guid id) { await using var connection = new NpgsqlConnection(connectionString);await connection.OpenAsync();try {await EscrowService.ReserveFromWalletAsync(connection,id,customer,talent,300m);return true;}catch(InvalidOperationException){return false;} }

