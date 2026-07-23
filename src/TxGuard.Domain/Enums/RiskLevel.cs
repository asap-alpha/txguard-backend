namespace TxGuard.Domain.Enums;

/// <summary>
/// Fraud risk classification derived from the ML risk score (0.00–1.00) against
/// the LOW_RISK_THRESHOLD (default 0.40) and HIGH_RISK_THRESHOLD (default 0.80).
/// SRS §3.9 FR-AI-003/004/005.
/// </summary>
public enum RiskLevel
{
    /// <summary>score &lt; LOW threshold — auto-approved.</summary>
    Low,

    /// <summary>LOW ≤ score &lt; HIGH — proceeds but flagged for post-transaction review.</summary>
    Medium,

    /// <summary>score ≥ HIGH threshold — held in FRAUD_REVIEW for a human decision.</summary>
    High
}
