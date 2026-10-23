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

/// <summary>
/// The business-side calendar. Staff see only their own appointments; owners
/// see the whole diary. That split is enforced here rather than in the UI,
/// because a staff member can call the API directly.
/// </summary>
[ApiController]
[Route("api/t/{tenantSlug}/appointments")]
[Authorize(Policy = Policies.TenantStaff)]
[EnableRateLimiting(RateLimitPolicies.Api)]
public class AppointmentsController(
    AppDbContext db,
    IBookingService bookings,
    ICurrentUser currentUser) : ControllerBase
{
    [HttpGet]
    [ProducesResponseType<IReadOnlyList<AppointmentResponse>>(StatusCodes.Status200OK)]
    public async Task<ActionResult<IReadOnlyList<AppointmentResponse>>> GetAll(
        [FromQuery] DateTime? fromUtc,
        [FromQuery] DateTime? toUtc,
        [FromQuery] AppointmentStatus? status,
        CancellationToken ct = default)
    {
        var from = fromUtc ?? DateTime.UtcNow.Date;
        var to = toUtc ?? from.AddDays(14);

        var query = db.Appointments
            .AsNoTracking()
            .Where(a => a.StartsAtUtc >= from && a.StartsAtUtc < to);

        if (status is not null)
        {
            query = query.Where(a => a.Status == status);
        }

        if (!await IsOwnerAsync(ct))
        {
            // Staff see their own diary only. A staff member with no linked
            // StaffMember row sees nothing, which is the safe direction.
            var ownStaffIds = await db.StaffMembers
                .Where(s => s.UserId == currentUser.RequireUserId())
                .Select(s => s.Id)
                .ToListAsync(ct);

            query = query.Where(a => ownStaffIds.Contains(a.StaffMemberId));
        }

        var appointments = await query
            .OrderBy(a => a.StartsAtUtc)
            .Select(a => new AppointmentResponse(
                a.Id, a.ServiceId, a.Service!.Name, a.StaffMemberId, a.StaffMember!.DisplayName,
                a.CustomerName, a.CustomerEmail, a.CustomerPhone,
                a.StartsAtUtc, a.EndsAtUtc, a.Status, a.PriceMinorUnits, a.Currency, a.Notes, a.CreatedAt))
            .ToListAsync(ct);

        return Ok(appointments);
    }

    [HttpPost("{id:guid}/cancel")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Cancel(Guid id, CancelBookingRequest request, CancellationToken ct)
    {
        var appointment = await FindForCallerAsync(id, ct);

        if (appointment is null)
        {
            return NotFound();
        }

        try
        {
            await bookings.CancelAsync(appointment, request.Reason, currentUser.Email ?? "a team member", ct);
            return NoContent();
        }
        catch (BookingException ex)
        {
            return Problem(ex.Message, statusCode: StatusCodes.Status400BadRequest, title: ex.Code);
        }
    }

    [HttpPost("{id:guid}/complete")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public async Task<IActionResult> Complete(Guid id, CancellationToken ct) =>
        await TransitionAsync(id, AppointmentStatus.Completed, ct);

    [HttpPost("{id:guid}/no-show")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public async Task<IActionResult> NoShow(Guid id, CancellationToken ct) =>
        await TransitionAsync(id, AppointmentStatus.NoShow, ct);

    private async Task<IActionResult> TransitionAsync(Guid id, AppointmentStatus target, CancellationToken ct)
    {
        var appointment = await FindForCallerAsync(id, ct);

        if (appointment is null)
        {
            return NotFound();
        }

        if (appointment.Status == AppointmentStatus.Cancelled)
        {
            return Problem("A cancelled appointment cannot be reopened.",
                statusCode: StatusCodes.Status400BadRequest);
        }

        appointment.Status = target;
        await db.SaveChangesAsync(ct);

        return NoContent();
    }

    /// <summary>
    /// Loads an appointment the caller is allowed to touch. Returns null rather
    /// than throwing, so a staff member probing another colleague's id gets a
    /// plain 404 and learns nothing.
    /// </summary>
    private async Task<Appointment?> FindForCallerAsync(Guid id, CancellationToken ct)
    {
        var appointment = await db.Appointments.FirstOrDefaultAsync(a => a.Id == id, ct);

        if (appointment is null)
        {
            return null;
        }

        if (await IsOwnerAsync(ct))
        {
            return appointment;
        }

        var isOwnAppointment = await db.StaffMembers
            .AnyAsync(s => s.Id == appointment.StaffMemberId && s.UserId == currentUser.RequireUserId(), ct);

        return isOwnAppointment ? appointment : null;
    }

    private async Task<bool> IsOwnerAsync(CancellationToken ct) =>
        await db.Memberships.AnyAsync(
            m => m.UserId == currentUser.RequireUserId() && m.Role == TenantRole.Owner, ct);
}
