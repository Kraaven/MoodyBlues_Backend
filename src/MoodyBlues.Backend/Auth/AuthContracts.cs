namespace MoodyBlues.Backend.Auth;

public sealed record RegisterRequest(string Email, string Password);

public sealed record LoginRequest(string Email, string Password);

/// <summary>Body of a successful sign-in or refresh. The tokens themselves are only in httpOnly cookies.</summary>
public sealed record AuthResponse(Guid UserId, string Email);

public sealed record MeResponse(Guid UserId, string Email);
