using Microsoft.AspNetCore.SignalR;
using TxGuard.Domain.Abstractions;

namespace TxGuard.Api.Realtime;

/// <summary>SignalR hub the Vue dashboard subscribes to for live transaction updates.</summary>
public sealed class TransactionsHub : Hub { }

/// <summary>
/// SignalR-backed <see cref="ITransactionNotifier"/>. Broadcasts a "transactionChanged"
/// event (carrying the transaction id) whenever the read model changes, so dashboards
/// update in real time instead of polling.
/// </summary>
public sealed class SignalRTransactionNotifier : ITransactionNotifier
{
    private readonly IHubContext<TransactionsHub> _hub;

    public SignalRTransactionNotifier(IHubContext<TransactionsHub> hub) => _hub = hub;

    public Task TransactionChangedAsync(string transactionId, CancellationToken ct = default)
        => _hub.Clients.All.SendAsync("transactionChanged", transactionId, ct);
}
