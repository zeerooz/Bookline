using System.Net;
using System.Net.Http.Json;
using Bookline.Api.Domain;
using Bookline.Api.Dtos;

namespace Bookline.Tests.Integration;

/// <summary>
/// The property the whole product rests on: one business must never be able to
/// see or touch another's data, however it asks.
/// </summary>
public class TenantIsolationTests(BooklineFactory factory) : IClassFixture<BooklineFactory>
{
    [Fact]
    public async Task An_owner_cannot_read_another_businesss_services()
    {
        var mine = await factory.CreateBusinessAsync();
        var theirs = await factory.CreateBusinessAsync();

        // My credentials, their slug.
        var response = await mine.Owner.GetAsync($"/api/t/{theirs.Slug}/services");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task An_owner_cannot_modify_another_businesss_service_by_id()
    {
        var mine = await factory.CreateBusinessAsync();
        var theirs = await factory.CreateBusinessAsync();

        var response = await mine.Owner.PutAsJsonAsync($"/api/t/{mine.Slug}/services/{theirs.ServiceId}",
            new ServiceRequest { Name = "Hijacked", DurationMinutes = 30 });

        // Routed through my own tenant, so authorization passes — and the
        // global query filter still makes their row invisible.
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task A_service_list_contains_only_the_callers_own_services()
    {
        var mine = await factory.CreateBusinessAsync();
        await factory.CreateBusinessAsync();

        var services = await mine.Owner.GetFromJsonAsync<List<ServiceResponse>>($"/api/t/{mine.Slug}/services");

        Assert.Single(services!);
        Assert.Equal(mine.ServiceId, services![0].Id);
    }

    [Fact]
    public async Task Staff_from_one_business_cannot_be_attached_to_anothers_service()
    {
        var mine = await factory.CreateBusinessAsync();
        var theirs = await factory.CreateBusinessAsync();

        var response = await mine.Owner.PostAsJsonAsync($"/api/t/{mine.Slug}/services", new ServiceRequest
        {
            Name = "Cross-tenant attempt",
            DurationMinutes = 30,
            StaffMemberIds = [theirs.StaffId],
        });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task A_booking_made_at_one_business_is_invisible_to_another()
    {
        var mine = await factory.CreateBusinessAsync();
        var theirs = await factory.CreateBusinessAsync();

        var guest = factory.CreateClient();
        var booked = await guest.BookAsync(mine.Slug, Scenario.BookingAt(mine.ServiceId, Scenario.SlotAt(10)));
        booked.EnsureSuccessStatusCode();

        var theirAppointments = await theirs.Owner.GetFromJsonAsync<List<AppointmentResponse>>(
            $"/api/t/{theirs.Slug}/appointments?fromUtc=2027-01-01&toUtc=2028-01-01");

        Assert.Empty(theirAppointments!);
    }

    [Fact]
    public async Task One_businesss_bookings_do_not_block_anothers_calendar()
    {
        var mine = await factory.CreateBusinessAsync();
        var theirs = await factory.CreateBusinessAsync();

        var guest = factory.CreateClient();
        var slot = Scenario.SlotAt(11);

        (await guest.BookAsync(mine.Slug, Scenario.BookingAt(mine.ServiceId, slot))).EnsureSuccessStatusCode();

        // Same wall-clock time, different business. Must be perfectly fine.
        var second = await guest.BookAsync(theirs.Slug, Scenario.BookingAt(theirs.ServiceId, slot));

        Assert.Equal(HttpStatusCode.Created, second.StatusCode);
    }

    [Fact]
    public async Task An_unknown_business_slug_returns_not_found()
    {
        var client = factory.CreateClient();

        var response = await client.GetAsync("/api/public/no-such-business/services");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task The_audit_log_shows_only_the_callers_own_events()
    {
        var mine = await factory.CreateBusinessAsync();
        await factory.CreateBusinessAsync();

        var entries = await mine.Owner.GetFromJsonAsync<List<AuditEntryResponse>>($"/api/t/{mine.Slug}/audit");

        Assert.NotEmpty(entries!);
        // Setting up one business records exactly its own staff, service and
        // hours changes — three events, with nothing from the other business.
        Assert.Equal(3, entries!.Count);
        Assert.Contains(entries, e => e.Action == "service.created");
        Assert.Contains(entries, e => e.Action == "staff.created");
        Assert.Contains(entries, e => e.Action == "hours.replaced");
    }

    [Fact]
    public async Task Membership_of_one_business_grants_nothing_at_another()
    {
        var mine = await factory.CreateBusinessAsync();
        var theirs = await factory.CreateBusinessAsync();

        var staffEmail = Scenario.UniqueEmail("staff");
        await factory.RegisterUserAsync(staffEmail);
        await mine.AddMemberAsync(staffEmail, TenantRole.Owner);

        var staffClient = await factory.SignInAsync(staffEmail);

        // Owner at one business, nobody at the other.
        (await staffClient.GetAsync($"/api/t/{mine.Slug}/services")).EnsureSuccessStatusCode();

        var elsewhere = await staffClient.GetAsync($"/api/t/{theirs.Slug}/services");
        Assert.Equal(HttpStatusCode.Forbidden, elsewhere.StatusCode);
    }

    [Fact]
    public async Task Me_reports_every_business_the_user_belongs_to()
    {
        var first = await factory.CreateBusinessAsync();

        var email = Scenario.UniqueEmail("multi");
        await factory.RegisterUserAsync(email);
        await first.AddMemberAsync(email, TenantRole.Staff);

        var client = await factory.SignInAsync(email);
        var me = await client.GetFromJsonAsync<UserResponse>("/api/auth/me");

        // Cross-tenant by design: this is the "which businesses am I in" list.
        Assert.Single(me!.Memberships);
        Assert.Equal(first.Slug, me.Memberships[0].TenantSlug);
        Assert.Equal(TenantRole.Staff, me.Memberships[0].Role);
    }
}
