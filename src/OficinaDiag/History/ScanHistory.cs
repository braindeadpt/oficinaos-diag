using System.IO;
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
