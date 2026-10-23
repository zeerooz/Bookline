using System.Security.Cryptography;
using Bookline.Api.Data;
using Bookline.Api.Domain;
using Bookline.Api.Dtos;
using Bookline.Api.Security;
using Microsoft.EntityFrameworkCore;

namespace Bookline.Api.Services;

/// <summary>Thrown when a request is valid in shape but not allowed by the business rules.</summary>
public class BookingException(string message, string code = "booking_failed") : Exception(message)
{
    public string Code { get; } = code;
}

public interface IBookingService
{
    Task<IReadOnlyList<AvailabilityResponse>> GetAvailabilityAsync(
        Guid serviceId, Guid? staffMemberId, DateOnly from, DateOnly to, CancellationToken ct);

    Task<BookingConfirmation> CreateAsync(CreateBookingRequest request, Guid? customerUserId, CancellationToken ct);

    Task CancelAsync(Appointment appointment, string? reason, string actorDescription, CancellationToken ct);
}

public class BookingService(
    AppDbContext db,
    ITenantContext tenant,
    INotificationQueue notifications,
    IAuditService audit,
    TimeProvider clock,
    ILogger<BookingService> logger) : IBookingService
{
    public async Task<IReadOnlyList<AvailabilityResponse>> GetAvailabilityAsync(
        Guid serviceId, Guid? staffMemberId, DateOnly from, DateOnly to, CancellationToken ct)
    {
        var business = await LoadTenantAsync(ct);
        var zone = ResolveTimeZone(business.TimeZoneId);

        var service = await db.Services
            .AsNoTracking()
            .FirstOrDefaultAsync(s => s.Id == serviceId && s.IsActive, ct)
            ?? throw new BookingException("That service is not available.", "service_not_found");

        // Cap the range so a client cannot ask for ten years of slots.
        var horizonEnd = DateOnly.FromDateTime(clock.GetUtcNow().UtcDateTime.AddDays(business.BookingHorizonDays));

        if (to > horizonEnd)
        {
            to = horizonEnd;
        }

        if (to < from)
        {
            return [];
        }

        var candidates = await db.StaffServices
            .AsNoTracking()
            .Where(ss => ss.ServiceId == serviceId)
            .Where(ss => staffMemberId == null || ss.StaffMemberId == staffMemberId)
            .Select(ss => ss.StaffMember!)
            .Where(s => s.IsActive)
            .ToListAsync(ct);

        if (candidates.Count == 0)
        {
            return [];
        }

        var staffIds = candidates.Select(s => s.Id).ToList();

        // One window wide enough to cover the whole requested range, so the
        // busy intervals are fetched in a single round trip rather than per day.
        var rangeStart = from.ToDateTime(TimeOnly.MinValue).AddDays(-1).ToUniversalTime();
        var rangeEnd = to.ToDateTime(TimeOnly.MaxValue).AddDays(1).ToUniversalTime();

        var appointments = await db.Appointments
            .AsNoTracking()
            .Where(a => staffIds.Contains(a.StaffMemberId))
            .Where(a => a.StartsAtUtc < rangeEnd && a.BlockedUntilUtc > rangeStart)
            .Where(a => a.Status == AppointmentStatus.Confirmed
                        || a.Status == AppointmentStatus.PendingPayment
                        || a.Status == AppointmentStatus.Completed)
            .Select(a => new { a.StaffMemberId, a.StartsAtUtc, a.BlockedUntilUtc })
            .ToListAsync(ct);

        var timeOff = await db.TimeOff
            .AsNoTracking()
            .Where(t => staffIds.Contains(t.StaffMemberId))
            .Where(t => t.StartsAtUtc < rangeEnd && t.EndsAtUtc > rangeStart)
            .Select(t => new { t.StaffMemberId, t.StartsAtUtc, t.EndsAtUtc })
            .ToListAsync(ct);

        var hours = await db.BusinessHours.AsNoTracking().ToListAsync(ct);

        var results = new List<AvailabilityResponse>();

        foreach (var staff in candidates)
        {
            // A staff member with their own hours overrides the business-wide
            // ones entirely, rather than intersecting with them: a part-timer's
            // schedule should not have to repeat the shop's opening times.
            var ownWindows = hours.Where(h => h.StaffMemberId == staff.Id).ToList();
            var applicable = ownWindows.Count > 0 ? ownWindows : hours.Where(h => h.StaffMemberId is null).ToList();

            var busy = appointments
                .Where(a => a.StaffMemberId == staff.Id)
                .Select(a => new Interval(a.StartsAtUtc, a.BlockedUntilUtc))
                .Concat(timeOff
                    .Where(t => t.StaffMemberId == staff.Id)
                    .Select(t => new Interval(t.StartsAtUtc, t.EndsAtUtc)))
                .ToList();

            var slots = AvailabilityCalculator.Compute(new AvailabilityRequest
            {
                TimeZone = zone,
                FromDate = from,
                ToDate = to,
                DurationMinutes = service.DurationMinutes,
                BufferMinutes = service.BufferMinutes,
                WeeklyWindows = applicable.Select(h => new WeeklyWindow(h.DayOfWeek, h.StartMinute, h.EndMinute)).ToList(),
                Busy = busy,
                NowUtc = clock.GetUtcNow().UtcDateTime,
                MinimumNoticeMinutes = business.MinimumNoticeMinutes,
                BookingHorizonDays = business.BookingHorizonDays,
            });

            if (slots.Count > 0)
            {
                results.Add(new AvailabilityResponse(serviceId, staff.Id, staff.DisplayName, business.TimeZoneId, slots));
            }
        }

        return results;
    }

    public async Task<BookingConfirmation> CreateAsync(
        CreateBookingRequest request, Guid? customerUserId, CancellationToken ct)
    {
        var business = await LoadTenantAsync(ct);
        var now = clock.GetUtcNow().UtcDateTime;

        var service = await db.Services
            .FirstOrDefaultAsync(s => s.Id == request.ServiceId && s.IsActive, ct)
            ?? throw new BookingException("That service is not available.", "service_not_found");

        var startsAt = DateTime.SpecifyKind(request.StartsAtUtc, DateTimeKind.Utc);

        if (startsAt < now.AddMinutes(business.MinimumNoticeMinutes))
        {
            throw new BookingException(
                $"Bookings need at least {business.MinimumNoticeMinutes} minutes' notice.", "too_soon");
        }

        if (startsAt > now.AddDays(business.BookingHorizonDays))
        {
            throw new BookingException(
                $"Bookings open {business.BookingHorizonDays} days ahead.", "too_far_ahead");
        }

        var staff = await ResolveStaffAsync(request, startsAt, service, business, ct);

        var endsAt = startsAt.AddMinutes(service.DurationMinutes);
        var blockedUntil = endsAt.AddMinutes(service.BufferMinutes);

        // Authoritative availability check. The published slot list is a
        // convenience; this is the decision, and it runs against current data.
        if (!await IsSlotFreeAsync(staff.Id, startsAt, blockedUntil, ct))
        {
            throw new BookingException("That time has just been taken. Pick another slot.", "slot_taken");
        }

        if (!await IsWithinOpeningHoursAsync(staff.Id, startsAt, endsAt, business, ct))
        {
            throw new BookingException("That time is outside opening hours.", "outside_hours");
        }

        var requiresPayment = service.RequiresPrepayment && service.PriceMinorUnits > 0;

        var appointment = new Appointment
        {
            TenantId = business.Id,
            ServiceId = service.Id,
            StaffMemberId = staff.Id,
            CustomerUserId = customerUserId,
            CustomerName = request.CustomerName.Trim(),
            CustomerEmail = request.CustomerEmail.Trim().ToLowerInvariant(),
            CustomerPhone = request.CustomerPhone?.Trim(),
            StartsAtUtc = startsAt,
            EndsAtUtc = endsAt,
            BlockedUntilUtc = blockedUntil,
            Status = requiresPayment ? AppointmentStatus.PendingPayment : AppointmentStatus.Confirmed,
            PriceMinorUnits = service.PriceMinorUnits,
            Currency = business.Currency,
            Notes = request.Notes?.Trim(),
            ManagementToken = GenerateManagementToken(),
            CreatedAt = now,
        };

        db.Appointments.Add(appointment);

        // Queued in the same transaction as the booking, so the two cannot
        // disagree about whether this appointment exists.
        if (!requiresPayment)
        {
            notifications.QueueBookingConfirmed(appointment, service, staff, business);
            notifications.QueueReminder(appointment, service, staff, business);
        }

        audit.Record("appointment.created", nameof(Appointment), appointment.Id.ToString(),
            $"{service.Name} with {staff.DisplayName}");

        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex) when (IsOverlapViolation(ex))
        {
            // The database exclusion constraint caught a race that the check
            // above could not: two requests both saw a free slot.
            logger.LogWarning(ex, "Overlapping booking rejected by the database for staff {StaffId}", staff.Id);
            throw new BookingException("That time has just been taken. Pick another slot.", "slot_taken");
        }

        var response = await BuildResponseAsync(appointment, service, staff);

        return new BookingConfirmation(response, appointment.ManagementToken, null);
    }

    public async Task CancelAsync(Appointment appointment, string? reason, string actorDescription, CancellationToken ct)
    {
        if (appointment.Status == AppointmentStatus.Cancelled)
        {
            return;
        }

        if (appointment.Status == AppointmentStatus.Completed)
        {
            throw new BookingException("A completed appointment cannot be cancelled.", "already_completed");
        }

        appointment.Status = AppointmentStatus.Cancelled;
        appointment.CancelledAt = clock.GetUtcNow().UtcDateTime;
        appointment.CancellationReason = reason?.Trim();

        var business = await LoadTenantAsync(ct);
        var service = await db.Services.AsNoTracking().FirstAsync(s => s.Id == appointment.ServiceId, ct);
        var staff = await db.StaffMembers.AsNoTracking().FirstAsync(s => s.Id == appointment.StaffMemberId, ct);

        notifications.QueueBookingCancelled(appointment, service, staff, business);

        // Any reminder still sitting in the outbox is now wrong.
        notifications.CancelPendingReminders(appointment.Id);

        audit.Record("appointment.cancelled", nameof(Appointment), appointment.Id.ToString(),
            $"cancelled by {actorDescription}");

        await db.SaveChangesAsync(ct);
    }

    // -----------------------------------------------------------------------

    private async Task<StaffMember> ResolveStaffAsync(
        CreateBookingRequest request, DateTime startsAt, Service service, Tenant business, CancellationToken ct)
    {
        var qualified = await db.StaffServices
            .Where(ss => ss.ServiceId == service.Id)
            .Where(ss => request.StaffMemberId == null || ss.StaffMemberId == request.StaffMemberId)
            .Select(ss => ss.StaffMember!)
            .Where(s => s.IsActive)
            .ToListAsync(ct);

        if (qualified.Count == 0)
        {
            throw new BookingException(
                request.StaffMemberId is null
                    ? "Nobody is currently able to deliver that service."
                    : "That team member does not offer this service.",
                "staff_unavailable");
        }

        if (request.StaffMemberId is not null)
        {
            return qualified[0];
        }

        // "Any available" — take the first who is genuinely free, so the
        // customer is not bounced back to the slot list.
        var blockedUntil = startsAt.AddMinutes(service.DurationMinutes + service.BufferMinutes);
        var endsAt = startsAt.AddMinutes(service.DurationMinutes);

        // The two reasons a candidate can be ruled out are kept apart, because
        // they are different answers for the customer: "we are closed then" is
        // a bad request they can fix by picking another time, while "somebody
        // got there first" is a genuine conflict.
        var working = new List<StaffMember>();

        foreach (var candidate in qualified)
        {
            if (await IsWithinOpeningHoursAsync(candidate.Id, startsAt, endsAt, business, ct))
            {
                working.Add(candidate);
            }
        }

        if (working.Count == 0)
        {
            throw new BookingException("That time is outside opening hours.", "outside_hours");
        }

        foreach (var candidate in working)
        {
            if (await IsSlotFreeAsync(candidate.Id, startsAt, blockedUntil, ct))
            {
                return candidate;
            }
        }

        throw new BookingException("Nobody is free at that time.", "slot_taken");
    }

    private async Task<bool> IsSlotFreeAsync(Guid staffId, DateTime startsAt, DateTime blockedUntil, CancellationToken ct)
    {
        var clashes = await db.Appointments
            .AsNoTracking()
            .Where(a => a.StaffMemberId == staffId)
            .Where(a => a.Status == AppointmentStatus.Confirmed
                        || a.Status == AppointmentStatus.PendingPayment
                        || a.Status == AppointmentStatus.Completed)
            // Half-open overlap: touching at the boundary is not a clash.
            .AnyAsync(a => a.StartsAtUtc < blockedUntil && a.BlockedUntilUtc > startsAt, ct);

        if (clashes)
        {
            return false;
        }

        return !await db.TimeOff
            .AsNoTracking()
            .Where(t => t.StaffMemberId == staffId)
            .AnyAsync(t => t.StartsAtUtc < blockedUntil && t.EndsAtUtc > startsAt, ct);
    }

    private async Task<bool> IsWithinOpeningHoursAsync(
        Guid staffId, DateTime startsAt, DateTime endsAt, Tenant business, CancellationToken ct)
    {
        var zone = ResolveTimeZone(business.TimeZoneId);
        var hours = await db.BusinessHours.AsNoTracking().ToListAsync(ct);

        var ownWindows = hours.Where(h => h.StaffMemberId == staffId).ToList();
        var applicable = ownWindows.Count > 0 ? ownWindows : hours.Where(h => h.StaffMemberId is null).ToList();

        var localStart = TimeZoneInfo.ConvertTimeFromUtc(startsAt, zone);
        var localEnd = TimeZoneInfo.ConvertTimeFromUtc(endsAt, zone);

        // A booking that would cross midnight cannot sit inside one weekly window.
        if (localStart.Date != localEnd.Date && localEnd.TimeOfDay != TimeSpan.Zero)
        {
            return false;
        }

        var startMinute = (int)localStart.TimeOfDay.TotalMinutes;
        var endMinute = startMinute + (int)(endsAt - startsAt).TotalMinutes;

        return applicable.Any(h =>
            h.DayOfWeek == localStart.DayOfWeek && h.StartMinute <= startMinute && h.EndMinute >= endMinute);
    }

    private async Task<Tenant> LoadTenantAsync(CancellationToken ct)
    {
        var id = tenant.TenantId
                 ?? throw new InvalidOperationException("No tenant resolved for the current request.");

        return await db.Tenants.AsNoTracking().FirstAsync(t => t.Id == id, ct);
    }

    private async Task<AppointmentResponse> BuildResponseAsync(Appointment a, Service service, StaffMember staff)
    {
        await Task.CompletedTask;

        return new AppointmentResponse(
            a.Id, service.Id, service.Name, staff.Id, staff.DisplayName,
            a.CustomerName, a.CustomerEmail, a.CustomerPhone,
            a.StartsAtUtc, a.EndsAtUtc, a.Status, a.PriceMinorUnits, a.Currency, a.Notes, a.CreatedAt);
    }

    internal static TimeZoneInfo ResolveTimeZone(string id)
    {
        try
        {
            return TimeZoneInfo.FindSystemTimeZoneById(id);
        }
        catch (Exception ex) when (ex is TimeZoneNotFoundException or InvalidTimeZoneException)
        {
            // Better to serve UTC slots than to fail every request because a
            // tenant was configured with a zone this host does not know.
            return TimeZoneInfo.Utc;
        }
    }

    /// <summary>Base64url of 32 random bytes: unguessable, and safe in a URL.</summary>
    private static string GenerateManagementToken() =>
        Convert.ToBase64String(RandomNumberGenerator.GetBytes(24))
            .Replace('+', '-').Replace('/', '_').TrimEnd('=');

    /// <summary>
    /// Recognises the Postgres exclusion-constraint violation (SQLSTATE 23P01)
    /// raised by the overlap guard added in the initial migration.
    /// </summary>
    private static bool IsOverlapViolation(DbUpdateException ex) =>
        ex.InnerException?.GetType().GetProperty("SqlState")?.GetValue(ex.InnerException) as string == "23P01";
}
