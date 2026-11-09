using System.Security.Claims;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using MoodyBlues.Backend.Config;
using MoodyBlues.Backend.Data;
using MoodyBlues.Backend.Logging;

namespace MoodyBlues.Backend.Auth;

/// <summary>
/// Dashboard accounts (security plan phase 3). Signing in sets two httpOnly cookies
/// (<see cref="AuthCookies"/>): a short-lived access token and a rotating refresh token.
/// <list type="bullet">
/// <item><c>POST /auth/register</c>, <c>POST /auth/login</c>: sign in.</item>
/// <item><c>POST /auth/refresh</c>: replace both cookies; the dashboard calls it when a request gets a 401.</item>
/// <item><c>POST /auth/logout</c>: revoke the refresh token and clear the cookies.</item>
/// <item><c>GET /auth/me</c>: who is signed in.</item>
/// </list>
/// Login answers the same way, and takes as long, for an unknown email as for a wrong password.
/// </summary>
public static class AuthEndpoints
{
    private const int MinPasswordLength = 8;
    private const int MaxPasswordLength = 1024;

    public const string LoginFailedMessage = "Wrong email or password.";

    public const string RegisterFailedMessage = "An account could not be created with this email. If you already have one, sign in instead.";

    public static void Map(WebApplication app)
    {
        app.MapPost("/auth/register", RegisterAsync).RequireRateLimiting(Common.RateLimits.Auth);
        app.MapPost("/auth/login", LoginAsync).RequireRateLimiting(Common.RateLimits.Auth);
        app.MapPost("/auth/refresh", RefreshAsync).RequireRateLimiting(Common.RateLimits.Ingest);
        app.MapPost("/auth/logout", LogoutAsync);
        app.MapGet("/auth/me", Me).RequireAuthorization();
    }

    /// <summary>Public so it can be exercised directly in tests without a live Postgres.</summary>
    public static async Task<IResult> RegisterAsync(
        RegisterRequest request,
        HttpResponse response,
        MoodyBluesDbContext db,
        JwtTokenService jwt,
        ServerConfig config)
    {
        string email = request.Email?.Trim().ToLowerInvariant() ?? string.Empty;

        if (string.IsNullOrWhiteSpace(email) || !email.Contains('@') || email.Length > 320)
        {
            return Results.BadRequest("A valid email is required.");
        }

        if (string.IsNullOrWhiteSpace(request.Password) || request.Password.Length < MinPasswordLength || request.Password.Length > MaxPasswordLength)
        {
            return Results.BadRequest($"Password must be {MinPasswordLength} to {MaxPasswordLength} characters.");
        }

        // Hash first, so a taken email does not answer measurably faster than a new one.
        string passwordHash = PasswordHasher.Hash(request.Password);
        if (await db.Users.AnyAsync(u => u.Email == email))
        {
            return Results.BadRequest(RegisterFailedMessage);
        }

        var user = new User
        {
            Id = Guid.NewGuid(),
            Email = email,
            PasswordHash = passwordHash,
            CreatedAtUtc = DateTime.UtcNow,
        };

        db.Users.Add(user);
        try
        {
            await db.SaveChangesAsync();
        }
        catch (DbUpdateException)
        {
            // Lost a race with another registration of the same email.
            return Results.BadRequest(RegisterFailedMessage);
        }

        ConsoleLog.Info($"Registered new account {user.Id}.");
        return await SignInAsync(response, db, jwt, config, user);
    }

    /// <summary>Public so it can be exercised directly in tests without a live Postgres.</summary>
    public static async Task<IResult> LoginAsync(
        LoginRequest request,
        HttpResponse response,
        MoodyBluesDbContext db,
        JwtTokenService jwt,
        ServerConfig config)
    {
        string email = request.Email?.Trim().ToLowerInvariant() ?? string.Empty;
        string password = request.Password ?? string.Empty;
        if (password.Length > MaxPasswordLength)
        {
            return Results.Json(LoginFailedMessage, statusCode: StatusCodes.Status401Unauthorized);
        }

        User? user = await db.Users.FirstOrDefaultAsync(u => u.Email == email);
        if (user is null)
        {
            PasswordHasher.VerifyDummy(password);
            return Results.Json(LoginFailedMessage, statusCode: StatusCodes.Status401Unauthorized);
        }

        if (!PasswordHasher.Verify(password, user.PasswordHash))
        {
            return Results.Json(LoginFailedMessage, statusCode: StatusCodes.Status401Unauthorized);
        }

        // Hashes from before the iteration increase are upgraded the first time they are used.
        if (PasswordHasher.NeedsRehash(user.PasswordHash))
        {
            user.PasswordHash = PasswordHasher.Hash(password);
            await db.SaveChangesAsync();
        }

        return await SignInAsync(response, db, jwt, config, user);
    }

    public static async Task<IResult> RefreshAsync(
        HttpRequest request,
        HttpResponse response,
        MoodyBluesDbContext db,
        JwtTokenService jwt,
        ServerConfig config)
    {
        var rotated = await RefreshTokens.RotateAsync(db, config, request.Cookies[AuthCookies.RefreshCookie]);
        if (rotated is null)
        {
            AuthCookies.Clear(response, config);
            return Results.Json("Your session expired. Sign in again.", statusCode: StatusCodes.Status401Unauthorized);
        }

        (User user, RefreshTokens.Issued token) = rotated.Value;
        AuthCookies.Set(response, config, jwt.IssueToken(user), token.Token, token.ExpiresAtUtc);
        return Results.Ok(new AuthResponse(user.Id, user.Email));
    }

    public static async Task<IResult> LogoutAsync(HttpRequest request, HttpResponse response, MoodyBluesDbContext db, ServerConfig config)
    {
        await RefreshTokens.RevokeAsync(db, request.Cookies[AuthCookies.RefreshCookie]);
        AuthCookies.Clear(response, config);
        return Results.NoContent();
    }

    private static async Task<IResult> SignInAsync(HttpResponse response, MoodyBluesDbContext db, JwtTokenService jwt, ServerConfig config, User user)
    {
        RefreshTokens.Issued refresh = await RefreshTokens.IssueAsync(db, config, user.Id);
        AuthCookies.Set(response, config, jwt.IssueToken(user), refresh.Token, refresh.ExpiresAtUtc);
        return Results.Ok(new AuthResponse(user.Id, user.Email));
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
