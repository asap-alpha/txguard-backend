namespace TxGuard.Domain.Errors;

/// <summary>
/// Canonical error registry from SRS §9.2. Each error carries a stable code, an
/// HTTP status for the API surface, and a human-readable description.
/// </summary>
public sealed record TxGuardError(string Code, string Name, int HttpStatus, string Description)
{
    public static readonly TxGuardError InsufficientFunds =
        new("TXG-001", "INSUFFICIENT_FUNDS", 422, "Sender account has insufficient balance");

    public static readonly TxGuardError AccountNotFound =
        new("TXG-002", "ACCOUNT_NOT_FOUND", 404, "Sender or recipient account does not exist");

    public static readonly TxGuardError DuplicateTransaction =
        new("TXG-003", "DUPLICATE_TRANSACTION", 409, "Idempotency key already exists; original returned");

    public static readonly TxGuardError NetworkTimeout =
        new("TXG-004", "NETWORK_TIMEOUT", 503, "Transient network error; will be retried automatically");

    public static readonly TxGuardError CreditExhausted =
        new("TXG-005", "CREDIT_EXHAUSTED", 500, "Credit failed after max retries; reversal initiated");

    public static readonly TxGuardError ReversalFailed =
        new("TXG-006", "REVERSAL_FAILED", 500, "Reversal failed; escalated to MANUAL_REVIEW");

    public static readonly TxGuardError AmountLimitExceeded =
        new("TXG-007", "AMOUNT_LIMIT_EXCEEDED", 400, "Transaction amount exceeds configured maximum");

    public static readonly TxGuardError WorkflowNotFound =
        new("TXG-008", "WORKFLOW_NOT_FOUND", 404, "Transaction ID does not exist in the system");

    public static readonly TxGuardError FraudRiskFlagged =
        new("TXG-009", "FRAUD_RISK_FLAGGED", 202,
            "Transaction accepted but held in FRAUD_REVIEW; ML risk score exceeded HIGH_RISK_THRESHOLD");

    public static readonly TxGuardError FraudRejected =
        new("TXG-010", "FRAUD_REJECTED", 403,
            "Transaction denied by operations staff following FRAUD_REVIEW; no funds were moved");
}
