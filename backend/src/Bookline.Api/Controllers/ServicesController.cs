using Bookline.Api.Data;
using Bookline.Api.Domain;
using Bookline.Api.Dtos;
using Bookline.Api.Security;
using Bookline.Api.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;

namespace Bookline.Api.Controllers;

[ApiController]
[Route("api/t/{tenantSlug}/services")]
[Authorize(Policy = Policies.TenantStaff)]
[EnableRateLimiting(RateLimitPolicies.Api)]
public class ServicesController(AppDbContext db, IAuditService audit) : ControllerBase
{
    [HttpGet]
    [ProducesResponseType<IReadOnlyList<ServiceResponse>>(StatusCodes.Status200OK)]
    public async Task<ActionResult<IReadOnlyList<ServiceResponse>>> GetAll(CancellationToken ct) =>
        Ok(await db.Services
            .AsNoTracking()
            .OrderBy(s => s.Name)
            .Select(s => new ServiceResponse(
                s.Id, s.Name, s.Description, s.DurationMinutes, s.BufferMinutes,
                s.PriceMinorUnits, s.RequiresPrepayment, s.IsActive))
            .ToListAsync(ct));

    [HttpPost]
    [Authorize(Policy = Policies.TenantOwner)]
    [ProducesResponseType<ServiceResponse>(StatusCodes.Status201Created)]
    public async Task<IActionResult> Create(ServiceRequest request, CancellationToken ct)
    {
        var service = new Service
        {
            Name = request.Name.Trim(),
            Description = request.Description?.Trim(),
            DurationMinutes = request.DurationMinutes,
            BufferMinutes = request.BufferMinutes,
            PriceMinorUnits = request.PriceMinorUnits,
            RequiresPrepayment = request.RequiresPrepayment,
            IsActive = request.IsActive,
        };

        db.Services.Add(service);

        if (!await AssignStaffAsync(service, request.StaffMemberIds, ct))
        {
            return Problem("One of those team members does not belong to this business.",
                statusCode: StatusCodes.Status400BadRequest);
        }

        audit.Record("service.created", nameof(Service), service.Id.ToString(), service.Name);
        await db.SaveChangesAsync(ct);

        return Created($"/api/t/{RouteData.Values["tenantSlug"]}/services/{service.Id}",
            ToResponse(service, request.StaffMemberIds));
    }

    [HttpPut("{id:guid}")]
    [Authorize(Policy = Policies.TenantOwner)]
    [ProducesResponseType<ServiceResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Update(Guid id, ServiceRequest request, CancellationToken ct)
    {
        // The query filter means another tenant's id simply is not found here.
        var service = await db.Services
            .Include(s => s.StaffServices)
            .FirstOrDefaultAsync(s => s.Id == id, ct);

        if (service is null)
        {
            return NotFound();
        }

        service.Name = request.Name.Trim();
        service.Description = request.Description?.Trim();
        service.DurationMinutes = request.DurationMinutes;
        service.BufferMinutes = request.BufferMinutes;
        service.PriceMinorUnits = request.PriceMinorUnits;
        service.RequiresPrepayment = request.RequiresPrepayment;
        service.IsActive = request.IsActive;

        db.StaffServices.RemoveRange(service.StaffServices);

        if (!await AssignStaffAsync(service, request.StaffMemberIds, ct))
        {
            return Problem("One of those team members does not belong to this business.",
                statusCode: StatusCodes.Status400BadRequest);
        }

        audit.Record("service.updated", nameof(Service), service.Id.ToString(), service.Name);
        await db.SaveChangesAsync(ct);

        return Ok(ToResponse(service, request.StaffMemberIds));
    }

    /// <summary>
    /// Deactivates rather than deletes. Past appointments reference this
    /// service, and a business asking "what did we earn on haircuts last year"
    /// needs those rows to still make sense.
    /// </summary>
    [HttpDelete("{id:guid}")]
    [Authorize(Policy = Policies.TenantOwner)]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Deactivate(Guid id, CancellationToken ct)
    {
        var service = await db.Services.FirstOrDefaultAsync(s => s.Id == id, ct);

        if (service is null)
        {
            return NotFound();
        }

        service.IsActive = false;
        audit.Record("service.deactivated", nameof(Service), service.Id.ToString(), service.Name);
        await db.SaveChangesAsync(ct);

        return NoContent();
    }

    private async Task<bool> AssignStaffAsync(Service service, List<Guid> staffIds, CancellationToken ct)
    {
        if (staffIds.Count == 0)
        {
            return true;
        }

        var distinct = staffIds.Distinct().ToList();

        // Filtered to this tenant, so a foreign staff id comes back short and
        // the request is rejected rather than silently linking nothing.
        var valid = await db.StaffMembers
            .Where(s => distinct.Contains(s.Id))
            .Select(s => s.Id)
            .ToListAsync(ct);

        if (valid.Count != distinct.Count)
        {
            return false;
        }

        foreach (var staffId in valid)
        {
            db.StaffServices.Add(new StaffService { ServiceId = service.Id, StaffMemberId = staffId });
        }

        return true;
    }

    private static ServiceResponse ToResponse(Service s, List<Guid> _) =>
        new(s.Id, s.Name, s.Description, s.DurationMinutes, s.BufferMinutes,
            s.PriceMinorUnits, s.RequiresPrepayment, s.IsActive);
}
