using Microsoft.EntityFrameworkCore;
using MoodyBlues.Backend.Config;
using MoodyBlues.Backend.Data;
using MoodyBlues.Backend.Ingest;
using MoodyBlues.Backend.Logging;

namespace MoodyBlues.Backend.Auth;

/// <summary>
/// Rotating refresh tokens. Each use replaces the token; a token that was already replaced and is
/// presented again means someone else has a copy, so its whole family (every token from that
/// sign-in) is revoked and that browser has to sign in again.
///
/// Two tabs refreshing at the same moment would look exactly like that, so a token replaced less
/// than <see cref="ConcurrentRefreshGrace"/> ago is accepted once more instead.
/// </summary>
public static class RefreshTokens
{
    public static readonly TimeSpan ConcurrentRefreshGrace = TimeSpan.FromSeconds(30);

    public sealed record Issued(Guid Id, string Token, DateTime ExpiresAtUtc);

    /// <summary>Starts a new family (a sign-in) or continues one (a refresh).</summary>
    public static async Task<Issued> IssueAsync(MoodyBluesDbContext db, ServerConfig config, Guid userId, Guid? familyId = null)
    {
        string raw = StreamTokens.NewToken();
        DateTime now = DateTime.UtcNow;
        var token = new RefreshToken
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            TokenHash = StreamTokens.Hash(raw),
            FamilyId = familyId ?? Guid.NewGuid(),
            CreatedAtUtc = now,
            ExpiresAtUtc = now.Add(config.RefreshTokenLifetime),
        };
        db.RefreshTokens.Add(token);

        // Opportunistic cleanup, so the table only holds tokens that can still matter.
        await db.RefreshTokens.Where(t => t.UserId == userId && t.ExpiresAtUtc < now).ExecuteDeleteIfSupportedAsync(db);

        await db.SaveChangesAsync();
        return new Issued(token.Id, raw, token.ExpiresAtUtc);
    }

    /// <summary>The user and a replacement token, or null if the token is unknown, expired or revoked.</summary>
    public static async Task<(User User, Issued Token)?> RotateAsync(MoodyBluesDbContext db, ServerConfig config, string? raw)
    {
        if (string.IsNullOrEmpty(raw) || raw.Length > 128) return null;

        string hash = StreamTokens.Hash(raw);
        RefreshToken? token = await db.RefreshTokens.FirstOrDefaultAsync(t => t.TokenHash == hash);
        DateTime now = DateTime.UtcNow;
        if (token is null || token.ExpiresAtUtc <= now) return null;

        if (token.RevokedAtUtc is { } revokedAt)
        {
            bool concurrentRefresh = token.ReplacedByTokenId is not null && now - revokedAt < ConcurrentRefreshGrace;
            if (!concurrentRefresh)
            {
                await RevokeFamilyAsync(db, token.FamilyId, now);
                ConsoleLog.Warn($"Refresh token reuse for user {token.UserId}: revoked its sign-in (family {token.FamilyId}).");
                return null;
            }
        }

        User? user = await db.Users.FindAsync(token.UserId);
        if (user is null) return null;

        Issued next = await IssueAsync(db, config, user.Id, token.FamilyId);
        if (token.RevokedAtUtc is null)
        {
            token.RevokedAtUtc = now;
            token.ReplacedByTokenId = next.Id;
            await db.SaveChangesAsync();
        }

        return (user, next);
    }

    /// <summary>Signs a browser out: revokes the token's whole family.</summary>
    public static async Task RevokeAsync(MoodyBluesDbContext db, string? raw)
    {
        if (string.IsNullOrEmpty(raw) || raw.Length > 128) return;
        string hash = StreamTokens.Hash(raw);
        RefreshToken? token = await db.RefreshTokens.FirstOrDefaultAsync(t => t.TokenHash == hash);
        if (token is not null) await RevokeFamilyAsync(db, token.FamilyId, DateTime.UtcNow);
    }

    private static async Task RevokeFamilyAsync(MoodyBluesDbContext db, Guid familyId, DateTime now)
    {
        List<RefreshToken> family = await db.RefreshTokens.Where(t => t.FamilyId == familyId && t.RevokedAtUtc == null).ToListAsync();
        foreach (RefreshToken t in family) t.RevokedAtUtc = now;
        await db.SaveChangesAsync();
    }

    /// <summary>ExecuteDelete is not available on the in-memory provider the tests use.</summary>
    private static async Task ExecuteDeleteIfSupportedAsync(this IQueryable<RefreshToken> query, MoodyBluesDbContext db)
    {
        if (db.Database.IsRelational())
        {
            await query.ExecuteDeleteAsync();
        }
        else
        {
            db.RefreshTokens.RemoveRange(await query.ToListAsync());
        }
    }
}
