using OficinaDiag.Devices;
using OficinaDiag.History;
using Xunit;

namespace OficinaDiag.Tests;

public sealed class ScanHistoryTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"hist-{Guid.NewGuid():N}");
    private readonly string _file;
    private readonly ScanHistory _h;

    public ScanHistoryTests()
    {
        _file = Path.Combine(_dir, "history.jsonl");
        _h = new ScanHistory(_file);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { }
    }

    private static DeviceReport Rep(string serial, string? capacity = null)
    {
        var r = new DeviceReport { Platform = "android" };
        r.Device.Serial = serial;
        if (capacity is not null) r.Set("battery.capacity", "info", capacity);
        return r;
    }

    [Fact]
    public void AddEAll_RoundTrip()
    {
        _h.Add(Rep("AAA"));
        _h.Add(Rep("BBB"));
        var all = _h.All();
        Assert.Equal(2, all.Count);
        Assert.Equal("AAA", all[0].Device.Serial);
    }

    [Fact]
    public void LinhaCorrompida_EIgnorada()
    {
        _h.Add(Rep("AAA"));
        File.AppendAllText(_file, "{isto não é json}\n");
        var all = _h.All();
        Assert.Single(all);
    }

    [Fact]
    public void ForSerial_CaseInsensitive()
    {
        _h.Add(Rep("abc123"));
        Assert.Single(_h.ForSerial("ABC123"));
    }

    [Fact]
    public void BatteryTrend_CaseInsensitiveEOrdenado()
    {
        var r1 = Rep("SERIAL-X", "70%");
        var r2 = Rep("serial-x", "65%");
        _h.Add(r1);
        _h.Add(r2);
        var trend = _h.BatteryTrend("Serial-X");
        Assert.Equal(2, trend.Count);
        Assert.True(trend[0].at <= trend[1].at);
    }

    [Fact]
    public void ReportSemBateria_NaoEntraNaTrend()
    {
        _h.Add(Rep("AAA")); // sem battery.capacity
        Assert.Empty(_h.BatteryTrend("AAA"));
    }

    [Fact]
    public void Prune_MantemFicheiroBaixoDoTeto()
    {
        // >8 MB de linhas → a poda corre no próximo Add.
        var big = new string('x', 500_000);
        for (var i = 0; i < 20; i++)
        {
            var r = Rep($"S{i}");
            r.LogsText = big; // ~0.5 MB por linha
            _h.Add(r);
        }
        var info = new FileInfo(_file);
        Assert.True(info.Length <= 8 * 1024 * 1024 + 1024,
            $"ficheiro com {info.Length} bytes depois da poda");
        // as mais recentes sobrevivem
        var all = _h.All();
        Assert.Equal("S19", all[^1].Device.Serial);
    }
}
