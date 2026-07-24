using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace TxGuard.Infrastructure.Persistence;

/// <summary>
/// Stores an enum as its name (like the default <c>HasConversion&lt;string&gt;()</c>), but on
/// the way back maps any unrecognised string to a caller-supplied fallback instead of
/// throwing. EF's built-in converter fails the entire query when a persisted value is not a
/// defined member — so a single stray row (schema drift, hand-edited data, a value from a
/// newer build) can take down a whole read. This keeps the read resilient: the bad row shows
/// up as the fallback rather than 500-ing the request.
/// </summary>
public sealed class TolerantEnumToStringConverter<TEnum> : ValueConverter<TEnum, string>
    where TEnum : struct, Enum
{
    public TolerantEnumToStringConverter(TEnum fallback)
        : base(
            value => value.ToString(),
            stored => Parse(stored, fallback))
    {
    }

    // A named method rather than an inline lambda: expression trees (which EF compiles the
    // conversion into) can't contain an `out var` declaration.
    private static TEnum Parse(string stored, TEnum fallback) =>
        Enum.TryParse<TEnum>(stored, out var parsed) ? parsed : fallback;
}
