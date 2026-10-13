using System.Security.Cryptography;
using FundLedger.Infrastructure.Security;

namespace FundLedger.Api.IntegrationTests;

public sealed class JwtKeyRingTests
{
    [Fact]
    public void A_signing_key_written_on_one_line_with_backslash_n_works_like_the_multi_line_form()
    {
        using var generated = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var pem = generated.ExportPkcs8PrivateKeyPem();
        var oneLine = pem.Trim().ReplaceLineEndings("\\n");
        Assert.DoesNotContain('\n', oneLine);

        using var multi = new JwtKeyRing(new JwtOptions { SigningKeyPem = pem }, allowEphemeral: false);
        using var flat = new JwtKeyRing(new JwtOptions { SigningKeyPem = oneLine }, allowEphemeral: false);

        Assert.False(flat.IsEphemeral);
        var data = "token"u8.ToArray();
        var signature = flat.SigningKey.ECDsa.SignData(data, HashAlgorithmName.SHA256);
        Assert.True(multi.SigningKey.ECDsa.VerifyData(data, signature, HashAlgorithmName.SHA256));      // the same key
    }

    [Fact]
    public void Production_refuses_to_start_without_a_signing_key()
    {
        Assert.Throws<InvalidOperationException>(() => new JwtKeyRing(new JwtOptions(), allowEphemeral: false));
    }
}
