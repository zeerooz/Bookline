namespace Bookline.Api.Domain;

/// <summary>
/// A recurring weekly window during which a business accepts bookings. Stored
/// as local wall-clock minutes from midnight in the tenant's time zone, not as
/// instants: "we open at 09:00" must stay true across a daylight-saving change.
/// </summary>
public class BusinessHour : ITenantOwned
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid TenantId { get; set; }
    public Tenant? Tenant { get; set; }

    /// <summary>Null means it applies to the whole business.</summary>
    public Guid? StaffMemberId { get; set; }
    public StaffMember? StaffMember { get; set; }

    public DayOfWeek DayOfWeek { get; set; }

    public int StartMinute { get; set; }
    public int EndMinute { get; set; }
}

/// <summary>A one-off absence: holiday, sick day, a blocked afternoon.</summary>
public class TimeOff : ITenantOwned
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid TenantId { get; set; }

    public Guid StaffMemberId { get; set; }
    public StaffMember? StaffMember { get; set; }

    /// <summary>Stored in UTC, because an absence is a real interval in time.</summary>
    public DateTime StartsAtUtc { get; set; }
    public DateTime EndsAtUtc { get; set; }

    public string? Reason { get; set; }
}
