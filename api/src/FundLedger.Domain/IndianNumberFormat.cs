using System.Globalization;
using System.Text;

namespace FundLedger.Domain;

/// <summary>
/// Indian digit grouping (1,25,000.00) used on receipts and PDF exports
/// (Design Brief §4). Implemented by hand so output never depends on the
/// server's installed ICU data.
/// </summary>
public static class IndianNumberFormat
{
    public static string Format(decimal value, bool includeDecimals = true)
    {
        var negative = value < 0;
        var abs = Math.Abs(value);
        var fixedPoint = abs.ToString(includeDecimals ? "0.00" : "0", CultureInfo.InvariantCulture);

        var dot = fixedPoint.IndexOf('.', StringComparison.Ordinal);
        var integerPart = dot >= 0 ? fixedPoint[..dot] : fixedPoint;
        var fraction = dot >= 0 ? fixedPoint[dot..] : string.Empty;

        var grouped = new StringBuilder();
        if (integerPart.Length <= 3)
        {
            grouped.Append(integerPart);
        }
        else
        {
            var head = integerPart[..^3];
            var tail = integerPart[^3..];
            // Groups of two from the right of the head: 12,34,56,789
            var firstGroup = head.Length % 2;
            if (firstGroup > 0)
            {
                grouped.Append(head[..firstGroup]).Append(',');
            }

            for (var i = firstGroup; i < head.Length; i += 2)
            {
                grouped.Append(head, i, 2).Append(',');
            }

            grouped.Append(tail);
        }

        return (negative ? "-" : string.Empty) + grouped + fraction;
    }

    /// <summary>"₹1,25,000.00" — currency symbol prefix for INR.</summary>
    public static string FormatRupees(decimal value) =>
        value < 0 ? "-₹" + Format(-value) : "₹" + Format(value);
}
