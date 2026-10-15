using System.Security.Cryptography;
using System.Text;
using OficinaDiag.Cloud;
using Xunit;

namespace OficinaDiag.Tests;

public class UpdateCheckerTests
{
    private const string Tag = "v0.2.0";

    [Theory]
    [InlineData("https://github.com/braindeadpt/oficinaos-diag/releases/download/v0.2.0/oficinaos-diag-win-x64.zip")]
    [InlineData("https://github.com/braindeadpt/oficinaos-diag/releases/download/v0.2.0/SHA256SUMS.txt")]
    public void AcceptsPinnedPublisher(string url) => Assert.True(UpdateChecker.IsTrustedAssetUrl(url, Tag));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("http://github.com/braindeadpt/oficinaos-diag/releases/download/v0.2.0/x.zip")]            // não https
    [InlineData("https://evil.example/braindeadpt/oficinaos-diag/releases/download/v0.2.0/x.zip")]        // outro host
    [InlineData("https://github.com.evil.example/braindeadpt/oficinaos-diag/releases/download/v0.2.0/x.zip")]
    [InlineData("https://github.com/attacker/oficinaos-diag/releases/download/v0.2.0/x.zip")]            // outro owner
    [InlineData("https://github.com/braindeadpt/other/releases/download/v0.2.0/x.zip")]                  // outro repo
    [InlineData("https://github.com/braindeadpt/oficinaos-diag/releases/download/v0.1.0/x.zip")]         // outra tag
    [InlineData("https://github.com:8443/braindeadpt/oficinaos-diag/releases/download/v0.2.0/x.zip")]    // porta
    [InlineData("https://user@github.com/braindeadpt/oficinaos-diag/releases/download/v0.2.0/x.zip")]    // userinfo
    [InlineData("https://github.com/braindeadpt/oficinaos-diag/releases/download/v0.2.0/x.zip?a=b")]     // query
    [InlineData("https://github.com/braindeadpt/oficinaos-diag/raw/main/x.zip")]
    public void RejectsEverythingElse(string? url) => Assert.False(UpdateChecker.IsTrustedAssetUrl(url, Tag));

    // --- Assinatura das releases (EC P-256 sobre o SHA256SUMS) ---

    private static (string pubB64, string sigB64, byte[] sums) SignedFixture(string sumsText = "abc123  oficinaos-diag-win-x64.zip\n")
    {
        using var ec = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var sums = Encoding.UTF8.GetBytes(sumsText);
        var sig = ec.SignData(sums, HashAlgorithmName.SHA256,
            DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
        return (Convert.ToBase64String(ec.ExportSubjectPublicKeyInfo()),
                Convert.ToBase64String(sig), sums);
    }

    [Fact]
    public void AcceptsValidSignature()
    {
        var (pub, sig, sums) = SignedFixture();
        Assert.True(UpdateChecker.VerifySumsSignature(sums, sig, pub));
    }

    [Fact]
    public void RejectsTamperedChecksumFile()
    {
        var (pub, sig, _) = SignedFixture();
        var evil = Encoding.UTF8.GetBytes("deadbeef  oficinaos-diag-win-x64.zip\n");
        Assert.False(UpdateChecker.VerifySumsSignature(evil, sig, pub));
    }

    [Fact]
    public void RejectsSignatureFromWrongKey()
    {
        var (_, sig, sums) = SignedFixture();
        using var other = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var otherPub = Convert.ToBase64String(other.ExportSubjectPublicKeyInfo());
        Assert.False(UpdateChecker.VerifySumsSignature(sums, sig, otherPub));
    }

    [Theory]
    [InlineData("not-base64!!!")]
    [InlineData("AAAA")]                 // curto demais para uma assinatura P1363
    [InlineData("")]                      // asset .sig vazio
    public void RejectsGarbageSignature(string sig) =>
        Assert.False(UpdateChecker.VerifySumsSignature(
            Encoding.UTF8.GetBytes("x"), sig, SignedFixture().pubB64));

    [Fact]
    public void EmbeddedPublicKeyIsWellFormed()
    {
        // A chave embutida tem de ser SPKI P-256 válida — uma string estragada
        // deixava o updater permanentemente incapaz de atualizar.
        using var ec = ECDsa.Create();
        ec.ImportSubjectPublicKeyInfo(
            Convert.FromBase64String(UpdateChecker.ReleaseSigningPublicKey), out var read);
        Assert.Equal(ec.ExportSubjectPublicKeyInfo().Length, read);
    }
}
