using TxGuard.Domain.Enums;

namespace TxGuard.Domain.Transactions;

/// <summary>
/// Output of the fraud scoring activity (SRS §3.9). The score is 0.00–1.00; the
/// level is derived from the configured thresholds. Feature values are logged to
/// the audit trail (FR-AI-007) and shown in the Fraud Review screen.
/// </summary>
public sealed record FraudAssessment(
    double Score,
    RiskLevel Level,
    string ModelVersion,
    IReadOnlyDictionary<string, double> Features)
{
    public static FraudAssessment Classify(
        double score, string modelVersion, IReadOnlyDictionary<string, double> features,
        double lowThreshold, double highThreshold)
    {
        var level = score >= highThreshold ? RiskLevel.High
                  : score >= lowThreshold ? RiskLevel.Medium
                  : RiskLevel.Low;
        return new FraudAssessment(score, level, modelVersion, features);
    }
}
