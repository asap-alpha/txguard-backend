using Microsoft.EntityFrameworkCore;
using TxGuard.Domain.Enums;
using TxGuard.Domain.Transactions;
using TxGuard.Infrastructure.Persistence;

namespace TxGuard.Tests;

/// <summary>
/// The queryable read model and its append-only audit trail (SRS §3.5, §3.6). Activities
/// are re-executed after a crash, so every write path here has to be safe to repeat, and
/// every state change has to leave an audit event behind.
/// </summary>
public class ReadModelStoreTests
{
    private static (EfTransactionStore Store, IDbContextFactory<TxGuardDbContext> Db, CountingNotifier Notifier) New()
    {
        var db = TestSupport.NewDbFactory();
        var notifier = new CountingNotifier();
        return (new EfTransactionStore(db, notifier), db, notifier);
    }

    [Fact]
    public async Task Create_persists_Pending_with_a_creation_event()
    {
        var (store, db, _) = New();
        var request = TestSupport.Request("TXG-1");

        await store.RecordCreatedAsync(request);

        await using var ctx = await db.CreateDbContextAsync();
        var tx = await ctx.Transactions.SingleAsync();
        Assert.Equal(TransactionState.Pending, tx.State);
        Assert.Equal(125_00, tx.AmountMinor);

        var evt = await ctx.AuditEvents.SingleAsync();
        Assert.Equal(AuditEventType.TransactionCreated, evt.EventType);
        Assert.Equal(TransactionState.Pending, evt.NewState);
        Assert.Null(evt.PreviousState);
    }

    [Fact]
    public async Task Create_is_idempotent_on_re_execution()
    {
        // FR-REC-001: a worker crash re-runs the activity. The second run must not create a
        // duplicate row or a second creation event.
        var (store, db, _) = New();
        var request = TestSupport.Request("TXG-2");

        await store.RecordCreatedAsync(request);
        await store.RecordCreatedAsync(request);

        await using var ctx = await db.CreateDbContextAsync();
        Assert.Equal(1, await ctx.Transactions.CountAsync());
        Assert.Equal(1, await ctx.AuditEvents.CountAsync());
    }

    [Fact]
    public async Task Fraud_result_is_attached_to_the_audit_log()
    {
        // FR-AI-007: score, tier and model version are all recorded.
        var (store, db, _) = New();
        await store.RecordCreatedAsync(TestSupport.Request("TXG-3"));

        var assessment = FraudAssessment.Classify(
            0.91, "heuristic-v0.1", new Dictionary<string, double> { ["amount_factor"] = 0.5 }, 0.40, 0.80);
        await store.RecordFraudAsync("TXG-3", assessment);

        await using var ctx = await db.CreateDbContextAsync();
        var tx = await ctx.Transactions.SingleAsync();
        Assert.Equal(0.91, tx.RiskScore);
        Assert.Equal(RiskLevel.High, tx.RiskLevel);
        Assert.Equal("heuristic-v0.1", tx.FraudModelVersion);

        var evt = await ctx.AuditEvents.SingleAsync(e => e.EventType == AuditEventType.FraudScored);
        Assert.Contains("heuristic-v0.1", evt.DataJson);
    }

    [Fact]
    public async Task Transition_records_both_the_previous_and_the_new_state()
    {
        var (store, db, _) = New();
        await store.RecordCreatedAsync(TestSupport.Request("TXG-4"));

        await store.TransitionAsync("TXG-4", TransactionState.Debiting, AuditEventType.DebitInitiated);
        await store.TransitionAsync("TXG-4", TransactionState.Crediting, AuditEventType.CreditInitiated);

        await using var ctx = await db.CreateDbContextAsync();
        Assert.Equal(TransactionState.Crediting, (await ctx.Transactions.SingleAsync()).State);

        var credit = await ctx.AuditEvents.SingleAsync(e => e.EventType == AuditEventType.CreditInitiated);
        Assert.Equal(TransactionState.Debiting, credit.PreviousState);
        Assert.Equal(TransactionState.Crediting, credit.NewState);
    }

    [Fact]
    public async Task Failure_reason_is_captured_on_a_failing_transition()
    {
        var (store, db, _) = New();
        await store.RecordCreatedAsync(TestSupport.Request("TXG-5"));

        await store.TransitionAsync("TXG-5", TransactionState.DebitFailed, AuditEventType.DebitFailed,
            failureReason: "Sender account has insufficient balance");

        await using var ctx = await db.CreateDbContextAsync();
        Assert.Equal("Sender account has insufficient balance",
            (await ctx.Transactions.SingleAsync()).FailureReason);
    }

    [Fact]
    public async Task Retry_events_increment_the_visible_retry_counter()
    {
        var (store, db, _) = New();
        await store.RecordCreatedAsync(TestSupport.Request("TXG-6"));

        await store.AppendEventAsync("TXG-6", AuditEventType.RetryScheduled, "Retry attempt 2 for debit");
        await store.AppendEventAsync("TXG-6", AuditEventType.RetryScheduled, "Retry attempt 3 for debit");

        await using var ctx = await db.CreateDbContextAsync();
        Assert.Equal(2, (await ctx.Transactions.SingleAsync()).Retries);
    }

    [Fact]
    public async Task Non_retry_events_leave_the_retry_counter_alone()
    {
        var (store, db, _) = New();
        await store.RecordCreatedAsync(TestSupport.Request("TXG-7"));

        await store.AppendEventAsync("TXG-7", AuditEventType.DebitSucceeded, "Debit confirmed");

        await using var ctx = await db.CreateDbContextAsync();
        Assert.Equal(0, (await ctx.Transactions.SingleAsync()).Retries);
    }

    [Fact]
    public async Task Every_change_notifies_connected_dashboards()
    {
        // The dashboard is push-driven, not polled: each write fans out exactly one update.
        var (store, _, notifier) = New();
        await store.RecordCreatedAsync(TestSupport.Request("TXG-8"));
        await store.TransitionAsync("TXG-8", TransactionState.Debiting, AuditEventType.DebitInitiated);

        Assert.Equal(new[] { "TXG-8", "TXG-8" }, notifier.Notified);
    }

    [Fact]
    public async Task Writes_for_an_unknown_transaction_are_ignored()
    {
        // A transition can arrive for a transaction the read model never saw (e.g. the
        // database was unreachable during creation). Failing here would stall the workflow
        // on a projection problem; the durable event history remains the source of truth.
        var (store, _, _) = New();

        await store.TransitionAsync("TXG-missing", TransactionState.Completed, AuditEventType.TransactionCompleted);
        await store.RecordFraudAsync("TXG-missing",
            FraudAssessment.Classify(0.1, "test", new Dictionary<string, double>(), 0.4, 0.8));
    }

    [Fact]
    public async Task A_full_lifecycle_leaves_an_ordered_audit_trail()
    {
        // FR-AL-001: the trail is the evidence a regulator reads. It must be complete and
        // in order for the whole happy path.
        var (store, db, _) = New();
        await store.RecordCreatedAsync(TestSupport.Request("TXG-9"));
        await store.RecordFraudAsync("TXG-9",
            FraudAssessment.Classify(0.12, "heuristic-v0.1", new Dictionary<string, double>(), 0.40, 0.80));
        await store.TransitionAsync("TXG-9", TransactionState.Debiting, AuditEventType.DebitInitiated);
        await store.AppendEventAsync("TXG-9", AuditEventType.DebitSucceeded);
        await store.TransitionAsync("TXG-9", TransactionState.Crediting, AuditEventType.CreditInitiated);
        await store.AppendEventAsync("TXG-9", AuditEventType.CreditSucceeded);
        await store.TransitionAsync("TXG-9", TransactionState.Completed, AuditEventType.TransactionCompleted);

        await using var ctx = await db.CreateDbContextAsync();
        var trail = await ctx.AuditEvents.OrderBy(e => e.Id).Select(e => e.EventType).ToListAsync();

        Assert.Equal(new[]
        {
            AuditEventType.TransactionCreated,
            AuditEventType.FraudScored,
            AuditEventType.DebitInitiated,
            AuditEventType.DebitSucceeded,
            AuditEventType.CreditInitiated,
            AuditEventType.CreditSucceeded,
            AuditEventType.TransactionCompleted,
        }, trail);
    }
}
