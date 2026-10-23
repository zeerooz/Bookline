using Bookline.Api.Data;
using Bookline.Api.Domain;
using Bookline.Api.Security;

namespace Bookline.Api.Services;

public interface IAuditService
{
    /// <summary>Queues an audit row. The caller owns the SaveChanges that commits it.</summary>
    void Record(string action, string entityType, string? entityId, string? detail = null);
}

public class AuditService(AppDbContext db, ITenantContext tenant, ICurrentUser user, IHttpContextAccessor accessor)
    : IAuditService
{
    public void Record(string action, string entityType, string? entityId, string? detail = null)
    {
        if (!tenant.HasTenant)
        {
            return;
        }

        db.AuditEntries.Add(new AuditEntry
        {
            TenantId = tenant.FilterValue,
            ActorUserId = user.UserId,
            ActorEmail = user.Email,
            Action = action,
            EntityType = entityType,
            EntityId = entityId,
            Detail = Truncate(detail, 1000),
            IpAddress = Truncate(accessor.HttpContext?.Connection.RemoteIpAddress?.ToString(), 64),
        });
    }

    private static string? Truncate(string? value, int max) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Length <= max ? value : value[..max];
}
