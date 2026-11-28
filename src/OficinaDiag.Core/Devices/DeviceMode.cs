namespace OficinaDiag.Devices;

/// <summary>
/// O modo de arranque/ligação de um aparelho — o conceito único que governa
/// a UI do pipeline (docs/pipeline-diag-reparacao.md §2). A deteção é por
/// VID/PID USB (WMI no Windows) + estados adb/fastboot/usbmux; este enum é a
/// superfície tipada que o resto do Core e a UI consomem em vez das strings
/// de estado soltas.
/// </summary>
public enum DeviceMode
{
    /// <summary>Estado por determinar / estranho ao adb.</summary>
    Unknown,

    /// <summary>Android a correr, adb autorizado (adb state "device").</summary>
    AndroidReady,

    /// <summary>Android ligado mas a depuração USB por autorizar.</summary>
    AndroidUnauthorized,

    /// <summary>Android em recovery (adb state "recovery").</summary>
    AndroidRecovery,

    /// <summary>Android em sideload (adb state "sideload").</summary>
    AndroidSideload,

    /// <summary>Android offline/outro estado intermédio do adb.</summary>
    AndroidOffline,

    /// <summary>Android no bootloader — falado por fastboot.</summary>
    Fastboot,

    /// <summary>
    /// MediaTek BROM/preloader — a porta USB de arranque dos chipsets MTK
    /// (VID_0E8D PID_0003/2000, "PreLoader"). Modo morto distinto do EDL
    /// Qualcomm: ferramentas e procedimentos diferentes.
    /// </summary>
    MtkBrom,

    /// <summary>
    /// Qualcomm HS-USB QDLoader 9008 / Xiaomi FF18 / SPD-Unisoc — Emergency
    /// Download.
    /// </summary>
    QualcommEdl,

    /// <summary>Samsung Download mode (Odin).</summary>
    SamsungOdin,

    /// <summary>iPhone/iPad a correr iOS — falado por usbmux/libimobiledevice.</summary>
    IosNormal,

    /// <summary>iPhone/iPad em DFU (VID_05AC PID_1227 / "DFU Mode").</summary>
    IosDfu,

    /// <summary>iPhone/iPad em recovery do iOS ("Apple Mobile Device (Recovery Mode)").</summary>
    IosRecovery,
}

public static class DeviceModeMapping
{
    /// <summary>
    /// Converte o estado textual herdado (adb/fastboot/ServiceModeUsb) no
    /// modo tipado. "device" é o adb normal; "edl" cobre Qualcomm EDL e o
    /// preloader SPD; "brom" é o MediaTek separado na Fase 1 do pipeline.
    /// </summary>
    public static DeviceMode FromState(string state) => state switch
    {
        "device" or "ready" => DeviceMode.AndroidReady,
        "unauthorized" => DeviceMode.AndroidUnauthorized,
        "recovery" => DeviceMode.AndroidRecovery,
        "sideload" => DeviceMode.AndroidSideload,
        "offline" => DeviceMode.AndroidOffline,
        "fastboot" => DeviceMode.Fastboot,
        "brom" => DeviceMode.MtkBrom,
        "edl" => DeviceMode.QualcommEdl,
        "odin" => DeviceMode.SamsungOdin,
        "ios" => DeviceMode.IosNormal,
        "dfu" => DeviceMode.IosDfu,
        "iosrecovery" => DeviceMode.IosRecovery,
        _ => DeviceMode.Unknown,
    };
}
