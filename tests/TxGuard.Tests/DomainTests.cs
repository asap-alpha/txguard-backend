using TxGuard.Domain;
using TxGuard.Domain.Enums;
using TxGuard.Domain.Errors;
using TxGuard.Domain.Transactions;

namespace TxGuard.Tests;

/// <summary>
/// Domain invariants: minor-unit money (SRS §2.6), the retry schedules (§6.2), terminal
/// states (§6.1), and the error registry (§9.2).
/// </summary>
public class MoneyTests
{
    [Fact]
    public void Stores_amounts_as_integer_minor_units()
    {
        var money = Money.FromMajor(1250.00m);
        Assert.Equal(125_000, money.MinorUnits);
        Assert.Equal(1250.00m, money.ToMajor());
    }

    [Theory]
    [InlineData(0.01, 1)]
    [InlineData(9.99, 999)]
    [InlineData(10_000, 1_000_000)]
    public void Converts_major_to_minor_without_drift(double major, long expectedMinor)
    {
        Assert.Equal(expectedMinor, Money.FromMajor((decimal)major).MinorUnits);
    }

    [Fact]
    public void Formats_cedis_with_symbol_and_two_decimals()
    {
        Assert.Equal("GH₵1,250.00", new Money(125_000).ToString());
        Assert.Equal("USD 12.50", new Money(1250, "USD").ToString());
    }

    [Fact]
    public void Defaults_and_normalises_currency()
    {
        Assert.Equal("GHS", new Money(100).Currency);
        Assert.Equal("GHS", new Money(100, "  ").Currency);
        Assert.Equal("USD", new Money(100, "usd").Currency);
    }

    [Fact]
    public void Zero_and_negative_amounts_are_not_positive()
    {
        Assert.False(new Money(0).IsPositive);
        Assert.False(new Money(-1).IsPositive);
        Assert.True(new Money(1).IsPositive);
    }
}

public class RetryPolicySpecTests
{
    [Fact]
    public void Debit_matches_the_SRS_schedule()
    {
        Assert.Equal(TimeSpan.FromSeconds(1), RetryPolicySpec.Debit.InitialInterval);
        Assert.Equal(2.0, RetryPolicySpec.Debit.BackoffCoefficient);
        Assert.Equal(5, RetryPolicySpec.Debit.MaxAttempts);
    }

    [Fact]
    public void Credit_matches_the_SRS_schedule()
    {
        Assert.Equal(TimeSpan.FromSeconds(2), RetryPolicySpec.Credit.InitialInterval);
        Assert.Equal(2.0, RetryPolicySpec.Credit.BackoffCoefficient);
        Assert.Equal(7, RetryPolicySpec.Credit.MaxAttempts);
    }

    [Fact]
    public void Reversal_retries_forever_with_a_capped_interval()
    {
        // 0 attempts == unlimited. The sender must always get their money back, so the
        // compensating leg is the one policy that is not allowed to give up.
        Assert.Equal(0, RetryPolicySpec.Reversal.MaxAttempts);
        Assert.Empty(RetryPolicySpec.Reversal.NonRetryableErrorTypes);
        Assert.Equal(TimeSpan.FromMinutes(1), RetryPolicySpec.Reversal.MaximumInterval);
    }

    [Fact]
    public void Debit_and_credit_never_retry_permanent_errors()
    {
        Assert.Contains(nameof(PermanentBankingException), RetryPolicySpec.Debit.NonRetryableErrorTypes);
        Assert.Contains(nameof(PermanentBankingException), RetryPolicySpec.Credit.NonRetryableErrorTypes);
    }
}

public class TransactionStateTests
{
    [Theory]
    [InlineData(TransactionState.Completed)]
    [InlineData(TransactionState.Failed)]
    [InlineData(TransactionState.DebitFailed)]
    [InlineData(TransactionState.FraudRejected)]
    [InlineData(TransactionState.ManualReview)]
    public void Terminal_states_are_terminal(TransactionState state) => Assert.True(state.IsTerminal());

    [Theory]
    [InlineData(TransactionState.Pending)]
    [InlineData(TransactionState.FraudReview)]
    [InlineData(TransactionState.Debiting)]
    [InlineData(TransactionState.Crediting)]
    [InlineData(TransactionState.CreditFailed)]
    [InlineData(TransactionState.Reversing)]
    public void In_flight_states_are_not_terminal(TransactionState state) => Assert.False(state.IsTerminal());
}

public class FraudAssessmentTests
{
    [Theory]
    [InlineData(0.00, RiskLevel.Low)]
    [InlineData(0.39, RiskLevel.Low)]
    [InlineData(0.40, RiskLevel.Medium)]   // boundary: LOW threshold is inclusive of Medium
    [InlineData(0.79, RiskLevel.Medium)]
    [InlineData(0.80, RiskLevel.High)]     // boundary: HIGH threshold routes to review
    [InlineData(1.00, RiskLevel.High)]
    public void Classifies_against_the_configured_thresholds(double score, RiskLevel expected)
    {
        var assessment = FraudAssessment.Classify(
            score, "test", new Dictionary<string, double>(), lowThreshold: 0.40, highThreshold: 0.80);

        Assert.Equal(expected, assessment.Level);
        Assert.Equal(score, assessment.Score);
    }

    [Fact]
    public void Retains_the_model_version_and_features_for_the_audit_log()
    {
        // FR-AI-007: the score alone is not auditable — the model version and the feature
        // values that produced it have to travel with it.
        var features = new Dictionary<string, double> { ["amount_factor"] = 0.25 };
        var assessment = FraudAssessment.Classify(0.25, "heuristic-v0.1", features, 0.40, 0.80);

        Assert.Equal("heuristic-v0.1", assessment.ModelVersion);
        Assert.Equal(0.25, assessment.Features["amount_factor"]);
    }
}

public class ErrorRegistryTests
{
    [Fact]
    public void Codes_are_unique_and_follow_the_TXG_format()
    {
        var errors = typeof(TxGuardError)
            .GetFields(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static)
            .Where(f => f.FieldType == typeof(TxGuardError))
            .Select(f => (TxGuardError)f.GetValue(null)!)
            .ToList();

        Assert.Equal(10, errors.Count);                                  // SRS §9.2 registry
        Assert.Equal(errors.Count, errors.Select(e => e.Code).Distinct().Count());
        Assert.All(errors, e => Assert.Matches(@"^TXG-\d{3}$", e.Code));
        Assert.All(errors, e => Assert.InRange(e.HttpStatus, 200, 599));
    }

    [Fact]
    public void Banking_exceptions_carry_their_canonical_error()
    {
        var permanent = new PermanentBankingException(TxGuardError.InsufficientFunds);
        var transient = new TransientBankingException(TxGuardError.NetworkTimeout);

        Assert.Equal("TXG-001", permanent.Error.Code);
        Assert.Equal(TxGuardError.InsufficientFunds.Description, permanent.Message);
        Assert.Equal("TXG-004", transient.Error.Code);
    }
}

public class TransactionIdTests
{
    [Fact]
    public void Ids_use_the_TXG_uuid_format()
    {
        var id = Workflows.TxGuardConstants.DeriveTransactionId("some-key");
        Assert.Matches(@"^TXG-[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$", id);
    }
}
