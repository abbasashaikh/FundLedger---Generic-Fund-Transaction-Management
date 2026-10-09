namespace FundLedger.Api.Hosting;

/// <summary>
/// Binds an enum from a query string in the same wire form used in JSON (<c>MONEY_IN</c>, <c>EXPENSE</c>,
/// any case). ASP.NET's default binder only accepts the exact C# member name (<c>MoneyIn</c>), which would
/// make filters like <c>?type=EXPENSE</c> fail. Minimal APIs bind types that expose a static
/// <c>TryParse(string, out T)</c>, so this wrapper is used as <c>EnumQuery&lt;TxnType&gt;?</c> parameters.
/// </summary>
public readonly record struct EnumQuery<T> where T : struct, Enum
{
    public T Value { get; init; }

    // Minimal APIs discover this exact static method by convention, so CA1000 is intentionally suppressed.
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1000:Do not declare static members on generic types", Justification = "Required by minimal-API TryParse binding.")]
    public static bool TryParse(string? text, out EnumQuery<T> result)
    {
        if (!string.IsNullOrWhiteSpace(text) &&
            Enum.TryParse<T>(text.Replace("_", string.Empty, StringComparison.Ordinal), ignoreCase: true, out var value) &&
            Enum.IsDefined(value))
        {
            result = new EnumQuery<T> { Value = value };
            return true;
        }

        result = default;
        return false;
    }
}

internal static class EnumQueryExtensions
{
    public static T? Unwrap<T>(this EnumQuery<T>? query) where T : struct, Enum => query?.Value;
}
