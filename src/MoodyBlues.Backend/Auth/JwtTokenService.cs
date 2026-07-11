using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using Microsoft.IdentityModel.Tokens;
using MoodyBlues.Backend.Config;
using MoodyBlues.Backend.Data;

namespace MoodyBlues.Backend.Auth;

/// <summary>Issues and validates the JWTs the dashboard SPA sends as <c>Authorization: Bearer ...</c>.</summary>
public sealed class JwtTokenService(ServerConfig config)
{
    private static readonly TimeSpan TokenLifetime = TimeSpan.FromDays(7);

    public const string UserIdClaimType = ClaimTypes.NameIdentifier;

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
            issuer: "moodyblues-backend",
            audience: "moodyblues-dashboard",
            claims: claims,
            expires: DateTime.UtcNow.Add(TokenLifetime),
            signingCredentials: credentials);

        return new JwtSecurityTokenHandler().WriteToken(token);
    }

    public TokenValidationParameters BuildValidationParameters()
    {
        return new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidIssuer = "moodyblues-backend",
            ValidateAudience = true,
            ValidAudience = "moodyblues-dashboard",
            ValidateIssuerSigningKey = true,
            IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(config.JwtSecret)),
            ValidateLifetime = true,
            ClockSkew = TimeSpan.FromMinutes(1),
        };
    }
}
