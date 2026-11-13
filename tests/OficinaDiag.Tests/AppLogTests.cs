using System.Text;
using OficinaDiag.Log;
using Xunit;

namespace OficinaDiag.Tests;

/// <summary>
/// `AppLog` roda o log acima de 1 MB cortando os últimos ~500 KB. O corte cai
/// a meio de um caractere multi-byte ("ç", "—", "€") sempre que o ficheiro tem
/// acentos — e a app escrevia essa cauda tal e qual, deixando a primeira linha
/// com �. Agora salta até ao fim da linha; aqui prova-se com um ficheiro cujo
/// corte cai dentro de um "€" de 3 bytes.
/// </summary>
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
    public void RotacaoNaoDeixaUtf8OrfaoNoTopo()
    {
        // 400 000 'x' + 200 000 '€' (600 000 bytes) + quebras + linhas finais.
        // O corte de 500 000 bytes cai 112 001 bytes dentro da série de '€',
        // ou seja, a meio do segundo byte de um caractere — o caso que parte.
        var conteudo = new StringBuilder()
            .Append('x', 400_000)
            .Append('€', 200_000)
            .Append('\n');
        for (var i = 0; i < 1000; i++) conteudo.Append("recente €\n");
        File.WriteAllText(_file, conteudo.ToString());

        var antes = File.ReadAllBytes(_file);
        Assert.True(antes.Length > LogFile.MaxBytes, $"ficheiro com {antes.Length} bytes");
        var corte = antes.Length - (int)(LogFile.MaxBytes / 2);
        Assert.True((antes[corte] & 0b1100_0000) == 0b1000_0000,
            $"o corte em {corte} não caiu a meio de um caractere (byte 0x{antes[corte]:X2})");

        AppLog.Write("rotação depois");

        var depois = File.ReadAllBytes(_file);
        // Primeiro a decodificação estrita — lança se houver sequência partida,
        // que é o defeito. O tamanho só é verificado depois.
        var texto = new UTF8Encoding(false, throwOnInvalidBytes: true).GetString(depois);
        Assert.DoesNotContain('\uFFFD', texto);
        Assert.True(depois.Length < 200_000, $"ficheiro com {depois.Length} bytes depois da poda");
        Assert.Contains("recente €", texto);
        Assert.Contains("rotação depois", texto);
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
