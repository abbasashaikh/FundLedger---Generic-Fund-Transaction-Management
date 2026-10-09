using System.Security.Cryptography;

namespace FundLedger.Domain.Users;

/// <summary>
/// PIN rules (ADR-0002, TRD TR-012): exactly 6 digits, and not trivially guessable.
/// A 6-digit PIN has only 10^6 combinations, so lockout (TR-013) is the main defence;
/// these rules remove the PINs attackers try first.
/// </summary>
public static class PinPolicy
{
    public const int Length = 6;

    private static readonly HashSet<string> CommonPins = new(StringComparer.Ordinal)
    {
        "123123", "121212", "112233", "001122", "112211", "123321", "654456", "102030",
        "111222", "000111", "101010", "010101", "202020", "696969", "131313", "159753",
        "147258", "258369", "147852", "741852", "963852", "123654", "123789", "789456",
        "456123", "520520", "786786", "110011", "100100", "999000",
    };

    public static bool IsWellFormed(string? pin) =>
        pin is { Length: Length } && pin.All(char.IsAsciiDigit);

    /// <summary>Validates a PIN a user chooses. Returns null when acceptable, else the reason code.</summary>
    public static string? Check(string? pin, string mobileE164)
    {
        if (!IsWellFormed(pin))
        {
            return "PIN_FORMAT";
        }

        var p = pin!;
        if (p.Distinct().Count() == 1)
        {
            return "PIN_TOO_WEAK";                       // 000000, 111111, ...
        }

        if (IsStraightSequence(p, +1) || IsStraightSequence(p, -1))
        {
            return "PIN_TOO_WEAK";                       // 123456, 987654, 890123 ...
        }

        if (mobileE164.EndsWith(p, StringComparison.Ordinal))
        {
            return "PIN_TOO_WEAK";                       // last 6 digits of own mobile
        }

        return CommonPins.Contains(p) ? "PIN_TOO_WEAK" : null;
    }

    public static void EnsureAcceptable(string? pin, string mobileE164)
    {
        switch (Check(pin, mobileE164))
        {
            case null:
                return;
            case "PIN_FORMAT":
                throw new DomainException("VALIDATION_FAILED", "PIN must be exactly 6 digits.");
            default:
                throw new DomainException("PIN_TOO_WEAK",
                    "Choose a less predictable PIN. Avoid repeated digits, sequences like 123456, or the end of your mobile number.");
        }
    }

    /// <summary>A random temporary PIN that itself passes the policy.</summary>
    public static string GenerateTemporary(string mobileE164)
    {
        while (true)
        {
            var pin = RandomNumberGenerator.GetInt32(0, 1_000_000).ToString("D6", System.Globalization.CultureInfo.InvariantCulture);
            if (Check(pin, mobileE164) is null)
            {
                return pin;
            }
        }
    }

    private static bool IsStraightSequence(string pin, int step)
    {
        for (var i = 1; i < pin.Length; i++)
        {
            var expected = ((pin[i - 1] - '0') + step + 10) % 10;
            if (pin[i] - '0' != expected)
            {
                return false;
            }
        }

        return true;
    }
}
