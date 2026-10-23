using Bookline.Api.Domain;
using Microsoft.AspNetCore.Authorization;

namespace Bookline.Api.Security;

public static class Policies
{
    /// <summary>Any member of the resolved tenant, including customers.</summary>
    public const string TenantMember = "TenantMember";

    /// <summary>Staff or Owner.</summary>
    public const string TenantStaff = "TenantStaff";

    /// <summary>Owner only: billing, members, deleting the business.</summary>
    public const string TenantOwner = "TenantOwner";
}

/// <summary>
/// Requires the caller to hold at least <see cref="Minimum"/> within the tenant
/// the current request resolved to. Roles are per-tenant, so this is checked
/// against the membership table rather than against claims on the token: a
/// token issued before someone was demoted must not keep working.
/// </summary>
public class TenantRoleRequirement(TenantRole minimum) : IAuthorizationRequirement
{
    public TenantRole Minimum { get; } = minimum;
}
