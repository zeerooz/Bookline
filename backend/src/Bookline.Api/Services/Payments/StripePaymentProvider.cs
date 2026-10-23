using Bookline.Api.Security;
using Microsoft.Extensions.Options;
using Stripe;
using Stripe.Checkout;

namespace Bookline.Api.Services.Payments;

/// <summary>
/// Stripe Checkout in test mode.
///
/// The customer is sent to a page Stripe hosts, so no card data ever reaches
/// this server. What comes back is a session id and, later, a signed webhook.
/// The signature check is the important part: the webhook endpoint is public,
/// and without verification anyone could mark any appointment as paid.
/// </summary>
public class StripePaymentProvider : IPaymentProvider
{
    private readonly PaymentOptions _options;
    private readonly ILogger<StripePaymentProvider> _logger;

    public StripePaymentProvider(IOptions<PaymentOptions> options, ILogger<StripePaymentProvider> logger)
    {
        _options = options.Value;
        _logger = logger;

        StripeConfiguration.ApiKey = _options.StripeSecretKey;
    }

    public string Name => "stripe";

    public async Task<CheckoutSession> CreateCheckoutAsync(CheckoutRequest request, CancellationToken ct)
    {
        var options = new SessionCreateOptions
        {
            Mode = "payment",
            CustomerEmail = request.CustomerEmail,
            SuccessUrl = _options.SuccessUrl.Replace("{token}", request.ManagementToken),
            CancelUrl = _options.CancelUrl.Replace("{token}", request.ManagementToken),
            LineItems =
            [
                new SessionLineItemOptions
                {
                    Quantity = 1,
                    PriceData = new SessionLineItemPriceDataOptions
                    {
                        Currency = request.Currency.ToLowerInvariant(),
                        UnitAmount = request.AmountMinorUnits,
                        ProductData = new SessionLineItemPriceDataProductDataOptions
                        {
                            Name = request.Description,
                        },
                    },
                },
            ],
            // Carried back on the webhook so the payment can be matched to an
            // appointment without trusting anything in the redirect URL.
            Metadata = new Dictionary<string, string>
            {
                ["appointmentId"] = request.AppointmentId.ToString(),
            },
        };

        var session = await new SessionService().CreateAsync(options, cancellationToken: ct);

        return new CheckoutSession(session.Id, session.Url);
    }

    public Task<PaymentEvent?> ParseWebhookAsync(string payload, string? signature, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(_options.StripeWebhookSecret))
        {
            _logger.LogError("A Stripe webhook arrived but no webhook secret is configured; refusing to trust it.");
            return Task.FromResult<PaymentEvent?>(null);
        }

        Event stripeEvent;

        try
        {
            // Throws when the signature does not match the payload, which is
            // exactly the forgery case this endpoint has to survive.
            stripeEvent = EventUtility.ConstructEvent(payload, signature, _options.StripeWebhookSecret);
        }
        catch (StripeException ex)
        {
            _logger.LogWarning(ex, "Rejected a Stripe webhook with an invalid signature.");
            return Task.FromResult<PaymentEvent?>(null);
        }

        return Task.FromResult(Interpret(stripeEvent));
    }

    private static PaymentEvent? Interpret(Event stripeEvent) => stripeEvent.Type switch
    {
        "checkout.session.completed" when stripeEvent.Data.Object is Session session =>
            new PaymentEvent(session.Id, session.PaymentStatus == "paid", null),

        "checkout.session.async_payment_failed" when stripeEvent.Data.Object is Session failed =>
            new PaymentEvent(failed.Id, false, "The payment was declined."),

        "checkout.session.expired" when stripeEvent.Data.Object is Session expired =>
            new PaymentEvent(expired.Id, false, "The checkout session expired."),

        // Everything else is a Stripe event this application has no opinion
        // about. Ignoring it is correct; erroring would make Stripe retry.
        _ => null,
    };
}
