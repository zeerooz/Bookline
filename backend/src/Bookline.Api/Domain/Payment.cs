namespace Bookline.Api.Domain;

/// <summary>
/// A payment attempt against an appointment. The platform stores identifiers
/// and amounts only — card details never touch this system, which is what keeps
/// it out of PCI scope.
/// </summary>
public class Payment : ITenantOwned
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid TenantId { get; set; }

    public Guid AppointmentId { get; set; }
    public Appointment? Appointment { get; set; }

    public int AmountMinorUnits { get; set; }
    public string Currency { get; set; } = "EGP";

    public PaymentStatus Status { get; set; } = PaymentStatus.Pending;

    /// <summary>"stripe" or "fake". Named so a third provider can be added without a migration.</summary>
    public string Provider { get; set; } = string.Empty;

    /// <summary>The provider's own identifier, used to reconcile webhooks.</summary>
    public string? ProviderReference { get; set; }

    public string? FailureReason { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? CompletedAt { get; set; }
}

public enum PaymentStatus
{
    Pending = 0,
    Succeeded = 1,
    Failed = 2,
    Refunded = 3
}
