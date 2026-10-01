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

        await Task.Run(() =>
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

                    r.Set("device.activation", "pass", GetValue(client, null, "ActivationState"),
                        "Activated = o equipamento está funcional");
                    r.Set("device.sim", "info", GetValue(client, null, "SIMStatus"));
                    r.Set("device.wifi", "info", GetValue(client, null, "WiFiAddress"));

                    // Storage
                    if (ulong.TryParse(GetValue(client, null, "TotalDiskCapacity"), out var total))
                    {
                        ulong.TryParse(GetValue(client, null, "TotalDataAvailable"), out var free);
                        var pctFree = total > 0 ? free * 100.0 / total : 0;
                        r.Set("storage", pctFree < 10 ? "warn" : "pass",
                            $"{free / 1_073_741_824.0:0.#} GB livres de {total / 1_073_741_824.0:0.#} GB");
                    }

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
            }
        });
        return r;
    }
}
