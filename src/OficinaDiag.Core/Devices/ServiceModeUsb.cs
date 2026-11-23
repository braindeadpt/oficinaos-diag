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
    /// <param name="Vid">Se não-null, o DeviceID tem de conter este prefixo
    /// (ex.: "vid_04e8") — obrigatório quando o nome do driver é genérico e
    /// periféricos de outros fabricantes o podem usar ("Gadget Serial",
    /// "DFU Mode").</param>
    private sealed record Rule(string State, DeviceKind Kind, string Label, string[] Needles, string? Vid = null);

    // Needles são comparadas (case-insensitive) contra Name e DeviceID do
    // Win32_PnPEntity. Cuidado com VID/PID partilhados: Samsung usa
    // VID_04E8&PID_685D tanto no ADB normal como no Download mode — por isso
    // o Odin exige VID Samsung + nome "Gadget Serial", nunca o PID sozinho.
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
        // MediaTek bootrom/preloader — "MediaTek USB Port"/"PreLoader".
        // O MT65xx PreLoader enumera como PID_0003 ou PID_2000 (usb.ids).
        new("edl", DeviceKind.Android, "Android em modo download (preloader)",
            ["vid_0e8d&pid_0003", "vid_0e8d&pid_2000", "preloader usb vcom"]),
        // SPD/Unisoc download — "SCI USB2Serial".
        new("edl", DeviceKind.Android, "Android em modo download (preloader)",
            ["vid_1782&pid_4d00"]),
        // Samsung Download Mode (Odin) — "Gadget Serial". O PID 685D é
        // partilhado com o ADB normal e o nome existe em periféricos Linux:
        // exige VID Samsung + nome de modo.
        new("odin", DeviceKind.Android, "Android em modo Download (Odin)",
            ["gadget serial"], "vid_04e8"),
        // iPhone/iPad DFU — PID_1227 é exclusivo do modo DFU; o nome
        // "DFU Mode" sozinho aparece em STM32 e outros — exige VID Apple.
        new("dfu", DeviceKind.Ios, "iPhone/iPad em DFU",
            ["vid_05ac&pid_1227"]),
        new("dfu", DeviceKind.Ios, "iPhone/iPad em DFU",
            ["dfu mode"], "vid_05ac"),
        // iPhone/iPad Recovery — nome do driver é fiável; o PID 1281 também
        // aparece em modos normais de iPads antigos, por isso só o nome.
        new("iosrecovery", DeviceKind.Ios, "iPhone/iPad em recovery",
            ["apple mobile device (recovery mode)"]),
        // Variante com o nome genérico — exige VID Apple porque "recovery
        // mode" sozinho também aparece noutros drivers USB.
        new("iosrecovery", DeviceKind.Ios, "iPhone/iPad em recovery",
            ["recovery mode"], "vid_05ac"),
    ];

    /// <summary>
    /// Match puro: devolve o primeiro estado de serviço que casa com o nome
    /// ou DeviceID do dispositivo, ou null se for um USB normal.
    /// </summary>
    internal static ServiceModeMatch? MatchServiceMode(string? name, string? deviceId)
    {
        if (string.IsNullOrEmpty(deviceId) && string.IsNullOrEmpty(name)) return null;
        var id = deviceId ?? "";
        foreach (var r in Rules)
        {
            if (r.Vid is not null
                && !id.Contains(r.Vid, StringComparison.OrdinalIgnoreCase))
                continue;
            foreach (var n in r.Needles)
                if ((name?.Contains(n, StringComparison.OrdinalIgnoreCase) ?? false)
                    || id.Contains(n, StringComparison.OrdinalIgnoreCase))
                    return new ServiceModeMatch(r.Kind, r.State, r.Label);
        }
        return null;
    }

    /// <summary>
    /// Snapshot dos dispositivos USB em modo de serviço. Sem cache: a WMI
    /// enumera as entidades PnP (~1k linhas) mas o adb já spawna um processo
    /// por poll — e um cache de segundos podia esconder uma entrada breve
    /// num modo de serviço logo após uma ronda vazia.
    /// </summary>
    internal static IReadOnlyList<DetectedDevice> List()
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            return Array.Empty<DetectedDevice>();
        return Query();
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
