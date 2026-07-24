using TxGuard.Domain.Enums;
using TxGuard.Infrastructure.Persistence;
using Xunit;

namespace TxGuard.Tests;

/// <summary>
/// Guards the read-model against a poisoned audit row: an EventType string that isn't a
/// defined enum member must degrade to <see cref="AuditEventType.Unknown"/> rather than
/// throw, which previously took down the entire transaction-detail request with a 500.
/// </summary>
public class TolerantEnumConverterTests
{
    private static readonly TolerantEnumToStringConverter<AuditEventType> Converter = new(AuditEventType.Unknown);

    [Fact]
    public void Known_value_round_trips()
    {
        var stored = (string)Converter.ConvertToProvider(AuditEventType.FraudApproved)!;
        Assert.Equal("FraudApproved", stored);

        var read = (AuditEventType)Converter.ConvertFromProvider(stored)!;
        Assert.Equal(AuditEventType.FraudApproved, read);
    }

    [Theory]
    [InlineData("ReadModelReconciled")]   // the exact value that caused the incident
    [InlineData("SomeFutureEvent")]
    [InlineData("")]
    public void Unknown_string_maps_to_fallback_instead_of_throwing(string stored)
    {
        var read = (AuditEventType)Converter.ConvertFromProvider(stored)!;
        Assert.Equal(AuditEventType.Unknown, read);
    }
}
