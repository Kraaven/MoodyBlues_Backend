using System.Security.Claims;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using MoodyBlues.Backend.Data;
using MoodyBlues.Backend.Logging;

namespace MoodyBlues.Backend.Auth;

/// <summary>Dashboard account registration/login -- <c>POST /auth/register</c>, <c>POST /auth/login</c>, <c>GET /auth/me</c>.</summary>
public static class AuthEndpoints
{
    private const int MinPasswordLength = 8;

    public static void Map(WebApplication app)
    {
        app.MapPost("/auth/register", RegisterAsync);
        app.MapPost("/auth/login", LoginAsync);
        app.MapGet("/auth/me", Me).RequireAuthorization();
    }

    /// <summary>Public so it can be exercised directly in tests without a live Postgres.</summary>
    public static async Task<IResult> RegisterAsync(RegisterRequest request, MoodyBluesDbContext db, JwtTokenService jwt)
    {
        string email = request.Email?.Trim().ToLowerInvariant() ?? string.Empty;

        if (string.IsNullOrWhiteSpace(email) || !email.Contains('@'))
        {
            return Results.BadRequest("A valid email is required.");
        }

        if (string.IsNullOrWhiteSpace(request.Password) || request.Password.Length < MinPasswordLength)
        {
            return Results.BadRequest($"Password must be at least {MinPasswordLength} characters.");
        }

        bool alreadyExists = await db.Users.AnyAsync(u => u.Email == email);
        if (alreadyExists)
        {
            return Results.Conflict("An account with that email already exists.");
        }

        var user = new User
        {
            Id = Guid.NewGuid(),
            Email = email,
            PasswordHash = PasswordHasher.Hash(request.Password),
            CreatedAtUtc = DateTime.UtcNow,
        };

        db.Users.Add(user);
        await db.SaveChangesAsync();

        ConsoleLog.Info($"Registered new account: {email}");

        return Results.Ok(new AuthResponse(jwt.IssueToken(user), user.Id, user.Email));
    }

    /// <summary>Public so it can be exercised directly in tests without a live Postgres.</summary>
    public static async Task<IResult> LoginAsync(LoginRequest request, MoodyBluesDbContext db, JwtTokenService jwt)
    {
        string email = request.Email?.Trim().ToLowerInvariant() ?? string.Empty;

        User? user = await db.Users.FirstOrDefaultAsync(u => u.Email == email);
        if (user is null || !PasswordHasher.Verify(request.Password ?? string.Empty, user.PasswordHash))
        {
            return Results.Unauthorized();
        }

        return Results.Ok(new AuthResponse(jwt.IssueToken(user), user.Id, user.Email));
    }

    private static IResult Me(ClaimsPrincipal principal)
    {
        if (!CurrentUser.TryGetUserId(principal, out Guid userId))
        {
            return Results.Unauthorized();
        }

        string email = principal.FindFirstValue(ClaimTypes.Email) ?? string.Empty;
        return Results.Ok(new MeResponse(userId, email));
    }
}
