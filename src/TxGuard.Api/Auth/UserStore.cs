using System.Security.Cryptography;
using Microsoft.Extensions.Options;

namespace TxGuard.Api.Auth;

/// <summary>An authenticated user record (no secret material once loaded).</summary>
public sealed record AuthUser(string Username, string Role, string DisplayName);

/// <summary>
/// In-memory user directory seeded from <see cref="AuthOptions.Users"/>. Passwords are
/// hashed with PBKDF2 (a fresh random salt per user) at startup, so the plaintext demo
/// passwords in configuration are never held in memory or compared directly. A real
/// deployment would back this with a database and an identity provider.
/// </summary>
public sealed class UserStore
{
    private sealed record Entry(string Role, string DisplayName, byte[] Salt, byte[] Hash);

    private const int SaltBytes = 16, HashBytes = 32, Iterations = 100_000;
    private readonly Dictionary<string, Entry> _users = new(StringComparer.OrdinalIgnoreCase);

    public UserStore(IOptions<AuthOptions> options)
    {
        foreach (var u in options.Value.Users)
        {
            if (string.IsNullOrWhiteSpace(u.Username) || string.IsNullOrWhiteSpace(u.Password))
                continue;
            var salt = RandomNumberGenerator.GetBytes(SaltBytes);
            _users[u.Username] = new Entry(
                string.IsNullOrWhiteSpace(u.Role) ? Roles.Integrator : u.Role,
                string.IsNullOrWhiteSpace(u.DisplayName) ? u.Username : u.DisplayName!,
                salt, Hash(u.Password, salt));
        }
    }

    /// <summary>Validates credentials in constant time; null on any mismatch.</summary>
    public AuthUser? Validate(string username, string password)
    {
        if (string.IsNullOrWhiteSpace(username) || !_users.TryGetValue(username, out var e))
            return null;
        var candidate = Hash(password, e.Salt);
        if (!CryptographicOperations.FixedTimeEquals(candidate, e.Hash))
            return null;
        return new AuthUser(username, e.Role, e.DisplayName);
    }

    private static byte[] Hash(string password, byte[] salt) =>
        Rfc2898DeriveBytes.Pbkdf2(password, salt, Iterations, HashAlgorithmName.SHA256, HashBytes);
}
