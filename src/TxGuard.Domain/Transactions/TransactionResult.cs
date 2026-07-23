using TxGuard.Domain.Enums;

namespace TxGuard.Domain.Transactions;

/// <summary>Terminal outcome returned by a completed transaction workflow.</summary>
public sealed record TransactionResult(
    string TransactionId,
    TransactionState FinalState,
    string? FailureReason);

/// <summary>Analyst decision delivered to a workflow waiting in FRAUD_REVIEW (FR-AI-003).</summary>
public enum FraudDecision
{
    Approve,
    Reject
}
