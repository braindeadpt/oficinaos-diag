using System.Management;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using OficinaDiag.Log;

namespace OficinaDiag.Devices;

/// <summary>Correspondência de um dispositivo USB a um modo de serviço.</summary>
/// <param name="Kind">Android ou iOS — decide o gate "nenhum ready deste kind".</param>
/// <param name="State">edl | odin | dfu | iosrecovery — estados que o adb/fastboot/idevice não listam.</param>
/// <param name="Label">Etiqueta PT embutida no status localizado (padrão dos outros estados).</param>
internal sealed record ServiceModeMatch(DeviceKind Kind, string State, string Label);

/// <summary>
/// Modos de serviço invisíveis ao adb/fastboot/idevice: EDL Qualcomm/Xiaomi/
/// MediaTek/SPD, Download/Odin Samsung e DFU/recovery iOS. Estes estados
/// aparecem como dispositivos USB PnP com VID/PID próprios — o adb não os
/// lista, logo um telefone preso num deles parecia "nada detetado".
/// WMI (<see cref="Win32_PnPEntity"/>) só existe no Windows; noutros OS
/// devolve vazio. O matcher é puro para os testes o exercitarem sem WMI.
/// </summary>
internal static class ServiceModeUsb
{
    private sealed record Rule(string State, DeviceKind Kind, string Label, string[] Needles);

    // Needles são comparadas (case-insensitive) contra Name e DeviceID do
    // Win32_PnPEntity. Cuidado com VID/PID partilhados: Samsung usa
    // VID_04E8&PID_685D tanto no ADB normal como no Download mode — por isso
    // o Odin só casa pelo nome do driver ("Gadget Serial"), nunca pelo PID.
    private static readonly Rule[] Rules =
    [
        // Qualcomm HS-USB QDLoader 9008 — EDL/Sahara genérico (inclui muitos
        // Xiaomi que caem no enumerador Qualcomm quando entram em EDL).
        new("edl", DeviceKind.Android, "Android em modo download (EDL)",
            ["vid_05c6&pid_9008", "qdloader 9008"]),
        // Xiaomi MiFlash EDL — VID_2717 PID_FF18 (demais PIDs FFxx são
        // fastboot/MTP+ADB e não devem casar aqui).
        new("edl", DeviceKind.Android, "Android em modo download (EDL)",
            ["vid_2717&pid_ff18"]),
        // MediaTek bootrom/preloader — "MediaTek USB Port".
        new("edl", DeviceKind.Android, "Android em modo download (preloader)",
            ["vid_0e8d&pid_0003"]),
        // SPD/Unisoc download — "SCI USB2Serial".
        new("edl", DeviceKind.Android, "Android em modo download (preloader)",
            ["vid_1782&pid_4d00"]),
        // Samsung Download Mode (Odin) — "Gadget Serial". O PID 685D é
        // partilhado com o ADB normal: só o nome do driver distingue.
        new("odin", DeviceKind.Android, "Android em modo Download (Odin)",
            ["gadget serial"]),
        // iPhone/iPad DFU — VID_05AC PID_1227 é exclusivo do modo DFU.
        new("dfu", DeviceKind.Ios, "iPhone/iPad em DFU",
            ["vid_05ac&pid_1227", "dfu mode"]),
        // iPhone/iPad Recovery — nome do driver é fiável; o PID 1281 também
        // aparece em modos normais de iPads antigos, por isso só o nome.
        new("iosrecovery", DeviceKind.Ios, "iPhone/iPad em recovery",
            ["apple mobile device (recovery mode)"]),
    ];

    /// <summary>
    /// Match puro: devolve o primeiro estado de serviço que casa com o nome
    /// ou DeviceID do dispositivo, ou null se for um USB normal.
    /// </summary>
    internal static ServiceModeMatch? MatchServiceMode(string? name, string? deviceId)
    {
        if (string.IsNullOrEmpty(deviceId) && string.IsNullOrEmpty(name)) return null;
        foreach (var r in Rules)
            foreach (var n in r.Needles)
                if ((name?.Contains(n, StringComparison.OrdinalIgnoreCase) ?? false)
                    || (deviceId?.Contains(n, StringComparison.OrdinalIgnoreCase) ?? false))
                    return new ServiceModeMatch(r.Kind, r.State, r.Label);
        return null;
    }

    private static readonly object Gate = new();
    private static List<DetectedDevice>? _cache;
    private static DateTime _cacheAt = DateTime.MinValue;

    /// <summary>
    /// Snapshot dos dispositivos USB em modo de serviço. Cache de 8 s: a WMI
    /// enumera todas as entidades PnP (~1k linhas) e o poll corre a cada 2 s —
    /// sem cache era trabalho desperdiçado quando não há telefone nenhum.
    /// </summary>
    internal static IReadOnlyList<DetectedDevice> List()
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            return Array.Empty<DetectedDevice>();
        lock (Gate)
        {
            if (_cache is not null && (DateTime.UtcNow - _cacheAt).TotalSeconds < 8)
                return _cache;
            var list = Query();
            _cache = list;
            _cacheAt = DateTime.UtcNow;
            return list;
        }
    }

    /// <summary>WMI real — isolado para o matcher puro ficar testável sem Windows.</summary>
    [SupportedOSPlatform("windows")]
    private static List<DetectedDevice> Query()
    {
        var found = new List<DetectedDevice>();
        try
        {
            using var searcher = new ManagementObjectSearcher(
                "SELECT Name, DeviceID, Present FROM Win32_PnPEntity");
            foreach (var o in searcher.Get())
            {
                if (o["Present"] is bool present && !present) continue;
                var deviceId = o["DeviceID"] as string;
                if (string.IsNullOrEmpty(deviceId)) continue;
                var m = MatchServiceMode(o["Name"] as string, deviceId);
                if (m is not null)
                    found.Add(new DetectedDevice(m.Kind, deviceId, m.Label, m.State));
            }
        }
        catch (Exception ex)
        {
            // WMI indisponível/falhou — ronda vazia, nunca deitar a app.
            AppLog.Exception("ServiceModeUsb.Query", ex);
        }
        return found;
    }
}
