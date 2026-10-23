using System.Security.Cryptography;
using System.Text.Json;

namespace Bookline.Api.Services.Payments;

/// <summary>
/// A gateway that always succeeds, used when no Stripe keys are configured.
///
/// It exists so the project runs end to end — including the prepayment path —
/// immediately after cloning, with no account to create and no keys to paste.
/// Reviewers see the whole flow; the Stripe adapter proves it works against a
/// real provider.
/// </summary>
public class FakePaymentProvider(ILogger<FakePaymentProvider> logger) : IPaymentProvider
{
    public string Name => "fake";

    public Task<CheckoutSession> CreateCheckoutAsync(CheckoutRequest request, CancellationToken ct)
    {
        var reference = $"fake_{Convert.ToHexString(RandomNumberGenerator.GetBytes(12)).ToLowerInvariant()}";

        logger.LogInformation(
            "Fake checkout {Reference} for appointment {AppointmentId}: {Amount} {Currency}",
            reference, request.AppointmentId, request.AmountMinorUnits, request.Currency);

        // A URL the local web app understands, standing in for a hosted
        // checkout page. Confirming it posts back to the webhook endpoint.
        var redirect = $"/checkout/simulate?reference={reference}&token={request.ManagementToken}";

        return Task.FromResult(new CheckoutSession(reference, redirect));
    }

    public Task<PaymentEvent?> ParseWebhookAsync(string payload, string? signature, CancellationToken ct)
    {
        // No signature to verify: this provider is only ever enabled in
        // development, and the webhook route says so plainly.
        try
        {
            using var document = JsonDocument.Parse(payload);
            var root = document.RootElement;

            if (!root.TryGetProperty("reference", out var reference))
            {
                return Task.FromResult<PaymentEvent?>(null);
            }

            var succeeded = !root.TryGetProperty("succeeded", out var flag) || flag.GetBoolean();

            return Task.FromResult<PaymentEvent?>(new PaymentEvent(
                reference.GetString() ?? string.Empty,
                succeeded,
                succeeded ? null : "Declined in the payment simulator."));
        }
        catch (JsonException)
        {
            return Task.FromResult<PaymentEvent?>(null);
        }
    }
}
