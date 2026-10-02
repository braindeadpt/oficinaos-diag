using System.Diagnostics;
using System.Globalization;
using System.Text.RegularExpressions;

namespace OficinaDiag.Devices;

/// <summary>
/// Collects a DeviceReport from an Android phone over USB using the bundled
/// adb.exe. Requires USB debugging enabled on the device.
/// </summary>
public sealed class AndroidCollector
{
    private readonly string _adbPath;

    public AndroidCollector(string adbPath) => _adbPath = adbPath;

    private async Task<string?> AdbAsync(string serial, string args, int timeoutMs = 8000)
    {
        try
        {
            var psi = new ProcessStartInfo(_adbPath, $"-s {serial} {args}")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            using var p = Process.Start(psi);
            if (p is null) return null;
            var read = p.StandardOutput.ReadToEndAsync();
            if (!p.WaitForExit(timeoutMs)) { p.Kill(); return null; }
            return await read;
        }
        catch { return null; }
    }

    private async Task<string?> PropAsync(string serial, string name) =>
        (await AdbAsync(serial, $"shell getprop {name}"))?.Trim();

    private static string? Match(string? text, string pattern) =>
        text is null ? null : Regex.Match(text, pattern).Groups[1].Value.Trim();

    public async Task<DeviceReport> CollectAsync(string serial)
    {
        var r = new DeviceReport { Platform = "android" };
        r.Device.Serial = serial;
        r.Device.Brand = await PropAsync(serial, "ro.product.brand");
        r.Device.Model = await PropAsync(serial, "ro.product.model");
        r.Device.Os = "Android";
        r.Device.OsVersion = await PropAsync(serial, "ro.build.version.release");

        r.Set("identity.model", "info", r.Device.Model);
        r.Set("identity.serial", "info", serial);
        r.Set("os.version", "info", $"{r.Device.Os} {r.Device.OsVersion}",
            $"SDK {await PropAsync(serial, "ro.build.version.sdk")}, patch {await PropAsync(serial, "ro.build.version.security_patch")}");

        // Bootloader / tamper indicators
        var vbState = await PropAsync(serial, "ro.boot.verifiedbootstate");
        var buildTags = await PropAsync(serial, "ro.build.tags");
        r.Set("security.bootloader", vbState == "green" ? "pass" : "warn", vbState,
            "Bootloader verification state (green = stock, locked)");
        if (buildTags?.Contains("test-keys") == true)
            r.Set("security.build", "warn", buildTags, "Test-keys build — software modificado");
        else
            r.Set("security.build", "pass", buildTags);
        var su = await AdbAsync(serial, "shell which su");
        if (!string.IsNullOrWhiteSpace(su))
            r.Set("security.root", "warn", "su encontrado", "O equipamento pode ter root");

        // Battery — dumpsys gives level/temp/health flag; sysfs may give cycles & capacity
        var batt = await AdbAsync(serial, "shell dumpsys battery");
        var level = Match(batt, @"level: (\d+)");
        var temp = Match(batt, @"temperature: (\d+)");
        var health = Match(batt, @"health: (\d+)");
        var healthText = health switch { "2" => "good", "3" => "overheat", "4" => "dead", "5" => "over-voltage", "6" => "failure", _ => health };
        r.Set("battery.level", "info", level is null ? null : $"{level}%");
        r.Set("battery.temperature",
            double.TryParse(temp, out var t) && t / 10 > 40 ? "warn" : "info",
            temp is null ? null : $"{t / 10:0.#}°C");
        r.Set("battery.health", healthText == "good" ? "pass" : "warn", healthText);

        // Cycle count + real capacity where the vendor exposes it (Samsung, Pixel…)
        var cycles = (await AdbAsync(serial, "shell cat /sys/class/power_supply/battery/cycle_count"))?.Trim();
        var chargeFull = (await AdbAsync(serial, "shell cat /sys/class/power_supply/battery/charge_full"))?.Trim();
        var chargeDesign = (await AdbAsync(serial, "shell cat /sys/class/power_supply/battery/charge_full_design"))?.Trim();
        if (int.TryParse(cycles, out var c))
            r.Set("battery.cycles", c > 800 ? "warn" : "info", c.ToString());
        if (double.TryParse(chargeFull, NumberStyles.Float, CultureInfo.InvariantCulture, out var cf)
            && double.TryParse(chargeDesign, NumberStyles.Float, CultureInfo.InvariantCulture, out var cd) && cd > 0)
        {
            var pct = cf / cd * 100;
            r.Set("battery.capacity", pct >= 80 ? "pass" : pct >= 60 ? "warn" : "fail",
                $"{pct:0}%",
                $"Capacidade real {cf:0} / design {cd:0} — {(pct < 80 ? "bateria degradada" : "saúde boa")}");
        }

        // Storage
        var df = await AdbAsync(serial, "shell df /data");
        var dfLine = df?.Split('\n').LastOrDefault(l => l.Contains('%'));
        if (dfLine is not null)
        {
            var cols = Regex.Split(dfLine.Trim(), @"\s+");
            if (cols.Length >= 6)
            {
                var pctUsed = cols[^2];
                // "info": disco cheio é uso normal do cliente, não defeito
                r.Set("storage", "info",
                    $"{pctUsed} usado", $"livre: {cols[^3]} KB — utilização normal, não é defeito de hardware");
            }
        }

        // RAM
        var mem = await AdbAsync(serial, "shell cat /proc/meminfo");
        var memKb = Match(mem, @"MemTotal:\s+(\d+)");
        if (long.TryParse(memKb, out var kb))
            r.Set("memory", "info", $"{kb / 1048576.0:0.#} GB RAM");

        // Screen
        var wmSize = Match(await AdbAsync(serial, "shell wm size"), @"(\d+x\d+)");
        r.Set("display", "info", wmSize, "Resolução física — teste visual no menu de testes");

        // Sensors inventory
        var sensors = await AdbAsync(serial, "shell dumpsys sensorservice");
        var sensorCount = sensors?.Split('\n').Count(l => Regex.IsMatch(l, @"^\s*0x[0-9a-f]+\)")) ?? 0;
        r.Set("sensors", "info", $"{sensorCount} detetados", "Inventário — falhas individuais só em teste manual");

        // Uptime / last reboot
        var uptime = (await AdbAsync(serial, "shell uptime"))?.Trim();
        if (!string.IsNullOrWhiteSpace(uptime))
            r.Raw["uptime"] = uptime;

        r.Set("scan", "pass", "completo", $"Recolha via ADB em {(r.Device.Model ?? "dispositivo")}");
        return r;
    }
}
