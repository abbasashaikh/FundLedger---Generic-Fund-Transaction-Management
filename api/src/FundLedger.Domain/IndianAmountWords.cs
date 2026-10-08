using System.Text;

namespace FundLedger.Domain;

/// <summary>
/// Rupee amounts in words, Indian system (TRD TR-066): "Rupees One Lakh Twenty-Five Thousand Only",
/// "Rupees Ten and Fifty Paise Only". Crore, lakh, thousand, hundred; amounts of 100 crore and more
/// read as "One Hundred Crore" etc. (the crore part is itself spelled in the Indian system).
/// </summary>
public static class IndianAmountWords
{
    private static readonly string[] Ones =
    [
        "Zero", "One", "Two", "Three", "Four", "Five", "Six", "Seven", "Eight", "Nine", "Ten",
        "Eleven", "Twelve", "Thirteen", "Fourteen", "Fifteen", "Sixteen", "Seventeen", "Eighteen", "Nineteen",
    ];

    private static readonly string[] Tens = ["", "", "Twenty", "Thirty", "Forty", "Fifty", "Sixty", "Seventy", "Eighty", "Ninety"];

    public static string ToWords(decimal amount)
    {
        if (amount < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(amount), "Amounts in words must not be negative.");
        }

        var rounded = Math.Round(amount, 2, MidpointRounding.AwayFromZero);
        var rupees = (long)Math.Truncate(rounded);
        var paise = (int)((rounded - rupees) * 100);

        var sb = new StringBuilder("Rupees ");
        if (rupees > 0 || paise == 0)
        {
            sb.Append(Spell(rupees));
        }

        if (paise > 0)
        {
            sb.Append(rupees > 0 ? " and " : string.Empty).Append(Spell(paise)).Append(" Paise");
        }

        return sb.Append(" Only").ToString();
    }

    /// <summary>Spells a whole number in the Indian system.</summary>
    public static string Spell(long n)
    {
        if (n == 0)
        {
            return Ones[0];
        }

        var parts = new List<string>();
        var crore = n / 1_00_00_000;
        n %= 1_00_00_000;
        if (crore > 0)
        {
            parts.Add(Spell(crore) + " Crore");
        }

        var lakh = n / 1_00_000;
        n %= 1_00_000;
        if (lakh > 0)
        {
            parts.Add(BelowHundred((int)lakh) + " Lakh");
        }

        var thousand = n / 1000;
        n %= 1000;
        if (thousand > 0)
        {
            parts.Add(BelowHundred((int)thousand) + " Thousand");
        }

        var hundred = n / 100;
        n %= 100;
        if (hundred > 0)
        {
            parts.Add(Ones[hundred] + " Hundred");
        }

        if (n > 0)
        {
            parts.Add(BelowHundred((int)n));
        }

        return string.Join(' ', parts);
    }

    private static string BelowHundred(int n) =>
        n < 20 ? Ones[n] : Tens[n / 10] + (n % 10 > 0 ? "-" + Ones[n % 10] : string.Empty);
}
