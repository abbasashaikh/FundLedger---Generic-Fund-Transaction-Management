using FundLedger.Domain.Users;

namespace FundLedger.Domain.Tests;

public sealed class MobileNumberTests
{
    [Theory]
    [InlineData("9876543210", "+919876543210")]
    [InlineData("98765 43210", "+919876543210")]
    [InlineData("+91 98765-43210", "+919876543210")]
    [InlineData("09876543210", "+919876543210")]
    [InlineData("919876543210", "+919876543210")]
    [InlineData("(+91) 98765.43210", "+919876543210")]
    [InlineData("+447911123456", "+447911123456")]
    public void Normalizes_to_e164(string input, string expected)
    {
        Assert.True(MobileNumber.TryNormalize(input, out var e164));
        Assert.Equal(expected, e164);
    }

    [Theory]
    [InlineData("")]
    [InlineData("12345")]
    [InlineData("5876543210")]        // Indian mobiles start 6–9
    [InlineData("+91987654321")]      // 9 digits
    [InlineData("98765abc10")]
    [InlineData("+0123456789")]
    [InlineData("9876543210; DROP TABLE")]
    public void Rejects_invalid_numbers(string input) =>
        Assert.False(MobileNumber.TryNormalize(input, out _));

    [Fact]
    public void Normalize_throws_domain_error_for_invalid() =>
        Assert.Equal("INVALID_MOBILE", Assert.Throws<DomainException>(() => MobileNumber.Normalize("123")).Code);

    [Fact]
    public void Mask_keeps_country_code_and_last_four() =>
        Assert.Equal("+91******3210", MobileNumber.Mask("+919876543210"));
}

public sealed class PinPolicyTests
{
    private const string Mobile = "+919876543210";

    [Theory]
    [InlineData("482915")]
    [InlineData("730264")]
    public void Accepts_unpredictable_pins(string pin) => Assert.Null(PinPolicy.Check(pin, Mobile));

    [Theory]
    [InlineData("12345")]
    [InlineData("1234567")]
    [InlineData("12a456")]
    [InlineData(null)]
    public void Rejects_malformed(string? pin) => Assert.Equal("PIN_FORMAT", PinPolicy.Check(pin, Mobile));

    [Theory]
    [InlineData("000000")]
    [InlineData("777777")]
    [InlineData("123456")]
    [InlineData("654321")]
    [InlineData("890123")]          // wrap-around ascending
    [InlineData("543210")]
    [InlineData("121212")]          // common
    public void Rejects_weak(string pin) => Assert.Equal("PIN_TOO_WEAK", PinPolicy.Check(pin, Mobile));

    [Fact]
    public void Rejects_last_six_digits_of_own_mobile() =>
        Assert.Equal("PIN_TOO_WEAK", PinPolicy.Check("543210", "+919876543210"));

    [Fact]
    public void Generated_temporary_pins_pass_the_policy()
    {
        for (var i = 0; i < 500; i++)
        {
            var pin = PinPolicy.GenerateTemporary(Mobile);
            Assert.Null(PinPolicy.Check(pin, Mobile));
        }
    }
}
