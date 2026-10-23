namespace Bookline.Api.Domain;

/// <summary>
/// Someone who delivers services. Linked to a user account when that person can
/// sign in; a staff member can also exist without one, for a business that
/// manages a colleague's calendar on their behalf.
/// </summary>
public class StaffMember : ITenantOwned
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid TenantId { get; set; }
    public Tenant? Tenant { get; set; }

    public Guid? UserId { get; set; }
    public ApplicationUser? User { get; set; }

    public string DisplayName { get; set; } = string.Empty;
    public string? Title { get; set; }

    public bool IsActive { get; set; } = true;

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public List<StaffService> StaffServices { get; set; } = [];
    public List<TimeOff> TimeOff { get; set; } = [];
}

/// <summary>Which staff member can deliver which service.</summary>
public class StaffService : ITenantOwned
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid TenantId { get; set; }

    public Guid StaffMemberId { get; set; }
    public StaffMember? StaffMember { get; set; }

    public Guid ServiceId { get; set; }
    public Service? Service { get; set; }
}
