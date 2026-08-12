using Temporalio.Client;
using Temporalio.Testing;
using Temporalio.Worker;
using TxGuard.Domain.Enums;
using TxGuard.Domain.Transactions;
using TxGuard.Workflows;

namespace TxGuard.Tests;

/// <summary>
/// Human-in-the-loop fraud review (SRS §3.9 FR-AI-002/003/004). Verifies that the risk
/// score decides routing, that a HIGH-risk transaction durably holds before any funds
/// move, and that the analyst's signal resolves the hold in both directions.
/// </summary>
public class FraudReviewWorkflowTests
{
    /// <summary>Runs a workflow to completion, optionally delivering a fraud decision at start.</summary>
    private static async Task<(TransactionResult Result, RecordingBank Bank)> RunAsync(
        double riskScore, FraudDecision? decision = null)
    {
        var bank = new RecordingBank();
        var activities = new TransactionActivities(bank, new FixedScorer(riskScore), new NoOpStore());
        var req = TestSupport.Request($"TXG-fraud-{riskScore}-{decision}");

        await using var env = await WorkflowEnvironment.StartTimeSkippingAsync();
        using var worker = new TemporalWorker(env.Client,
            new TemporalWorkerOptions(TxGuardConstants.TaskQueue)
                .AddAllActivities(activities)
                .AddWorkflow<TransactionWorkflow>());

        var result = await worker.ExecuteAsync(async () =>
        {
            var options = new WorkflowOptions(req.TransactionId, TxGuardConstants.TaskQueue);
            if (decision is not null)
            {
                // Signal-with-start: the decision is delivered in the workflow's first task,
                // so the wait condition resolves without the test racing the worker.
                options.StartSignal = "SubmitFraudDecision";
                options.StartSignalArgs = new object?[] { decision.Value };
            }

            var handle = await env.Client.StartWorkflowAsync(
                (TransactionWorkflow wf) => wf.RunAsync(req), options);
            return await handle.GetResultAsync();
        });

        return (result, bank);
    }

    [Fact]
    public async Task LowRisk_IsAutoApproved_AndCompletes()
    {
        // FR-AI-004: below LOW_RISK_THRESHOLD → no review, straight through.
        var (result, bank) = await RunAsync(riskScore: 0.10);

        Assert.Equal(TransactionState.Completed, result.FinalState);
        Assert.Equal(1, bank.CountOf("debit"));
        Assert.Equal(1, bank.CountOf("credit"));
    }

    [Fact]
    public async Task MediumRisk_ProceedsWithoutHumanReview()
    {
        // FR-AI-005: between the thresholds → processed normally (flagged, not held).
        var (result, bank) = await RunAsync(riskScore: 0.55);

        Assert.Equal(TransactionState.Completed, result.FinalState);
        Assert.Equal(1, bank.CountOf("debit"));
    }

    [Fact]
    public async Task HighRisk_ApprovedByAnalyst_Completes()
    {
        // FR-AI-003: held for review, then cleared — processing resumes from the hold.
        var (result, bank) = await RunAsync(riskScore: 0.95, FraudDecision.Approve);

        Assert.Equal(TransactionState.Completed, result.FinalState);
        Assert.Equal(1, bank.CountOf("debit"));
        Assert.Equal(1, bank.CountOf("credit"));
    }

    [Fact]
    public async Task HighRisk_Rejected_MovesNoFunds()
    {
        // FR-AI-002 + TXG-010: fraud scoring runs BEFORE the debit, so a rejected
        // transaction must never touch the banking rail at all.
        var (result, bank) = await RunAsync(riskScore: 0.95, FraudDecision.Reject);

        Assert.Equal(TransactionState.FraudRejected, result.FinalState);
        Assert.Empty(bank.Calls);
    }

    [Fact]
    public async Task HighRisk_WithNoDecision_HoldsInFraudReview()
    {
        // The hold is durable: with no analyst decision the workflow stays parked in
        // FRAUD_REVIEW indefinitely rather than timing out or proceeding on its own.
        var bank = new RecordingBank();
        var activities = new TransactionActivities(bank, new FixedScorer(0.95), new NoOpStore());
        var req = TestSupport.Request("TXG-fraud-hold");

        await using var env = await WorkflowEnvironment.StartTimeSkippingAsync();
        using var worker = new TemporalWorker(env.Client,
            new TemporalWorkerOptions(TxGuardConstants.TaskQueue)
                .AddAllActivities(activities)
                .AddWorkflow<TransactionWorkflow>());

        await worker.ExecuteAsync(async () =>
        {
            var handle = await env.Client.StartWorkflowAsync(
                (TransactionWorkflow wf) => wf.RunAsync(req),
                new WorkflowOptions(req.TransactionId, TxGuardConstants.TaskQueue));

            var state = await WaitForStateAsync(handle, TransactionState.FraudReview);
            Assert.Equal(TransactionState.FraudReview, state);

            // Still parked, and still no funds moved.
            var describe = await handle.DescribeAsync();
            Assert.False(describe.Status == Temporalio.Api.Enums.V1.WorkflowExecutionStatus.Completed);
            Assert.Empty(bank.Calls);

            // Release it so the environment shuts down cleanly.
            await handle.SignalAsync(wf => wf.SubmitFraudDecision(FraudDecision.Reject));
            var result = await handle.GetResultAsync();
            Assert.Equal(TransactionState.FraudRejected, result.FinalState);
        });
    }

    /// <summary>Polls the workflow's State query until it reports <paramref name="expected"/>.</summary>
    private static async Task<TransactionState> WaitForStateAsync(
        WorkflowHandle<TransactionWorkflow> handle, TransactionState expected)
    {
        for (var i = 0; i < 100; i++)
        {
            var state = await handle.QueryAsync(wf => wf.State);
            if (state == expected) return state;
            await Task.Delay(100);
        }
        return await handle.QueryAsync(wf => wf.State);
    }
}
