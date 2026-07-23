using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;

namespace TxGuard.Api.Auth;

/// <summary>
/// Authenticates machine/integrator callers by the <c>X-Api-Key</c> header. On success
/// the request runs as the key's role (Integrator), identified by the key's name, so
/// the same <c>[Authorize(Roles=...)]</c> gates apply to human and machine callers alike.
/// </summary>
public sealed class ApiKeyAuthenticationHandler : AuthenticationHandler<AuthenticationSchemeOptions>
{
    public const string SchemeName = "ApiKey";
    public const string HeaderName = "X-Api-Key";

    private readonly ApiKeyService _keys;

    public ApiKeyAuthenticationHandler(
        IOptionsMonitor<AuthenticationSchemeOptions> options,
        ILoggerFactory logger, UrlEncoder encoder, ApiKeyService keys)
        : base(options, logger, encoder)
    {
        _keys = keys;
    }

    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        if (!Request.Headers.TryGetValue(HeaderName, out var presented) || string.IsNullOrWhiteSpace(presented))
            return AuthenticateResult.NoResult();

        var identity = await _keys.ValidateAsync(presented!, Context.RequestAborted);
        if (identity is null)
            return AuthenticateResult.Fail("Invalid or revoked API key");

        var claims = new[]
        {
            new Claim(ClaimTypes.Name, identity.Name),
            new Claim(ClaimTypes.Role, identity.Role),
            new Claim("displayName", identity.Name),
            new Claim("apiKeyId", identity.Id.ToString()),
        };
        var principal = new ClaimsPrincipal(new ClaimsIdentity(claims, SchemeName));
        return AuthenticateResult.Success(new AuthenticationTicket(principal, SchemeName));
    }
}
