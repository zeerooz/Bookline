using System.ComponentModel.DataAnnotations;
using Bookline.Api.Domain;

namespace Bookline.Api.Dtos;

public record RegisterRequest
{
    [Required, EmailAddress, MaxLength(256)] public string Email { get; init; } = string.Empty;

    [Required, MinLength(10), MaxLength(128)]
    public string Password { get; init; } = string.Empty;

    [Required, MaxLength(200)] public string FullName { get; init; } = string.Empty;
}

public record LoginRequest
{
    [Required, EmailAddress, MaxLength(256)] public string Email { get; init; } = string.Empty;
    [Required, MaxLength(128)] public string Password { get; init; } = string.Empty;
}

public record AuthResponse(string AccessToken, int ExpiresInSeconds, UserResponse User);

public record MembershipSummary(Guid TenantId, string TenantName, string TenantSlug, TenantRole Role);

public record UserResponse(Guid Id, string Email, string FullName, IReadOnlyList<MembershipSummary> Memberships);

public record CreateBusinessRequest
{
    [Required, MaxLength(200)] public string Name { get; init; } = string.Empty;

    /// <summary>Lower-case letters, digits and hyphens. Becomes the public booking URL.</summary>
    [Required, MaxLength(64), RegularExpression("^[a-z0-9]+(-[a-z0-9]+)*$",
        ErrorMessage = "Use lower-case letters, numbers and hyphens, for example bright-smile-clinic.")]
    public string Slug { get; init; } = string.Empty;

    [Required, MaxLength(64)] public string TimeZoneId { get; init; } = "Africa/Cairo";
    [Required, MinLength(3), MaxLength(3)] public string Currency { get; init; } = "EGP";

    [Range(1, 365)] public int BookingHorizonDays { get; init; } = 60;
    [Range(0, 10_080)] public int MinimumNoticeMinutes { get; init; } = 60;
}

public record InviteMemberRequest
{
    [Required, EmailAddress, MaxLength(256)] public string Email { get; init; } = string.Empty;
    public TenantRole Role { get; init; } = TenantRole.Staff;
}

public record MemberResponse(Guid UserId, string Email, string FullName, TenantRole Role, DateTime JoinedAt);
