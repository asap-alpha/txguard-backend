namespace TxGuard.Api.Auth;

/// <summary>
/// Canonical role names (RBAC). Kept as constants so controllers and the token service
/// reference the same strings the JWT carries.
/// </summary>
public static class Roles
{
    /// <summary>Full access, including the demo/chaos panel.</summary>
    public const string Admin = "Admin";

    /// <summary>Fraud analyst — can approve/reject reviews and read everything.</summary>
    public const string Analyst = "Analyst";

    /// <summary>Machine/partner integrator — can submit and refund transactions and read.</summary>
    public const string Integrator = "Integrator";
}

/// <summary>
/// Authentication configuration, bound from the "Auth" section. The signing key and
/// seeded users have dev defaults so the app runs out of the box; override them (and
/// especially the key) via configuration or environment variables in any real deployment.
/// </summary>
public sealed class AuthOptions
{
    public const string SectionName = "Auth";

    public JwtOptions Jwt { get; set; } = new();

    /// <summary>Seed users. Passwords are plaintext here (demo only) and hashed on load.</summary>
    public List<SeedUser> Users { get; set; } = new();
}

public sealed class JwtOptions
{
    public string Issuer { get; set; } = "txguard";
    public string Audience { get; set; } = "txguard-clients";

    /// <summary>HMAC signing key. MUST be overridden in production. Minimum 32 bytes.</summary>
    public string SigningKey { get; set; } = "dev-only-signing-key-change-me-please-32b+";

    public int AccessTokenMinutes { get; set; } = 60;
}

public sealed class SeedUser
{
    public string Username { get; set; } = "";
    public string Password { get; set; } = "";
    public string Role { get; set; } = Roles.Integrator;
    public string? DisplayName { get; set; }
}
