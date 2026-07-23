using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using TxGuard.Api.Auth;
using TxGuard.Api.Contracts;

namespace TxGuard.Api.Controllers;

/// <summary>
/// Authentication: exchange credentials for a signed JWT, and read back the caller's
/// identity. Every other API surface is gated by <c>[Authorize]</c> + roles; this
/// controller is the one anonymous entry point.
/// </summary>
[ApiController]
[Route("api/v1/auth")]
public sealed class AuthController : ControllerBase
{
    private readonly UserStore _users;
    private readonly TokenService _tokens;

    public AuthController(UserStore users, TokenService tokens)
    {
        _users = users;
        _tokens = tokens;
    }

    // ── Login (anonymous) ─────────────────────────────────────────────────
    // Rate-limited harder than the rest of the API: this is the only anonymous entry
    // point, so it is the natural target for credential stuffing.
    [AllowAnonymous]
    [EnableRateLimiting("auth")]
    [HttpPost("login")]
    public IActionResult Login([FromBody] LoginRequest body)
    {
        var user = _users.Validate(body.Username, body.Password);
        if (user is null)
            return Unauthorized(new ApiError("TXG-AUTH", "Invalid username or password"));

        var (token, expires) = _tokens.Issue(user);
        return Ok(new LoginResponse(token, user.Username, user.DisplayName, user.Role, expires));
    }

    // ── Who am I (any authenticated caller) ───────────────────────────────
    [Authorize]
    [HttpGet("me")]
    public IActionResult Me()
    {
        var username = User.FindFirstValue(ClaimTypes.Name) ?? User.Identity?.Name ?? "";
        var role = User.FindFirstValue(ClaimTypes.Role) ?? "";
        var displayName = User.FindFirstValue("displayName") ?? username;
        return Ok(new MeResponse(username, displayName, role));
    }
}
