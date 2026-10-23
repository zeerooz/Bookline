using Bookline.Api.Data;
using Microsoft.AspNetCore.Authorization;
using Microsoft.EntityFrameworkCore;

namespace Bookline.Api.Security;

public class TenantRoleHandler(AppDbContext db, ITenantContext tenant, ICurrentUser currentUser)
    : AuthorizationHandler<TenantRoleRequirement>
{
    protected override async Task HandleRequirementAsync(
        AuthorizationHandlerContext context,
        TenantRoleRequirement requirement)
    {
        var userId = currentUser.UserId;

        if (userId is null || !tenant.HasTenant)
        {
            return;
        }

        // The query filter already restricts this to the resolved tenant, so a
        // membership of a different business cannot satisfy the requirement.
        var role = await db.Memberships
            .AsNoTracking()
            .Where(m => m.UserId == userId.Value)
            .Select(m => (Domain.TenantRole?)m.Role)
            .FirstOrDefaultAsync();

        if (role is not null && role >= requirement.Minimum)
        {
            context.Succeed(requirement);
        }
    }
}
