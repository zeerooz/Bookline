namespace Bookline.Api.Domain;

/// <summary>
/// Marks an entity that belongs to a tenant. The DbContext applies a global
/// query filter to every type implementing this, so a query that forgets to
/// filter by tenant still cannot read another tenant's rows. Opting in by
/// interface rather than per-entity means a new entity is protected the moment
/// it is added, which is the failure mode worth designing against.
/// </summary>
public interface ITenantOwned
{
    Guid TenantId { get; set; }
}
