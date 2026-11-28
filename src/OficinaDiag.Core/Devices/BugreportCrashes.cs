using System.IO.Compression;
using System.Text.RegularExpressions;

namespace OficinaDiag.Devices;

/// <summary>
/// Contagens de crashes extraídas do `adb bugreport` — pipeline fase 2.
/// O bugreport contém dados pessoais do dono do telefone (mensagens,
/// contas, localização): por isso exige consentimento explícito do operador
/// e daqui saem APENAS contagens e tipos — nomes de ficheiros tombstone/
/// dropbox e tags, nunca conteúdo. O zip é apagado depois da contagem.
/// </summary>
public static class BugreportCrashes
{
    /// <summary>Contagens por tipo — a única informação que sai do bugreport.</summary>
    public sealed record Counts(int Tombstones, int AppCrashes, int Anrs, int KernelPanics, int SystemCrashes)
    {
        public int Total => Tombstones + AppCrashes + Anrs + KernelPanics + SystemCrashes;
    }

    /// <summary>
    /// Classifica um nome de entrada do zip do bugreport. Os tipos:
    /// tombstones nativos (hardware/sinal forte), crashes e ANRs de apps
    /// (software), panics de kernel e crashes de sistema.
    /// </summary>
    internal static string? Classify(string entryName)
    {
        var n = entryName.Replace('\\', '/');
        if (n.Contains("/tombstone", StringComparison.OrdinalIgnoreCase)) return "tombstone";
        if (Regex.IsMatch(n, @"system_app_crash|app_crash", RegexOptions.IgnoreCase)) return "appcrash";
        if (Regex.IsMatch(n, @"anr", RegexOptions.IgnoreCase)) return "anr";
        if (Regex.IsMatch(n, @"kernel_panic|last_kmsg|ramdump", RegexOptions.IgnoreCase)) return "kernelpanic";
        if (Regex.IsMatch(n, @"system_server_crash|system_crash", RegexOptions.IgnoreCase)) return "systemcrash";
        return null;
    }

    /// <summary>Conta as entradas de um zip de bugreport aberto — sem ler conteúdo.</summary>
    public static Counts CountEntries(ZipArchive zip)
    {
        int tombstones = 0, appCrashes = 0, anrs = 0, kernelPanics = 0, systemCrashes = 0;
        foreach (var e in zip.Entries)
        {
            switch (Classify(e.FullName))
            {
                case "tombstone": tombstones++; break;
                case "appcrash": appCrashes++; break;
                case "anr": anrs++; break;
                case "kernelpanic": kernelPanics++; break;
                case "systemcrash": systemCrashes++; break;
            }
        }
        return new Counts(tombstones, appCrashes, anrs, kernelPanics, systemCrashes);
    }

    /// <summary>Resumo de uma linha para o relatório — contagens e tipos só.</summary>
    public static string Summarize(Counts c)
    {
        if (c.Total == 0) return "sem crashes registados no bugreport";
        var parts = new List<string>();
        if (c.Tombstones > 0) parts.Add($"{c.Tombstones} tombstones nativos");
        if (c.SystemCrashes > 0) parts.Add($"{c.SystemCrashes} crashes de sistema");
        if (c.KernelPanics > 0) parts.Add($"{c.KernelPanics} panics de kernel");
        if (c.AppCrashes > 0) parts.Add($"{c.AppCrashes} crashes de apps");
        if (c.Anrs > 0) parts.Add($"{c.Anrs} ANRs");
        return string.Join(" · ", parts);
    }

    /// <summary>
    /// Pior estado do resumo: panics de kernel/tombstones repetidos são
    /// indício forte de hardware (não prova — drivers também panicam);
    /// crashes de apps ficam informativos.
    /// </summary>
    public static string Status(Counts c) =>
        c.KernelPanics > 0 || c.Tombstones > 5 || c.SystemCrashes > 3 ? "warn" : "info";
}
