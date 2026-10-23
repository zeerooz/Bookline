using Bookline.Api.Data;
using Bookline.Api.Security;
using Microsoft.EntityFrameworkCore;

namespace Bookline.Api.Middleware;

/// <summary>
/// Reads {tenantSlug} from the matched route and resolves it to a tenant id
/// before anything else runs.
///
/// It must sit after UseRouting (so route values exist) and before
/// authorization (so policies can see the tenant). An unknown or inactive slug
/// is rejected here rather than deeper in, which keeps every controller free of
/// the question.
/// </summary>
public class TenantResolutionMiddleware(RequestDelegate next, ILogger<TenantResolutionMiddleware> logger)
{
    public const string RouteKey = "tenantSlug";

    public async Task InvokeAsync(HttpContext context, AppDbContext db, ITenantContext tenant)
    {
        var slug = context.GetRouteValue(RouteKey)?.ToString();

        if (string.IsNullOrWhiteSpace(slug))
        {
            await next(context);
            return;
        }

        slug = slug.Trim().ToLowerInvariant();

        // Tenant is not itself tenant-owned, so this lookup is unfiltered by design.
        var record = await db.Tenants
            .AsNoTracking()
            .Where(t => t.Slug == slug && t.IsActive)
            .Select(t => new { t.Id, t.Slug })
            .FirstOrDefaultAsync(context.RequestAborted);

        if (record is null)
        {
            logger.LogInformation("Rejected request for unknown tenant slug {Slug}", slug);

            context.Response.StatusCode = StatusCodes.Status404NotFound;
            await context.Response.WriteAsJsonAsync(new { title = "Business not found", status = 404 });
            return;
        }

        tenant.Set(record.Id, record.Slug);

        await next(context);
    }
}
