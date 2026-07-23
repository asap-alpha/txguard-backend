using TxGuard.Domain.Transactions;

namespace TxGuard.Domain.Abstractions;

/// <summary>
/// Port to the fraud detection model (SRS §3.9). The default implementation is a
/// deterministic heuristic scorer; it can be swapped for a real XGBoost/LightGBM
/// microservice (a Python FastAPI service) without changing workflow code.
/// </summary>
public interface IFraudScorer
{
    /// <summary>
    /// Computes a real-time risk score (0.00–1.00) and classification for a
    /// transaction before any funds move (FR-AI-001/002).
    /// </summary>
    Task<FraudAssessment> ScoreAsync(TransactionRequest request, CancellationToken ct = default);
}
