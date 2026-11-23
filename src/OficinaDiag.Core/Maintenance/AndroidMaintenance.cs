using System.Globalization;
using System.Text.RegularExpressions;

namespace OficinaDiag.Maintenance;

/// <summary>O que o técnico escolheu correr no aparelho — cada ação é opcional.</summary>
public sealed class MaintenancePlan
{
    /// <summary>`pm trim-caches` — o sistema liberta o que considerar descartável.</summary>
    public bool TrimCaches { get; set; } = true;

    /// <summary>Apps de utilizador a limpar SÓ o cache (`pm clear --cache-only`).</summary>
    public List<string> ClearCachePackages { get; set; } = [];

    /// <summary>Escalas de animação a 0 — o "mais fluido" medido, não prometido.</summary>
    public bool ZeroAnimations { get; set; }

    /// <summary>Repõe escalas guardadas de uma limpeza anterior (null = não repor).</summary>
    public AnimationScales? RestoreAnimations { get; set; }

    /// <summary>Apps de utilizador a forçar paragem (`am force-stop`).</summary>
    public List<string> ForceStopPackages { get; set; } = [];

    /// <summary>`am kill-all` — fecha os processos em segundo plano.</summary>
    public bool KillBackgroundApps { get; set; }

    /// <summary>Modo poupança: null = não mexer, true = ligar, false = desligar.</summary>
    public bool? BatterySaver { get; set; }

    /// <summary>Abre o ecrã de otimização de bateria no telemóvel para revisão guiada.</summary>
    public bool OpenBatteryOptimization { get; set; }

    /// <summary>True quando o plano tem pelo menos uma ação — o diálogo avisa antes de correr.</summary>
    public bool HasAnyAction => TrimCaches
        || ClearCachePackages.Count > 0 || ForceStopPackages.Count > 0
        || KillBackgroundApps || ZeroAnimations || RestoreAnimations is not null
        || BatterySaver is not null || OpenBatteryOptimization;
}

/// <summary>
/// Limpeza/otimização Android medida: tira um "antes", corre as ações pedidas
/// e tira um "depois". Nunca inventa números — o que o adb não leu fica null.
/// Cada ação é independente: uma falha não aborta as restantes.
/// </summary>
public sealed class AndroidMaintenance
{
    /// <summary>
    /// `pm trim-caches` recebe o espaço LIVRE desejado — 512G é mais do que
    /// qualquer telefone tem, ou seja "liberta tudo o que considerares
    /// descartável". O comando não devolve quanto libertou: só o df antes/depois mede.
    /// </summary>
    private const string TrimCachesCommand = "pm trim-caches 512G";

    private readonly Func<string, Task<string?>> _shell;

    public AndroidMaintenance(string adbPath, string serial)
    {
        // O serial vem do adb; o AndroidCollector já valida os caracteres antes
        // de o passar ao processo — um serial hostil nunca chega ao sh remoto.
        var collector = new Devices.AndroidCollector(adbPath);
        _shell = cmd => collector.ShellAsync(serial, cmd, 15000);
    }

    /// <summary>Construtor para testes — o executor shell é falso.</summary>
    internal AndroidMaintenance(Func<string, Task<string?>> shell) => _shell = shell;

    /// <summary>Lista as apps de utilizador (`pm list packages -3`) para o técnico escolher.</summary>
    public async Task<IReadOnlyList<string>> ListUserPackagesAsync()
    {
        var output = await _shell("pm list packages -3");
        return MaintenanceParsing.ParseUserPackages(output);
    }

    /// <summary>Medição instantânea — 6 shells em sequência, ~2-3 s num telemóvel real.</summary>
    public async Task<MaintenanceSnapshot> SnapshotAsync()
    {
        var s = new MaintenanceSnapshot();

        var storage = await _shell("df /data");
        if (MaintenanceParsing.ParseDf(storage) is { } df)
        {
            s.StorageUsedBytes = df.UsedKb * 1024L;
            s.StorageFreeBytes = df.AvailKb * 1024L;
        }

        var mem = await _shell("cat /proc/meminfo");
        if (MaintenanceParsing.ParseMemAvailableKb(mem) is { } memKb)
            s.MemAvailableBytes = memKb * 1024L;

        var uptime = await _shell("cat /proc/uptime");
        s.UptimeSeconds = MaintenanceParsing.ParseUptimeSeconds(uptime) is { } up
            ? (long)up : null;

        var settings = await _shell(
            "settings get global window_animation_scale; " +
            "settings get global transition_animation_scale; " +
            "settings get global animator_duration_scale; " +
            "settings get global low_power");
        var (animations, lowPower) = MaintenanceParsing.ParseSettingsDump(settings);
        s.Animations = animations;
        s.LowPowerMode = lowPower;

        var batt = await _shell("dumpsys battery");
        var (level, temp) = MaintenanceParsing.ParseBattery(batt);
        s.BatteryLevelPct = level;
        s.BatteryTempC = temp;

        var procs = await _shell("ps -A 2>/dev/null | wc -l");
        s.RunningProcesses = MaintenanceParsing.ParseProcessCount(procs);

        return s;
    }

    /// <summary>
    /// Corre o plano inteiro: antes → ações → depois → relatório com deltas.
    /// Uma ação que falha fica registada como fail e as restantes correm na mesma.
    /// </summary>
    public async Task<MaintenanceReport> RunCleanupAsync(
        MaintenancePlan plan, MaintenanceReport? previous = null)
    {
        var before = await SnapshotAsync();
        var actions = new List<MaintenanceActionResult>();

        if (plan.TrimCaches)
            actions.Add(await TrimCachesAsync());

        if (plan.ClearCachePackages.Count > 0)
            actions.Add(await ClearAppCachesAsync(plan.ClearCachePackages));

        if (plan.ZeroAnimations)
            actions.Add(await SetAnimationsAsync(0.0, before?.Animations));

        if (plan.RestoreAnimations is { } restore)
            actions.Add(await RestoreAnimationsAsync(restore));

        if (plan.ForceStopPackages.Count > 0)
            actions.Add(await ForceStopAppsAsync(plan.ForceStopPackages));

        if (plan.KillBackgroundApps)
            actions.Add(await KillBackgroundAsync());

        if (plan.BatterySaver is { } saver)
            actions.Add(await SetBatterySaverAsync(saver));

        if (plan.OpenBatteryOptimization)
            actions.Add(await OpenBatteryOptimizationAsync());

        var after = await SnapshotAsync();
        var report = MaintenanceReport.Build(before, after, actions);

        // Os valores originais das animações são os MAIS ANTIGOS conhecidos:
        // uma segunda limpeza não pode substituí-los pelos zeros que a
        // primeira deixou (o "antes" já lê o estado alterado). Escalas reais
        // e não-zero no "antes" são configuração actual do cliente — essa sim
        // é a nova origem. Uma limpeza sem animações preserva as origens.
        if (plan.ZeroAnimations && before?.Animations is { } current)
        {
            var stillZero = (current.Window ?? 0) == 0 && (current.Transition ?? 0) == 0
                && (current.Animator ?? 0) == 0;
            report.AnimationsBefore = stillZero && previous?.AnimationsBefore is { } kept
                ? kept : current;
        }
        else if (previous?.AnimationsBefore is { } keptOriginals)
            report.AnimationsBefore = keptOriginals;
        return report;
    }

    // ── ações ──────────────────────────────────────────────────────────

    private async Task<MaintenanceActionResult> TrimCachesAsync()
    {
        var output = await _shell(TrimCachesCommand);
        return SimpleResult("trim-caches", "Limpeza de caches do sistema (pm trim-caches)",
            output, "caches do sistema libertados");
    }

    /// <summary>Cache de apps selecionadas — um resultado agregado, não uma linha por app.</summary>
    private async Task<MaintenanceActionResult> ClearAppCachesAsync(List<string> packages)
    {
        var valid = packages.Where(MaintenanceParsing.IsValidPackageName).Distinct().ToList();
        var skipped = packages.Count - valid.Count;
        if (valid.Count == 0)
            return new MaintenanceActionResult
            {
                Key = "clear-cache",
                LabelPt = "Limpeza de cache das apps selecionadas",
                Status = "skipped",
                Detail = "nenhum nome de pacote válido",
            };

        var ok = 0;
        var failures = new List<string>();
        foreach (var pkg in valid)
        {
            // `pm clear` imprime "Success" no stdout no êxito; uma falha só em
            // stderr (descartado pelo adb) chegaria como string vazia — que o
            // IsSuccess genérico contaria como boa. Aqui exige-se a confirmação.
            var output = await _shell($"pm clear --cache-only {pkg}");
            if (IsSuccess(output)
                && output!.Contains("Success", StringComparison.OrdinalIgnoreCase)) ok++;
            else failures.Add($"{pkg} ({FailureText(output)})");
        }

        var detail = $"{ok}/{valid.Count} apps" +
            (failures.Count > 0 ? $" · falhas: {Truncate(string.Join(", ", failures))}" : "") +
            (skipped > 0 ? $" · {skipped} nome(s) inválido(s) ignorado(s)" : "");
        return new MaintenanceActionResult
        {
            Key = "clear-cache",
            LabelPt = "Limpeza de cache das apps selecionadas",
            Status = failures.Count == 0 ? "ok" : ok > 0 ? "partial" : "fail",
            Detail = detail,
        };
    }

    /// <summary>Animações a 0 (ou repostas) — 3 settings, valores anteriores no detalhe.</summary>
    private async Task<MaintenanceActionResult> SetAnimationsAsync(
        double target, AnimationScales? previous)
    {
        var keys = new[]
        {
            "window_animation_scale", "transition_animation_scale", "animator_duration_scale",
        };
        var values = new[] { previous?.Window, previous?.Transition, previous?.Animator };
        var before = string.Join(" · ", keys.Select((k, i) =>
            $"{ShortScaleName(k)} {ScaleText(values[i])}"));

        var failures = new List<string>();
        for (var i = 0; i < keys.Length; i++)
        {
            var output = await _shell($"settings put global {keys[i]} {target.ToString("0.##", CultureInfo.InvariantCulture)}");
            if (!IsSuccess(output)) failures.Add($"{ShortScaleName(keys[i])} ({FailureText(output)})");
        }
        var after = string.Join(" · ", keys.Select((k, i) =>
            $"{ShortScaleName(k)} {target.ToString("0.##", CultureInfo.InvariantCulture)}"));

        return new MaintenanceActionResult
        {
            Key = target == 0.0 ? "animations-off" : "animations-set",
            LabelPt = target == 0.0 ? "Animações do sistema a 0" : "Escalas de animação alteradas",
            Status = failures.Count == 0 ? "ok" : "fail",
            Detail = $"antes: {before} → depois: {after}" +
                (failures.Count > 0 ? $" · falhas: {Truncate(string.Join(", ", failures))}" : ""),
        };
    }

    /// <summary>Repõe as escalas guardadas; chave nunca escrita volta a `settings delete`.</summary>
    private async Task<MaintenanceActionResult> RestoreAnimationsAsync(AnimationScales restore)
    {
        var keys = new[]
        {
            "window_animation_scale", "transition_animation_scale", "animator_duration_scale",
        };
        var values = new[] { restore.Window, restore.Transition, restore.Animator };
        var failures = new List<string>();
        for (var i = 0; i < keys.Length; i++)
        {
            // null = a chave nunca tinha sido escrita → delete repõe o default do sistema.
            var cmd = values[i] is { } v
                ? $"settings put global {keys[i]} {v.ToString("0.##", CultureInfo.InvariantCulture)}"
                : $"settings delete global {keys[i]}";
            var output = await _shell(cmd);
            if (!IsSuccess(output)) failures.Add($"{ShortScaleName(keys[i])} ({FailureText(output)})");
        }
        var restored = string.Join(" · ", keys.Select((k, i) =>
            $"{ShortScaleName(k)} {ScaleText(values[i])}"));
        return new MaintenanceActionResult
        {
            Key = "animations-restore",
            LabelPt = "Animações repostas aos valores anteriores",
            Status = failures.Count == 0 ? "ok" : "fail",
            Detail = restored +
                (failures.Count > 0 ? $" · falhas: {Truncate(string.Join(", ", failures))}" : ""),
        };
    }

    private async Task<MaintenanceActionResult> ForceStopAppsAsync(List<string> packages)
    {
        var valid = packages.Where(MaintenanceParsing.IsValidPackageName).Distinct().ToList();
        if (valid.Count == 0)
            return new MaintenanceActionResult
            {
                Key = "force-stop",
                LabelPt = "Forçar paragem das apps selecionadas",
                Status = "skipped",
                Detail = "nenhum nome de pacote válido",
            };

        var ok = 0;
        var failures = new List<string>();
        foreach (var pkg in valid)
        {
            var output = await _shell($"am force-stop {pkg}");
            if (IsSuccess(output)) ok++;
            else failures.Add(pkg);
        }
        return new MaintenanceActionResult
        {
            Key = "force-stop",
            LabelPt = "Forçar paragem das apps selecionadas",
            Status = failures.Count == 0 ? "ok" : ok > 0 ? "partial" : "fail",
            Detail = $"{ok}/{valid.Count} apps paradas" +
                (failures.Count > 0 ? $" · sem resposta: {Truncate(string.Join(", ", failures))}" : ""),
        };
    }

    private async Task<MaintenanceActionResult> KillBackgroundAsync()
    {
        var output = await _shell("am kill-all");
        return SimpleResult("kill-background", "Fecho de apps em segundo plano (am kill-all)",
            output, "processos em segundo plano terminados");
    }

    private async Task<MaintenanceActionResult> SetBatterySaverAsync(bool on)
    {
        var output = await _shell($"settings put global low_power {(on ? 1 : 0)}");
        return SimpleResult("battery-saver",
            on ? "Modo poupança ligado" : "Modo poupança desligado",
            output, on ? "modo poupança ativo" : "modo poupança desativado");
    }

    /// <summary>Ecrã de otimização de bateria — revisão guiada, a decisão é do técnico/cliente.</summary>
    private async Task<MaintenanceActionResult> OpenBatteryOptimizationAsync()
    {
        var output = await _shell("am start -a android.settings.IGNORE_BATTERY_OPTIMIZATION_SETTINGS");
        return SimpleResult("battery-optimization",
            "Ecrã de otimização de bateria aberto no telemóvel",
            output, "o telemóvel mostra as apps fora da otimização — rever com o cliente");
    }

    // ── helpers ────────────────────────────────────────────────────────

    /// <summary>
    /// O stdout do `adb shell` é vazio no sucesso da maioria destes comandos —
    /// o que conta é o adb ter respondido e o aparelho não ter imprimido erro.
    /// </summary>
    private static bool IsSuccess(string? output) =>
        output is not null
        && !output.Contains("Error", StringComparison.OrdinalIgnoreCase)
        && !output.Contains("Exception", StringComparison.OrdinalIgnoreCase)
        && !output.Contains("SecurityException", StringComparison.OrdinalIgnoreCase);

    private static string FailureText(string? output)
    {
        if (output is null) return "sem resposta do adb";
        var t = output.Trim();
        return t.Length == 0 ? "recusado pelo sistema" : Truncate(t.Replace('\n', ' '));
    }

    private MaintenanceActionResult SimpleResult(
        string key, string label, string? output, string okDetail)
    {
        var ok = IsSuccess(output);
        return new MaintenanceActionResult
        {
            Key = key,
            LabelPt = label,
            Status = ok ? "ok" : "fail",
            Detail = ok ? okDetail : FailureText(output),
        };
    }

    private static string ShortScaleName(string key) => key switch
    {
        "window_animation_scale" => "janela",
        "transition_animation_scale" => "transição",
        "animator_duration_scale" => "durator",
        _ => key,
    };

    private static string ScaleText(double? value) =>
        value is { } v ? v.ToString("0.##", CultureInfo.InvariantCulture) : "predefinição";

    private static string Truncate(string text, int max = 160) =>
        text.Length <= max ? text : text[..max] + "…";
}
