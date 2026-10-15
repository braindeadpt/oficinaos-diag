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

    // --- Anti-downgrade: a linha "version x.y.z" assinada prende o conteúdo ao tag ---

    private static (string pubB64, string sigB64, byte[] sums) SignedSums(
        string version, string zipName = "oficinaos-diag-v0.3.0-win-x64.zip")
        => SignedFixture($"version {version}\nabc123  {zipName}\n");

    [Fact]
    public void AcceptsSignedSumsWithMatchingNewerVersion()
    {
        var (pub, sig, sums) = SignedSums("0.3.0");
        Assert.Null(UpdateChecker.ValidateSums(sums, sig, "v0.3.0", new Version(0, 2, 0), pub));
    }

    [Fact]
    public void RejectsSignedSumsWithMismatchedTag()
    {
        // SUMS assinado de uma release antiga republicado num tag mais alto —
        // a assinatura é válida, mas a versão assinada não bate no tag.
        var (pub, sig, sums) = SignedSums("0.2.0");
        Assert.NotNull(UpdateChecker.ValidateSums(sums, sig, "v0.9.9", new Version(0, 1, 0), pub));
    }

    [Theory]
    [InlineData("0.2.0")]  // igual à versão local — replay
    [InlineData("0.1.9")]  // mais antiga — downgrade assinado
    public void RejectsSignedSumsNotNewerThanLocal(string version)
    {
        var (pub, sig, sums) = SignedSums(version);
        Assert.NotNull(UpdateChecker.ValidateSums(sums, sig, $"v{version}", new Version(0, 2, 0), pub));
    }

    [Fact]
    public void RejectsSignedSumsWithoutVersionLine()
    {
        // SUMS antigo (pré-version) mesmo bem assinado não pode atualizar —
        // sem a linha assinada não há prova anti-downgrade.
        var (pub, sig, sums) = SignedFixture();
        Assert.NotNull(UpdateChecker.ValidateSums(sums, sig, "v9.9.9", new Version(0, 1, 0), pub));
    }

    [Fact]
    public void AcceptsSignedSumsWithCommitLine()
    {
        // Formato novo: linha "commit <sha-40>" entre version e ficheiros.
        // É metadado assinado — o updater ignora-a e valida na mesma.
        var (pub, sig, sums) = SignedFixture(
            "version 0.3.0\ncommit 0123456789abcdef0123456789abcdef01234567\nabc123  oficinaos-diag-v0.3.0-win-x64.zip\n");
        Assert.Null(UpdateChecker.ValidateSums(sums, sig, "v0.3.0", new Version(0, 2, 0), pub));
    }

    [Fact]
    public void CommitLineIsNotAFileEntry()
    {
        // A linha "commit <sha>" não pode ser confundida com a entrada do
        // zip — o pin do ficheiro continua a vir da linha de hash própria.
        var sums = Encoding.UTF8.GetBytes(
            "version 0.3.0\ncommit 0123456789abcdef0123456789abcdef01234567\nabc123  oficinaos-diag-v0.3.0-win-x64.zip\n");
        Assert.Equal("abc123", UpdateChecker.ExpectedHash(sums,
            "https://github.com/braindeadpt/oficinaos-diag/releases/download/v0.3.0/oficinaos-diag-v0.3.0-win-x64.zip"));
        // E um ficheiro que não consta do SUMS não arranja hash emprestado.
        Assert.Null(UpdateChecker.ExpectedHash(sums,
            "https://github.com/braindeadpt/oficinaos-diag/releases/download/v0.3.0/outro.zip"));
    }

    [Fact]
    public void RejectsValidVersionWithBadSignature()
    {
        // A ordem importa: versão válida + assinatura forjada → recusado.
        var (_, sig, sums) = SignedSums("0.3.0");
        using var other = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var otherPub = Convert.ToBase64String(other.ExportSubjectPublicKeyInfo());
        Assert.NotNull(UpdateChecker.ValidateSums(sums, sig, "v0.3.0", new Version(0, 2, 0), otherPub));
    }

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
