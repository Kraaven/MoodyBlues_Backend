namespace MoodyBlues.Backend.Auth;

public sealed record RegisterRequest(string Email, string Password);

public sealed record LoginRequest(string Email, string Password);

/// <summary>The signed-in user: the body of a sign-in, a refresh and <c>GET /auth/me</c>. The tokens themselves are only in httpOnly cookies.</summary>
public sealed record UserResponse(Guid UserId, string Email);
