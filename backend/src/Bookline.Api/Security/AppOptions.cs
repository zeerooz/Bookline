using System.ComponentModel.DataAnnotations;

namespace Bookline.Api.Security;

public class JwtOptions
{
    public const string SectionName = "Jwt";

    [Required, MinLength(32)]
    public string SigningKey { get; set; } = string.Empty;

    [Required] public string Issuer { get; set; } = "bookline";
    [Required] public string Audience { get; set; } = "bookline-web";

    public int AccessTokenMinutes { get; set; } = 60;
}

public class PaymentOptions
{
    public const string SectionName = "Payments";

    /// <summary>"stripe" or "fake". Defaults to fake so the app runs with no keys at all.</summary>
    public string Provider { get; set; } = "fake";

    public string? StripeSecretKey { get; set; }
    public string? StripeWebhookSecret { get; set; }

    /// <summary>Where Stripe sends the customer back after checkout.</summary>
    public string SuccessUrl { get; set; } = "http://localhost:8081/booking/{token}";
    public string CancelUrl { get; set; } = "http://localhost:8081/booking/{token}?cancelled=1";
}

public class NotificationOptions
{
    public const string SectionName = "Notifications";

    /// <summary>"log" writes to the logger; "smtp" actually sends.</summary>
    public string EmailProvider { get; set; } = "log";

    public string FromAddress { get; set; } = "no-reply@bookline.app";
    public string? SmtpHost { get; set; }
    public int SmtpPort { get; set; } = 587;
    public string? SmtpUser { get; set; }
    public string? SmtpPassword { get; set; }

    /// <summary>How long before an appointment the reminder is queued for.</summary>
    public int ReminderLeadMinutes { get; set; } = 1440;

    public int DispatchIntervalSeconds { get; set; } = 15;
    public int MaxAttempts { get; set; } = 5;
}

public class CorsOptions
{
    public const string SectionName = "Cors";
    public string[] AllowedOrigins { get; set; } = [];
}
