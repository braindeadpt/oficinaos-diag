using System.Diagnostics;
using System.IO;
using System.Xml.Linq;
using iMobileDevice;
using iMobileDevice.iDevice;
using iMobileDevice.Lockdown;
using iMobileDevice.Plist;

namespace OficinaDiag.Devices;

/// <summary>
/// Collects a DeviceReport from an iPhone over USB via libimobiledevice.
/// Needs the Apple USB driver (iTunes / Apple Devices app) for Windows to see
/// the device, plus "Trust this computer" accepted on the phone.
/// </summary>
public sealed class IosCollector
{
    private readonly IiDeviceApi _idevice = LibiMobileDevice.Instance.iDevice;
    private readonly ILockdownApi _lockdown = LibiMobileDevice.Instance.Lockdown;
    private readonly IPlistApi _plist = LibiMobileDevice.Instance.Plist;

    public IReadOnlyList<string> ListDevices()
    {
        var count = 0;
        var ret = _idevice.idevice_get_device_list(out var udids, ref count);
        return ret == iDeviceError.Success ? udids : Array.Empty<string>();
    }

    private string? GetValue(LockdownClientHandle client, string? domain, string key)
    {
        try
        {
            if (_lockdown.lockdownd_get_value(client, domain!, key, out var node) != LockdownError.Success)
                return null;
            using (node)
            {
                switch (_plist.plist_get_node_type(node))
                {
                    case PlistType.String:
                        _plist.plist_get_string_val(node, out var s);
                        return s;
                    case PlistType.Uint:
                        ulong u = 0;
                        _plist.plist_get_uint_val(node, ref u);
                        return u.ToString();
                    case PlistType.Boolean:
                        var ch = '\0';
                        _plist.plist_get_bool_val(node, ref ch);
                        return ch == '\x01' ? "true" : "false";
                    default:
                        return null;
                }
            }
        }
        catch { return null; }
    }

    public async Task<DeviceReport> CollectAsync(string udid)
    {
        var r = new DeviceReport { Platform = "ios" };
        r.Device.Serial = udid;

        var cts = new CancellationTokenSource();
        var collect = Task.Run(() =>
        {
            if (_idevice.idevice_new(out var device, udid) != iDeviceError.Success)
            {
                r.Set("scan", "fail", null, "Não consegui abrir o dispositivo — driver Apple em falta?");
                return;
            }
            using (device)
            {
                if (_lockdown.lockdownd_client_new_with_handshake(
                        device, out var client, "OficinaDiag") != LockdownError.Success)
                {
                    r.Set("scan", "fail", null,
                        "Emparelhamento recusado — desbloqueia o iPhone e aceita «Confiar neste computador»");
                    return;
                }
                using (client)
                {
                    r.Device.Os = "iOS";
                    r.Device.Model = GetValue(client, null, "ProductType");   // ex.: iPhone14,2
                    r.Device.Brand = "Apple";
                    r.Device.OsVersion = GetValue(client, null, "ProductVersion");
                    var marketingName = GetValue(client, null, "DeviceName");

                    r.Set("identity.model", "info", marketingName ?? r.Device.Model, r.Device.Model);
                    r.Set("identity.serial", "info", GetValue(client, null, "SerialNumber"));
                    r.Set("os.version", "info", $"{r.Device.Os} {r.Device.OsVersion}",
                        $"build {GetValue(client, null, "BuildVersion")}");

                    // Não ativado ≠ Activation Lock, mas fica preso no ecrã
                    // "Hello" — não se pode revender sem confirmar a conta.
                    var act = GetValue(client, null, "ActivationState");
                    r.Set("device.activation",
                        act is null ? "skipped" : act == "Activated" ? "pass" : "warn",
                        act,
                        act is null ? "estado de ativação não exposto por USB"
                            : act == "Activated" ? "equipamento ativado e funcional"
                            : "não ativado — pode estar preso no ecrã de ativação; verificar iCloud lock");
                    var simStatus = GetValue(client, null, "SIMStatus")
                        ?.Replace("kCTSIMSupportSIMStatus", "");
                    r.Set("device.sim",
                        simStatus?.Contains("Locked") == true ? "warn" : "info",
                        simStatus,
                        simStatus?.Contains("Locked") == true
                            ? "SIM bloqueado — PIN do cartão ou lock de operadora"
                            : null);
                    r.Set("device.wifi", "info", GetValue(client, null, "WiFiAddress"));
                    r.Set("security.password", "info", GetValue(client, null, "PasswordProtected"),
                        "true = código de desbloqueio definido");

                    // Modem — o domínio interno expõe a baseband; fallback ao
                    // domínio nulo para iOS que a rejeitem no interno.
                    var baseband = GetValue(client, "com.apple.mobile.internal", "BasebandVersion")
                        ?? GetValue(client, null, "BasebandVersion");
                    if (baseband is not null)
                        r.Set("device.baseband", "info", baseband, "firmware do modem — afeta rede/SIM");
                    var iccid = GetValue(client, null, "IntegratedCircuitCardIdentity");
                    if (iccid is not null)
                        r.Set("identity.iccid", "info", iccid, "nº do cartão SIM físico");

                    // Flags de bloqueio — honestas: o que não é consultável por
                    // USB fica "skipped", nunca fingido.
                    var supervised = GetValue(client, null, "IsSupervised")
                        ?? GetValue(client, "com.apple.mobile.supervision", "IsSupervised");
                    if (supervised is not null)
                        r.Set("security.mdm", Truthy(supervised) ? "warn" : "pass",
                            Truthy(supervised) ? "supervisionado (MDM)" : "sem supervisão",
                            Truthy(supervised)
                                ? "perfil de gestão — pode estar preso a uma empresa ou leasing"
                                : null);
                    else
                        r.Set("security.mdm", "skipped", "indeterminado",
                            "não verificável por USB — confirma em Definições → Geral → VPN e Gestão de Dispositivo");
                    r.Set("security.findmy", "skipped", "indeterminado",
                        "Find My / Activation Lock não é consultável sem apagar — pede ao cliente para o desativar à tua frente");

                    // Storage — o espaço de dados vive no domínio disk_usage;
                    // TotalDiskCapacity no domínio nulo serve de fallback.
                    if (ulong.TryParse(
                            GetValue(client, "com.apple.disk_usage", "TotalDataCapacity")
                            ?? GetValue(client, null, "TotalDiskCapacity"), out var total))
                    {
                        ulong.TryParse(GetValue(client, "com.apple.disk_usage", "TotalDataAvailable")
                            ?? GetValue(client, null, "TotalDataAvailable"), out var free);
                        // Sempre "info": espaço usado é dados do utilizador,
                        // não defeito de hardware — contexto, não achado.
                        r.Set("storage", "info",
                            $"{free / 1_073_741_824.0:0.#} GB livres de {total / 1_073_741_824.0:0.#} GB",
                            "utilização normal — não é defeito de hardware");
                    }

                    var imei = GetValue(client, null, "InternationalMobileEquipmentIdentity");
                    if (imei is not null)
                        r.Set("identity.imei", "info", imei);

                    // Battery — com.apple.mobile.battery domain
                    var battLevel = GetValue(client, "com.apple.mobile.battery", "BatteryCurrentCapacity");
                    var battCharging = GetValue(client, "com.apple.mobile.battery", "BatteryIsCharging");
                    r.Set("battery.level", "info",
                        battLevel is null ? null : $"{battLevel}%",
                        battCharging == "true" ? "a carregar" : null);

                    var cycles = GetValue(client, null, "BatteryCycleCount");
                    if (int.TryParse(cycles, out var c))
                        r.Set("battery.cycles", c > 800 ? "warn" : "info", c.ToString());

                    r.Set("scan", "pass", "completo", "Recolha via libimobiledevice");
                }

                // Bateria a sério: ciclos + capacidade vs design vivem na
                // IORegistry (AppleSmartBattery) — só acessível via
                // diagnostics_relay, não pelo lockdownd.
                if (!cts.IsCancellationRequested)
                    CollectBatteryHealth(r, udid);

                // MobileGestalt: part number, cor, região, ecrã — os dados que
                // uma loja precisa para grading de usados.
                if (!cts.IsCancellationRequested)
                    CollectGestalt(r, udid);

                // Panic/crash logs do dispositivo — kernel panics apontam a
                // componente de hardware a falhar; crashes de apps são info.
                if (!cts.IsCancellationRequested)
                    CollectCrashLogs(r, udid);
            }
        });

        // O handshake lockdownd pode bloquear para sempre quando outra app do
        // Windows (Fotos/AutoPlay) tem o iPhone ocupado — timeout defensivo.
        if (await Task.WhenAny(collect, Task.Delay(TimeSpan.FromSeconds(30))) != collect)
        {
            cts.Cancel(); // a task não morre na hora, mas salta as secções restantes
            r.Set("scan", "fail", null,
                "Timeout — fecha a janela de importação de fotos do Windows e tenta de novo");
        }

        return r;
    }

    /// <summary>idevicediagnostics.exe bundled ao lado do exe — ponte para a IORegistry.</summary>
    private static string? RunTool(string udid, string args, int timeoutMs = 15000)
    {
        var exe = Path.Combine(AppContext.BaseDirectory, "idevicediagnostics.exe");
        if (!File.Exists(exe)) return null;
        try
        {
            using var p = Process.Start(new ProcessStartInfo(exe, $"-u {udid} {args}")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            });
            if (p is null) return null;
            var stdout = p.StandardOutput.ReadToEndAsync();
            var drain = p.StandardError.ReadToEndAsync(); // stderr cheio bloqueava o processo
            if (!p.WaitForExit(timeoutMs))
            {
                try { p.Kill(); } catch { }
                return null;
            }
            return stdout.Result;
        }
        catch { return null; }
    }

    /// <summary>Leitura instantânea para o ticker — AppleSmartBattery só.</summary>
    public async Task<LiveTelemetry?> ProbeAsync(string udid)
    {
        var xml = await Task.Run(() => RunTool(udid, "ioregentry AppleSmartBattery", 8000));
        if (xml is null) return null;
        var io = ParsePlistDict(xml);
        if (io.Count == 0) return null;

        int? mA = io.TryGetValue("InstantAmperage", out var a) && long.TryParse(a, out var m)
            ? (int)Math.Abs(m) : null;
        double? volts = io.TryGetValue("Voltage", out var v) && int.TryParse(v, out var mv)
            ? mv / 1000.0 : null;
        var rawTemp = io.TryGetValue("Temperature", out var t1) ? t1
            : io.TryGetValue("AverageBattSkinTemp", out var t2) ? t2 : null;
        double? tempC = rawTemp is not null && double.TryParse(rawTemp, out var tc)
            ? (tc > 150 ? tc / 100 : tc) : null;
        int? pct = io.TryGetValue("BatteryCurrentCapacity", out var p) && int.TryParse(p, out var lv)
            ? lv : null;
        bool? charging = io.TryGetValue("IsCharging", out var ch) ? Truthy(ch) : null;
        double? negW = io.TryGetValue("Watts", out var w) && int.TryParse(w, out var wv) && wv > 0
            ? wv : null;
        return new LiveTelemetry(mA, volts, tempC, pct, charging, negW);
    }

    /// <summary>Achata todos os &lt;dict&gt; aninhados de um plist XML → pares key/valor a string.</summary>
    private static Dictionary<string, string> ParsePlistDict(string xml)
    {
        var dict = new Dictionary<string, string>();
        try
        {
            foreach (var d in XDocument.Parse(xml).Descendants("dict"))
            {
                var nodes = d.Elements().ToList();
                for (var i = 0; i + 1 < nodes.Count; i += 2)
                {
                    if (nodes[i].Name.LocalName != "key") break;
                    var v = nodes[i + 1];
                    // ignora containers — só scalars
                    if (v.Name.LocalName is "dict" or "array") continue;
                    dict[nodes[i].Value] = v.Name.LocalName is "true" or "false"
                        ? v.Name.LocalName
                        : v.Value;
                }
            }
        }
        catch { }
        return dict;
    }

    private static bool Truthy(string? v) => v is "true" or "1";

    private void CollectBatteryHealth(DeviceReport r, string udid)
    {
        var xml = RunTool(udid, "ioregentry AppleSmartBattery");
        if (xml is null) return;
        var io = ParsePlistDict(xml);
        if (io.Count == 0) return;

        if (io.TryGetValue("CycleCount", out var cycles) && int.TryParse(cycles, out var c))
            r.Set("battery.cycles", c >= 800 ? "warn" : "info", c.ToString(),
                "a Apple garante ≥80% de capacidade até ~500–1000 ciclos conforme o modelo");

        // Capacidade real vs design = saúde da bateria
        var maxCap = io.TryGetValue("NominalChargeCapacity", out var ncc) ? ncc
            : io.TryGetValue("AppleRawMaxCapacity", out var arm) ? arm : null;
        var design = io.TryGetValue("DesignCapacity", out var dc) ? dc : null;
        if (maxCap is not null && design is not null
            && int.TryParse(maxCap, out var max) && int.TryParse(design, out var des) && des > 0)
        {
            var health = max * 100.0 / des;
            r.Set("battery.health", health < 75 ? "fail" : health < 85 ? "warn" : "pass",
                $"{health:0}%", $"{max}/{des} mAh — carga real vs fábrica");
        }

        // Temperatura: "Temperature" (gerações antigas, centésimos de °C) ou
        // "AverageBattSkinTemp" (iOS recente, já em °C)
        var rawTemp = io.TryGetValue("Temperature", out var t1) ? t1
            : io.TryGetValue("AverageBattSkinTemp", out var t2) ? t2 : null;
        if (rawTemp is not null && double.TryParse(rawTemp, out var tC))
        {
            if (tC > 150) tC /= 100; // escala antiga em centésimos
            r.Set("battery.temp", tC > 40 ? "warn" : "info",
                $"{tC:0.#} °C", tC > 40 ? "quente — afeta a saúde a longo prazo" : null);
        }

        if (io.TryGetValue("Voltage", out var volt) && int.TryParse(volt, out var mV))
            r.Set("battery.voltage", "info", $"{mV} mV");

        if (io.TryGetValue("InstantAmperage", out var amp) && long.TryParse(amp, out var mA))
        {
            // IsCharging aparece como <false/> no topo e <integer>0 em ChargerData.
            var charging = Truthy(io.TryGetValue("IsCharging", out var ch) ? ch : null);
            r.Set("battery.current", "info",
                $"{Math.Abs(mA)} mA — {(charging ? "a carregar" : "a descarregar")}",
                charging ? null : "valor alto a descarregar parado sugere consumo anómalo");
        }

        // Carregador ligado: potência negociada (AdapterDetails.Watts)
        if (Truthy(io.TryGetValue("ExternalConnected", out var ec) ? ec : null)
            && io.TryGetValue("Watts", out var watts) && int.TryParse(watts, out var w) && w > 0)
            r.Set("battery.charger", "info", $"{w} W",
                io.TryGetValue("Description", out var desc) ? desc : null);

        if (io.TryGetValue("Serial", out var batSerial) && batSerial.Length > 0)
            r.Set("battery.serial", "info", batSerial,
                "série da célula — compara com o histórico para detetar bateria trocada");

        if (io.TryGetValue("ManufactureDate", out var mfg) && mfg.Length > 0)
            r.Set("battery.mfg", "info", mfg, "data de fabrico da bateria");
    }

    /// <summary>MobileGestalt via idevicediagnostics — specs que o lockdownd não expõe.</summary>
    private void CollectGestalt(DeviceReport r, string udid)
    {
        var xml = RunTool(udid,
            "mobilegestalt RegulatoryModelNumber ModelNumber DeviceColor DeviceEnclosureColor " +
            "RegionInfo BluetoothAddress MainScreenHeight MainScreenWidth MainScreenScale TelephonyCapability");
        if (xml is null) return;
        var mg = ParsePlistDict(xml);
        if (mg.Count == 0) return;

        if (mg.TryGetValue("RegulatoryModelNumber", out var regModel) && regModel.Length > 0)
            r.Set("identity.regmodel", "info", regModel, "modelo regulatório (A-num) — garante a peça certa");

        if (mg.TryGetValue("ModelNumber", out var partNo) && partNo.Length > 0)
            r.Set("identity.partno", "info", partNo,
                "part number Apple — codifica capacidade, cor e região do aparelho");

        if (mg.TryGetValue("RegionInfo", out var region) && region.Length > 0)
            r.Set("identity.region", "info", region, "região de venda — afeta garantia e bandas");

        var color = (mg.TryGetValue("DeviceColor", out var dc2) ? dc2 : null)
                    ?? (mg.TryGetValue("DeviceEnclosureColor", out var ec) ? ec : null);
        if (!string.IsNullOrWhiteSpace(color))
            r.Set("identity.color", "info", color);

        if (mg.TryGetValue("BluetoothAddress", out var bt) && bt.Length > 0)
            r.Set("device.bluetooth", "info", bt);

        if (mg.TryGetValue("MainScreenHeight", out var sh) && mg.TryGetValue("MainScreenWidth", out var sw)
            && mg.TryGetValue("MainScreenScale", out var scale))
            r.Set("screen.specs", "info", $"{sw}×{sh} pt @{scale}x",
                "resolução lógica do ecrã — refere o modelo no iFixit");

        if (mg.TryGetValue("TelephonyCapability", out var telephony))
            r.Set("device.telephony", "info",
                telephony == "true" ? "SIM/móvel" : "Wi-Fi only (iPad)");
    }

    /// <summary>
    /// idevicecrashreport -e -k: copia os logs para uma pasta temp sem apagar
    /// nada no telefone do cliente. panic-full (bug_type 309) = kernel panic —
    /// o panicString costuma nomear o componente de hardware a falhar.
    /// </summary>
    private void CollectCrashLogs(DeviceReport r, string udid)
    {
        var exe = Path.Combine(AppContext.BaseDirectory, "idevicecrashreport.exe");
        if (!File.Exists(exe)) return;
        var dir = Path.Combine(Path.GetTempPath(), $"oficinadiag-crash-{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(dir);
            using var p = Process.Start(new ProcessStartInfo(exe, $"-e -k -u {udid} \"{dir}\"")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            });
            if (p is null) return;
            var stdoutTask = p.StandardOutput.ReadToEndAsync();
            var stderrTask = p.StandardError.ReadToEndAsync(); // drain — buffers cheios bloqueiam
            if (!p.WaitForExit(25_000)) { try { p.Kill(); } catch { } return; }

            var files = Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories)
                .OrderByDescending(File.GetLastWriteTimeUtc).Take(60).ToList();
            if (files.Count == 0)
            {
                r.Set("logs.panics", "pass", "0", "sem panic/crash logs no dispositivo");
                return;
            }

            var panics = new List<string>();
            var jetsam = 0;
            var crashes = 0;
            var sb = new System.Text.StringBuilder();
            foreach (var f in files)
            {
                var name = Path.GetFileName(f);
                string text;
                try
                {
                    using var sr = new StreamReader(f);
                    var buf = new char[50_000];
                    text = new string(buf, 0, sr.Read(buf));
                }
                catch { continue; }

                // Kernel panics são panic-full/panic-base-*.ips — bug_type 309
                // aparece em TODOS os .ips de crash de app, não distingue nada.
                var isPanic = name.StartsWith("panic", StringComparison.OrdinalIgnoreCase);
                var isJetsam = name.Contains("jetsam", StringComparison.OrdinalIgnoreCase);

                if (isPanic)
                {
                    var ps = System.Text.RegularExpressions.Regex
                        .Match(text, "panicString\"?\\s*[:=]\\s*\"?([^\"\\n]+)")
                        .Groups[1].Value.Trim();
                    panics.Add($"{name}: {(ps.Length > 0 ? ps : "panicString não extraído")}");
                }
                else if (isJetsam) jetsam++;
                else crashes++;

                if (sb.Length < 40_000)
                    sb.AppendLine($"--- {name} ---\n{(text.Length > 600 ? text[..600] : text)}");
            }
            r.LogsText = sb.Length > 0 ? sb.ToString() : null;

            r.Set("logs.panics", panics.Count == 0 ? "pass" : "warn",
                panics.Count.ToString(),
                panics.Count == 0
                    ? "sem kernel panics"
                    : $"kernel panic recente — possível falha de hardware: {panics[0]}"
                        + (panics.Count > 1 ? $" (+{panics.Count - 1})" : ""));
            if (crashes + jetsam > 0)
                r.Set("logs.crashes", "info", $"{crashes} apps · {jetsam} jetsam",
                    "crashes de apps = software; jetsam = pressão de memória, não defeito");
        }
        catch { }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }
}
