namespace TxGuard.Domain.Enums;

/// <summary>
/// Immutable audit event types recorded for every state transition (SRS §3.5, §3.11).
/// The 14 lifecycle events from the demo Audit Log plus fraud-decision events.
/// </summary>
public enum AuditEventType
{
    /// <summary>
    /// Fallback for an event type persisted by an older/newer schema (or bad data) that
    /// this build does not recognise. Never written by the app; it exists so reading the
    /// audit log can degrade to a label instead of failing the whole request. Kept last so
    /// the ordinals of the real events are unaffected.
    /// </summary>
    Unknown = -1,

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
