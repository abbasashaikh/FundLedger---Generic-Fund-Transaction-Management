using System.Security.Cryptography;
using System.Text;
using FundLedger.Application.Reports;
using Microsoft.Extensions.Configuration;

namespace FundLedger.Infrastructure.Exports;

/// <summary>
/// Receipt verification code (TRD TR-066): the first 8 hex characters of HMAC-SHA256(key, "id:revision"). The key is
/// derived from the JWT signing key, which is already a managed secret, so no new secret is introduced. Without a signing
/// key (Development/Testing only) a fixed local key is used. Whoever holds the key can recompute and so verify a code.
/// </summary>
public sealed class ReceiptCodeSigner(IConfiguration configuration) : IReceiptCodeSigner
{
    private readonly byte[] _key = SHA256.HashData(Encoding.UTF8.GetBytes(
        "fundledger-receipt|" + (configuration["Auth:Jwt:SigningKeyPem"] is { Length: > 0 } pem ? pem : "local-development-only")));

    public string Sign(Guid transactionId, int revision)
    {
        var mac = HMACSHA256.HashData(_key, Encoding.UTF8.GetBytes($"{transactionId:N}:{revision}"));
        return Convert.ToHexString(mac.AsSpan(0, 4));
    }
}
