namespace OficinaDiag.Devices;

/// <summary>
/// O modo do aparelho governa que etapas do pipeline aparecem (plano §2):
/// em modos de firmware morto (BROM/EDL/Odin/DFU/recovery iOS) só a
/// reparação se aplica; fastboot e recovery Android dão diagnóstico
/// limitado + reparação. Reparar nunca se esconde — qualquer modo a pode
/// precisar, e nos modos de firmware é tudo o que existe.
/// </summary>
public static class PipelineStages
{
    /// <summary>Modos em que não há OS para diagnosticar — só firmware.</summary>
    public static bool IsFirmwareOnly(DeviceMode mode) => mode switch
    {
        DeviceMode.MtkBrom or DeviceMode.QualcommEdl or DeviceMode.SamsungOdin
            or DeviceMode.IosDfu or DeviceMode.IosRecovery => true,
        _ => false,
    };

    /// <summary>① Diagnosticar aparece neste modo?</summary>
    public static bool AllowsDiagnosticar(DeviceMode mode) => !IsFirmwareOnly(mode);

    /// <summary>③ Verificar e ④ Entregar aparecem neste modo?</summary>
    public static bool AllowsVerificarEntregar(DeviceMode mode) => !IsFirmwareOnly(mode);
}
