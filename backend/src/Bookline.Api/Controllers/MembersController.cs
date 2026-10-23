using Bookline.Api.Data;
using Bookline.Api.Domain;
using Bookline.Api.Dtos;
using Bookline.Api.Security;
using Bookline.Api.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;

namespace Bookline.Api.Controllers;

[ApiController]
[Route("api/t/{tenantSlug}/members")]
[Authorize(Policy = Policies.TenantOwner)]
[EnableRateLimiting(RateLimitPolicies.Api)]
public class MembersController(
    AppDbContext db,
    UserManager<ApplicationUser> users,
    ICurrentUser currentUser,
    IAuditService audit) : ControllerBase
{
    [HttpGet]
    [ProducesResponseType<IReadOnlyList<MemberResponse>>(StatusCodes.Status200OK)]
    public async Task<ActionResult<IReadOnlyList<MemberResponse>>> GetAll(CancellationToken ct) =>
        Ok(await db.Memberships
            .AsNoTracking()
            .OrderBy(m => m.CreatedAt)
            .Select(m => new MemberResponse(
                m.UserId, m.User!.Email ?? string.Empty, m.User.FullName, m.Role, m.CreatedAt))
            .ToListAsync(ct));

    /// <summary>
    /// Adds an existing account to this business. There is no email invitation
    /// flow yet, so the person must already have registered — that limitation
    /// is in the README rather than hidden behind a silent failure.
    /// </summary>
    [HttpPost]
    [ProducesResponseType<MemberResponse>(StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Add(InviteMemberRequest request, CancellationToken ct)
    {
        var email = request.Email.Trim().ToLowerInvariant();
        var user = await users.FindByEmailAsync(email);

        if (user is null)
        {
            return Problem("No account exists with that email address. Ask them to sign up first.",
                statusCode: StatusCodes.Status404NotFound);
        }

        if (await db.Memberships.AnyAsync(m => m.UserId == user.Id, ct))
        {
            return Problem("That person is already a member of this business.",
                statusCode: StatusCodes.Status409Conflict);
        }

        var membership = new Membership { UserId = user.Id, Role = request.Role };
        db.Memberships.Add(membership);

        audit.Record("member.added", nameof(Membership), user.Id.ToString(), $"{email} as {request.Role}");
        await db.SaveChangesAsync(ct);

        return Created($"/api/t/{RouteData.Values["tenantSlug"]}/members",
            new MemberResponse(user.Id, email, user.FullName, request.Role, membership.CreatedAt));
    }

    [HttpPut("{userId:guid}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public async Task<IActionResult> ChangeRole(Guid userId, [FromBody] TenantRole role, CancellationToken ct)
    {
        var membership = await db.Memberships.FirstOrDefaultAsync(m => m.UserId == userId, ct);

        if (membership is null)
        {
            return NotFound();
        }

        if (membership.Role == TenantRole.Owner && role != TenantRole.Owner && !await HasAnotherOwnerAsync(userId, ct))
        {
            // Locking everyone out of their own business is not a state the
            // API should let someone reach by accident.
            return Problem("A business must keep at least one owner.", statusCode: StatusCodes.Status400BadRequest);
        }

        membership.Role = role;
        audit.Record("member.role_changed", nameof(Membership), userId.ToString(), role.ToString());
        await db.SaveChangesAsync(ct);

        return NoContent();
    }

    [HttpDelete("{userId:guid}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public async Task<IActionResult> Remove(Guid userId, CancellationToken ct)
    {
        if (userId == currentUser.RequireUserId())
        {
            return Problem("Remove another owner first, then ask them to remove you.",
                statusCode: StatusCodes.Status400BadRequest);
        }

        var membership = await db.Memberships.FirstOrDefaultAsync(m => m.UserId == userId, ct);

        if (membership is null)
        {
            return NotFound();
        }

        if (membership.Role == TenantRole.Owner && !await HasAnotherOwnerAsync(userId, ct))
        {
            return Problem("A business must keep at least one owner.", statusCode: StatusCodes.Status400BadRequest);
        }

        db.Memberships.Remove(membership);
        audit.Record("member.removed", nameof(Membership), userId.ToString(), null);
        await db.SaveChangesAsync(ct);

        return NoContent();
    }

    private Task<bool> HasAnotherOwnerAsync(Guid excludingUserId, CancellationToken ct) =>
        db.Memberships.AnyAsync(m => m.Role == TenantRole.Owner && m.UserId != excludingUserId, ct);
}

[ApiController]
[Route("api/t/{tenantSlug}/audit")]
[Authorize(Policy = Policies.TenantOwner)]
[EnableRateLimiting(RateLimitPolicies.Api)]
public class AuditController(AppDbContext db) : ControllerBase
{
    [HttpGet]
    [ProducesResponseType<IReadOnlyList<AuditEntryResponse>>(StatusCodes.Status200OK)]
    public async Task<ActionResult<IReadOnlyList<AuditEntryResponse>>> GetAll(
        [FromQuery] int take = 100, CancellationToken ct = default) =>
        Ok(await db.AuditEntries
            .AsNoTracking()
            .OrderByDescending(a => a.Id)
            .Take(Math.Clamp(take, 1, 500))
            .Select(a => new AuditEntryResponse(
                a.Id, a.Action, a.EntityType, a.EntityId, a.Detail, a.ActorEmail, a.CreatedAt))
            .ToListAsync(ct));
}
