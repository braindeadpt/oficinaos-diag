using System.Diagnostics;
using System.IO;
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
    private readonly bool _securityAudit;
    private readonly bool _bugreportConsent;
    private CancellationToken _ct;

    /// <param name="securityAudit">
    /// Falso no scan gratuito: a auditoria de segurança das apps é Pro
    /// (decisão do dono) e sem ela o scan poupa os dumpsys extra.
    /// </param>
    /// <param name="bugreportConsent">
    /// Consentimento explícito do operador para recolher o `adb bugreport`
    /// — contém dados pessoais do dono do telefone. Sem ele, as contagens
    /// profundas de crashes não correm (o dropbox/logcat sem dados
    /// pessoais continuam). Da contagem saem só tipos e quantidades.
    /// </param>
    public AndroidCollector(string adbPath, bool securityAudit = true, bool bugreportConsent = false)
    {
        _adbPath = adbPath;
        _securityAudit = securityAudit;
        _bugreportConsent = bugreportConsent;
    }

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
            using var timeout = new CancellationTokenSource(timeoutMs);
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(_ct, timeout.Token);
            try { await p.WaitForExitAsync(linked.Token); }
            catch (OperationCanceledException)
            {
                try { p.Kill(entireProcessTree: true); } catch { }
                if (_ct.IsCancellationRequested) throw;
                return null;
            }
            return await read;
        }
        catch (OperationCanceledException) { throw; }
        catch { return null; }
    }

    /// <summary>
    /// Todas as props do Android numa só chamada: `getprop` sem argumentos
    /// imprime `[chave]: [valor]` para cada prop — ~20 roundtrips adb viram 1.
    /// Props inexistentes ou vazias ficam fora do dicionário (→ null, nunca
    /// "" — um "" a jusante virava check `warn` sem valor).
    /// </summary>
    private async Task<Dictionary<string, string>> PropsAsync(string serial)
    {
        var props = new Dictionary<string, string>(StringComparer.Ordinal);
        var dump = await AdbAsync(serial, "shell getprop");
        foreach (var line in (dump ?? "").Split('\n'))
        {
            var m = Regex.Match(line.Trim(), @"^\[([^\]]+)\]: \[(.*)\]$");
            if (m.Success && m.Groups[2].Value.Trim().Length > 0)
                props.TryAdd(m.Groups[1].Value, m.Groups[2].Value.Trim());
        }
        return props;
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

    public async Task<DeviceReport> CollectAsync(string serial, CancellationToken ct = default)
    {
        _ct = ct;
        ct.ThrowIfCancellationRequested();
        try
        {
            return await CollectCoreAsync(serial);
        }
        finally
        {
            _ct = CancellationToken.None;
        }
    }

    private async Task<DeviceReport> CollectCoreAsync(string serial)
    {
        var r = new DeviceReport { Platform = "android" };
        r.Device.Serial = serial;
        var props = await PropsAsync(serial);
        string? Prop(string name) => props.TryGetValue(name, out var v) ? v : null;
        r.Device.Brand = Prop("ro.product.brand");
        r.Device.Model = Prop("ro.product.model");
        r.Device.Os = "Android";
        r.Device.OsVersion = Prop("ro.build.version.release");

        // Nome comercial — prop de marketing do fabricante, senão tabela de códigos
        foreach (var prop in ModelNameTable.MarketingProps)
        {
            var name = ModelNameTable.Clean(Prop(prop), r.Device.Model);
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
            $"SDK {Prop("ro.build.version.sdk")}, patch {Prop("ro.build.version.security_patch")}");
        // Props ausentes não viram linha em branco no relatório.
        if (Prop("ro.build.fingerprint") is { } fingerprint)
            r.Set("identity.fingerprint", "info", fingerprint,
                "build exata do firmware — útil para confirmar stock vs ROM modificado");
        if (Prop("gsm.version.baseband") is { } baseband)
            r.Set("device.baseband", "info", baseband, "firmware do modem — afeta rede/SIM");

        // SIM/carrier — PIN/PUK do cartão e lock de operadora saltam aqui
        // (NETWORK_LOCKED/PERM_DISABLED = telemóvel preso a uma operadora —
        // decisivo para retoma; PIN_REQUIRED = PIN do cartão do cliente).
        var simState = Prop("gsm.sim.state");
        var carrier = Prop("gsm.operator.alpha");
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
        var vbState = Prop("ro.boot.verifiedbootstate");
        var buildTags = Prop("ro.build.tags");
        var bootloader = AndroidDecisions.Bootloader(vbState);
        r.Set("security.bootloader", bootloader.Status, bootloader.Value, bootloader.Note);
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
        var crypto = Prop("ro.crypto.state");
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
            var accCount = AdbParsing.ParseAccountCount(accounts);
            if (accCount is { } contas)
                r.Set("security.accounts", contas > 0 ? "warn" : "pass",
                    contas == 0 ? "sem contas" : $"{contas} conta(s)",
                    contas > 0
                        ? "conta(s) do fabricante/Google ativas — um reset dispara FRP; desvincular antes de vender"
                        : "sem contas — FRP não dispara num reset");
            else
                // `?? 0` aqui dava um «pass» tranquilizador a um dump que não
                // se percebeu — e é o veredicto que tranca a revenda.
                r.Set("security.accounts", "warn", "dump ilegível",
                    "não foi possível contar as contas — confirma à mão antes de vender (um reset pode disparar FRP)");
        }

        // MDM: device/profile owner = gerido por empresa ou preso a leasing —
        // red flag máxima para retoma.
        var policy = await AdbAsync(serial, "shell dumpsys device_policy");
        if (policy is not null)
        {
            // "Device Owner Type: -1" é o dump de um Android SEM gestão: a
            // substring "Device Owner" dava falso positivo e, como security.mdm
            // está nos HardLocks do Grader, capava o grau em D.
            var mdm = AndroidDecisions.Mdm(AdbParsing.HasDeviceOwner(policy));
            r.Set("security.mdm", mdm.Status, mdm.Value, mdm.Note);
        }

        // Knox / warranty bit + OEM unlock — o que o fabricante usa para
        // recusar garantia numa retoma.
        var warrantyBit = Prop("ro.boot.warranty_bit") ?? Prop("ro.warranty_bit");
        if (warrantyBit is "0" or "1")
            r.Set("security.warranty", warrantyBit == "1" ? "fail" : "pass",
                warrantyBit == "1" ? "Knox queimado (0x1)" : "intacto",
                warrantyBit == "1"
                    ? "garantia oficial perdida — o fabricante recusa reparação"
                    : null);
        var flashLocked = Prop("ro.boot.flash.locked"); // 1 = bloqueado
        var oemUnlock = Prop("sys.oem_unlock_allowed"); // 1 = permitido
        if (flashLocked == "0" || oemUnlock == "1")
            r.Set("security.oemunlock", "warn",
                flashLocked == "0" ? "bootloader desbloqueado" : "desbloqueio OEM permitido",
                "software modificável — afeta garantia e valor de retoma");
        else if (flashLocked == "1" || oemUnlock == "0")
            r.Set("security.oemunlock", "pass", "bootloader bloqueado");

        // Auditoria de segurança das apps (pacote Pro): origem dos installs,
        // permissões sensíveis, acessibilidade, verificação de pacotes.
        if (_securityAudit)
            await CollectAppAuditAsync(r, serial);

        // Desgaste NAND (eMMC life_time / pre_eol) — raro sem root, mas quando
        // exposto é um aviso "vai morrer" que nenhuma app de cliente mostra.
        var nandProbe = (await AdbAsync(serial,
            "shell for n in /sys/block/mmcblk0/device/life_time " +
            "/sys/class/mmc_host/mmc0/mmc0:0001/life_time; " +
            "do if [ -e $n ]; then v=$(cat $n 2>/dev/null); " +
            "if [ -z \"$v\" ]; then echo denied; else echo \"value:$v\"; fi; " +
            "else echo missing; fi; done"))?.Trim();
        var nandLines = (nandProbe ?? "").Split('\n').Select(l => l.Trim()).ToList();
        var nandVals = string.Join("\n",
            nandLines.Where(l => l.StartsWith("value:", StringComparison.Ordinal)).Select(l => l[6..]));
        var worstLife = DeepReadingParsing.ParseEmmcLifeClass(nandVals);
        if (worstLife is { } worst)
        {
            // classes 1–11, ~10% de vida gasta por classe
            r.Set("storage.wear", worst >= 9 ? "warn" : "info",
                $"classe {worst}/11 (~{Math.Min(100, worst * 10)}% de vida gasta)",
                "desgaste estimado da NAND reportado pelo controlador eMMC");
        }
        else if (nandLines.Any(l => l == "denied"))
            r.Set("storage.wear", "skipped", "requer root",
                "o controlador eMMC expõe o desgaste mas o shell não o lê");
        else
            r.Set("storage.wear", "skipped", "não disponível neste aparelho",
                "UFS e a maioria dos eMMC modernos não expõem life_time ao shell");

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

        if (_bugreportConsent)
            await CollectBugreportCountsAsync(r, serial);

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
        if (AndroidDecisions.BatteryPresence(AdbParsing.ParsePresent(batt)) is { } absent)
        {
            r.Set("battery.present", absent.Status, absent.Value, absent.Note);
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
        // Um só shell para todos os nós sysfs — cada `adb shell cat` custava
        // um roundtrip inteiro. Xiaomi/Qualcomm expõem a capacidade no nó
        // `bms` (alguns no max170xx) em vez de `battery`.
        // O probe marca cada nó: «missing» (o aparelho não expõe),
        // «denied» (existe mas o shell não lê — requer root) ou o valor.
        // Sem a distinção, uma leitura bloqueada por permissões saía como
        // "não disponível" e a bancada não sabia que o root a expõe.
        var sysfsDump = await AdbAsync(serial,
            "shell for f in battery/cycle_count battery/charge_full " +
            "battery/charge_full_design bms/charge_full bms/charge_full_design " +
            "max170xx_battery/charge_full max170xx_battery/charge_full_design; " +
            "do n=/sys/class/power_supply/$f; " +
            "if [ -e $n ]; then v=$(cat $n 2>/dev/null); " +
            "if [ -z \"$v\" ]; then echo \"$f:denied\"; else echo \"$f:value:$v\"; fi; " +
            "else echo \"$f:missing\"; fi; done");
        var sysfs = new Dictionary<string, string>(StringComparer.Ordinal);
        var sysfsState = new Dictionary<string, string>(StringComparer.Ordinal);
        DeepReadingParsing.ParseMarkedSysfs(sysfsDump, sysfs, sysfsState);
        string? Sysfs(string node)
            => sysfs.TryGetValue(node, out var v) && v.Length > 0 ? v : null;
        // «requer root» se ALGUM nó da família existe mas está bloqueado;
        // «não disponível» quando todos faltam. Um nó com valor não declara
        // estado nenhum — a leitura já saiu.
        string? DeepState(params string[] nodes)
        {
            if (nodes.Any(n => sysfs.ContainsKey(n))) return null;
            if (nodes.Any(n => sysfsState.TryGetValue(n, out var st) && st == "denied"))
                return "requer root";
            if (nodes.Any(n => sysfsState.TryGetValue(n, out var st) && st is "denied" or "missing"))
                return "não disponível neste aparelho";
            return "não disponível neste aparelho";
        }
        // Primeiro nó com um número positivo — um valor inválido não vazio
        // num nó não bloqueia o seguinte (a lógica de antes do batch).
        string? FirstNumeric(params string[] nodes)
        {
            foreach (var node in nodes)
                if (AdbParsing.TryDouble(Sysfs(node), out var d) && d > 0)
                    return Sysfs(node);
            return null;
        }
        var cycles = Sysfs("battery/cycle_count");
        var chargeFull = FirstNumeric("battery/charge_full",
            "bms/charge_full", "max170xx_battery/charge_full");
        var chargeDesign = FirstNumeric("battery/charge_full_design",
            "bms/charge_full_design", "max170xx_battery/charge_full_design");
        if (DeepReadingParsing.ParseCycleCount(cycles) is { } c)
            r.Set("battery.cycles", c > 800 ? "warn" : "info",
                c.ToString(CultureInfo.InvariantCulture),
                c > 800 ? "acima de ~800 ciclos a degradação é esperada" : null);
        else if (DeepState("battery/cycle_count") is { } cyclesState)
            r.Set("battery.cycles", "skipped", cyclesState,
                "o contador de ciclos não é exposto por todas as marcas");

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

        // Samsung OneUI sem charge_full no sysfs: mSavedBatteryAsoc é a saúde
        // calculada pelo fuel gauge (86 = 86%) — melhor que nada.
        if (!r.Results.ContainsKey("battery.capacity")
            && AndroidDecisions.CapacityFromSavedAsoc(AdbParsing.ParseSavedAsoc(batt)) is { } saved)
            r.Set("battery.capacity", saved.Status, saved.Value, saved.Note);

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

        // Nem sysfs, nem asoc, nem estimativa: a leitura declara-se
        // explicitamente — «requer root» quando o nó existe mas está
        // bloqueado, «não disponível» quando o aparelho não o expõe.
        // O cálculo precisa do par real+design: uma capacidade real legível
        // com o design em falta/bloqueado também fica sem percentagem — o
        // estado olha para as duas famílias de nós.
        // A percentagem exige o PAR real+design: o estado vem da família
        // que falta — um valor legível numa não esconde a falta da outra.
        string[] realNodes = ["battery/charge_full", "bms/charge_full", "max170xx_battery/charge_full"];
        string[] designNodes = ["battery/charge_full_design", "bms/charge_full_design", "max170xx_battery/charge_full_design"];
        bool AnyDenied(string[] nodes) =>
            nodes.Any(n => sysfsState.TryGetValue(n, out var st) && st == "denied");
        var realOk = realNodes.Any(n => sysfs.ContainsKey(n));
        var designOk = designNodes.Any(n => sysfs.ContainsKey(n));
        var fb = DeepReadingParsing.CapacityFallbackState(
            realOk, designOk, AnyDenied(realNodes), AnyDenied(designNodes));
        if (!r.Results.ContainsKey("battery.capacity")
            && !r.Results.ContainsKey("battery.capacity_learned")
            && fb is { } capState)
            r.Set("battery.capacity", "skipped", capState.state,
                capState.partial
                    ? "parte dos nós exige root mas o resto não existe no aparelho — root não chega"
                    : realOk && !designOk
                        ? "capacidade real legível mas sem o valor de fábrica — percentagem impossível"
                        : "capacidade real de fábrica não medida neste aparelho");
    }

    /// <summary>
    /// Auditoria de apps do pacote Pro — só leitura, nenhuma ação no
    /// aparelho. Checks que dependem da lista de apps ficam `skipped`
    /// quando o `pm` não responde; nunca se escreve "tudo bem" às cegas.
    /// </summary>
    private async Task CollectAppAuditAsync(DeviceReport r, string serial)
    {
        var apps = AndroidSecurityAudit.ParseUserPackages(
            await AdbAsync(serial, "shell pm list packages -3 -i", 15000));

        if (apps is null)
        {
            foreach (var key in AppAuditKeys)
                r.Set(key, "skipped", "apps ilegíveis",
                    "pm list packages não respondeu — auditoria de apps incompleta");
        }
        else
        {
            var sideload = AndroidSecurityAudit.Sideloaded(apps);
            r.Set("security.audit.sideload", sideload.Status, sideload.Value, sideload.Note);

            // Um só `dumpsys package`: por app seriam ~100 arranques de JVM
            // no telemóvel; o dump completo transfere uma vez e filtra-se no Core.
            var grants = AndroidSecurityAudit.ParsePackageDumpGrants(
                await AdbAsync(serial, "shell dumpsys package", 60000));
            if (grants is null)
            {
                r.Set("security.audit.permissions", "skipped", "dump ilegível",
                    "dumpsys package não respondeu — permissões não auditadas");
            }
            else
            {
                var perms = AndroidSecurityAudit.SensitivePermissions(apps, grants);
                r.Set("security.audit.permissions", perms.Status, perms.Value, perms.Note);
            }

            // Instalação de APKs: REQUEST_INSTALL_PACKAGES é signature|appop —
            // o "install permissions" do dump mostra sempre granted=false para
            // apps de utilizador; a autorização real vive nos AppOps.
            var appOps = AndroidSecurityAudit.ParseInstallPackagesAppOps(
                await AdbAsync(serial, "shell dumpsys appops", 30000));
            if (appOps is null)
            {
                r.Set("security.audit.installpackages", "skipped", "appops ilegível",
                    "dumpsys appops não respondeu — instalação de APKs não auditada");
            }
            else
            {
                // Política do dispositivo (no_install_unknown_sources) pode
                // bloquear as instalações apesar das autorizações guardadas.
                // Só se consulta o dumpsys user quando há apps autorizadas —
                // no caso comum (nenhuma) poupa-se um adb extra.
                bool? blocked = appOps.Count > 0
                    ? AndroidSecurityAudit.ParseUnknownSourcesBlocked(
                        await AdbAsync(serial, "shell dumpsys user", 15000))
                    : false;
                var inst = AndroidSecurityAudit.InstallPackages(apps, appOps, blocked);
                r.Set("security.audit.installpackages", inst.Status, inst.Value, inst.Note);
            }

            var services = AndroidSecurityAudit.ParseAccessibilityServices(
                await AdbAsync(serial, "shell settings get secure enabled_accessibility_services"));
            var access = AndroidSecurityAudit.Accessibility(services,
                apps.Select(a => a.Package).ToHashSet(StringComparer.Ordinal));
            r.Set("security.audit.accessibility", access.Status, access.Value, access.Note);
        }

        // Apps de utilizador desativadas (-3): as de sistema desativadas são
        // ruído de OEM (NFC, etc.) e não são ação do técnico.
        var disabled = AndroidSecurityAudit.ParseDisabledPackages(
            await AdbAsync(serial, "shell pm list packages -3 -d", 15000));
        if (disabled is null)
            r.Set("security.audit.disabled", "skipped", "pm ilegível",
                "pm list packages -3 -d não respondeu");
        else
        {
            var dis = AndroidSecurityAudit.DisabledApps(disabled);
            r.Set("security.audit.disabled", dis.Status, dis.Value, dis.Note);
        }

        var verifierOut = await AdbAsync(serial,
            "shell echo V1=$(settings get global package_verifier_enable); " +
            "echo V2=$(settings get global verifier_verify_adb_installs)");
        var (enabled, verifyAdb) = AndroidSecurityAudit.ParseVerifierSettings(verifierOut);
        var ver = AndroidSecurityAudit.Verifier(enabled, verifyAdb);
        r.Set("security.audit.verifier", ver.Status, ver.Value, ver.Note);
    }

    /// <summary>Checks da auditoria de apps que dependem da lista `pm list packages -3`.</summary>
    private static readonly string[] AppAuditKeys =
    [
        "security.audit.sideload", "security.audit.permissions",
        "security.audit.installpackages", "security.audit.accessibility",
    ];

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

    /// <summary>
    /// Frequência média de CPU (MHz) — só a bancada de perfil a usa: um
    /// `scaling_cur_freq` por núcleo online, uma shell call. Sem root nem
    /// nó exposto → null (o check sai skipped, nunca zero).
    /// </summary>
    public async Task<double?> ProbeCpuFreqAsync(string serial)
    {
        var o = await AdbAsync(serial,
            "shell for c in /sys/devices/system/cpu/cpu[0-9]*/cpufreq/scaling_cur_freq; " +
            "do cat $c 2>/dev/null; done", 5000);
        return AdbParsing.ParseCpuFreqMhz(o);
    }

    /// <summary>
    /// Estado de ecrã lido antes de uma sessão (perfil/bancada) mexer nele —
    /// reposto no fim para o aparelho não ficar com brilho/stay-awake
    /// alterados. Campos null = definição não lida (não se inventa valor).
    /// </summary>
    public sealed record ScreenState(string? Brightness, string? BrightnessMode, string? StayOn);

    /// <summary>
    /// Lê brilho, modo de brilho e stay-on-while-plugged-in numa shell call.
    /// `settings get` imprime "null" quando a chave não existe — não é um
    /// valor e não pode virar um `settings put` inventado.
    /// </summary>
    public async Task<ScreenState?> ProbeScreenStateAsync(string serial)
    {
        var o = await ShellAsync(serial,
            "settings get system screen_brightness; settings get system screen_brightness_mode; " +
            "settings get global stay_on_while_plugged_in", 5000);
        return ParseScreenState(o);
    }

    /// <summary>Parse das 3 linhas do probe — "null"/lixo não-numérico → null.</summary>
    internal static ScreenState? ParseScreenState(string? output)
    {
        if (output is null) return null;
        var lines = output.Split('\n',
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        static string? Val(string[] ls, int i) =>
            i < ls.Length && ls[i].Length > 0 && ls[i] != "null" && ls[i].All(char.IsDigit)
                ? ls[i] : null;
        return new ScreenState(Val(lines, 0), Val(lines, 1), Val(lines, 2));
    }

    /// <summary>
    /// Comandos que repõem o estado lido por <see cref="ProbeScreenStateAsync"/>
    /// — só se escrevem valores realmente capturados; um stay-on desconhecido
    /// cai no defeito (desligado) em vez de ficar ligado para sempre.
    /// </summary>
    internal static List<string> BuildScreenRestoreCommands(ScreenState prior)
    {
        var cmds = new List<string>();
        // Brilho antes do modo: repõe o nível manual mesmo quando o modo
        // original era automático (o utilizador pode voltar a manual).
        if (prior.Brightness is { } b) cmds.Add($"settings put system screen_brightness {b}");
        if (prior.BrightnessMode is { } m) cmds.Add($"settings put system screen_brightness_mode {m}");
        if (prior.StayOn is { } s) cmds.Add($"settings put global stay_on_while_plugged_in {s}");
        else cmds.Add("svc power stayon false");
        cmds.Add("input keyevent KEYCODE_SLEEP");
        return cmds;
    }

    /// <summary>Repõe o estado de ecrã capturado — uma shell call, ecrã a dormir.</summary>
    public async Task RestoreScreenStateAsync(string serial, ScreenState prior)
    {
        var cmds = BuildScreenRestoreCommands(prior);
        if (cmds.Count > 0)
            await ShellAsync(serial, string.Join("; ", cmds));
    }

    private async Task CollectLogsAsync(DeviceReport r, string serial)
    {
        var crashBuf = await AdbAsync(serial, "logcat -d -b crash -t 80", 10000);
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
            r.LogsText = crashBuf.Length > 14_000 ? crashBuf[^14_000..] : crashBuf;

        r.Set("logs.crashes",
            sysCrashes > 3 ? "warn" : "info",
            $"{appCrashes} app crashes · {anrs} ANRs · {sysCrashes} de sistema",
            sysCrashes > 3
                ? "falhas repetidas de componentes de sistema — investigar"
                : "crashes de apps = software, informativo — não é defeito de hardware");
    }

    /// <summary>
    /// Contagens de crashes do `adb bugreport` — só corre com consentimento
    /// explícito do operador (o zip contém dados pessoais do dono). Daqui
    /// saem apenas contagens e tipos; o zip é sempre apagado no fim.
    /// </summary>
    private async Task CollectBugreportCountsAsync(DeviceReport r, string serial)
    {
        // Pasta dedicada: se a app morrer a meio da recolha, o zip com dados
        // pessoais não fica esquecido no temp comum — a recolha seguinte
        // purga qualquer resíduo antes de produzir o seu.
        var dir = Path.Combine(Path.GetTempPath(), "oficinadiag-bugreport");
        try
        {
            Directory.CreateDirectory(dir);
            foreach (var stale in Directory.GetFiles(dir, "*.zip"))
                try { File.Delete(stale); } catch { }
        }
        catch { }
        var tmp = Path.Combine(dir, $"br-{Guid.NewGuid():N}.zip");
        try
        {
            // Processo dedicado: o caminho local pode ter espaços e o
            // AdbAsync parte os argumentos em espaços. O adb moderno aceita
            // o caminho de destino no host como argumento único.
            var psi = new ProcessStartInfo(_adbPath)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            psi.ArgumentList.Add("-s");
            psi.ArgumentList.Add(serial);
            psi.ArgumentList.Add("bugreport");
            psi.ArgumentList.Add(tmp);
            using (var p = Process.Start(psi))
            {
                if (p is not null)
                {
                    var drainOut = p.StandardOutput.ReadToEndAsync();
                    var drainErr = p.StandardError.ReadToEndAsync();
                    // O bugreport demora minutos — 4 min de teto. A espera é
                    // cancelável: um Cancel do operador mata o adb já, em vez
                    // de o scan ficar preso e gravar um relatório cancelado.
                    var sw = Stopwatch.StartNew();
                    while (!p.HasExited && sw.ElapsedMilliseconds < 240000)
                    {
                        _ct.ThrowIfCancellationRequested();
                        await Task.Delay(250, _ct);
                    }
                    if (!p.HasExited)
                        try { p.Kill(entireProcessTree: true); } catch { }
                }
            }
            if (!File.Exists(tmp) || new FileInfo(tmp).Length == 0)
            {
                r.Set("logs.bugreport", "skipped", "bugreport não produzido",
                    "o aparelho não gerou o bugreport — contagens profundas indisponíveis");
                return;
            }
            using var zip = System.IO.Compression.ZipFile.OpenRead(tmp);
            var counts = BugreportCrashes.CountEntries(zip);
            r.Set("logs.bugreport", BugreportCrashes.Status(counts),
                BugreportCrashes.Summarize(counts),
                "contagens do bugreport (com consentimento) — tipos e quantidades; conteúdo nunca extraído/guardado; " +
                "tombstones/panics repetidos são indício de hardware, não prova");
        }
        catch (Exception)
        {
            // Nunca rebenta o scan por causa da contagem profunda.
            r.Set("logs.bugreport", "skipped", "leitura falhou",
                "o bugreport foi produzido mas não se conseguiu contar");
        }
        finally
        {
            try { if (File.Exists(tmp)) File.Delete(tmp); } catch { }
        }
    }
}
