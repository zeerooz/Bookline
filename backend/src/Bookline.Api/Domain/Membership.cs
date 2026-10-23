namespace Bookline.Api.Domain;

/// <summary>
/// Binds a user to a tenant with a role. Authorisation asks this table, never
/// a global role list: being an Owner of one business must grant nothing
/// anywhere else.
/// </summary>
public class Membership : ITenantOwned
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid TenantId { get; set; }
    public Tenant? Tenant { get; set; }

    public Guid UserId { get; set; }
    public ApplicationUser? User { get; set; }

    public TenantRole Role { get; set; } = TenantRole.Customer;

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

public enum TenantRole
{
    /// <summary>Books appointments. Cannot see anyone else's.</summary>
    Customer = 0,

    /// <summary>Sees and manages their own calendar.</summary>
    Staff = 1,

    /// <summary>Full control of the business, including billing and other members.</summary>
    Owner = 2
}
