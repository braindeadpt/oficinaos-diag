using System.Diagnostics;
using System.Text.RegularExpressions;

namespace OficinaDiag.Devices;

public enum AndroidAppKind { User, System }
public enum AndroidAppState { Enabled, Disabled, Uninstalled }
public enum AndroidAppActionKind { Uninstall, Disable, Enable, Restore, ForceStop }

/// <summary>Comando adb já validado contra a allowlist de ações do gestor.</summary>
public sealed record AndroidAppAction(
    AndroidAppActionKind Kind,
    string PackageName,
    string Command,
    AndroidAppAction? Undo = null);

public sealed record AndroidAppActionResult(
    bool Ok,
    AndroidAppAction Action,
    AndroidAppAction? Undo,
    string? Output,
    string? Error);

public sealed record AndroidAppEntry
{
    public required string PackageName { get; init; }
    public string? InstallerPackageName { get; init; }
    public AndroidAppKind Kind { get; init; }
    public AndroidAppState State { get; init; }
    /// <summary>"critical" | "launcher" | "invalid" — null = ações permitidas.</summary>
    public string? ProtectionReason { get; init; }
    public IReadOnlyList<AndroidAppAction> Actions { get; init; } = [];
}

public sealed record AndroidAppInventory
{
    public required IReadOnlyList<AndroidAppEntry> Apps { get; init; }
    public string? HomePackage { get; init; }
    public string? Error { get; init; }
}

internal static class AndroidAppParsing
{
    private static readonly Regex PackageNamePattern = new(
        @"^[A-Za-z][A-Za-z0-9_]*(\.[A-Za-z0-9_]+)*$", RegexOptions.Compiled);
    private static readonly Regex InstallerPattern = new(
        @"installer=([^\s]+)", RegexOptions.Compiled);
    private static readonly Regex HomePattern = new(
        @"^([A-Za-z][A-Za-z0-9_]*(?:\.[A-Za-z0-9_]+)*)/", RegexOptions.Compiled);

    internal static bool IsValidPackageName(string? package) =>
        package is { Length: > 0 and <= 200 } && PackageNamePattern.IsMatch(package);

    /// <summary>
    /// `pm list packages -f -i` imprime `package:/data/app/…=com.foo installer=com.bar`.
    /// O path pode ter `=` (padding base64), por isso o nome é o que segue ao ÚLTIMO
    /// `=` do primeiro token — nunca o primeiro.
    /// </summary>
    internal static List<(string Package, string? Installer)> ParsePackageLines(string? output)
    {
        var found = new List<(string, string?)>();
        foreach (var raw in (output ?? "").Split('\n'))
        {
            var line = raw.Trim();
            if (!line.StartsWith("package:", StringComparison.Ordinal)) continue;
            var rest = line["package:".Length..].TrimStart();
            var end = rest.IndexOfAny([' ', '\t']);
            var token = end < 0 ? rest : rest[..end];
            var package = token.Contains('=', StringComparison.Ordinal)
                ? token[(token.LastIndexOf('=') + 1)..]
                : token;
            if (!IsValidPackageName(package)) continue;

            string? installer = null;
            var im = InstallerPattern.Match(rest);
            if (im.Success && !string.Equals(im.Groups[1].Value, "null", StringComparison.Ordinal))
                installer = im.Groups[1].Value;
            found.Add((package, installer));
        }
        return found;
    }

    internal static string? ParseHomePackage(string? output)
    {
        if (output is null) return null;
        string? fallback = null;
        foreach (var raw in output.Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line.Contains('=')) continue;
            var m = HomePattern.Match(line);
            if (m.Success) return m.Groups[1].Value;
            if (IsValidPackageName(line)) fallback ??= line;
        }
        return fallback;
    }
}

internal static class AndroidAppRules
{
    // Nunca desbloqueáveis por esta via: SO, UI, instalador e GMS/GSF. O launcher
    // atual entra por parâmetro (resolve-activity) porque muda por aparelho.
    private static readonly HashSet<string> ProtectedPackages = new(StringComparer.OrdinalIgnoreCase)
    {
        "android",
        "com.android.systemui",
        "com.android.settings",
        "com.android.packageinstaller",
        "com.google.android.gms",
        "com.google.android.gsf",
    };

    internal static string? ProtectionReason(string packageName, string? homePackage)
    {
        if (!AndroidAppParsing.IsValidPackageName(packageName)) return "invalid";
        if (ProtectedPackages.Contains(packageName)) return "critical";
        if (homePackage is not null
            && string.Equals(packageName, homePackage, StringComparison.OrdinalIgnoreCase))
            return "launcher";
        return null;
    }

    internal static IReadOnlyList<AndroidAppAction> ActionsFor(
        AndroidAppEntry entry, string? homePackage, int? sdk)
    {
        if (ProtectionReason(entry.PackageName, homePackage) is not null) return [];
        var p = entry.PackageName;
        var actions = new List<AndroidAppAction>();
        if (entry.State == AndroidAppState.Enabled)
        {
            if (entry.Kind == AndroidAppKind.User)
            {
                actions.Add(Action(AndroidAppActionKind.Uninstall, p));
                actions.Add(Action(AndroidAppActionKind.ForceStop, p));
            }
            else
            {
                actions.Add(Action(AndroidAppActionKind.Disable, p, AndroidAppActionKind.Enable));
                actions.Add(Action(AndroidAppActionKind.ForceStop, p));
            }
        }
        else if (entry.State == AndroidAppState.Disabled)
        {
            actions.Add(Action(AndroidAppActionKind.Enable, p, AndroidAppActionKind.Disable));
        }
        else if (entry.Kind == AndroidAppKind.System && sdk is >= 26)
        {
            actions.Add(Action(AndroidAppActionKind.Restore, p, AndroidAppActionKind.Uninstall));
        }
        return actions;
    }

    private static AndroidAppAction Action(
        AndroidAppActionKind kind, string packageName, AndroidAppActionKind? undoKind = null)
    {
        var undo = undoKind is { } uk
            ? new AndroidAppAction(uk, packageName, CommandFor(uk, packageName))
            : null;
        return new AndroidAppAction(kind, packageName, CommandFor(kind, packageName), undo);
    }

    internal static string CommandFor(AndroidAppActionKind kind, string packageName) => kind switch
    {
        AndroidAppActionKind.Uninstall => $"pm uninstall -k --user 0 {packageName}",
        AndroidAppActionKind.Disable => $"pm disable-user --user 0 {packageName}",
        AndroidAppActionKind.Enable => $"pm enable --user 0 {packageName}",
        AndroidAppActionKind.Restore => $"cmd package install-existing --user 0 {packageName}",
        AndroidAppActionKind.ForceStop => $"am force-stop --user 0 {packageName}",
        _ => throw new ArgumentOutOfRangeException(nameof(kind)),
    };

    /// <summary>Revalida na execução — a UI não pode passar um comando arbitrário.</summary>
    internal static string? Validate(AndroidAppAction action, string? homePackage)
    {
        if (!AndroidAppParsing.IsValidPackageName(action.PackageName)) return "invalid";
        if (ProtectionReason(action.PackageName, homePackage) is not null) return "protected";
        if (action.Command.Contains('\n') || action.Command.Contains('\r')) return "invalid";
        return string.Equals(action.Command, CommandFor(action.Kind, action.PackageName),
            StringComparison.Ordinal) ? null : "invalid";
    }
}

/// <summary>
/// Inventário e ações de pacotes Android via adb. A execução real fica numa
/// seam injetável para os testes provarem o fluxo sem telefone.
/// </summary>
public sealed class AndroidAppManager
{
    private readonly string _adbPath;
    private readonly Func<string, string, int, CancellationToken, Task<string?>>? _shell;

    public AndroidAppManager(string adbPath) : this(adbPath, null) { }

    internal AndroidAppManager(
        string adbPath,
        Func<string, string, int, CancellationToken, Task<string?>>? shell)
    {
        _adbPath = adbPath;
        _shell = shell;
    }

    private static readonly string[] HomeCommands =
    [
        "cmd package resolve-activity --brief --user 0 -a android.intent.action.MAIN " +
        "-c android.intent.category.HOME",
        "pm resolve-activity --brief --user 0 -a android.intent.action.MAIN " +
        "-c android.intent.category.HOME",
    ];

    public async Task<AndroidAppInventory> ListAsync(string serial, CancellationToken ct = default)
    {
        var user = await ShellAsync(serial, "pm list packages -3 -i -f --user 0", 20000, ct);
        var system = await ShellAsync(serial, "pm list packages -s -i -f --user 0", 20000, ct);
        var disabledOut = await ShellAsync(serial, "pm list packages -d -i -f --user 0", 20000, ct);
        var systemAllOut = await ShellAsync(serial, "pm list packages -s -u -i -f --user 0", 30000, ct);
        var homeOut = await ResolveHomeAsync(serial, ct);
        var sdkOut = await ShellAsync(serial, "getprop ro.build.version.sdk", 5000, ct);
        // Sem uma destas listas o inventário fica parcial: um `-s` em falta faria
        // todos os apps de sistema parecerem removidos, e sem launcher não há
        // proteção para o pacote que não se pode tocar.
        if (user is null || system is null || disabledOut is null
            || systemAllOut is null || homeOut is null)
            return new AndroidAppInventory { Apps = [], Error = "adb" };
        var home = AndroidAppParsing.ParseHomePackage(homeOut);
        if (home is null)
            return new AndroidAppInventory { Apps = [], Error = "adb" };
        var sdk = int.TryParse(sdkOut?.Trim(), out var parsedSdk) ? parsedSdk : (int?)null;

        var disabled = new HashSet<string>(
            AndroidAppParsing.ParsePackageLines(disabledOut).Select(p => p.Package),
            StringComparer.OrdinalIgnoreCase);
        var systemAll = AndroidAppParsing.ParsePackageLines(systemAllOut);
        var systemAllNames = new HashSet<string>(systemAll.Select(p => p.Package),
            StringComparer.OrdinalIgnoreCase);

        var map = new Dictionary<string, AndroidAppEntry>(StringComparer.OrdinalIgnoreCase);
        void AddRange(string? output, AndroidAppKind kind)
        {
            foreach (var (package, installer) in AndroidAppParsing.ParsePackageLines(output))
            {
                if (map.ContainsKey(package)) continue;
                map[package] = new AndroidAppEntry
                {
                    PackageName = package,
                    InstallerPackageName = installer,
                    Kind = kind,
                    State = disabled.Contains(package)
                        ? AndroidAppState.Disabled : AndroidAppState.Enabled,
                };
            }
        }
        AddRange(user, AndroidAppKind.User);
        AddRange(system, AndroidAppKind.System);

        // `-d` pode trazer pacotes que as listas por omissão escondem.
        foreach (var (package, installer) in AndroidAppParsing.ParsePackageLines(disabledOut))
        {
            if (map.ContainsKey(package)) continue;
            map[package] = new AndroidAppEntry
            {
                PackageName = package,
                InstallerPackageName = installer,
                Kind = systemAllNames.Contains(package) ? AndroidAppKind.System : AndroidAppKind.User,
                State = AndroidAppState.Disabled,
            };
        }

        // `-u` inclui pacotes de sistema desinstalados para o utilizador 0 — são
        // os que `install-existing` repõe.
        foreach (var (package, installer) in systemAll)
        {
            if (map.ContainsKey(package) || disabled.Contains(package)) continue;
            map[package] = new AndroidAppEntry
            {
                PackageName = package,
                InstallerPackageName = installer,
                Kind = AndroidAppKind.System,
                State = AndroidAppState.Uninstalled,
            };
        }

        var apps = map.Values
            .Select(e => e with
            {
                ProtectionReason = AndroidAppRules.ProtectionReason(e.PackageName, home),
                Actions = AndroidAppRules.ActionsFor(e, home, sdk),
            })
            .OrderBy(e => e.Kind)
            .ThenBy(e => e.State)
            .ThenBy(e => e.PackageName, StringComparer.OrdinalIgnoreCase)
            .ToList();
        return new AndroidAppInventory { Apps = apps, HomePackage = home };
    }

    public async Task<AndroidAppActionResult> RunAsync(
        string serial, AndroidAppAction action, string? homePackage = null,
        CancellationToken ct = default)
    {
        if (AndroidAppRules.Validate(action, homePackage) is { } invalid)
            return new AndroidAppActionResult(false, action, null, null, invalid);

        var output = await ShellAsync(serial, action.Command, 20000, ct, allowNonZero: true);
        if (output is null)
            return new AndroidAppActionResult(false, action, null, null, "adb");

        var text = output.Trim();
        var failed = text.Contains("Failure", StringComparison.OrdinalIgnoreCase)
            || text.Contains("Unknown command", StringComparison.OrdinalIgnoreCase)
            || text.Contains("Error:", StringComparison.OrdinalIgnoreCase)
            || text.Contains("Unknown package", StringComparison.OrdinalIgnoreCase)
            || text.Contains("not installed", StringComparison.OrdinalIgnoreCase)
            || text.Contains("does not exist", StringComparison.OrdinalIgnoreCase)
            || text.Contains("SecurityException", StringComparison.OrdinalIgnoreCase)
            || text.Contains("Exception", StringComparison.OrdinalIgnoreCase);
        var ok = !failed && (action.Kind == AndroidAppActionKind.ForceStop || text.Length > 0);
        return new AndroidAppActionResult(ok, action, action.Undo, output,
            ok ? null : text.Length > 0 ? text : "sem resposta do adb");
    }

    private async Task<string?> ResolveHomeAsync(string serial, CancellationToken ct)
    {
        string? last = null;
        foreach (var command in HomeCommands)
        {
            var output = await ShellAsync(serial, command, 10000, ct);
            if (AndroidAppParsing.ParseHomePackage(output) is not null) return output;
            last ??= output;
        }
        return last;
    }

    private Task<string?> ShellAsync(
        string serial, string command, int timeoutMs, CancellationToken ct,
        bool allowNonZero = false) =>
        _shell?.Invoke(serial, command, timeoutMs, ct)
        ?? AdbShellAsync(serial, command, timeoutMs, ct, allowNonZero);

    private async Task<string?> AdbShellAsync(
        string serial, string command, int timeoutMs, CancellationToken ct,
        bool allowNonZero = false)
    {
        if (string.IsNullOrWhiteSpace(serial)
            || serial.Any(c => !char.IsLetterOrDigit(c) && c is not ('-' or '_' or '.' or ':')))
            return null;
        try
        {
            var psi = new ProcessStartInfo(_adbPath)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            psi.ArgumentList.Add("-s");
            psi.ArgumentList.Add(serial);
            psi.ArgumentList.Add("shell");
            psi.ArgumentList.Add(command);
            using var p = Process.Start(psi);
            if (p is null) return null;
            var read = p.StandardOutput.ReadToEndAsync();
            var drain = p.StandardError.ReadToEndAsync();
            using var timeout = new CancellationTokenSource(timeoutMs);
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timeout.Token);
            try { await p.WaitForExitAsync(linked.Token); }
            catch (OperationCanceledException)
            {
                try { p.Kill(entireProcessTree: true); } catch { }
                return null;
            }
            if (ct.IsCancellationRequested) return null;
            var exitCode = p.ExitCode;
            var error = await drain;
            var output = await read;
            if (error.Contains("adb: error", StringComparison.OrdinalIgnoreCase)
                || error.Contains("error: device", StringComparison.OrdinalIgnoreCase))
                return null;
            if (exitCode != 0)
            {
                if (!allowNonZero) return null;
                if (string.IsNullOrWhiteSpace(output))
                    return string.IsNullOrWhiteSpace(error) ? null : error;
                return output;
            }
            return output;
        }
        catch { return null; }
    }
}
