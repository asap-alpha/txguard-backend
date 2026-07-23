using Temporalio.Activities;
using TxGuard.Domain.Abstractions;
using TxGuard.Domain.Enums;
using TxGuard.Domain.Transactions;

namespace TxGuard.Workflows;

/// <summary>
/// Inputs for the generic state-transition and event activities. Records keep the
/// Temporal payloads simple and stable for serialization.
/// </summary>
public sealed record TransitionInput(
    string TransactionId, TransactionState State, AuditEventType EventType, string? Reason = null);

public sealed record AppendEventInput(
    string TransactionId, AuditEventType Type, string? Details = null);

/// <summary>
/// The non-deterministic side-effecting steps of a transaction. Each is a Temporal
/// Activity: retried independently, idempotent, and durably recorded. Dependencies
/// are injected so the underlying banking rail and fraud model are pluggable.
/// </summary>
public sealed class TransactionActivities
{
    private readonly IBankingAdapter _bank;
    private readonly IFraudScorer _fraud;
    private readonly ITransactionStore _store;

    public TransactionActivities(IBankingAdapter bank, IFraudScorer fraud, ITransactionStore store)
    {
        _bank = bank;
        _fraud = fraud;
        _store = store;
    }

    [Activity("record_created")]
    public Task RecordCreated(TransactionRequest req) => _store.RecordCreatedAsync(req);

    [Activity("score_fraud")]
    public async Task<FraudAssessment> ScoreFraud(TransactionRequest req)
    {
        var assessment = await _fraud.ScoreAsync(req);
        await _store.RecordFraudAsync(req.TransactionId, assessment);
        return assessment;
    }

    [Activity("transition")]
    public Task Transition(TransitionInput i)
        => _store.TransitionAsync(i.TransactionId, i.State, i.EventType, i.Reason);

    [Activity("append_event")]
    public Task AppendEvent(AppendEventInput i)
        => _store.AppendEventAsync(i.TransactionId, i.Type, i.Details);

    [Activity("debit")]
    public async Task<BankOperationReceipt> Debit(TransactionRequest req)
    {
        await RecordRetryIfAny(req.TransactionId, "debit");
        return await _bank.DebitAsync(req.Sender, req.AmountMinor, req.Currency, req.TransactionId);
    }

    [Activity("credit")]
    public async Task<BankOperationReceipt> Credit(TransactionRequest req)
    {
        await RecordRetryIfAny(req.TransactionId, "credit");
        // '-credit' idempotency suffix per FR-CR-002.
        return await _bank.CreditAsync(req.Recipient, req.AmountMinor, req.Currency, $"{req.TransactionId}-credit");
    }

    [Activity("reverse")]
    public async Task<BankOperationReceipt> Reverse(TransactionRequest req)
    {
        await RecordRetryIfAny(req.TransactionId, "reversal");
        // '-reversal' idempotency suffix per FR-CP-002.
        return await _bank.ReverseAsync(req.Sender, req.AmountMinor, req.Currency, $"{req.TransactionId}-reversal");
    }

    /// <summary>On retry attempts (&gt;1), record a RetryScheduled audit event and bump the retry counter.</summary>
    private async Task RecordRetryIfAny(string transactionId, string operation)
    {
        var attempt = ActivityExecutionContext.Current.Info.Attempt;
        if (attempt > 1)
            await _store.AppendEventAsync(transactionId, AuditEventType.RetryScheduled,
                $"Retry attempt {attempt} for {operation}", new { attempt, operation });
    }
}
