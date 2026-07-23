using TxGuard.Domain.Enums;

namespace TxGuard.Infrastructure.Persistence;

/// <summary>
/// Append-only audit event (SRS §3.5). Once written, rows are never updated or
/// deleted (FR-AL-004 tamper-evident). Records the state transition and metadata.
/// </summary>
public class AuditEventEntity
{
    public long Id { get; set; }                       // identity, also gives ordering
    public string TransactionId { get; set; } = default!;
    public AuditEventType EventType { get; set; }
    public TransactionState? PreviousState { get; set; }
    public TransactionState? NewState { get; set; }
    public string? Details { get; set; }
    public string? DataJson { get; set; }              // structured metadata (JSON)
    public DateTime TimestampUtc { get; set; }
}
