namespace TxGuard.Infrastructure.Configuration;

/// <summary>
/// Live, mutable copies of the tuning knobs that the demo/chaos panel flips at
/// runtime. Seeded from <see cref="TxGuardOptions"/> (so appsettings.json and the
/// TxGuard__* environment overrides still define the defaults, NFR-M-003), but
/// changeable without a restart so a demo can force fraud review, retries, or saga
/// compensation on demand.
/// </summary>
public interface IRuntimeSettings
{
    double LowRiskThreshold { get; set; }
    double HighRiskThreshold { get; set; }
    long MaxAmountMinor { get; }

    double DebitTransientFailureRate { get; set; }
    double CreditTransientFailureRate { get; set; }
    double CreditPermanentFailureRate { get; set; }
    double ReversalPermanentFailureRate { get; set; }
    int LatencyMs { get; set; }

    /// <summary>Restores every value to what configuration specified at startup.</summary>
    void ResetToConfiguredDefaults();
}

public sealed class RuntimeSettings : IRuntimeSettings
{
    private readonly TxGuardOptions _configured;

    public RuntimeSettings(Microsoft.Extensions.Options.IOptions<TxGuardOptions> options)
    {
        _configured = options.Value;
        ResetToConfiguredDefaults();
    }

    public double LowRiskThreshold { get; set; }
    public double HighRiskThreshold { get; set; }
    public long MaxAmountMinor => _configured.MaxAmountMinor;

    public double DebitTransientFailureRate { get; set; }
    public double CreditTransientFailureRate { get; set; }
    public double CreditPermanentFailureRate { get; set; }
    public double ReversalPermanentFailureRate { get; set; }
    public int LatencyMs { get; set; }

    public void ResetToConfiguredDefaults()
    {
        LowRiskThreshold = _configured.LowRiskThreshold;
        HighRiskThreshold = _configured.HighRiskThreshold;
        DebitTransientFailureRate = _configured.MockBanking.DebitTransientFailureRate;
        CreditTransientFailureRate = _configured.MockBanking.CreditTransientFailureRate;
        CreditPermanentFailureRate = _configured.MockBanking.CreditPermanentFailureRate;
        ReversalPermanentFailureRate = _configured.MockBanking.ReversalPermanentFailureRate;
        LatencyMs = _configured.MockBanking.LatencyMs;
    }
}
