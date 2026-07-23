namespace TxGuard.Domain.Enums;

/// <summary>
/// Transaction lifecycle states. Mirrors the deterministic state machine in
/// SRS §6.1. Terminal states end the workflow; non-terminal states may transition.
/// </summary>
public enum TransactionState
{
    /// <summary>Accepted; workflow started. Next: FraudReview or Debiting.</summary>
    Pending,

    /// <summary>Held pending manual fraud review (ML risk &gt; HIGH threshold). Non-terminal.</summary>
    FraudReview,

    /// <summary>Denied by an analyst during fraud review. Terminal — no funds moved.</summary>
    FraudRejected,

    /// <summary>Debit activity executing or retrying.</summary>
    Debiting,

    /// <summary>Permanent debit failure. Terminal.</summary>
    DebitFailed,

    /// <summary>Credit activity executing, retrying, or durably waiting.</summary>
    Crediting,

    /// <summary>Both debit and credit succeeded. Terminal — happy path.</summary>
    Completed,

    /// <summary>Credit exhausted all retries. Transitions to Reversing.</summary>
    CreditFailed,

    /// <summary>Debit reversal (compensation) executing.</summary>
    Reversing,

    /// <summary>Rolled back successfully after credit failure. Terminal.</summary>
    Failed,

    /// <summary>
    /// Legacy. Formerly set when a reversal failed. No longer reachable: reversal now
    /// retries until it succeeds, and manual review is reserved for fraud holds
    /// (<see cref="FraudReview"/>). Retained only so transactions already persisted in
    /// this state still deserialise and render.
    /// </summary>
    ManualReview
}

public static class TransactionStateExtensions
{
    public static bool IsTerminal(this TransactionState state) => state is
        TransactionState.FraudRejected or
        TransactionState.DebitFailed or
        TransactionState.Completed or
        TransactionState.Failed or
        TransactionState.ManualReview;
}
