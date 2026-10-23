using Bookline.Api.Data;
using Bookline.Api.Domain;
using Bookline.Api.Dtos;
using Bookline.Api.Security;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;

namespace Bookline.Api.Controllers;

/// <summary>
/// Creating a business is the one write that cannot be tenant-scoped, because
/// the tenant does not exist yet. Everything after this point goes through
/// /api/t/{tenantSlug}.
/// </summary>
[ApiController]
[Route("api/businesses")]
[Authorize]
[EnableRateLimiting(RateLimitPolicies.Api)]
public class BusinessesController(AppDbContext db, ICurrentUser currentUser) : ControllerBase
{
    [HttpPost]
    [ProducesResponseType<TenantSummary>(StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> Create(CreateBusinessRequest request, CancellationToken ct)
    {
        var slug = request.Slug.Trim().ToLowerInvariant();

        if (await db.Tenants.AnyAsync(t => t.Slug == slug, ct))
        {
            return Problem("That address is already taken. Pick another.", statusCode: StatusCodes.Status409Conflict);
        }

        if (!IsKnownTimeZone(request.TimeZoneId))
        {
            return Problem($"'{request.TimeZoneId}' is not a time zone this server recognises.",
                statusCode: StatusCodes.Status400BadRequest);
        }

        var tenant = new Tenant
        {
            Name = request.Name.Trim(),
            Slug = slug,
            TimeZoneId = request.TimeZoneId,
            Currency = request.Currency.ToUpperInvariant(),
            BookingHorizonDays = request.BookingHorizonDays,
            MinimumNoticeMinutes = request.MinimumNoticeMinutes,
        };

        db.Tenants.Add(tenant);

        // The creator becomes the Owner. TenantId is set explicitly because no
        // tenant is resolved on this route, so nothing can stamp it.
        db.Memberships.Add(new Membership
        {
            TenantId = tenant.Id,
            UserId = currentUser.RequireUserId(),
            Role = TenantRole.Owner,
        });

        await db.SaveChangesAsync(ct);

        var summary = new TenantSummary(
            tenant.Id, tenant.Name, tenant.Slug, tenant.TimeZoneId, tenant.Currency, tenant.BookingHorizonDays);

        return Created($"/api/t/{tenant.Slug}", summary);
    }

    private static bool IsKnownTimeZone(string id)
    {
        try
        {
            TimeZoneInfo.FindSystemTimeZoneById(id);
            return true;
        }
        catch (Exception ex) when (ex is TimeZoneNotFoundException or InvalidTimeZoneException)
        {
            return false;
        }
    }
}
