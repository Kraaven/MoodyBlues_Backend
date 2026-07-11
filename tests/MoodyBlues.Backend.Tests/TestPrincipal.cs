using System.Security.Claims;
using MoodyBlues.Backend.Auth;

namespace MoodyBlues.Backend.Tests;

/// <summary>Builds a fake authenticated <see cref="ClaimsPrincipal"/> -- mirrors what a validated JWT produces.</summary>
public static class TestPrincipal
{
    public static ClaimsPrincipal For(Guid userId, string email = "user@example.com")
    {
        var identity = new ClaimsIdentity(
        [
            new Claim(JwtTokenService.UserIdClaimType, userId.ToString()),
            new Claim(ClaimTypes.Email, email),
        ], authenticationType: "TestAuth");

        return new ClaimsPrincipal(identity);
    }
}
