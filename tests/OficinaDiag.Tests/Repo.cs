using System.IO;

namespace OficinaDiag.Tests;

/// <summary>
/// Raiz do repositório a partir do output de testes: a lista do pacote e os
/// fixtures vivem no checkout, não no bin. Uma cópia da procura em cada teste
/// era duas respostas para a mesma pergunta.
/// </summary>
internal static class Repo
{
    public static string Root { get; } = Localizar();

    private static string Localizar()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        for (var i = 0; i < 10 && dir is not null; i++, dir = dir.Parent)
        {
            if (Directory.Exists(System.IO.Path.Combine(dir.FullName, "src", "OficinaDiag")))
                return dir.FullName;
        }
        throw new DirectoryNotFoundException(
            "raiz do repositório não encontrada a partir de " + AppContext.BaseDirectory);
    }

    /// <summary>Caminho absoluto de um ficheiro do repositório.</summary>
    public static string Path(params string[] partes) =>
        System.IO.Path.Combine([Root, .. partes]);

    /// <summary>Lê um ficheiro do repositório (fixtures, listas).</summary>
    public static string Read(params string[] partes) => File.ReadAllText(Path(partes));
}
