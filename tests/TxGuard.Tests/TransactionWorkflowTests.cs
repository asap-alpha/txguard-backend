using Temporalio.Api.Enums.V1;
using Temporalio.Client;
using Temporalio.Exceptions;
using Temporalio.Testing;
using Temporalio.Worker;
using TxGuard.Domain.Abstractions;
using TxGuard.Domain.Enums;
using TxGuard.Domain.Errors;
using TxGuard.Domain.Transactions;
using TxGuard.Workflows;

namespace TxGuard.Tests;

/// <summary>
/// Exercises the durable transaction workflow with the Temporal time-skipping test
/// environment, so retry backoffs are fast-forwarded. Uses in-memory fakes for the
/// pluggable ports.
/// </summary>
public class TransactionWorkflowTests
{
    private static TransactionRequest Sample(string id = "TXG-test-1") => new(
        id,
        new Party("acc-sender", "Ama Owusu", "0244000000", "MTN MoMo"),
        new Party("acc-recipient", "Kofi Mensah", "0201111111", "GCB Bank"),
        AmountMinor: 125_00, Currency: "GHS", Type: TransactionType.Transfer,
        IdempotencyKey: id, Reference: "test", CallerIp: null, CreatedAtUtc: DateTime.UtcNow);

    private static async Task<TransactionResult> RunAsync(TransactionActivities activities, TransactionRequest req)
    {
        await using var env = await WorkflowEnvironment.StartTimeSkippingAsync();
        using var worker = new TemporalWorker(env.Client,
            new TemporalWorkerOptions(TxGuardConstants.TaskQueue)
                .AddAllActivities(activities)
                .AddWorkflow<TransactionWorkflow>());

        return await worker.ExecuteAsync(async () =>
            await env.Client.ExecuteWorkflowAsync(
                (TransactionWorkflow wf) => wf.RunAsync(req),
                new(id: req.TransactionId, taskQueue: TxGuardConstants.TaskQueue)));
    }

    [Fact]
    public async Task HappyPath_ReachesCompleted()
    {
        var activities = new TransactionActivities(new SucceedingBank(), new LowRiskScorer(), new NullStore());
        var result = await RunAsync(activities, Sample());
        Assert.Equal(TransactionState.Completed, result.FinalState);
    }

    [Fact]
    public async Task TransientDebitFailures_RetryThenComplete()
    {
        // Debit fails twice (transient) then succeeds — must still complete via retries.
        var activities = new TransactionActivities(new FlakyBank(debitFailuresBeforeSuccess: 2), new LowRiskScorer(), new NullStore());
        var result = await RunAsync(activities, Sample());
        Assert.Equal(TransactionState.Completed, result.FinalState);
    }

    [Fact]
    public async Task CreditPermanentlyFails_TriggersSagaReversal()
    {
        // Credit fails permanently → debit is reversed → terminal FAILED (no silent loss).
        var activities = new TransactionActivities(new CreditFailsBank(), new LowRiskScorer(), new NullStore());
        var result = await RunAsync(activities, Sample());
        Assert.Equal(TransactionState.Failed, result.FinalState);
    }

    [Fact]
    public void DeriveTransactionId_IsStablePerKey_AndDistinctAcrossKeys()
    {
        // Same idempotency key → same transaction id (so duplicate submits converge
        // on one workflow); different keys → different ids.
        Assert.Equal(TxGuardConstants.DeriveTransactionId("key-A"), TxGuardConstants.DeriveTransactionId("key-A"));
        Assert.NotEqual(TxGuardConstants.DeriveTransactionId("key-A"), TxGuardConstants.DeriveTransactionId("key-B"));
        Assert.StartsWith(TxGuardConstants.TransactionIdPrefix, TxGuardConstants.DeriveTransactionId("key-A"));
    }

    [Fact]
    public async Task DuplicateSubmit_SameId_StartsExactlyOneWorkflow()
    {
        // Guards the idempotency fix: with a deterministic id + RejectDuplicate, concurrent
        // submissions of the same key resolve to one workflow — Temporal rejects the rest,
        // so no second debit can occur (FR-TI-003; "no duplicate charges").
        var activities = new TransactionActivities(new SucceedingBank(), new LowRiskScorer(), new NullStore());
        await using var env = await WorkflowEnvironment.StartTimeSkippingAsync();
        using var worker = new TemporalWorker(env.Client,
            new TemporalWorkerOptions(TxGuardConstants.TaskQueue)
                .AddAllActivities(activities)
                .AddWorkflow<TransactionWorkflow>());

        await worker.ExecuteAsync(async () =>
        {
            var req = Sample(TxGuardConstants.DeriveTransactionId("dup-key"));
            WorkflowOptions Opts() => new(id: req.TransactionId, taskQueue: TxGuardConstants.TaskQueue)
            {
                IdReusePolicy = WorkflowIdReusePolicy.RejectDuplicate,
            };

            // Fire several concurrent starts for the same id.
            var starts = Enumerable.Range(0, 5).Select(_ => Task.Run(async () =>
            {
                try
                {
                    await env.Client.StartWorkflowAsync((TransactionWorkflow wf) => wf.RunAsync(req), Opts());
                    return true;   // won the race
                }
                catch (WorkflowAlreadyStartedException)
                {
                    return false;  // rejected as duplicate
                }
            }));
            var results = await Task.WhenAll(starts);

            Assert.Equal(1, results.Count(won => won));   // exactly one workflow created

            // And that single workflow still completes normally.
            var handle = env.Client.GetWorkflowHandle<TransactionWorkflow>(req.TransactionId);
            var result = await handle.GetResultAsync<TransactionResult>();
            Assert.Equal(TransactionState.Completed, result.FinalState);
        });
    }

    // ── Fakes ───────────────────────────────────────────────────────────────
    private sealed class LowRiskScorer : IFraudScorer
    {
        public Task<FraudAssessment> ScoreAsync(TransactionRequest r, CancellationToken ct = default) =>
            Task.FromResult(FraudAssessment.Classify(0.10, "test", new Dictionary<string, double>(), 0.40, 0.80));
    }

    private sealed class SucceedingBank : IBankingAdapter
    {
        private static Task<BankOperationReceipt> Ok() =>
            Task.FromResult(new BankOperationReceipt("REF", DateTime.UtcNow));
        public Task<BankOperationReceipt> DebitAsync(Party a, long m, string c, string k, CancellationToken ct = default) => Ok();
        public Task<BankOperationReceipt> CreditAsync(Party a, long m, string c, string k, CancellationToken ct = default) => Ok();
        public Task<BankOperationReceipt> ReverseAsync(Party a, long m, string c, string k, CancellationToken ct = default) => Ok();
    }

    private sealed class FlakyBank : IBankingAdapter
    {
        private readonly int _target;
        private int _debitAttempts;
        public FlakyBank(int debitFailuresBeforeSuccess) => _target = debitFailuresBeforeSuccess;
        public Task<BankOperationReceipt> DebitAsync(Party a, long m, string c, string k, CancellationToken ct = default)
        {
            if (Interlocked.Increment(ref _debitAttempts) <= _target)
                throw new TransientBankingException(TxGuardError.NetworkTimeout);
            return Task.FromResult(new BankOperationReceipt("DBT", DateTime.UtcNow));
        }
        public Task<BankOperationReceipt> CreditAsync(Party a, long m, string c, string k, CancellationToken ct = default) =>
            Task.FromResult(new BankOperationReceipt("CRD", DateTime.UtcNow));
        public Task<BankOperationReceipt> ReverseAsync(Party a, long m, string c, string k, CancellationToken ct = default) =>
            Task.FromResult(new BankOperationReceipt("REV", DateTime.UtcNow));
    }

    private sealed class CreditFailsBank : IBankingAdapter
    {
        public Task<BankOperationReceipt> DebitAsync(Party a, long m, string c, string k, CancellationToken ct = default) =>
            Task.FromResult(new BankOperationReceipt("DBT", DateTime.UtcNow));
        public Task<BankOperationReceipt> CreditAsync(Party a, long m, string c, string k, CancellationToken ct = default) =>
            throw new PermanentBankingException(TxGuardError.AccountNotFound, "recipient unreachable");
        public Task<BankOperationReceipt> ReverseAsync(Party a, long m, string c, string k, CancellationToken ct = default) =>
            Task.FromResult(new BankOperationReceipt("REV", DateTime.UtcNow));
    }

    private sealed class NullStore : ITransactionStore
    {
        public Task RecordCreatedAsync(TransactionRequest r, CancellationToken ct = default) => Task.CompletedTask;
        public Task RecordFraudAsync(string id, FraudAssessment a, CancellationToken ct = default) => Task.CompletedTask;
        public Task TransitionAsync(string id, TransactionState s, AuditEventType e, string? r = null, int? rt = null, object? d = null, CancellationToken ct = default) => Task.CompletedTask;
        public Task AppendEventAsync(string id, AuditEventType t, string? de = null, object? d = null, CancellationToken ct = default) => Task.CompletedTask;
    }
}
