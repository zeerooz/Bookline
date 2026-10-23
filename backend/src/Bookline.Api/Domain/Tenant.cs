namespace Bookline.Api.Domain;

/// <summary>
/// A business using the platform: a salon, a clinic, a studio. Every other
/// record in the system belongs to exactly one tenant, and isolation between
/// tenants is the single most important correctness property here.
/// </summary>
public class Tenant
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// Appears in the public booking URL (/book/{slug}). Unique across the
    /// platform, lower-cased, and the one tenant field customers ever see.
    /// </summary>
    public string Slug { get; set; } = string.Empty;

    /// <summary>IANA identifier, e.g. "Africa/Cairo". Every slot is computed in this zone.</summary>
    public string TimeZoneId { get; set; } = "Africa/Cairo";

    /// <summary>ISO 4217, e.g. "EGP". Prices are stored in this currency's minor units.</summary>
    public string Currency { get; set; } = "EGP";

    /// <summary>How far ahead customers may book, in days.</summary>
    public int BookingHorizonDays { get; set; } = 60;

    /// <summary>Minimum notice before a slot starts, in minutes.</summary>
    public int MinimumNoticeMinutes { get; set; } = 60;

    public bool IsActive { get; set; } = true;

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public List<Membership> Memberships { get; set; } = [];
    public List<Service> Services { get; set; } = [];
    public List<StaffMember> Staff { get; set; } = [];
    public List<BusinessHour> BusinessHours { get; set; } = [];
}
