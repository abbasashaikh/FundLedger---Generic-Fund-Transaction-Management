using System.Security.Cryptography;
using FundLedger.Application.Abstractions;
using FundLedger.Domain.Users;
using Microsoft.AspNetCore.Identity;

namespace FundLedger.Infrastructure.Security;

/// <summary>
/// PINs hashed with ASP.NET Core Identity's <see cref="PasswordHasher{TUser}"/>
/// (PBKDF2, per-hash salt, versioned format with automatic rehash). ADR-0002.
/// </summary>
public sealed class PinHasher : IPinHasher
{
    private static readonly User HashSubject = new() { FullName = "-", MobileE164 = "-" }; // the hasher ignores it
    private readonly PasswordHasher<User> _hasher = new();
    private readonly string _dummyHash;

    public PinHasher()
    {
        // Verified against when the user is unknown/inactive so timing doesn't reveal it (TR-011).
        _dummyHash = _hasher.HashPassword(HashSubject, RandomNumberGenerator.GetHexString(12));
    }

    public string Hash(string pin) => _hasher.HashPassword(HashSubject, pin);

    public PinVerification Verify(string hash, string pin) => _hasher.VerifyHashedPassword(HashSubject, hash, pin) switch
    {
        PasswordVerificationResult.Success => PinVerification.Success,
        PasswordVerificationResult.SuccessRehashNeeded => PinVerification.SuccessRehashNeeded,
        _ => PinVerification.Failed,
    };

    public void VerifyDummy(string pin) => _ = _hasher.VerifyHashedPassword(HashSubject, _dummyHash, pin);
}
