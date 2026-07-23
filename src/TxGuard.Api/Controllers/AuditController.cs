using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using TxGuard.Api.Contracts;
using TxGuard.Domain.Enums;
using TxGuard.Infrastructure.Persistence;

namespace TxGuard.Api.Controllers;

[ApiController]
[Authorize]   // any authenticated role may read the audit log
[Route("api/v1/audit")]
public sealed class AuditController : ControllerBase
{
    private readonly IDbContextFactory<TxGuardDbContext> _dbf;

    public AuditController(IDbContextFactory<TxGuardDbContext> dbf) => _dbf = dbf;

    // ── Audit log query (FR-AL-003) ───────────────────────────────────────
    [HttpGet]
    public async Task<IActionResult> Query(
        [FromQuery] string? eventType, [FromQuery] string? transactionId,
        [FromQuery] int? page, [FromQuery] int? pageSize)
    {
        var p = page is null or <= 0 ? 1 : page.Value;
        var ps = pageSize is null or <= 0 or > 500 ? 50 : pageSize.Value;

        await using var db = await _dbf.CreateDbContextAsync();
        var q = db.AuditEvents.AsNoTracking().OrderByDescending(e => e.Id).AsQueryable();
        if (!string.IsNullOrWhiteSpace(transactionId))
            q = q.Where(e => e.TransactionId == transactionId);
        if (!string.IsNullOrWhiteSpace(eventType) && Enum.TryParse<AuditEventType>(eventType, true, out var et))
            q = q.Where(e => e.EventType == et);

        var total = await q.CountAsync();
        var items = await q.Skip((p - 1) * ps).Take(ps)
            .Select(e => e.ToDto()).ToListAsync();
        return Ok(new PagedResult<AuditEventDto>(items, p, ps, total));
    }
}
