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
}
