using FundLedger.Domain;

namespace FundLedger.Domain.Tests;

public sealed class MoneyTests
{
    [Theory]
    [InlineData("25000.00", 25000.00)]
    [InlineData("0.01", 0.01)]
    [InlineData("1000000", 1000000)]
    public void Positive_accepts_valid_amounts(string wire, decimal expected) =>
        Assert.Equal(expected, Money.Positive(Money.Parse(wire).Amount).Amount);

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Positive_rejects_zero_and_negative_BR005(decimal amount)
    {
        var ex = Assert.Throws<DomainException>(() => Money.Positive(amount));
        Assert.Equal("AMOUNT_OUT_OF_RANGE", ex.Code);
    }

    [Fact]
    public void Rejects_more_than_two_decimals()
    {
        var ex = Assert.Throws<DomainException>(() => Money.Of(10.005m));
        Assert.Equal("AMOUNT_PRECISION", ex.Code);
    }

    [Theory]
    [InlineData("abc")]
    [InlineData("1,000")]
    [InlineData("")]
    public void Parse_rejects_non_decimal_strings(string wire) =>
        Assert.Throws<DomainException>(() => Money.Parse(wire));

    [Fact]
    public void Wire_format_is_invariant_with_two_decimals() =>
        Assert.Equal("25000.50", Money.Of(25000.5m).ToString());

    [Fact]
    public void Arithmetic_supports_negative_balances()
    {
        var balance = Money.Of(1400m) - Money.Of(2000m);
        Assert.Equal(-600m, balance.Amount);
        Assert.True(balance < Money.Zero);
    }
}
