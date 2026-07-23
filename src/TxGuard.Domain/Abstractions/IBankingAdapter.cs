using TxGuard.Domain.Transactions;

namespace TxGuard.Domain.Abstractions;

/// <summary>
/// Port to an external payment rail (core banking / mobile money). Implementations
/// are modular per provider so new rails can be added without touching workflow
/// logic (NFR-M-001, Risk #3 adapter pattern). All write operations take an
/// idempotency key so re-execution never double-processes (SRS §2.6, FR-DB-002).
///
/// On failure, implementations throw <see cref="Errors.PermanentBankingException"/>
/// (do not retry) or <see cref="Errors.TransientBankingException"/> (retryable).
/// </summary>
public interface IBankingAdapter
{
    Task<BankOperationReceipt> DebitAsync(
        Party account, long amountMinor, string currency, string idempotencyKey,
        CancellationToken ct = default);

    Task<BankOperationReceipt> CreditAsync(
        Party account, long amountMinor, string currency, string idempotencyKey,
        CancellationToken ct = default);

    Task<BankOperationReceipt> ReverseAsync(
        Party account, long amountMinor, string currency, string idempotencyKey,
        CancellationToken ct = default);
}

/// <summary>Receipt returned by a successful banking operation.</summary>
public sealed record BankOperationReceipt(string ProviderReference, DateTime ProcessedAtUtc);
