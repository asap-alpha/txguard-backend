using TxGuard.Infrastructure.Persistence;

namespace TxGuard.Api.Contracts;

public static class Mapping
{
    public static TransactionDto ToDto(this TransactionEntity t) => new(
        t.TransactionId,
        t.SenderName, t.SenderNumber, t.SenderProvider,
        t.RecipientName, t.RecipientNumber, t.RecipientProvider,
        t.AmountMinor, t.Currency, t.Type.ToString(),
        t.State.ToString(), t.FailureReason, t.Retries,
        t.RiskScore, t.RiskLevel?.ToString(), t.FraudModelVersion,
        t.CreatedAtUtc, t.UpdatedAtUtc);

    public static AuditEventDto ToDto(this AuditEventEntity e) => new(
        e.Id, e.TransactionId, e.EventType.ToString(),
        e.PreviousState?.ToString(), e.NewState?.ToString(),
        e.Details, e.DataJson, e.TimestampUtc);
}
