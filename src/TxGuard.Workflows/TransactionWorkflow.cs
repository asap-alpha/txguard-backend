using Temporalio.Exceptions;
using Temporalio.Workflows;
using TxGuard.Domain;
using TxGuard.Domain.Enums;
using TxGuard.Domain.Transactions;

namespace TxGuard.Workflows;

/// <summary>
/// The durable transaction lifecycle (SRS §6.1). Runs the deterministic state
/// machine: fraud-score → (optional human review) → debit → credit → complete,
/// with exponential-backoff retries per §6.2 and saga compensation (auto-reversal)
/// when credit permanently fails. Temporal persists every step to event history,
/// so the workflow survives worker crashes and resumes exactly where it left off
/// (FR-REC-001, FR-CR-004/005).
/// </summary>
[Workflow]
public class TransactionWorkflow
{
    private FraudDecision? _fraudDecision;

    /// <summary>Analyst decision delivered while the workflow waits in FRAUD_REVIEW (FR-AI-003).</summary>
    [WorkflowSignal]
    public Task SubmitFraudDecision(FraudDecision decision)
    {
        _fraudDecision = decision;
        return Task.CompletedTask;
    }

    /// <summary>Current lifecycle state, queryable without loading the read model.</summary>
    [WorkflowQuery]
    public TransactionState State { get; private set; } = TransactionState.Pending;

    [WorkflowRun]
    public async Task<TransactionResult> RunAsync(TransactionRequest req)
    {
        var store = ActivityOptionsFactory.ForStore();

        // 1. Durably persist the transaction (PENDING) before anything moves.
        await Workflow.ExecuteActivityAsync((TransactionActivities a) => a.RecordCreated(req), store);

        // 2. Fraud scoring is the FIRST activity — no funds move before assessment (FR-AI-002).
        var fraud = await Workflow.ExecuteActivityAsync(
            (TransactionActivities a) => a.ScoreFraud(req), store);

        // 3. High risk → hold in FRAUD_REVIEW and durably wait for a human decision.
        if (fraud.Level == RiskLevel.High)
        {
            await SetState(req, TransactionState.FraudReview, AuditEventType.FraudReviewQueued,
                $"Risk {fraud.Score:0.00} ≥ high threshold — awaiting analyst");

            await Workflow.WaitConditionAsync(() => _fraudDecision is not null);

            if (_fraudDecision == FraudDecision.Reject)
            {
                await SetState(req, TransactionState.FraudRejected, AuditEventType.FraudRejected,
                    "Denied by analyst during fraud review");
                return Result(TransactionState.FraudRejected, "Rejected in fraud review");
            }

            await Append(req, AuditEventType.FraudApproved, "Cleared by analyst");
        }

        // 4. DEBIT — retry with exponential backoff (1s ×2, max 5). Permanent errors don't retry.
        await SetState(req, TransactionState.Debiting, AuditEventType.DebitInitiated);
        try
        {
            await Workflow.ExecuteActivityAsync((TransactionActivities a) => a.Debit(req),
                ActivityOptionsFactory.ForBanking(RetryPolicySpec.Debit));
            await Append(req, AuditEventType.DebitSucceeded, "Debit confirmed");
        }
        catch (ActivityFailureException ex)
        {
            var reason = Reason(ex);
            await SetState(req, TransactionState.DebitFailed, AuditEventType.DebitFailed, reason);
            return Result(TransactionState.DebitFailed, reason);
        }

        // 5. CREDIT — retry with exponential backoff (2s ×2, max 7). On permanent failure → compensate.
        await SetState(req, TransactionState.Crediting, AuditEventType.CreditInitiated);
        try
        {
            await Workflow.ExecuteActivityAsync((TransactionActivities a) => a.Credit(req),
                ActivityOptionsFactory.ForBanking(RetryPolicySpec.Credit));
            await Append(req, AuditEventType.CreditSucceeded, "Credit confirmed");
            await SetState(req, TransactionState.Completed, AuditEventType.TransactionCompleted);
            return Result(TransactionState.Completed, null);
        }
        catch (ActivityFailureException ex)
        {
            var reason = Reason(ex);
            await SetState(req, TransactionState.CreditFailed, AuditEventType.CreditFailed, reason);
            return await CompensateAsync(req, reason);
        }
    }

    /// <summary>
    /// Saga rollback: return the debited funds to the sender (FR-CP). This cannot fail —
    /// <see cref="RetryPolicySpec.Reversal"/> retries forever, so the workflow stays in
    /// REVERSING for as long as the rail is refusing and only leaves once the money is
    /// actually back. There is deliberately no catch and no escalation: a failed
    /// transaction is never a human's problem, only a fraud hold is.
    /// </summary>
    private async Task<TransactionResult> CompensateAsync(TransactionRequest req, string creditReason)
    {
        await SetState(req, TransactionState.Reversing, AuditEventType.ReversalInitiated,
            "Credit failed — reversing debit");

        await Workflow.ExecuteActivityAsync((TransactionActivities a) => a.Reverse(req),
            ActivityOptionsFactory.ForBanking(RetryPolicySpec.Reversal));

        await SetState(req, TransactionState.Failed, AuditEventType.DebitReversed,
            "Debit reversed after credit failure");
        return Result(TransactionState.Failed, creditReason);
    }

    private async Task SetState(TransactionRequest req, TransactionState state,
        AuditEventType eventType, string? reason = null)
    {
        State = state;
        await Workflow.ExecuteActivityAsync(
            (TransactionActivities a) => a.Transition(new TransitionInput(req.TransactionId, state, eventType, reason)),
            ActivityOptionsFactory.ForStore());
    }

    private Task Append(TransactionRequest req, AuditEventType type, string? details) =>
        Workflow.ExecuteActivityAsync(
            (TransactionActivities a) => a.AppendEvent(new AppendEventInput(req.TransactionId, type, details)),
            ActivityOptionsFactory.ForStore());

    private TransactionResult Result(TransactionState state, string? reason) =>
        new(Workflow.Info.WorkflowId, state, reason);

    private static string Reason(ActivityFailureException ex) =>
        (ex.InnerException as ApplicationFailureException)?.Message ?? ex.InnerException?.Message ?? ex.Message;
}
