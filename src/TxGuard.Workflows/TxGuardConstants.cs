using System.Security.Cryptography;
using System.Text;
using Temporalio.Common;
using TxGuard.Domain;

namespace TxGuard.Workflows;

public static class TxGuardConstants
{
    /// <summary>Temporal task queue that routes transaction workflow/activity work.</summary>
    public const string TaskQueue = "txguard-transactions";

    /// <summary>Prefix for workflow IDs — the transaction ID (SRS §9.1: TXG-{uuid4}).</summary>
    public const string TransactionIdPrefix = "TXG-";

    /// <summary>
    /// Derives a stable <c>TXG-{guid}</c> transaction id (also the Temporal workflow id)
    /// from an idempotency key. Identical keys always map to the same id, so duplicate
    /// submissions converge on one workflow that Temporal deduplicates (FR-TI-003). MD5
    /// is used only to fold the key into a GUID shape — not for security.
    /// </summary>
    public static string DeriveTransactionId(string idempotencyKey)
    {
        var hash = MD5.HashData(Encoding.UTF8.GetBytes(idempotencyKey));
        return TransactionIdPrefix + new Guid(hash);
    }
}

/// <summary>Maps a domain <see cref="RetryPolicySpec"/> to Temporal activity options.</summary>
public static class ActivityOptionsFactory
{
    // Deliberately no ScheduleToCloseTimeout: it would cap total retry duration and so
    // silently defeat the unlimited reversal policy. StartToClose bounds each attempt.
    public static Temporalio.Workflows.ActivityOptions ForBanking(RetryPolicySpec spec) => new()
    {
        StartToCloseTimeout = TimeSpan.FromSeconds(30),
        RetryPolicy = new RetryPolicy
        {
            InitialInterval = spec.InitialInterval,
            BackoffCoefficient = (float)spec.BackoffCoefficient,
            MaximumAttempts = spec.MaxAttempts, // 0 = unlimited
            MaximumInterval = spec.MaximumInterval,
            NonRetryableErrorTypes = spec.NonRetryableErrorTypes.ToArray(),
        },
    };

    /// <summary>Options for the lightweight read-model/audit activities.</summary>
    public static Temporalio.Workflows.ActivityOptions ForStore() => new()
    {
        StartToCloseTimeout = TimeSpan.FromSeconds(15),
        RetryPolicy = new RetryPolicy { MaximumAttempts = 5, InitialInterval = TimeSpan.FromSeconds(1) },
    };
}
