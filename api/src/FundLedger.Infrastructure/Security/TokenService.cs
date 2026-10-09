using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using FundLedger.Application.Abstractions;
using FundLedger.Domain.Users;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace FundLedger.Infrastructure.Security;

/// <summary>Configuration section <c>Auth:Jwt</c>. Signing keys come from secrets/env, never the repo.</summary>
public sealed class JwtOptions
{
    public const string Section = "Auth:Jwt";

    public string Issuer { get; set; } = "fundledger-api";

    public string Audience { get; set; } = "fundledger-pwa";

    public int AccessTokenMinutes { get; set; } = 15;

    /// <summary>PKCS#8 PEM of an EC P-256 private key (ES256).</summary>
    public string? SigningKeyPem { get; set; }

    public string KeyId { get; set; } = "k1";

    /// <summary>Public keys of retired signing keys, still accepted during rotation (TR-020).</summary>
    public IList<PublicKeyOption> PreviousKeys { get; } = [];
}

public sealed class PublicKeyOption
{
    public string KeyId { get; set; } = string.Empty;

    public string PublicKeyPem { get; set; } = string.Empty;
}

/// <summary>Holds the signing key and all keys accepted for validation.</summary>
public sealed class JwtKeyRing : IDisposable
{
    private readonly List<ECDsa> _owned = [];

    public JwtKeyRing(JwtOptions options, bool allowEphemeral)
    {
        ArgumentNullException.ThrowIfNull(options);
        Options = options;

        var signing = ECDsa.Create();
        _owned.Add(signing);
        if (!string.IsNullOrWhiteSpace(options.SigningKeyPem))
        {
            signing.ImportFromPem(options.SigningKeyPem);
            SigningKey = new ECDsaSecurityKey(signing) { KeyId = options.KeyId };
        }
        else if (allowEphemeral)
        {
            // Development/Testing only: tokens die with the process.
            signing.GenerateKey(ECCurve.NamedCurves.nistP256);
            SigningKey = new ECDsaSecurityKey(signing) { KeyId = "ephemeral" };
            IsEphemeral = true;
        }
        else
        {
            throw new InvalidOperationException("Auth:Jwt:SigningKeyPem is not configured.");
        }

        var validation = new List<SecurityKey> { SigningKey };
        foreach (var previous in options.PreviousKeys)
        {
            var key = ECDsa.Create();
            _owned.Add(key);
            key.ImportFromPem(previous.PublicKeyPem);
            validation.Add(new ECDsaSecurityKey(key) { KeyId = previous.KeyId });
        }

        ValidationKeys = validation;
    }

    public JwtOptions Options { get; }

    public ECDsaSecurityKey SigningKey { get; }

    public IReadOnlyList<SecurityKey> ValidationKeys { get; }

    public bool IsEphemeral { get; }

    public void Dispose()
    {
        foreach (var key in _owned)
        {
            key.Dispose();
        }
    }
}

/// <summary>Claim names in FundLedger access tokens.</summary>
public static class FundLedgerClaims
{
    public const string Subject = "sub";
    public const string Organization = "org";
    public const string Session = "sid";
    public const string Family = "fam";
    public const string Role = "role";
}

public sealed class TokenService(JwtKeyRing keys, TimeProvider clock) : ITokenService
{
    private readonly JsonWebTokenHandler _handler = new() { SetDefaultTimesOnTokenCreation = false };

    public TimeSpan AccessTokenLifetime => TimeSpan.FromMinutes(keys.Options.AccessTokenMinutes);

    public string IssueAccessToken(User user, Guid sessionId, Guid familyId)
    {
        ArgumentNullException.ThrowIfNull(user);
        var now = clock.GetUtcNow().UtcDateTime;
        return _handler.CreateToken(new SecurityTokenDescriptor
        {
            Issuer = keys.Options.Issuer,
            Audience = keys.Options.Audience,
            IssuedAt = now,
            NotBefore = now,
            Expires = now.Add(AccessTokenLifetime),
            Subject = new ClaimsIdentity(
            [
                new Claim(FundLedgerClaims.Subject, user.Id.ToString()),
                new Claim(FundLedgerClaims.Organization, user.OrganizationId.ToString()),
                new Claim(FundLedgerClaims.Session, sessionId.ToString()),
                new Claim(FundLedgerClaims.Family, familyId.ToString()),
                new Claim(FundLedgerClaims.Role, user.Role.ToString().ToUpperInvariant()),
                new Claim(JwtRegisteredClaimNames.Jti, Guid.CreateVersion7().ToString("N")),
            ]),
            SigningCredentials = new SigningCredentials(keys.SigningKey, SecurityAlgorithms.EcdsaSha256),
        });
    }

    public (string Token, byte[] Hash) NewRefreshToken()
    {
        var token = Base64UrlEncoder.Encode(RandomNumberGenerator.GetBytes(32));
        return (token, HashRefreshToken(token));
    }

    public byte[] HashRefreshToken(string token) => SHA256.HashData(Encoding.UTF8.GetBytes(token));
}
