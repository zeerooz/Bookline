namespace Bookline.Api.Domain;

/// <summary>
/// A booked slot. Times are UTC instants; the tenant's time zone is applied
/// only when presenting them or when computing availability from weekly hours.
/// </summary>
public class Appointment : ITenantOwned
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid TenantId { get; set; }
    public Tenant? Tenant { get; set; }

    public Guid ServiceId { get; set; }
    public Service? Service { get; set; }

    public Guid StaffMemberId { get; set; }
    public StaffMember? StaffMember { get; set; }

    /// <summary>Null for a guest booking made without an account.</summary>
    public Guid? CustomerUserId { get; set; }
    public ApplicationUser? CustomerUser { get; set; }

    public string CustomerName { get; set; } = string.Empty;
    public string CustomerEmail { get; set; } = string.Empty;
    public string? CustomerPhone { get; set; }

    public DateTime StartsAtUtc { get; set; }

    /// <summary>
    /// Excludes the service buffer. <see cref="BlockedUntilUtc"/> is the end of
    /// the footprint that blocks other bookings.
    /// </summary>
    public DateTime EndsAtUtc { get; set; }

    public DateTime BlockedUntilUtc { get; set; }

    public AppointmentStatus Status { get; set; } = AppointmentStatus.Confirmed;

    /// <summary>
    /// Snapshot of the price at the time of booking. Re-reading the service
    /// later would silently rewrite history every time a price changes.
    /// </summary>
    public int PriceMinorUnits { get; set; }
    public string Currency { get; set; } = "EGP";

    public string? Notes { get; set; }

    /// <summary>Unguessable token that lets a guest manage their own booking.</summary>
    public string ManagementToken { get; set; } = string.Empty;

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? CancelledAt { get; set; }
    public string? CancellationReason { get; set; }

    /// <summary>Set once a reminder has gone out, so it cannot be sent twice.</summary>
    public DateTime? ReminderSentAt { get; set; }

    /// <summary>Optimistic concurrency token, mapped to the Postgres xmin system column.</summary>
    public uint Version { get; set; }

    public Payment? Payment { get; set; }

    public bool OccupiesTime =>
        Status is AppointmentStatus.Confirmed or AppointmentStatus.PendingPayment or AppointmentStatus.Completed;
}

public enum AppointmentStatus
{
    /// <summary>Holding the slot while prepayment is completed.</summary>
    PendingPayment = 0,
    Confirmed = 1,
    Cancelled = 2,
    Completed = 3,
    NoShow = 4
}
