using System.IO;
using System.Text;
using System.Text.Json;
using OficinaDiag.Devices;

namespace OficinaDiag.History;

/// <summary>Local JSON-lines history of past scans — offline, no account.</summary>
public sealed class ScanHistory
{
    private readonly string _file;

    public ScanHistory(string? file = null) =>
        _file = file ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
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
            // Linhas mais recentes primeiro, até ao teto.
            var tail = new List<string>();
            var acc = 0L;
            foreach (var l in File.ReadLines(_file).Reverse())
            {
                if (string.IsNullOrWhiteSpace(l)) continue;
                if ((acc += Encoding.UTF8.GetByteCount(l) + 1) > maxBytes) break;
                tail.Add(l);
            }
            tail.Reverse();
            // tmp+rename — crash a meio da poda não destrói o histórico.
            var tmp = _file + ".tmp";
            File.WriteAllLines(tmp, tail);
            File.Move(tmp, _file, overwrite: true);
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

    /// <summary>Scans anteriores de um serial, mais antigo primeiro — base do antes/depois.</summary>
    public IReadOnlyList<DeviceReport> ForSerial(string serial) =>
        All()
            .Where(r => string.Equals(r.Device.Serial, serial, StringComparison.OrdinalIgnoreCase))
            .OrderBy(r => r.CollectedAt)
            .ToList();

    /// <summary>Battery capacity trend for a serial — the fun longitudinal stat.</summary>
    public IReadOnlyList<(DateTimeOffset at, string capacity)> BatteryTrend(string serial) =>
        All()
            .Where(r => string.Equals(r.Device.Serial, serial, StringComparison.OrdinalIgnoreCase)
                && r.Results.TryGetValue("battery.capacity", out _))
            .Select(r => (at: r.CollectedAt, capacity: r.Results["battery.capacity"].Value ?? "?"))
            .OrderBy(t => t.at)
            .ToList();
}
