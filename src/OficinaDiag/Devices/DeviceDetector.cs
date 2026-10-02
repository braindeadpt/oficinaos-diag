using System.Diagnostics;
using System.IO;
using System.Text.RegularExpressions;

namespace OficinaDiag.Devices;

public enum DeviceKind { Android, Ios }

public sealed record DetectedDevice(DeviceKind Kind, string Id, string Label);

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
                        try { p.Kill(); } catch { }
                        return found;
                    }
                    var output = readTask.GetAwaiter().GetResult();
                    foreach (Match m in Regex.Matches(output, @"^([\w.:@-]+)\tdevice$", RegexOptions.Multiline))
                        found.Add(new DetectedDevice(DeviceKind.Android, m.Groups[1].Value, "Android"));
                    foreach (Match m in Regex.Matches(output, @"^([\w.:@-]+)\tunauthorized$", RegexOptions.Multiline))
                        found.Add(new DetectedDevice(DeviceKind.Android, m.Groups[1].Value + "!", "Android (autorizar no telefone)"));
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

    private void Poll()
    {
        var now = Current().ToDictionary(d => d.Id);
        foreach (var (id, d) in now)
            if (_seen.Remove(id)) _seen[id] = d;   // refresca label (unauthorized→device)
            else { _seen[id] = d; Attached?.Invoke(d); }
        foreach (var (id, d) in _seen.ToArray())
            if (!now.ContainsKey(id)) { _seen.Remove(id); Detached?.Invoke(d); }
    }

    public void Dispose() => _timer.Dispose();
}
