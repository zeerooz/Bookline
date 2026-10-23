using Bookline.Api.Data;
using Bookline.Api.Domain;
using Bookline.Api.Dtos;
using Bookline.Api.Security;
using Bookline.Api.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;

namespace Bookline.Api.Controllers;

/// <summary>
/// What a customer sees. No sign-in required: a business loses bookings if it
/// forces an account first. A guest instead receives an unguessable management
/// token, which is the only thing that lets them view or cancel that booking.
/// </summary>
[ApiController]
[Route("api/public/{tenantSlug}")]
[EnableRateLimiting(RateLimitPolicies.PublicBooking)]
public class PublicBookingController(
    AppDbContext db,
    ITenantContext tenant,
    IBookingService bookings,
    ICurrentUser currentUser) : ControllerBase
{
    [HttpGet]
    [ProducesResponseType<TenantSummary>(StatusCodes.Status200OK)]
    public async Task<ActionResult<TenantSummary>> GetBusiness(CancellationToken ct)
    {
        var business = await db.Tenants
            .AsNoTracking()
            .FirstAsync(t => t.Id == tenant.TenantId, ct);

        return Ok(new TenantSummary(
            business.Id, business.Name, business.Slug, business.TimeZoneId,
            business.Currency, business.BookingHorizonDays));
    }

    [HttpGet("services")]
    [ProducesResponseType<IReadOnlyList<ServiceResponse>>(StatusCodes.Status200OK)]
    public async Task<ActionResult<IReadOnlyList<ServiceResponse>>> GetServices(CancellationToken ct)
    {
        // Only active services, and only ones somebody can actually deliver —
        // offering a service with no qualified staff is a dead end.
        var services = await db.Services
            .AsNoTracking()
            .Where(s => s.IsActive)
            .Where(s => s.StaffServices.Any(ss => ss.StaffMember!.IsActive))
            .OrderBy(s => s.Name)
            .Select(s => new ServiceResponse(
                s.Id, s.Name, s.Description, s.DurationMinutes, s.BufferMinutes,
                s.PriceMinorUnits, s.RequiresPrepayment, s.IsActive))
            .ToListAsync(ct);

        return Ok(services);
    }

    [HttpGet("staff")]
    [ProducesResponseType<IReadOnlyList<StaffMemberResponse>>(StatusCodes.Status200OK)]
    public async Task<ActionResult<IReadOnlyList<StaffMemberResponse>>> GetStaff(CancellationToken ct)
    {
        var staff = await db.StaffMembers
            .AsNoTracking()
            .Where(s => s.IsActive)
            .OrderBy(s => s.DisplayName)
            .Select(s => new StaffMemberResponse(
                s.Id, s.DisplayName, s.Title, s.IsActive,
                s.StaffServices.Select(ss => ss.ServiceId).ToList()))
            .ToListAsync(ct);

        return Ok(staff);
    }

    [HttpGet("availability")]
    [ProducesResponseType<IReadOnlyList<AvailabilityResponse>>(StatusCodes.Status200OK)]
    public async Task<IActionResult> GetAvailability(
        [FromQuery] Guid serviceId,
        [FromQuery] DateOnly from,
        [FromQuery] DateOnly? to,
        [FromQuery] Guid? staffMemberId,
        CancellationToken ct)
    {
        // A month at a time keeps one request from scanning a year of calendar.
        var end = to ?? from.AddDays(13);

        if (end < from)
        {
            return Problem("The end date is before the start date.", statusCode: StatusCodes.Status400BadRequest);
        }

        if (end.DayNumber - from.DayNumber > 31)
        {
            return Problem("Ask for at most 31 days at a time.", statusCode: StatusCodes.Status400BadRequest);
        }

        try
        {
            return Ok(await bookings.GetAvailabilityAsync(serviceId, staffMemberId, from, end, ct));
        }
        catch (BookingException ex)
        {
            return Problem(ex.Message, statusCode: StatusCodes.Status400BadRequest, title: ex.Code);
        }
    }

    [HttpPost("bookings")]
    [ProducesResponseType<BookingConfirmation>(StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> Create(CreateBookingRequest request, CancellationToken ct)
    {
        try
        {
            // Signed in? Attach the booking to the account so it shows up in
            // their history. Otherwise it is a guest booking.
            var confirmation = await bookings.CreateAsync(request, currentUser.UserId, ct);

            return Created(
                $"/api/public/{tenant.Slug}/bookings/{confirmation.ManagementToken}",
                confirmation);
        }
        catch (BookingException ex)
        {
            var status = ex.Code == "slot_taken"
                ? StatusCodes.Status409Conflict
                : StatusCodes.Status400BadRequest;

            return Problem(ex.Message, statusCode: status, title: ex.Code);
        }
    }

    /// <summary>
    /// Looking up a booking by its management token. The token is the
    /// credential, so this route is deliberately open — and the token is 24
    /// random bytes, which is not something anyone enumerates.
    /// </summary>
    [HttpGet("bookings/{token}")]
    [ProducesResponseType<AppointmentResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetByToken(string token, CancellationToken ct)
    {
        var appointment = await FindByTokenAsync(token, ct);

        return appointment is null ? NotFound() : Ok(ToResponse(appointment));
    }

    [HttpPost("bookings/{token}/cancel")]
    [ProducesResponseType<AppointmentResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Cancel(string token, CancelBookingRequest request, CancellationToken ct)
    {
        var appointment = await db.Appointments
            .Include(a => a.Service)
            .Include(a => a.StaffMember)
            .FirstOrDefaultAsync(a => a.ManagementToken == token, ct);

        if (appointment is null)
        {
            return NotFound();
        }

        try
        {
            await bookings.CancelAsync(appointment, request.Reason, "the customer", ct);
            return Ok(ToResponse(appointment));
        }
        catch (BookingException ex)
        {
            return Problem(ex.Message, statusCode: StatusCodes.Status400BadRequest, title: ex.Code);
        }
    }

    private Task<Appointment?> FindByTokenAsync(string token, CancellationToken ct) =>
        db.Appointments
            .AsNoTracking()
            .Include(a => a.Service)
            .Include(a => a.StaffMember)
            .FirstOrDefaultAsync(a => a.ManagementToken == token, ct);

    private static AppointmentResponse ToResponse(Appointment a) =>
        new(a.Id, a.ServiceId, a.Service?.Name ?? string.Empty,
            a.StaffMemberId, a.StaffMember?.DisplayName ?? string.Empty,
            a.CustomerName, a.CustomerEmail, a.CustomerPhone,
            a.StartsAtUtc, a.EndsAtUtc, a.Status, a.PriceMinorUnits, a.Currency, a.Notes, a.CreatedAt);
}
