namespace TxGuard.Domain.Abstractions;

/// <summary>
/// Port for pushing live updates to connected dashboards. Implemented over SignalR
/// in the API; a no-op default is used when no real-time transport is configured.
/// </summary>
public interface ITransactionNotifier
{
    Task TransactionChangedAsync(string transactionId, CancellationToken ct = default);
}
