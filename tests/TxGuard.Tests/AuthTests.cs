using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
using TxGuard.Api.Auth;

namespace TxGuard.Tests;

/// <summary>
/// Authentication and RBAC (SRS §4.3 NFR-S-002/003/006). Covers credential validation,
/// the claims a token carries, and the API-key lifecycle that machine integrators use.
/// </summary>
public class UserStoreTests
{
    private static UserStore Store() => new(Options.Create(new AuthOptions
    {
        Users =
        {
            new SeedUser { Username = "admin", Password = "admin123", Role = Roles.Admin, DisplayName = "Ada Admin" },
            new SeedUser { Username = "analyst", Password = "analyst123", Role = Roles.Analyst },
            new SeedUser { Username = "blank", Password = "" },   // incomplete entries are skipped
        },
    }));

    [Fact]
    public void Valid_credentials_return_the_user_and_their_role()
    {
        var user = Store().Validate("admin", "admin123");

        Assert.NotNull(user);
        Assert.Equal(Roles.Admin, user!.Role);
        Assert.Equal("Ada Admin", user.DisplayName);
    }

    [Fact]
    public void Display_name_falls_back_to_the_username()
    {
        Assert.Equal("analyst", Store().Validate("analyst", "analyst123")!.DisplayName);
    }

    [Theory]
    [InlineData("admin", "wrong-password")]
    [InlineData("nobody", "admin123")]
    [InlineData("", "admin123")]
    [InlineData("blank", "")]
    public void Bad_credentials_are_rejected(string username, string password)
    {
        Assert.Null(Store().Validate(username, password));
    }

    [Fact]
    public void Usernames_are_case_insensitive_but_passwords_are_not()
    {
        var store = Store();
        Assert.NotNull(store.Validate("ADMIN", "admin123"));
        Assert.Null(store.Validate("admin", "ADMIN123"));
    }
}

public class TokenServiceTests
{
    private static readonly AuthOptions Options = new()
    {
        Jwt = new JwtOptions
        {
            Issuer = "txguard",
            Audience = "txguard-clients",
            SigningKey = "test-signing-key-at-least-32-bytes-long!",
            AccessTokenMinutes = 30,
        },
    };

    [Fact]
    public void Issued_token_carries_the_identity_and_role_claims()
    {
        var (token, expires) = new TokenService(Microsoft.Extensions.Options.Options.Create(Options))
            .Issue(new AuthUser("analyst", Roles.Analyst, "Kojo Analyst"));

        var jwt = new JwtSecurityTokenHandler().ReadJwtToken(token);
        Assert.Equal("txguard", jwt.Issuer);
        Assert.Contains("txguard-clients", jwt.Audiences);
        Assert.Equal("analyst", jwt.Claims.First(c => c.Type == ClaimTypes.Name).Value);
        Assert.Equal(Roles.Analyst, jwt.Claims.First(c => c.Type == ClaimTypes.Role).Value);
        Assert.Equal("Kojo Analyst", jwt.Claims.First(c => c.Type == "displayName").Value);
        Assert.True(expires > DateTime.UtcNow);
    }

    [Fact]
    public void Issued_token_validates_against_the_configured_signing_key()
    {
        var (token, _) = new TokenService(Microsoft.Extensions.Options.Options.Create(Options))
            .Issue(new AuthUser("admin", Roles.Admin, "Ada Admin"));

        var principal = new JwtSecurityTokenHandler().ValidateToken(token, new TokenValidationParameters
        {
            ValidIssuer = Options.Jwt.Issuer,
            ValidAudience = Options.Jwt.Audience,
            IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(Options.Jwt.SigningKey)),
            ValidateLifetime = true,
        }, out _);

        Assert.True(principal.IsInRole(Roles.Admin));
    }

    [Fact]
    public void A_token_signed_with_a_different_key_is_rejected()
    {
        var (token, _) = new TokenService(Microsoft.Extensions.Options.Options.Create(Options))
            .Issue(new AuthUser("admin", Roles.Admin, "Ada Admin"));

        Assert.Throws<SecurityTokenSignatureKeyNotFoundException>(() =>
            new JwtSecurityTokenHandler().ValidateToken(token, new TokenValidationParameters
            {
                ValidateIssuer = false,
                ValidateAudience = false,
                ValidateLifetime = false,
                IssuerSigningKey = new SymmetricSecurityKey(
                    Encoding.UTF8.GetBytes("a-completely-different-key-32-bytes!!")),
            }, out _));
    }
}

public class ApiKeyServiceTests
{
    [Fact]
    public async Task Created_key_is_returned_once_and_hashed()
    {
        var db = TestSupport.NewDbFactory();
        var service = new ApiKeyService(db);

        var (info, fullKey) = await service.CreateAsync("Acme Payments", Roles.Integrator, "admin");

        Assert.StartsWith("txg_live_", fullKey);
        Assert.StartsWith(info.Prefix, fullKey);
        Assert.True(info.Active);

        await using var ctx = await db.CreateDbContextAsync();
        var stored = await ctx.ApiKeys.SingleAsync();
        Assert.DoesNotContain(fullKey, stored.Hash);   // the secret itself is never persisted
        Assert.Equal(64, stored.Hash.Length);          // SHA-256, hex
    }

    [Fact]
    public async Task Valid_key_authenticates_as_its_role_and_records_last_use()
    {
        var db = TestSupport.NewDbFactory();
        var service = new ApiKeyService(db);
        var (_, fullKey) = await service.CreateAsync("Acme Payments", Roles.Integrator, "admin");

        var identity = await service.ValidateAsync(fullKey);

        Assert.NotNull(identity);
        Assert.Equal(Roles.Integrator, identity!.Role);
        Assert.Equal("Acme Payments", identity.Name);

        await using var ctx = await db.CreateDbContextAsync();
        Assert.NotNull((await ctx.ApiKeys.SingleAsync()).LastUsedAtUtc);
    }

    [Theory]
    [InlineData("")]
    [InlineData("not-a-txguard-key")]
    [InlineData("txg_live_deadbeef")]
    public async Task Unknown_or_malformed_keys_are_rejected(string presented)
    {
        var service = new ApiKeyService(TestSupport.NewDbFactory());
        Assert.Null(await service.ValidateAsync(presented));
    }

    [Fact]
    public async Task Revoked_key_stops_authenticating()
    {
        var service = new ApiKeyService(TestSupport.NewDbFactory());
        var (info, fullKey) = await service.CreateAsync("Acme Payments", Roles.Integrator, "admin");

        Assert.True(await service.RevokeAsync(info.Id));
        Assert.Null(await service.ValidateAsync(fullKey));
        Assert.False(await service.RevokeAsync(info.Id));
        Assert.False(await service.RevokeAsync(9999));
    }

    [Fact]
    public async Task Listing_exposes_metadata_but_never_the_secret()
    {
        var service = new ApiKeyService(TestSupport.NewDbFactory());
        var (_, first) = await service.CreateAsync("Partner A", Roles.Integrator, "admin");
        await service.CreateAsync("Partner B", Roles.Integrator, "admin");

        var keys = await service.ListAsync();

        Assert.Equal(2, keys.Count);
        Assert.All(keys, k => Assert.DoesNotContain(first, k.Prefix));
        Assert.Contains(keys, k => k.Name == "Partner A");
    }

    [Fact]
    public async Task Two_keys_are_never_identical()
    {
        var service = new ApiKeyService(TestSupport.NewDbFactory());
        var (_, a) = await service.CreateAsync("A", Roles.Integrator, "admin");
        var (_, b) = await service.CreateAsync("B", Roles.Integrator, "admin");

        Assert.NotEqual(a, b);
    }
}
