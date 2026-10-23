namespace Bookline.Api.Services.Payments;

public sealed record CheckoutRequest(
    Guid AppointmentId,
    string ManagementToken,
    int AmountMinorUnits,
    string Currency,
    string Description,
    string CustomerEmail);

public sealed record CheckoutSession(string Reference, string? RedirectUrl);

/// <summary>Result of interpreting a provider callback.</summary>
public sealed record PaymentEvent(string Reference, bool Succeeded, string? FailureReason);

/// <summary>
/// A payment gateway.
///
/// The interface exists because the gateway is the part of this system most
/// likely to be swapped: Stripe is the sensible default, Paymob is what an
/// Egyptian business will actually ask for, and neither should reach into the
/// booking logic. Card details never pass through here — the provider hosts
/// the form and we store an identifier — which is what keeps the platform out
/// of PCI scope.
/// </summary>
public interface IPaymentProvider
{
    /// <summary>Short name stored on the payment row, e.g. "stripe".</summary>
    string Name { get; }

    Task<CheckoutSession> CreateCheckoutAsync(CheckoutRequest request, CancellationToken ct);

    /// <summary>
    /// Verifies and parses a provider callback. Returns null when the payload
    /// is not something this provider recognises or cannot authenticate —
    /// never throw on untrusted input arriving at a public endpoint.
    /// </summary>
    Task<PaymentEvent?> ParseWebhookAsync(string payload, string? signature, CancellationToken ct);
}
