namespace Bookline.Api.Security;

/// <summary>
/// The tenant the current request is acting on. Resolved once by middleware and
/// read by the DbContext, so no query has to remember to filter by tenant.
/// </summary>
public interface ITenantContext
{
    Guid? TenantId { get; }
    string? Slug { get; }

    bool HasTenant { get; }

    /// <summary>
    /// The value the global query filter compares against. Returns
    /// <see cref="Guid.Empty"/> when no tenant is resolved, so an unscoped
    /// request matches nothing rather than everything. Failing closed matters:
    /// the alternative leaks every tenant's data on a routing mistake.
    /// </summary>
    Guid FilterValue { get; }

    void Set(Guid tenantId, string slug);

    /// <summary>
    /// Lifts tenant scoping for genuinely cross-tenant work: the background
    /// notification worker, and resolving a tenant by slug before one is known.
    /// Deliberately explicit and deliberately awkward to call.
    /// </summary>
    IDisposable BypassForBackgroundWork();

    bool IsBypassed { get; }
}

public class TenantContext : ITenantContext
{
    public Guid? TenantId { get; private set; }
    public string? Slug { get; private set; }

    public bool HasTenant => TenantId.HasValue;
    public bool IsBypassed { get; private set; }

    public Guid FilterValue => TenantId ?? Guid.Empty;

    public void Set(Guid tenantId, string slug)
    {
        TenantId = tenantId;
        Slug = slug;
    }

    public IDisposable BypassForBackgroundWork()
    {
        IsBypassed = true;
        return new BypassScope(this);
    }

    private sealed class BypassScope(TenantContext context) : IDisposable
    {
        public void Dispose() => context.IsBypassed = false;
    }
}
