using TxGuard.Domain.Enums;

namespace TxGuard.Domain.Transactions;

/// <summary>
/// The durable input to a transaction workflow. Kept minimal and PII-light
/// (NFR-S-007). Amount is in minor units (pesewas) per SRS §2.6.
/// </summary>
public sealed record TransactionRequest(
    string TransactionId,
    Party Sender,
    Party Recipient,
    long AmountMinor,
    string Currency,
    TransactionType Type,
    string IdempotencyKey,
    string? Reference,
    string? CallerIp,
    DateTime CreatedAtUtc)
{
    public Money Amount => new(AmountMinor, Currency);
}
