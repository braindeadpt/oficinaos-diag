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

    // Defesa em profundidade do redeem (CloudClient.RedeemPairingAsync):
    // scope "full" na resposta é recusado; "diag" passa; ausente passa.
    [Theory]
    [InlineData("full")]
    [InlineData("admin")]
    [InlineData("")]
    public void RedeemScopeNaoDiag_ERecusado(string scope) =>
        Assert.Equal(scope, IntakeRules.RedeemScopeError(scope));

    [Theory]
    [InlineData("diag")]
    [InlineData(null)] // cloud antiga não devolve scope — não partir
    public void RedeemScopeDiagOuAusente_Passa(string? scope) =>
        Assert.Null(IntakeRules.RedeemScopeError(scope));

    // POST /diag-logs: a Cloud recusa log vazio (min 1) ou >200k (max 200_000).

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   \n  ")]
    public void PrepareLog_VazioOuBranco_NaoEnvia(string? log) =>
        Assert.Null(IntakeRules.PrepareLogForUpload(log));

    [Fact]
    public void PrepareLog_DentroDoLimite_PassaIgual()
    {
        var log = new string('x', IntakeRules.LogMaxChars);
        Assert.Equal(log, IntakeRules.PrepareLogForUpload(log));
    }

    [Fact]
    public void PrepareLog_AcimaDoLimite_FicaACaudaComMarcador()
    {
        var log = new string('a', 50_000) + "\n" + new string('b', IntakeRules.LogMaxChars);
        var preparado = IntakeRules.PrepareLogForUpload(log)!;
        Assert.True(preparado.Length <= IntakeRules.LogMaxChars);
        Assert.StartsWith(L10n.T("log.log.truncated"), preparado);
        Assert.EndsWith(new string('b', 1000), preparado);
        // O corte salta para o início da linha seguinte — não parte linhas.
        Assert.DoesNotContain("a\nb", preparado[..50]);
    }

    [Fact]
    public void PrepareLog_CortePertoDeNewline_ComecaNaLinhaSeguinte()
    {
        // O '\n' cai logo depois do ponto de corte: o resultado começa na
        // linha inteira seguinte ('b'), não a meio da linha de 'p'.
        var marcador = L10n.T("log.log.truncated") + "\n";
        var room = IntakeRules.LogMaxChars - marcador.Length;
        var log = new string('p', room) + "\n" + new string('b', room - 2);
        var preparado = IntakeRules.PrepareLogForUpload(log)!;
        Assert.True(preparado.Length <= IntakeRules.LogMaxChars);
        Assert.Equal('b', preparado[marcador.Length]);
    }

    [Fact]
    public void PrepareLog_CorteEmParSurrogate_NaoParteOEmoji()
    {
        // Emoji = par surrogate: o corte é posicionado sobre a metade baixa.
        // Sem a proteção o upload começava com um caractere inválido.
        var marcador = L10n.T("log.log.truncated") + "\n";
        var room = IntakeRules.LogMaxChars - marcador.Length;
        var log = new string('p', marcador.Length) + "\U0001F600" + new string('b', room - 1);
        var preparado = IntakeRules.PrepareLogForUpload(log)!;
        Assert.Equal('b', preparado[marcador.Length]);
        Assert.DoesNotContain("\uFFFD", preparado);
    }
}
