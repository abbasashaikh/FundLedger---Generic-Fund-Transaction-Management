using System.Globalization;

namespace FundLedger.Domain;

/// <summary>
/// A monetary amount in the organization's currency (INR in V1), always with
/// at most 2 decimal places. Transaction amounts must additionally be positive
/// (BR-005); balances may be negative, so that rule lives in <see cref="Positive"/>.
/// </summary>
public readonly record struct Money : IComparable<Money>
{
    public const decimal MaxTransactionAmount = 9_999_999_999_999_999.99m; // numeric(18,2)

    public decimal Amount { get; }

    private Money(decimal amount) => Amount = amount;

    public static Money Zero { get; } = new(0m);

    public static Money Of(decimal amount)
    {
        if (decimal.Round(amount, 2) != amount)
        {
            throw new DomainException("AMOUNT_PRECISION", "Amount can have at most 2 decimal places.");
        }

        if (Math.Abs(amount) > MaxTransactionAmount)
        {
            throw new DomainException("AMOUNT_OUT_OF_RANGE", "Amount is too large.");
        }

        return new Money(amount);
    }

    /// <summary>A transaction amount: greater than zero (BR-005).</summary>
    public static Money Positive(decimal amount)
    {
        var money = Of(amount);
        if (money.Amount <= 0m)
        {
            throw new DomainException("AMOUNT_OUT_OF_RANGE", "Amount must be greater than zero.");
        }

        return money;
    }

    /// <summary>Parses the API wire format: a decimal string such as "25000.00".</summary>
    public static Money Parse(string value)
    {
        if (!decimal.TryParse(value, NumberStyles.AllowDecimalPoint | NumberStyles.AllowLeadingSign,
                CultureInfo.InvariantCulture, out var amount))
        {
            throw new DomainException("VALIDATION_FAILED", "Amount must be a decimal number like 25000.00.");
        }

        return Of(amount);
    }

    public static Money operator +(Money a, Money b) => Of(a.Amount + b.Amount);

    public static Money operator -(Money a, Money b) => Of(a.Amount - b.Amount);

    public static Money operator -(Money a) => new(-a.Amount);

    public static bool operator <(Money a, Money b) => a.Amount < b.Amount;

    public static bool operator >(Money a, Money b) => a.Amount > b.Amount;

    public static bool operator <=(Money a, Money b) => a.Amount <= b.Amount;

    public static bool operator >=(Money a, Money b) => a.Amount >= b.Amount;

    public int CompareTo(Money other) => Amount.CompareTo(other.Amount);

    /// <summary>Wire format: invariant culture, exactly 2 decimals.</summary>
    public override string ToString() => Amount.ToString("0.00", CultureInfo.InvariantCulture);
}
