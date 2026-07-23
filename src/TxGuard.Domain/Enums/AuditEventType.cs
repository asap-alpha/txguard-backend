namespace TxGuard.Domain.Enums;

/// <summary>
/// Immutable audit event types recorded for every state transition (SRS §3.5, §3.11).
/// The 14 lifecycle events from the demo Audit Log plus fraud-decision events.
/// </summary>
public enum AuditEventType
{
    TransactionCreated,
    FraudScored,
    FraudReviewQueued,
    FraudApproved,
    FraudRejected,
    DebitInitiated,
    DebitSucceeded,
    DebitFailed,
    CreditInitiated,
    CreditSucceeded,
    CreditFailed,
    RetryScheduled,
    ReversalInitiated,
    DebitReversed,
    ReversalFailed,
    ManualReviewEscalated,
    TransactionCompleted
}
