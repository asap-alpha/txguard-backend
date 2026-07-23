using TxGuard.Domain.Abstractions;
using TxGuard.Domain.Transactions;
using TxGuard.Infrastructure.Configuration;

namespace TxGuard.Infrastructure.Fraud;

/// <summary>
/// Default rule-based fraud scorer (SRS §3.9). Produces the same 0.00–1.00 risk
/// score, LOW/MEDIUM/HIGH classification, and per-feature values that a real
/// XGBoost/LightGBM model would — using transparent heuristics over amount,
/// velocity, time-of-day, and recipient signals. Swap this out for a Python
/// FastAPI ML microservice by re-binding <see cref="IFraudScorer"/> in DI; nothing
/// else changes (FR-AI-001, NFR-M-001).
/// </summary>
public sealed class HeuristicFraudScorer : IFraudScorer
{
    public const string ModelVersion = "heuristic-v0.1";
    private readonly IRuntimeSettings _opt;

    public HeuristicFraudScorer(IRuntimeSettings settings) => _opt = settings;

    public Task<FraudAssessment> ScoreAsync(TransactionRequest r, CancellationToken ct = default)
    {
        // Feature 1: amount relative to the configured max (bigger → riskier).
        double amountFactor = Math.Clamp(r.AmountMinor / (double)_opt.MaxAmountMinor, 0, 1);

        // Feature 2: time-of-day risk — peak fraud window 17:00–21:00 (SRS §2.2).
        int hour = r.CreatedAtUtc.Hour;
        double timeRisk = hour is >= 17 and <= 21 ? 0.6 : 0.2;

        // Feature 3: cross-provider transfers carry slightly more risk.
        double providerRisk = r.Sender.Provider == r.Recipient.Provider ? 0.1 : 0.3;

        // Feature 4: deterministic "behavioural" spread seeded from the accounts,
        // standing in for velocity / recipient-age signals until the feature store exists.
        double behavioural = Unit($"{r.Sender.AccountId}->{r.Recipient.AccountId}");

        double score = Math.Clamp(
            0.35 * amountFactor + 0.20 * timeRisk + 0.15 * providerRisk + 0.30 * behavioural,
            0, 1);

        var features = new Dictionary<string, double>
        {
            ["amount_factor"] = Math.Round(amountFactor, 4),
            ["time_of_day_risk"] = timeRisk,
            ["provider_risk"] = providerRisk,
            ["behavioural_score"] = Math.Round(behavioural, 4),
        };

        var assessment = FraudAssessment.Classify(
            Math.Round(score, 4), ModelVersion, features,
            _opt.LowRiskThreshold, _opt.HighRiskThreshold);

        return Task.FromResult(assessment);
    }

    private static double Unit(string key)
    {
        const uint offset = 2166136261, prime = 16777619;
        uint h = offset;
        foreach (var c in key) { h ^= c; h *= prime; }
        return h / (double)uint.MaxValue;
    }
}
