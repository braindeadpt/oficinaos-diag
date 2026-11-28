using System.IO.Compression;
using System.Text.RegularExpressions;

namespace OficinaDiag.Devices;

/// <summary>
/// Contagens de crashes extraídas do `adb bugreport` — pipeline fase 2.
/// O bugreport contém dados pessoais do dono do telefone (mensagens,
/// contas, localização): por isso exige consentimento explícito do operador.
/// O texto principal é percorrido em memória para contar tags dropbox, mas
/// daí saem APENAS contagens e tipos para o relatório — nenhum conteúdo é
/// extraído, guardado ou enviado. O zip é apagado depois da contagem.
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

    /// <summary>
    /// Conta um zip de bugreport aberto. Duas fontes, deduplicadas:
    /// 1. nomes de entradas (tombstones, ficheiros dropbox) — o mesmo crash
    ///    nativo pode vir em texto e em protobuf (.pb), por isso deduplica-se
    ///    pelo nome base sem extensão;
    /// 2. tags dropbox no texto principal do bugreport — no Android de
    ///    produção os crashes de sistema/apps NÃO são entradas próprias do
    ///    zip, aparecem como linhas «tag@timestamp» na secção do dropbox.
    ///    Contam-se só as linhas de tag — o conteúdo nunca é extraído.
    /// </summary>
    public static Counts CountEntries(ZipArchive zip)
    {
        var tombstones = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        int appCrashes = 0, anrs = 0, kernelPanics = 0, systemCrashes = 0;
        var dropboxTags = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        string? mainEntry = null;

        foreach (var e in zip.Entries)
        {
            var name = e.FullName.Replace('\\', '/');
            if (mainEntry is null && Regex.IsMatch(name, @"bugreport-[^/]*\.txt$"))
                mainEntry = e.FullName;
            switch (Classify(e.FullName))
            {
                case "tombstone":
                    // tombstone_00 e tombstone_00.pb são o mesmo crash.
                    var baseName = Regex.Replace(name, @"\.(pb|txt)$", "", RegexOptions.IgnoreCase);
                    tombstones.Add(baseName);
                    break;
                case "appcrash" or "anr" or "systemcrash":
                    // Entradas dropbox alimentam o mesmo conjunto de tags das
                    // linhas de texto — dedupe cross-source pelo tag@timestamp.
                    var tagKey = Regex.Match(name, @"(data_app|system_app|system_server)[^/]*@\d+",
                        RegexOptions.IgnoreCase);
                    if (tagKey.Success) dropboxTags.Add(tagKey.Value);
                    else if (Classify(e.FullName) == "anr") anrs++;
                    else if (Classify(e.FullName) == "systemcrash") systemCrashes++;
                    else appCrashes++;
                    break;
                case "kernelpanic": kernelPanics++; break;
            }
        }

        // Tags dropbox no texto principal — produção AOSP lista-os aqui e não
        // como entradas. Conta só pares tag@timestamp distintos.
        if (mainEntry is not null)
        {
            using var stream = zip.GetEntry(mainEntry)!.Open();
            using var reader = new StreamReader(stream);
            string? line;
            while ((line = reader.ReadLine()) is not null)
            {
                var m = Regex.Match(line.Trim(),
                    @"^(data_app_crash|data_app_anr|data_app_wtf|system_app_crash|system_server_anr|system_server_crash|SYSTEM_TOMBSTONE)@");
                if (m.Success) dropboxTags.Add(line.Trim().Split('\n')[0]);
            }
        }
        foreach (var tag in dropboxTags)
        {
            if (tag.Contains("anr", StringComparison.OrdinalIgnoreCase)) anrs++;
            // system_app_crash é uma APP (de sistema) a crashar — software,
            // como qualquer app. Crash do sistema mesmo é o system_server
            // ou a tombstone de sistema.
            else if (tag.Contains("system_server", StringComparison.OrdinalIgnoreCase)
                     || tag.StartsWith("SYSTEM_TOMBSTONE", StringComparison.OrdinalIgnoreCase)) systemCrashes++;
            else appCrashes++;
        }

        return new Counts(tombstones.Count, appCrashes, anrs, kernelPanics, systemCrashes);
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
