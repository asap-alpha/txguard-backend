namespace TxGuard.Domain.Errors;

/// <summary>
/// Base for banking-adapter failures. Carries the canonical <see cref="TxGuardError"/>
/// so the workflow and API can surface a stable code.
/// </summary>
public abstract class BankingException : Exception
{
    public TxGuardError Error { get; }

    protected BankingException(TxGuardError error, string? message = null)
        : base(message ?? error.Description)
    {
        Error = error;
    }
}

/// <summary>
/// A permanent failure that must NOT be retried (SRS FR-DB-004): insufficient funds,
/// account not found/closed/frozen, invalid currency. The activity retry policy lists
/// this type name in NonRetryableErrorTypes so Temporal fails fast instead of retrying.
/// </summary>
public sealed class PermanentBankingException : BankingException
{
    public PermanentBankingException(TxGuardError error, string? message = null)
        : base(error, message) { }
}

/// <summary>
/// A transient failure (network timeout, 5xx) that IS eligible for retry with backoff.
/// </summary>
public sealed class TransientBankingException : BankingException
{
    public TransientBankingException(TxGuardError error, string? message = null)
        : base(error, message) { }
}
