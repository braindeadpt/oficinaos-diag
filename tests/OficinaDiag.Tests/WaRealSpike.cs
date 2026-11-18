using OficinaDiag.WhatsApp;
using Xunit;
using Xunit.Abstractions;

namespace OficinaDiag.Tests;

/// <summary>
/// Spike descartável contra a BD real extraída do iPhone 12 Pro (Business,
/// 1.43M msgs). Não é teste de CI — prova manual de escala; corre só
/// quando o ficheiro existe nesta máquina.
/// </summary>
public sealed class WaRealSpike(ITestOutputHelper o)
{
    private const string RealDb =
        @"E:\oficinaos-ios-backup-v2\00008101-0002390A3600801E\72\724bd3b98b18518b455a87c1f3ac3a0d189c4466";

    [Fact]
    public void RealBusinessDb_Inspects()
    {
        if (!File.Exists(RealDb)) return;
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
        if (!File.Exists(RealDb)) return;
        using var db = WaStore.OpenReadOnly(RealDb);
        db.Open();

        var outDir = @"E:\wa-export-spike";
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var chats = WaIosReader.Chats(db);
        o.WriteLine($"{chats.Count} conversas");

        var n = WaHtmlExport.Export(db, outDir);
        o.WriteLine($"{n} ficheiros exportados para {outDir} em {sw.Elapsed}");
    }
}
