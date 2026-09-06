using System.Text.Json;

namespace ImajinationAPI.Services;

public static class PayMongoPaymentVerification
{
    // Payment existence alone is not proof of capture. Match the stored order total.
    public static bool TryGetPaidPayment(JsonElement attributes, decimal expectedAmount, out JsonElement payment)
    {
        payment = default;
        if (expectedAmount <= 0 || expectedAmount * 100 != decimal.Truncate(expectedAmount * 100) ||
            !attributes.TryGetProperty("payments", out var payments) || payments.ValueKind != JsonValueKind.Array)
            return false;
        foreach (var candidate in payments.EnumerateArray())
        {
            if (!candidate.TryGetProperty("id", out var id) || id.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(id.GetString()) ||
                !candidate.TryGetProperty("attributes", out var details) || details.ValueKind != JsonValueKind.Object ||
                !details.TryGetProperty("status", out var status) || status.GetString() != "paid" ||
                !details.TryGetProperty("currency", out var currency) || currency.GetString() != "PHP" ||
                !details.TryGetProperty("amount", out var amount) || !amount.TryGetDecimal(out var centavos) || centavos != expectedAmount * 100)
                continue;
            payment = candidate;
            return true;
        }
        return false;
    }
}
