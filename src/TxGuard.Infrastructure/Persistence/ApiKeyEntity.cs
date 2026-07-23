namespace TxGuard.Infrastructure.Persistence;

/// <summary>
/// A partner/integrator API key. Only the SHA-256 <see cref="Hash"/> of the secret is
/// stored — the full key is shown once, at creation, and never again. <see cref="Prefix"/>
/// keeps a non-secret fragment (e.g. "txg_live_9f3a…") so admins can identify a key in
/// listings. A non-null <see cref="RevokedAtUtc"/> permanently disables the key.
/// </summary>
public class ApiKeyEntity
{
    public long Id { get; set; }
    public string Name { get; set; } = default!;          // partner/label, e.g. "Acme Payments"
    public string Prefix { get; set; } = default!;        // display fragment, non-secret
    public string Hash { get; set; } = default!;          // SHA-256(secret), hex
    public string Role { get; set; } = default!;          // granted role (Integrator)
    public string CreatedBy { get; set; } = default!;     // admin username that issued it
    public DateTime CreatedAtUtc { get; set; }
    public DateTime? LastUsedAtUtc { get; set; }
    public DateTime? RevokedAtUtc { get; set; }

    public bool IsActive => RevokedAtUtc is null;
}
