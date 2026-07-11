using System.Security.Claims;

namespace MoodyBlues.Backend.Auth;

/// <summary>Reads the authenticated user's id out of the JWT claims <c>[Authorize]</c> endpoints receive.</summary>
public static class CurrentUser
{
    public static bool TryGetUserId(ClaimsPrincipal principal, out Guid userId)
    {
        string? value = principal.FindFirstValue(JwtTokenService.UserIdClaimType);
        return Guid.TryParse(value, out userId);
    }
}
