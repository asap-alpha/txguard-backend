namespace TxGuard.Domain.Transactions;

/// <summary>
/// A party in a transaction (sender or recipient). <see cref="Provider"/> is the
/// payment rail, e.g. "MTN MoMo", "Telecel", "GCB Bank", "Ecobank".
/// </summary>
public sealed record Party(
    string AccountId,
    string Name,
    string AccountNumber,
    string Provider);
