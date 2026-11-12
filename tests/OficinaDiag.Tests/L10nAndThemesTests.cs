using System.Globalization;
using System.Reflection;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Xunit;

namespace OficinaDiag.Tests;

/// <summary>
/// B-15: uma chave só em PT aparece em português a quem trabalha em EN; um
/// `x:Key` só num tema deixa um `DynamicResource` a resolver para nada (fundo
/// transparente, brush cinzenta). Ambos os erros passam a quebrar aqui.
/// </summary>
public sealed class L10nAndThemesTests
{
    private static readonly Regex Placeholder = new(@"\{(\d+)\}", RegexOptions.Compiled);

    private static Dictionary<string, Dictionary<string, string>> Dicts()
    {
        var field = typeof(L10n).GetField("D", BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(field);
        return (Dictionary<string, Dictionary<string, string>>)field!.GetValue(null)!;
    }

    [Fact]
    public void PtEEnTemExatamenteAsMesmasChaves()
    {
        var d = Dicts();
        var pt = d["pt"].Keys.ToHashSet(StringComparer.Ordinal);
        var en = d["en"].Keys.ToHashSet(StringComparer.Ordinal);

        Assert.Empty(pt.Except(en));
        Assert.Empty(en.Except(pt));
    }

    [Fact]
    public void PlaceholdersSaoOsMesmosENaoPartemOFormat()
    {
        var d = Dicts();
        foreach (var (key, pt) in d["pt"])
        {
            var en = d["en"][key];
            var ptIdx = Placeholder.Matches(pt).Select(m => int.Parse(m.Groups[1].Value)).Distinct().Order().ToList();
            var enIdx = Placeholder.Matches(en).Select(m => int.Parse(m.Groups[1].Value)).Distinct().Order().ToList();
            Assert.Equal(ptIdx, enIdx);
            if (ptIdx.Count == 0) continue;

            // Argumentos dummy cobrem o índice máximo: um {2} sem {1} rebentava
            // em runtime com FormatException.
            var args = Enumerable.Range(0, ptIdx.Max() + 1).Select(i => (object?)$"v{i}").ToArray();
            Assert.NotNull(string.Format(CultureInfo.InvariantCulture, pt, args));
            Assert.NotNull(string.Format(CultureInfo.InvariantCulture, en, args));
        }
    }

    [Fact]
    public void OsTresTemasDefinemOsMesmosXKey()
    {
        var themes = Path.Combine(RepoRoot(), "src", "OficinaDiag", "Themes");
        var ns = XNamespace.Get("http://schemas.microsoft.com/winfx/2006/xaml");

        HashSet<string> Keys(string path) =>
            XDocument.Load(path).Descendants()
                .Select(e => e.Attribute(ns + "Key")?.Value)
                .Where(k => k is not null)
                .Select(k => k!)
                .ToHashSet(StringComparer.Ordinal);

        var terminal = Keys(Path.Combine(themes, "Terminal.xaml"));
        var fluent = Keys(Path.Combine(themes, "Fluent.xaml"));
        var win95 = Keys(Path.Combine(themes, "Win95.xaml"));

        Assert.NotEmpty(terminal);
        Assert.Empty(terminal.Except(fluent));
        Assert.Empty(fluent.Except(terminal));
        Assert.Empty(terminal.Except(win95));
        Assert.Empty(win95.Except(terminal));
    }

    /// <summary>Sobe do bin/ do teste até à raiz do repositório.</summary>
    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        for (var i = 0; i < 10 && dir is not null; i++)
        {
            if (Directory.Exists(Path.Combine(dir.FullName, "src", "OficinaDiag", "Themes")))
                return dir.FullName;
            dir = dir.Parent;
        }
        throw new DirectoryNotFoundException("raiz do repositório não encontrada a partir de " + AppContext.BaseDirectory);
    }
}
