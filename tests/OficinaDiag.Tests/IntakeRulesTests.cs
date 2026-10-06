using System.Net.Http;
using OficinaDiag.Cloud;
using Xunit;

namespace OficinaDiag.Tests;

public class IntakeRulesTests
{
    [Theory]
    [InlineData("ana@exemplo.pt")]
    [InlineData("joao.silva+loja@mail.example.com")]
    [InlineData("o'neil@exemplo.co.uk")]
    [InlineData("  ana@exemplo.pt  ")] // trim
    public void ValidEmails(string email) => Assert.True(IntakeRules.IsValidEmail(email));

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("ana")]
    [InlineData("ana@")]
    [InlineData("@exemplo.pt")]
    [InlineData("ana@exemplo")]
    [InlineData("ana@exemplo.p")]
    [InlineData("ana@@exemplo.pt")]
    [InlineData("ana@exemplo..pt")]
    [InlineData(".ana@exemplo.pt")]
    [InlineData("ana.@exemplo.pt")]
    [InlineData("an..a@exemplo.pt")]
    [InlineData("ana silva@exemplo.pt")]
    [InlineData("ana@exem plo.pt")]
    [InlineData("ana@-exemplo.pt")]
    public void InvalidEmails(string email) => Assert.False(IntakeRules.IsValidEmail(email));

    [Fact]
    public void NullEmailIsInvalid() => Assert.False(IntakeRules.IsValidEmail(null));

    [Fact]
    public void EmailOverCloudMaxIsInvalid()
    {
        var email = new string('a', IntakeRules.EmailMaxLength) + "@exemplo.pt";
        Assert.False(IntakeRules.IsValidEmail(email));
    }

    [Fact]
    public void NotesLimitMatchesCloud()
    {
        Assert.Equal(2000, IntakeRules.NotesMaxLength);
        Assert.True(IntakeRules.NotesWithinLimit(null));
        Assert.True(IntakeRules.NotesWithinLimit(""));
        Assert.True(IntakeRules.NotesWithinLimit(new string('x', 2000)));
        Assert.False(IntakeRules.NotesWithinLimit(new string('x', 2001)));
    }

    [Fact]
    public void IdempotencyKeyIsGuidAndUniquePerSubmission()
    {
        var a = IntakeRules.NewIdempotencyKey();
        var b = IntakeRules.NewIdempotencyKey();
        Assert.True(Guid.TryParse(a, out _));
        Assert.NotEqual(a, b);
    }

    [Fact]
    public void SameKeyIsSentOnEveryRetry()
    {
        // Simula o SendWithRetryAsync: o builder é chamado uma vez por tentativa.
        var key = IntakeRules.NewIdempotencyKey();
        HttpRequestMessage Build() =>
            new HttpRequestMessage(HttpMethod.Post, "https://cloud.example/intake/ABC123")
                .WithIdempotencyKey(key);

        using var first = Build();
        using var retry = Build();
        Assert.Equal(key, Assert.Single(first.Headers.GetValues("Idempotency-Key")));
        Assert.Equal(key, Assert.Single(retry.Headers.GetValues("Idempotency-Key")));
    }

    [Fact]
    public void WithIdempotencyKeyReplacesExistingHeader()
    {
        using var req = new HttpRequestMessage(HttpMethod.Post, "https://cloud.example/intake/ABC123");
        req.WithIdempotencyKey("old").WithIdempotencyKey("new");
        Assert.Equal("new", Assert.Single(req.Headers.GetValues(IntakeRules.IdempotencyHeader)));
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    public void WithIdempotencyKeyRejectsBlank(string key)
    {
        using var req = new HttpRequestMessage(HttpMethod.Post, "https://cloud.example/x");
        Assert.ThrowsAny<ArgumentException>(() => req.WithIdempotencyKey(key));
    }
}
