namespace TxGuard.Infrastructure.Configuration;

/// <summary>
/// Externally configurable parameters (NFR-M-003). Bound from the "TxGuard"
/// configuration section. Defaults match the SRS.
/// </summary>
public sealed class TxGuardOptions
{
    public const string SectionName = "TxGuard";

    /// <summary>Fraud risk thresholds (SRS §3.9). score ≥ High → FRAUD_REVIEW; score &lt; Low → auto-approve.</summary>
    public double LowRiskThreshold { get; set; } = 0.40;
    public double HighRiskThreshold { get; set; } = 0.80;

    /// <summary>FAIL_OPEN (default) lets transactions proceed if the fraud service is down (Risk #8).</summary>
    public string FraudFailureMode { get; set; } = "FAIL_OPEN";

    /// <summary>Maximum transaction amount in minor units (GHS 10,000 default). FR-TI-006.</summary>
    public long MaxAmountMinor { get; set; } = 10_000 * 100;

    /// <summary>Idempotency dedup window (FR-TI-003).</summary>
    public TimeSpan IdempotencyWindow { get; set; } = TimeSpan.FromHours(24);

    /// <summary>Behaviour of the mock banking adapter, so we can demo retries/saga.</summary>
    public MockBankingOptions MockBanking { get; set; } = new();
}

/// <summary>
/// Controls injected failures in the mock banking adapter. Deterministic per
/// transaction id + operation so workflow replays stay consistent.
/// </summary>
public sealed class MockBankingOptions
{
    /// <summary>Probability [0..1] a debit hits a transient (retryable) error before succeeding.</summary>
    public double DebitTransientFailureRate { get; set; } = 0.15;

    /// <summary>Probability [0..1] a credit hits a transient (retryable) error before succeeding.</summary>
    public double CreditTransientFailureRate { get; set; } = 0.25;

    /// <summary>Probability [0..1] a credit fails permanently (drives saga compensation).</summary>
    public double CreditPermanentFailureRate { get; set; } = 0.05;

    /// <summary>
    /// Probability [0..1] the compensating reversal ALSO fails permanently — the double
    /// failure that escalates to MANUAL_REVIEW (FR-CP-006). Zero by default: the reversal
    /// is the safety net, so it only misbehaves when a demo explicitly asks it to.
    /// </summary>
    public double ReversalPermanentFailureRate { get; set; } = 0.0;

    /// <summary>Simulated processing latency per operation (ms).</summary>
    public int LatencyMs { get; set; } = 150;
}
