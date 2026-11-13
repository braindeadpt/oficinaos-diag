using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
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
        // Native direto: o wrapper managed (ReadOnlyCollection<string>) nunca
        // chama idevice_device_list_free — o char** ficava alocado e cada poll
        // do DeviceDetector vazava N ponteiros. Aqui controlamos a vida toda.
        var count = 0;
        var ret = iDeviceNativeMethods.idevice_get_device_list(out IntPtr listPtr, ref count);
        if (ret != iDeviceError.Success || listPtr == IntPtr.Zero || count <= 0)
            return Array.Empty<string>();
        try
        {
            var udids = new List<string>(count);
            for (var i = 0; i < count; i++)
            {
                var p = Marshal.ReadIntPtr(listPtr, i * IntPtr.Size);
                var s = p == IntPtr.Zero ? null : Marshal.PtrToStringUTF8(p);
                if (!string.IsNullOrEmpty(s)) udids.Add(s!);
            }
            return udids;
        }
        finally
        {
            iDeviceNativeMethods.idevice_device_list_free(listPtr);
        }
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

    /// <summary>
    /// O iPhone é single-client no diagnostics_relay e no lockdownd: um scan e
    /// o ticker no mesmo UDID têm de esperar um pelo outro em vez de colidir.
    /// </summary>
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> DeviceLocks =
        new(StringComparer.OrdinalIgnoreCase);

    private static SemaphoreSlim LockFor(string udid) =>
        DeviceLocks.GetOrAdd(udid, _ => new SemaphoreSlim(1, 1));

    public async Task<DeviceReport> CollectAsync(string udid)
    {
        var gate = LockFor(udid);
        if (!await gate.WaitAsync(TimeSpan.FromSeconds(5)))
        {
            var busy = new DeviceReport { Platform = "ios" };
            busy.Device.Serial = udid;
            busy.Set("scan", "fail", null,
                "USB ocupado — outro scan deste iPhone está a correr; tenta de novo em instantes");
            return busy;
        }
        try { return await CollectLockedAsync(udid); }
        finally { gate.Release(); }
    }

    private async Task<DeviceReport> CollectLockedAsync(string udid)
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

                    // DeviceName é o nome pessoal do cliente ("iPhone de Maria") e
                    // nunca vai para o relatório/histórico. O modelo comercial sai
                    // da tabela local a partir do ProductType.
                    var serial = GetValue(client, null, "SerialNumber");
                    var mkt = ModelNameTable.Lookup(r.Device.Model);
                    r.Device.MarketingName = mkt;
                    r.Set("identity.model", "info",
                        mkt is not null ? $"{mkt} ({r.Device.Model})" : r.Device.Model, r.Device.Model);
                    r.Set("identity.serial", "info", serial);
                    if (!string.IsNullOrWhiteSpace(serial))
                        r.Device.Serial = serial!;
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
                    CollectBatteryHealth(r, udid, cts.Token);

                // MobileGestalt: part number, cor, região, ecrã — os dados que
                // uma loja precisa para grading de usados.
                if (!cts.IsCancellationRequested)
                    CollectGestalt(r, udid, cts.Token);

                // Panic/crash logs do dispositivo — kernel panics apontam a
                // componente de hardware a falhar; crashes de apps são info.
                if (!cts.IsCancellationRequested)
                    CollectCrashLogs(r, udid, cts.Token);
            }
        });

        // O handshake lockdownd pode bloquear para sempre quando outra app do
        // Windows (Fotos/AutoPlay) tem o iPhone ocupado — timeout defensivo.
        if (await Task.WhenAny(collect, Task.Delay(TimeSpan.FromSeconds(30))) != collect)
        {
            cts.Cancel(); // a task não morre na hora, mas salta as secções restantes

            // A task órfã pode estar a meio de CollectBatteryHealth/Gestalt e
            // continuar a chamar Set() no `r` original — devolvemos uma cópia
            // destacada que ela já não alcança, senão a UI enumera Results
            // enquanto a task escreve (InvalidOperationException + relatório
            // a mudar depois de apresentado).
            var detached = new DeviceReport
            {
                Platform = r.Platform,
                LogsText = r.LogsText,
                Device =
                {
                    Serial = r.Device.Serial,
                    Brand = r.Device.Brand,
                    Model = r.Device.Model,
                    MarketingName = r.Device.MarketingName,
                    Os = r.Device.Os,
                    OsVersion = r.Device.OsVersion,
                },
            };
            foreach (var kv in r.SnapshotResults())
                detached.Results[kv.Key] = kv.Value;
            detached.Set("scan", "fail", null,
                "Timeout — fecha a janela de importação de fotos do Windows e tenta de novo");
            r = detached;
        }

        // A task órfã pode continuar viva (o Kill só mata os filhos que o
        // token apanhou): o CTS só pode ser libertado quando ela terminar.
        _ = collect.ContinueWith(_ => cts.Dispose(), TaskScheduler.Default);

        return r;
    }

    /// <summary>
    /// Ferramentas idevice*.exe — no publish ficam ao lado do exe; em builds de
    /// dev (`dotnet run`) o NuGet deixa-as em runtimes\win-x64\native\.
    /// </summary>
    private static string? ToolPath(string name)
    {
        var baseDir = AppContext.BaseDirectory;
        var exe = Path.Combine(baseDir, name);
        if (File.Exists(exe)) return exe;
        exe = Path.Combine(baseDir, "runtimes", "win-x64", "native", name);
        return File.Exists(exe) ? exe : null;
    }

    /// <summary>idevicediagnostics.exe bundled ao lado do exe — ponte para a IORegistry.</summary>
    private static string? RunTool(string udid, string args, int timeoutMs = 15000,
        CancellationToken ct = default)
    {
        var exe = ToolPath("idevicediagnostics.exe");
        if (exe is null) return null;
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
            var stdout = p.StandardOutput.ReadToEndAsync(ct);
            var drain = p.StandardError.ReadToEndAsync(ct); // stderr cheio bloqueava o processo
            // Cancela o processo-filho assim que o token dispara, em vez de
            // deixar um idevicediagnostics órfão a segurar o relay.
            using var kill = ct.Register(() =>
            {
                try { if (!p.HasExited) p.Kill(entireProcessTree: true); } catch { }
            });
            if (!p.WaitForExit(timeoutMs))
            {
                try { p.Kill(entireProcessTree: true); } catch { }
                return null;
            }
            return stdout.Result;
        }
        catch { return null; }
    }

    /// <summary>Leitura instantânea para o ticker — AppleSmartBattery só.</summary>
    public async Task<LiveTelemetry?> ProbeAsync(string udid)
    {
        var gate = LockFor(udid);
        // Scan a correr neste iPhone? O ticker salta em silêncio (1,5 s).
        if (!await gate.WaitAsync(1500)) return null;
        try
        {
            var xml = await Task.Run(() => RunTool(udid, "ioregentry AppleSmartBattery", 8000));
            if (xml is null) return null;
            var io = ParsePlistDict(xml);
            if (io.Count == 0) return null;

            double? volts = Field(io, "Voltage") is { } v
                && int.TryParse(v, NumberStyles.Integer, CultureInfo.InvariantCulture, out var mv)
                ? mv / 1000.0 : null;
            double? tempC = IosDecisions.CelsiusFrom(
                Field(io, "Temperature", "AverageBattSkinTemp"));
            int? pct = Field(io, "BatteryCurrentCapacity", "CurrentCapacity") is { } p
                && int.TryParse(p, NumberStyles.Integer, CultureInfo.InvariantCulture, out var lv)
                ? lv : null;
            int? mA = Field(io, "InstantAmperage") is { } a
                && long.TryParse(a, NumberStyles.Integer, CultureInfo.InvariantCulture, out var m)
                ? (int)Math.Abs(m) : null;
            bool? charging = Field(io, "IsCharging") is { } ch ? Truthy(ch) : null;
            double? negW = Field(io, "AdapterDetails.Watts") is { } w
                && int.TryParse(w, NumberStyles.Integer, CultureInfo.InvariantCulture, out var wv) && wv > 0
                ? wv : null;
            return new LiveTelemetry(mA, volts, tempC, pct, charging, negW);
        }
        finally { gate.Release(); }
    }

    /// <summary>Sub-dicts cujos escalares também queremos, com prefixo.</summary>
    private static readonly string[] NestedDicts = ["AdapterDetails", "BatteryData"];

    /// <summary>
    /// Lê um plist de ioreg/MobileGestalt sem deixar a telemetria de carga
    /// (ChargerData, DeadBatteryBootData, AppleRawAdapterDetails, ...) pisar os
    /// valores da célula: escalares do dict principal ficam sem prefixo, e só
    /// os sub-dicts de que precisamos ficam também acessíveis com prefixo
    /// (é de BatteryData que saem as capacidades e de AdapterDetails os watts).
    /// </summary>
    internal static Dictionary<string, string> ParsePlistDict(string xml)
    {
        var dict = new Dictionary<string, string>(StringComparer.Ordinal);
        // idevicediagnostics imprime o "usage" em stdout com exit 0 quando os
        // argumentos falham — sem esta guarda entrava como plist vazio e o
        // coletor saltava a bateria em silêncio.
        if (string.IsNullOrWhiteSpace(xml) || !xml.Contains("<plist", StringComparison.Ordinal))
            return dict;
        try
        {
            var root = XDocument.Parse(xml).Descendants("dict").FirstOrDefault();
            if (root is null) return dict;

            // ioregentry embrulha a entrada num dict contentor (IORegistry).
            var main = root;
            var top = root.Elements().ToList();
            if (top.Count == 2 && top[0].Name.LocalName == "key" && top[1].Name.LocalName == "dict")
                main = top[1];

            foreach (var (key, value) in Pairs(main))
            {
                if (value.Name.LocalName is "dict" or "array") continue;
                dict[key] = Scalar(value);
            }

            foreach (var sub in NestedDicts)
            {
                foreach (var (key, value) in Pairs(main))
                {
                    if (key != sub || value.Name.LocalName != "dict") continue;
                    foreach (var (k, v) in Pairs(value))
                    {
                        if (v.Name.LocalName is "dict" or "array") continue;
                        dict[$"{sub}.{k}"] = Scalar(v);
                    }
                }
            }
        }
        catch { }
        return dict;
    }

    private static IEnumerable<(string Key, XElement Value)> Pairs(XElement dict)
    {
        var nodes = dict.Elements().ToList();
        for (var i = 0; i + 1 < nodes.Count; i += 2)
        {
            if (nodes[i].Name.LocalName != "key") break;
            yield return (nodes[i].Value, nodes[i + 1]);
        }
    }

    private static string Scalar(XElement v) =>
        v.Name.LocalName is "true" or "false" ? v.Name.LocalName : v.Value;

    /// <summary>Primeiro valor não vazio entre vários nomes (topo e prefixado).</summary>
    /// <summary>
    /// panic-full com "no successful checkins from thermalmonitord" é o
    /// padrão clássico de flex de carga / sensor térmico mal assente.
    /// </summary>
    internal const string ThermalPanicHint =
        "panic de thermalmonitord — verificar flex de carga e sensor térmico (padrão de bancada Apple)";

    internal static bool IsThermalPanic(string panicText) =>
        panicText.Contains("no successful checkins from thermalmonitord", StringComparison.OrdinalIgnoreCase);

    private static string? Field(IReadOnlyDictionary<string, string> io, params string[] keys)
    {
        foreach (var k in keys)
            if (io.TryGetValue(k, out var v) && !string.IsNullOrWhiteSpace(v)) return v;
        return null;
    }

    private static bool Truthy(string? v) => v is "true" or "1";

    private void CollectBatteryHealth(DeviceReport r, string udid, CancellationToken ct)
    {
        var xml = RunTool(udid, "ioregentry AppleSmartBattery", 15000, ct);
        if (xml is null) return;
        var io = ParsePlistDict(xml);
        if (io.Count == 0) return;

        if (Field(io, "CycleCount", "BatteryData.CycleCount") is { } cycles
            && int.TryParse(cycles, NumberStyles.Integer, CultureInfo.InvariantCulture, out var c))
            r.Set("battery.cycles", c >= 800 ? "warn" : "info",
                c.ToString(CultureInfo.InvariantCulture),
                "a Apple garante ≥80% de capacidade até ~500–1000 ciclos conforme o modelo");

        // Capacidade real vs design — as duas vivem em BatteryData, não no topo.
        var maxCap = Field(io, "NominalChargeCapacity", "BatteryData.NominalChargeCapacity",
            "AppleRawMaxCapacity", "BatteryData.AppleRawMaxCapacity");
        var design = Field(io, "DesignCapacity", "BatteryData.DesignCapacity");
        if (int.TryParse(maxCap, NumberStyles.Integer, CultureInfo.InvariantCulture, out var max)
            && int.TryParse(design, NumberStyles.Integer, CultureInfo.InvariantCulture, out var des)
            && IosDecisions.Capacity(max, des) is { } cap)
            r.Set("battery.capacity", cap.Status, cap.Value, cap.Note);

        // Temperatura: "Temperature" (gerações antigas, centésimos de °C) ou
        // "AverageBattSkinTemp" (iOS recente, já em °C). O 0 de
        // DeadBatteryBootData já não entra — 0 significa ausência de leitura.
        if (IosDecisions.TemperatureFrom(
                Field(io, "Temperature", "AverageBattSkinTemp")) is { } temp)
            r.Set("battery.temp", temp.Status, temp.Value, temp.Note);

        if (Field(io, "Voltage") is { } volt
            && int.TryParse(volt, NumberStyles.Integer, CultureInfo.InvariantCulture, out var mV))
            r.Set("battery.voltage", "info", $"{mV} mV");

        if (Field(io, "InstantAmperage") is { } amp
            && long.TryParse(amp, NumberStyles.Integer, CultureInfo.InvariantCulture, out var mA))
        {
            // IsCharging do topo já não é pisado pelo ChargerData.
            var charging = Truthy(Field(io, "IsCharging"));
            r.Set("battery.current", "info",
                $"{Math.Abs(mA)} mA — {(charging ? "a carregar" : "a descarregar")}",
                charging ? null : "valor alto a descarregar parado sugere consumo anómalo");
        }

        // Carregador ligado: potência negociada — só existe em AdapterDetails.
        if (int.TryParse(Field(io, "AdapterDetails.Watts"), NumberStyles.Integer,
                CultureInfo.InvariantCulture, out var w)
            && IosDecisions.ChargerWatts(Truthy(Field(io, "ExternalConnected")), w,
                Field(io, "AdapterDetails.Description")) is { } charger)
            r.Set("battery.charger", charger.Status, charger.Value, charger.Note);

        if (Field(io, "Serial") is { Length: > 0 } batSerial)
            r.Set("battery.serial", "info", batSerial,
                "série da célula — compara com o histórico para detetar bateria trocada");

        if (Field(io, "ManufactureDate") is { Length: > 0 } mfg)
            r.Set("battery.mfg", "info", mfg, "data de fabrico da bateria");
    }

    /// <summary>MobileGestalt via idevicediagnostics — specs que o lockdownd não expõe.</summary>
    private void CollectGestalt(DeviceReport r, string udid, CancellationToken ct)
    {
        var xml = RunTool(udid,
            "mobilegestalt RegulatoryModelNumber ModelNumber DeviceColor DeviceEnclosureColor " +
            "RegionInfo BluetoothAddress MainScreenHeight MainScreenWidth MainScreenScale TelephonyCapability",
            15000, ct);
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
    private void CollectCrashLogs(DeviceReport r, string udid, CancellationToken ct)
    {
        var exe = ToolPath("idevicecrashreport.exe");
        if (exe is null) return;
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
            using var kill = ct.Register(() =>
            {
                try { if (!p.HasExited) p.Kill(entireProcessTree: true); } catch { }
            });
            if (!p.WaitForExit(25_000))
            {
                try { p.Kill(entireProcessTree: true); } catch { }
                return;
            }

            var files = Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories)
                .OrderByDescending(File.GetLastWriteTimeUtc).Take(60).ToList();
            if (files.Count == 0)
            {
                r.Set("logs.panics", "pass", "0", "sem panic/crash logs no dispositivo");
                return;
            }

            var panics = new List<string>();
            // Marcador à parte: um thermalmonitord é o MESMO panic, não um segundo.
            var thermalPanic = false;
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
                    // thermalmonitord sem check-ins = flex de carga/sensor
                    // térmico solto: sinaliza-se à parte, sem contar como panic extra.
                    if (IsThermalPanic(text)) thermalPanic = true;
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
            // Padrão de bancada Apple: sai salientado à parte, porque aponta
            // para peça (flex/sensor) e não para software.
            if (thermalPanic)
                r.Set("logs.thermal", "warn", "thermalmonitord", ThermalPanicHint);
            if (crashes + jetsam > 0)
                r.Set("logs.crashes", "info", $"{crashes} apps · {jetsam} jetsam",
                    "crashes de apps = software; jetsam = pressão de memória, não defeito");
        }
        catch { }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }
}
