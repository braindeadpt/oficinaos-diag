using System.Text.RegularExpressions;
using Xunit;

namespace OficinaDiag.Tests;

/// <summary>
/// O botão "ok" do checklist levava a chave `chk.ok`, que não existia: em PT
/// saía literal e o resto do ecrã vinha traduzido. Um `L10n.T` sem chave cai
/// para o próprio nome, por isso é fácil não dar por isso — este teste lê as
/// chaves usadas na app e exige-as nos dois idiomas.
/// </summary>
[Collection(SerialCollection.Name)]
public sealed class L10nTests
{
    [Fact]
    public void TodaAChaveUsadaNaAppEstaTraduzidaNosDoisIdiomas()
    {
        var chaves = ChavesUsadas().Distinct().OrderBy(k => k).ToList();
        Assert.NotEmpty(chaves);

        var anterior = L10n.Current;
        var faltam = new List<string>();
        try
        {
            foreach (var idioma in new[] { "pt", "en" })
            {
                L10n.Set(idioma);
                foreach (var chave in chaves)
                    // T cai para o próprio nome quando a chave não existe.
                    if (L10n.T(chave) == chave) faltam.Add($"{idioma}:{chave}");
            }
        }
        finally { L10n.Set(anterior); }

        Assert.True(faltam.Count == 0,
            "chaves sem tradução: " + string.Join(", ", faltam));
    }

    /// <summary>
    /// Literais passados a `L10n.T/F/Btn/Section` no projeto WPF. Apanha também
    /// os ternários (`L10n.T(x ? "a" : "b")`), que um regex por chamada perdia.
    /// </summary>
    private static IEnumerable<string> ChavesUsadas()
    {
        var chamada = new Regex(@"L10n\.(?:T|F|Btn|Section)\((.*?)\)", RegexOptions.Singleline);
        var literal = new Regex("\"([a-z][a-z0-9]*(?:\\.[a-z0-9]+)+)\"");

        var dir = Repo.Path("src", "OficinaDiag");
        foreach (var ficheiro in Directory.EnumerateFiles(dir, "*.cs", SearchOption.AllDirectories))
        {
            if (ficheiro.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")) continue;
            var codigo = File.ReadAllText(ficheiro);
            foreach (Match m in chamada.Matches(codigo))
                foreach (Match k in literal.Matches(m.Groups[1].Value))
                    yield return k.Groups[1].Value;
        }
    }
}
