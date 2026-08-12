using TxGuard.Domain.Enums;
using TxGuard.Domain.Errors;
using TxGuard.Domain.Transactions;
using TxGuard.Infrastructure.Banking;
using TxGuard.Infrastructure.Fraud;

namespace TxGuard.Tests;

/// <summary>
/// The shipped fraud risk engine (SRS §3.9). It must produce a bounded score, an
/// explainable feature vector, and a classification that tracks the configured
/// thresholds — the same contract a trained gradient-boosting model plugged in behind
/// <c>IFraudScorer</c> would have to honour.
/// </summary>
public class HeuristicFraudScorerTests
{
    private static TransactionRequest At(int hourUtc, long amountMinor = 100_00, string recipientProvider = "GCB Bank")
        => TestSupport.Request(amountMinor: amountMinor) with
        {
            CreatedAtUtc = new DateTime(2026, 1, 1, hourUtc, 0, 0, DateTimeKind.Utc),
            Recipient = new Party("acc-recipient", "Kofi Mensah", "0201111111", recipientProvider),
        };

    [Fact]
    public async Task Score_is_always_within_the_zero_to_one_range()
    {
        var scorer = new HeuristicFraudScorer(new StubSettings());

        foreach (var amount in new long[] { 1, 100_00, 1_000_000, 50_000_000 })
        foreach (var hour in new[] { 0, 9, 18, 23 })
        {
            var assessment = await scorer.ScoreAsync(At(hour, amount));
            Assert.InRange(assessment.Score, 0.0, 1.0);
        }
    }

    [Fact]
    public async Task Larger_amounts_score_higher_than_smaller_ones()
    {
        var scorer = new HeuristicFraudScorer(new StubSettings());

        var small = await scorer.ScoreAsync(At(9, amountMinor: 10_00));
        var large = await scorer.ScoreAsync(At(9, amountMinor: 900_000));

        Assert.True(large.Score > small.Score,
            $"expected the larger amount to score higher, got {large.Score} vs {small.Score}");
    }

    [Fact]
    public async Task Peak_fraud_window_scores_higher_than_a_quiet_hour()
    {
        // SRS §2.2 identifies 17:00–21:00 as the elevated-risk window.
        var scorer = new HeuristicFraudScorer(new StubSettings());

        var quiet = await scorer.ScoreAsync(At(9));
        var peak = await scorer.ScoreAsync(At(19));

        Assert.True(peak.Score > quiet.Score);
        Assert.Equal(0.6, peak.Features["time_of_day_risk"]);
        Assert.Equal(0.2, quiet.Features["time_of_day_risk"]);
    }

    [Fact]
    public async Task Cross_provider_transfers_carry_more_risk()
    {
        var scorer = new HeuristicFraudScorer(new StubSettings());

        var samePro = await scorer.ScoreAsync(At(9, recipientProvider: "MTN MoMo"));   // sender is MTN MoMo
        var crossPro = await scorer.ScoreAsync(At(9, recipientProvider: "GCB Bank"));

        Assert.Equal(0.1, samePro.Features["provider_risk"]);
        Assert.Equal(0.3, crossPro.Features["provider_risk"]);
    }

    [Fact]
    public async Task Publishes_every_feature_that_fed_the_score()
    {
        // FR-AI-007: the decision has to be explainable, not just a number.
        var assessment = await new HeuristicFraudScorer(new StubSettings()).ScoreAsync(At(9));

        Assert.Equal(HeuristicFraudScorer.ModelVersion, assessment.ModelVersion);
        Assert.Equal(
            new[] { "amount_factor", "behavioural_score", "provider_risk", "time_of_day_risk" },
            assessment.Features.Keys.OrderBy(k => k).ToArray());
    }

    [Fact]
    public async Task Scoring_twice_gives_the_same_answer()
    {
        // Determinism matters: activities are replayed, and an audit trail that disagrees
        // with itself on re-execution is not an audit trail.
        var scorer = new HeuristicFraudScorer(new StubSettings());
        var request = At(19, 500_00);

        var first = await scorer.ScoreAsync(request);
        var second = await scorer.ScoreAsync(request);

        Assert.Equal(first.Score, second.Score);
        Assert.Equal(first.Level, second.Level);
    }

    [Fact]
    public async Task Lowering_the_high_threshold_forces_review()
    {
        // NFR-M-003: thresholds are configuration, and the classification follows them
        // without any code change. This is what the demo panel manipulates.
        var settings = new StubSettings { LowRiskThreshold = 0.40, HighRiskThreshold = 0.80 };
        var scorer = new HeuristicFraudScorer(settings);
        var request = At(19, 500_00);

        var before = await scorer.ScoreAsync(request);
        settings.HighRiskThreshold = 0.01;
        var after = await scorer.ScoreAsync(request);

        Assert.NotEqual(RiskLevel.High, before.Level);
        Assert.Equal(RiskLevel.High, after.Level);
        Assert.Equal(before.Score, after.Score);   // the score itself is unchanged
    }
}

/// <summary>
/// The mock payment rail (SRS §1.3). Its failure injection is what makes retries and
/// saga compensation demonstrable, so its behaviour has to be deterministic and its
/// error classification correct.
/// </summary>
public class MockBankingAdapterTests
{
    private static readonly Party Account = new("acc", "Ama Owusu", "0244000000", "MTN MoMo");

    [Fact]
    public async Task Succeeds_immediately_when_no_failures_are_configured()
    {
        var adapter = new MockBankingAdapter(new StubSettings());

        var receipt = await adapter.DebitAsync(Account, 100_00, "GHS", "TXG-a");

        Assert.StartsWith("DBT", receipt.ProviderReference);
    }

    [Fact]
    public async Task Permanent_credit_failure_is_not_retryable()
    {
        // Rate 1.0 forces the permanent branch for every transaction.
        var adapter = new MockBankingAdapter(new StubSettings { CreditPermanentFailureRate = 1.0 });

        var ex = await Assert.ThrowsAsync<PermanentBankingException>(
            () => adapter.CreditAsync(Account, 100_00, "GHS", "TXG-b-credit"));

        Assert.Equal(TxGuardError.AccountNotFound.Code, ex.Error.Code);
    }

    [Fact]
    public async Task Transient_debit_failures_clear_after_retries()
    {
        var adapter = new MockBankingAdapter(new StubSettings { DebitTransientFailureRate = 1.0 });
        const string key = "TXG-c";

        var failures = 0;
        for (var attempt = 0; attempt < 5; attempt++)
        {
            try
            {
                await adapter.DebitAsync(Account, 100_00, "GHS", key);
                break;
            }
            catch (TransientBankingException)
            {
                failures++;
            }
        }

        Assert.InRange(failures, 1, 2);   // the adapter injects 1–2 transient failures, then settles
    }

    [Fact]
    public async Task Reversal_failures_are_always_transient()
    {
        // A permanent reversal failure would strand the sender's funds forever under the
        // unlimited retry policy, so the rail must never produce one.
        var adapter = new MockBankingAdapter(new StubSettings { ReversalPermanentFailureRate = 1.0 });

        await Assert.ThrowsAsync<TransientBankingException>(
            () => adapter.ReverseAsync(Account, 100_00, "GHS", "TXG-d-reversal"));
    }

    [Fact]
    public async Task Failure_injection_is_deterministic_per_idempotency_key()
    {
        // Two fresh adapters must agree on whether a given transaction is "unlucky",
        // otherwise a workflow replay could diverge from its recorded history.
        var settings = new StubSettings { CreditPermanentFailureRate = 0.5 };

        var outcomes = new List<bool>();
        for (var run = 0; run < 2; run++)
        {
            var adapter = new MockBankingAdapter(settings);
            try
            {
                await adapter.CreditAsync(Account, 100_00, "GHS", "TXG-stable-credit");
                outcomes.Add(true);
            }
            catch (PermanentBankingException)
            {
                outcomes.Add(false);
            }
        }

        Assert.Equal(outcomes[0], outcomes[1]);
    }
}
