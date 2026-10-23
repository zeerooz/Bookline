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

/// <summary>
/// Accounts live above tenants: the same person can be staff at one business
/// and a customer at another, so none of these routes carry a tenant slug.
/// </summary>
[ApiController]
[Route("api/auth")]
[EnableRateLimiting(RateLimitPolicies.Authentication)]
public class AuthController(
    UserManager<ApplicationUser> users,
    AppDbContext db,
    ITokenService tokens,
    ICurrentUser currentUser) : ControllerBase
{
    [HttpPost("register")]
    [ProducesResponseType<AuthResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> Register(RegisterRequest request)
    {
        var email = request.Email.Trim().ToLowerInvariant();

        var user = new ApplicationUser
        {
            UserName = email,
            Email = email,
            FullName = request.FullName.Trim(),
        };

        var result = await users.CreateAsync(user, request.Password);

        if (!result.Succeeded)
        {
            // Identity's messages are already user-facing and specific about
            // password rules, which is what a registration form needs.
            return Problem(
                string.Join(' ', result.Errors.Select(e => e.Description)),
                statusCode: StatusCodes.Status400BadRequest);
        }

        return Ok(await BuildAuthResponseAsync(user));
    }

    [HttpPost("login")]
    [ProducesResponseType<AuthResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> Login(LoginRequest request)
    {
        var email = request.Email.Trim().ToLowerInvariant();
        var user = await users.FindByEmailAsync(email);

        if (user is null || !await users.CheckPasswordAsync(user, request.Password))
        {
            // One message for both cases, so this cannot be used to find out
            // which email addresses are registered.
            return Problem("Email or password is incorrect.", statusCode: StatusCodes.Status401Unauthorized);
        }

        if (await users.IsLockedOutAsync(user))
        {
            return Problem("This account is temporarily locked. Try again shortly.",
                statusCode: StatusCodes.Status401Unauthorized);
        }

        return Ok(await BuildAuthResponseAsync(user));
    }

    [HttpGet("me")]
    [Authorize]
    [ProducesResponseType<UserResponse>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Me()
    {
        var user = await users.FindByIdAsync(currentUser.RequireUserId().ToString());

        return user is null ? NotFound() : Ok(await BuildUserResponseAsync(user));
    }

    private async Task<AuthResponse> BuildAuthResponseAsync(ApplicationUser user) =>
        new(tokens.CreateAccessToken(user), tokens.ExpiresInSeconds, await BuildUserResponseAsync(user));

    private async Task<UserResponse> BuildUserResponseAsync(ApplicationUser user)
    {
        // IgnoreQueryFilters is correct here and only here: the caller is
        // asking which businesses *they* belong to, which is inherently a
        // cross-tenant question, and the query is still pinned to their own id.
        var memberships = await db.Memberships
            .IgnoreQueryFilters()
            .AsNoTracking()
            .Where(m => m.UserId == user.Id)
            .Select(m => new MembershipSummary(m.TenantId, m.Tenant!.Name, m.Tenant.Slug, m.Role))
            .ToListAsync();

        return new UserResponse(user.Id, user.Email ?? string.Empty, user.FullName, memberships);
    }
}
