using System.Xml.Linq;
using Xunit;

namespace OficinaDiag.Tests;

/// <summary>
/// A lista do que o zip de release tem de conter (<c>tools/package-files.txt</c>)
/// vive fora dos csproj: se um ficheiro de runtime for renomeado ou acrescentado
/// no publish sem atualizar a lista, o passo de CI verifica o pacote errado e
/// passa. Estes testes comparam os dois lados, nos dois sentidos.
/// </summary>
public class PackageFilesTests
{
    private static readonly string Root = RepoRoot();

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        for (var i = 0; i < 10 && dir is not null; i++)
        {
            if (Directory.Exists(Path.Combine(dir.FullName, "src", "OficinaDiag")))
                return dir.FullName;
            dir = dir.Parent;
        }
        throw new DirectoryNotFoundException(
            "raiz do repositório não encontrada a partir de " + AppContext.BaseDirectory);
    }

    private static string[] Required()
    {
        var path = Path.Combine(Root, "tools", "package-files.txt");
        Assert.True(File.Exists(path), $"lista ausente: {path}");
        return File.ReadAllLines(path)
            .Select(l => l.Trim())
            .Where(l => l.Length > 0 && !l.StartsWith('#'))
            .ToArray();
    }

    /// <summary>
    /// Caminhos que o publish leva do csproj: <c>Content</c> com
    /// <c>CopyToPublishDirectory</c>, já sem wildcards e com o <c>LinkBase</c>
    /// aplicado. Um diretório acaba em '/'.
    /// </summary>
    private static List<string> PublishedContent()
    {
        var published = new List<string>();
        foreach (var csproj in new[] { "src/OficinaDiag/OficinaDiag.csproj", "src/OficinaDiag.Core/OficinaDiag.Core.csproj" })
        {
            var doc = XDocument.Load(Path.Combine(Root, csproj));
            foreach (var item in doc.Descendants("Content"))
            {
                if ((string?)item.Attribute("CopyToPublishDirectory") is null)
                    continue;

                var linkBase = (string?)item.Attribute("LinkBase");
                var include = ((string?)item.Attribute("Include") ?? string.Empty).Replace('\\', '/');
                if (linkBase is not null)
                {
                    var hasWildcard = include.Contains('*');
                    var name = include.TrimEnd('/').Split('/').Last().Replace("*", string.Empty);
                    published.Add(hasWildcard ? $"{linkBase.TrimEnd('/')}/" : $"{linkBase.TrimEnd('/')}/{name}");
                    continue;
                }

                var isWildcard = include.Contains('*');
                var normalized = include.Replace("**", string.Empty).Replace("*", string.Empty).TrimEnd('/');
                published.Add(isWildcard ? normalized + "/" : normalized);
            }
        }
        return published;
    }

    /// <summary>O exe da app, a partir do AssemblyName do csproj (não à mão).</summary>
    private static string AppExe() =>
        XDocument.Load(Path.Combine(Root, "src/OficinaDiag/OficinaDiag.csproj"))
            .Descendants("AssemblyName").First().Value + ".exe";

    /// <summary>
    /// Um caminho publicado como diretório (acaba em '/') cobre tudo lá dentro;
    /// um ficheiro tem de bater certo à letra.
    /// </summary>
    private static bool Cobre(string publicado, string caminho) =>
        publicado.EndsWith('/')
            ? caminho.StartsWith(publicado, StringComparison.Ordinal)
            : caminho == publicado;

    [Fact]
    public void ExeDaAppEstaNaLista()
    {
        // Não vem de um Content, por isso escapa aos testes de divergência.
        Assert.Contains(AppExe(), Required());
    }

    [Fact]
    public void CadaFicheiroExigidoVemDoPublishOuE_O_Exe()
    {
        var published = PublishedContent();
        var exe = AppExe();

        foreach (var entry in Required())
        {
            if (entry == exe)
                continue; // PublishSingleFile gera-o; não há Content.

            Assert.True(
                published.Any(p => Cobre(p, entry)),
                $"'{entry}' está na lista mas nenhum copy-to-publish do csproj o produz");
        }
    }

    [Fact]
    public void TodoContentPublicadoEstaNaLista()
    {
        var required = Required();
        foreach (var p in PublishedContent())
        {
            Assert.True(
                required.Any(r => Cobre(p, r)),
                $"o csproj publica '{p}' mas nada em tools/package-files.txt o verifica");
        }
    }

    [Fact]
    public void WorkflowLeAListaEmVezDeTerCopiaPropria()
    {
        var yml = File.ReadAllText(Path.Combine(Root, ".github", "workflows", "release.yml"));
        Assert.Contains("tools/package-files.txt", yml);
        // Nenhum caminho da lista pode voltar a estar escrito no YAML.
        Assert.DoesNotContain(Required(), r => yml.Contains(r));
    }
}
