using OficinaDiag.Devices;
using Xunit;

namespace OficinaDiag.Tests;

/// <summary>
/// Fase A.1: o matcher puro de <see cref="ServiceModeUsb"/> — os modos de
/// serviço que o adb/fastboot/idevice não enumeram (EDL, Odin, DFU, recovery
/// iOS). Testado sem WMI: cada caso é um par (Name, DeviceID) realista do
/// Win32_PnPEntity, incluindo os falsos positivos que os VID/PID partilhados
/// poderiam causar (Samsung 685D no ADB normal).
/// </summary>
public sealed class ServiceModeUsbTests
{
    [Fact]
    public void QualcommEdl9008_DetetaAndroidEmEdl()
    {
        var m = ServiceModeUsb.MatchServiceMode(
            "Qualcomm HS-USB QDLoader 9008 (COM5)",
            @"USB\VID_05C6&PID_9008\6&2A1B3C4D&0&2");

        Assert.NotNull(m);
        Assert.Equal(DeviceKind.Android, m.Kind);
        Assert.Equal("edl", m.State);
    }

    [Fact]
    public void XiaomiEdlFf18_DetetaAndroidEmEdl()
    {
        var m = ServiceModeUsb.MatchServiceMode(
            "Xiaomi EDL",
            @"USB\VID_2717&PID_FF18\6&2A1B3C4D&0&3");

        Assert.NotNull(m);
        Assert.Equal(DeviceKind.Android, m.Kind);
        Assert.Equal("edl", m.State);
    }

    [Fact]
    public void MediaTekPreloader_DetetaAndroidEmEdl()
    {
        var m = ServiceModeUsb.MatchServiceMode(
            "MediaTek USB Port (COM4)",
            @"USB\VID_0E8D&PID_0003\6&2A1B3C4D&0&1");

        Assert.NotNull(m);
        Assert.Equal(DeviceKind.Android, m.Kind);
        Assert.Equal("edl", m.State);
    }

    [Fact]
    public void MediaTekPreloader2000_DetetaAndroidEmEdl()
    {
        // MT65xx PreLoader enumera como PID_2000 (usb.ids), não só 0003.
        var m = ServiceModeUsb.MatchServiceMode(
            "MediaTek PreLoader USB VCOM (Android) (COM7)",
            @"USB\VID_0E8D&PID_2000\6&2A1B3C4D&0&9");

        Assert.NotNull(m);
        Assert.Equal(DeviceKind.Android, m.Kind);
        Assert.Equal("edl", m.State);
    }

    [Fact]
    public void MediaTekPreloader2000_SoPeloDeviceId()
    {
        // Se o Windows só der o DeviceID (sem nome de driver útil), o PID chega.
        var m = ServiceModeUsb.MatchServiceMode(
            "USB Serial Device",
            @"USB\VID_0E8D&PID_2000\6&2A1B3C4D&0&9");

        Assert.NotNull(m);
        Assert.Equal("edl", m.State);
    }

    [Fact]
    public void MediaTekPreloader2000_SoPeloNome()
    {
        // E se o DeviceID vier sem o PID esperado, o nome do driver chega.
        var m = ServiceModeUsb.MatchServiceMode(
            "MediaTek PreLoader USB VCOM (Android) (COM7)",
            @"USB\VID_0E8D&PID_6000\6&2A1B3C4D&0&A");

        Assert.NotNull(m);
        Assert.Equal("edl", m.State);
    }

    [Fact]
    public void SamsungGadgetSerial_DetetaOdin()
    {
        var m = ServiceModeUsb.MatchServiceMode(
            "Gadget Serial",
            @"USB\VID_04E8&PID_685D\6&2A1B3C4D&0&4");

        Assert.NotNull(m);
        Assert.Equal(DeviceKind.Android, m.Kind);
        Assert.Equal("odin", m.State);
    }

    [Fact]
    public void SamsungAdbNormal_NaoCasaOdin()
    {
        // Samsung ADB/MTP normal usa o MESMO VID_04E8&PID_685D — só o nome
        // "Gadget Serial" distingue o Download mode. Sem ele, não pode casar.
        var m = ServiceModeUsb.MatchServiceMode(
            "SAMSUNG Mobile USB Modem",
            @"USB\VID_04E8&PID_685D\6&2A1B3C4D&0&5");

        Assert.Null(m);
    }

    [Fact]
    public void GadgetSerialSemVidSamsung_NaoCasaOdin()
    {
        // Gadget serial de periférico Linux (ex.: 0525:A4A6) — o nome
        // genérico só conta com VID Samsung.
        var m = ServiceModeUsb.MatchServiceMode(
            "Gadget Serial",
            @"USB\VID_0525&PID_A4A6\6&2A1B3C4D&0&8");

        Assert.Null(m);
    }

    [Fact]
    public void AppleDfu_DetetaIosEmDfu()
    {
        var m = ServiceModeUsb.MatchServiceMode(
            "Apple Mobile Device (DFU Mode)",
            @"USB\VID_05AC&PID_1227\000081100001234ABC");

        Assert.NotNull(m);
        Assert.Equal(DeviceKind.Ios, m.Kind);
        Assert.Equal("dfu", m.State);
    }

    [Fact]
    public void StmDfu_NaoCasaIosDfu()
    {
        // STM32 em DFU (0483:DF11) — "DFU Mode" no nome não chega: exige VID Apple.
        var m = ServiceModeUsb.MatchServiceMode(
            "STM Device in DFU Mode",
            @"USB\VID_0483&PID_DF11\6&2A1B3C4D&0&A");

        Assert.Null(m);
    }

    [Fact]
    public void AppleRecovery_DetetaIosEmRecovery()
    {
        var m = ServiceModeUsb.MatchServiceMode(
            "Apple Mobile Device (Recovery Mode)",
            @"USB\VID_05AC&PID_1281\000081100001234ABC");

        Assert.NotNull(m);
        Assert.Equal(DeviceKind.Ios, m.Kind);
        Assert.Equal("iosrecovery", m.State);
    }

    [Fact]
    public void AppleUsbNormal_NaoCasaRecovery()
    {
        // PID 1281 também aparece em iPads antigos em modo normal — o nome
        // do driver é a única prova fiável de recovery.
        var m = ServiceModeUsb.MatchServiceMode(
            "Apple Mobile Device USB Driver",
            @"USB\VID_05AC&PID_1281\000081100009999XYZ");

        Assert.Null(m);
    }

    [Fact]
    public void AndroidAdbNormal_NaoCasaNada()
    {
        var m = ServiceModeUsb.MatchServiceMode(
            "Android Composite ADB Interface",
            @"USB\VID_18D1&PID_4EE7\6&2A1B3C4D&0&6");

        Assert.Null(m);
    }

    [Fact]
    public void EntradasVazias_DevolvemNull()
    {
        Assert.Null(ServiceModeUsb.MatchServiceMode(null, null));
        Assert.Null(ServiceModeUsb.MatchServiceMode("", ""));
        Assert.Null(ServiceModeUsb.MatchServiceMode("USB genérico", null));
    }

    [Fact]
    public void NomeOuDeviceId_CadaUmChegaParaCasar()
    {
        // Nem sempre o driver expõe o nome — o DeviceID chega.
        var m = ServiceModeUsb.MatchServiceMode(
            null,
            @"USB\VID_05C6&PID_9008\6&2A1B3C4D&0&7");

        Assert.NotNull(m);
        Assert.Equal("edl", m.State);
    }
}
