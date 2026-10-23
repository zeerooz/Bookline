namespace Bookline.Api.Services;

/// <summary>A half-open interval [Start, End) in UTC.</summary>
public readonly record struct Interval(DateTime StartUtc, DateTime EndUtc)
{
    public bool Overlaps(Interval other) => StartUtc < other.EndUtc && other.StartUtc < EndUtc;

    public bool IsEmpty => EndUtc <= StartUtc;
}

/// <summary>A recurring weekly opening window, in local wall-clock minutes from midnight.</summary>
public readonly record struct WeeklyWindow(DayOfWeek Day, int StartMinute, int EndMinute);

public sealed record AvailabilityRequest
{
    public required TimeZoneInfo TimeZone { get; init; }

    /// <summary>First and last local dates to consider, inclusive.</summary>
    public required DateOnly FromDate { get; init; }
    public required DateOnly ToDate { get; init; }

    public required int DurationMinutes { get; init; }
    public int BufferMinutes { get; init; }

    /// <summary>How far apart candidate start times are placed. 15 suits most businesses.</summary>
    public int SlotStepMinutes { get; init; } = 15;

    public required IReadOnlyList<WeeklyWindow> WeeklyWindows { get; init; }

    /// <summary>Appointment footprints and time off, already in UTC.</summary>
    public IReadOnlyList<Interval> Busy { get; init; } = [];

    public required DateTime NowUtc { get; init; }
    public int MinimumNoticeMinutes { get; init; }
    public int BookingHorizonDays { get; init; } = 60;
}

/// <summary>
/// Turns opening hours minus existing commitments into bookable start times.
///
/// Pure: no database, no clock, no time-zone ambient state. Everything it needs
/// arrives in the request, which is what makes the awkward cases — daylight
/// saving, midnight boundaries, a slot that would run past closing — testable
/// without standing up a business.
/// </summary>
public static class AvailabilityCalculator
{
    public static IReadOnlyList<DateTime> Compute(AvailabilityRequest request)
    {
        if (request.DurationMinutes <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(request), "A service must last longer than zero minutes.");
        }

        if (request.SlotStepMinutes <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(request), "The slot step must be a positive number of minutes.");
        }

        var earliest = request.NowUtc.AddMinutes(request.MinimumNoticeMinutes);
        var latest = request.NowUtc.AddDays(request.BookingHorizonDays);

        var slots = new List<DateTime>();

        for (var date = request.FromDate; date <= request.ToDate; date = date.AddDays(1))
        {
            foreach (var window in request.WeeklyWindows.Where(w => w.Day == date.DayOfWeek))
            {
                AddSlotsForWindow(request, date, window, earliest, latest, slots);
            }
        }

        // Several windows on one day (a morning and an afternoon shift) can
        // produce the same start time twice.
        return slots.Distinct().Order().ToList();
    }

    private static void AddSlotsForWindow(
        AvailabilityRequest request,
        DateOnly date,
        WeeklyWindow window,
        DateTime earliest,
        DateTime latest,
        List<DateTime> slots)
    {
        if (window.EndMinute <= window.StartMinute)
        {
            return;
        }

        var footprint = request.DurationMinutes + request.BufferMinutes;

        for (var minute = window.StartMinute; minute + request.DurationMinutes <= window.EndMinute; minute += request.SlotStepMinutes)
        {
            // Resolved per candidate rather than once per day: on a
            // daylight-saving boundary the offset changes midway through.
            if (!TryResolveToUtc(date, minute, request.TimeZone, out var startUtc))
            {
                continue;
            }

            if (startUtc < earliest || startUtc > latest)
            {
                continue;
            }

            var candidate = new Interval(startUtc, startUtc.AddMinutes(footprint));

            if (request.Busy.Any(busy => busy.Overlaps(candidate)))
            {
                continue;
            }

            slots.Add(startUtc);
        }
    }

    /// <summary>
    /// Converts a local wall-clock time to a UTC instant.
    ///
    /// Two cases make this more than a subtraction. When the clocks go forward
    /// the local time may not exist at all, and we skip it — offering a slot at
    /// a time no clock will ever show is worse than offering one slot fewer.
    /// When they go back the same local time happens twice, and we take the
    /// first (daylight) occurrence, so the business opens once rather than
    /// appearing to open twice.
    /// </summary>
    internal static bool TryResolveToUtc(DateOnly date, int minuteOfDay, TimeZoneInfo zone, out DateTime utc)
    {
        utc = default;

        // Minutes can run past midnight for a window that closes at 24:00.
        var local = date.ToDateTime(TimeOnly.MinValue).AddMinutes(minuteOfDay);
        local = DateTime.SpecifyKind(local, DateTimeKind.Unspecified);

        if (zone.IsInvalidTime(local))
        {
            return false;
        }

        if (zone.IsAmbiguousTime(local))
        {
            // GetAmbiguousTimeOffsets returns the offsets in ascending order;
            // the larger one is daylight time, which comes first in real time.
            var offsets = zone.GetAmbiguousTimeOffsets(local);
            var daylight = offsets.Max();
            utc = DateTime.SpecifyKind(local - daylight, DateTimeKind.Utc);
            return true;
        }

        utc = TimeZoneInfo.ConvertTimeToUtc(local, zone);
        return true;
    }
}
