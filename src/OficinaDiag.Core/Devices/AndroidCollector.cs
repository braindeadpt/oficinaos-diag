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
        // O serial vem do adb, mas não é interpolado numa linha de comando:
        // ArgumentList evita injecção e aspas perdidas em serials estranhos.
        if (string.IsNullOrWhiteSpace(serial)
            || serial.Any(c => !char.IsLetterOrDigit(c) && c is not ('-' or '_' or '.' or ':')))
            return null;
        try
        {
            var psi = new ProcessStartInfo(_adbPath)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            psi.ArgumentList.Add("-s");
            psi.ArgumentList.Add(serial);
            if (args.StartsWith("shell ", StringComparison.Ordinal))
            {
                // O comando shell vai como UM argumento: pipes e aspas voltam a
                // ser interpretados pelo sh do telefone, como antes.
                psi.ArgumentList.Add("shell");
                psi.ArgumentList.Add(args[6..]);
            }
            else
            {
                foreach (var a in args.Split(' ', StringSplitOptions.RemoveEmptyEntries))
                    psi.ArgumentList.Add(a);
            }
            using var p = Process.Start(psi);
            if (p is null) return null;
            var read = p.StandardOutput.ReadToEndAsync();
            var drain = p.StandardError.ReadToEndAsync(); // stderr cheio bloqueava o adb
            if (!p.WaitForExit(timeoutMs))
            {
                try { p.Kill(entireProcessTree: true); } catch { }
                return null;
            }
            return await read;
        }
        catch { return null; }
    }

    /// <summary>
    /// Prop do Android. Vazio → null: o `getprop` de prop inexistente imprime
    /// só uma linha vazia, e um "" a jusante virava check `warn` sem valor.
    /// </summary>
    private async Task<string?> PropAsync(string serial, string name)
    {
        var v = (await AdbAsync(serial, $"shell getprop {name}"))?.Trim();
        return string.IsNullOrEmpty(v) ? null : v;
    }

    /// <summary>Grupo 1 do regex, ou null quando não casa — "" nunca é dado.</summary>
    private static string? Match(string? text, string pattern)
    {
        if (text is null) return null;
        var m = Regex.Match(text, pattern);
        if (!m.Success) return null;
        var v = m.Groups[1].Value.Trim();
        return v.Length == 0 ? null : v;
    }

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
        // Props ausentes não viram linha em branco no relatório.
        if (await PropAsync(serial, "ro.build.fingerprint") is { } fingerprint)
            r.Set("identity.fingerprint", "info", fingerprint,
                "build exata do firmware — útil para confirmar stock vs ROM modificado");
        if (await PropAsync(serial, "gsm.version.baseband") is { } baseband)
            r.Set("device.baseband", "info", baseband, "firmware do modem — afeta rede/SIM");

        // SIM/carrier — PIN/PUK do cartão e lock de operadora saltam aqui
        // (NETWORK_LOCKED/PERM_DISABLED = telemóvel preso a uma operadora —
        // decisivo para retoma; PIN_REQUIRED = PIN do cartão do cliente).
        var simState = await PropAsync(serial, "gsm.sim.state");
        var carrier = await PropAsync(serial, "gsm.operator.alpha");
        var simLocked = simState?.Split(',')
            .Any(s => s.Trim() is "PIN_REQUIRED" or "PUK_REQUIRED"
                or "NETWORK_LOCKED" or "PERM_DISABLED") == true;
        var isCarrierLock = simState?.Split(',')
            .Any(s => s.Trim() is "NETWORK_LOCKED" or "PERM_DISABLED") == true;
        if (simState is not null)
            r.Set("device.sim", isCarrierLock ? "fail" : simLocked ? "warn" : "info",
                simState,
            string.Join(" — ", new[]
            {
                isCarrierLock ? "lock de operadora — não aceita SIM de outras redes"
                    : simLocked ? "cartão pede PIN/PUK — não é defeito do telefone" : null,
                string.IsNullOrWhiteSpace(carrier) ? null : $"operadora: {carrier}",
                }.Where(s => s is not null)));

        // IMEI — service call funciona no shell de muitos dispositivos;
        // quando o fabricante bloqueia, sai como "info" sem quebrar o scan.
        var imei = ParseImei(await AdbAsync(serial, "shell service call iphonesubinfo 1"));
        if (imei is not null)
            r.Set("identity.imei", "info", imei);

        // Bootloader / tamper indicators
        var vbState = await PropAsync(serial, "ro.boot.verifiedbootstate");
        var buildTags = await PropAsync(serial, "ro.build.tags");
        r.Set("security.bootloader",
            vbState is null ? "skipped" : vbState == "green" ? "pass" : "warn",
            vbState ?? "prop ausente",
            vbState is null
                ? "ro.boot.verifiedbootstate não exposto neste Android — não verificável"
                : "Bootloader verification state (green = stock, locked)");
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

        // ── Flags de bloqueio — o que decide uma retoma de usado ──────────
        // Contas Google/Samsung ativas: não é defeito (todo o telefone de um
        // cliente as tem), mas um factory reset dispara FRP e o aparelho fica
        // preso à conta — bloqueia revenda até serem desvinculadas.
        var accounts = await AdbAsync(serial, "shell dumpsys account");
        if (accounts is not null)
        {
            var accCount = AdbParsing.ParseAccountCount(accounts) ?? 0;
            r.Set("security.accounts", accCount > 0 ? "warn" : "pass",
                accCount == 0 ? "sem contas" : $"{accCount} conta(s)",
                accCount > 0
                    ? "conta(s) do fabricante/Google ativas — um reset dispara FRP; desvincular antes de vender"
                    : "sem contas — FRP não dispara num reset");
        }

        // MDM: device/profile owner = gerido por empresa ou preso a leasing —
        // red flag máxima para retoma.
        var policy = await AdbAsync(serial, "shell dumpsys device_policy");
        if (policy is not null)
        {
            // "Device Owner Type: -1" é o dump de um Android SEM gestão: a
            // substring "Device Owner" dava falso positivo e, como security.mdm
            // está nos HardLocks do Grader, capava o grau em D.
            var hasOwner = AdbParsing.HasDeviceOwner(policy);
            r.Set("security.mdm", hasOwner ? "warn" : "pass",
                hasOwner ? "gestão remota ativa" : "sem gestão remota",
                hasOwner
                    ? "device/profile owner definido — pode estar bloqueado a empresa ou contrato de leasing"
                    : null);
        }

        // Knox / warranty bit + OEM unlock — o que o fabricante usa para
        // recusar garantia numa retoma.
        var warrantyBit = (await PropAsync(serial, "ro.boot.warranty_bit"))
            ?? await PropAsync(serial, "ro.warranty_bit");
        if (warrantyBit is "0" or "1")
            r.Set("security.warranty", warrantyBit == "1" ? "fail" : "pass",
                warrantyBit == "1" ? "Knox queimado (0x1)" : "intacto",
                warrantyBit == "1"
                    ? "garantia oficial perdida — o fabricante recusa reparação"
                    : null);
        var flashLocked = await PropAsync(serial, "ro.boot.flash.locked"); // 1 = bloqueado
        var oemUnlock = await PropAsync(serial, "sys.oem_unlock_allowed"); // 1 = permitido
        if (flashLocked == "0" || oemUnlock == "1")
            r.Set("security.oemunlock", "warn",
                flashLocked == "0" ? "bootloader desbloqueado" : "desbloqueio OEM permitido",
                "software modificável — afeta garantia e valor de retoma");
        else if (flashLocked == "1" || oemUnlock == "0")
            r.Set("security.oemunlock", "pass", "bootloader bloqueado");

        // Desgaste NAND (eMMC life_time / pre_eol) — raro sem root, mas quando
        // exposto é um aviso "vai morrer" que nenhuma app de cliente mostra.
        var nand = (await AdbAsync(serial,
            "shell cat /sys/block/mmcblk0/device/life_time " +
            "/sys/class/mmc_host/mmc0/mmc0:0001/life_time 2>/dev/null"))?.Trim();
        var life = Regex.Matches(nand ?? "", @"0x([0-9A-Fa-f]{2})")
            .Select(m => Convert.ToInt32(m.Groups[1].Value, 16)).ToList();
        if (life.Count > 0)
        {
            var worst = life.Max(); // classes 1–11, ~10% de vida gasta por classe
            r.Set("storage.wear", worst >= 9 ? "warn" : "info",
                $"classe {worst}/11 (~{Math.Min(100, worst * 10)}% de vida gasta)",
                "desgaste estimado da NAND reportado pelo controlador eMMC");
        }

        await CollectBatteryAsync(r, serial);

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
        if (wmSize is not null)
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

        // Se nem modelo nem OS vieram, o aparelho não respondeu (unauthorized,
        // adb morto, driver) — reportar "completo" aqui escondia uma recolha vazia.
        if (r.Device.Model is null && r.Device.Brand is null && r.Device.OsVersion is null)
            r.Set("scan", "fail", "sem resposta",
                "o aparelho não respondeu ao adb — verifica a autorização USB no telefone");
        else
            r.Set("scan", "pass", "completo",
                $"Recolha via ADB em {(r.Device.Model ?? "dispositivo")}");
        return r;
    }

    /// <summary>
    /// Bateria: dumpsys dá nível/temperatura/saúde/estado de carga, sysfs dá
    /// ciclos e capacidade e o batterystats a estimativa aprendida. Sem
    /// bateria (`present: false`, bancada/emulador) nada disto é medível —
    /// fica `skipped` em vez de inventar 0 °C, 0,00 V ou "estado 1".
    /// </summary>
    private async Task CollectBatteryAsync(DeviceReport r, string serial)
    {
        var batt = await AdbAsync(serial, "shell dumpsys battery");
        if (AdbParsing.ParsePresent(batt) == false)
        {
            r.Set("battery.present", "skipped", "sem bateria exposta",
                "dumpsys battery reporta present=false — nível, temperatura e tensão não são medíveis aqui");
            return;
        }

        var level = Match(batt, @"level: (\d+)");
        if (level is not null)
            r.Set("battery.level", "info", $"{level}%");

        // 0 é ausência de leitura, não 0 °C.
        var temp = Match(batt, @"temperature: (\d+)");
        var tempOk = AdbParsing.TryDouble(temp, out var t) && t > 0;
        if (tempOk)
            r.Set("battery.temperature",
                t / 10 > 40 ? "warn" : "info", $"{t / 10:0.#}°C");

        // `health: 1` é BATTERY_HEALTH_UNKNOWN — ausência de dado, não defeito.
        var health = Match(batt, @"health: (\d+)");
        var healthText = health switch
        {
            "2" => "good", "3" => "overheat", "4" => "dead",
            "5" => "over-voltage", "6" => "failure", _ => null,
        };
        if (health is "1")
            r.Set("battery.health", "skipped", "desconhecido (UNKNOWN)",
                "o kernel não reporta saúde da bateria — não é defeito");
        else if (healthText is not null)
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
        if (AdbParsing.TryDouble(voltage, out var mv) && mv > 0)
            r.Set("battery.voltage",
                mv is < 3400 or > 4400 ? "warn" : "info",
                $"{mv / 1000:0.00} V",
                mv is < 3400 or > 4400
                    ? "fora da faixa normal 3.4–4.4V"
                    : null);

        var tech = Match(batt, @"technology: (.+)");
        if (!string.IsNullOrWhiteSpace(tech))
            r.Set("battery.technology", "info", tech);

        // Carga restante medida pelo fuel gauge (µAh) — dado bruto, serve de
        // referência para degradacao entre scans (histórico local).
        var chargeCounter = Match(batt, @"Charge counter: (\d+)");
        if (AdbParsing.TryLong(chargeCounter, out var cc) && cc > 0)
            r.Set("battery.charge_now", "info", $"{cc / 1000} mAh",
                "carga atual medida pelo fuel gauge");

        // Ciclos + capacidade real onde o fabricante expõe (Samsung, Pixel…).
        var cycles = await AdbAsync(serial, "shell cat /sys/class/power_supply/battery/cycle_count");
        var chargeFull = await AdbAsync(serial, "shell cat /sys/class/power_supply/battery/charge_full");
        var chargeDesign = await AdbAsync(serial, "shell cat /sys/class/power_supply/battery/charge_full_design");
        if (AdbParsing.TryInt(cycles?.Trim(), out var c))
            r.Set("battery.cycles", c > 800 ? "warn" : "info",
                c.ToString(CultureInfo.InvariantCulture),
                c > 800 ? "acima de ~800 ciclos a degradação é esperada" : null);

        double? designMah = null;
        if (AdbParsing.TryDouble(chargeDesign?.Trim(), out var cd) && cd > 0)
            designMah = AdbParsing.NormalizeMah(cd);
        if (AdbParsing.TryDouble(chargeFull?.Trim(), out var cf) && cf > 0 && designMah is > 0)
        {
            var cfMah = AdbParsing.NormalizeMah(cf);
            var pct = cfMah / designMah.Value * 100;
            r.Set("battery.capacity", pct >= 80 ? "pass" : pct >= 60 ? "warn" : "fail",
                $"{pct:0}%",
                $"Capacidade real {cfMah:0} / design {designMah.Value:0} mAh — " +
                (pct < 80 ? "bateria degradada" : "saúde boa"));
        }

        // Capacidade aprendida pelo Android (batterystats) — fallback quando o
        // fabricante não expõe charge_full. O 1000 mAh que o Android estima
        // sem célula é o default, não uma medição.
        var stats = await AdbAsync(serial, "shell dumpsys batterystats | grep -m1 -i \"capacity\"", 10000);
        var learned = Match(stats, @"capacity:\s*([\d.,]+)");
        if (learned is not null
            && double.TryParse(learned.Replace(',', '.'), NumberStyles.Float,
                CultureInfo.InvariantCulture, out var learnedMah)
            && learnedMah > 0
            && !(learnedMah == 1000 && designMah is null))
        {
            r.Set("battery.capacity_learned", "info", $"{learnedMah:0} mAh",
                "estimativa aprendida pelo Android — compara com a capacidade de design");
        }
    }

    /// <summary>Shell livre — usado pela bancada para aplicar/retirar carga (ecrã, brilho).</summary>
    public Task<string?> ShellAsync(string serial, string cmd, int timeoutMs = 8000)
        => AdbAsync(serial, $"shell {cmd}", timeoutMs);

    /// <summary>Leitura instantânea para o ticker — um só adb por tick.</summary>
    public async Task<LiveTelemetry?> ProbeAsync(string serial)
    {
        // `status` vem junto com as leituras e decide a direção: Samsung/Exynos,
        // MediaTek e Huawei reportam corrente negativa a carregar.
        var o = await AdbAsync(serial,
            "shell for f in current_now voltage_now temp status capacity; " +
            "do cat /sys/class/power_supply/battery/$f 2>/dev/null || echo _; done", 5000);
        if (o is null) return null;

        var (mA, charging, volts, tempC, pct) = AdbParsing.ParseBatteryProbe(o);
        if (mA is null && volts is null && pct is null) return null;
        return new LiveTelemetry(mA, volts, tempC, pct, charging);
    }

    /// <summary>
    /// Zonas térmicas via sysfs (uma shell call, mais leve que dumpsys) —
    /// a bancada cruza a subida da célula com a do SoC para distinguir
    /// "bateria a trabalhar" de "throttling do processador".
    /// </summary>
    public async Task<double?> ProbeSocTempAsync(string serial)
    {
        var o = await AdbAsync(serial,
            "shell for z in /sys/class/thermal/thermal_zone*; " +
            "do echo $(cat $z/type 2>/dev/null):$(cat $z/temp 2>/dev/null); done", 5000);
        return AdbParsing.ParseSocTemp(o);
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
