namespace OficinaDiag.Devices;

/// <summary>
/// Decisões do <c>AndroidCollector</c> sobre o que escrever no relatório,
/// separadas do adb para serem testadas sem dispositivo. O coletor só aplica
/// o resultado em <c>r.Set(reading.Status, reading.Value, reading.Note)</c> —
/// nenhuma regra de estado fica lá.
/// </summary>
internal static class AndroidDecisions
{
    /// <summary>Uma linha do relatório: estado, valor e nota opcional.</summary>
    internal readonly record struct Reading(string Status, string Value, string? Note);

    /// <summary>
    /// Bootloader a partir de <c>ro.boot.verifiedbootstate</c>. A prop ausente
    /// é `skipped`, não `warn`: não se sabe, logo não se penaliza.
    /// </summary>
    internal static Reading Bootloader(string? verifiedBootState) => verifiedBootState is null
        ? new Reading("skipped", "prop ausente",
            "ro.boot.verifiedbootstate não exposto neste Android — não verificável")
        : new Reading(verifiedBootState == "green" ? "pass" : "warn", verifiedBootState,
            "Bootloader verification state (green = stock, locked)");

    /// <summary>
    /// MDM a partir do `dumpsys device_policy`. `security.mdm` está nos
    /// HardLocks do Grader: qualquer falso positivo capa o grau em D.
    /// </summary>
    internal static Reading Mdm(bool hasDeviceOwner) => hasDeviceOwner
        ? new Reading("warn", "gestão remota ativa",
            "device/profile owner definido — pode estar bloqueado a empresa ou contrato de leasing")
        : new Reading("pass", "sem gestão remota", null);

    /// <summary>
    /// Bateria ausente (`present: false`, bancada/emulador): nada de nível,
    /// temperatura ou tensão é medível. `null` = há bateria, seguir a recolha.
    /// </summary>
    internal static Reading? BatteryPresence(bool? present) => present == false
        ? new Reading("skipped", "sem bateria exposta",
            "dumpsys battery reporta present=false — nível, temperatura e tensão não são medíveis aqui")
        : null;

    /// <summary>
    /// Capacidade por `mSavedBatteryAsoc` quando o kernel não expõe
    /// `charge_full` (Samsung OneUI): o fuel gauge dá a saúde em percentagem.
    /// `null` = sem leitura válida, não se escreve capacidade.
    /// </summary>
    internal static Reading? CapacityFromSavedAsoc(int? asoc) => asoc is { } a
        ? new Reading(a >= 80 ? "pass" : a >= 60 ? "warn" : "fail", $"{a}%",
            "saúde calculada pelo fuel gauge OneUI (mSavedBatteryAsoc) — kernel sem charge_full")
        : null;
}
