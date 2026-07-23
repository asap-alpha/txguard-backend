using TxGuard.Domain.Enums;
using TxGuard.Domain.Transactions;

namespace TxGuard.Domain.Abstractions;

/// <summary>
/// Write-side port for the queryable read model. Temporal's event history is the
/// durable source of truth; this projection makes transactions and their audit
/// trail queryable for the dashboard and audit APIs (SRS §3.6, §3.8).
///
/// Implementations MUST be idempotent per transaction: activities can be re-run
/// after a worker crash, so the same call may arrive more than once.
/// </summary>
public interface ITransactionStore
{
    /// <summary>Insert the transaction row (PENDING) and log TransactionCreated. No-op if it already exists.</summary>
    Task RecordCreatedAsync(TransactionRequest request, CancellationToken ct = default);

    /// <summary>Attach the fraud score/classification and log a FraudScored event.</summary>
    Task RecordFraudAsync(string transactionId, FraudAssessment assessment, CancellationToken ct = default);

    /// <summary>
    /// Move the transaction to <paramref name="newState"/>, update the failure reason
    /// and retry count, and append the corresponding lifecycle audit event capturing
    /// previous → new state.
    /// </summary>
    Task TransitionAsync(string transactionId, TransactionState newState, AuditEventType eventType,
        string? failureReason = null, int? retries = null, object? data = null, CancellationToken ct = default);

    /// <summary>Append a non-transition audit event (e.g. DebitInitiated, RetryScheduled).</summary>
    Task AppendEventAsync(string transactionId, AuditEventType type,
        string? details = null, object? data = null, CancellationToken ct = default);
}
