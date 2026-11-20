using System.Text;
using OficinaDiag.Log;
using Xunit;

namespace OficinaDiag.Tests;

/// <summary>
/// `AppLog` roda o log acima de 1 MB: o ficheiro inteiro passa para
/// `diag.log.1` (sem cortes, por isso nenhum caractere multi-byte fica partido)
/// e o `diag.log` recomeça com a linha nova.
/// </summary>
/// O destino do `AppLog` é estático: em paralelo com outro teste que o
/// redirecione (ou que provoque um log), as escritas caem no ficheiro errado.
[Collection(SerialCollection.Name)]
public sealed class AppLogTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"log-{Guid.NewGuid():N}");
    private readonly string _file;
    private readonly string _anterior;

    public AppLogTests()
    {
        Directory.CreateDirectory(_dir);
        _file = Path.Combine(_dir, "diag.log");
        _anterior = AppLog.RedirectTo(_file);
    }

    public void Dispose()
    {
        AppLog.RedirectTo(_anterior);
        try { Directory.Delete(_dir, true); } catch { }
    }

    [Fact]
    public void RotacaoPassaFicheiroInteiroParaPontoUm()
    {
        // 400 000 'x' + 200 000 '€' (600 000 bytes) + quebra + linhas finais.
        var conteudo = new StringBuilder()
            .Append('x', 400_000)
            .Append('€', 200_000)
            .Append('\n');
        for (var i = 0; i < 1000; i++) conteudo.Append("recente €\n");
        File.WriteAllText(_file, conteudo.ToString());

        var antes = File.ReadAllBytes(_file);
        Assert.True(antes.Length > LogFile.MaxBytes, $"ficheiro com {antes.Length} bytes");

        AppLog.Write("rotação depois");

        // O ficheiro rodado é o original byte a byte — nenhum corte.
        Assert.Equal(antes, File.ReadAllBytes(_file + ".1"));

        var depois = File.ReadAllBytes(_file);
        var texto = new UTF8Encoding(false, throwOnInvalidBytes: true).GetString(depois);
        Assert.True(depois.Length < 200_000, $"ficheiro com {depois.Length} bytes depois da rotação");
        Assert.DoesNotContain("recente €", texto);
        Assert.Contains("rotação depois", texto);
    }

    [Fact]
    public void SegundaRotacaoSobrescrevePontoUm()
    {
        File.WriteAllText(_file + ".1", "rotação velha\n");
        File.WriteAllText(_file, new string('x', (int)LogFile.MaxBytes + 1));

        AppLog.Write("linha nova");

        Assert.DoesNotContain("rotação velha", File.ReadAllText(_file + ".1"));
        Assert.Contains("linha nova", File.ReadAllText(_file));
    }

    [Fact]
    public void AbaixoDoTeto_NaoRoda()
    {
        File.WriteAllText(_file, "linha antiga\n");
        AppLog.Write("linha nova");
        var texto = File.ReadAllText(_file);
        Assert.Contains("linha antiga", texto);
        Assert.Contains("linha nova", texto);
    }

    [Fact]
    public void Exception_VaiParaOMesmoFicheiro()
    {
        AppLog.Exception("teste", new InvalidOperationException("rebentou"));
        var texto = File.ReadAllText(_file);
        Assert.Contains("! teste: InvalidOperationException: rebentou", texto);
    }
}
