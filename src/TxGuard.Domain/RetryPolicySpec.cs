namespace TxGuard.Domain;

/// <summary>
/// Retry policy specification for a durable activity (SRS §6.2). Values are the
/// SRS defaults but are intended to be externally configurable (NFR-M-003) — the
/// intelligent retry planner (§3.10) may override the interval at runtime, falling
/// back to these fixed schedules when no model prediction is available (FR-IRT-005).
/// </summary>
/// <param name="MaxAttempts">Zero means unlimited — Temporal retries until the activity succeeds.</param>
/// <param name="MaximumInterval">Caps exponential growth between attempts. Null leaves Temporal's default.</param>
public sealed record RetryPolicySpec(
    TimeSpan InitialInterval,
    double BackoffCoefficient,
    int MaxAttempts,
    IReadOnlyList<string> NonRetryableErrorTypes,
    TimeSpan? MaximumInterval = null)
{
    // Non-retryable domain error *names* (matched by Temporal against the thrown
    // exception type). PermanentBankingException is always non-retryable.
    private static readonly IReadOnlyList<string> Permanent = new[] { nameof(Errors.PermanentBankingException) };

    /// <summary>Debit Account: 1s initial, ×2, max 5 attempts.</summary>
    public static readonly RetryPolicySpec Debit =
        new(TimeSpan.FromSeconds(1), 2.0, 5, Permanent);

    /// <summary>Credit Account: 2s initial, ×2, max 7 attempts.</summary>
    public static readonly RetryPolicySpec Credit =
        new(TimeSpan.FromSeconds(2), 2.0, 7, Permanent);

    /// <summary>
    /// Reverse Debit: 5s initial, ×1.5, capped at 1 minute between attempts, retried
    /// FOREVER. Unlike debit and credit, reversal has no attempt ceiling and no
    /// non-retryable errors — a debited sender must always get their funds back, so
    /// giving up is not an outcome the saga is allowed to have. Manual review is
    /// reserved for fraud (FRAUD_REVIEW); it is never a compensation outcome.
    /// </summary>
    public static readonly RetryPolicySpec Reversal =
        new(TimeSpan.FromSeconds(5), 1.5, 0, Array.Empty<string>(), TimeSpan.FromMinutes(1));

    /// <summary>Send Notification: 1s initial, ×2, max 3 attempts.</summary>
    public static readonly RetryPolicySpec Notification =
        new(TimeSpan.FromSeconds(1), 2.0, 3, Permanent);
}
