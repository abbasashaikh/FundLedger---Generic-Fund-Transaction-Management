using FundLedger.Domain;

namespace FundLedger.Domain.Tests;

public sealed class IndianNumberFormatTests
{
    [Theory]
    [InlineData(0, "0.00")]
    [InlineData(999, "999.00")]
    [InlineData(1000, "1,000.00")]
    [InlineData(25000, "25,000.00")]
    [InlineData(125000, "1,25,000.00")]
    [InlineData(1000000, "10,00,000.00")]
    [InlineData(12345678.9, "1,23,45,678.90")]
    [InlineData(100000.5, "1,00,000.50")]
    [InlineData(-8500, "-8,500.00")]
    public void Formats_with_lakh_crore_grouping(decimal value, string expected) =>
        Assert.Equal(expected, IndianNumberFormat.Format(value));

    [Fact]
    public void Can_omit_decimals() =>
        Assert.Equal("1,25,000", IndianNumberFormat.Format(125000m, includeDecimals: false));

    [Theory]
    [InlineData(31400, "₹31,400.00")]
    [InlineData(-100, "-₹100.00")]
    public void Formats_rupees(decimal value, string expected) =>
        Assert.Equal(expected, IndianNumberFormat.FormatRupees(value));
}
