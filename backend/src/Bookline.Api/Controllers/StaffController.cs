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
[Route("api/t/{tenantSlug}/staff")]
[Authorize(Policy = Policies.TenantStaff)]
[EnableRateLimiting(RateLimitPolicies.Api)]
public class StaffController(AppDbContext db, IAuditService audit) : ControllerBase
{
    [HttpGet]
    [ProducesResponseType<IReadOnlyList<StaffMemberResponse>>(StatusCodes.Status200OK)]
    public async Task<ActionResult<IReadOnlyList<StaffMemberResponse>>> GetAll(CancellationToken ct) =>
        Ok(await db.StaffMembers
            .AsNoTracking()
            .OrderBy(s => s.DisplayName)
            .Select(s => new StaffMemberResponse(
                s.Id, s.DisplayName, s.Title, s.IsActive,
                s.StaffServices.Select(ss => ss.ServiceId).ToList()))
            .ToListAsync(ct));

    [HttpPost]
    [Authorize(Policy = Policies.TenantOwner)]
    [ProducesResponseType<StaffMemberResponse>(StatusCodes.Status201Created)]
    public async Task<IActionResult> Create(StaffMemberRequest request, CancellationToken ct)
    {
        var staff = new StaffMember
        {
            DisplayName = request.DisplayName.Trim(),
            Title = request.Title?.Trim(),
            IsActive = request.IsActive,
        };

        db.StaffMembers.Add(staff);

        if (!await AssignServicesAsync(staff, request.ServiceIds, ct))
        {
            return Problem("One of those services does not belong to this business.",
                statusCode: StatusCodes.Status400BadRequest);
        }

        audit.Record("staff.created", nameof(StaffMember), staff.Id.ToString(), staff.DisplayName);
        await db.SaveChangesAsync(ct);

        return Created($"/api/t/{RouteData.Values["tenantSlug"]}/staff/{staff.Id}",
            new StaffMemberResponse(staff.Id, staff.DisplayName, staff.Title, staff.IsActive, request.ServiceIds));
    }

    [HttpPut("{id:guid}")]
    [Authorize(Policy = Policies.TenantOwner)]
    [ProducesResponseType<StaffMemberResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Update(Guid id, StaffMemberRequest request, CancellationToken ct)
    {
        var staff = await db.StaffMembers
            .Include(s => s.StaffServices)
            .FirstOrDefaultAsync(s => s.Id == id, ct);

        if (staff is null)
        {
            return NotFound();
        }

        staff.DisplayName = request.DisplayName.Trim();
        staff.Title = request.Title?.Trim();
        staff.IsActive = request.IsActive;

        db.StaffServices.RemoveRange(staff.StaffServices);

        if (!await AssignServicesAsync(staff, request.ServiceIds, ct))
        {
            return Problem("One of those services does not belong to this business.",
                statusCode: StatusCodes.Status400BadRequest);
        }

        audit.Record("staff.updated", nameof(StaffMember), staff.Id.ToString(), staff.DisplayName);
        await db.SaveChangesAsync(ct);

        return Ok(new StaffMemberResponse(staff.Id, staff.DisplayName, staff.Title, staff.IsActive, request.ServiceIds));
    }

    /// <summary>
    /// Deactivates. Future appointments already on this person's calendar are
    /// left alone on purpose — silently cancelling a customer's booking because
    /// an employee left is the wrong default; the business should decide.
    /// </summary>
    [HttpDelete("{id:guid}")]
    [Authorize(Policy = Policies.TenantOwner)]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public async Task<IActionResult> Deactivate(Guid id, CancellationToken ct)
    {
        var staff = await db.StaffMembers.FirstOrDefaultAsync(s => s.Id == id, ct);

        if (staff is null)
        {
            return NotFound();
        }

        staff.IsActive = false;
        audit.Record("staff.deactivated", nameof(StaffMember), staff.Id.ToString(), staff.DisplayName);
        await db.SaveChangesAsync(ct);

        return NoContent();
    }

    // ---- Opening hours ----------------------------------------------------

    [HttpGet("/api/t/{tenantSlug}/hours")]
    [ProducesResponseType<IReadOnlyList<BusinessHourResponse>>(StatusCodes.Status200OK)]
    public async Task<ActionResult<IReadOnlyList<BusinessHourResponse>>> GetHours(CancellationToken ct) =>
        Ok(await db.BusinessHours
            .AsNoTracking()
            .OrderBy(h => h.DayOfWeek).ThenBy(h => h.StartMinute)
            .Select(h => new BusinessHourResponse(h.Id, (int)h.DayOfWeek, h.StartMinute, h.EndMinute, h.StaffMemberId))
            .ToListAsync(ct));

    /// <summary>
    /// Replaces the whole schedule in one call. A weekly timetable is edited as
    /// a unit, and sending it whole avoids the half-applied states that
    /// per-row editing produces when a request fails midway.
    /// </summary>
    [HttpPut("/api/t/{tenantSlug}/hours")]
    [Authorize(Policy = Policies.TenantOwner)]
    [ProducesResponseType<IReadOnlyList<BusinessHourResponse>>(StatusCodes.Status200OK)]
    public async Task<IActionResult> ReplaceHours(List<BusinessHourRequest> request, CancellationToken ct)
    {
        foreach (var window in request)
        {
            if (window.EndMinute <= window.StartMinute)
            {
                return Problem("A closing time must come after its opening time.",
                    statusCode: StatusCodes.Status400BadRequest);
            }
        }

        var staffIds = request.Where(h => h.StaffMemberId is not null)
            .Select(h => h.StaffMemberId!.Value)
            .Distinct()
            .ToList();

        if (staffIds.Count > 0)
        {
            var valid = await db.StaffMembers.CountAsync(s => staffIds.Contains(s.Id), ct);

            if (valid != staffIds.Count)
            {
                return Problem("One of those team members does not belong to this business.",
                    statusCode: StatusCodes.Status400BadRequest);
            }
        }

        db.BusinessHours.RemoveRange(await db.BusinessHours.ToListAsync(ct));

        foreach (var window in request)
        {
            db.BusinessHours.Add(new BusinessHour
            {
                DayOfWeek = (DayOfWeek)window.DayOfWeek,
                StartMinute = window.StartMinute,
                EndMinute = window.EndMinute,
                StaffMemberId = window.StaffMemberId,
            });
        }

        audit.Record("hours.replaced", nameof(BusinessHour), null, $"{request.Count} window(s)");
        await db.SaveChangesAsync(ct);

        return Ok(await db.BusinessHours
            .AsNoTracking()
            .OrderBy(h => h.DayOfWeek).ThenBy(h => h.StartMinute)
            .Select(h => new BusinessHourResponse(h.Id, (int)h.DayOfWeek, h.StartMinute, h.EndMinute, h.StaffMemberId))
            .ToListAsync(ct));
    }

    private async Task<bool> AssignServicesAsync(StaffMember staff, List<Guid> serviceIds, CancellationToken ct)
    {
        if (serviceIds.Count == 0)
        {
            return true;
        }

        var distinct = serviceIds.Distinct().ToList();

        var valid = await db.Services
            .Where(s => distinct.Contains(s.Id))
            .Select(s => s.Id)
            .ToListAsync(ct);

        if (valid.Count != distinct.Count)
        {
            return false;
        }

        foreach (var serviceId in valid)
        {
            db.StaffServices.Add(new StaffService { ServiceId = serviceId, StaffMemberId = staff.Id });
        }

        return true;
    }
}
