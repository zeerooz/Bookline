using Bookline.Api.Services;

namespace Bookline.Tests.Unit;

/// <summary>
/// The availability engine is where a booking product is usually wrong, so it
/// is tested as a pure function with the clock, the zone and the calendar all
/// supplied by the test.
/// </summary>
public class AvailabilityCalculatorTests
{
    private static readonly TimeZoneInfo Cairo = TimeZoneInfo.FindSystemTimeZoneById("Africa/Cairo");

    private static readonly DateOnly Monday = new(2026, 6, 1);

    private static AvailabilityRequest Request(
        int duration = 60,
        int buffer = 0,
        int step = 30,
        IReadOnlyList<WeeklyWindow>? windows = null,
        IReadOnlyList<Interval>? busy = null,
        DateTime? now = null,
        int notice = 0,
        DateOnly? from = null,
        DateOnly? to = null) => new()
    {
        TimeZone = Cairo,
        FromDate = from ?? Monday,
        ToDate = to ?? Monday,
        DurationMinutes = duration,
        BufferMinutes = buffer,
        SlotStepMinutes = step,
        // 09:00–12:00 on the Monday under test.
        WeeklyWindows = windows ?? [new WeeklyWindow(DayOfWeek.Monday, 9 * 60, 12 * 60)],
        Busy = busy ?? [],
        NowUtc = now ?? new DateTime(2026, 5, 1, 0, 0, 0, DateTimeKind.Utc),
        MinimumNoticeMinutes = notice,
        BookingHorizonDays = 365,
    };

    /// <summary>Cairo is UTC+3 in June, so 09:00 local is 06:00 UTC.</summary>
    private static DateTime CairoSummer(int hour, int minute = 0) =>
        new DateTime(2026, 6, 1, hour - 3, minute, 0, DateTimeKind.Utc);

    [Fact]
    public void Produces_one_slot_per_step_inside_the_window()
    {
        var slots = AvailabilityCalculator.Compute(Request());

        // 09:00-12:00 with a 60-minute service at 30-minute steps:
        // 09:00, 09:30, 10:00, 10:30, 11:00. Not 11:30 — it would end at 12:30.
        Assert.Equal(5, slots.Count);
        Assert.Equal(CairoSummer(9), slots[0]);
        Assert.Equal(CairoSummer(11), slots[^1]);
    }

    [Fact]
    public void Never_offers_a_slot_that_would_run_past_closing()
    {
        var slots = AvailabilityCalculator.Compute(Request(duration: 90, step: 30));

        Assert.All(slots, slot => Assert.True(slot.AddMinutes(90) <= CairoSummer(12)));
    }

    [Fact]
    public void Returns_nothing_when_the_service_is_longer_than_the_window()
    {
        Assert.Empty(AvailabilityCalculator.Compute(Request(duration: 240)));
    }

    [Fact]
    public void Returns_nothing_on_a_day_with_no_opening_hours()
    {
        // The window is declared for Tuesday; the requested day is a Monday.
        var slots = AvailabilityCalculator.Compute(
            Request(windows: [new WeeklyWindow(DayOfWeek.Tuesday, 9 * 60, 17 * 60)]));

        Assert.Empty(slots);
    }

    [Fact]
    public void Skips_slots_that_overlap_an_existing_appointment()
    {
        var busy = new[] { new Interval(CairoSummer(10), CairoSummer(11)) };

        var slots = AvailabilityCalculator.Compute(Request(busy: busy));

        // 09:30 and 10:00 and 10:30 all collide with a 10:00-11:00 booking
        // once the one-hour duration is taken into account.
        Assert.Equal([CairoSummer(9), CairoSummer(11)], slots);
    }

    [Fact]
    public void Treats_touching_appointments_as_compatible()
    {
        // A booking that ends exactly when the next begins is not a clash.
        var busy = new[] { new Interval(CairoSummer(9), CairoSummer(10)) };

        var slots = AvailabilityCalculator.Compute(Request(busy: busy));

        Assert.Contains(CairoSummer(10), slots);
        Assert.DoesNotContain(CairoSummer(9), slots);
    }

    [Fact]
    public void Counts_the_buffer_as_occupied_time()
    {
        // Existing commitment 10:30-11:30. A 60-minute service starting at
        // 09:30 ends at 10:30, which only touches it — fine without a buffer.
        var busy = new[] { new Interval(CairoSummer(10, 30), CairoSummer(11, 30)) };

        var withoutBuffer = AvailabilityCalculator.Compute(Request(buffer: 0, busy: busy));
        Assert.Contains(CairoSummer(9, 30), withoutBuffer);

        // Add a 30-minute buffer and that same booking now runs to 11:00,
        // which does collide. The buffer is the only thing that changed.
        var withBuffer = AvailabilityCalculator.Compute(Request(buffer: 30, busy: busy));

        Assert.DoesNotContain(CairoSummer(9, 30), withBuffer);
        Assert.Equal([CairoSummer(9)], withBuffer);
    }

    [Fact]
    public void A_slot_whose_buffer_runs_past_closing_is_still_offered()
    {
        // The customer's hour fits before closing; the staff member's tidy-up
        // afterwards is their own problem, not a reason to refuse the booking.
        var slots = AvailabilityCalculator.Compute(Request(duration: 60, buffer: 60, step: 60));

        Assert.Contains(CairoSummer(11), slots);
    }

    [Fact]
    public void Respects_the_minimum_notice_period()
    {
        var now = CairoSummer(9).AddMinutes(-30);

        var slots = AvailabilityCalculator.Compute(Request(now: now, notice: 120));

        // Nothing before 11:00 UTC-equivalent is bookable with two hours' notice.
        Assert.All(slots, slot => Assert.True(slot >= now.AddMinutes(120)));
        Assert.DoesNotContain(CairoSummer(9), slots);
    }

    [Fact]
    public void Respects_the_booking_horizon()
    {
        var request = Request(from: Monday, to: Monday.AddDays(21)) with
        {
            BookingHorizonDays = 7,
            NowUtc = new DateTime(2026, 6, 1, 0, 0, 0, DateTimeKind.Utc),
        };

        var slots = AvailabilityCalculator.Compute(request);

        Assert.All(slots, slot => Assert.True(slot <= request.NowUtc.AddDays(7)));
    }

    [Fact]
    public void Handles_two_windows_on_the_same_day()
    {
        var windows = new[]
        {
            new WeeklyWindow(DayOfWeek.Monday, 9 * 60, 11 * 60),
            new WeeklyWindow(DayOfWeek.Monday, 14 * 60, 16 * 60),
        };

        var slots = AvailabilityCalculator.Compute(Request(duration: 60, step: 60, windows: windows));

        Assert.Equal([CairoSummer(9), CairoSummer(10), CairoSummer(14), CairoSummer(15)], slots);
    }

    [Fact]
    public void Does_not_return_the_same_slot_twice_when_windows_overlap()
    {
        var windows = new[]
        {
            new WeeklyWindow(DayOfWeek.Monday, 9 * 60, 12 * 60),
            new WeeklyWindow(DayOfWeek.Monday, 10 * 60, 13 * 60),
        };

        var slots = AvailabilityCalculator.Compute(Request(duration: 60, step: 60, windows: windows));

        Assert.Equal(slots.Distinct().Count(), slots.Count);
    }

    [Fact]
    public void Returns_slots_in_chronological_order()
    {
        var windows = new[]
        {
            new WeeklyWindow(DayOfWeek.Monday, 14 * 60, 16 * 60),
            new WeeklyWindow(DayOfWeek.Monday, 9 * 60, 11 * 60),
        };

        var slots = AvailabilityCalculator.Compute(Request(duration: 60, step: 60, windows: windows));

        Assert.Equal(slots.Order().ToList(), slots);
    }

    [Fact]
    public void Rejects_a_zero_length_service()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => AvailabilityCalculator.Compute(Request(duration: 0)));
    }

    [Fact]
    public void Rejects_a_zero_length_step()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => AvailabilityCalculator.Compute(Request(step: 0)));
    }

    [Fact]
    public void Ignores_a_window_that_closes_before_it_opens()
    {
        var slots = AvailabilityCalculator.Compute(
            Request(windows: [new WeeklyWindow(DayOfWeek.Monday, 17 * 60, 9 * 60)]));

        Assert.Empty(slots);
    }
}

/// <summary>
/// Daylight saving is the part of a calendar that silently produces wrong
/// answers, so it gets its own class. Egypt moves its clocks again as of 2023,
/// which makes this directly relevant rather than theoretical.
/// </summary>
public class DaylightSavingTests
{
    private static readonly TimeZoneInfo Cairo = TimeZoneInfo.FindSystemTimeZoneById("Africa/Cairo");

    [Fact]
    public void A_local_time_that_does_not_exist_is_skipped()
    {
        // Egypt moves to summer time at the end of April. Find the real
        // transition rather than assuming a date, so the test survives a
        // change to the published rules.
        var invalid = FindInvalidLocalTime(Cairo, new DateOnly(2026, 1, 1), 400);

        if (invalid is null)
        {
            // No transition in range on this host's time-zone data.
            return;
        }

        var (date, minute) = invalid.Value;

        Assert.False(AvailabilityCalculator.TryResolveToUtc(date, minute, Cairo, out _));
    }

    [Fact]
    public void An_ambiguous_local_time_resolves_to_the_first_occurrence()
    {
        var ambiguous = FindAmbiguousLocalTime(Cairo, new DateOnly(2026, 1, 1), 400);

        if (ambiguous is null)
        {
            return;
        }

        var (date, minute) = ambiguous.Value;

        Assert.True(AvailabilityCalculator.TryResolveToUtc(date, minute, Cairo, out var utc));

        var local = date.ToDateTime(TimeOnly.MinValue).AddMinutes(minute);
        var offsets = Cairo.GetAmbiguousTimeOffsets(DateTime.SpecifyKind(local, DateTimeKind.Unspecified));

        // The daylight (larger) offset corresponds to the earlier instant.
        Assert.Equal(DateTime.SpecifyKind(local - offsets.Max(), DateTimeKind.Utc), utc);
    }

    [Fact]
    public void A_fixed_opening_time_stays_fixed_in_local_terms_across_a_transition()
    {
        // 09:00 must remain 09:00 for the business, even though the UTC
        // instant it maps to shifts by an hour.
        var before = new DateOnly(2026, 3, 1);
        var after = new DateOnly(2026, 8, 1);

        Assert.True(AvailabilityCalculator.TryResolveToUtc(before, 9 * 60, Cairo, out var utcBefore));
        Assert.True(AvailabilityCalculator.TryResolveToUtc(after, 9 * 60, Cairo, out var utcAfter));

        Assert.Equal(9, TimeZoneInfo.ConvertTimeFromUtc(utcBefore, Cairo).Hour);
        Assert.Equal(9, TimeZoneInfo.ConvertTimeFromUtc(utcAfter, Cairo).Hour);
    }

    [Fact]
    public void Slots_on_a_transition_day_are_all_real_local_times()
    {
        var slots = AvailabilityCalculator.Compute(new AvailabilityRequest
        {
            TimeZone = Cairo,
            FromDate = new DateOnly(2026, 4, 1),
            ToDate = new DateOnly(2026, 5, 31),
            DurationMinutes = 30,
            SlotStepMinutes = 15,
            WeeklyWindows = Enum.GetValues<DayOfWeek>().Select(d => new WeeklyWindow(d, 0, 1440)).ToList(),
            NowUtc = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            BookingHorizonDays = 365,
        });

        Assert.NotEmpty(slots);

        foreach (var slot in slots)
        {
            var local = TimeZoneInfo.ConvertTimeFromUtc(slot, Cairo);
            Assert.False(Cairo.IsInvalidTime(DateTime.SpecifyKind(local, DateTimeKind.Unspecified)));
        }
    }

    private static (DateOnly Date, int Minute)? FindInvalidLocalTime(TimeZoneInfo zone, DateOnly start, int days)
    {
        for (var i = 0; i < days; i++)
        {
            var date = start.AddDays(i);

            for (var minute = 0; minute < 1440; minute += 15)
            {
                var local = DateTime.SpecifyKind(date.ToDateTime(TimeOnly.MinValue).AddMinutes(minute), DateTimeKind.Unspecified);

                if (zone.IsInvalidTime(local))
                {
                    return (date, minute);
                }
            }
        }

        return null;
    }

    private static (DateOnly Date, int Minute)? FindAmbiguousLocalTime(TimeZoneInfo zone, DateOnly start, int days)
    {
        for (var i = 0; i < days; i++)
        {
            var date = start.AddDays(i);

            for (var minute = 0; minute < 1440; minute += 15)
            {
                var local = DateTime.SpecifyKind(date.ToDateTime(TimeOnly.MinValue).AddMinutes(minute), DateTimeKind.Unspecified);

                if (zone.IsAmbiguousTime(local))
                {
                    return (date, minute);
                }
            }
        }

        return null;
    }
}
