using FundLedger.Domain;

namespace FundLedger.Domain.Tests;

public sealed class IndianAmountWordsTests
{
    [Theory]
    [InlineData("0", "Rupees Zero Only")]
    [InlineData("1", "Rupees One Only")]
    [InlineData("19", "Rupees Nineteen Only")]
    [InlineData("21", "Rupees Twenty-One Only")]
    [InlineData("100", "Rupees One Hundred Only")]
    [InlineData("105", "Rupees One Hundred Five Only")]
    [InlineData("1000", "Rupees One Thousand Only")]
    [InlineData("25000", "Rupees Twenty-Five Thousand Only")]
    [InlineData("125000", "Rupees One Lakh Twenty-Five Thousand Only")]
    [InlineData("1000000", "Rupees Ten Lakh Only")]
    [InlineData("10000000", "Rupees One Crore Only")]
    [InlineData("12345678", "Rupees One Crore Twenty-Three Lakh Forty-Five Thousand Six Hundred Seventy-Eight Only")]
    [InlineData("1000000000", "Rupees One Hundred Crore Only")]
    [InlineData("99999", "Rupees Ninety-Nine Thousand Nine Hundred Ninety-Nine Only")]
    public void Spells_rupees_in_the_indian_system(string amount, string expected) =>
        Assert.Equal(expected, IndianAmountWords.ToWords(decimal.Parse(amount, System.Globalization.CultureInfo.InvariantCulture)));

    [Theory]
    [InlineData("10.50", "Rupees Ten and Fifty Paise Only")]
    [InlineData("0.05", "Rupees Five Paise Only")]
    [InlineData("125000.01", "Rupees One Lakh Twenty-Five Thousand and One Paise Only")]
    [InlineData("99.999", "Rupees One Hundred Only")]
    public void Includes_paise_only_when_not_zero(string amount, string expected) =>
        Assert.Equal(expected, IndianAmountWords.ToWords(decimal.Parse(amount, System.Globalization.CultureInfo.InvariantCulture)));

    [Fact]
    public void Rejects_negative_amounts() => Assert.Throws<ArgumentOutOfRangeException>(() => IndianAmountWords.ToWords(-1m));
}
