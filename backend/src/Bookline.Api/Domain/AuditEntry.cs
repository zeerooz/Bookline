namespace Bookline.Api.Domain;

/// <summary>
/// Who did what, to which record, and when. Scoped per tenant so a business
/// can answer "who cancelled that booking" without seeing anyone else's data.
/// </summary>
public class AuditEntry : ITenantOwned
{
    public long Id { get; set; }

    public Guid TenantId { get; set; }

    public Guid? ActorUserId { get; set; }
    public string? ActorEmail { get; set; }

    public string Action { get; set; } = string.Empty;
    public string EntityType { get; set; } = string.Empty;
    public string? EntityId { get; set; }

    /// <summary>Short human-readable context. Never credentials or card data.</summary>
    public string? Detail { get; set; }

    public string? IpAddress { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
