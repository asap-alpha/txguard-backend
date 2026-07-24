using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using TxGuard.Api.Contracts;
using TxGuard.Domain;
using TxGuard.Infrastructure.Configuration;

namespace TxGuard.Api.Controllers;

/// <summary>
/// Read-only operational limits an integrator builds against — chiefly the maximum
/// transaction amount enforced at submission. Served from configuration so the value
/// shown in the Integration Guide is always the one the API actually enforces, never a
/// hard-coded number that can silently drift.
/// </summary>
[ApiController]
[Authorize]
[Route("api/v1/meta")]
public sealed class MetaController : ControllerBase
{
    private readonly TxGuardOptions _options;

    public MetaController(IOptions<TxGuardOptions> options) => _options = options.Value;

    [HttpGet]
    public IActionResult Get() => Ok(new MetaResponse(
        _options.MaxAmountMinor,
        Money.DefaultCurrency,
        _options.LowRiskThreshold,
        _options.HighRiskThreshold,
        (int)_options.IdempotencyWindow.TotalHours));
}
