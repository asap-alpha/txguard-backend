using System.Globalization;

namespace TxGuard.Domain;

/// <summary>
/// Monetary amount stored as integer minor units (pesewas) to avoid floating-point
/// errors. SRS §2.6: "All monetary values must be stored as integer minor units".
/// 1 GHS = 100 pesewas.
/// </summary>
public readonly record struct Money
{
    public const int MinorUnitsPerMajor = 100;
    public const string DefaultCurrency = "GHS";

    /// <summary>Amount in minor units (pesewas). Always non-negative for valid transactions.</summary>
    public long MinorUnits { get; }

    public string Currency { get; }

    public Money(long minorUnits, string currency = DefaultCurrency)
    {
        MinorUnits = minorUnits;
        Currency = string.IsNullOrWhiteSpace(currency) ? DefaultCurrency : currency.ToUpperInvariant();
    }

    public static Money FromMajor(decimal major, string currency = DefaultCurrency)
        => new((long)Math.Round(major * MinorUnitsPerMajor, MidpointRounding.ToEven), currency);

    public decimal ToMajor() => MinorUnits / (decimal)MinorUnitsPerMajor;

    public bool IsPositive => MinorUnits > 0;

    /// <summary>Formats as e.g. "GH₵1,250.00".</summary>
    public override string ToString()
    {
        var symbol = Currency == DefaultCurrency ? "GH₵" : Currency + " ";
        return symbol + ToMajor().ToString("N2", CultureInfo.InvariantCulture);
    }
}
