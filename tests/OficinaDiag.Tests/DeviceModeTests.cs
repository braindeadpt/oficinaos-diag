using OficinaDiag.Devices;
using Xunit;

namespace OficinaDiag.Tests;

/// <summary>
/// Fase 1 do pipeline: a tabela VID/PID → <see cref="DeviceMode"/> — um caso
/// por modo, mais a derivação do modo tipado a partir do estado textual
/// (adb/fastboot/usbmux). Complementa os <see cref="ServiceModeUsbTests"/>
/// (que cobrem os falsos positivos) com a cobertura positiva completa: cada
/// modo detetável tem aqui pelo menos um par (nome, DeviceID) realista.
/// </summary>
public sealed class DeviceModeTests
{
    public static TheoryData<string, string, string, DeviceMode> TabelaVidPid => new()
    {
        // nome do driver, DeviceID do Win32_PnPEntity, estado esperado, modo
        { "Qualcomm HS-USB QDLoader 9008 (COM5)", @"USB\VID_05C6&PID_9008\6&2A1B3C4D&0&2", "edl", DeviceMode.QualcommEdl },
        { "Xiaomi EDL", @"USB\VID_2717&PID_FF18\6&2A1B3C4D&0&3", "edl", DeviceMode.QualcommEdl },
        { "SCI USB2Serial (COM9)", @"USB\VID_1782&PID_4D00\6&2A1B3C4D&0&5", "edl", DeviceMode.QualcommEdl },
        { "MediaTek USB Port (COM4)", @"USB\VID_0E8D&PID_0003\6&2A1B3C4D&0&1", "brom", DeviceMode.MtkBrom },
        { "MediaTek PreLoader USB VCOM (Android) (COM7)", @"USB\VID_0E8D&PID_2000\6&2A1B3C4D&0&9", "brom", DeviceMode.MtkBrom },
        { "Gadget Serial", @"USB\VID_04E8&PID_685D\6&2A1B3C4D&0&4", "odin", DeviceMode.SamsungOdin },
        { "Apple Mobile Device (DFU Mode)", @"USB\VID_05AC&PID_1227\000081100001234ABC", "dfu", DeviceMode.IosDfu },
        { "Apple Mobile Device (Recovery Mode)", @"USB\VID_05AC&PID_1281\000081100001234ABC", "iosrecovery", DeviceMode.IosRecovery },
    };

    [Theory]
    [MemberData(nameof(TabelaVidPid))]
    public void VidPid_MapaParaOModoEsperado(string name, string deviceId, string state, DeviceMode mode)
    {
        var m = ServiceModeUsb.MatchServiceMode(name, deviceId);

        Assert.NotNull(m);
        Assert.Equal(state, m.State);
        Assert.Equal(mode, DeviceModeMapping.FromState(m.State));
    }

    [Theory]
    [InlineData(DeviceKind.Android, "ready", DeviceMode.AndroidReady)]
    [InlineData(DeviceKind.Android, "device", DeviceMode.AndroidReady)]
    [InlineData(DeviceKind.Ios, "ready", DeviceMode.IosNormal)]
    [InlineData(DeviceKind.Android, "unauthorized", DeviceMode.AndroidUnauthorized)]
    [InlineData(DeviceKind.Android, "recovery", DeviceMode.AndroidRecovery)]
    [InlineData(DeviceKind.Android, "sideload", DeviceMode.AndroidSideload)]
    [InlineData(DeviceKind.Android, "offline", DeviceMode.AndroidOffline)]
    [InlineData(DeviceKind.Android, "fastboot", DeviceMode.Fastboot)]
    [InlineData(DeviceKind.Android, "brom", DeviceMode.MtkBrom)]
    [InlineData(DeviceKind.Android, "edl", DeviceMode.QualcommEdl)]
    [InlineData(DeviceKind.Android, "odin", DeviceMode.SamsungOdin)]
    [InlineData(DeviceKind.Ios, "dfu", DeviceMode.IosDfu)]
    [InlineData(DeviceKind.Ios, "iosrecovery", DeviceMode.IosRecovery)]
    [InlineData(DeviceKind.Android, "batota", DeviceMode.Unknown)]
    public void DetectedDevice_ModoDerivadoDeKindEEstado(DeviceKind kind, string state, DeviceMode esperado)
    {
        // O "ready" não chega para separar Android de iOS — o adb fala de um
        // e o usbmux do outro, por isso o Kind entra na conta.
        var d = new DetectedDevice(kind, "id", "label", state);

        Assert.Equal(esperado, d.Mode);
    }

    [Fact]
    public void DetectedDevice_IosNormalNaoConfundeComAndroid()
    {
        // iPhone normal chega do usbmux com o State por omissão ("ready") —
        // tem de dar IosNormal, nunca AndroidReady.
        var d = new DetectedDevice(DeviceKind.Ios, "udid-abc", "iPhone/iPad");

        Assert.Equal(DeviceMode.IosNormal, d.Mode);
    }
}
