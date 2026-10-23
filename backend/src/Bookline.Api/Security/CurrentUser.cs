using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;

namespace Bookline.Api.Security;

public interface ICurrentUser
{
    Guid? UserId { get; }
    string? Email { get; }
    Guid RequireUserId();
}

public class CurrentUser(IHttpContextAccessor accessor) : ICurrentUser
{
    public Guid? UserId
    {
        get
        {
            var value = Principal?.FindFirstValue(JwtRegisteredClaimNames.Sub)
                        ?? Principal?.FindFirstValue(ClaimTypes.NameIdentifier);

            return Guid.TryParse(value, out var id) ? id : null;
        }
    }

    public string? Email =>
        Principal?.FindFirstValue(JwtRegisteredClaimNames.Email) ?? Principal?.FindFirstValue(ClaimTypes.Email);

    public Guid RequireUserId() =>
        UserId ?? throw new InvalidOperationException("No authenticated user on the current request.");

    private ClaimsPrincipal? Principal => accessor.HttpContext?.User;
}
