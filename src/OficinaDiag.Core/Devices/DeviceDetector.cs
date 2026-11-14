using System.Diagnostics;
using System.IO;
using System.Text.RegularExpressions;
using OficinaDiag.Log;

namespace OficinaDiag.Devices;

public enum DeviceKind { Android, Ios }

/// <param name="State">ready | unauthorized | recovery | sideload | fastboot | offline</param>
public sealed record DetectedDevice(DeviceKind Kind, string Id, string Label, string State = "ready");

/// <summary>
/// Polls adb and libimobiledevice for connected devices and raises events on
/// attach/detach. Pure polling — simple and robust on Windows.
/// </summary>
public sealed class DeviceDetector : IDisposable
{
    private readonly string _adbPath;
    private readonly IosCollector _ios = new();
    private readonly Timer _timer;
    private readonly Dictionary<string, DetectedDevice> _seen = new();
    private int _polling;

    public event Action<DetectedDevice>? Attached;
    public event Action<DetectedDevice>? Detached;

    public bool AdbAvailable => File.Exists(_adbPath);

    public DeviceDetector(string adbPath)
    {
        _adbPath = adbPath;
        _timer = new Timer(_ => Poll(), null, 0, 2000);
    }

    private List<DetectedDevice> Current()
    {
        var found = new List<DetectedDevice>();

        if (AdbAvailable)
        {
            try
            {
                var psi = new ProcessStartInfo(_adbPath, "devices")
                {
                    RedirectStandardOutput = true,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                };
                using var p = Process.Start(psi);
                if (p is not null)
                {
                    var readTask = p.StandardOutput.ReadToEndAsync();
                    if (!p.WaitForExit(5000))
                    {
                        try { p.Kill(entireProcessTree: true); } catch { }
                        return found;
                    }
                    var output = readTask.GetAwaiter().GetResult();
                    // O estado importa: `recovery`/`sideload`/`unauthorized` não
                    // têm OS completo, por isso o scan tem de os tratar à parte.
                    foreach (Match m in Regex.Matches(output, @"^([\w.:@-]+)\t(\w+)$", RegexOptions.Multiline))
                    {
                        var id = m.Groups[1].Value;
                        var state = m.Groups[2].Value;
                        found.Add(state switch
                        {
                            "device" => new DetectedDevice(DeviceKind.Android, id, "Android"),
                            "unauthorized" => new DetectedDevice(DeviceKind.Android, id + "!",
                                "Android (autorizar no telefone)", "unauthorized"),
                            "recovery" => new DetectedDevice(DeviceKind.Android, id + "!",
                                "Android em recovery", "recovery"),
                            "sideload" => new DetectedDevice(DeviceKind.Android, id + "!",
                                "Android em sideload", "sideload"),
                            _ => new DetectedDevice(DeviceKind.Android, id + "!",
                                $"Android ({state})", state),
                        });
                    }
                    // `fastboot devices` custa um processo: só vale a pena
                    // quando nenhum Android chegou a `ready` (bootloader/recovery).
                    if (!found.Any(d => d.State == "ready")) found.AddRange(FastbootDevices());
                }
            }
            catch { /* adb falhou — ignora nesta ronda */ }
        }

        try
        {
            foreach (var udid in _ios.ListDevices())
                found.Add(new DetectedDevice(DeviceKind.Ios, udid, "iPhone/iPad"));
        }
        catch { /* usbmuxd indisponível */ }

        return found;
    }

    /// <summary>Snapshot do último polling — alimenta o seletor multi-dispositivo.</summary>
    public IReadOnlyList<DetectedDevice> ConnectedDevices
    {
        get { lock (_seen) return _seen.Values.ToArray(); }
    }

    /// <summary>
    /// `fastboot devices` apanha Androids presos no bootloader — o adb não os
    /// vê. Só corre quando o `fastboot.exe` está ao lado do adb (tools/).
    /// </summary>
    private IEnumerable<DetectedDevice> FastbootDevices()
    {
        var exe = Path.Combine(Path.GetDirectoryName(_adbPath) ?? "", "fastboot.exe");
        if (!File.Exists(exe)) yield break;
        var output = RunFastboot(exe);
        if (output is null) yield break;
        foreach (Match m in Regex.Matches(output, @"^([\w.:@-]+)\tfastboot$", RegexOptions.Multiline))
            yield return new DetectedDevice(DeviceKind.Android, m.Groups[1].Value + "!",
                "Android em fastboot", "fastboot");
    }

    /// <summary>2 s chegam: um fastboot vivo responde de imediato.</summary>
    private static string? RunFastboot(string exe)
    {
        try
        {
            var psi = new ProcessStartInfo(exe, "devices")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            using var p = Process.Start(psi);
            if (p is null) return null;
            var read = p.StandardOutput.ReadToEndAsync();
            if (!p.WaitForExit(2000))
            {
                try { p.Kill(entireProcessTree: true); } catch { }
                return null;
            }
            return read.GetAwaiter().GetResult();
        }
        catch { return null; }
    }

    private void Poll()
    {
        // Timer callbacks overlap on the thread pool — a slow adb/iOS scan
        // (~5s) would re-enter while a previous poll still runs. _seen is
        // not thread-safe and double events would fire, so skip the round.
        if (Interlocked.Exchange(ref _polling, 1) == 1)
            return;
        try
        {
            // O mesmo UDID pode vir enumerado por dois transportes na mesma
            // ronda (ex.: iPhone emparelhado por USB e por Wi-Fi) — um
            // ToDictionary direto rebentava com duplicate key e a ronda
            // inteira era perdida. Último enumerado fica.
            var now = new Dictionary<string, DetectedDevice>();
            foreach (var d in Current()) now[d.Id] = d;
            lock (_seen)
            {
                foreach (var (id, d) in now)
                    if (_seen.Remove(id)) _seen[id] = d;   // refresca label (unauthorized→device)
                    else { _seen[id] = d; Attached?.Invoke(d); }
                foreach (var (id, d) in _seen.ToArray())
                    if (!now.ContainsKey(id)) { _seen.Remove(id); Detached?.Invoke(d); }
            }
        }
        catch (Exception ex)
        {
            // An exception escaping a Timer callback terminates the process.
            // A throwing event handler (e.g. dispatcher already shut down)
            // must not kill the app — log and keep polling.
            AppLog.Exception("DeviceDetector.Poll", ex);
        }
        finally
        {
            Volatile.Write(ref _polling, 0);
        }
    }

    public void Dispose() => _timer.Dispose();
}
