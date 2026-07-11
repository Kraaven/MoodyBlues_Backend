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

    [Fact]
    public async Task Register_CreatesUser_AndReturnsToken()
    {
        using var db = TestDbContextFactory.Create();
        var request = new RegisterRequest("New.User@Example.com", "supersecret1");

        var result = await AuthEndpoints.RegisterAsync(request, db, Jwt);
        var response = await HttpResultTestHelpers.ExecuteAndDeserializeAsync<AuthResponse>(result);

        Assert.NotNull(response);
        Assert.False(string.IsNullOrWhiteSpace(response!.Token));
        Assert.Equal("new.user@example.com", response.Email);

        User? user = await db.Users.FirstOrDefaultAsync(u => u.Email == "new.user@example.com");
        Assert.NotNull(user);
        Assert.True(PasswordHasher.Verify("supersecret1", user!.PasswordHash));
    }

    [Fact]
    public async Task Register_DuplicateEmail_ReturnsConflict()
    {
        using var db = TestDbContextFactory.Create();
        await AuthEndpoints.RegisterAsync(new RegisterRequest("dup@example.com", "supersecret1"), db, Jwt);

        var result = await AuthEndpoints.RegisterAsync(new RegisterRequest("dup@example.com", "anotherpass"), db, Jwt);
        var (statusCode, _) = await HttpResultTestHelpers.ExecuteAsync(result);

        Assert.Equal(409, statusCode);
    }

    [Theory]
    [InlineData("not-an-email", "supersecret1")]
    [InlineData("valid@example.com", "short")]
    public async Task Register_InvalidInput_ReturnsBadRequest(string email, string password)
    {
        using var db = TestDbContextFactory.Create();

        var result = await AuthEndpoints.RegisterAsync(new RegisterRequest(email, password), db, Jwt);
        var (statusCode, _) = await HttpResultTestHelpers.ExecuteAsync(result);

        Assert.Equal(400, statusCode);
    }

    [Fact]
    public async Task Login_CorrectPassword_ReturnsToken()
    {
        using var db = TestDbContextFactory.Create();
        await AuthEndpoints.RegisterAsync(new RegisterRequest("login@example.com", "correct-password"), db, Jwt);

        var result = await AuthEndpoints.LoginAsync(new LoginRequest("login@example.com", "correct-password"), db, Jwt);
        var response = await HttpResultTestHelpers.ExecuteAndDeserializeAsync<AuthResponse>(result);

        Assert.NotNull(response);
        Assert.False(string.IsNullOrWhiteSpace(response!.Token));
    }

    [Fact]
    public async Task Login_WrongPassword_ReturnsUnauthorized()
    {
        using var db = TestDbContextFactory.Create();
        await AuthEndpoints.RegisterAsync(new RegisterRequest("login2@example.com", "correct-password"), db, Jwt);

        var result = await AuthEndpoints.LoginAsync(new LoginRequest("login2@example.com", "wrong-password"), db, Jwt);
        var (statusCode, _) = await HttpResultTestHelpers.ExecuteAsync(result);

        Assert.Equal(401, statusCode);
    }

    [Fact]
    public async Task Login_UnknownEmail_ReturnsUnauthorized()
    {
        using var db = TestDbContextFactory.Create();

        var result = await AuthEndpoints.LoginAsync(new LoginRequest("nobody@example.com", "whatever1"), db, Jwt);
        var (statusCode, _) = await HttpResultTestHelpers.ExecuteAsync(result);

        Assert.Equal(401, statusCode);
    }
}
