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

    /// <summary>
    /// `service call iphonesubinfo` devolve um Parcel com o IMEI em ASCII
    /// intercalado com nulos — extrai os dígitos das colunas '....'.
    /// </summary>
    private static string? ParseImei(string? parcel)
    {
        if (parcel is null) return null;
        var digits = string.Concat(
            Regex.Matches(parcel, @"'([^']*)'")
                .Select(m => new string(m.Groups[1].Value.Where(char.IsDigit).ToArray())));
        return digits.Length is >= 14 and <= 17 ? digits[..15] : null;
    }

    public async Task<DeviceReport> CollectAsync(string serial)
    {
        var r = new DeviceReport { Platform = "android" };
        r.Device.Serial = serial;
        r.Device.Brand = await PropAsync(serial, "ro.product.brand");
        r.Device.Model = await PropAsync(serial, "ro.product.model");
        r.Device.Os = "Android";
        r.Device.OsVersion = await PropAsync(serial, "ro.build.version.release");

        // Nome comercial — prop de marketing do fabricante, senão tabela de códigos
        foreach (var prop in ModelNameTable.MarketingProps)
        {
            var name = ModelNameTable.Clean(await PropAsync(serial, prop), r.Device.Model);
            if (name is not null) { r.Device.MarketingName = name; break; }
        }
        r.Device.MarketingName ??= ModelNameTable.Lookup(r.Device.Model);

        r.Set("identity.model", "info",
            r.Device.MarketingName ?? r.Device.Model,
            r.Device.MarketingName is not null
                ? $"código: {r.Device.Model}"
                : "modelo por código — sem nome comercial conhecido");
        r.Set("identity.serial", "info", serial);
        r.Set("os.version", "info", $"{r.Device.Os} {r.Device.OsVersion}",
            $"SDK {await PropAsync(serial, "ro.build.version.sdk")}, patch {await PropAsync(serial, "ro.build.version.security_patch")}");
        r.Set("identity.fingerprint", "info", await PropAsync(serial, "ro.build.fingerprint"),
            "build exata do firmware — útil para confirmar stock vs ROM modificado");
        r.Set("device.baseband", "info", await PropAsync(serial, "gsm.version.baseband"),
            "firmware do modem — afeta rede/SIM");

        // SIM/carrier — estado de leitura do cartão e operadora detetada
        var simState = await PropAsync(serial, "gsm.sim.state");
        var carrier = await PropAsync(serial, "gsm.operator.alpha");
        r.Set("device.sim", simState == "READY" ? "pass" : "info", simState,
            string.IsNullOrWhiteSpace(carrier) ? null : $"operadora: {carrier}");

        // IMEI — service call funciona no shell de muitos dispositivos;
        // quando o fabricante bloqueia, sai como "info" sem quebrar o scan.
        var imei = ParseImei(await AdbAsync(serial, "shell service call iphonesubinfo 1"));
        if (imei is not null)
            r.Set("identity.imei", "info", imei);

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

        // SELinux + encriptação — flags de segurança para avaliação de retoma
        var selinux = (await AdbAsync(serial, "shell getenforce"))?.Trim();
        if (!string.IsNullOrWhiteSpace(selinux))
            r.Set("security.selinux", selinux == "Enforcing" ? "pass" : "warn", selinux,
                selinux == "Enforcing" ? "proteção de kernel ativa" : "SELinux desativado — software modificado");
        var crypto = await PropAsync(serial, "ro.crypto.state");
        if (!string.IsNullOrWhiteSpace(crypto))
            r.Set("security.crypto", crypto == "encrypted" ? "pass" : "info", crypto,
                "dados do utilizador encriptados em repouso");

        // Battery — dumpsys gives level/temp/health/charge-state; sysfs may
        // give cycles & capacity; batterystats gives the learned estimate.
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

        // Estado de carga + fonte — "a carregar por USB a X%" distingue
        // porta lenta de carregador de parede quando o cliente diz "não carrega".
        var battStatus = Match(batt, @"status: (\d+)");
        var battStatusText = battStatus switch
        {
            "2" => "a carregar", "3" => "a descarregar",
            "4" => "sem carga", "5" => "cheia", _ => null,
        };
        var plugged = Match(batt, @"plugged: (\d+)");
        var pluggedText = plugged switch
        {
            "1" => "AC", "2" => "USB", "4" => "wireless", _ => null,
        };
        if (battStatusText is not null)
            r.Set("battery.status", "info", battStatusText,
                pluggedText is null ? null : $"fonte: {pluggedText}");

        var voltage = Match(batt, @"voltage: (\d+)");
        if (double.TryParse(voltage, out var mv))
            r.Set("battery.voltage",
                mv is < 3400 or > 4400 ? "warn" : "info", $"{mv / 1000:0.00} V",
                mv is < 3400 or > 4400
                    ? "fora da faixa normal 3.4–4.4V"
                    : null);

        var tech = Match(batt, @"technology: (.+)");
        if (!string.IsNullOrWhiteSpace(tech))
            r.Set("battery.technology", "info", tech);

        // Carga restante medida pelo fuel gauge (µAh) — dado bruto, serve de
        // referência para degradacao entre scans (histórico local).
        var chargeCounter = Match(batt, @"Charge counter: (\d+)");
        if (long.TryParse(chargeCounter, out var cc) && cc > 0)
            r.Set("battery.charge_now", "info", $"{cc / 1000} mAh",
                "carga atual medida pelo fuel gauge");

        // Cycle count + real capacity where the vendor exposes it (Samsung, Pixel…)
        var cycles = (await AdbAsync(serial, "shell cat /sys/class/power_supply/battery/cycle_count"))?.Trim();
        var chargeFull = (await AdbAsync(serial, "shell cat /sys/class/power_supply/battery/charge_full"))?.Trim();
        var chargeDesign = (await AdbAsync(serial, "shell cat /sys/class/power_supply/battery/charge_full_design"))?.Trim();
        if (int.TryParse(cycles, out var c))
            r.Set("battery.cycles", c > 800 ? "warn" : "info", c.ToString(),
                c > 800 ? "acima de ~800 ciclos a degradação é esperada" : null);
        if (double.TryParse(chargeFull, NumberStyles.Float, CultureInfo.InvariantCulture, out var cf)
            && double.TryParse(chargeDesign, NumberStyles.Float, CultureInfo.InvariantCulture, out var cd) && cd > 0)
        {
            var pct = cf / cd * 100;
            r.Set("battery.capacity", pct >= 80 ? "pass" : pct >= 60 ? "warn" : "fail",
                $"{pct:0}%",
                $"Capacidade real {cf:0} / design {cd:0} — {(pct < 80 ? "bateria degradada" : "saúde boa")}");
        }

        // Capacidade aprendida pelo Android (batterystats) — fallback quando o
        // fabricante não expõe charge_full no sysfs.
        var stats = await AdbAsync(serial, "shell dumpsys batterystats | grep -m1 -i \"capacity\"", 10000);
        var learned = Match(stats, @"capacity:\s*([\d.,]+)");
        if (learned is not null && double.TryParse(
                learned.Replace(',', '.'), NumberStyles.Float,
                CultureInfo.InvariantCulture, out var learnedMah) && learnedMah > 0)
        {
            r.Set("battery.capacity_learned", "info", $"{learnedMah:0} mAh",
                "estimativa aprendida pelo Android — compara com a capacidade de design");
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

        // Logs de crash/ANR — o buffer -b crash do logcat guarda falhas de apps;
        // o dropbox acumula crashes/ANRs/WTFs contados por tag. São quase sempre
        // software (informativo) — falhas repetidas de componentes de sistema
        // é que sugerem hardware.
        await CollectLogsAsync(r, serial);

        r.Set("scan", "pass", "completo", $"Recolha via ADB em {(r.Device.Model ?? "dispositivo")}");
        return r;
    }

    /// <summary>Shell livre — usado pela bancada para aplicar/retirar carga (ecrã, brilho).</summary>
    public Task<string?> ShellAsync(string serial, string cmd, int timeoutMs = 8000)
        => AdbAsync(serial, $"shell {cmd}", timeoutMs);

    /// <summary>Leitura instantânea para o ticker — um só adb por tick.</summary>
    public async Task<LiveTelemetry?> ProbeAsync(string serial)
    {
        var o = await AdbAsync(serial,
            "shell cat /sys/class/power_supply/battery/current_now " +
            "/sys/class/power_supply/battery/voltage_now " +
            "/sys/class/power_supply/battery/temp " +
            "/sys/class/power_supply/battery/capacity 2>/dev/null", 5000);
        if (o is null) return null;
        var l = o.Split('\n', StringSplitOptions.RemoveEmptyEntries)
                 .Select(s => s.Trim()).ToArray();
        if (l.Length == 0) return null;

        int? mA = null; double? volts = null; double? tempC = null; int? pct = null;
        bool? charging = null;
        if (l.Length > 0 && long.TryParse(l[0], out var cur))
        {
            // drivers variam: µA ou mA — acima de ~20A assume µA
            var norm = Math.Abs(cur) > 20000 ? cur / 1000 : cur;
            mA = (int)Math.Abs(norm);
            // drivers com sinal: negativo a descarregar; sem sinal fica incerto
            charging = cur < 0 ? false : cur > 0 ? true : null;
        }
        if (l.Length > 1 && double.TryParse(l[1], out var uv))
            volts = uv > 100000 ? uv / 1_000_000 : uv / 1000; // µV ou mV
        if (l.Length > 2 && double.TryParse(l[2], out var tp))
            tempC = tp > 150 ? tp / 10 : tp; // décimos de °C
        if (l.Length > 3 && int.TryParse(l[3], out var cp))
            pct = cp;
        return new LiveTelemetry(mA, volts, tempC, pct, charging);
    }

    private async Task CollectLogsAsync(DeviceReport r, string serial)
    {
        var crashBuf = await AdbAsync(serial, "logcat -d -b crash -t 150", 10000);
        var appCrashes = crashBuf?.Split('\n')
            .Count(l => l.Contains("FATAL EXCEPTION")) ?? 0;

        var drop = await AdbAsync(serial,
            "shell dumpsys dropbox 2>/dev/null | grep -oE \"(data_app_crash|data_app_anr|data_app_wtf|system_app_crash|system_server_anr)\" | sort | uniq -c", 10000);
        var anrs = 0; var sysCrashes = 0;
        if (drop is not null)
        {
            anrs = drop.Split('\n')
                .Where(l => l.Contains("anr"))
                .Sum(l => int.TryParse(l.Trim().Split(' ')[0], out var n) ? n : 0);
            sysCrashes = drop.Split('\n')
                .Where(l => l.Contains("system_"))
                .Sum(l => int.TryParse(l.Trim().Split(' ')[0], out var n) ? n : 0);
        }

        if (crashBuf is not null && crashBuf.Length > 0)
            r.LogsText = crashBuf.Length > 40_000 ? crashBuf[^40_000..] : crashBuf;

        r.Set("logs.crashes",
            sysCrashes > 3 ? "warn" : "info",
            $"{appCrashes} app crashes · {anrs} ANRs · {sysCrashes} de sistema",
            sysCrashes > 3
                ? "falhas repetidas de componentes de sistema — investigar"
                : "crashes de apps = software, informativo — não é defeito de hardware");
    }
}
