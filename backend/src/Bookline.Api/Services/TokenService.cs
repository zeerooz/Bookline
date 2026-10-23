using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using Bookline.Api.Domain;
using Bookline.Api.Security;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;

namespace Bookline.Api.Services;

public interface ITokenService
{
    string CreateAccessToken(ApplicationUser user);
    int ExpiresInSeconds { get; }
}

public class TokenService(IOptions<JwtOptions> options) : ITokenService
{
    private readonly JwtOptions _jwt = options.Value;

    public int ExpiresInSeconds => _jwt.AccessTokenMinutes * 60;

    public string CreateAccessToken(ApplicationUser user)
    {
        var key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(_jwt.SigningKey));
        var credentials = new SigningCredentials(key, SecurityAlgorithms.HmacSha256);

        // Deliberately no tenant or role claims. Roles are per-tenant and are
        // read from the database on each request, so removing someone from a
        // business takes effect immediately rather than when their token
        // happens to expire.
        Claim[] claims =
        [
            new(JwtRegisteredClaimNames.Sub, user.Id.ToString()),
            new(JwtRegisteredClaimNames.Email, user.Email ?? string.Empty),
            new(JwtRegisteredClaimNames.Jti, Guid.NewGuid().ToString()),
        ];

        var token = new JwtSecurityToken(
            issuer: _jwt.Issuer,
            audience: _jwt.Audience,
            claims: claims,
            notBefore: DateTime.UtcNow,
            expires: DateTime.UtcNow.AddMinutes(_jwt.AccessTokenMinutes),
            signingCredentials: credentials);

        return new JwtSecurityTokenHandler().WriteToken(token);
    }
}
