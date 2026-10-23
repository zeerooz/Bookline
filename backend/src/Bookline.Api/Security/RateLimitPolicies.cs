namespace Bookline.Api.Security;

public static class RateLimitPolicies
{
    /// <summary>Credential routes. Tight, because these are guessing targets.</summary>
    public const string Authentication = "authentication";

    /// <summary>Authenticated management traffic.</summary>
    public const string Api = "api";

    /// <summary>
    /// The unauthenticated booking routes. Separate from the others because
    /// anyone on the internet can reach them, and a booking form is a cheap
    /// way to spam a business with junk appointments.
    /// </summary>
    public const string PublicBooking = "public-booking";
}
