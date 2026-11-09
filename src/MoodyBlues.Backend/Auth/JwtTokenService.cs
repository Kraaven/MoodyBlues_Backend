using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using Microsoft.IdentityModel.Tokens;
using MoodyBlues.Backend.Config;
using MoodyBlues.Backend.Data;

namespace MoodyBlues.Backend.Auth;

/// <summary>
/// Issues and validates the dashboard's short-lived access tokens (JWT, HS256). The dashboard never
/// sees them: they travel in an httpOnly cookie (<see cref="AuthCookies"/>), renewed through
/// <c>POST /auth/refresh</c>. A Bearer header is still accepted, for scripts and tests.
/// </summary>
public sealed class JwtTokenService(ServerConfig config)
{
    public const string UserIdClaimType = ClaimTypes.NameIdentifier;

    private const string Issuer = "moodyblues-backend";
    private const string Audience = "moodyblues-dashboard";

    public TimeSpan AccessTokenLifetime => config.AccessTokenLifetime;

    public string IssueToken(User user)
    {
        var key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(config.JwtSecret));
        var credentials = new SigningCredentials(key, SecurityAlgorithms.HmacSha256);

        var claims = new[]
        {
            new Claim(UserIdClaimType, user.Id.ToString()),
            new Claim(ClaimTypes.Email, user.Email),
        };

        var token = new JwtSecurityToken(
            issuer: Issuer,
            audience: Audience,
            claims: claims,
            expires: DateTime.UtcNow.Add(config.AccessTokenLifetime),
            signingCredentials: credentials);

        return new JwtSecurityTokenHandler().WriteToken(token);
    }

    public TokenValidationParameters BuildValidationParameters()
    {
        return new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidIssuer = Issuer,
            ValidateAudience = true,
            ValidAudience = Audience,
            ValidateIssuerSigningKey = true,
            IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(config.JwtSecret)),
            ValidateLifetime = true,
            ClockSkew = TimeSpan.FromSeconds(30),
        };
    }
}
