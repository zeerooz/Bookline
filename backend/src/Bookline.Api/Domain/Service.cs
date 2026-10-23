namespace Bookline.Api.Domain;

/// <summary>Something customers can book: a haircut, a consultation, a class.</summary>
public class Service : ITenantOwned
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid TenantId { get; set; }
    public Tenant? Tenant { get; set; }

    public string Name { get; set; } = string.Empty;
    public string? Description { get; set; }

    public int DurationMinutes { get; set; } = 30;

    /// <summary>
    /// Padding after the appointment that cannot be booked — cleaning a room,
    /// writing notes. Part of the slot's footprint but not of its duration.
    /// </summary>
    public int BufferMinutes { get; set; }

    /// <summary>
    /// Price in the tenant's currency, in minor units (piastres, cents).
    /// Integers, never decimal or double: money arithmetic in floating point
    /// is how rounding bugs reach invoices.
    /// </summary>
    public int PriceMinorUnits { get; set; }

    /// <summary>When true, the slot is only held once payment succeeds.</summary>
    public bool RequiresPrepayment { get; set; }

    public bool IsActive { get; set; } = true;

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public List<StaffService> StaffServices { get; set; } = [];
}
