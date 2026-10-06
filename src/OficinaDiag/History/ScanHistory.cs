using System.IO;
using System.Text;
using System.Text.Json;
using OficinaDiag.Devices;

namespace OficinaDiag.History;

/// <summary>Local JSON-lines history of past scans — offline, no account.</summary>
public sealed class ScanHistory
{
    private readonly string _file =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "OficinaDiag", "history.jsonl");

    private static readonly JsonSerializerOptions Opts = new() { WriteIndented = false };

    public void Add(DeviceReport report)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_file)!);
        File.AppendAllText(_file, JsonSerializer.Serialize(report, Opts) + Environment.NewLine);
        Prune();
    }

    /// <summary>
    /// Cap do ficheiro — os crash logs dentro dos relatórios podem fazer cada
    /// linha pesar ~1MB; sem poda o ficheiro crescia sem limite. Ficam as
    /// entradas mais recentes dentro de ~8MB.
    /// </summary>
    private void Prune()
    {
        const long maxBytes = 8 * 1024 * 1024;
        try
        {
            var info = new FileInfo(_file);
            if (!info.Exists || info.Length <= maxBytes) return;
            var keep = File.ReadLines(_file)
                .Where(l => !string.IsNullOrWhiteSpace(l))
                .Reverse()
                .TakeWhile(l => true)
                .ToList();
            // Mantém a cauda até ao limite sem re-parsear tudo.
            var acc = 0L;
            var tail = keep.TakeWhile(l => (acc += Encoding.UTF8.GetByteCount(l) + 1) <= maxBytes).ToList();
            tail.Reverse();
            File.WriteAllLines(_file, tail);
        }
        catch { /* poda falhou — fica para a próxima vez */ }
    }

    public IReadOnlyList<DeviceReport> All()
    {
        var list = new List<DeviceReport>();
        if (!File.Exists(_file)) return list;
        foreach (var line in File.ReadLines(_file))
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            try
            {
                var r = JsonSerializer.Deserialize<DeviceReport>(line);
                if (r is not null) list.Add(r);
            }
            catch { /* linha corrompida — ignora */ }
        }
        return list;
    }

    /// <summary>Battery capacity trend for a serial — the fun longitudinal stat.</summary>
    public IReadOnlyList<(DateTimeOffset at, string capacity)> BatteryTrend(string serial) =>
        All()
            .Where(r => r.Device.Serial == serial && r.Results.TryGetValue("battery.capacity", out _))
            .Select(r => (at: r.CollectedAt, capacity: r.Results["battery.capacity"].Value ?? "?"))
            .OrderBy(t => t.at)
            .ToList();
}
