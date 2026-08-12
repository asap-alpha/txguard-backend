using Microsoft.EntityFrameworkCore;
using TxGuard.Domain.Abstractions;
using TxGuard.Domain.Enums;
using TxGuard.Domain.Errors;
using TxGuard.Domain.Transactions;
using TxGuard.Infrastructure.Configuration;
using TxGuard.Infrastructure.Persistence;

namespace TxGuard.Tests;

/// <summary>
/// Shared fixtures for the test suite: sample requests, configurable fakes for the
/// pluggable ports, and an in-memory <see cref="TxGuardDbContext"/> factory so the
/// read-model store can be exercised without a Postgres server.
/// </summary>
internal static class TestSupport
{
    public static TransactionRequest Request(
        string id = "TXG-test", long amountMinor = 125_00, TransactionType type = TransactionType.Transfer)
        => new(
            id,
            new Party("acc-sender", "Ama Owusu", "0244000000", "MTN MoMo"),
            new Party("acc-recipient", "Kofi Mensah", "0201111111", "GCB Bank"),
            amountMinor, "GHS", type, IdempotencyKey: id, Reference: "test",
            CallerIp: null, CreatedAtUtc: new DateTime(2026, 1, 1, 9, 0, 0, DateTimeKind.Utc));

    /// <summary>A fresh isolated in-memory database per call.</summary>
    public static IDbContextFactory<TxGuardDbContext> NewDbFactory()
        => new InMemoryDbContextFactory(Guid.NewGuid().ToString("N"));

    private sealed class InMemoryDbContextFactory : IDbContextFactory<TxGuardDbContext>
    {
        private readonly string _name;
        public InMemoryDbContextFactory(string name) => _name = name;

        public TxGuardDbContext CreateDbContext() =>
            new(new DbContextOptionsBuilder<TxGuardDbContext>()
                .UseInMemoryDatabase(_name)
                // The read model has no real relational constraints under test; silence the
                // provider's "transactions are ignored" warning rather than failing on it.
                .ConfigureWarnings(w => w.Ignore(
                    Microsoft.EntityFrameworkCore.Diagnostics.InMemoryEventId.TransactionIgnoredWarning))
                .Options);
    }
}

/// <summary>Fraud scorer returning a fixed score, classified against real thresholds.</summary>
internal sealed class FixedScorer : IFraudScorer
{
    private readonly double _score, _low, _high;

    public FixedScorer(double score, double low = 0.40, double high = 0.80)
    {
        _score = score;
        _low = low;
        _high = high;
    }

    public Task<FraudAssessment> ScoreAsync(TransactionRequest r, CancellationToken ct = default) =>
        Task.FromResult(FraudAssessment.Classify(
            _score, "test-model", new Dictionary<string, double> { ["fixed"] = _score }, _low, _high));
}

/// <summary>
/// Banking adapter that records every call (operation + idempotency key) and can be
/// told how each leg should behave. Used to assert both control flow and that no funds
/// move on paths where none should.
/// </summary>
internal sealed class RecordingBank : IBankingAdapter
{
    private readonly object _lock = new();
    private int _reversalAttempts;

    /// <summary>(operation, idempotencyKey) for every call, in order.</summary>
    public List<(string Operation, string Key)> Calls { get; } = new();

    /// <summary>Credit fails permanently (drives saga compensation).</summary>
    public bool CreditFailsPermanently { get; init; }

    /// <summary>Debit fails permanently (terminal DebitFailed, no credit leg).</summary>
    public bool DebitFailsPermanently { get; init; }

    /// <summary>Number of times the reversal is refused (transiently) before it settles.</summary>
    public int ReversalRefusals { get; init; }

    public int CountOf(string operation)
    {
        lock (_lock) return Calls.Count(c => c.Operation == operation);
    }

    public Task<BankOperationReceipt> DebitAsync(Party a, long m, string c, string key, CancellationToken ct = default)
    {
        Record("debit", key);
        if (DebitFailsPermanently)
            throw new PermanentBankingException(TxGuardError.InsufficientFunds, "sender has no balance");
        return Ok("DBT");
    }

    public Task<BankOperationReceipt> CreditAsync(Party a, long m, string c, string key, CancellationToken ct = default)
    {
        Record("credit", key);
        if (CreditFailsPermanently)
            throw new PermanentBankingException(TxGuardError.AccountNotFound, "recipient unreachable");
        return Ok("CRD");
    }

    public Task<BankOperationReceipt> ReverseAsync(Party a, long m, string c, string key, CancellationToken ct = default)
    {
        Record("reversal", key);
        if (Interlocked.Increment(ref _reversalAttempts) <= ReversalRefusals)
            throw new TransientBankingException(TxGuardError.ReversalFailed, "rail refused the reversal");
        return Ok("REV");
    }

    private void Record(string operation, string key)
    {
        lock (_lock) Calls.Add((operation, key));
    }

    private static Task<BankOperationReceipt> Ok(string prefix) =>
        Task.FromResult(new BankOperationReceipt(prefix + "-REF", DateTime.UtcNow));
}

/// <summary>Transaction store that discards everything — for workflow-only tests.</summary>
internal sealed class NoOpStore : ITransactionStore
{
    public Task RecordCreatedAsync(TransactionRequest r, CancellationToken ct = default) => Task.CompletedTask;
    public Task RecordFraudAsync(string id, FraudAssessment a, CancellationToken ct = default) => Task.CompletedTask;
    public Task TransitionAsync(string id, TransactionState s, AuditEventType e, string? r = null,
        int? rt = null, object? d = null, CancellationToken ct = default) => Task.CompletedTask;
    public Task AppendEventAsync(string id, AuditEventType t, string? de = null, object? d = null,
        CancellationToken ct = default) => Task.CompletedTask;
}

/// <summary>Captures notifier calls so real-time fan-out can be asserted.</summary>
internal sealed class CountingNotifier : ITransactionNotifier
{
    public List<string> Notified { get; } = new();

    public Task TransactionChangedAsync(string transactionId, CancellationToken ct = default)
    {
        Notified.Add(transactionId);
        return Task.CompletedTask;
    }
}

/// <summary>Runtime settings with directly assignable values, for scorer/adapter tests.</summary>
internal sealed class StubSettings : IRuntimeSettings
{
    public double LowRiskThreshold { get; set; } = 0.40;
    public double HighRiskThreshold { get; set; } = 0.80;
    public long MaxAmountMinor { get; set; } = 1_000_000;
    public double DebitTransientFailureRate { get; set; }
    public double CreditTransientFailureRate { get; set; }
    public double CreditPermanentFailureRate { get; set; }
    public double ReversalPermanentFailureRate { get; set; }
    public int LatencyMs { get; set; }
    public void ResetToConfiguredDefaults() { }
}
