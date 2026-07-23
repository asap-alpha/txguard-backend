using TxGuard.Domain.Enums;

namespace TxGuard.Infrastructure.Persistence;

/// <summary>
/// Read-model row for a transaction — the projection queried by the dashboard,
/// transactions list, and fraud-review screens. Money is stored in minor units.
/// </summary>
public class TransactionEntity
{
    public string TransactionId { get; set; } = default!;   // TXG-{uuid4}
    public string IdempotencyKey { get; set; } = default!;

    // Sender
    public string SenderAccountId { get; set; } = default!;
    public string SenderName { get; set; } = default!;
    public string SenderNumber { get; set; } = default!;
    public string SenderProvider { get; set; } = default!;

    // Recipient
    public string RecipientAccountId { get; set; } = default!;
    public string RecipientName { get; set; } = default!;
    public string RecipientNumber { get; set; } = default!;
    public string RecipientProvider { get; set; } = default!;

    public long AmountMinor { get; set; }
    public string Currency { get; set; } = "GHS";
    public TransactionType Type { get; set; }
    public string? Reference { get; set; }

    public TransactionState State { get; set; } = TransactionState.Pending;
    public string? FailureReason { get; set; }
    public int Retries { get; set; }

    // Fraud
    public double? RiskScore { get; set; }
    public RiskLevel? RiskLevel { get; set; }
    public string? FraudModelVersion { get; set; }

    public DateTime CreatedAtUtc { get; set; }
    public DateTime UpdatedAtUtc { get; set; }

    public List<AuditEventEntity> Events { get; set; } = new();
}
