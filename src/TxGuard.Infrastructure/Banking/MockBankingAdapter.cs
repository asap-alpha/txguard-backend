using System.Collections.Concurrent;
using TxGuard.Domain.Abstractions;
using TxGuard.Domain.Errors;
using TxGuard.Domain.Transactions;
using TxGuard.Infrastructure.Configuration;

namespace TxGuard.Infrastructure.Banking;

/// <summary>
/// In-memory stand-in for real payment rails (SRS §1.3: MTN MoMo / Telecel use mock
/// adapters). Injects deterministic transient and permanent failures so retries,
/// exponential backoff, and saga compensation can be demonstrated end-to-end.
///
/// Failure decisions are seeded from a stable hash of (transactionId, operation) so
/// behaviour is reproducible; attempt counts are tracked in-process so a transaction
/// "fails a few times then succeeds", exercising the retry path.
/// </summary>
public sealed class MockBankingAdapter : IBankingAdapter
{
    private readonly IRuntimeSettings _opt;
    private readonly ConcurrentDictionary<string, int> _attempts = new();

    public MockBankingAdapter(IRuntimeSettings settings) => _opt = settings;

    public async Task<BankOperationReceipt> DebitAsync(Party account, long amountMinor, string currency,
        string idempotencyKey, CancellationToken ct = default)
    {
        await SimulateLatency(ct);
        var transientCount = PlannedTransientFailures(idempotencyKey, "debit", _opt.DebitTransientFailureRate);
        var attempt = _attempts.AddOrUpdate($"{idempotencyKey}:debit", 1, (_, v) => v + 1);
        if (attempt <= transientCount)
            throw new TransientBankingException(TxGuardError.NetworkTimeout,
                $"Debit transient failure (attempt {attempt}) for {account.Provider}");
        return Receipt("DBT");
    }

    public async Task<BankOperationReceipt> CreditAsync(Party account, long amountMinor, string currency,
        string idempotencyKey, CancellationToken ct = default)
    {
        await SimulateLatency(ct);

        // Some transactions fail credit permanently → drives saga compensation.
        if (Unit(idempotencyKey, "credit-permanent") < _opt.CreditPermanentFailureRate)
            throw new PermanentBankingException(TxGuardError.AccountNotFound,
                $"Credit permanently failed: recipient unreachable at {account.Provider}");

        var transientCount = PlannedTransientFailures(idempotencyKey, "credit", _opt.CreditTransientFailureRate);
        var attempt = _attempts.AddOrUpdate($"{idempotencyKey}:credit", 1, (_, v) => v + 1);
        if (attempt <= transientCount)
            throw new TransientBankingException(TxGuardError.NetworkTimeout,
                $"Credit transient failure (attempt {attempt}) for {account.Provider}");
        return Receipt("CRD");
    }

    public async Task<BankOperationReceipt> ReverseAsync(Party account, long amountMinor, string currency,
        string idempotencyKey, CancellationToken ct = default)
    {
        await SimulateLatency(ct);

        // Reversal is the safety net and is retried forever, so failures here are always
        // TRANSIENT — an "unlucky" transaction refuses a few times and then settles. A
        // permanent failure would loop indefinitely and strand the sender's funds, which
        // is precisely the outcome the unlimited retry policy exists to rule out.
        var refusals = PlannedReversalRefusals(idempotencyKey, _opt.ReversalPermanentFailureRate);
        var attempt = _attempts.AddOrUpdate($"{idempotencyKey}:reversal", 1, (_, v) => v + 1);
        if (attempt <= refusals)
            throw new TransientBankingException(TxGuardError.ReversalFailed,
                $"Reversal refused (attempt {attempt}) at {account.Provider} — retrying");

        return Receipt("REV");
    }

    private async Task SimulateLatency(CancellationToken ct)
    {
        if (_opt.LatencyMs > 0) await Task.Delay(_opt.LatencyMs, ct);
    }

    private static BankOperationReceipt Receipt(string prefix)
        => new($"{prefix}-{Guid.NewGuid():N}"[..16], DateTime.UtcNow);

    /// <summary>Number of transient failures to inject before success (0, 1, or 2), seeded deterministically.</summary>
    private static int PlannedTransientFailures(string key, string op, double rate)
    {
        if (Unit(key, op) >= rate) return 0;
        // If this transaction is "unlucky", make it fail 1–2 times then recover.
        return 1 + (int)(Unit(key, op + ":count") * 2); // 1 or 2
    }

    /// <summary>
    /// Number of times a reversal is refused before it settles (0, or 3-5 when unlucky).
    /// Deliberately more than the transient debit/credit counts so the retry loop is
    /// visible in the audit lineage rather than clearing on the first retry.
    /// </summary>
    private static int PlannedReversalRefusals(string key, double rate)
    {
        if (Unit(key, "reversal-refuse") >= rate) return 0;
        return 3 + (int)(Unit(key, "reversal-refuse:count") * 3); // 3, 4, or 5
    }

    /// <summary>Stable value in [0,1) from an FNV-1a hash — reproducible across processes and replays.</summary>
    private static double Unit(string key, string salt)
    {
        const uint offset = 2166136261, prime = 16777619;
        uint h = offset;
        foreach (var c in key + "|" + salt) { h ^= c; h *= prime; }
        return h / (double)uint.MaxValue;
    }
}
