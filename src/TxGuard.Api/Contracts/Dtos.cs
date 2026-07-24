using System.ComponentModel.DataAnnotations;
using TxGuard.Domain.Enums;

namespace TxGuard.Api.Contracts;

/// <summary>Party payload on a submission.</summary>
public sealed record PartyDto(
    [Required] string AccountId,
    [Required] string Name,
    [Required] string AccountNumber,
    [Required] string Provider);

/// <summary>POST /api/v1/transactions body. Amount is in minor units (pesewas) — SRS §2.6.</summary>
public sealed record SubmitTransactionRequest(
    [Required] PartyDto Sender,
    [Required] PartyDto Recipient,
    [Range(1, long.MaxValue)] long AmountMinor,
    string? Currency,
    TransactionType Type,
    string? Reference,
    string? IdempotencyKey);

/// <summary>Response to a submission (FR-TI-002/005).</summary>
public sealed record SubmitTransactionResponse(
    string TransactionId, TransactionState Status, string Message);

/// <summary>Row in the transactions list / detail (mirrors the demo table).</summary>
public sealed record TransactionDto(
    string TransactionId,
    string SenderName, string SenderNumber, string SenderProvider,
    string RecipientName, string RecipientNumber, string RecipientProvider,
    long AmountMinor, string Currency, string Type,
    string State, string? FailureReason, int Retries,
    double? RiskScore, string? RiskLevel, string? FraudModelVersion,
    DateTime CreatedAtUtc, DateTime UpdatedAtUtc,
    string? Reference);

/// <summary>Audit event row (SRS §3.5 / demo Audit Log).</summary>
public sealed record AuditEventDto(
    long Id, string TransactionId, string EventType,
    string? PreviousState, string? NewState,
    string? Details, string? DataJson, DateTime TimestampUtc);

/// <summary>Full transaction detail with its event history.</summary>
public sealed record TransactionDetailDto(TransactionDto Transaction, IReadOnlyList<AuditEventDto> Events);

/// <summary>Analyst decision for a FRAUD_REVIEW transaction (FR-AI-003).</summary>
public sealed record FraudDecisionRequest([Required] string Decision); // "Approve" | "Reject"

/// <summary>
/// POST /api/v1/transactions/{id}/refund body. Both fields optional: without an
/// idempotency key one is derived from the original id, which makes a repeated
/// refund of the same transaction a safe no-op (409).
/// </summary>
public sealed record RefundRequest(string? Reason, string? IdempotencyKey);

/// <summary>Result of requesting a refund — a NEW transaction that returns the funds.</summary>
public sealed record RefundResponse(
    string TransactionId, string OriginalTransactionId,
    TransactionState Status, long AmountMinor, string Message);

/// <summary>Paged list envelope.</summary>
public sealed record PagedResult<T>(IReadOnlyList<T> Items, int Page, int PageSize, int Total);

/// <summary>Overview dashboard metrics (SRS §3.8 / demo Overview).</summary>
public sealed record OverviewDto(
    int InFlight, int FraudQueue, int CompletedToday, int FailedOrEscalated,
    double SuccessRate, int Total, IReadOnlyDictionary<string, int> StateBreakdown);

/// <summary>Standardised API error (FR-SQ-005).</summary>
public sealed record ApiError(string Code, string Message, string? TransactionId = null);

// ── Auth ────────────────────────────────────────────────────────────────────

/// <summary>POST /api/v1/auth/login body.</summary>
public sealed record LoginRequest(
    [Required] string Username,
    [Required] string Password);

/// <summary>Successful login — the bearer token plus who/what it grants.</summary>
public sealed record LoginResponse(
    string Token, string Username, string DisplayName, string Role, DateTime ExpiresAtUtc);

/// <summary>GET /api/v1/auth/me — the caller's identity as read from their token.</summary>
public sealed record MeResponse(string Username, string DisplayName, string Role);

// ── Admin: API keys ─────────────────────────────────────────────────────────

/// <summary>POST /api/v1/admin/api-keys — issue a key for a partner/integrator.</summary>
public sealed record CreateApiKeyRequest([Required] string Name);

/// <summary>Key metadata for listings (never the secret).</summary>
public sealed record ApiKeyDto(
    long Id, string Name, string Prefix, string Role, string CreatedBy,
    DateTime CreatedAtUtc, DateTime? LastUsedAtUtc, DateTime? RevokedAtUtc, bool Active);

/// <summary>Creation response — the FULL key is shown here once and never again.</summary>
public sealed record CreateApiKeyResponse(ApiKeyDto Key, string FullKey, string Message);

// ── Demo / chaos panel (Development only) ───────────────────────────────────

/// <summary>Current state of every demo control.</summary>
public sealed record DemoStatusDto(
    double LowRiskThreshold, double HighRiskThreshold,
    double DebitTransientFailureRate, double CreditTransientFailureRate,
    double CreditPermanentFailureRate, double ReversalPermanentFailureRate, int LatencyMs,
    bool DbBroken, bool WorkerRunning);

/// <summary>Fraud thresholds — lower HighRiskThreshold to force FRAUD_REVIEW.</summary>
public sealed record FraudThresholdsRequest(double LowRiskThreshold, double HighRiskThreshold);

/// <summary>Mock banking failure rates — raise these to force retries / saga reversal.</summary>
public sealed record BankingRatesRequest(
    double DebitTransientFailureRate, double CreditTransientFailureRate,
    double CreditPermanentFailureRate, double ReversalPermanentFailureRate, int LatencyMs);
