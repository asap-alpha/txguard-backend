using TxGuard.Domain.Abstractions;

namespace TxGuard.Infrastructure;

/// <summary>Default no-op notifier used when no real-time transport (e.g. SignalR) is registered.</summary>
public sealed class NullTransactionNotifier : ITransactionNotifier
{
    public Task TransactionChangedAsync(string transactionId, CancellationToken ct = default)
        => Task.CompletedTask;
}
