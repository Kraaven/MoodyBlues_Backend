using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using MoodyBlues.Backend.Auth;
using MoodyBlues.Backend.Config;
using MoodyBlues.Backend.Data;

namespace MoodyBlues.Backend.Tests;

/// <summary>
/// Exercises <see cref="AuthEndpoints"/> directly against an EF Core
/// InMemory-backed <see cref="MoodyBluesDbContext"/> -- no live Postgres or running host needed.
/// </summary>
public class AuthEndpointsTests
{
    private static readonly ServerConfig Config = new() { JwtSecret = "test-secret-at-least-32-bytes-long!!" };
    private static JwtTokenService Jwt => new(Config);

    private static HttpResponse NewResponse() => new DefaultHttpContext().Response;

    private static string SetCookies(HttpResponse response) => string.Join("\n", response.Headers.SetCookie.ToArray());

    [Fact]
    public async Task Register_CreatesUser_AndSetsAuthCookies()
    {
        using var db = TestDbContextFactory.Create();
        var request = new RegisterRequest("New.User@Example.com", "supersecret1");
        HttpResponse httpResponse = NewResponse();

        var result = await AuthEndpoints.RegisterAsync(request, httpResponse, db, Jwt, Config);
        var response = await HttpResultTestHelpers.ExecuteAndDeserializeAsync<AuthResponse>(result);

        Assert.NotNull(response);
        Assert.Equal("new.user@example.com", response!.Email);
        string cookies = SetCookies(httpResponse);
        Assert.Contains($"{AuthCookies.AccessCookie}=", cookies);
        Assert.Contains($"{AuthCookies.RefreshCookie}=", cookies);
        Assert.Contains("httponly", cookies, StringComparison.OrdinalIgnoreCase);

        User? user = await db.Users.FirstOrDefaultAsync(u => u.Email == "new.user@example.com");
        Assert.NotNull(user);
        Assert.True(PasswordHasher.Verify("supersecret1", user!.PasswordHash));
        Assert.Single(db.RefreshTokens);
    }

    [Fact]
    public async Task Register_DuplicateEmail_IsRefusedWithAGenericMessage()
    {
        using var db = TestDbContextFactory.Create();
        await AuthEndpoints.RegisterAsync(new RegisterRequest("dup@example.com", "supersecret1"), NewResponse(), db, Jwt, Config);

        var result = await AuthEndpoints.RegisterAsync(new RegisterRequest("dup@example.com", "anotherpass"), NewResponse(), db, Jwt, Config);
        var (statusCode, body) = await HttpResultTestHelpers.ExecuteAsync(result);

        Assert.Equal(400, statusCode);
        Assert.Contains("could not be created", body);
    }

    [Theory]
    [InlineData("not-an-email", "supersecret1")]
    [InlineData("valid@example.com", "short")]
    public async Task Register_InvalidInput_ReturnsBadRequest(string email, string password)
    {
        using var db = TestDbContextFactory.Create();

        var result = await AuthEndpoints.RegisterAsync(new RegisterRequest(email, password), NewResponse(), db, Jwt, Config);
        var (statusCode, _) = await HttpResultTestHelpers.ExecuteAsync(result);

        Assert.Equal(400, statusCode);
    }

    [Fact]
    public async Task Login_CorrectPassword_SetsAuthCookies()
    {
        using var db = TestDbContextFactory.Create();
        await AuthEndpoints.RegisterAsync(new RegisterRequest("login@example.com", "correct-password"), NewResponse(), db, Jwt, Config);
        HttpResponse httpResponse = NewResponse();

        var result = await AuthEndpoints.LoginAsync(new LoginRequest("login@example.com", "correct-password"), httpResponse, db, Jwt, Config);
        var response = await HttpResultTestHelpers.ExecuteAndDeserializeAsync<AuthResponse>(result);

        Assert.Equal("login@example.com", response!.Email);
        Assert.Contains($"{AuthCookies.AccessCookie}=", SetCookies(httpResponse));
    }

    [Fact]
    public async Task Login_UpgradesAnOldPasswordHash()
    {
        using var db = TestDbContextFactory.Create();
        string oldHash = OldHash("correct-password", 100_000);
        db.Users.Add(new User { Id = Guid.NewGuid(), Email = "old@example.com", PasswordHash = oldHash, CreatedAtUtc = DateTime.UtcNow });
        await db.SaveChangesAsync();

        var result = await AuthEndpoints.LoginAsync(new LoginRequest("old@example.com", "correct-password"), NewResponse(), db, Jwt, Config);
        var (status, _) = await HttpResultTestHelpers.ExecuteAsync(result);

        Assert.Equal(200, status);
        string upgraded = (await db.Users.SingleAsync()).PasswordHash;
        Assert.StartsWith($"{PasswordHasher.Iterations}.", upgraded);
        Assert.True(PasswordHasher.Verify("correct-password", upgraded));
    }

    [Fact]
    public async Task Login_WrongPassword_ReturnsUnauthorized()
    {
        using var db = TestDbContextFactory.Create();
        await AuthEndpoints.RegisterAsync(new RegisterRequest("login2@example.com", "correct-password"), NewResponse(), db, Jwt, Config);

        var result = await AuthEndpoints.LoginAsync(new LoginRequest("login2@example.com", "wrong-password"), NewResponse(), db, Jwt, Config);
        var (statusCode, body) = await HttpResultTestHelpers.ExecuteAsync(result);

        Assert.Equal(401, statusCode);
        Assert.Contains(AuthEndpoints.LoginFailedMessage, body);
    }

    [Fact]
    public async Task Login_UnknownEmail_LooksLikeAWrongPassword()
    {
        using var db = TestDbContextFactory.Create();

        var result = await AuthEndpoints.LoginAsync(new LoginRequest("nobody@example.com", "whatever1"), NewResponse(), db, Jwt, Config);
        var (statusCode, body) = await HttpResultTestHelpers.ExecuteAsync(result);

        Assert.Equal(401, statusCode);
        Assert.Contains(AuthEndpoints.LoginFailedMessage, body);
    }

    private static string OldHash(string password, int iterations)
    {
        byte[] salt = System.Security.Cryptography.RandomNumberGenerator.GetBytes(16);
        byte[] hash = System.Security.Cryptography.Rfc2898DeriveBytes.Pbkdf2(password, salt, iterations, System.Security.Cryptography.HashAlgorithmName.SHA256, 32);
        return $"{iterations}.{Convert.ToBase64String(salt)}.{Convert.ToBase64String(hash)}";
    }
}
