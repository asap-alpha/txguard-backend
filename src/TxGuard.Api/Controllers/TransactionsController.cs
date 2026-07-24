using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Temporalio.Client;
using Temporalio.Exceptions;
using TxGuard.Api.Auth;
using TxGuard.Api.Contracts;
using TxGuard.Domain;
using TxGuard.Domain.Enums;
using TxGuard.Domain.Errors;
using TxGuard.Domain.Transactions;
using TxGuard.Infrastructure.Configuration;
using TxGuard.Infrastructure.Persistence;
using TxGuard.Workflows;

namespace TxGuard.Api.Controllers;

[ApiController]
[Authorize]   // every action requires a valid token; individual actions tighten by role
[Route("api/v1/transactions")]
public sealed class TransactionsController : ControllerBase
{
    private readonly ITemporalClient _temporal;
    private readonly IDbContextFactory<TxGuardDbContext> _dbf;
    private readonly TxGuardOptions _options;

    public TransactionsController(
        ITemporalClient temporal,
        IDbContextFactory<TxGuardDbContext> dbf,
        IOptions<TxGuardOptions> options)
    {
        _temporal = temporal;
        _dbf = dbf;
        _options = options.Value;
    }

    // ── Submit a transaction (FR-TI-001..007) ─────────────────────────────
    [HttpPost]
    [Authorize(Roles = $"{Roles.Integrator},{Roles.Admin}")]
    public async Task<IActionResult> Submit([FromBody] SubmitTransactionRequest body)
    {
        var o = _options;
        var currency = string.IsNullOrWhiteSpace(body.Currency) ? Money.DefaultCurrency : body.Currency!;

        if (body.AmountMinor <= 0)
            return BadRequest(new ApiError(TxGuardError.AmountLimitExceeded.Code, "Amount must be greater than zero"));
        if (body.AmountMinor > o.MaxAmountMinor)
            return BadRequest(new ApiError(TxGuardError.AmountLimitExceeded.Code, TxGuardError.AmountLimitExceeded.Description));

        var idempotencyKey = string.IsNullOrWhiteSpace(body.IdempotencyKey)
            ? Guid.NewGuid().ToString("N") : body.IdempotencyKey!;

        // FR-TI-003: the transaction id (== Temporal workflow id) is derived
        // deterministically from the idempotency key, so two concurrent submissions
        // of the same key resolve to the same workflow — Temporal then rejects the
        // duplicate start atomically (see RejectDuplicate below), closing the
        // read-model race that a lookup-based check alone cannot.
        var txId = TxGuardConstants.DeriveTransactionId(idempotencyKey);

        // Fast path: an existing transaction for this key within the window → 409 (TXG-003).
        await using (var db = await _dbf.CreateDbContextAsync())
        {
            var cutoff = DateTime.UtcNow - o.IdempotencyWindow;
            var existing = await db.Transactions.AsNoTracking()
                .FirstOrDefaultAsync(t => t.IdempotencyKey == idempotencyKey && t.CreatedAtUtc >= cutoff);
            if (existing is not null)
                return Conflict(new ApiError(TxGuardError.DuplicateTransaction.Code,
                    TxGuardError.DuplicateTransaction.Description, existing.TransactionId));
        }

        var req = new TransactionRequest(
            txId,
            new Party(body.Sender.AccountId, body.Sender.Name, body.Sender.AccountNumber, body.Sender.Provider),
            new Party(body.Recipient.AccountId, body.Recipient.Name, body.Recipient.AccountNumber, body.Recipient.Provider),
            body.AmountMinor, currency, body.Type, idempotencyKey, body.Reference, null, DateTime.UtcNow);

        try
        {
            await _temporal.StartWorkflowAsync(
                (TransactionWorkflow wf) => wf.RunAsync(req),
                new WorkflowOptions(id: txId, taskQueue: TxGuardConstants.TaskQueue)
                {
                    // Reject a second start for the same id — the durable backstop for
                    // duplicate submissions racing ahead of the read-model write.
                    IdReusePolicy = Temporalio.Api.Enums.V1.WorkflowIdReusePolicy.RejectDuplicate,
                });
        }
        catch (WorkflowAlreadyStartedException)
        {
            // Duplicate idempotency key (TXG-003) — original transaction already exists.
            return Conflict(new ApiError(TxGuardError.DuplicateTransaction.Code,
                TxGuardError.DuplicateTransaction.Description, txId));
        }

        return Ok(new SubmitTransactionResponse(txId, TransactionState.Pending, "Transaction accepted"));
    }

    // ── Get one transaction with its audit trail (FR-SQ-001) ──────────────
    [HttpGet("{id}")]
    public async Task<IActionResult> GetOne(string id)
    {
        await using var db = await _dbf.CreateDbContextAsync();
        var tx = await db.Transactions.AsNoTracking().FirstOrDefaultAsync(t => t.TransactionId == id);
        if (tx is null)
            return NotFound(new ApiError(TxGuardError.WorkflowNotFound.Code, TxGuardError.WorkflowNotFound.Description, id));

        var events = await db.AuditEvents.AsNoTracking()
            .Where(e => e.TransactionId == id).OrderBy(e => e.Id)
            .Select(e => e.ToDto()).ToListAsync();

        // Surface whether this transfer has already been refunded. The dashboard refund
        // button derives the refund's idempotency key deterministically from the original
        // id ("refund-{id}"), so a single lookup finds the refund leg if one exists.
        RefundLinkDto? refund = null;
        if (tx.Type != TransactionType.Refund)
        {
            var refundKey = $"refund-{tx.TransactionId}";
            var refundLeg = await db.Transactions.AsNoTracking()
                .Where(t => t.IdempotencyKey == refundKey)
                .Select(t => new { t.TransactionId, t.State })
                .FirstOrDefaultAsync();
            if (refundLeg is not null)
                refund = new RefundLinkDto(refundLeg.TransactionId, refundLeg.State.ToString());
        }

        return Ok(new TransactionDetailDto(tx.ToDto(), events, refund));
    }

    // ── List transactions with paging + filter (FR-SQ-002) ────────────────
    [HttpGet]
    public async Task<IActionResult> List(
        [FromQuery] string? status, [FromQuery] string? type,
        [FromQuery] int? page, [FromQuery] int? pageSize)
    {
        var p = page is null or <= 0 ? 1 : page.Value;
        var ps = pageSize is null or <= 0 or > 200 ? 25 : pageSize.Value;

        await using var db = await _dbf.CreateDbContextAsync();
        var q = db.Transactions.AsNoTracking().OrderByDescending(t => t.CreatedAtUtc).AsQueryable();
        if (!string.IsNullOrWhiteSpace(status) && Enum.TryParse<TransactionState>(status, true, out var st))
            q = q.Where(t => t.State == st);
        if (!string.IsNullOrWhiteSpace(type) && Enum.TryParse<TransactionType>(type, true, out var ty))
            q = q.Where(t => t.Type == ty);

        var total = await q.CountAsync();
        var items = await q.Skip((p - 1) * ps).Take(ps)
            .Select(t => t.ToDto()).ToListAsync();
        return Ok(new PagedResult<TransactionDto>(items, p, ps, total));
    }

    // ── Refund / reversal of a completed transaction ──────────────────────
    // Integrators call this to return funds. It does NOT signal the original workflow
    // (that one is already terminal); it starts a NEW durable transaction in the
    // opposite direction, so the return leg gets the same retries and saga protection.
    [HttpPost("{id}/refund")]
    [Authorize(Roles = $"{Roles.Integrator},{Roles.Admin}")]
    public async Task<IActionResult> Refund(string id, [FromBody] RefundRequest? body)
    {
        TransactionEntity original;
        await using (var db = await _dbf.CreateDbContextAsync())
        {
            var found = await db.Transactions.AsNoTracking()
                .FirstOrDefaultAsync(t => t.TransactionId == id);
            if (found is null)
                return NotFound(new ApiError(TxGuardError.WorkflowNotFound.Code,
                    TxGuardError.WorkflowNotFound.Description, id));
            original = found;
        }

        // Only a Completed transaction has money on the far side to bring back. Every
        // other state either never moved funds or already compensated.
        if (original.State != TransactionState.Completed)
            return Conflict(new ApiError("TXG-INVALID",
                $"Only a Completed transaction can be refunded; {id} is {original.State}", id));

        // Deriving the key from the original id makes a duplicate refund request return
        // 409 (TXG-003) through the same dedup path as a duplicate submission.
        var idempotencyKey = string.IsNullOrWhiteSpace(body?.IdempotencyKey)
            ? $"refund-{original.TransactionId}" : body!.IdempotencyKey!;
        var refundTxId = TxGuardConstants.DeriveTransactionId(idempotencyKey);

        // Reverse the direction: the original recipient gives the money back to the sender.
        var req = new TransactionRequest(
            refundTxId,
            new Party(original.RecipientAccountId, original.RecipientName, original.RecipientNumber, original.RecipientProvider),
            new Party(original.SenderAccountId, original.SenderName, original.SenderNumber, original.SenderProvider),
            original.AmountMinor, original.Currency, TransactionType.Refund, idempotencyKey,
            $"Refund of {original.TransactionId}"
                + (string.IsNullOrWhiteSpace(body?.Reason) ? "" : $" — {body!.Reason}"),
            null, DateTime.UtcNow);

        try
        {
            await _temporal.StartWorkflowAsync(
                (TransactionWorkflow wf) => wf.RunAsync(req),
                new WorkflowOptions(id: refundTxId, taskQueue: TxGuardConstants.TaskQueue)
                {
                    IdReusePolicy = Temporalio.Api.Enums.V1.WorkflowIdReusePolicy.RejectDuplicate,
                });
        }
        catch (WorkflowAlreadyStartedException)
        {
            return Conflict(new ApiError(TxGuardError.DuplicateTransaction.Code,
                $"{original.TransactionId} has already been refunded", refundTxId));
        }

        return Ok(new RefundResponse(refundTxId, original.TransactionId,
            TransactionState.Pending, original.AmountMinor, "Refund accepted"));
    }

    // ── Fraud review decision (FR-AI-003) ─────────────────────────────────
    [HttpPost("{id}/fraud-decision")]
    [Authorize(Roles = $"{Roles.Analyst},{Roles.Admin}")]
    public async Task<IActionResult> FraudDecision(string id, [FromBody] FraudDecisionRequest body)
    {
        if (!Enum.TryParse<FraudDecision>(body.Decision, true, out var decision))
            return BadRequest(new ApiError("TXG-INVALID", "Decision must be 'Approve' or 'Reject'", id));

        var handle = _temporal.GetWorkflowHandle<TransactionWorkflow>(id);
        await handle.SignalAsync(wf => wf.SubmitFraudDecision(decision));
        return Ok(new { transactionId = id, decision = decision.ToString() });
    }
}
