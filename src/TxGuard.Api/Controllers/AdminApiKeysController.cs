using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using TxGuard.Api.Auth;
using TxGuard.Api.Contracts;

namespace TxGuard.Api.Controllers;

/// <summary>
/// Admin management of integrator API keys. Admin-only. The secret is returned exactly
/// once, on creation; thereafter only non-secret metadata is available.
/// </summary>
[ApiController]
[Authorize(Roles = Roles.Admin)]
[Route("api/v1/admin/api-keys")]
public sealed class AdminApiKeysController : ControllerBase
{
    private readonly ApiKeyService _keys;

    public AdminApiKeysController(ApiKeyService keys) => _keys = keys;

    [HttpGet]
    public async Task<IActionResult> List()
    {
        var keys = await _keys.ListAsync();
        return Ok(keys.Select(Map).ToList());
    }

    [HttpPost]
    public async Task<IActionResult> Create([FromBody] CreateApiKeyRequest body)
    {
        if (string.IsNullOrWhiteSpace(body.Name))
            return BadRequest(new ApiError("TXG-INVALID", "A key name is required"));

        var admin = User.FindFirstValue(ClaimTypes.Name) ?? "admin";
        // Keys authenticate machine integrators — always the Integrator role.
        var (info, fullKey) = await _keys.CreateAsync(body.Name.Trim(), Roles.Integrator, admin);
        return Ok(new CreateApiKeyResponse(Map(info), fullKey,
            "Copy this key now — it will not be shown again."));
    }

    [HttpDelete("{id:long}")]
    public async Task<IActionResult> Revoke(long id)
    {
        var revoked = await _keys.RevokeAsync(id);
        if (!revoked)
            return NotFound(new ApiError("TXG-INVALID", "Key not found or already revoked"));
        return Ok(new { id, revoked = true });
    }

    private static ApiKeyDto Map(ApiKeyInfo k) => new(
        k.Id, k.Name, k.Prefix, k.Role, k.CreatedBy,
        k.CreatedAtUtc, k.LastUsedAtUtc, k.RevokedAtUtc, k.Active);
}
