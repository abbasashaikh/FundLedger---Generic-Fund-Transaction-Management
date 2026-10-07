namespace FundLedger.Domain.Users;

/// <summary>
/// Normalizes user-entered mobile numbers to E.164 (<c>+919876543210</c>), the form
/// stored in <c>users.mobile_e164</c>. Indian numbers are the V1 default: a bare
/// 10-digit number starting 6–9 gets <c>+91</c>. Spaces, dashes and brackets are ignored.
/// </summary>
public static class MobileNumber
{
    public static bool TryNormalize(string? input, out string e164)
    {
        e164 = string.Empty;
        if (string.IsNullOrWhiteSpace(input))
        {
            return false;
        }

        var trimmed = input.Trim();
        var hasPlus = trimmed.StartsWith('+');
        var digits = new string(trimmed.Where(char.IsAsciiDigit).ToArray());
        if (trimmed.Any(c => !(char.IsAsciiDigit(c) || c is ' ' or '-' or '(' or ')' or '+' or '.')))
        {
            return false;
        }

        string candidate;
        if (hasPlus)
        {
            candidate = "+" + digits;
        }
        else if (digits.Length == 10)
        {
            candidate = "+91" + digits;
        }
        else if (digits.Length == 11 && digits[0] == '0')
        {
            candidate = "+91" + digits[1..];
        }
        else if (digits.Length == 12 && digits.StartsWith("91", StringComparison.Ordinal))
        {
            candidate = "+" + digits;
        }
        else
        {
            return false;
        }

        // E.164: + then 8–15 digits, no leading zero.
        if (candidate.Length is < 9 or > 16 || candidate[1] == '0')
        {
            return false;
        }

        // Indian mobiles: exactly 10 digits after +91, starting 6–9.
        if (candidate.StartsWith("+91", StringComparison.Ordinal) &&
            (candidate.Length != 13 || candidate[3] is < '6' or > '9'))
        {
            return false;
        }

        e164 = candidate;
        return true;
    }

    public static string Normalize(string? input) =>
        TryNormalize(input, out var e164)
            ? e164
            : throw new DomainException("INVALID_MOBILE", "Enter a valid 10-digit mobile number.");

    /// <summary>For logs and non-admin displays: <c>+91******3210</c> (TRD TR-077).</summary>
    public static string Mask(string e164) =>
        e164.Length <= 7 ? "***" : e164[..3] + new string('*', e164.Length - 7) + e164[^4..];
}
