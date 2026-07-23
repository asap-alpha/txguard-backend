using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using TxGuard.Infrastructure.Persistence;

namespace TxGuard.Api.Auth;

/// <summary>Metadata for an issued key (never includes the secret).</summary>
public sealed record ApiKeyInfo(
    long Id, string Name, string Prefix, string Role,
    string CreatedBy, DateTime CreatedAtUtc, DateTime? LastUsedAtUtc, DateTime? RevokedAtUtc, bool Active);

/// <summary>Identity resolved from a valid presented key.</summary>
public sealed record ApiKeyIdentity(long Id, string Name, string Role);

/// <summary>
/// Issues, validates, lists and revokes partner API keys. Secrets are never stored —
/// only their SHA-256 hash and a non-secret display prefix. The full key is returned
/// exactly once, at creation.
/// </summary>
public sealed class ApiKeyService
{
    private const string KeyPrefix = "txg_live_";
    private readonly IDbContextFactory<TxGuardDbContext> _dbf;

    public ApiKeyService(IDbContextFactory<TxGuardDbContext> dbf) => _dbf = dbf;

    /// <summary>Creates a key for the given role and returns its metadata plus the one-time secret.</summary>
    public async Task<(ApiKeyInfo Info, string FullKey)> CreateAsync(
        string name, string role, string createdBy, CancellationToken ct = default)
    {
        var secret = Convert.ToHexString(RandomNumberGenerator.GetBytes(24)).ToLowerInvariant();
        var fullKey = KeyPrefix + secret;

        var entity = new ApiKeyEntity
        {
            Name = name,
            Prefix = fullKey[..Math.Min(fullKey.Length, 17)],   // "txg_live_" + first 8
            Hash = Hash(fullKey),
            Role = role,
            CreatedBy = createdBy,
            CreatedAtUtc = DateTime.UtcNow,
        };

        await using var db = await _dbf.CreateDbContextAsync(ct);
        db.ApiKeys.Add(entity);
        await db.SaveChangesAsync(ct);
        return (ToInfo(entity), fullKey);
    }

    /// <summary>Validates a presented key; null if unknown or revoked. Updates last-used on success.</summary>
    public async Task<ApiKeyIdentity?> ValidateAsync(string presentedKey, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(presentedKey) || !presentedKey.StartsWith(KeyPrefix))
            return null;

        var hash = Hash(presentedKey);
        await using var db = await _dbf.CreateDbContextAsync(ct);
        var entity = await db.ApiKeys.FirstOrDefaultAsync(k => k.Hash == hash, ct);
        if (entity is null || entity.RevokedAtUtc is not null)
            return null;

        entity.LastUsedAtUtc = DateTime.UtcNow;
        await db.SaveChangesAsync(ct);
        return new ApiKeyIdentity(entity.Id, entity.Name, entity.Role);
    }

    public async Task<IReadOnlyList<ApiKeyInfo>> ListAsync(CancellationToken ct = default)
    {
        await using var db = await _dbf.CreateDbContextAsync(ct);
        var rows = await db.ApiKeys.AsNoTracking().OrderByDescending(k => k.Id).ToListAsync(ct);
        return rows.Select(ToInfo).ToList();
    }

    /// <summary>Revokes a key; returns false if it doesn't exist or was already revoked.</summary>
    public async Task<bool> RevokeAsync(long id, CancellationToken ct = default)
    {
        await using var db = await _dbf.CreateDbContextAsync(ct);
        var entity = await db.ApiKeys.FirstOrDefaultAsync(k => k.Id == id, ct);
        if (entity is null || entity.RevokedAtUtc is not null) return false;
        entity.RevokedAtUtc = DateTime.UtcNow;
        await db.SaveChangesAsync(ct);
        return true;
    }

    private static string Hash(string key) =>
        Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(key))).ToLowerInvariant();

    private static ApiKeyInfo ToInfo(ApiKeyEntity e) => new(
        e.Id, e.Name, e.Prefix, e.Role, e.CreatedBy, e.CreatedAtUtc, e.LastUsedAtUtc, e.RevokedAtUtc, e.IsActive);
}
