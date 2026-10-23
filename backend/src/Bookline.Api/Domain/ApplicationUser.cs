using Microsoft.AspNetCore.Identity;

namespace Bookline.Api.Domain;

/// <summary>
/// A person. Deliberately NOT tenant-owned: the same human may be staff at one
/// business and a customer at another, so the tenant relationship lives on
/// <see cref="Membership"/> rather than on the account itself.
/// </summary>
public class ApplicationUser : IdentityUser<Guid>
{
    public string FullName { get; set; } = string.Empty;

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public List<Membership> Memberships { get; set; } = [];
}
