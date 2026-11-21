using OficinaDiag.WhatsApp;
using Xunit;
using Xunit.Abstractions;

namespace OficinaDiag.Tests;

/// <summary>
/// Spike descartável contra a BD real extraída de um iPhone de teste
/// (Business, 1.43M msgs). Não é teste de CI — prova manual de escala;
/// corre só quando OFICINAOS_SPIKE_DB aponta para o ficheiro nesta máquina
/// (o caminho — que contém o UDID do aparelho — nunca fica no repo).
/// </summary>
public sealed class WaRealSpike(ITestOutputHelper o)
{
    private static readonly string? RealDb =
        Environment.GetEnvironmentVariable("OFICINAOS_SPIKE_DB");

    [Fact]
    public void RealBusinessDb_Inspects()
    {
        if (RealDb is null || !File.Exists(RealDb)) return;
        using var db = WaStore.OpenReadOnly(RealDb);
        db.Open();

        var info = WaStore.Inspect(db);
        o.WriteLine($"variant={info.Variant} model={info.ModelVersion} " +
            $"ok={info.IntegrityOk} msgs={info.MessageCount} " +
            $"chats={info.ChatCount} media={info.MediaItemCount}");
        o.WriteLine($"first={info.FirstMessageUtc:u} last={info.LastMessageUtc:u}");
        foreach (var (t, n) in info.MessageTypes.OrderByDescending(kv => kv.Value).Take(12))
            o.WriteLine($"  type {t}: {n}");

        Assert.Equal(WaStoreVariant.IosChatStorage, info.Variant);
        Assert.True(info.IntegrityOk);
    }

    [Fact]
    public void RealBusinessDb_Exports()
    {
        if (RealDb is null || !File.Exists(RealDb)) return;
        using var db = WaStore.OpenReadOnly(RealDb);
        db.Open();

        var outDir = Path.Combine(Path.GetTempPath(), "wa-export-spike");
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var chats = WaIosReader.Chats(db);
        o.WriteLine($"{chats.Count} conversas");

        var n = WaHtmlExport.Export(db, outDir);
        o.WriteLine($"{n} ficheiros exportados para {outDir} em {sw.Elapsed}");
    }
}
