using Temporalio.Client;
using Temporalio.Testing;
using Temporalio.Worker;
using TxGuard.Domain.Enums;
using TxGuard.Domain.Transactions;
using TxGuard.Workflows;

namespace TxGuard.Tests;

/// <summary>
/// Saga compensation and the idempotency-key contract (SRS §3.4, FR-DB-002, FR-CR-002,
/// FR-CP-002). These are the guarantees that make "no silent fund loss, no duplicate
/// charge" true, so they are asserted on the actual calls reaching the banking rail.
/// </summary>
public class SagaCompensationTests
{
    private static async Task<TransactionResult> RunAsync(RecordingBank bank, TransactionRequest req)
    {
        var activities = new TransactionActivities(bank, new FixedScorer(0.10), new NoOpStore());

        await using var env = await WorkflowEnvironment.StartTimeSkippingAsync();
        using var worker = new TemporalWorker(env.Client,
            new TemporalWorkerOptions(TxGuardConstants.TaskQueue)
                .AddAllActivities(activities)
                .AddWorkflow<TransactionWorkflow>());

        return await worker.ExecuteAsync(async () =>
            await env.Client.ExecuteWorkflowAsync(
                (TransactionWorkflow wf) => wf.RunAsync(req),
                new(req.TransactionId, TxGuardConstants.TaskQueue)));
    }

    [Fact]
    public async Task EachLeg_UsesItsOwnDerivedIdempotencyKey()
    {
        // FR-DB-002 / FR-CR-002 / FR-CP-002: debit uses the transaction id, credit and
        // reversal use the '-credit' and '-reversal' suffixes, so a replayed activity can
        // never be mistaken for a different leg by the rail.
        var req = TestSupport.Request("TXG-keys");
        var bank = new RecordingBank { CreditFailsPermanently = true };

        await RunAsync(bank, req);

        Assert.Contains(("debit", "TXG-keys"), bank.Calls);
        Assert.Contains(("credit", "TXG-keys-credit"), bank.Calls);
        Assert.Contains(("reversal", "TXG-keys-reversal"), bank.Calls);
    }

    [Fact]
    public async Task PermanentDebitFailure_IsTerminal_AndNeverCredits()
    {
        // FR-DB-004/005: a permanent debit error is not retried, and because no funds left
        // the sender there is nothing to credit and nothing to compensate.
        var bank = new RecordingBank { DebitFailsPermanently = true };

        var result = await RunAsync(bank, TestSupport.Request("TXG-debit-perm"));

        Assert.Equal(TransactionState.DebitFailed, result.FinalState);
        Assert.Equal(1, bank.CountOf("debit"));      // not retried
        Assert.Equal(0, bank.CountOf("credit"));
        Assert.Equal(0, bank.CountOf("reversal"));
    }

    [Fact]
    public async Task ReversalIsRetriedUntilItSucceeds()
    {
        // FR-CP-006 / RetryPolicySpec.Reversal: the reversal policy has no attempt ceiling,
        // so a rail that refuses repeatedly still ends with the sender made whole. A refusal
        // count above the credit policy's 7 attempts proves the ceiling really is unlimited.
        var bank = new RecordingBank { CreditFailsPermanently = true, ReversalRefusals = 9 };

        var result = await RunAsync(bank, TestSupport.Request("TXG-rev-retry"));

        Assert.Equal(TransactionState.Failed, result.FinalState);
        Assert.Equal(10, bank.CountOf("reversal"));   // 9 refusals + the one that settled
    }

    [Fact]
    public async Task CompletedTransaction_IsNeverCompensated()
    {
        // The happy path must not trigger the saga: exactly one debit, one credit, no reversal.
        var bank = new RecordingBank();

        var result = await RunAsync(bank, TestSupport.Request("TXG-happy"));

        Assert.Equal(TransactionState.Completed, result.FinalState);
        Assert.Equal(0, bank.CountOf("reversal"));
    }
}
