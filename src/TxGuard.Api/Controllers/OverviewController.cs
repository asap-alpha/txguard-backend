using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using TxGuard.Api.Contracts;
using TxGuard.Domain.Enums;
using TxGuard.Infrastructure.Persistence;

namespace TxGuard.Api.Controllers;

[ApiController]
[Authorize]   // any authenticated role may read overview metrics
[Route("api/v1/overview")]
public sealed class OverviewController : ControllerBase
{
    private readonly IDbContextFactory<TxGuardDbContext> _dbf;

    public OverviewController(IDbContextFactory<TxGuardDbContext> dbf) => _dbf = dbf;

    // ── Overview dashboard metrics (FR-ADM-001) ───────────────────────────
    [HttpGet]
    public async Task<IActionResult> Get()
    {
        await using var db = await _dbf.CreateDbContextAsync();
        var counts = await db.Transactions.AsNoTracking()
            .GroupBy(t => t.State)
            .Select(g => new { State = g.Key, Count = g.Count() })
            .ToListAsync();

        int C(TransactionState s) => counts.FirstOrDefault(x => x.State == s)?.Count ?? 0;
        var breakdown = counts.ToDictionary(x => x.State.ToString(), x => x.Count);
        var total = counts.Sum(x => x.Count);

        var inflight = C(TransactionState.Pending) + C(TransactionState.FraudReview) + C(TransactionState.Debiting)
                     + C(TransactionState.Crediting) + C(TransactionState.CreditFailed) + C(TransactionState.Reversing);
        var completed = C(TransactionState.Completed);
        var failed = C(TransactionState.DebitFailed) + C(TransactionState.Failed)
                   + C(TransactionState.ManualReview) + C(TransactionState.FraudRejected);
        var terminal = completed + failed;
        var successRate = terminal == 0 ? 0 : Math.Round(100.0 * completed / terminal, 1);

        var todayUtc = DateTime.UtcNow.Date;
        var completedToday = await db.Transactions.AsNoTracking()
            .CountAsync(t => t.State == TransactionState.Completed && t.UpdatedAtUtc >= todayUtc);

        return Ok(new OverviewDto(
            inflight, C(TransactionState.FraudReview), completedToday, failed,
            successRate, total, breakdown));
    }
}
