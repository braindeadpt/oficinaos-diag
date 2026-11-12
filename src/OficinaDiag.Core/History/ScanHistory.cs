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

    /// <summary>
    /// Append+poda intercalados por duas instâncias (ex.: app + segundo
    /// processo) perdiam linhas: a poda lia o ficheiro, outro processo
    /// escrevia, o Move sobrescrevia a linha nova. Um named mutex torna a
    /// sequência atómica entre processos.
    /// </summary>
    private static readonly Mutex WriteLock = new(initiallyOwned: false, "OficinaDiag.ScanHistory");

    /// <summary>
    /// Mutex com timeout: uma instância presa não pode congelar a UI. Dono
    /// anterior morto com o mutex lança AbandonedMutexException — a posse
    /// passa para nós e o trabalho continua.
    /// </summary>
    private static bool TryLock(TimeSpan timeout)
    {
        try { return WriteLock.WaitOne(timeout); }
        catch (AbandonedMutexException) { return true; }
    }

    public void Add(DeviceReport report)
    {
        if (!TryLock(TimeSpan.FromSeconds(5))) return; // outra instância presa — não bloqueia
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_file)!);
            File.AppendAllText(_file, JsonSerializer.Serialize(report, Opts) + Environment.NewLine);
            Prune();
        }
        finally { WriteLock.ReleaseMutex(); }
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
        // Ler a meio de uma poda noutro processo apanhava linhas truncadas.
        // Se o lock não vier em 5 s lê na mesma: as linhas são append-only e
        // as truncadas caem no try/catch do parser — melhor que UI congelada.
        var locked = TryLock(TimeSpan.FromSeconds(5));
        try
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
        finally { if (locked) WriteLock.ReleaseMutex(); }
    }

    /// <summary>Scans anteriores de um serial, mais antigo primeiro — base do antes/depois.</summary>
    public IReadOnlyList<DeviceReport> ForSerial(string serial) =>
        All()
            .Where(r => string.Equals(r.Device.Serial, serial, StringComparison.OrdinalIgnoreCase))
            .OrderBy(r => r.CollectedAt)
            .ToList();

    /// <summary>
    /// Nota quando o serial da célula mudou desde o scan anterior do mesmo
    /// aparelho: em iOS esse serial não se repete, logo é bateria trocada.
    /// </summary>
    public string? BatterySwappedSince(string serial, string? batterySerial)
    {
        if (string.IsNullOrWhiteSpace(serial) || string.IsNullOrWhiteSpace(batterySerial))
            return null;
        var previous = All()
            .Where(r => string.Equals(r.Device.Serial, serial, StringComparison.OrdinalIgnoreCase))
            .Where(r => r.Results.TryGetValue("battery.serial", out var c)
                && !string.IsNullOrWhiteSpace(c.Value))
            .OrderByDescending(r => r.CollectedAt)
            .FirstOrDefault();
        if (previous is null) return null;
        var old = previous.Results["battery.serial"].Value!;
        return string.Equals(old.Trim(), batterySerial.Trim(), StringComparison.OrdinalIgnoreCase)
            ? null
            : $"bateria substituída desde o scan de {previous.CollectedAt:dd-MM-yyyy}";
    }

    /// <summary>Battery capacity trend for a serial — the fun longitudinal stat.</summary>
    public IReadOnlyList<(DateTimeOffset at, string capacity)> BatteryTrend(string serial) =>
        All()
            .Where(r => string.Equals(r.Device.Serial, serial, StringComparison.OrdinalIgnoreCase)
                && r.Results.TryGetValue("battery.capacity", out _))
            .Select(r => (at: r.CollectedAt, capacity: r.Results["battery.capacity"].Value ?? "?"))
            .OrderBy(t => t.at)
            .ToList();
}
