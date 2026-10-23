using System.Net;
using System.Net.Http.Json;
using Bookline.Api.Domain;
using Bookline.Api.Dtos;

namespace Bookline.Tests.Integration;

/// <summary>
/// Roles are per business, and they are read from the database on every
/// request rather than baked into the token. These tests pin both halves of
/// that: what each role can reach, and that a change takes effect at once.
/// </summary>
public class AuthorizationTests(BooklineFactory factory) : IClassFixture<BooklineFactory>
{
    [Fact]
    public async Task Management_routes_reject_anonymous_callers()
    {
        var business = await factory.CreateBusinessAsync();
        var anonymous = factory.CreateClient();

        var response = await anonymous.GetAsync($"/api/t/{business.Slug}/services");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task A_signed_in_stranger_is_forbidden_not_unauthorised()
    {
        var business = await factory.CreateBusinessAsync();
        var stranger = await factory.RegisterUserAsync();

        var response = await stranger.GetAsync($"/api/t/{business.Slug}/services");

        // Authenticated but not a member: 403, which is the honest answer.
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task A_customer_cannot_reach_the_management_api()
    {
        var business = await factory.CreateBusinessAsync();

        var email = Scenario.UniqueEmail("customer");
        await factory.RegisterUserAsync(email);
        await business.AddMemberAsync(email, TenantRole.Customer);

        var customer = await factory.SignInAsync(email);

        Assert.Equal(HttpStatusCode.Forbidden,
            (await customer.GetAsync($"/api/t/{business.Slug}/services")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden,
            (await customer.GetAsync($"/api/t/{business.Slug}/appointments")).StatusCode);
    }

    [Fact]
    public async Task Staff_can_read_the_catalogue_but_not_change_it()
    {
        var business = await factory.CreateBusinessAsync();

        var email = Scenario.UniqueEmail("staff");
        await factory.RegisterUserAsync(email);
        await business.AddMemberAsync(email, TenantRole.Staff);

        var staff = await factory.SignInAsync(email);

        (await staff.GetAsync($"/api/t/{business.Slug}/services")).EnsureSuccessStatusCode();

        var created = await staff.PostAsJsonAsync($"/api/t/{business.Slug}/services",
            new ServiceRequest { Name = "Unauthorised", DurationMinutes = 30 });

        Assert.Equal(HttpStatusCode.Forbidden, created.StatusCode);
    }

    [Fact]
    public async Task Staff_cannot_read_the_audit_log()
    {
        var business = await factory.CreateBusinessAsync();

        var email = Scenario.UniqueEmail("staff");
        await factory.RegisterUserAsync(email);
        await business.AddMemberAsync(email, TenantRole.Staff);

        var staff = await factory.SignInAsync(email);

        Assert.Equal(HttpStatusCode.Forbidden, (await staff.GetAsync($"/api/t/{business.Slug}/audit")).StatusCode);
    }

    [Fact]
    public async Task Staff_see_only_their_own_appointments()
    {
        var business = await factory.CreateBusinessAsync();

        // A second staff member, linked to a real account.
        var email = Scenario.UniqueEmail("stylist");
        await factory.RegisterUserAsync(email, "Nour Adel");
        await business.AddMemberAsync(email, TenantRole.Staff);

        var guest = factory.CreateClient();
        (await guest.BookAsync(business.Slug, Scenario.BookingAt(business.ServiceId, Scenario.SlotAt(10))))
            .EnsureSuccessStatusCode();

        var staff = await factory.SignInAsync(email);

        var theirDiary = await staff.GetFromJsonAsync<List<AppointmentResponse>>(
            $"/api/t/{business.Slug}/appointments?fromUtc=2027-01-01&toUtc=2028-01-01");

        // The booking belongs to Sara, and this account is not linked to her
        // staff record, so it must not appear.
        Assert.Empty(theirDiary!);

        var ownerDiary = await business.Owner.GetFromJsonAsync<List<AppointmentResponse>>(
            $"/api/t/{business.Slug}/appointments?fromUtc=2027-01-01&toUtc=2028-01-01");

        Assert.Single(ownerDiary!);
    }

    [Fact]
    public async Task Revoking_membership_takes_effect_on_the_next_request()
    {
        var business = await factory.CreateBusinessAsync();

        var email = Scenario.UniqueEmail("temp");
        await factory.RegisterUserAsync(email);
        await business.AddMemberAsync(email, TenantRole.Staff);

        // Token issued while they were still a member.
        var member = await factory.SignInAsync(email);
        (await member.GetAsync($"/api/t/{business.Slug}/services")).EnsureSuccessStatusCode();

        var userId = (await member.GetFromJsonAsync<UserResponse>("/api/auth/me"))!.Id;
        (await business.Owner.DeleteAsync($"/api/t/{business.Slug}/members/{userId}")).EnsureSuccessStatusCode();

        // Same token, now worthless — because the role was never in the token.
        var after = await member.GetAsync($"/api/t/{business.Slug}/services");

        Assert.Equal(HttpStatusCode.Forbidden, after.StatusCode);
    }

    [Fact]
    public async Task A_business_cannot_be_left_without_an_owner()
    {
        var business = await factory.CreateBusinessAsync();
        var ownerId = (await business.Owner.GetFromJsonAsync<UserResponse>("/api/auth/me"))!.Id;

        var demote = await business.Owner.PutAsJsonAsync(
            $"/api/t/{business.Slug}/members/{ownerId}", TenantRole.Staff);

        Assert.Equal(HttpStatusCode.BadRequest, demote.StatusCode);
    }

    [Fact]
    public async Task An_owner_cannot_remove_themselves()
    {
        var business = await factory.CreateBusinessAsync();
        var ownerId = (await business.Owner.GetFromJsonAsync<UserResponse>("/api/auth/me"))!.Id;

        var response = await business.Owner.DeleteAsync($"/api/t/{business.Slug}/members/{ownerId}");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task A_duplicate_business_address_is_rejected()
    {
        var business = await factory.CreateBusinessAsync();
        var other = await factory.RegisterUserAsync();

        var response = await other.PostAsJsonAsync("/api/businesses", new CreateBusinessRequest
        {
            Name = "Copycat",
            Slug = business.Slug,
            TimeZoneId = "UTC",
            Currency = "EGP",
        });

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
    }

    [Fact]
    public async Task An_unknown_time_zone_is_rejected_at_creation()
    {
        var user = await factory.RegisterUserAsync();

        var response = await user.PostAsJsonAsync("/api/businesses", new CreateBusinessRequest
        {
            Name = "Nowhere",
            Slug = Scenario.UniqueSlug(),
            TimeZoneId = "Mars/Olympus_Mons",
            Currency = "EGP",
        });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Login_does_not_reveal_whether_an_email_is_registered()
    {
        var email = Scenario.UniqueEmail();
        await factory.RegisterUserAsync(email);

        var client = factory.CreateClient();

        var wrongPassword = await client.PostAsJsonAsync("/api/auth/login",
            new LoginRequest { Email = email, Password = "definitely-not-the-password" });

        var noSuchUser = await client.PostAsJsonAsync("/api/auth/login",
            new LoginRequest { Email = Scenario.UniqueEmail(), Password = "definitely-not-the-password" });

        Assert.Equal(noSuchUser.StatusCode, wrongPassword.StatusCode);

        // Compare the meaningful fields rather than the raw body: ProblemDetails
        // carries a per-request traceId that differs by design and leaks nothing.
        var a = await noSuchUser.Content.ReadFromJsonAsync<Microsoft.AspNetCore.Mvc.ProblemDetails>();
        var b = await wrongPassword.Content.ReadFromJsonAsync<Microsoft.AspNetCore.Mvc.ProblemDetails>();

        Assert.Equal(a!.Title, b!.Title);
        Assert.Equal(a.Detail, b.Detail);
        Assert.Equal(a.Status, b.Status);
    }
}
