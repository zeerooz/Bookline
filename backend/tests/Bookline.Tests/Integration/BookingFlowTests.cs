using System.Net;
using System.Net.Http.Json;
using Bookline.Api.Domain;
using Bookline.Api.Dtos;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Bookline.Tests.Integration;

public class BookingFlowTests(BooklineFactory factory) : IClassFixture<BooklineFactory>
{
    [Fact]
    public async Task A_guest_can_book_without_an_account()
    {
        var business = await factory.CreateBusinessAsync();
        var guest = factory.CreateClient();

        var response = await guest.BookAsync(business.Slug, Scenario.BookingAt(business.ServiceId, Scenario.SlotAt(10)));

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var confirmation = await response.Content.ReadFromJsonAsync<BookingConfirmation>();

        Assert.Equal(AppointmentStatus.Confirmed, confirmation!.Appointment.Status);
        Assert.False(string.IsNullOrWhiteSpace(confirmation.ManagementToken));
        // The price is snapshotted onto the appointment, not read live later.
        Assert.Equal(25_000, confirmation.Appointment.PriceMinorUnits);
    }

    [Fact]
    public async Task The_same_slot_cannot_be_booked_twice()
    {
        var business = await factory.CreateBusinessAsync();
        var guest = factory.CreateClient();
        var slot = Scenario.SlotAt(10);

        (await guest.BookAsync(business.Slug, Scenario.BookingAt(business.ServiceId, slot))).EnsureSuccessStatusCode();

        var second = await guest.BookAsync(business.Slug, Scenario.BookingAt(business.ServiceId, slot));

        Assert.Equal(HttpStatusCode.Conflict, second.StatusCode);
    }

    [Fact]
    public async Task An_overlapping_slot_cannot_be_booked()
    {
        var business = await factory.CreateBusinessAsync();
        var guest = factory.CreateClient();

        (await guest.BookAsync(business.Slug, Scenario.BookingAt(business.ServiceId, Scenario.SlotAt(10))))
            .EnsureSuccessStatusCode();

        // 10:30 starts inside the 10:00-11:00 appointment.
        var overlapping = await guest.BookAsync(
            business.Slug, Scenario.BookingAt(business.ServiceId, Scenario.SlotAt(10, 30)));

        Assert.Equal(HttpStatusCode.Conflict, overlapping.StatusCode);
    }

    [Fact]
    public async Task A_slot_that_only_touches_another_is_allowed()
    {
        var business = await factory.CreateBusinessAsync();
        var guest = factory.CreateClient();

        (await guest.BookAsync(business.Slug, Scenario.BookingAt(business.ServiceId, Scenario.SlotAt(10))))
            .EnsureSuccessStatusCode();

        // 11:00 begins exactly when the previous appointment ends.
        var adjacent = await guest.BookAsync(
            business.Slug, Scenario.BookingAt(business.ServiceId, Scenario.SlotAt(11)));

        Assert.Equal(HttpStatusCode.Created, adjacent.StatusCode);
    }

    [Fact]
    public async Task A_buffer_blocks_the_slot_that_follows()
    {
        var business = await factory.CreateBusinessAsync(durationMinutes: 60, bufferMinutes: 30);
        var guest = factory.CreateClient();

        (await guest.BookAsync(business.Slug, Scenario.BookingAt(business.ServiceId, Scenario.SlotAt(10))))
            .EnsureSuccessStatusCode();

        // The 10:00 booking runs to 11:00 plus 30 minutes of buffer.
        var tooSoon = await guest.BookAsync(
            business.Slug, Scenario.BookingAt(business.ServiceId, Scenario.SlotAt(11)));
        Assert.Equal(HttpStatusCode.Conflict, tooSoon.StatusCode);

        var afterBuffer = await guest.BookAsync(
            business.Slug, Scenario.BookingAt(business.ServiceId, Scenario.SlotAt(11, 30)));
        Assert.Equal(HttpStatusCode.Created, afterBuffer.StatusCode);
    }

    [Fact]
    public async Task A_booking_outside_opening_hours_is_refused()
    {
        var business = await factory.CreateBusinessAsync();
        var guest = factory.CreateClient();

        // The business opens 09:00-17:00; 07:00 is before that.
        var response = await guest.BookAsync(
            business.Slug, Scenario.BookingAt(business.ServiceId, Scenario.SlotAt(7)));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task A_booking_that_would_run_past_closing_is_refused()
    {
        var business = await factory.CreateBusinessAsync();
        var guest = factory.CreateClient();

        // 16:30 plus an hour ends at 17:30, past the 17:00 close.
        var response = await guest.BookAsync(
            business.Slug, Scenario.BookingAt(business.ServiceId, Scenario.SlotAt(16, 30)));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task A_booking_in_the_past_is_refused()
    {
        var business = await factory.CreateBusinessAsync();
        var guest = factory.CreateClient();

        var response = await guest.BookAsync(business.Slug, Scenario.BookingAt(
            business.ServiceId, new DateTime(2020, 1, 1, 10, 0, 0, DateTimeKind.Utc)));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Availability_stops_offering_a_slot_once_it_is_taken()
    {
        var business = await factory.CreateBusinessAsync();
        var guest = factory.CreateClient();
        var slot = Scenario.SlotAt(10);
        var date = DateOnly.FromDateTime(slot);

        var before = await GetSlotsAsync(guest, business.Slug, business.ServiceId, date);
        Assert.Contains(slot, before);

        (await guest.BookAsync(business.Slug, Scenario.BookingAt(business.ServiceId, slot))).EnsureSuccessStatusCode();

        var after = await GetSlotsAsync(guest, business.Slug, business.ServiceId, date);
        Assert.DoesNotContain(slot, after);
    }

    [Fact]
    public async Task Cancelling_frees_the_slot_again()
    {
        var business = await factory.CreateBusinessAsync();
        var guest = factory.CreateClient();
        var slot = Scenario.SlotAt(12);

        var created = await guest.BookAsync(business.Slug, Scenario.BookingAt(business.ServiceId, slot));
        var confirmation = await created.Content.ReadFromJsonAsync<BookingConfirmation>();

        var cancelled = await guest.PostAsJsonAsync(
            $"/api/public/{business.Slug}/bookings/{confirmation!.ManagementToken}/cancel",
            new CancelBookingRequest { Reason = "Something came up" });

        cancelled.EnsureSuccessStatusCode();

        var rebooked = await guest.BookAsync(business.Slug, Scenario.BookingAt(business.ServiceId, slot));
        Assert.Equal(HttpStatusCode.Created, rebooked.StatusCode);
    }

    [Fact]
    public async Task A_management_token_is_required_to_see_a_booking()
    {
        var business = await factory.CreateBusinessAsync();
        var guest = factory.CreateClient();

        var created = await guest.BookAsync(business.Slug, Scenario.BookingAt(business.ServiceId, Scenario.SlotAt(13)));
        var confirmation = await created.Content.ReadFromJsonAsync<BookingConfirmation>();

        var withToken = await guest.GetAsync(
            $"/api/public/{business.Slug}/bookings/{confirmation!.ManagementToken}");
        Assert.Equal(HttpStatusCode.OK, withToken.StatusCode);

        var guessed = await guest.GetAsync($"/api/public/{business.Slug}/bookings/not-a-real-token");
        Assert.Equal(HttpStatusCode.NotFound, guessed.StatusCode);
    }

    [Fact]
    public async Task A_service_nobody_can_deliver_is_hidden_from_customers()
    {
        var business = await factory.CreateBusinessAsync();

        var orphan = await business.Owner.PostAsJsonAsync($"/api/t/{business.Slug}/services", new ServiceRequest
        {
            Name = "Nobody offers this",
            DurationMinutes = 30,
            StaffMemberIds = [],
        });

        orphan.EnsureSuccessStatusCode();

        var guest = factory.CreateClient();
        var visible = await guest.GetFromJsonAsync<List<ServiceResponse>>($"/api/public/{business.Slug}/services");

        Assert.Single(visible!);
        Assert.Equal(business.ServiceId, visible![0].Id);
    }

    [Fact]
    public async Task Booking_a_service_the_chosen_staff_member_does_not_offer_is_refused()
    {
        var business = await factory.CreateBusinessAsync();

        var otherStaff = await business.Owner.PostAsJsonAsync($"/api/t/{business.Slug}/staff",
            new StaffMemberRequest { DisplayName = "Nour Adel", ServiceIds = [] });

        var created = await otherStaff.Content.ReadFromJsonAsync<StaffMemberResponse>();

        var guest = factory.CreateClient();
        var response = await guest.BookAsync(business.Slug,
            Scenario.BookingAt(business.ServiceId, Scenario.SlotAt(14), created!.Id));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task A_confirmed_booking_queues_a_confirmation_email()
    {
        var business = await factory.CreateBusinessAsync();
        var guest = factory.CreateClient();

        var created = await guest.BookAsync(business.Slug, Scenario.BookingAt(business.ServiceId, Scenario.SlotAt(15)));
        created.EnsureSuccessStatusCode();

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<Bookline.Api.Data.AppDbContext>();

        // Read past the tenant filter: this is a test assertion, not a request.
        var queued = await db.OutboxMessages
            .IgnoreQueryFilters()
            .Where(m => m.Recipient == "omar@example.com")
            .ToListAsync();

        Assert.Contains(queued, m => m.Kind == NotificationKind.BookingConfirmed);
        // A reminder is scheduled ahead of the appointment, not sent now.
        Assert.Contains(queued, m => m.Kind == NotificationKind.BookingReminder && m.SendAfterUtc > DateTime.UtcNow);
    }

    private static async Task<List<DateTime>> GetSlotsAsync(
        HttpClient client, string slug, Guid serviceId, DateOnly date)
    {
        var response = await client.GetFromJsonAsync<List<AvailabilityResponse>>(
            $"/api/public/{slug}/availability?serviceId={serviceId}&from={date:yyyy-MM-dd}&to={date:yyyy-MM-dd}");

        return response!.SelectMany(a => a.Slots).ToList();
    }
}
