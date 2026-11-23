using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using OficinaDiag.Devices;

namespace OficinaDiag.History;

/// <summary>Local JSON-lines history of past scans — offline, no account.</summary>
public sealed class ScanHistory
{
    private readonly string _file;

    /// <summary>
    /// Append+poda intercalados por duas instâncias (ex.: app + segundo
    /// processo) perdiam linhas: a poda lia o ficheiro, outro processo
    /// escrevia, o Move sobrescrevia a linha nova. Um named mutex (nome
    /// derivado do caminho, ver <see cref="LockNameFor"/>) torna a sequência
    /// atómica entre processos — e só entre os que partilham o ficheiro.
    /// </summary>
    private readonly Mutex _writeLock;

    public ScanHistory(string? file = null)
    {
        _file = file ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "OficinaDiag", "history.jsonl");
        _writeLock = new Mutex(initiallyOwned: false, LockNameFor(_file));
    }

    /// <summary>
    /// Nome do mutex derivado do caminho: duas instâncias sobre o mesmo
    /// histórico contendem (é o que serializa append+poda entre processos),
    /// mas dois históricos diferentes não se bloqueiam um ao outro.
    /// </summary>
    internal static string LockNameFor(string file)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(Path.GetFullPath(file)));
        return "OficinaDiag.ScanHistory." + Convert.ToHexString(hash, 0, 8);
    }

    private static readonly JsonSerializerOptions Opts = new() { WriteIndented = false };

    /// <summary>
    /// Mutex com timeout: uma instância presa não pode congelar a UI. Dono
    /// anterior morto com o mutex lança AbandonedMutexException — a posse
    /// passa para nós e o trabalho continua.
    /// </summary>
    private bool TryLock(TimeSpan timeout)
    {
        try { return _writeLock.WaitOne(timeout); }
        catch (AbandonedMutexException) { return true; }
    }

    /// <summary>
    /// Dias de retenção do histórico (RGPD, Opções). 0 = sem limite. Aplica-se
    /// na escrita e na leitura, para baixar o limite ter efeito imediato.
    /// </summary>
    public int RetentionDays { get; set; }

    public void Add(DeviceReport report)
    {
        if (!TryLock(TimeSpan.FromSeconds(5))) return; // outra instância presa — não bloqueia
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_file)!);
            File.AppendAllText(_file, JsonSerializer.Serialize(report, Opts) + Environment.NewLine);
            PruneExpired();
            Prune();
        }
        finally { _writeLock.ReleaseMutex(); }
    }

    /// <summary>
    /// Regrava a entrada de histórico que corresponde a este relatório.
    /// Os resultados manuais (checklist, teste no ecrã, bancada, perfil de
    /// desempenho) são gravados DEPOIS do scan — sem isto a cópia em disco
    /// ficava congelada no momento do scan e o antes/depois não via o
    /// "antes" da manutenção. Casa por CollectedAt+serial (o timestamp do
    /// scan é único); tmp+rename como no Add — crash a meio não trunca.
    /// Devolve true se uma entrada foi atualizada.
    /// </summary>
    public bool Update(DeviceReport report)
    {
        if (!TryLock(TimeSpan.FromSeconds(5))) return false;
        try
        {
            if (!File.Exists(_file)) return false;
            var lines = File.ReadAllLines(_file);
            var found = false;
            for (var i = 0; i < lines.Length; i++)
            {
                if (string.IsNullOrWhiteSpace(lines[i])) continue;
                DeviceReport? r = null;
                try { r = JsonSerializer.Deserialize<DeviceReport>(lines[i]); }
                catch { /* linha corrompida — fica como está */ }
                if (r is null || r.CollectedAt != report.CollectedAt) continue;
                if (!string.Equals(r.Device.Serial, report.Device.Serial,
                        StringComparison.OrdinalIgnoreCase)) continue;
                lines[i] = JsonSerializer.Serialize(report, Opts);
                found = true;
                break; // uma entrada por scan — o timestamp é único
            }
            if (!found) return false;
            var tmp = _file + ".tmp";
            File.WriteAllLines(tmp, lines);
            File.Move(tmp, _file, overwrite: true);
            return true;
        }
        finally { _writeLock.ReleaseMutex(); }
    }

    /// <summary>Remove entradas mais velhas que a retenção. Chamar com o lock tomado.</summary>
    private void PruneExpired()
    {
        if (RetentionDays <= 0) return;
        try
        {
            if (!File.Exists(_file)) return;
            var cutoff = DateTimeOffset.UtcNow.AddDays(-RetentionDays);
            var keep = new List<string>();
            var changed = false;
            foreach (var line in File.ReadLines(_file))
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                try
                {
                    var r = JsonSerializer.Deserialize<DeviceReport>(line);
                    if (r is not null && r.CollectedAt < cutoff) { changed = true; continue; }
                }
                catch { /* linha corrompida cai fora na mesma */ changed = true; continue; }
                keep.Add(line);
            }
            if (!changed) return;
            var tmp = _file + ".tmp";
            File.WriteAllLines(tmp, keep);
            File.Move(tmp, _file, overwrite: true);
        }
        catch { /* poda falhou — fica para a próxima escrita */ }
    }

    /// <summary>RGPD: apaga o histórico local inteiro (IMT/seriais de clientes).</summary>
    public void ClearAll()
    {
        if (!TryLock(TimeSpan.FromSeconds(5))) return;
        try { if (File.Exists(_file)) File.Delete(_file); }
        catch { /* sem disco — nada a apagar */ }
        finally { try { _writeLock.ReleaseMutex(); } catch { } }
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
            // Retenção aplicada também na leitura: mudar 90 → 30 dias nas
            // Opções tem de esconder já o que passou do limite.
            if (RetentionDays > 0)
            {
                var cutoff = DateTimeOffset.UtcNow.AddDays(-RetentionDays);
                list.RemoveAll(r => r.CollectedAt < cutoff);
            }
            return list;
        }
        finally { if (locked) _writeLock.ReleaseMutex(); }
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
