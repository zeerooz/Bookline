using System.Net.Http.Headers;
using System.Net.Http.Json;
using Bookline.Api.Domain;
using Bookline.Api.Dtos;

namespace Bookline.Tests.Integration;

/// <summary>A fully set-up business: an owner, a service, a staff member and opening hours.</summary>
public record Business(
    string Slug,
    HttpClient Owner,
    string OwnerEmail,
    Guid ServiceId,
    Guid StaffId);

/// <summary>
/// Builds the world a booking test needs. Everything goes through the public
/// API rather than straight into the database, so the fixtures exercise the
/// same validation and authorization a real client would hit.
/// </summary>
public static class Scenario
{
    public const string Password = "integration-test-password";

    public static string UniqueEmail(string prefix = "user") => $"{prefix}-{Guid.NewGuid():N}@example.com";

    public static string UniqueSlug(string prefix = "biz") => $"{prefix}-{Guid.NewGuid():N}"[..24];

    public static async Task<HttpClient> RegisterUserAsync(
        this BooklineFactory factory, string? email = null, string fullName = "Test Person")
    {
        var client = factory.CreateClient();
        email ??= UniqueEmail();

        var response = await client.PostAsJsonAsync("/api/auth/register", new RegisterRequest
        {
            Email = email,
            Password = Password,
            FullName = fullName,
        });

        response.EnsureSuccessStatusCode();
        var auth = await response.Content.ReadFromJsonAsync<AuthResponse>();

        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", auth!.AccessToken);
        return client;
    }

    public static async Task<HttpClient> SignInAsync(this BooklineFactory factory, string email)
    {
        var client = factory.CreateClient();

        var response = await client.PostAsJsonAsync("/api/auth/login", new LoginRequest
        {
            Email = email,
            Password = Password,
        });

        response.EnsureSuccessStatusCode();
        var auth = await response.Content.ReadFromJsonAsync<AuthResponse>();

        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", auth!.AccessToken);
        return client;
    }

    /// <summary>
    /// A business open 09:00-17:00 every day, with one 60-minute service and
    /// one staff member who can deliver it.
    /// </summary>
    public static async Task<Business> CreateBusinessAsync(
        this BooklineFactory factory,
        string? slug = null,
        bool requiresPrepayment = false,
        int durationMinutes = 60,
        int bufferMinutes = 0)
    {
        var ownerEmail = UniqueEmail("owner");
        var owner = await factory.RegisterUserAsync(ownerEmail, "Business Owner");
        slug ??= UniqueSlug();

        var created = await owner.PostAsJsonAsync("/api/businesses", new CreateBusinessRequest
        {
            Name = $"Business {slug}",
            Slug = slug,
            // UTC keeps the arithmetic in these tests obvious; the daylight
            // saving behaviour has its own dedicated unit tests.
            TimeZoneId = "UTC",
            Currency = "EGP",
            MinimumNoticeMinutes = 0,
            BookingHorizonDays = 365,
        });

        created.EnsureSuccessStatusCode();

        var staffResponse = await owner.PostAsJsonAsync($"/api/t/{slug}/staff", new StaffMemberRequest
        {
            DisplayName = "Sara Hassan",
            Title = "Senior stylist",
        });

        staffResponse.EnsureSuccessStatusCode();
        var staff = await staffResponse.Content.ReadFromJsonAsync<StaffMemberResponse>();

        var serviceResponse = await owner.PostAsJsonAsync($"/api/t/{slug}/services", new ServiceRequest
        {
            Name = "Consultation",
            DurationMinutes = durationMinutes,
            BufferMinutes = bufferMinutes,
            PriceMinorUnits = 25_000,
            RequiresPrepayment = requiresPrepayment,
            StaffMemberIds = [staff!.Id],
        });

        serviceResponse.EnsureSuccessStatusCode();
        var service = await serviceResponse.Content.ReadFromJsonAsync<ServiceResponse>();

        var hours = Enumerable.Range(0, 7)
            .Select(day => new BusinessHourRequest { DayOfWeek = day, StartMinute = 9 * 60, EndMinute = 17 * 60 })
            .ToList();

        (await owner.PutAsJsonAsync($"/api/t/{slug}/hours", hours)).EnsureSuccessStatusCode();

        return new Business(slug, owner, ownerEmail, service!.Id, staff.Id);
    }

    /// <summary>A UTC instant on a fixed future date, so tests never straddle "today".</summary>
    public static DateTime SlotAt(int hour, int minute = 0, int addDays = 0) =>
        new DateTime(2027, 3, 15, hour, minute, 0, DateTimeKind.Utc).AddDays(addDays);

    public static CreateBookingRequest BookingAt(Guid serviceId, DateTime startsAt, Guid? staffId = null) => new()
    {
        ServiceId = serviceId,
        StaffMemberId = staffId,
        StartsAtUtc = startsAt,
        CustomerName = "Omar Khaled",
        CustomerEmail = "omar@example.com",
        CustomerPhone = "+201000000000",
    };

    public static Task<HttpResponseMessage> BookAsync(
        this HttpClient client, string slug, CreateBookingRequest request) =>
        client.PostAsJsonAsync($"/api/public/{slug}/bookings", request);

    public static async Task AddMemberAsync(this Business business, string email, TenantRole role)
    {
        var response = await business.Owner.PostAsJsonAsync($"/api/t/{business.Slug}/members",
            new InviteMemberRequest { Email = email, Role = role });

        response.EnsureSuccessStatusCode();
    }
}
